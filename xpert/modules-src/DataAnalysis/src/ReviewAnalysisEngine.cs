using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    internal sealed class ReviewAnalysisRequest
    {
        internal string ProjectPath="",LocationFilePath="";
        internal string EndpointPolicy="2;1.5;OR;OR";
        internal long LocationDataId;
        internal int AlgorithmIndex=5,MaxPulses=30,MultipleReflectionSuppressionCount=1;
        internal bool AutomaticAcceptance=true,ManualThresholdEnabled,ApplyLinearFilter,UseFirstArrivalTiming;
        internal double ManualThreshold,AutomaticThresholdMultiplier=3,AttenuationTolerancePercent=15,CorrelationErrorPercent=30;
        internal double ConnectingCableLengthM,SampleRateHz=125e6,OscillationFrequencyHz;
        internal double NoiseSimilarityTolerancePercent=1;
        internal double LocationSuspiciousMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultSuspiciousMinimumPercent;
        internal double LocationManualAcceptanceMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultManualAcceptanceMinimumPercent;
        internal double LocationAutomaticAcceptanceMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultAutomaticAcceptanceMinimumPercent;
        internal bool[] PhaseQuadrants={true,true,true,true};
        internal double[] LinearFilterCutoffsHz={10000,3000000};
        internal AHistoryBatch AHistoryBatch;
        internal string AHistoryAcquisition="";
        internal int AHistoryPhase;
        internal bool AHistoryRequireHeaderIdentity;
        internal bool AnalyzeZeroVoltageData,ZeroVoltageRecord;
        internal ReviewAcquisitionQuality AcquisitionQuality=ReviewAcquisitionQuality.Unrecorded;
        internal System.Threading.CancellationToken Cancellation;
    }
    internal sealed class ReviewAnalysisResult
    {
        internal bool Success;
        internal bool Canceled;
        internal bool FirstArrivalUnresolved;
        internal ReviewAcquisitionQuality AcquisitionQuality=ReviewAcquisitionQuality.Unrecorded;
        internal string Status="";
        internal double[,] Parameters=new double[0,36],Raw=new double[0,0],Filtered=new double[0,0],Equivalent=new double[0,0];
        internal double AppliedThreshold=double.NaN;
        internal AHistoryReceipt AHistoryReceipt;
        internal AHistorySelection AHistorySelection;
    }
    internal static class ReviewAnalysisEngine
    {
        internal const string AlgorithmFileName="fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll";
        static readonly Version MinimumVersion=new Version(3,0,22,0);
        // Resolve the algorithm beside the deployed control, including hosts
        // that shadow-copy assemblies to a temporary CLR cache.
        internal static string OriginalAssemblyPath(Assembly assembly)
        {
            if(assembly==null)throw new ArgumentNullException("assembly");
            string codeBase=assembly.GetName(false).CodeBase;
            Uri uri;
            if(!String.IsNullOrEmpty(codeBase)&&Uri.TryCreate(codeBase,UriKind.Absolute,out uri)&&uri.IsFile)
                return Path.GetFullPath(uri.LocalPath);
            throw new InvalidOperationException("Cannot resolve the original file path of the .NET control or Task17 assembly.");
        }
        internal static string ControlSourcePath {get{return OriginalAssemblyPath(typeof(ReviewAnalysisEngine).Assembly);}}
        internal static string ExpectedAlgorithmPath
        {
            get
            {
                string owner=ControlSourcePath;
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(owner),AlgorithmFileName));
            }
        }
        internal static string DiagnosticHeader(string projectPath)
        {
            return "Control loaded: "+typeof(ReviewAnalysisEngine).Assembly.Location+Environment.NewLine+
                "Control source: "+ControlSourcePath+Environment.NewLine+
                "Expected Task17: "+ExpectedAlgorithmPath+Environment.NewLine+
                "Process: "+(IntPtr.Size==8?"x64":"x86")+Environment.NewLine+"Project: "+(projectPath??"");
        }
        internal static string DescribeException(Exception error)
        {
            var text=new StringBuilder();AppendException(text,error,new HashSet<Exception>());return text.ToString().TrimEnd();
        }
        static void AppendException(StringBuilder text,Exception error,HashSet<Exception> seen)
        {
            if(error==null||!seen.Add(error))return;
            text.AppendLine(error.GetType().Name+": "+error.Message);
            var missing=error as FileNotFoundException;var loading=error as FileLoadException;var image=error as BadImageFormatException;
            string file=missing!=null?missing.FileName:loading!=null?loading.FileName:image!=null?image.FileName:null;
            if(!String.IsNullOrEmpty(file))text.AppendLine("File: "+file);
            text.AppendLine("HRESULT: 0x"+error.HResult.ToString("X8",CultureInfo.InvariantCulture));
            AppendException(text,error.InnerException,seen);
            var types=error as ReflectionTypeLoadException;
            if(types!=null&&types.LoaderExceptions!=null)foreach(var loader in types.LoaderExceptions)AppendException(text,loader,seen);
        }
        // Check common dependencies once before a batch, without analyzing LOC
        // data or changing any record's analyzed/accepted state.
        internal static bool CheckEnvironment(string projectPath,int algorithmIndex,out string diagnostic)
        {
            var trace=ReviewAnalysisDebugTrace.Begin("environment","Project="+projectPath+"; algorithm="+algorithmIndex);
            try
            {
                if(String.IsNullOrWhiteSpace(projectPath))throw new ArgumentException("ProjectPath is required.");
                string root=Path.GetFullPath(projectPath);
                if(File.Exists(root))root=Path.GetDirectoryName(root);
                if(!Directory.Exists(root))throw new DirectoryNotFoundException("Project directory does not exist: "+root);
                string[] files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly);
                foreach(string required in new[]{"ITEM.in","CABLE.in"})
                {
                    string path=files.FirstOrDefault(p=>String.Equals(Path.GetFileName(p),required,StringComparison.OrdinalIgnoreCase));
                    if(path==null)throw new FileNotFoundException("Project input is missing: "+required,Path.Combine(root,required));
                    using(var input=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){input.ReadByte();}
                }
                if(HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root)) {
                    diagnostic="电容局放：使用已绑定原始 ADC 的 DEM 或兼容已校准 LOC，仅统计真实电量与可核验相位；不调用定位组件。";trace.End("capacitor-success");return true;
                }
                ValidateLocationMethod(algorithmIndex);
                if(ReviewEAnalysis.IsMethod(algorithmIndex)){ReviewEAnalysis.CheckEnvironment();diagnostic="E/E+ source-pinned runtime present; per-file validation remains required.";trace.End("E-environment-success");return true;}
                Assembly assembly=LoadAlgorithm();Type type;GetAnalysisMethod(assembly,algorithmIndex,out type);
                trace.Point("algorithm-contract-loaded");
                object target=Activator.CreateInstance(type);
                SetRequiredProperty(target,"ConnectingCableLengthM",0d);
                SetRequiredProperty(target,"AnalysisPhaseQuadrants",new[]{true,true,true,true});
                if(algorithmIndex==7)SetRequiredProperty(target,"AttenuationErrorTolerancePercent",15d);
                diagnostic="Analysis environment check passed. File-specific validation still runs during analysis.";trace.End("success");return true;
            }
            catch(Exception error)
            {
                if(IsFatal(error))throw;
                trace.Failure(error);trace.End("failed");
                diagnostic=DescribeException(error);return false;
            }
            finally{trace.Dispose();}
        }
        static bool IsFatal(Exception error)
        {return error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException||error is System.Threading.ThreadAbortException;}
        static readonly string[] OutputNames={"parameterMatrix2DOutput","pulseEnvelopeMatrix2DOutput","filteredPulseEnvelopeMatrix2DOutput",
            "equivalentPulseWaveformMatrix","statusMessage","currentParameterMatrix2DOutput","currentPulseEnvelopeMatrix2DOutput",
            "currentFilteredPulseEnvelopeMatrix2DOutput","currentEquivalentPulseWaveformMatrix"};
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static void Number(double value,double min,double max,string name)
        {if(!Finite(value)||value<min||value>max)throw new ArgumentOutOfRangeException(name);}
        static ReviewAnalysisRequest Snapshot(ReviewAnalysisRequest source)
        {
            if(source==null)throw new ArgumentNullException("request");
            HVX.SignalEvidence.RawEndpointPolicy.Parse(source.EndpointPolicy);
            if(source.MaxPulses<1||source.MaxPulses>100)throw new ArgumentOutOfRangeException("MaxPulses","Expected 1..100.");
            if(source.MultipleReflectionSuppressionCount< -1||source.MultipleReflectionSuppressionCount>100)
                throw new ArgumentOutOfRangeException("MultipleReflectionSuppressionCount","Expected -1 (automatic) or 0..100.");
            if(source.LocationDataId<0)throw new ArgumentOutOfRangeException("LocationDataId");
            Number(source.ManualThreshold,0,double.MaxValue,"ManualThreshold");Number(source.AutomaticThresholdMultiplier,0,1000,"AutomaticThresholdMultiplier");
            Number(source.AttenuationTolerancePercent,0,100,"AttenuationTolerancePercent");Number(source.CorrelationErrorPercent,0,100,"CorrelationErrorPercent");
            Number(source.ConnectingCableLengthM,0,double.MaxValue,"ConnectingCableLengthM");Number(source.SampleRateHz,6000000,double.MaxValue,"SampleRateHz");
            Number(source.OscillationFrequencyHz,0,double.MaxValue,"OscillationFrequencyHz");
            Number(source.NoiseSimilarityTolerancePercent,0,10,"NoiseSimilarityTolerancePercent");
            if(!HVX.LocationScoring.LocationScorePolicy.IsValid(source.LocationSuspiciousMinimumPercent,source.LocationManualAcceptanceMinimumPercent,source.LocationAutomaticAcceptanceMinimumPercent))
                throw new ArgumentException("Invalid or unordered location score limits.");
            if(source.PhaseQuadrants==null||source.PhaseQuadrants.Length!=4)throw new ArgumentException("Exactly four phase quadrants are required.");
            if(String.IsNullOrWhiteSpace(source.ProjectPath)||String.IsNullOrWhiteSpace(source.LocationFilePath))throw new ArgumentException("ProjectPath and LocationFilePath are required.");
            string project=Path.GetFullPath(source.ProjectPath),loc=Path.GetFullPath(source.LocationFilePath);
            if(!Directory.Exists(project)&&!File.Exists(project))throw new DirectoryNotFoundException("Project path does not exist: "+project);
            string projectRoot=File.Exists(project)?Path.GetDirectoryName(project):project;
            bool capacitor=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(projectRoot);
            if(!capacitor)ValidateLocationMethod(source.AlgorithmIndex);
            if(!File.Exists(loc))throw new FileNotFoundException("LOC file does not exist.",loc);
            bool capacitorDem=capacitor&&ReviewCapacitorDemInput.IsDem(loc);
            if(!String.Equals(Path.GetExtension(loc),".loc",StringComparison.OrdinalIgnoreCase)&&!capacitorDem)throw new ArgumentException("LocationFilePath must identify a LOC file or a bound capacitor DEM.");
            var metadata=capacitorDem?ReviewCapacitorDemInput.Metadata(loc):ReviewLocationFileReader.ReadMetadata(loc);
            bool namedZero=System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(loc),@"^(ALL|A|B|C|L1|L2|L3)_0(?:\.0+)?kV_",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool zero=namedZero||metadata.VoltageKv==0||metadata.ZeroVoltageAnalysisOptIn;
            if(zero){
                if(!source.AnalyzeZeroVoltageData||!namedZero||!metadata.ZeroVoltageAnalysisOptIn)
                    throw new InvalidDataException("该零电压记录未获准分析，仅可作为噪声参考。");
                // Validate the entire declared record, not just a header token.
                if(!capacitorDem)ReviewLocationFileReader.Read(loc,projectRoot);
            }
            double actualSampleRate=metadata.SampleRateHz;
            Number(actualSampleRate,6000000,double.MaxValue,"LOC sample rate");
            double[] cutoffs=source.LinearFilterCutoffsHz==null?new double[0]:(double[])source.LinearFilterCutoffsHz.Clone();
            if(source.ApplyLinearFilter&&(cutoffs.Length!=2||!Finite(cutoffs[0])||!Finite(cutoffs[1])||cutoffs[0]<0||cutoffs[0]>=cutoffs[1]||cutoffs[1]>=actualSampleRate/2))
                throw new ArgumentException("Linear filter requires finite 0 <= low < high <= sample rate / 2.");
            return new ReviewAnalysisRequest{EndpointPolicy=source.EndpointPolicy,ProjectPath=project,LocationFilePath=loc,LocationDataId=source.LocationDataId,AlgorithmIndex=source.AlgorithmIndex,
                MaxPulses=source.MaxPulses,MultipleReflectionSuppressionCount=source.MultipleReflectionSuppressionCount,
                AutomaticAcceptance=source.AutomaticAcceptance&&!metadata.AcquisitionQuality.ReferenceOnly,AcquisitionQuality=metadata.AcquisitionQuality,ManualThresholdEnabled=source.ManualThresholdEnabled,UseFirstArrivalTiming=source.UseFirstArrivalTiming,
                ManualThreshold=source.ManualThreshold,AutomaticThresholdMultiplier=source.AutomaticThresholdMultiplier,AttenuationTolerancePercent=source.AttenuationTolerancePercent,
                CorrelationErrorPercent=source.CorrelationErrorPercent,ConnectingCableLengthM=source.ConnectingCableLengthM,SampleRateHz=actualSampleRate,
                NoiseSimilarityTolerancePercent=source.NoiseSimilarityTolerancePercent,OscillationFrequencyHz=zero?0:source.OscillationFrequencyHz,PhaseQuadrants=zero?new[]{true,true,true,true}:(bool[])source.PhaseQuadrants.Clone(),ApplyLinearFilter=source.ApplyLinearFilter,LinearFilterCutoffsHz=cutoffs,
                LocationSuspiciousMinimumPercent=source.LocationSuspiciousMinimumPercent,LocationManualAcceptanceMinimumPercent=source.LocationManualAcceptanceMinimumPercent,LocationAutomaticAcceptanceMinimumPercent=source.LocationAutomaticAcceptanceMinimumPercent,
                AnalyzeZeroVoltageData=source.AnalyzeZeroVoltageData,ZeroVoltageRecord=zero,Cancellation=source.Cancellation,
                AHistoryBatch=!capacitor&&!zero&&source.AlgorithmIndex==5?source.AHistoryBatch:null,AHistoryAcquisition=source.AHistoryAcquisition,AHistoryPhase=source.AHistoryPhase,AHistoryRequireHeaderIdentity=source.AHistoryRequireHeaderIdentity};
        }
        static Assembly LoadAlgorithm()
        {
            if(IntPtr.Size!=8)throw new PlatformNotSupportedException("自动分析需要使用 64 位软件。 / Automatic analysis requires the 64-bit application.");
            string path=ExpectedAlgorithmPath;
            if(!File.Exists(path))throw new FileNotFoundException("请将最新版定位算法 DLL 与统一控件放在同一目录。 / Place the current Task17 DLL beside the review control.",path);
            Version diskVersion;if(!Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion,out diskVersion)||diskVersion!=MinimumVersion)
                throw new InvalidOperationException("定位组件版本不兼容，请重新安装当前软件版本。 / The location component is incompatible; reinstall the current application version.");
            Assembly assembly=Assembly.LoadFrom(path);
            var version=assembly.GetCustomAttributes(typeof(AssemblyFileVersionAttribute),false).Cast<AssemblyFileVersionAttribute>().SingleOrDefault();
            Version loadedVersion;
            if(!String.Equals(OriginalAssemblyPath(assembly),path,StringComparison.OrdinalIgnoreCase)||
                version==null||!Version.TryParse(version.Version,out loadedVersion)||loadedVersion!=MinimumVersion||loadedVersion!=diskVersion)
                throw new InvalidOperationException("已加载其他版本的定位组件，请关闭并重新启动软件。 / A different location component is already loaded; close and restart the application.");
            return assembly;
        }
        static MethodInfo GetAnalysisMethod(Assembly assembly,int algorithmIndex,out Type type)
        {
            ValidateLocationMethod(algorithmIndex);
            string stem=algorithmIndex==5?"Sixth":"Project";
            type=assembly.GetType("HVX.Task17.Location."+stem+"LocationAnalyzer",true);
            if(algorithmIndex==7)
            {
                // B must never use an older Project entry that silently rejects or
                // substitutes its index. Research sources are not auto-imported.
                var fileVersion=assembly.GetCustomAttributes(typeof(AssemblyFileVersionAttribute),false).Cast<AssemblyFileVersionAttribute>().SingleOrDefault();
                Version version;var capability=type.GetProperty("SupportsUltraShortCableMethod",BindingFlags.Public|BindingFlags.Instance);
                if(fileVersion==null||!Version.TryParse(fileVersion.Version,out version)||version<new Version(2,11,0,0)||
                    capability==null||capability.PropertyType!=typeof(bool)||!capability.CanRead||capability.CanWrite||
                    !Object.Equals(capability.GetValue(Activator.CreateInstance(type),null),true))
                    throw new InvalidOperationException("定位方法 B 的组件尚未就绪，请选择 A 或更新软件。 / Location method B is not available; select A or update the software.");
            }
            MethodInfo[] entries=type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly).Where(m=>m.Name=="Analyze"+stem+"LocationIncremental").ToArray();
            if(entries.Length!=1||entries[0].ReturnType!=typeof(bool))throw new InvalidOperationException("Task17 analysis entry point is not unique or compatible.");
            if(entries[0].GetParameters().Length!=(algorithmIndex==5?40:37))throw new InvalidOperationException("定位组件不兼容，请更新软件。 / The location component is incompatible; update the software.");
            return entries[0];
        }
        static void ValidateLocationMethod(int index)
        {
            if(index!=5&&index!=7&&index!=8&&index!=9)throw new ArgumentException("原定位方法已停用，请重新选择 A、B、E 或 E+。 / Select location method A, B, E or E+.");
        }
        static void SetRequiredProperty(object target,string name,object value)
        {
            PropertyInfo property=target.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.Instance);
            if(property==null||!property.CanWrite||property.PropertyType!=value.GetType())throw new InvalidOperationException("Current Task17 property is missing or incompatible: "+name);
            property.SetValue(target,value,null);
        }
        internal static ReviewAnalysisResult Analyze(ReviewAnalysisRequest request)
        {
            FileStream inputGuard=null;ReviewCalibrationInputScope calibrationInput=null;
            var trace=ReviewAnalysisDebugTrace.Begin("file","Path="+(request==null?"<null>":request.LocationFilePath)+"; algorithm="+(request==null?-1:request.AlgorithmIndex));
            try
            {
                // Keep declared metadata and body stable through validation and
                // the algorithm's own file read, including B and capacitor paths.
                if(request!=null&&!String.IsNullOrWhiteSpace(request.LocationFilePath))
                    inputGuard=new FileStream(Path.GetFullPath(request.LocationFilePath),FileMode.Open,FileAccess.Read,FileShare.Read);
                ReviewAnalysisRequest r=Snapshot(request);
                trace.Point("metadata-snapshot","sampleRateHz="+r.SampleRateHz.ToString("R",CultureInfo.InvariantCulture)+"; maximumPulses="+r.MaxPulses);
                string root=File.Exists(r.ProjectPath)?Path.GetDirectoryName(r.ProjectPath):r.ProjectPath;
                if(HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root)){
                    var capacitorResult=ReviewCapacitorDemInput.IsDem(r.LocationFilePath)?ReviewCapacitorAnalysis.AnalyzeDem(r,root):ReviewCapacitorAnalysis.Analyze(r,root);
                    trace.End("capacitor success="+capacitorResult.Success+"; rows="+capacitorResult.Parameters.GetLength(0));return ReviewAcquisitionQuality.Apply(capacitorResult,r.AcquisitionQuality);
                }
                // Neither blank nor invalid samples may be removed by the
                // legacy parser and silently change every subsequent timestamp.
                // Zero-voltage records were already fully checked in Snapshot.
                if(ReviewEAnalysis.IsMethod(r.AlgorithmIndex)){
                    // E/E+ use our strict ReadCore parser to obtain their input.
                    // That one read validates every sample, count and provenance
                    // while inputGuard still holds the source stable. A separate
                    // ValidateSamples pass would parse the identical body twice.
                    // Zero-voltage Snapshot validation intentionally stays intact.
                    trace.Point("E-strict-read-delegated");
                    var eResult=ReviewEAnalysis.Analyze(r);ReviewLocationScoreAcceptance.Apply(eResult,r);trace.End("E success; rows="+eResult.Parameters.GetLength(0));return ReviewAcquisitionQuality.Apply(eResult,r.AcquisitionQuality);
                }
                if(!r.ZeroVoltageRecord)ReviewLocationFileReader.ValidateSamples(r.LocationFilePath,root);
                trace.Point("sample-validation");
                Assembly assembly=LoadAlgorithm();
                trace.Point("algorithm-load");
                calibrationInput=ReviewCalibrationInputScope.Create(r.ProjectPath,r.LocationFilePath);
                trace.Point("calibration-projection");
                Type type;MethodInfo method=GetAnalysisMethod(assembly,r.AlgorithmIndex,out type);ParameterInfo[] signature=method.GetParameters();
                object target=Activator.CreateInstance(type);
                if(r.AlgorithmIndex==5){
                    SetRequiredProperty(target,"Method6ConfirmedLegacyVelocityUnits",ConfirmedLegacyUnits.Contains(inputGuard));
                }
                SetRequiredProperty(target,"ConnectingCableLengthM",r.ConnectingCableLengthM);
                SetRequiredProperty(target,"EndpointPolicy",r.EndpointPolicy);
                SetRequiredProperty(target,"AnalysisPhaseQuadrants",r.PhaseQuadrants);
                if(r.AlgorithmIndex==7)SetRequiredProperty(target,"AttenuationErrorTolerancePercent",r.AttenuationTolerancePercent);
                AHistoryIdentity historyIdentity=null;var history=new AHistorySelection();
                if(r.AlgorithmIndex==5&&r.AHistoryBatch!=null){
                    r.AHistoryBatch.Cancellation.ThrowIfCancellationRequested();
                    try{historyIdentity=AHistoryEvidence.Identify(r);history=AHistoryEvidence.Select(r.AHistoryBatch,historyIdentity,r.AHistoryPhase);}
                    catch(OperationCanceledException){throw;}
                    catch(Exception error){if(IsFatal(error))throw;trace.Failure(error);history=new AHistorySelection{State="unavailable",Detail=error.Message};historyIdentity=null;}
                }
                trace.Point("history-selection","qualifiedRows="+history.QualifiedRows);
                var inputs=new Dictionary<string,object>(StringComparer.Ordinal){
                    {"projectPath",calibrationInput.AlgorithmProjectPath},{"locationFilePath",r.LocationFilePath},{"locSampleHz",r.SampleRateHz},{"locData",new double[0]},{"useLocData",false},
                    {"previousParameterMatrix",history.Parameters},{"previousPulseEnvelopeMatrix",history.Raw},{"previousFilteredPulseEnvelopeMatrix",history.Filtered},
                    {"previousEquivalentPulseWaveformMatrix",history.Equivalent},{"locationDataId",r.LocationDataId},
                    // Manual acceptance is a review policy, not a request to skip
                    // the automatic pulse discovery/selection pipeline.
                    {"automaticAnalysis",true},{"useFirstArrivalTiming",r.UseFirstArrivalTiming},{"useAttenuationReference",true},{"usePulseWidthComparison",true},
                    {"thresholdType",r.ManualThresholdEnabled?-1:1},{"manualThresholdAmplitude",r.ManualThreshold},{"automaticThresholdMultiplier",r.AutomaticThresholdMultiplier},
                    {"multipleReflectionSuppressionCount",r.MultipleReflectionSuppressionCount},{"maximumDischargeCount",r.MaxPulses},{"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},
                    {"initialPhaseDegrees",0d},{"oscillationFrequencyHz",r.OscillationFrequencyHz},{"applyLinearFilter",r.ApplyLinearFilter},{"linearFilterCutoffsHz",r.LinearFilterCutoffsHz},
                    {"useEquivalentPulseWindow",false},{"equivalentPulseWindowNs",new double[0]}
                };
                if(r.AlgorithmIndex==5){inputs.Add("minimumCorrelationPercent",100-r.CorrelationErrorPercent);inputs.Add("attenuationErrorPercent",r.AttenuationTolerancePercent);inputs.Add("pulseWidthErrorPercent",-1d);inputs.Add("scoreWeights",new double[0]);}
                else inputs.Add("locationAlgorithm",r.AlgorithmIndex);
                var arguments=new object[signature.Length];var indices=new Dictionary<string,int>(StringComparer.Ordinal);
                for(int i=0;i<signature.Length;i++)
                {
                    ParameterInfo parameter=signature[i];indices.Add(parameter.Name,i);
                    if(parameter.IsOut)
                    {
                        if(!OutputNames.Contains(parameter.Name)||parameter.ParameterType!=(parameter.Name=="statusMessage"?typeof(string).MakeByRefType():typeof(double[,]).MakeByRefType()))
                            throw new InvalidOperationException("Unexpected Task17 output: "+parameter.Name);
                    }
                    else{object value;if(!inputs.TryGetValue(parameter.Name,out value)||value==null||parameter.ParameterType!=value.GetType())throw new InvalidOperationException("Task17 input mismatch: "+parameter.Name);arguments[i]=value;}
                }
                foreach(string output in OutputNames)if(!indices.ContainsKey(output))throw new InvalidOperationException("Missing Task17 output: "+output);
                bool success;
                if(r.AHistoryBatch!=null)r.AHistoryBatch.Cancellation.ThrowIfCancellationRequested();
                trace.Point("algorithm-invoke-start");
                try{success=(bool)method.Invoke(target,arguments);}catch(TargetInvocationException error){if(error.InnerException!=null)ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
                trace.Point("algorithm-invoke-end","returned="+success);
                calibrationInput.VerifySources();
                trace.Point("calibration-source-verification");
                string status=arguments[indices["statusMessage"]] as string??"";
                ReviewStatusJson.ValidateText(status);
                status=calibrationInput.AppendStatus(status);
                bool unresolvedFirstArrival=r.UseFirstArrivalTiming&&(status.IndexOf("first_arrival_unresolved_no_peak_fallback",StringComparison.Ordinal)>=0||
                    status.IndexOf("no peak timing was substituted",StringComparison.Ordinal)>=0);
                if(!success){trace.Point("algorithm-reported-failure",status);trace.End("failed");return new ReviewAnalysisResult{Status=status,FirstArrivalUnresolved=unresolvedFirstArrival};}
                var result=new ReviewAnalysisResult{Parameters=Matrix(arguments,indices,"currentParameterMatrix2DOutput"),Raw=Matrix(arguments,indices,"currentPulseEnvelopeMatrix2DOutput"),
                    Filtered=Matrix(arguments,indices,"currentFilteredPulseEnvelopeMatrix2DOutput"),Equivalent=Matrix(arguments,indices,"currentEquivalentPulseWaveformMatrix"),Status=status,FirstArrivalUnresolved=unresolvedFirstArrival};
                ValidateOutput(result,r.MaxPulses);
                // History receipts must describe the same policy-gated rows
                // that are published, not pre-policy automatic flags.
                ReviewLocationScoreAcceptance.Apply(result,r);
                trace.Point("output-validation","rows="+result.Parameters.GetLength(0)+"; rawColumns="+result.Raw.GetLength(1));
                // If a configuration used by the auxiliary changed while A ran,
                // do not publish a ranking based on a mixed evidence snapshot.
                if(history.QualifiedRows>0)AHistoryEvidence.VerifyUnchanged(historyIdentity);
                if(r.AlgorithmIndex==5&&r.AHistoryBatch!=null){
                    try{if(r.AutomaticAcceptance&&historyIdentity!=null&&r.AHistoryBatch.Failure.Length==0){AHistoryEvidence.VerifyUnchanged(historyIdentity);result.AHistoryReceipt=AHistoryEvidence.Capture(historyIdentity,result);}}
                    catch(Exception error){if(IsFatal(error))throw;trace.Failure(error);history.Detail="History capture unavailable: "+error.Message;if(history.QualifiedRows==0)history.State="unavailable";}
                    result.Status=AHistoryEvidence.AppendStatus(result.Status,history);
                    result.AHistorySelection=history;
                }
                for(int row=0;row<result.Parameters.GetLength(0);row++)
                {
                    double rho=result.Parameters[row,21];
                    if(r.ZeroVoltageRecord){result.Parameters[row,2]=0;result.Parameters[row,3]=Double.NaN;}
                }
                if(r.ZeroVoltageRecord){
                    var evidence=ReviewStatusJson.DeserializeObject(result.Status) as Dictionary<string,object>;
                    if(evidence==null)throw new InvalidDataException("定位结果缺少有效分析记录。");
                    evidence["zero_voltage_analysis"]=new Dictionary<string,object>{{"analysisOptIn",true},{"targetVoltageKv",0},{"phaseKnown",false},{"voltagePhaseApplied",false},{"voltagePhase",null},{"oscillationCycleKnown",false}};
                    result.Status=ReviewStatusJson.Serialize(evidence);
                }
                ReviewStatusJson.ValidateText(result.Status);
                result.AppliedThreshold=Threshold(status);result.Success=true;trace.End("success; rows="+result.Parameters.GetLength(0));return ReviewAcquisitionQuality.Apply(result,r.AcquisitionQuality);
            }
            catch(OperationCanceledException) when(request!=null&&request.Cancellation.IsCancellationRequested)
            {
                trace.End("canceled");
                return new ReviewAnalysisResult{Canceled=true,Status="Analysis canceled."};
            }
            catch(Exception error)
            {
                if(IsFatal(error))throw;
                trace.Failure(error);trace.End("failed");
                return new ReviewAnalysisResult{Status=DescribeException(error)};
            }
            finally {try {if(calibrationInput!=null)calibrationInput.Dispose();}finally {try{if(inputGuard!=null)inputGuard.Dispose();}finally{trace.Dispose();}}}
        }
        static double[,] Matrix(object[] arguments,Dictionary<string,int> indices,string name)
        {var matrix=arguments[indices[name]] as double[,];if(matrix==null)throw new InvalidDataException("Task17 returned no matrix: "+name);return (double[,])matrix.Clone();}
        static double Threshold(string status)
        {
            // Budget violations are record failures, never silently converted
            // into an otherwise successful analysis with a missing threshold.
            try
            {
                var root=ReviewStatusJson.DeserializeObject(status) as Dictionary<string,object>;object item;
                if(root==null||!root.TryGetValue("current_analysis",out item))return double.NaN;
                var current=item as Dictionary<string,object>;
                if(current==null||!current.TryGetValue("output_threshold",out item)||item==null)return double.NaN;
                double value=Convert.ToDouble(item,CultureInfo.InvariantCulture);return Finite(value)&&value>=0?value:double.NaN;
            }
            catch(ArgumentException){return double.NaN;}catch(InvalidOperationException){return double.NaN;}catch(FormatException){return double.NaN;}catch(OverflowException){return double.NaN;}
        }
        internal static void ValidateOutput(ReviewAnalysisResult result,int maximumRows)
        {
            double[,] p=result.Parameters,raw=result.Raw,filtered=result.Filtered,equivalent=result.Equivalent;
            int rows=p.GetLength(0),width=raw.GetLength(1);
            // Every output pulse, including unpaired review rows, obeys the
            // requested cap. Reject contract violations rather than truncate them.
            if(p.GetLength(1)!=36||rows>maximumRows||raw.GetLength(0)!=rows||filtered.GetLength(0)!=rows||equivalent.GetLength(0)!=rows||
                (rows>0&&(width==0||filtered.GetLength(1)!=width||equivalent.GetLength(1)==0)))throw new InvalidDataException("Task17 returned inconsistent or excessive matrix dimensions: P="+rows+"x"+p.GetLength(1)+", Raw="+raw.GetLength(0)+"x"+width+", Filtered="+filtered.GetLength(0)+"x"+filtered.GetLength(1)+", Equivalent="+equivalent.GetLength(0)+"x"+equivalent.GetLength(1)+".");
            for(int row=0;row<rows;row++)
            {
                double first=p[row,31],last=p[row,32];
                if(!Finite(first)||!Finite(last)||first<0||last<first||last>int.MaxValue||first!=Math.Truncate(first)||last!=Math.Truncate(last)||last-first+1>width)
                    throw new InvalidDataException("Task17 returned invalid waveform bounds.");
                int count=(int)(last-first+1);
                if(!Finite(p[row,10])||p[row,10]<first||p[row,10]>last||p[row,10]!=Math.Truncate(p[row,10]))throw new InvalidDataException("Incident marker is outside the returned waveform.");
                double reflection=p[row,11];
                if(Finite(reflection)&&reflection>=0&&(reflection<first||reflection>last||reflection!=Math.Truncate(reflection)))throw new InvalidDataException("Reflection marker is outside the returned waveform.");
                if(double.IsInfinity(reflection)||p[row,9]!=row||(p[row,35]!=0&&p[row,35]!=1)||double.IsInfinity(p[row,33])||
                    (Finite(p[row,33])&&(p[row,33]<0||p[row,33]>1)))throw new InvalidDataException("Task17 returned invalid row IDs, acceptance flags or confidence.");
                for(int k=0;k<width;k++)
                    if(k<count?(!Finite(raw[row,k])||!Finite(filtered[row,k])):(!double.IsNaN(raw[row,k])||!double.IsNaN(filtered[row,k])))
                        throw new InvalidDataException("Task17 waveform samples violate the finite-data / NaN-padding contract.");
                bool padding=false;
                for(int k=0;k<equivalent.GetLength(1);k++){double value=equivalent[row,k];if(double.IsNaN(value)){padding=true;continue;}if(padding||!Finite(value))throw new InvalidDataException("Equivalent waveform padding is invalid.");}
            }
        }
    }
}
