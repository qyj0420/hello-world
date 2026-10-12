using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using HVX.CalibrationStorage;
using Xiongxin.OWTS.Desktop;

static class PressureCalibrationSelectionSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(InvalidDataException){Check(true,message);return;}throw new Exception(message);}
    static string Block(string phase){return "0\t"+phase+"\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n";}
    static string MakeProject(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"三相单芯电缆\"\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),Block("A")+Block("B")+Block("C"));
        string path=Path.Combine(root,"ALL_5nC.cal");
        string header="Date\t2026-08-26\nTime\t19:47:00\nPhase\tALL\nCal.value (pC)\t5000\nNI_Calib_factor\t1845.1965311409\nCable length (m)\t169.7\nV (m/usec)\t169.6\nSR(N)\t125000000\nStart_point (s)\t0.00000016\nFinal_point (s)\t0.00000216\nFilterstate\t0\nCalib_Attenuation\t0\nComment\tlegacy fixture\n";
        File.WriteAllText(path,header+String.Join("\n",Enumerable.Range(0,512).Select(i=>(i==20?1:i==220?.5:0).ToString("R",CultureInfo.InvariantCulture))));
        return path;
    }
    static void SaveLive(string project)
    {
        var raw=new CalibrationRawReference[3];
        for(int i=0;i<3;i++){string name="live-"+i+".raw";File.WriteAllBytes(Path.Combine(project,name),new byte[]{1,2,(byte)i});raw[i]=new CalibrationRawReference{RelativePath=name,Sha256=LiveCalibrationRecord.HashFile(Path.Combine(project,name))};}
        var wave=new double[512];wave[20]=1;wave[220]=.5;
        new LiveCalibrationRecord {Phase="A",Level="5nC",DeviceSerial="QA000/0001",DeviceModel="fixture",ProjectHash=LiveCalibrationRecord.Identity(project),
            CreatedUtc=DateTime.UtcNow.ToString("o"),FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,
            LengthM=170,VelocityMPerUs=170,SampleRateHz=100e6,IncidentSeconds=20/100e6,ReflectionSeconds=220/100e6,
            CoefficientPcPerV=5000,ChargePc=5000,SnrDb=40,Correlation=.99,RangeIndex=7,PulseCount=3,IncidentPeakIndex=20,ReflectionPeakIndex=220,
            FirstArrival=true,Average=wave,RawFiles=raw}.Save(project);
    }
    static int Main(string[] args){try{
        string project=Path.Combine(args[0],"selection");string path=MakeProject(project);byte[] original=File.ReadAllBytes(path);
        Check(PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","0000000001")==null,"CAL alone is never a live calibration");
        var candidate=LegacyCalibrationBinding.ReadCandidate(project,path);
        var binding=LegacyCalibrationBinding.Confirm(project,candidate,"ALL","QA000/0001","0000000001",true,true,"系数与参考波速适用于当前设备和回路；历史设备未知。");
        var selected=PressureCalibrationSelection.Find(project,"B",true,"QA000/0001","0000000001");
        Check(selected!=null&&selected.IsLegacy&&selected.Source==binding.BindingPath,"same-length phase accepts operator-bound legacy source");
        Check(selected.CoefficientPcPerV==1845.1965311409&&selected.VelocityMPerUs==169.6,"exact confirmed coefficient and reference velocity are retained");
        Check(selected.ChargePc==5000,"confirmed legacy reference charge propagated without using hardware defaults");
        Check(selected.RecordedRangeIndex==null,"legacy source invents no acquisition range");
        Check(selected.SourceUtc==DateTimeOffset.Parse(binding.ConfirmedUtc),"legacy source timestamp is confirmation time");
        selected.Revalidate();Check(true,"legacy source revalidates before capture");
        Check(PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","0000000002")==null,"ART identity change rejects legacy binding");
        Check(PressureCalibrationSelection.Find(project,"A",true,"QA000/0002","0000000001")==null,"Pico identity change rejects legacy binding");
        Check(PressureCalibrationSelection.Find(project,"A",false,"QA000/0001","0000000001")==null,"timing change rejects legacy binding");
        Check(PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","")==null,"missing actual ART cannot use legacy binding");
        File.AppendAllText(path,"\n1");Reject(()=>selected.Revalidate(),"original changed after preparation is rejected");File.WriteAllBytes(path,original);
        selected.Revalidate();
        int contextChecks=0;bool captureCalled=false;
        Action boundary=()=>{selected.RevalidateForCapture(()=>contextChecks++,CancellationToken.None);captureCalled=true;};
        boundary();Check(captureCalled&&contextChecks==1,"capture boundary repeats context and calibration checks");
        captureCalled=false;File.AppendAllText(path,"\n1");Reject(boundary,"source changed during awaited confirmation or monitor shutdown is rejected before capture");Check(!captureCalled,"changed calibration never reaches capture callback");File.WriteAllBytes(path,original);
        using(var cancelled=new CancellationTokenSource()){
            cancelled.Cancel();bool rejected=false;int before=contextChecks;
            try{selected.RevalidateForCapture(()=>contextChecks++,cancelled.Token);captureCalled=true;}catch(OperationCanceledException){rejected=true;}
            Check(rejected&&!captureCalled&&contextChecks==before,"cancellation before boundary avoids context work and capture");
        }
        using(var during=new CancellationTokenSource()){
            bool rejected=false;
            try{selected.RevalidateForCapture(()=>during.Cancel(),during.Token);captureCalled=true;}catch(OperationCanceledException){rejected=true;}
            Check(rejected&&!captureCalled,"cancellation during validation cannot reach capture");
        }
        bool identityRejected=false;try{selected.RevalidateForCapture(()=>{throw new OperationCanceledException("changed device");},CancellationToken.None);captureCalled=true;}catch(OperationCanceledException){identityRejected=true;}
        Check(identityRejected&&!captureCalled,"changed current device after monitor stop cannot reach capture");
        string sourceRoot=args.Length>1?args[1]:Directory.GetCurrentDirectory();
        string hostSource=File.ReadAllText(Path.Combine(sourceRoot,"src","ModuleHost.FactoryPressure.cs"));
        int capture=hostSource.IndexOf("\"CaptureManualZeroAsync\":\"CaptureAsync\").Invoke",StringComparison.Ordinal);
        Check(capture>=0,"single capture selects the explicit manual-zero or supervised worker entry");
        int stop=hostSource.LastIndexOf("await session.StopMonitor()",capture,StringComparison.Ordinal);
        int dispatch=hostSource.LastIndexOf("var task=DispatchPreparedPressureCapture",capture,StringComparison.Ordinal);
        int guard=hostSource.LastIndexOf("calibration.RevalidateForCapture(validateBatch,token)",capture,StringComparison.Ordinal);
        int batchCapture=hostSource.IndexOf("bridge.GetMethod(\"CaptureBatchAsync\")",guard,StringComparison.Ordinal);
        Check(stop>=0&&dispatch>stop&&guard>dispatch&&batchCapture>guard&&capture>batchCapture,
            "shared calibration guard follows awaited monitor shutdown and precedes both batch and single capture delegates");
        int batchValidator=hostSource.IndexOf("Action validateBatch=",StringComparison.Ordinal);
        int baseValidation=hostSource.IndexOf("validate();",batchValidator,StringComparison.Ordinal);
        Check(batchValidator>=0&&baseValidation>batchValidator&&baseValidation<stop,
            "batch context validation retains the original device and project validation");
        SaveLive(project);
        var live=PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","0000000001");
        Check(live!=null&&!live.IsLegacy&&live.CoefficientPcPerV==5000&&live.RecordedRangeIndex==7,"genuine new calibration takes priority over legacy binding");
        Check(live.ChargePc==5000,"live source reference charge propagated");
        string oldFolder=Path.Combine(project,"Calibration","Live");Directory.CreateDirectory(oldFolder);
        string oldLive=Path.Combine(oldFolder,"A_5nC.hvcal");File.Move(live.Source,oldLive);
        live=PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","0000000001");
        Check(live!=null&&live.Source==oldLive,"historical formal archive uses actual source path rather than new canonical .cal");
        live.Revalidate();Check(true,"historical formal pressure selection revalidates before capture");
        var segment=LiveCalibrationRecord.Load(project,live.Source,true);segment.AcquisitionMode="Segment";segment.PulseCount=4096;segment.Save(project);
        Check(File.Exists(oldLive),"root-format resave preserves historical formal source");
        live=PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","0000000001");
        segment=LiveCalibrationRecord.Load(project,live.Source,true);segment.PulseCount=4095;segment.Save(project);
        Reject(()=>live.Revalidate(),"prepared selection rejects replaced record even when coefficient unchanged");
        segment.PulseCount=4096;segment.Save(project);
        live=PressureCalibrationSelection.Find(project,"A",true,"QA000/0001","0000000001");
        Check(live!=null&&!live.IsLegacy&&live.CoefficientPcPerV==5000&&LiveCalibrationRecord.Load(project,live.Source,true).PulseCount==4096,"pressure consumer accepts bounded segment calibration with all measured pulses");
        int segmentContext=0;live.RevalidateForCapture(()=>segmentContext++,CancellationToken.None);Check(segmentContext==1,"segment source receives normal context and raw integrity checks before consumption");
        live.Revalidate();File.AppendAllText(Path.Combine(project,"live-0.raw"),"bad");Reject(()=>live.Revalidate(),"changed live raw evidence stops prepared session");
        Check(File.ReadAllBytes(path).SequenceEqual(original),"original CAL stays unchanged after selection");
        Console.WriteLine("PRESSURE_CALIBRATION_SELECTION_PASS="+checks+"; HARDWARE=False; USER_PROJECTS_WRITTEN=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
