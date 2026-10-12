using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HVX.Controls;
using HVX.CalibrationStorage;
using Xiongxin.Calibration;

// Synthetic source and project only. No SDK, product, or process worker entry.
internal static class CalibrationDeleteResaveSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    [STAThread]static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"start\tQA\r\n");
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t180m\r\n");
            var raw=Enumerable.Range(0,8).Select(i=>{string p=Path.Combine(root,"raw-"+i+".bin");File.WriteAllBytes(p,new byte[]{1,2,3,(byte)i});return p;}).ToArray();
            var wave=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))).ToArray();
            var windows=Enumerable.Range(0,8).Select(i=>(double[])wave.Clone()).ToArray();
            var quality=HVX.OWTS.Task38.CalibrationIncidentQuality.Analyze(windows,200,336,465,100,.2,false,"offline",null);
            var capture=new CalibrationCaptureResult {Average=wave,Windows=windows,SampleRateHz=100e6,Assessment=quality,RawPaths=raw,
                DeviceSerial="OFFLINE-FIXTURE",DeviceModel="synthetic",AcquisitionMode="Pulse",RangeIndex=4,
                Reflection=new TdrCorrelationResult{Valid=false,IncidentIndex=400,ReflectionIndex=-1,Message="未检出可靠反射。"}};
            string file=LiveCalibrationRecord.RecordPath(root,"ALL","100pC");
            using(var page=new CalibrationProgramControl()) {
                page.ShowReflectionSelectionNoticeForTests=_=>{};
                page.ConfirmQualityWarningSaveForTests=_=>true;
                page.ConfirmDeleteLiveCalibrationForTests=()=>true;
                page.ProjectPath=root;page.SelectedLevelCode="100pC";Call(page,"SetPhaseParameters",-1,180d,180d,false,false);
                Call(page,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);Call(page,"ReceiveLiveCapture",-1,"100pC",capture);
                Check((bool)Call(page,"SaveLiveCalibration",-1,"100pC"),"initial synthetic coefficient saves");
                var before=LiveCalibrationRecord.Load(root,file,true);
                capture.RawPaths=new string[0];
                Check((bool)Call(page,"DeleteSelectedLiveCalibration"),"withdrawal succeeds");
                string backup=capture.WithdrawnRecordPath;
                Check(!File.Exists(file)&&capture.SavedRecordPath==null&&!page.SelectedCalibrationSaved,"withdrawal clears invalid formal receipt");
                Check(File.Exists(backup)&&LiveCalibrationRecord.HashFile(backup)==capture.WithdrawnRecordSha256,"recoverable backup retains verified source");
                Check(Object.ReferenceEquals(capture.Average,wave)&&capture.Assessment.CoefficientPcPerV==quality.CoefficientPcPerV,"withdrawal keeps valid waveform and coefficient");
                Check((bool)Call(page,"SaveLiveCalibration",-1,"100pC"),"explicit re-save uses verified self-contained backup without raw cache");
                var after=LiveCalibrationRecord.Load(root,file,true);
                Check(page.SelectedCalibrationSaved&&capture.SavedRecordPath==file&&capture.WithdrawnRecordPath==null,"successful re-save adopts new formal receipt");
                Check(before.CoefficientPcPerV==after.CoefficientPcPerV&&before.Average.SequenceEqual(after.Average),"re-save preserves exact coefficient and waveform");
                Check(File.Exists(backup),"withdrawn backup remains recoverable");
                Check((bool)Call(page,"DeleteSelectedLiveCalibration"),"repeat withdrawal succeeds");
                string second=capture.WithdrawnRecordPath;File.SetAttributes(second,FileAttributes.Normal);File.AppendAllText(second,"tampered");
                Check(!(bool)Call(page,"SaveLiveCalibration",-1,"100pC")&&!File.Exists(file),"tampered backup cannot become formal calibration");
            }
            Check((IntPtr)typeof(Art).GetField("module",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==IntPtr.Zero,"ART SDK never loaded");
            Console.WriteLine("CALIBRATION_DELETE_RESAVE_PASS="+checks+"; SDK_CALLS=0; PRODUCT_STARTED=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
