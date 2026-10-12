using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using HVX.PD.LocationFiltering;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // Language-neutral arrays for LabVIEW: no captions, tree drawing state,
        // waveform duplication in metadata, or machine-specific absolute paths.
        [Browsable(false)] public string AnalysisSessionStatus { get; private set; } = "";
        /// <summary>Preferred for ANSI-era hosts: metadata is an opaque U8 array containing UTF-8 JSON, not a local-codepage string.</summary>
        public bool ExportAnalysisSessionUtf8(out double[,] parameters,out double[,] pdMaps,out byte[] metadataUtf8)
        {
            metadataUtf8=new byte[0];string[,] files,settings;
            if(!ExportAnalysisSession(out parameters,out pdMaps,out files,out settings))return false;
            try{
                var payload=new Dictionary<string,object>{{"schema","HVX.AnalysisSession.Utf8"},{"version",1},
                    {"files",SessionJagged(files)},{"settings",SessionJagged(settings)}};
                var serializer=new JavaScriptSerializer{MaxJsonLength=ReviewAnalysisMetadataBudget.SessionMetadataBytes,RecursionLimit=16};
                byte[] bytes=new UTF8Encoding(false,true).GetBytes(serializer.Serialize(payload));
                if(bytes.Length>ReviewAnalysisMetadataBudget.SessionMetadataBytes)throw new ArgumentException("Session metadata exceeds its size limit.");
                metadataUtf8=bytes;return true;
            }catch(Exception ex){if(IsFatalHostException(ex))throw;parameters=new double[0,36];pdMaps=new double[0,0];LastError=ex.Message;AnalysisSessionStatus="SessionExportFailed";return false;}
        }
        public bool LoadAnalysisSessionUtf8(string projectDirectory,double[,] parameters,double[,] pdMaps,byte[] metadataUtf8)
        {
            try{
                if(metadataUtf8==null||metadataUtf8.Length>ReviewAnalysisMetadataBudget.SessionMetadataBytes)throw new ArgumentException("Invalid session metadata size.");
                var serializer=new JavaScriptSerializer{MaxJsonLength=ReviewAnalysisMetadataBudget.SessionMetadataBytes,RecursionLimit=16};
                var payload=serializer.DeserializeObject(new UTF8Encoding(false,true).GetString(metadataUtf8)) as Dictionary<string,object>;
                if(payload==null||!payload.ContainsKey("schema")||!object.Equals(payload["schema"],"HVX.AnalysisSession.Utf8")||
                    !payload.ContainsKey("version")||!object.Equals(payload["version"],1))throw new ArgumentException("Unsupported UTF-8 session schema.");
                return LoadAnalysisSession(projectDirectory,parameters,pdMaps,SessionTable(payload["files"],11),SessionTable(payload["settings"],2));
            }catch(Exception ex){if(IsFatalHostException(ex))throw;LastError=ex.Message;AnalysisSessionStatus="SessionLoadFailed";return false;}
        }
        static string[][] SessionJagged(string[,] source)
        {return Enumerable.Range(0,source.GetLength(0)).Select(r=>Enumerable.Range(0,source.GetLength(1)).Select(c=>source[r,c]).ToArray()).ToArray();}
        static string[,] SessionTable(object value,int columns)
        {
            var rows=value as object[];if(rows==null||rows.Length>100000)throw new ArgumentException("Invalid session metadata table.");
            var table=new string[rows.Length,columns];
            for(int r=0;r<rows.Length;r++){var row=rows[r] as object[];if(row==null||row.Length!=columns)throw new ArgumentException("Invalid session table columns.");
                for(int c=0;c<columns;c++){table[r,c]=row[c] as string;if(table[r,c]==null)throw new ArgumentException("Session table values must be strings.");}}
            return table;
        }
        static string SessionNumber(double value) { return value.ToString("R",CultureInfo.InvariantCulture); }
        static string SessionBool(bool value) { return value?"1":"0"; }
        static double SessionRead(string value,bool optional=false)
        {
            double result;
            if(!double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out result) ||
                double.IsInfinity(result)||(!optional&&double.IsNaN(result)))throw new ArgumentException("Invalid invariant session number.");
            return result;
        }
        static bool SessionFlag(string value)
        {if(value!="0"&&value!="1")throw new ArgumentException("Invalid session flag.");return value=="1";}
        static string SessionRoot(string path)
        {
            string root=Path.GetFullPath(File.Exists(path)?Path.GetDirectoryName(path):path);
            if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
            return root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        }
        static string SessionRelative(string root,string path)
        {
            string full=Path.GetFullPath(path);
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Session file is outside the project.");
            return full.Substring(root.Length).Replace('\\','/');
        }
        static string SessionResolve(string root,string relative)
        {
            if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(":"))
                throw new ArgumentException("Session paths must be relative.");
            string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            SessionRelative(root,full);
            return full;
        }
        static string SessionHash(string path)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
        }
        static string SessionProjectHash(string root)
        {
            // Calibration/cable changes invalidate old analysis, even if LOC files did not change.
            var files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly)
                .Where(p=>p.EndsWith(".cal",StringComparison.OrdinalIgnoreCase)||
                    string.Equals(Path.GetFileName(p),"CABLE.in",StringComparison.OrdinalIgnoreCase)||
                    string.Equals(Path.GetFileName(p),"ITEM.in",StringComparison.OrdinalIgnoreCase))
                .OrderBy(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase);
            string input=string.Join("\n",files.Select(p=>Path.GetFileName(p).ToUpperInvariant()+":"+SessionHash(p)));
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-","");
        }
        /// <summary>Four synchronized outputs. Persist DBL matrices losslessly and string tables as Unicode/UTF-8.</summary>
        public bool ExportAnalysisSession(out double[,] parameters,out double[,] pdMaps,out string[,] fileStates,out string[,] settings)
        {
            parameters=new double[0,36];pdMaps=new double[0,0];fileStates=new string[0,11];settings=new string[0,2];
            try{
                if(analysisBusy)throw new InvalidOperationException("Wait for analysis before exporting a session.");
                if(matching.LongLength>(IntPtr.Size==4?8000000:32000000))throw new InvalidOperationException("Session waveform memory budget exceeded.");
                string root=SessionRoot(projectPath);pdmap.CompleteReviewPreview();pdmap.FlushReviewPreview();
                string[] paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
                var states=new string[paths.Length,11];
                for(int i=0;i<paths.Length;i++){
                    ThresholdChoice choice;if(!fileThresholds.TryGetValue(paths[i],out choice))choice=new ThresholdChoice();
                    states[i,0]=SessionRelative(root,paths[i]);
                    for(int c=0;c<3;c++)states[i,c+1]=SessionNumber(catalog[i,c]);
                    states[i,4]=SessionNumber(catalog[i,4]);states[i,5]=SessionNumber(catalog[i,5]);
                    states[i,6]=SessionHash(paths[i]);states[i,7]=SessionBool(choice.Manual);states[i,8]=SessionNumber(choice.Value);
                    states[i,9]=SessionNumber(choice.Actual);states[i,10]=SessionNumber(choice.ActualMultiplier);
                }
                // Canonical row-aligned waveforms avoid saving unused legacy MAP rows.
                double[,] p,w;SubsetWorkspace(Enumerable.Range(0,SourceRowCount).ToArray(),out p,out w);
                var keys=new Dictionary<string,string>(StringComparer.Ordinal){
                    {"Schema","HVX.AnalysisSession"},{"Version","1"},{"ProjectFingerprint",SessionProjectHash(root)},
                    {"AHistoryAuxiliaryEnabled",SessionBool(AHistoryAuxiliaryEnabled)},
                    {"AnalyzeZeroVoltageData",SessionBool(AnalyzeZeroVoltageData)},
                    {"EffectiveSelectionVersion","1"},
                    {"EffectiveSelectionMask",CreateEffectiveSelectionMask()},
                    {"EffectiveSelectionMatrixSha256",EffectiveSelectionHash(p)},
                    {"EffectiveSelectionDiagnostics",LocationFilteringDiagnosticsJson},
                    {"ReportInputContext",CreateReportInputContext(p)},
                    {"PositionTolerancePercent",SessionNumber(pdmap.PositionTolerancePercent)},
                    {"JointApproximationErrorPercent",SessionNumber(pdmap.JointApproximationErrorPercent)},
                    {"ReviewedRows",new String(reviewed.Select(value=>value?'1':'0').ToArray())},
                    {"RemovedRows",new String(removed.Select(value=>value?'1':'0').ToArray())},
                    {"AcceptanceConfirmedRows",new String(acceptanceConfirmed.Select(value=>value?'1':'0').ToArray())},
                    {"AutomaticAcceptanceRows",new String(automaticAcceptanceRows.Select(value=>value?'1':'0').ToArray())},
                    {"ReviewDecisionVersion","2"},
                    {"ExactManualPositionRows",new String(exactManualPositionRows.Select(value=>value?'1':'0').ToArray())},
                    {"AlgorithmDiagnostics",AlgorithmDiagnosticsJson},
                    {"AcquisitionQualities",SerializeAcquisitionQualities()},
                    {"AlgorithmIndex",SessionNumber(AnalysisAlgorithmIndex)},
                    {"MultipleReflectionSuppressionCount",SessionNumber(AnalysisMultipleReflectionSuppressionCount)},
                    {"UseFirstArrivalTiming",SessionBool(AnalysisUseFirstArrivalTiming)},
                    {"AttenuationPercent",SessionNumber(AnalysisAttenuationTolerancePercent)},
                    {"CorrelationErrorPercent",SessionNumber(AnalysisCorrelationErrorPercent)},{"ThresholdMultiplier",SessionNumber(AnalysisAutomaticThresholdMultiplier)},
                    {"MaximumPulses",SessionNumber(AnalysisMaximumPulseCount)},{"PhaseFilter",SessionBool(AnalysisPhaseFilterEnabled)},
                    {"ThresholdAll",SessionBool(thresholdAll)},{"GlobalManual",SessionBool(globalThreshold.Manual)},{"GlobalThresholdPc",SessionNumber(globalThreshold.Value)},
                    {"LeadLengthM",SessionNumber(ConnectingCableLengthM)},{"GeometryCompensated",SessionBool(ResultGeometryAlreadyCompensated)},
                    {"Reverse",SessionBool(ReversePositionDisplay)},{"Snap",SessionBool(pdmap.ApproximateCablePositions)},
                    {"CompensatedCharge",SessionBool(spectrum.UseCompensatedCharge)},{"WaveformFilterMode",SessionNumber(WaveformFilterMode)}};
                for(int i=0;i<4;i++)keys["Quadrant"+i]=SessionBool(analysisQuadrants[i]);
                keys["AutomaticDisplayRecommendation"]=SessionBool(automaticDisplay);
                keys["LocationFilterEnabled"]=SessionBool(locationFilterEnabled);
                keys["LocationFilterStrength"]=SessionNumber(locationFilterStrength);
                keys["LocationFilterAlgorithmId"]=locationFilterAlgorithmId;
                keys["LocationFilterBVersion"]=LocationClusterStrengthFilterB.Version;
                keys["LocationFilterBChargeThresholdPc"]=SessionNumber(locationFilterBOptions.ChargeThresholdPc);
                keys["LocationFilterBVoltageLimitKv"]=SessionNumber(locationFilterBOptions.VoltageLimitKv);
                keys["LocationFilterBMinimumClusterSize"]=SessionNumber(locationFilterBOptions.MinimumClusterSize);
                keys["LocationFilterBFromM"]=SessionNumber(locationFilterBOptions.FromM);
                keys["LocationFilterBToM"]=locationFilterBOptions.ToM.HasValue?SessionNumber(locationFilterBOptions.ToM.Value):"";
                keys["ConfidenceClassificationEnabled"]=SessionBool(spectrum.ConfidenceClassificationEnabled);
                keys["DisplayRecommendationJson"]=DisplayRecommendationJson;
                var filters=FilterParameters;for(int i=0;i<filters.Length;i++)keys["Filter"+i]=SessionNumber(filters[i]);
                var table=new string[keys.Count,2];int row=0;foreach(var item in keys){table[row,0]=item.Key;table[row++,1]=item.Value;}
                // Validate all row-to-file relationships before exposing a partial snapshot.
                var identities=Enumerable.Range(0,paths.Length).ToDictionary(i=>SessionNumber(catalog[i,0])+":"+SessionNumber(catalog[i,1]),i=>catalog[i,2]);
                for(int r=0;r<p.GetLength(0);r++){double voltage;if(!identities.TryGetValue(SessionNumber(p[r,0])+":"+SessionNumber(p[r,1]),out voltage)||Math.Abs(p[r,2]-voltage)>.011+1e-12)
                    throw new InvalidOperationException("A result row has no unique project file mapping.");
                }
                parameters=p;pdMaps=w;fileStates=states;settings=table;AnalysisSessionStatus="SessionExported";LastError="";return true;
            }catch(Exception ex){if(IsFatalHostException(ex))throw;LastError=ex.Message;AnalysisSessionStatus="SessionExportFailed";return false;}
        }
        sealed class SessionPlan
        {
            internal string Root;
            internal string[] Paths,Analyzed,Selected;
            internal double[] Ids;
            internal double[,] Parameters,Waves;
            internal double[,] DetachedTreeCatalog;
            internal bool[] Reviewed;
            internal bool[] Removed;
            internal bool[] AcceptanceConfirmed;
            internal bool[] AutomaticallyAccepted;
            internal bool[] ExactManualPositions;
            internal Dictionary<string,string> AlgorithmDiagnostics;
            internal Dictionary<string,ReviewAcquisitionQuality> AcquisitionQualities;
            internal Dictionary<string,string> Settings;
            internal Dictionary<string,ThresholdChoice> Thresholds=new Dictionary<string,ThresholdChoice>(StringComparer.OrdinalIgnoreCase);
            internal int Missing,Changed,NewFiles,ZeroVoltage;
            internal bool ProjectChanged;
        }
        /// <summary>Restores data without restoring interface language. Changed/missing files never inherit old analysis.</summary>
        public bool LoadAnalysisSession(string projectDirectory,double[,] parameters,double[,] pdMaps,string[,] fileStates,string[,] settings)
        {
            try{
                if(analysisBusy)throw new InvalidOperationException("Wait for analysis before restoring a session.");
                var plan=PrepareSession(projectDirectory,parameters,pdMaps,fileStates,settings);
                // Exercise the complete existing matrix/geometry validators before replacing the live session.
                using(var probe=new PdReviewControl()){probe.AnalyzeZeroVoltageData=AnalyzeZeroVoltageData;probe.ApplySession(plan);}
                ApplySession(plan);
                AnalysisSessionStatus="SessionLoaded; rows="+SourceRowCount+"; missing="+plan.Missing+"; changed="+plan.Changed+
                    "; new="+plan.NewFiles+"; zeroVoltage="+plan.ZeroVoltage;
                if(!plan.Settings.ContainsKey("LocationFilterEnabled"))AnalysisSessionStatus+="; legacy location filter defaults: disabled, strength 40";
                if(plan.ProjectChanged)AnalysisSessionStatus+=L("; project/cable structure changed: reanalysis required.","；项目或电缆结构已变化，必须重新分析。");
                LastError="";return true;
            }catch(Exception ex){if(IsFatalHostException(ex))throw;LastError=ex.Message;AnalysisSessionStatus="SessionLoadFailed";return false;}
        }
        SessionPlan PrepareSession(string directory,double[,] p,double[,] w,string[,] files,string[,] settings)
        {return PrepareSession(directory,p,w,files,settings,null,null);}
        SessionPlan PrepareSession(string directory,double[,] p,double[,] w,string[,] files,string[,] settings,
            string archiveDirectory,Action<int,int,string> diagnosticsProgress)
        {
            if(p==null||w==null||files==null||settings==null||p.GetLength(1)<36||files.GetLength(1)!=11||settings.GetLength(1)!=2||
                files.GetLength(0)>100000||settings.GetLength(0)>128)
                throw new ArgumentException("Invalid session array dimensions.");
            int n=p.GetLength(0),blocks=w.GetLength(0)==3L*n?3:1;
            if(n>0&&(w.GetLength(1)==0||w.GetLength(0)!=(long)n*blocks)||w.LongLength>(IntPtr.Size==4?8000000:32000000)||p.LongLength>10000000)
                throw new ArgumentException("Invalid session waveform shape or memory budget.");
            var plan=new SessionPlan{Root=SessionRoot(directory),Settings=new Dictionary<string,string>(StringComparer.Ordinal)};
            for(int i=0;i<settings.GetLength(0);i++)plan.Settings.Add(settings[i,0],settings[i,1]);
            var s=plan.Settings;
            string savedQuality;
            plan.AcquisitionQualities=ParseAcquisitionQualities(s.TryGetValue("AcquisitionQualities",out savedQuality)?savedQuality:null);
            string auxiliary;bool historyAuxiliary=s.TryGetValue("AHistoryAuxiliaryEnabled",out auxiliary)&&SessionFlag(auxiliary);
            if(s["Schema"]!="HVX.AnalysisSession"||s["Version"]!="1")throw new ArgumentException("Unsupported session schema.");
            string savedReviewed;
            bool[] sourceReviewed=s.TryGetValue("ReviewedRows",out savedReviewed)?ParseReviewedRows(savedReviewed,n):new bool[n];
            string savedRemoved;
            bool[] sourceRemoved=s.TryGetValue("RemovedRows",out savedRemoved)?ParseReviewedRows(savedRemoved,n):new bool[n];
            string savedConfirmed;
            bool[] sourceConfirmed=s.TryGetValue("AcceptanceConfirmedRows",out savedConfirmed)?ParseReviewedRows(savedConfirmed,n):new bool[n];
            bool[] sourceAutomatic=ParseAutomaticAcceptanceRows(s,n);
            string decisionVersion;bool explicitDecisions=s.TryGetValue("ReviewDecisionVersion",out decisionVersion);
            if(explicitDecisions&&decisionVersion!="2")throw new ArgumentException("Unsupported review decision version.");
            string savedExact;
            bool[] sourceExact=s.TryGetValue("ExactManualPositionRows",out savedExact)?ParseReviewedRows(savedExact,n):new bool[n];
            for(int row=0;row<n;row++)if(sourceRemoved[row]&&(!sourceReviewed[row]||p[row,35]!=0))
                throw new ArgumentException("Removed pulses must be reviewed and unaccepted.");
            for(int row=0;row<n;row++)if(sourceConfirmed[row]&&(!sourceReviewed[row]||!sourceRemoved[row]&&p[row,35]!=1))
                throw new ArgumentException("Explicit acceptance must be reviewed and included, or retained as a removed decision.");
            // Old ReviewedRows recorded viewing, not an explicit uncertain choice.
            // Do not fabricate decisions when migrating those archives.
            if(!explicitDecisions)for(int row=0;row<n;row++)if(p[row,35]!=1&&!sourceRemoved[row])sourceReviewed[row]=false;
            bool projectChanged=s["ProjectFingerprint"]!=SessionProjectHash(plan.Root);
            plan.ProjectChanged=projectChanged;
            var model=new LabVIEWLocationTree.TreeDataModel();model.LoadProject(plan.Root);
            plan.Paths=model.Records.Select(x=>x.DataPath).ToArray();plan.Ids=new double[plan.Paths.Length];
            var current=plan.Paths.Select((path,i)=>new{path,i}).ToDictionary(x=>x.path,x=>x.i,StringComparer.OrdinalIgnoreCase);
            var oldRows=new Dictionary<string,int>(StringComparer.Ordinal);
            var savedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var good=new Dictionary<int,int>();var analyzed=new List<string>();var selectedFiles=new List<string>();
            double maxId=-1;
            for(int i=0;i<files.GetLength(0);i++){
                string path=SessionResolve(plan.Root,files[i,0]);if(!savedPaths.Add(path))throw new ArgumentException("Duplicate session path.");
                double phase=SessionRead(files[i,1]),id=SessionRead(files[i,2]),voltage=SessionRead(files[i,3]);
                if(phase<0||phase>3||phase!=Math.Truncate(phase)||id<0||id>9007199254740991d||id!=Math.Truncate(id)||voltage<0)
                    throw new ArgumentException("Invalid session file identity.");
                string key=SessionNumber(phase)+":"+SessionNumber(id);oldRows.Add(key,i);maxId=Math.Max(maxId,id);
                bool done=SessionFlag(files[i,4]),chosen=SessionFlag(files[i,5]);
                var choice=new ThresholdChoice{Manual=SessionFlag(files[i,7]),Value=SessionRead(files[i,8]),Actual=SessionRead(files[i,9],true),ActualMultiplier=SessionRead(files[i,10],true)};
                ValidateAnalysisNumber(choice.Value,0,1e9,"ThresholdPc");
                int index;if(!current.TryGetValue(path,out index)){plan.Missing++;continue;}
                plan.Ids[index]=id;var record=model.Records[index];
                // Empty legacy placeholders can be present in the saved tree even
                // when never selected/analyzed. They are unavailable sources, not
                // a reason to reject every valid result in the archive.
                var source=new FileInfo(path);
                if(!source.Exists||source.Length==0){plan.Missing++;continue;}
                if(projectChanged||record.Phase!=phase||record.VoltageKv!=voltage||files[i,6]!=SessionHash(path)){plan.Changed++;continue;}
                plan.Thresholds[path]=choice;
                if(!CanAnalyzeRecord(path,voltage)){plan.ZeroVoltage++;continue;}
                ReviewAcquisitionQuality quality;
                try{
                    if(ReviewCapacitorDemInput.IsDem(path))ReviewCapacitorDemInput.Validate(path,plan.Root,AnalyzeZeroVoltageData);
                    else if(voltage==0)ReviewLocationFileReader.Read(path,plan.Root);
                    quality=ReviewAcquisitionQuality.ReadFile(path);
                }catch(Exception error) when(error is InvalidDataException||error is ArgumentException||error is FormatException||error is OverflowException){
                    // Exclude this source and its derived rows. Do not fabricate
                    // quality, alter saved matrices/hashes or delete the LOC.
                    plan.Changed++;RecordOperation("AnalysisArchiveSourceExcluded",path+": "+error.Message);continue;
                }
                ReviewAcquisitionQuality saved;
                string qualityKey=phase.ToString("0",CultureInfo.InvariantCulture)+":"+id.ToString("0",CultureInfo.InvariantCulture);
                if(plan.AcquisitionQualities.TryGetValue(qualityKey,out saved))quality=ReviewAcquisitionQuality.Combine(saved,quality);
                if(quality.ReferenceOnly)plan.AcquisitionQualities[qualityKey]=quality;
                good.Add(i,index);if(done)analyzed.Add(path);else if(chosen)selectedFiles.Add(path);
            }
            for(int i=0;i<plan.Paths.Length;i++)if(!savedPaths.Contains(plan.Paths[i])){
                if(maxId>=9007199254740991d)throw new ArgumentException("Session file IDs exhausted.");plan.Ids[i]=++maxId;plan.NewFiles++;
            }
            var keep=new List<int>();for(int r=0;r<n;r++){
                int file;if(!oldRows.TryGetValue(SessionNumber(p[r,0])+":"+SessionNumber(p[r,1]),out file)||!Finite(p[r,2])||Math.Abs(p[r,2]-SessionRead(files[file,3]))>.011+1e-12)
                    throw new ArgumentException("Result row does not match its saved file identity.");
                if(!ValidId(p[r,9]))throw new ArgumentException("Invalid session MAP ID.");
                if(good.ContainsKey(file)){
                    if(!SessionFlag(files[file,4]))throw new ArgumentException("MAP rows require an analyzed file state.");
                    if(p[r,2]==0&&!Double.IsNaN(p[r,3]))throw new ArgumentException("零电压记录不能恢复为已知电压相位。");
                    keep.Add(r);
                }
            }
            plan.Parameters=new double[keep.Count,p.GetLength(1)];plan.Waves=new double[keep.Count*blocks,w.GetLength(1)];
            plan.Reviewed=keep.Select(r=>sourceReviewed[r]).ToArray();
            plan.Removed=keep.Select(r=>sourceRemoved[r]).ToArray();
            plan.AcceptanceConfirmed=keep.Select(r=>sourceConfirmed[r]).ToArray();
            plan.AutomaticallyAccepted=keep.Select(r=>sourceAutomatic[r]).ToArray();
            plan.ExactManualPositions=keep.Select(r=>sourceExact[r]).ToArray();
            for(int i=0;i<keep.Count;i++){
                for(int c=0;c<p.GetLength(1);c++)plan.Parameters[i,c]=p[keep[i],c];
                if(plan.AcquisitionQualities.ContainsKey(QualityKey(plan.Parameters,i))) {
                    plan.Parameters[i,35]=0;plan.AcceptanceConfirmed[i]=false;plan.AutomaticallyAccepted[i]=false;
                }
                for(int b=0;b<blocks;b++)for(int c=0;c<w.GetLength(1);c++)plan.Waves[i+b*keep.Count,c]=w[keep[i]+b*n,c];
            }
            model.SetDataIds(plan.Paths,plan.Ids);model.SetParameterMatrix(plan.Parameters);
            plan.Analyzed=analyzed.ToArray();plan.Selected=selectedFiles.ToArray();
            string savedDiagnostics;
            plan.AlgorithmDiagnostics=ParseAlgorithmDiagnostics(s.TryGetValue("AlgorithmDiagnostics",out savedDiagnostics)?savedDiagnostics:null,plan.Root,plan.Analyzed,archiveDirectory,diagnosticsProgress);
            return plan;
        }
        void ApplySession(SessionPlan plan)
        {
            var s=plan.Settings;
            string auxiliary;bool historyAuxiliary=s.TryGetValue("AHistoryAuxiliaryEnabled",out auxiliary)&&SessionFlag(auxiliary);
            string savedDisplay;
            bool savedAuto=s.TryGetValue("AutomaticDisplayRecommendation",out savedDisplay)&&SessionFlag(savedDisplay);
            bool savedFilter=s.TryGetValue("LocationFilterEnabled",out savedDisplay)&&SessionFlag(savedDisplay);
            bool savedClassification=s.TryGetValue("ConfidenceClassificationEnabled",out savedDisplay)&&SessionFlag(savedDisplay);
            double savedStrength=s.TryGetValue("LocationFilterStrength",out savedDisplay)?SessionRead(savedDisplay):40;
            ValidateAnalysisNumber(savedStrength,0,100,"LocationFilterStrength");
            string savedFilterAlgorithm=s.TryGetValue("LocationFilterAlgorithmId",out savedDisplay)?savedDisplay:"A";
            ValidateLocationFilterAlgorithm(savedFilterAlgorithm);
            var savedFilterB=new LocationFilterBOptions{Strength=savedStrength};
            if(s.TryGetValue("LocationFilterBChargeThresholdPc",out savedDisplay))savedFilterB.ChargeThresholdPc=SessionRead(savedDisplay);
            if(s.TryGetValue("LocationFilterBVoltageLimitKv",out savedDisplay))savedFilterB.VoltageLimitKv=SessionRead(savedDisplay);
            if(s.TryGetValue("LocationFilterBMinimumClusterSize",out savedDisplay))savedFilterB.MinimumClusterSize=SessionInteger(savedDisplay,0,Int32.MaxValue);
            if(s.TryGetValue("LocationFilterBFromM",out savedDisplay))savedFilterB.FromM=SessionInteger(savedDisplay,0,100000);
            if(s.TryGetValue("LocationFilterBToM",out savedDisplay)&&savedDisplay!="")savedFilterB.ToM=SessionInteger(savedDisplay,0,100000);
            ValidateLocationFilterBOptions(savedFilterB);
            if(savedFilterAlgorithm=="B"){
                foreach(string key in new[]{"LocationFilterBVersion","LocationFilterBChargeThresholdPc","LocationFilterBVoltageLimitKv","LocationFilterBMinimumClusterSize","LocationFilterBFromM","LocationFilterBToM"})
                    if(!s.ContainsKey(key))throw new ArgumentException("Missing saved location filter B parameter: "+key);
                if(s["LocationFilterBVersion"]!=LocationClusterStrengthFilterB.Version)throw new ArgumentException("Unsupported location filter B version.");
            }
            double endpointTolerance=s.TryGetValue("PositionTolerancePercent",out savedDisplay)?SessionRead(savedDisplay):pdmap.PositionTolerancePercent;
            double jointTolerance=s.TryGetValue("JointApproximationErrorPercent",out savedDisplay)?SessionRead(savedDisplay):pdmap.JointApproximationErrorPercent;
            ValidateAnalysisNumber(endpointTolerance,0,100,"PositionTolerancePercent");ValidateAnalysisNumber(jointTolerance,0,100,"JointApproximationErrorPercent");
            // All these fields are validated by the detached probe first.
            int algorithm=SessionInteger(s["AlgorithmIndex"],0,9),maximum=SessionInteger(s["MaximumPulses"],1,100);
            string savedSuppression;int suppression=s.TryGetValue("MultipleReflectionSuppressionCount",out savedSuppression)
                ?SessionInteger(savedSuppression,-1,100):1;
            string savedTiming;bool firstArrival=s.TryGetValue("UseFirstArrivalTiming",out savedTiming)&&SessionFlag(savedTiming);
            int mode=SessionInteger(s["WaveformFilterMode"],0,2);
            var quadrants=Enumerable.Range(0,4).Select(i=>SessionFlag(s["Quadrant"+i])).ToArray();
            double attenuation=SessionRead(s["AttenuationPercent"]),correlation=SessionRead(s["CorrelationErrorPercent"]),multiplier=SessionRead(s["ThresholdMultiplier"]);
            ValidateAnalysisNumber(attenuation,0,100,"Attenuation");ValidateAnalysisNumber(correlation,0,100,"Correlation");ValidateAnalysisNumber(multiplier,0,1000,"Multiplier");
            double lead=SessionRead(s["LeadLengthM"]),threshold=SessionRead(s["GlobalThresholdPc"]);ValidateAnalysisNumber(lead,0,1e6,"Lead");ValidateAnalysisNumber(threshold,0,1e9,"Threshold");
            bool phase=SessionFlag(s["PhaseFilter"]),all=SessionFlag(s["ThresholdAll"]),manual=SessionFlag(s["GlobalManual"]);
            bool compensated=SessionFlag(s["GeometryCompensated"]),reverse=SessionFlag(s["Reverse"]),snap=SessionFlag(s["Snap"]),charge=SessionFlag(s["CompensatedCharge"]);
            double[] filters=Enumerable.Range(0,6).Select(i=>SessionRead(s["Filter"+i])).ToArray();
            ClearAllAnalysis();
            AHistoryAuxiliaryEnabled=historyAuxiliary;
            if(plan.DetachedTreeCatalog==null){ProjectPath=plan.Root;LoadTreeProject();}
            else
            {
                // Failed archive commits restore the already captured tree catalog,
                // not a fresh filesystem scan that could lose removed/changed files.
                projectPath=plan.Root;pdmap.ProjectPath=plan.Root;spectrum.ProjectPath=plan.Root;
                var catalog=plan.DetachedTreeCatalog;
                if(!analysisTree.LoadRecords(plan.Paths,Enumerable.Range(0,plan.Paths.Length).Select(i=>(int)catalog[i,0]).ToArray(),
                    Enumerable.Range(0,plan.Paths.Length).Select(i=>catalog[i,2]).ToArray(),
                    Enumerable.Range(0,plan.Paths.Length).Select(i=>(int)catalog[i,3]).ToArray(),plan.Ids))throw new InvalidOperationException(analysisTree.LastError);
            }
            if(!SetProjectLocationIds(plan.Paths,plan.Ids))throw new ArgumentException(LastError);
            SetAnalysisSettings(attenuation,correlation,multiplier,maximum,phase,quadrants);
            AnalysisAlgorithmIndex=algorithm;AnalysisMultipleReflectionSuppressionCount=suppression;
            AnalysisUseFirstArrivalTiming=firstArrival;
            inputGeometryAlreadyCompensated=compensated;ConnectingCableLengthM=lead;
            if(!LoadDataMatricesCore(plan.Parameters,plan.Waves,true,plan.ExactManualPositions,plan.AutomaticallyAccepted,plan.AcquisitionQualities,plan.DetachedTreeCatalog==null))throw new ArgumentException(LastError);
            RestoreReviewedRows(plan.Reviewed ?? new bool[SourceRowCount]);
            RestoreRemovedRows(plan.Removed ?? new bool[SourceRowCount]);
            RestoreAcceptanceConfirmedRows(plan.AcceptanceConfirmed ?? new bool[SourceRowCount]);
            algorithmDiagnostics=new Dictionary<string,string>(plan.AlgorithmDiagnostics??new Dictionary<string,string>(),StringComparer.OrdinalIgnoreCase);
            if(!analysisTree.MarkAnalyzed(plan.Analyzed))throw new ArgumentException(analysisTree.LastError);
            foreach(string path in plan.Selected)analysisTree.SetDataSelected(path,true);
            fileThresholds.Clear();foreach(var item in plan.Thresholds)fileThresholds[item.Key]=item.Value.Copy();
            globalThreshold=new ThresholdChoice{Manual=manual,Value=threshold};thresholdAll=all;
            FilterParameters=filters;WaveformFilterMode=mode;ReversePositionDisplay=reverse;pdmap.ApproximateCablePositions=snap;spectrum.UseCompensatedCharge=charge;
            pdmap.PositionTolerancePercent=endpointTolerance;pdmap.JointApproximationErrorPercent=jointTolerance;
            // Restoring history reproduces saved choices; no asynchronous advice
            // may rewrite them. Auto resumes only on a later data change/request.
            displayAdvisor.Invalidate();automaticDisplay=savedAuto;locationFilterEnabled=savedFilter;locationFilterStrength=savedStrength;
            // Historical A archives retain their rows and decisions, but new
            // display filtering has only B with the shared strength slider.
            locationFilterAlgorithmId="B";locationFilterBOptions=savedFilterB;InvalidateLocationScores();
            spectrum.ConfidenceClassificationEnabled=savedClassification;displayRecommendation=null;
            RefreshDisplayAdviceUi();RefreshStatistics();SyncThresholdUi();RefreshReviewUi();
            if(locationFilterAlgorithmId=="B"&&locationFilterEnabled&&!String.IsNullOrEmpty(locationFilterError))throw new ArgumentException(locationFilterError);
        }
        static int SessionInteger(string value,int min,int max)
        {double number=SessionRead(value);if(number<min||number>max||number!=Math.Truncate(number))throw new ArgumentException("Invalid session integer.");return (int)number;}
    }
}
