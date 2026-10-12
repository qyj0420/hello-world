using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

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
                string.IsNullOrWhiteSpace(manifest.Name)||manifest.Name.Length>160||manifest.SourceRowCount<0||manifest.SourceRowCount>1000000||manifest.Files==null||manifest.Files.Length>4+ReportChartLimit*3+ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments||
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
                long maximum=file.Path=="pdmap.bin"?(IntPtr.Size==4?256L*1024*1024+16:ReviewAnalysisMetadataBudget.ArchivedPdmapBytesX64):file.Path=="parameters.bin"?80000016:file.Path=="session.json"?ReviewAnalysisMetadataBudget.SessionMetadataBytes:IsDiagnosticsPartFileName(file.Path)?ReviewAnalysisMetadataBudget.SegmentTriggerBytes:
                    file.Path==ReviewDiagnosticArchivePack.MemberName?ReviewAnalysisMetadataBudget.DiagnosticsArchiveBytes:ReportChartBytesLimit;
                // diagnostics.bin is read one entry at a time under its own
                // ceiling, so it does not count toward the whole-read budget.
                if(file.Path!=ReviewDiagnosticArchivePack.MemberName)totalBytes+=file.Bytes;
                if(file.Bytes>maximum||totalBytes>ReviewAnalysisMetadataBudget.AnalysisArchiveTotalBytes)throw new InvalidDataException("Archive member or total size exceeds the memory/read budget.");
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
                var diagnosticsReferences=AlgorithmDiagnosticReferences(algorithmDiagnostics).ToArray();
                if(!ExportAnalysisSessionUtf8Core(out parameters,out waves,out session,true))throw new InvalidOperationException(LastError);
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
                if(diagnosticsReferences.Length>0)ReviewDiagnosticArchivePack.Write(ArchiveChild(stage,ReviewDiagnosticArchivePack.MemberName),diagnosticsReferences);
                var files=new List<ArchiveFile>();foreach(string file in new[]{"parameters.bin","pdmap.bin","session.json"})files.Add(DescribeArchiveFile(stage,file));
                if(diagnosticsReferences.Length>0)files.Add(DescribeArchiveFile(stage,ReviewDiagnosticArchivePack.MemberName));
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
        bool ArchiveFailure(Exception error)
        {AnalysisArchiveStatus=L("Analysis archive: ","分析存档：")+error.Message+(LastAnalysisBackupPath.Length>0?L("; complete backup retained: ","；已保留完整备份：")+LastAnalysisBackupPath:"");LastError=AnalysisArchiveStatus;NotifyReviewHost(AnalysisArchiveChanged);return false;}
        static void VerifyDiagnosticsPartsInManifest(ArchiveManifest manifest,string envelopeJson)
        {
            var envelope=ArchiveSessionJson().DeserializeObject(envelopeJson) as Dictionary<string,object>;
            if(envelope==null)throw new InvalidDataException("Invalid segmented diagnostics envelope.");
            var parts=envelope.ContainsKey("parts")?envelope["parts"] as object[]:null;
            if(parts==null||parts.Length==0)throw new InvalidDataException("Segmented diagnostics envelope has no part files.");
            var listed=new HashSet<string>(manifest.Files.Select(file=>file.Path),StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<parts.Length;i++){
                string name=parts[i] as string;
                if(name==null||!IsDiagnosticsPartFileName(name)||!String.Equals(name,DiagnosticsPartFileName(i+1),StringComparison.OrdinalIgnoreCase)||!listed.Contains(name))
                    throw new InvalidDataException("Diagnostics segment is missing from the analysis manifest: "+name);
            }
        }
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
