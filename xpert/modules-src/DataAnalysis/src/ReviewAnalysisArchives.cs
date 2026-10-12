using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        [Browsable(false)] public string LastSavedAnalysisId { get; private set; } = "";
        [Browsable(false)] public string LoadedAnalysisId { get; private set; } = "";
        [Browsable(false)] public string AnalysisArchiveStatus { get; private set; } = "";
        string loadedArchiveRoot="";
        public event EventHandler AnalysisArchiveChanged;
        public event EventHandler AnalysisSaved;
        internal void ResetAnalysisArchiveContext()
        {
            // A project switch only detaches memory. It never deletes or edits the former archive.
            reportCharts.Clear();reportChartBytes=0;loadedArchiveRoot="";
            loadedAnalysisFingerprint="";LastAnalysisBackupPath="";
            rememberedConclusion=null;
            LoadedAnalysisId=LastSavedAnalysisId="";ChartReportStatusMessage="";AnalysisArchiveStatus="";
        }

        [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)]
        internal sealed class ArchiveFile
        { public string Path { get; set; } public string Sha256 { get; set; } public long Bytes { get; set; } }
        [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)]
        internal sealed class ArchiveChart
        {
            public int Sequence { get; set; } public string Kind { get; set; } public string Title { get; set; }
            public string Region { get; set; } public string CreatedUtc { get; set; } public int Mode { get; set; }
            public int Language { get; set; } public int Width { get; set; } public int Height { get; set; }
            public bool Historical { get; set; } public string StatusReason { get; set; }
            public bool AcquisitionReferenceOnly { get; set; }
            public int[] Rows { get; set; } public string Image { get; set; } public string Csv { get; set; } public string Parameters { get; set; }
        }
        [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)]
        internal sealed class ArchiveManifest
        {
            public string Schema { get; set; } public int Version { get; set; } public string Id { get; set; }
            public string Name { get; set; } public string CreatedUtc { get; set; } public int SourceRowCount { get; set; }
            public string SavedUtc {get;set;} public ArchiveConclusion Conclusion {get;set;}
            public ArchiveFile[] Files { get; set; } public ArchiveChart[] Charts { get; set; }
            public string[] ExpandedNodes { get; set; } public string SelectedNode { get; set; } public string TopNode { get; set; }
            public double[] SelectedIdentity { get; set; } public int DisplayMode { get; set; } public int ViewPlane { get; set; }
        }
        [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)]
        internal sealed class ArchiveSelection
        { public string Schema { get; set; } public int[] IncludedSequences { get; set; } }
        static JavaScriptSerializer ArchiveJson() { return ReviewStatusJson.Serializer(); }
        static JavaScriptSerializer ArchiveSessionJson()
        { return new JavaScriptSerializer{MaxJsonLength=ReviewAnalysisMetadataBudget.SessionMetadataBytes,RecursionLimit=16}; }
        static readonly UTF8Encoding ArchiveEncoding=new UTF8Encoding(false,true);
        static string ArchiveChild(string directory,string relative)
        {
            string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string path=SessionResolve(root,relative);
            for(string cursor=path;cursor!=null&&cursor.StartsWith(root,StringComparison.OrdinalIgnoreCase);cursor=Path.GetDirectoryName(cursor))
                if((Directory.Exists(cursor)||File.Exists(cursor))&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Analysis archive links are not supported.");
            return path;
        }
        static string ArchiveRoot(string project)
        {
            string root=SessionRoot(project);return ArchiveChild(root,"Analysis report");
        }
        static void WriteNewText(string path,string text)
        { using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,ArchiveEncoding)){writer.Write(text);writer.Flush();stream.Flush(true);} }
        static void WriteNewBytes(string path,byte[] bytes)
        { using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);} }
        static byte[] ReadLimited(string path,long limit)
        { var info=new FileInfo(path);if(info.Length<0||info.Length>limit)throw new InvalidDataException("Archive file exceeds its size limit.");return File.ReadAllBytes(path); }
        static void WriteMatrix(string path,double[,] matrix)
        {
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(stream,Encoding.UTF8))
            {writer.Write(Encoding.ASCII.GetBytes("HVXMAT01"));writer.Write(matrix.GetLength(0));writer.Write(matrix.GetLength(1));foreach(double value in matrix)writer.Write(value);writer.Flush();stream.Flush(true);}
        }
        static double[,] ReadMatrix(string path,long maximumCells)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var reader=new BinaryReader(stream,Encoding.UTF8))
            {
                if(stream.Length<16||Encoding.ASCII.GetString(reader.ReadBytes(8))!="HVXMAT01")throw new InvalidDataException("Invalid matrix file signature.");
                int rows=reader.ReadInt32(),cols=reader.ReadInt32();long cells=(long)rows*cols;
                if(rows<0||rows>1000000||cols<0||cols>8000000||cells>maximumCells||cells<0||stream.Length!=16+cells*8)throw new InvalidDataException("Invalid archived matrix dimensions.");
                var matrix=new double[rows,cols];for(int r=0;r<rows;r++)for(int c=0;c<cols;c++)matrix[r,c]=reader.ReadDouble();return matrix;
            }
        }
        static ArchiveFile DescribeArchiveFile(string directory,string relative)
        { string path=ArchiveChild(directory,relative);return new ArchiveFile{Path=relative,Bytes=new FileInfo(path).Length,Sha256=SessionHash(path)}; }
        static ArchiveManifest ReadArchiveManifest(string directory,string id,bool verifyFiles)
        {
            var manifest=ArchiveJson().Deserialize<ArchiveManifest>(ArchiveEncoding.GetString(ReadLimited(ArchiveChild(directory,"analysis.json"),16*1024*1024)));
            if(manifest==null||manifest.Schema!="HVX.NamedAnalysis"||manifest.Version!=1||manifest.Id!=id||
                string.IsNullOrWhiteSpace(manifest.Name)||manifest.Name.Length>160||manifest.SourceRowCount<0||manifest.SourceRowCount>1000000||manifest.Files==null||manifest.Files.Length>3+ReportChartLimit*3||
                manifest.Charts==null||manifest.Charts.Length>ReportChartLimit||manifest.ExpandedNodes==null||manifest.ExpandedNodes.Length>100000||
                manifest.SelectedIdentity==null||manifest.SelectedIdentity.Length!=0&&manifest.SelectedIdentity.Length!=3||manifest.SelectedIdentity.Any(value=>double.IsNaN(value)||double.IsInfinity(value))||
                manifest.DisplayMode<0||manifest.DisplayMode>12||manifest.ViewPlane<0||manifest.ViewPlane>3||manifest.ViewPlane!=0&&manifest.DisplayMode!=5&&manifest.DisplayMode!=6&&manifest.DisplayMode!=12)
                throw new InvalidDataException("Invalid named analysis manifest.");
            DateTime parsed;if(!DateTime.TryParse(manifest.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed))throw new InvalidDataException("Invalid archive creation time.");
            if(manifest.SavedUtc!=null&&!DateTime.TryParse(manifest.SavedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed))throw new InvalidDataException("Invalid archive save time.");
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes=0;
            foreach(var file in manifest.Files)
            {
                if(file==null||!paths.Add(file.Path)||file.Bytes<0)throw new InvalidDataException("Duplicate or invalid archived file.");
                long maximum=file.Path=="pdmap.bin"?256L*1024*1024+16:file.Path=="parameters.bin"?80000016:file.Path=="session.json"?ReviewAnalysisMetadataBudget.SessionMetadataBytes:ReportChartBytesLimit;
                totalBytes+=file.Bytes;
                if(file.Bytes>maximum||totalBytes>512L*1024*1024)throw new InvalidDataException("Archive member or total size exceeds the memory/read budget.");
                string path=ArchiveChild(directory,file.Path);
                if(verifyFiles&&(!File.Exists(path)||new FileInfo(path).Length!=file.Bytes||!string.Equals(SessionHash(path),file.Sha256,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Archive data is missing or has changed: "+file.Path);
            }
            foreach(string required in new[]{"parameters.bin","pdmap.bin","session.json"})if(!paths.Contains(required))throw new InvalidDataException("Archive is missing "+required);
            ValidateSavedConclusion(manifest);
            var sequences=new HashSet<int>();foreach(var chart in manifest.Charts)
                if(chart==null||chart.Sequence<1||!sequences.Add(chart.Sequence)||chart.Rows==null||chart.Rows.Length>1000000||chart.Rows.Any(row=>row<0)||
                    (chart.Kind!="PDmap"&&chart.Kind!="Spectrum")||chart.Width<1||chart.Height<1||(long)chart.Width*chart.Height>4000000||
                    chart.Language<0||chart.Language>=ReviewLanguages.Names.Length||chart.Mode< -1||chart.Mode>12||!paths.Contains(chart.Image)||!paths.Contains(chart.Csv)||!paths.Contains(chart.Parameters))
                    throw new InvalidDataException("Invalid archived report chart.");
            return manifest;
        }
        static SavedAnalysisInfo ArchiveInfo(ArchiveManifest manifest,string directory)
        {
            DateTime created=DateTime.Parse(manifest.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
            return new SavedAnalysisInfo{Id=manifest.Id,Name=manifest.Name,CreatedUtc=manifest.CreatedUtc,CreatedLocal=created.ToLocalTime(),
                DirectoryPath=directory,SourceRowCount=manifest.SourceRowCount,ReportChartCount=manifest.Charts.Length};
        }
        public SavedAnalysisInfo[] EnumerateSavedAnalyses()
        {
            try
            {
                string root=ArchiveRoot(projectPath);if(!Directory.Exists(root))return new SavedAnalysisInfo[0];
                var items=new List<SavedAnalysisInfo>();int invalid=0;
                foreach(string directory in Directory.GetDirectories(root).Where(path=>!Path.GetFileName(path).StartsWith(".",StringComparison.Ordinal)))
                {try{string id=Path.GetFileName(directory);ArchiveChild(root,id);items.Add(ArchiveInfo(ReadArchiveManifest(directory,id,false),directory));}catch(Exception ex){if(IsFatalHostException(ex))throw;invalid++;}}
                AnalysisArchiveStatus=L("Saved analyses: ","已保存分析：")+items.Count+(invalid==0?"":L("; invalid archives skipped: ","；已跳过无效存档：")+invalid);
                return items.OrderByDescending(item=>item.CreatedUtc,StringComparer.Ordinal).ToArray();
            }
            catch(Exception ex){if(IsFatalHostException(ex))throw;ArchiveFailure(ex);return new SavedAnalysisInfo[0];}
        }
        public bool RemoveSavedAnalysis(string analysisId)
        {
            try
            {
                if(IsAnalysisRunning)throw new InvalidOperationException("Wait for analysis before removing an archive.");
                if(String.IsNullOrWhiteSpace(analysisId)||Path.GetFileName(analysisId)!=analysisId||analysisId.StartsWith(".",StringComparison.Ordinal))throw new ArgumentException("Invalid analysis identifier.");
                if(analysisId==LoadedAnalysisId)throw new InvalidOperationException(L("This analysis is currently loaded. Load another analysis before removing it.","该分析当前正在使用，请先切换其他分析后再删除。"));
                string root=ArchiveRoot(projectPath),source=ArchiveChild(root,analysisId);
                ReadArchiveManifest(source,analysisId,false);
                using(var guard=new FileStream(ArchiveChild(root,".save.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                {
                    string recovery=ArchiveChild(root,".deleted_"+Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(recovery);
                    Directory.Move(source,ArchiveChild(recovery,analysisId));
                    AnalysisArchiveStatus=L("Moved to recovery: ","已移入恢复目录：")+recovery;
                }
                if(LastSavedAnalysisId==analysisId)LastSavedAnalysisId="";
                NotifyReviewHost(AnalysisArchiveChanged);return true;
            }
            catch(Exception ex){if(IsFatalHostException(ex))throw;ArchiveFailure(ex);return false;}
        }
        public bool SaveNamedAnalysis(string analysisName)
        { return SaveNamedAnalysisCore(analysisName,false); }
        public bool OverwriteLoadedAnalysis()
        { return SaveNamedAnalysisCore(null,true); }
        bool SaveNamedAnalysisCore(string analysisName,bool overwrite)
        {
            FileStream saveLock=null;
            try
            {
                string name=overwrite?"":ValidateNewAnalysisName(analysisName);
                if(IsAnalysisRunning)throw new InvalidOperationException("Wait for analysis before saving.");
                double[,] parameters,waves;byte[] session;
                if(!ExportAnalysisSessionUtf8(out parameters,out waves,out session))throw new InvalidOperationException(LastError);
                string root=ArchiveRoot(projectPath);
                string id=overwrite?LoadedAnalysisId:DateTime.Now.ToString("yyyyMMdd_HHmmss_fff",CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N").Substring(0,12);
                string destination=ArchiveChild(root,id),stage=ArchiveChild(root,".pending_"+Guid.NewGuid().ToString("N"));
                string backup=ArchiveChild(root,".replaced_"+Guid.NewGuid().ToString("N"));
                // Temporary/recovery names do not repeat the archive ID. Validate
                // absolute paths before creating even a lock or pending directory.
                if(overwrite)VerifyLoadedAnalysisForOverwrite(root);
                EnsureAnalysisArchivePathsFit(root,stage,destination,backup,overwrite);
                Directory.CreateDirectory(root);
                saveLock=new FileStream(ArchiveChild(root,".save.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                ArchiveManifest previous=null;
                if(overwrite)previous=VerifyLoadedAnalysisForOverwrite(root);else name=ValidateNewAnalysisName(name);
                if(overwrite)name=previous.Name;
                Directory.CreateDirectory(stage);
                WriteMatrix(ArchiveChild(stage,"parameters.bin"),parameters);WriteMatrix(ArchiveChild(stage,"pdmap.bin"),waves);WriteNewBytes(ArchiveChild(stage,"session.json"),session);
                var files=new List<ArchiveFile>();foreach(string file in new[]{"parameters.bin","pdmap.bin","session.json"})files.Add(DescribeArchiveFile(stage,file));
                var charts=new List<ArchiveChart>();
                foreach(var chart in reportCharts)
                {
                    string prefix="chart_"+chart.Sequence.ToString("D6",CultureInfo.InvariantCulture);
                    var entry=new ArchiveChart{Sequence=chart.Sequence,Kind=chart.ChartKind,Title=chart.Title,Region=chart.RegionSummary,CreatedUtc=chart.CreatedUtc,Mode=chart.DisplayModeIndex,
                        Language=chart.LanguageIndex,Width=chart.ImageWidth,Height=chart.ImageHeight,Rows=chart.SourceRowIndices,Image=prefix+".png",Csv=prefix+".csv",Parameters=prefix+".bin",
                        Historical=chart.IsHistoricalOrStale,StatusReason=chart.StatusReason,AcquisitionReferenceOnly=chart.AcquisitionReferenceOnly};
                    WriteNewBytes(ArchiveChild(stage,entry.Image),chart.PngImage);WriteNewText(ArchiveChild(stage,entry.Csv),chart.CsvData);WriteMatrix(ArchiveChild(stage,entry.Parameters),chart.ParameterMatrix);
                    files.Add(DescribeArchiveFile(stage,entry.Image));files.Add(DescribeArchiveFile(stage,entry.Csv));files.Add(DescribeArchiveFile(stage,entry.Parameters));charts.Add(entry);
                }
                var manifest=new ArchiveManifest{Schema="HVX.NamedAnalysis",Version=1,Id=id,Name=name,CreatedUtc=overwrite?previous.CreatedUtc:DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),SourceRowCount=parameters.GetLength(0),
                    SavedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),Conclusion=ConclusionForSave(id,files.ToArray()),
                    Files=files.ToArray(),Charts=charts.ToArray(),ExpandedNodes=analysisTree.GetExpandedNodeKeys(),SelectedNode=analysisTree.GetSelectedNodeKey(),TopNode=analysisTree.GetTopNodeKey(),
                    SelectedIdentity=selected<0?new double[0]:new[]{working[selected,0],working[selected,1],working[selected,9]},DisplayMode=DisplayModeIndex,ViewPlane=spectrum.ViewPlaneIndex};
                WriteNewText(ArchiveChild(stage,"analysis.json"),ArchiveJson().Serialize(manifest));WriteNewText(ArchiveChild(stage,"selection.json"),ArchiveSelectionText(reportCharts));
                // Verify bytes and required members before publishing; overwrite keeps
                // the same archive identity and a complete recoverable previous directory.
                ReadArchiveManifest(stage,id,true);ReadArchiveSelection(stage,charts.Select(c=>c.Sequence));
                string publishedFingerprint=AnalysisDirectoryFingerprint(stage);
                if(overwrite)PublishAnalysisReplacement(root,stage,destination,id,backup);
                else { Directory.Move(stage,destination);LastAnalysisBackupPath=""; }
                if(AnalysisDirectoryFingerprint(destination)!=publishedFingerprint)throw new IOException("Published analysis changed during save; previous backup was retained.");
                LastSavedAnalysisId=LoadedAnalysisId=id;loadedArchiveRoot=SessionRoot(projectPath);
                loadedAnalysisFingerprint=publishedFingerprint;
                RememberSavedConclusion(manifest.Conclusion);
                foreach(var entry in charts){var chart=reportCharts.Single(item=>item.Sequence==entry.Sequence);chart.SetSavedPaths(ArchiveChild(destination,entry.Image),ArchiveChild(destination,entry.Csv));}
                MarkAnalysisClean();AnalysisArchiveStatus=L("Analysis saved: ","分析已保存：")+HVX.ProjectModel.AnalysisDateText.DisplayAutomaticName(name)+(overwrite?L("; previous complete backup: ","；原完整分析备份：")+LastAnalysisBackupPath:"");NotifyReviewHost(AnalysisArchiveChanged);NotifyReviewHost(AnalysisSaved);return true;
            }
            catch(Exception ex){if(IsFatalHostException(ex))throw;return ArchiveFailure(ex);}
            finally{if(saveLock!=null)saveLock.Dispose();}
        }
        public bool LoadSavedAnalysis(string analysisId)
        {
            bool priorDirty=analysisDirty,priorContent=analysisHadContent,loadedSuccessfully=false,loadedModified=false;string priorStamp=savedAnalysisParameterStamp,priorFingerprint=loadedAnalysisFingerprint,priorBackup=LastAnalysisBackupPath;suppressAnalysisDirty++;
            bool previousSuppress=suppressArchivePrompt;suppressArchivePrompt=true;
            Action rollback=null;bool applying=false;
            ArchiveConclusion priorConclusion=null;
            try
            {
                priorConclusion=CurrentConclusionSnapshot();
                if(IsAnalysisRunning)throw new InvalidOperationException("Wait for analysis before loading.");
                if(string.IsNullOrWhiteSpace(analysisId)||analysisId!=Path.GetFileName(analysisId)||analysisId.StartsWith(".",StringComparison.Ordinal))throw new ArgumentException("Use a saved analysis ID from the current project.");
                string project=SessionRoot(projectPath),directory=ArchiveChild(ArchiveRoot(project),analysisId);
                string fingerprint=AnalysisDirectoryFingerprint(directory);
                var manifest=ReadArchiveManifest(directory,analysisId,true);
                var parameters=ReadMatrix(ArchiveChild(directory,"parameters.bin"),10000000);
                if(parameters.GetLength(0)!=manifest.SourceRowCount)throw new InvalidDataException("Archive source row count does not match its matrix.");
                var waves=ReadMatrix(ArchiveChild(directory,"pdmap.bin"),IntPtr.Size==4?8000000:32000000);
                byte[] session=ReadLimited(ArchiveChild(directory,"session.json"),ReviewAnalysisMetadataBudget.SessionMetadataBytes);
                var payload=ArchiveSessionJson().DeserializeObject(ArchiveEncoding.GetString(session)) as Dictionary<string,object>;
                if(payload==null||!payload.ContainsKey("schema")||!object.Equals(payload["schema"],"HVX.AnalysisSession.Utf8")||!object.Equals(payload["version"],1))throw new InvalidDataException("Invalid analysis session schema.");
                var plan=PrepareSession(project,parameters,waves,SessionTable(payload["files"],11),SessionTable(payload["settings"],2));
                using(var probe=new PdReviewControl()){probe.PromptSavedAnalysisOnProjectLoad=false;probe.ApplySession(plan);}
                bool stale=plan.ProjectChanged||plan.Changed>0||plan.Missing>0;
                loadedModified=stale;
                var choices=ReadArchiveSelection(directory,manifest.Charts.Select(chart=>chart.Sequence));
                var charts=new List<ReportChartSnapshot>();long bytes=0;
                foreach(var entry in manifest.Charts)
                {
                    byte[] png=ReadLimited(ArchiveChild(directory,entry.Image),ReportChartBytesLimit);
                    if(png.Length<24||png[0]!=137||png[1]!=80||png[2]!=78||png[3]!=71||png[12]!=73||png[13]!=72||png[14]!=68||png[15]!=82||
                        PngDimension(png,16)!=entry.Width||PngDimension(png,20)!=entry.Height)throw new InvalidDataException("Invalid report PNG header or dimensions.");
                    using(var stream=new MemoryStream(png))using(var bitmap=Image.FromStream(stream,true,true))
                        if(bitmap.Width!=entry.Width||bitmap.Height!=entry.Height)throw new InvalidDataException("Report PNG dimensions do not match the manifest.");
                    string csv=ArchiveEncoding.GetString(ReadLimited(ArchiveChild(directory,entry.Csv),ReportChartBytesLimit));
                    var matrix=ReadMatrix(ArchiveChild(directory,entry.Parameters),ReportChartBytesLimit/8);
                    if(matrix.GetLength(0)!=entry.Rows.Length||matrix.GetLength(1)<36)throw new InvalidDataException("Report rows do not match the archived parameter matrix.");
                    var chart=new ReportChartSnapshot(entry.Sequence,entry.Kind,entry.Title,entry.Region,project,entry.Mode,entry.Language,png,entry.Width,entry.Height,matrix,(int[])entry.Rows.Clone(),csv);
                    chart.SetCreatedUtc(entry.CreatedUtc);
                    if(entry.Historical)chart.MarkHistorical(entry.StatusReason??"Previously retained historical snapshot.",english);
                    chart.SetReportSelection(choices.Contains(entry.Sequence));chart.SetSavedPaths(ArchiveChild(directory,entry.Image),ArchiveChild(directory,entry.Csv));
                    if(stale)chart.MarkHistorical("Project or source file fingerprint changed; review the historical snapshot before including it.",english);
                    if(entry.AcquisitionReferenceOnly||HasAcquisitionRiskInMatrix(matrix,plan.AcquisitionQualities))chart.MarkAcquisitionReferenceOnly(english);
                    bytes+=chart.StorageBytes;if(bytes>ReportChartBytesLimit)throw new InvalidDataException("Archived report attachments exceed 64 MiB.");charts.Add(chart);
                }
                // Validate everything, including images and attachments, before replacing the active analysis.
                if(AnalysisDirectoryFingerprint(directory)!=fingerprint)throw new IOException("The saved analysis changed while loading. Reload the latest archive.");
                rollback=CaptureNamedAnalysisRollback();applying=true;ApplySession(plan);
                AnalysisSessionStatus="SessionLoaded; rows="+SourceRowCount+"; missing="+plan.Missing+"; changed="+plan.Changed+"; new="+plan.NewFiles+"; zeroVoltage="+plan.ZeroVoltage;
                reportCharts.Clear();reportCharts.AddRange(charts);reportChartBytes=bytes;
                ChartReportRequestSequence=Math.Max(ChartReportRequestSequence,charts.Count==0?0:charts.Max(chart=>chart.Sequence));
                DisplayModeIndex=manifest.DisplayMode;spectrum.ViewPlaneIndex=manifest.ViewPlane;
                if(manifest.SelectedIdentity.Length==3)
                    for(int row=0;row<SourceRowCount;row++)if(working[row,0]==manifest.SelectedIdentity[0]&&working[row,1]==manifest.SelectedIdentity[1]&&working[row,9]==manifest.SelectedIdentity[2]){SelectSourceRowCore(row,false);break;}
                analysisTree.RestoreNodeView(manifest.ExpandedNodes,manifest.SelectedNode,manifest.TopNode);
                LoadedAnalysisId=analysisId;loadedArchiveRoot=project;
                loadedAnalysisFingerprint=fingerprint;LastAnalysisBackupPath="";
                // The stored conclusion is reusable only after an unchanged source/session load.
                evaluationResult=null;evaluationContext=null;
                RememberSavedConclusion(stale?null:manifest.Conclusion);
                AnalysisArchiveStatus=L("Analysis loaded: ","分析已加载：")+HVX.ProjectModel.AnalysisDateText.DisplayAutomaticName(manifest.Name);
                if(plan.ProjectChanged)AnalysisArchiveStatus+=L("; cable/project metadata changed: previous results are invalid; run analysis again.","；电缆结构或项目参数已变化，旧结果已失效，请重新分析。");
                if(plan.Missing>0||plan.Changed>0)AnalysisArchiveStatus+=L("; unavailable source records skipped: ","；已跳过不可用记录：")+(plan.Missing+plan.Changed);
                if(stale)AnalysisArchiveStatus+=L("; changed source data: report snapshots unchecked.","；源数据已变化，历史报告图表已取消勾选。");
                ChartReportStatusMessage=L("Report attachments restored: ","已恢复报告附件：")+charts.Count;
                loadedSuccessfully=true;NotifyReviewHost(AnalysisArchiveChanged);NotifyReviewHost(ChartReportStatusChanged);return true;
            }
            catch(Exception ex)
            {
                if(IsFatalHostException(ex))throw;
                if(applying&&rollback!=null)
                    try{rollback();RememberSavedConclusion(priorConclusion);}catch(Exception restoreError){if(IsFatalHostException(restoreError))throw;return ArchiveFailure(new InvalidOperationException(ex.Message+"; rollback failed: "+restoreError.Message));}
                return ArchiveFailure(ex);
            }
            finally{suppressArchivePrompt=previousSuppress;suppressAnalysisDirty--;if(loadedSuccessfully){MarkAnalysisClean();if(loadedModified)MarkAnalysisDirty();}else{analysisDirty=priorDirty;analysisHadContent=priorContent;savedAnalysisParameterStamp=priorStamp;loadedAnalysisFingerprint=priorFingerprint;LastAnalysisBackupPath=priorBackup;}}
        }
        bool ArchiveFailure(Exception error)
        {AnalysisArchiveStatus=L("Analysis archive: ","分析存档：")+error.Message+(LastAnalysisBackupPath.Length>0?L("; complete backup retained: ","；已保留完整备份：")+LastAnalysisBackupPath:"");LastError=AnalysisArchiveStatus;NotifyReviewHost(AnalysisArchiveChanged);return false;}
        static string ArchiveSelectionText(IEnumerable<ReportChartSnapshot> charts)
        {return ArchiveJson().Serialize(new ArchiveSelection{Schema="HVX.ReportSelection.1",IncludedSequences=charts.Where(chart=>chart.IncludedInReport).Select(chart=>chart.Sequence).ToArray()});}
        static HashSet<int> ReadArchiveSelection(string directory,IEnumerable<int> sequences)
        {
            var allowed=new HashSet<int>(sequences);
            var selection=ArchiveJson().Deserialize<ArchiveSelection>(ArchiveEncoding.GetString(ReadLimited(ArchiveChild(directory,"selection.json"),1024*1024)));
            if(selection==null||selection.Schema!="HVX.ReportSelection.1"||selection.IncludedSequences==null||selection.IncludedSequences.Length>ReportChartLimit||selection.IncludedSequences.Distinct().Count()!=selection.IncludedSequences.Length||selection.IncludedSequences.Any(value=>!allowed.Contains(value)))
                throw new InvalidDataException("Invalid saved report selection.");
            return new HashSet<int>(selection.IncludedSequences);
        }
        public bool IsReportChartSelected(int sequence) {var chart=reportCharts.FirstOrDefault(item=>item.Sequence==sequence);return chart!=null&&chart.IncludedInReport;}
        static long PngDimension(byte[] bytes,int offset) {return ((long)bytes[offset]<<24)|((long)bytes[offset+1]<<16)|((long)bytes[offset+2]<<8)|bytes[offset+3];}
        public ReportChartSnapshot[] GetSelectedReportCharts() {return reportCharts.Where(chart=>chart.IncludedInReport).ToArray();}
        public bool SetReportChartSelected(int sequence,bool selectedForReport)
        {
            var chart=reportCharts.FirstOrDefault(item=>item.Sequence==sequence);if(chart==null)return false;
            if(selectedForReport&&chart.AcquisitionReferenceOnly){ChartReportStatusMessage=AcquisitionRiskText();NotifyReviewHost(ChartReportStatusChanged);return false;}
            bool old=chart.IncludedInReport;if(old==selectedForReport)return true;chart.SetReportSelection(selectedForReport);
            try
            {
                // A report-selection adjustment is part of the pending analysis,
                // so leaving without saving must not have changed the old archive.
                MarkAnalysisDirty();
                ChartReportStatusMessage=L("Report inclusion updated; save analysis to persist.","已更新报告勾选状态；保存分析后写入存档。");NotifyReviewHost(ChartReportStatusChanged);return true;
            }
            catch(Exception ex){chart.SetReportSelection(old);if(IsFatalHostException(ex))throw;return ArchiveFailure(ex);}
        }
    }
}
