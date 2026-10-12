using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using HVX.Controls;
using Xiongxin.Calibration;
using Xiongxin.OWTS.Desktop;

// Offline synthetic projects only. No product Main, SDK, actual worker or device.
internal static class CalibrationRecordLifecycleR50Smoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Reject(Action action,string why){try{action();}catch(InvalidDataException){Check(true,why);return;}throw new Exception(why);}
    static string MakeProject(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"三相单芯电缆\"\r\n");
        string geometry=String.Join("",new[]{"A","B","C"}.Select(p=>"0\t"+p+"\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t180m\r\n"));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),geometry);return root;
    }
    static LiveCalibrationRecord CreateRecord(string root,int version,int rawCount)
    {
        var wave=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))).ToArray();
        var windows=Enumerable.Range(0,4).Select(_=>(double[])wave.Clone()).ToArray();
        var measured=HVX.OWTS.Task38.CalibrationIncidentQuality.Analyze(windows,200,336,465,5000,.2,false,"fake",null);
        var raw=Enumerable.Range(0,rawCount).Select(i=>{
            string name="source-"+i+".bin",path=Path.Combine(root,name);File.WriteAllBytes(path,new byte[]{1,2,3,(byte)i});
            return new CalibrationRawReference{RelativePath=name,Sha256=LiveCalibrationRecord.HashFile(path)};
        }).ToArray();
        return new LiveCalibrationRecord{Version=version,Phase="ALL",Level="5nC",MeasurementKind="Cable",DeviceSerial="QA000/0001",DeviceModel="FAKE",
            FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,ProjectHash=LiveCalibrationRecord.Identity(root),CreatedUtc=DateTime.UtcNow.ToString("o"),
            LengthM=180,VelocityMPerUs=180,SampleRateHz=100e6,IncidentSeconds=4e-6,IncidentPeakIndex=400,ReflectionSeconds=Double.NaN,ReflectionPeakIndex=-1,
            ReflectionStatus="NotDetected",VelocitySource="Configured",ReflectionNotice=LiveCalibrationRecord.UnconfirmedReflectionNotice,
            CoefficientPcPerV=measured.CoefficientPcPerV,ChargePc=5000,SnrDb=measured.SnrDb,Correlation=Double.NaN,AttenuationDb=Double.NaN,
            CoefficientProcessing=version==1?"":"HP10K_NO_LP",CoefficientIncidentPeakIndex=400,
            RangeIndex=4,PulseCount=4,AcquisitionMode="Segment",Average=wave,RawFiles=raw,
            EffectiveWindows=version==2?windows:null};
    }
    static CalibrationProgramControl Open(string root)
    {
        var page=new CalibrationProgramControl();
        page.ShowReflectionSelectionNoticeForTests=_=>{};
        page.ConfirmQualityWarningSaveForTests=_=>true;
        page.ConfirmDeleteLiveCalibrationForTests=()=>true;
        page.ProjectPath=root;page.SelectedLevelCode="5nC";
        return page;
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            foreach(int version in new[]{1,2,3}) {
                string root=MakeProject(Path.Combine(qa,"version-"+version));
                int rawCount=version==1?3:version==2?1:2;
                var source=CreateRecord(root,version,rawCount);
                string formal=source.Save(root),legacy=Path.Combine(root,"Calibration","Live","ALL_5nC.hvcal");
                Directory.CreateDirectory(Path.GetDirectoryName(legacy));File.Move(formal,legacy);
                using(var page=Open(root)) {
                    Check(page.HasCalibrationData&&page.IsCalibrationSavedValid&&page.SelectedCalibrationSaved,"V"+version+" actual legacy-folder record loads as saved");
                    Check(page.GetLevelSaveState(-1,"5nC")=="Saved","loaded per-level state is saved, not pending");
                    Check(page.DisplayDecayStatus!="Ready","no reflection pair does not fabricate a damping curve, despite valid charge coefficient");
                    var reasons=new[]{"Unavailable","UnknownPacket","OppositePolarity","NonDecaying"}.Select(status=>(string)Call(page,"DisplayDecayExplanation",status)).ToArray();
                    Check(reasons.All(reason=>!String.IsNullOrWhiteSpace(reason))&&reasons.Distinct().Count()==4,"each unknown/nonphysical decay boundary has a specific visible explanation");
                    var capture=(CalibrationCaptureResult)((IDictionary)Get(page,"liveCaptures"))["-1:5nC"];
                    Check(capture.SavedRecordPath==legacy,"loaded V"+version+" receipt retains actual source path");
                    Check(capture.Assessment.CoefficientPcPerV==source.CoefficientPcPerV&&capture.RawPaths.Length==rawCount,"load preserves exact coefficient and actual source count");
                    string hash=LiveCalibrationRecord.HashFile(legacy);
                    if(version==1) {
                        Check((bool)Call(page,"SaveLiveCalibration",-1,"5nC"),"unchanged V1 explicit save is a safe no-op");
                        Check(LiveCalibrationRecord.HashFile(legacy)==hash&&capture.CoefficientProcessing=="","no-op does not invent HP-only historical coefficient provenance");
                    }
                    var selected=PressureCalibrationSelection.Find(root,"B",false,"QA000/0001","ART-FAKE");
                    Check(selected!=null&&!selected.IsLegacy&&selected.Source==legacy&&selected.CoefficientPcPerV==source.CoefficientPcPerV,"pressure finds old-folder formal coefficient for equal-length phase");
                    selected.Revalidate();Check(true,"pressure validates actual archive plus applicable raw references");
                    Check((bool)Call(page,"DeleteSelectedLiveCalibration"),"V"+version+" delete withdraws actual loaded path");
                    Check(!File.Exists(legacy)&&capture.SavedRecordPath==null&&!page.SelectedCalibrationSaved,"withdrawn record never masquerades as still formally saved");
                    string backup=capture.WithdrawnRecordPath;
                    Check(File.Exists(backup)&&LiveCalibrationRecord.HashFile(backup)==capture.WithdrawnRecordSha256,"withdrawn original is recoverable and hash-bound");
                    if(version==1) {
                        Call(page,"SetPhaseBandwidth",-1,true,7d);
                        Check(!(bool)Call(page,"SaveLiveCalibration",-1,"5nC")&&!File.Exists(formal),"edited historical unknown-processing record cannot be relabeled a fresh calibration");
                        Call(page,"SetPhaseBandwidth",-1,false,60d);
                    }
                    Check((bool)Call(page,"SaveLiveCalibration",-1,"5nC"),"V"+version+" unchanged withdrawal can explicitly re-save");
                    var restored=LiveCalibrationRecord.Load(root,formal,true);
                    Check(restored.CoefficientPcPerV==source.CoefficientPcPerV&&restored.Average.SequenceEqual(source.Average),"restore retains exact coefficient and waveform");
                    Check(restored.Version==(version==1?1:3)&&restored.CoefficientProcessing==(version==1?"":"HP10K_NO_LP"),"restore retains honest historical vs current processing");
                    Check(restored.PulseCount==4&&restored.RawFiles.Length==rawCount,"pulse count is not fabricated to satisfy raw file count");
                    Check(page.IsCalibrationSavedValid&&page.SelectedCalibrationSaved&&capture.SavedRecordPath==formal,"restored receipt and UI become valid only after successful save");
                    Check(File.Exists(backup)&&capture.WithdrawnRecordPath==null,"recoverable backup remains after re-save");
                    Check((bool)Call(page,"DeleteSelectedLiveCalibration"),"second withdrawal works");
                    File.SetAttributes(capture.WithdrawnRecordPath,FileAttributes.Normal);File.AppendAllText(capture.WithdrawnRecordPath,"tampered");
                    Check(!(bool)Call(page,"SaveLiveCalibration",-1,"5nC")&&!File.Exists(formal),"changed backup cannot be restored");
                }
                Console.WriteLine("CALIBRATION_ARCHIVE_FIXTURE=version:"+version+",source_files:"+rawCount+",analyzed_synthetic_pulses:4,stored_average_samples:2048,sample_rate_hz:100000000; restore preserves coefficient; source bytes are synthetic hash fixtures, not device captures");
            }
            string negative=MakeProject(Path.Combine(qa,"negative"));
            var bad=CreateRecord(negative,3,1);bad.PulseCount=2;
            Reject(()=>bad.Validate(negative,true),"few pulses still require explicit quality acknowledgement despite one real source");
            bad.PulseCount=4;bad.RawFiles=new CalibrationRawReference[0];
            Reject(()=>bad.Validate(negative,true),"missing actual source evidence still rejected");
            var v1=CreateRecord(negative,1,1);Reject(()=>v1.Validate(negative,true),"V1 original raw-reference contract remains strict");
            Check((IntPtr)typeof(Art).GetField("module",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==IntPtr.Zero,"ART SDK never loaded");
            Console.WriteLine("CALIBRATION_RECORD_LIFECYCLE_R50_PASS="+checks+"; SDK_CALLS=0; PRODUCT_STARTED=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
