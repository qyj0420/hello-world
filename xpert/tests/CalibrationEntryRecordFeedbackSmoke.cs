using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using HVX.Controls;
using Xiongxin.Calibration;

// Synthetic files and managed input delegates only. No SDK, worker or product Main.
internal static class CalibrationEntryRecordFeedbackSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Get(object owner,string name){return owner.GetType().GetField(name,B).GetValue(owner);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(owner,args);}
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static LiveCalibrationRecord Record(string root,string level,bool arrival,double coefficient=5000)
    {
        var wave=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))).ToArray();
        return new LiveCalibrationRecord {Version=3,Phase="ALL",Level=level,MeasurementKind="Cable",DeviceSerial="OFFLINE-ONLY",DeviceModel="Synthetic",
            FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,ProjectHash=LiveCalibrationRecord.Identity(root),CreatedUtc=DateTime.UtcNow.ToString("o"),
            LengthM=180,VelocityMPerUs=180,SampleRateHz=100e6,IncidentSeconds=4e-6,IncidentPeakIndex=400,ReflectionSeconds=Double.NaN,ReflectionPeakIndex=-1,
            ReflectionStatus="NotDetected",VelocitySource="Configured",ReflectionNotice=LiveCalibrationRecord.UnconfirmedReflectionNotice,
            CoefficientPcPerV=coefficient,ChargePc=level=="5nC"?5000:100,SnrDb=60,Correlation=Double.NaN,AttenuationDb=Double.NaN,
            CoefficientProcessing="HP10K_NO_LP",CoefficientIncidentPeakIndex=400,RangeIndex=4,PulseCount=4,AcquisitionMode="Pulse",FirstArrival=arrival,Average=wave,
            RawFiles=new[]{new CalibrationRawReference{RelativePath="original-reference-not-required.bin",Sha256=Convert.ToBase64String(new byte[32])}}};
    }
    static string Project(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"一体三芯电缆\"\r\n电缆长度: = \"180\"\r\nALL相采用波速: = \"180\"\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t180m\r\n");return root;
    }
    static CalibrationProgramControl Open(string root,bool arrival,List<string> logs)
    {
        var page=new CalibrationProgramControl();page.CalibrationEntryDiagnosticSink=logs.Add;
        page.ShowReflectionSelectionNoticeForTests=_=>{};page.ConfirmQualityWarningSaveForTests=_=>true;
        page.UseFirstArrivalTiming=arrival;page.ProjectPath=root;page.SelectedLevelCode="100pC";return page;
    }
    static async Task Run(string qa)
    {
        foreach(bool arrival in new[]{false,true})
        {
            string root=Project(Path.Combine(qa,arrival?"arrival":"peak"));
            string old=Record(root,"5nC",!arrival).Save(root),oldHash=LiveCalibrationRecord.HashFile(old);
            var current=Record(root,"100pC",arrival,100/.15);string fresh=current.Save(root);
            var logs=new List<string>();using(var page=Open(root,arrival,logs))
            {
                Check(page.HasCalibrationData&&page.IsCalibrationSavedValid&&page.SelectedCalibrationSaved,"applicable saved level loads beside another old timing");
                Check(String.IsNullOrEmpty(page.LastCalibrationError),"old incompatible level does not label valid current load as failure");
                Check(logs.Count==1&&logs[0].Contains("ALL_5nC.cal")&&logs[0].Contains("计时方式"),"old filename and timing detail go to diagnostic sink only; synthetic diagnostics="+String.Join(" | ",logs));
                Check(page.GetLevelSaveState(-1,"5nC")=="Empty","incompatible old level remains excluded");
                Check(page.UseFirstArrivalTiming==arrival&&LiveCalibrationRecord.Load(root,old).FirstArrival==!arrival,"loading never rewrites either timing fact");
                var notices=new List<string>();page.ShowCalibrationEntryNoticeForTests=notices.Add;
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(false);page.NotifyCalibrationPageEntered();
                Check(notices.Count==0&&page.CalibrationEntryEmergencySignalHigh==false,"actual pressed stop has no press-stop or old-record popup");
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(true);page.NotifyCalibrationPageEntered();
                Check(notices.Count==1&&notices[0].Length<100&&notices[0].Contains("解除")&&notices[0].Contains("不会加压")&&!notices[0].Contains("ALL_")&&!notices[0].Contains("已放电"),"released stop has one concise safety-only reminder without false discharge claim");
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(null);page.NotifyCalibrationPageEntered();
                Check(notices.Count==2&&notices[1].Contains("未确认")&&!page.CalibrationEntryEmergencySignalHigh.HasValue&&!notices[1].Contains("解除")&&!notices[1].Contains("已按下"),"unknown is visible without inventing either physical state");
                var pending=new TaskCompletionSource<bool?>();page.ReadCalibrationEntryEmergencyForTests=()=>pending.Task;page.NotifyCalibrationPageEntered();
                var entry=(Task)Get(page,"calibrationEntryCheckTask");
                Check(page.IsBusy&&((string)page.GetType().GetProperty("CalibrationBusyCaption",B).GetValue(page,null)).Contains("正在确认急停状态"),"pending DI check immediately identifies its purpose in existing button feedback");
                page.NotifyCalibrationPageEntered();Check(Object.ReferenceEquals(entry,Get(page,"calibrationEntryCheckTask")),"pending entry does not spawn a second input reader");
                pending.SetResult(false);await entry;Check(!page.IsBusy&&notices.Count==2,"completed LOW clears waiting feedback without another reminder");
                Check((bool)Call(page,"SaveLiveCalibration",-1,"100pC"),"applicable current saved coefficient can save again");
                Check(LiveCalibrationRecord.Load(root,fresh).FirstArrival==arrival&&LiveCalibrationRecord.HashFile(old)==oldHash,"current save preserves actual timing and untouched old original");
            }
            using(var reload=Open(root,arrival,new List<string>()))Check(reload.IsCalibrationSavedValid&&String.IsNullOrEmpty(reload.LastCalibrationError),"valid current save reloads without another old-level error");
            string legacy=Path.Combine(root,"Calibration","Live","renamed-old.hvcal");Directory.CreateDirectory(Path.GetDirectoryName(legacy));File.Copy(fresh,legacy);
            var altered=Record(root,"100pC",arrival,1234);string replacement=altered.Save(root);File.SetAttributes(legacy,FileAttributes.Normal);File.Copy(replacement,legacy,true);current.Save(root);
            using(var priority=Open(root,arrival,new List<string>()))
            {
                var capture=(CalibrationCaptureResult)((IDictionary)Get(priority,"liveCaptures"))["-1:100pC"];
                Check(capture.SavedRecordPath==fresh&&capture.Assessment.CoefficientPcPerV==current.CoefficientPcPerV,"root current valid record wins over renamed older duplicate metadata");
            }
            Check(LiveCalibrationRecord.HashFile(old)==oldHash,"all entry and reload operations leave incompatible original unchanged");
        }
        string unmatched=Project(Path.Combine(qa,"unmatched"));string only=Record(unmatched,"5nC",true).Save(unmatched),digest=LiveCalibrationRecord.HashFile(only);
        var rejected=new List<string>();using(var page=Open(unmatched,false,rejected))
        {
            Check(!page.HasCalibrationData&&!page.IsCalibrationSavedValid&&page.LastCalibrationError.Contains("未找到适用于当前设置"),"no applicable record remains explicitly unavailable");
            Check(!page.LastCalibrationError.Contains("ALL_5nC")&&rejected.Single().Contains("ALL_5nC"),"operator gets short status while exact old file stays in logs");
            Check(LiveCalibrationRecord.HashFile(only)==digest,"unmatched record is not relabeled or rewritten");
        }
        foreach(bool arrival in new[]{false,true})
        {
            string root=Project(Path.Combine(qa,arrival?"new-capture-arrival":"new-capture-peak"));
            string old=Record(root,"5nC",!arrival).Save(root),oldDigest=LiveCalibrationRecord.HashFile(old);
            var wave=Record(root,"100pC",arrival).Average;
            var windows=Enumerable.Range(0,4).Select(_=>(double[])wave.Clone()).ToArray();
            var quality=HVX.OWTS.Task38.CalibrationIncidentQuality.Analyze(windows,200,336,465,100,.2,false,"offline-new-capture",null);
            string raw=Path.Combine(root,"synthetic-input.bin");File.WriteAllBytes(raw,new byte[]{1,2,3,4});
            using(var page=Open(root,arrival,new List<string>()))
            {
                page.SetPhaseParameters(-1,180,180,false,false);page.SetPhaseWaveform(-1,"100pC",wave,0,1e-8);
                var capture=new CalibrationCaptureResult{Average=wave,Windows=windows,SampleRateHz=1e8,Assessment=quality,RawPaths=new[]{raw},
                    DeviceSerial="OFFLINE-ONLY",DeviceModel="Synthetic",RangeIndex=4,AcquisitionMode="Pulse",
                    Reflection=new TdrCorrelationResult{Valid=false,IncidentIndex=400,ReflectionIndex=-1,Message="Synthetic incident only."}};
                Call(page,"ReceiveLiveCapture",-1,"100pC",capture);
                Check((bool)Call(page,"SaveLiveCalibration",-1,"100pC")&&String.IsNullOrEmpty(page.LastCalibrationError),"new synthetic acquisition saves despite unrelated incompatible old level");
                var saved=LiveCalibrationRecord.Load(root,LiveCalibrationRecord.RecordPath(root,"ALL","100pC"));
                Check(saved.FirstArrival==arrival&&saved.Average.SequenceEqual(wave)&&saved.PulseCount==4,"new save records actual current timing, exact samples and all actual pulse count");
            }
            using(var reload=Open(root,arrival,new List<string>()))Check(reload.SelectedCalibrationSaved&&reload.IsCalibrationSavedValid&&String.IsNullOrEmpty(reload.LastCalibrationError),"newly computed saved level reloads valid without old-file popup state");
            Check(LiveCalibrationRecord.HashFile(old)==oldDigest,"new acquisition/save leaves incompatible old CAL untouched");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {Directory.CreateDirectory(args[0]);var run=Run(args[0]);var until=DateTime.UtcNow.AddSeconds(25);
            while(!run.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException("offline entry feedback test timeout");Application.DoEvents();Thread.Sleep(1);}
            run.GetAwaiter().GetResult();Console.WriteLine("CALIBRATION_ENTRY_RECORD_FEEDBACK_PASS="+checks+"; SDK_CALLS=0; ACTUAL_WORKER_STARTED=0; PRODUCT_STARTED=0; ORIGINAL_DATA_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
