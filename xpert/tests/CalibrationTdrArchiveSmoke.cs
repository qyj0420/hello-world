using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationTdrArchiveSmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object New(string name){return Activator.CreateInstance(module.GetType(name,true),true);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static string Project(string parent,string name)
    {
        string root=Path.Combine(parent,name);Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"start\tQA\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t180m\r\n");return root;
    }
    static Control Prepare(string root,out object capture,out double[] wave)
    {
        wave=Enumerable.Range(0,2048).Select(i=>.15*Math.Exp(-Math.Pow((i-400)/8d,2))+.05*Math.Exp(-Math.Pow((i-500)/8d,2))+.08*Math.Exp(-Math.Pow((i-600)/8d,2))).ToArray();
        double[] local=wave;var windows=Enumerable.Range(0,4).Select(_=>(double[])local.Clone()).ToArray();
        capture=New("Xiongxin.Calibration.CalibrationCaptureResult");
        Set(capture,"Average",wave);Set(capture,"Windows",windows);Set(capture,"SampleRateHz",100e6);Set(capture,"AcquisitionMode","Pulse");
        Set(capture,"DeviceSerial","OFFLINE-TDR");Set(capture,"DeviceModel","fixture");Set(capture,"RangeIndex",4);
        string raw=Path.Combine(root,"raw.bin");File.WriteAllBytes(raw,new byte[]{1,2,3});Set(capture,"RawPaths",new[]{raw});
        object reflection=New("Xiongxin.Calibration.TdrCorrelationResult");Set(reflection,"Valid",true);Set(reflection,"Correlation",.99);
        Set(reflection,"IncidentIndex",400);Set(reflection,"ReflectionIndex",600);Set(reflection,"Message","synthetic");Set(capture,"Reflection",reflection);
        var ui=(Control)New("HVX.Controls.CalibrationProgramControl");Set(ui,"ProjectPath",root);Set(ui,"SelectedLevelCode","TDR");
        Call(ui,"SetPhaseParameters",-1,180d,180d,false,false);Call(ui,"SetPhaseWaveform",-1,"TDR",wave,0d,1e-8);
        Call(ui,"ReceiveLiveCapture",-1,"TDR",capture);return ui;
    }
    static void SavedCharge(string root)
    {
        object record=New("HVX.CalibrationStorage.LiveCalibrationRecord");
        double[] wave=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))+.08*Math.Exp(-Math.Pow((i-600)/8d,2))).ToArray();
        Set(record,"Version",3);Set(record,"Phase","ALL");Set(record,"Level","5nC");Set(record,"DeviceSerial","OFFLINE-TDR");
        Set(record,"DeviceModel","fixture");Set(record,"RangeIndex",4);Set(record,"SampleRateHz",100e6);Set(record,"AcquisitionMode","Pulse");
        Set(record,"PulseCount",4);Set(record,"Average",wave);Set(record,"IncidentSeconds",4e-6);Set(record,"ReflectionSeconds",6e-6);
        Set(record,"IncidentPeakIndex",400);Set(record,"ReflectionPeakIndex",600);Set(record,"CoefficientIncidentPeakIndex",400);
        Set(record,"LengthM",180d);Set(record,"VelocityMPerUs",180d);Set(record,"ChargePc",5000d);Set(record,"CoefficientPcPerV",5000/.15);
        Set(record,"CoefficientProcessing","HP10K_NO_LP");Set(record,"FilterId",module.GetType("HVX.SignalProcessing.PdLinearHighPass",true).GetField("FilterId",B).GetValue(null));
        Set(record,"SnrDb",40d);Set(record,"Correlation",.99);Set(record,"AttenuationDb",20*Math.Log10(.15/.08));
        Set(record,"ReflectionStatus","Detected");Set(record,"VelocitySource","Measured");Set(record,"ReflectionSelectionMethod","Automatic");
        Set(record,"CreatedUtc",DateTime.UtcNow.ToString("o"));Set(record,"ProjectHash",module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true).GetMethod("Identity").Invoke(null,new object[]{root}));
        Type rawType=module.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);Array raw=Array.CreateInstance(rawType,3);
        for(int i=0;i<3;i++) {
            string path=Path.Combine(root,"charge-"+i+".bin");File.WriteAllBytes(path,new byte[]{1,2,(byte)i});
            object reference=Activator.CreateInstance(rawType);Set(reference,"RelativePath",Path.GetFileName(path));
            Set(reference,"Sha256",module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true).GetMethod("HashFile").Invoke(null,new object[]{path}));raw.SetValue(reference,i);
        }
        Set(record,"RawFiles",raw);Call(record,"Save",root);
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("Fresh isolated fixture directory required.");Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.Combine(build,"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            Type storage=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
            string mixed=Project(qa,"mixed");SavedCharge(mixed);
            object capture;double[] wave;
            using(var ui=Prepare(mixed,out capture,out wave)) {
                Check((string)Call(ui,"GetLevelSaveState",-1,"5nC")=="Saved","saved charge stays saved after optional ranging acquisition");
                Check(!(bool)Get(ui,"HasUnsavedChargeCalibrationChanges"),"optional unsaved TDR does not prompt on home navigation");
                Check((bool)Call(ui,"SetPhaseTimingCursors",-1,"TDR",4e-6,6e-6),"ranging cursor selection available independently of charge");
                Call(ui,"SetPhaseParameters",-1,275d,140d,false,false);
                Check((string)Call(ui,"GetLevelSaveState",-1,"5nC")=="Saved","TDR geometry edit cannot dirty charge calibration");
                Set(ui,"SelectedLevelCode","5nC");
                Check((double)Get(ui,"CableLengthMeters")==180d,"charge uses persisted project length instead of transient TDR length");
                Set(ui,"SelectedLevelCode","TDR");
                Check((double)Get(ui,"PropagationVelocityMPerUs")==140d,"TDR retains its own selected velocity");
                Check((bool)Call(ui,"SaveMeasuredCableLength"),"save length commits TDR without invalidating charge: "+Get(ui,"LastCalibrationError"));
                var joints=((IEnumerable)Get(Call(ui,"GetPhaseStateNoLock",-1),"Joints")).Cast<object>().ToArray();
                Check(joints.Any(j=>(bool)Get(j,"AutomaticallyDetected"))&&joints.Where(j=>(bool)Get(j,"AutomaticallyDetected")).All(j=>!(bool)Get(j,"Confirmed")),"high-score repeated joint packets remain unconfirmed candidates");
                Check(!File.ReadAllText(Path.Combine(mixed,"CABLE.in")).Contains("校准识别"),"automatic candidates do not publish formal project joints");
                Check((string)Call(ui,"GetLevelSaveState",-1,"5nC")=="Saved"&&!(bool)Get(ui,"HasUnsavedChargeCalibrationChanges"),"saving length neither dirties charge nor blocks navigation");
                Set(ui,"SelectedLevelCode","5nC");
                Check(Math.Abs((double)Get(ui,"CableLengthMeters")-140)<1,"charge follows length only after explicit save length");
            }
            using(var ui=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(ui,"ProjectPath",mixed);Set(ui,"SelectedLevelCode","5nC");
                Check((string)Call(ui,"GetLevelSaveState",-1,"5nC")=="Saved"&&(string)Call(ui,"GetLevelSaveState",-1,"TDR")=="Saved","TDR and charge independently reload as saved");
                Check(!(bool)Get(ui,"HasUnsavedChargeCalibrationChanges"),"saved reload is not made dirty by archive order");
                Check(Math.Abs((double)Get(ui,"CableLengthMeters")-140)<1,"TDR reload cannot replace current project length for charge");
            }
            string project=Project(qa,"tdr"),file=Path.Combine(project,"ALL_TDR.cal");
            using(var ui=Prepare(project,out capture,out wave)) {
                Check((bool)Call(ui,"SaveMeasuredCableLength"),"TDR geometry and formal waveform save: "+Get(ui,"LastCalibrationError"));
                Check(File.Exists(file),"formal TDR file exists");
                Check((bool)Get(ui,"SelectedCalibrationSaved"),"TDR marked saved only after archive publication");
                Check((string)Get(capture,"SavedRecordPath")==file,"fresh TDR adopts verified formal source");
                string rawFile=Path.Combine(project,"raw.bin"),held=Path.Combine(project,"raw.temporary-fixture");
                File.Move(rawFile,held);
                Check((bool)Call(ui,"SaveMeasuredCableLength"),"same-page TDR resave needs no raw cache: "+Get(ui,"LastCalibrationError"));
                File.Move(held,rawFile);
            }
            object record=storage.GetMethod("Load").Invoke(null,new object[]{project,file,true});
            Check((bool)Get(record,"IsTdr")&&(int)Get(record,"Version")==3,"explicit average-only TDR record kind");
            Check(Double.IsNaN((double)Get(record,"ChargePc"))&&Double.IsNaN((double)Get(record,"CoefficientPcPerV")),"TDR invents no charge calibration");
            Check((string)Get(record,"VelocitySource")=="Configured","ranging retains configured velocity provenance");
            Check(((double[])Get(record,"Average")).SequenceEqual(wave)&&Get(record,"EffectiveWindows")==null&&(int)Get(record,"PulseCount")==4&&(int)Get(record,"Version")==3,"TDR last average and actual count, no pulse copies");
            Check(storage.GetMethod("BestForProject").Invoke(null,new object[]{project,"A",(bool)Get(record,"FirstArrival"),"OFFLINE-TDR"})==null,"TDR excluded from charge calibration selection");
            File.Move(Path.Combine(project,"raw.bin"),Path.Combine(project,"raw.fixture-moved"));
            using(var reloaded=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(reloaded,"ProjectPath",project);Set(reloaded,"SelectedLevelCode","TDR");
                Check((bool)Get(reloaded,"SelectedCalibrationSaved"),"TDR reloaded without raw cache");
                Check((bool)Call(reloaded,"SaveMeasuredCableLength"),"reloaded TDR can resave without raw cache: "+Get(reloaded,"LastCalibrationError"));
            }
            Check(Directory.GetFiles(Path.GetDirectoryName(file),"*.cal").Length==1&&Directory.GetFiles(Path.GetDirectoryName(file),"ALL_TDR.cal*.bak").Length==0,"only latest TDR formal record retained");
            foreach(string consumer in new[]{"DataBrowser","ReportGenerator"}) {
                var assembly=Assembly.LoadFrom(Path.Combine(build,"modules",consumer,"Xiongxin.OWTS."+consumer+".dll"));
                Type reader=assembly.GetType("HVX.Controls.BrowserReader",true);
                object data=reader.GetMethods(B).Single(m=>m.Name=="LoadProject"&&m.GetParameters().Length==1).Invoke(null,new object[]{project});
                var records=((IEnumerable)Get(data,"Records")).Cast<object>().ToArray();
                Check(records.Length==1&&(bool)Get(records[0],"IsValid"),consumer+" lists valid TDR");
                object trace=reader.GetMethods(B).Single(m=>m.Name=="LoadWaveform"&&m.GetParameters().Length==1).Invoke(null,new[]{records[0]});
                Check(((double[])Get(trace,"Y")).SequenceEqual(wave),consumer+" reads TDR actual wave");
            }
            string invalid=Project(qa,"invalid");
            string manual=Project(qa,"manual");
            using(var ui=Prepare(manual,out capture,out wave)) {
                Set(Get(capture,"Reflection"),"Valid",false);
                string before=File.ReadAllText(Path.Combine(manual,"CABLE.in"));
                Check(!(bool)Call(ui,"SaveMeasuredCableLength"),"unconfirmed automatic failure cannot save TDR");
                Check(before==File.ReadAllText(Path.Combine(manual,"CABLE.in")),"unconfirmed reflection leaves geometry unchanged");
                object phase=Call(ui,"GetPhaseStateNoLock",-1);
                Call(ui,"RememberManualReflectionNoLock",phase,"TDR");
                Check((bool)Call(ui,"SaveMeasuredCableLength"),"confirmed manual reflection can save after automatic failure: "+Get(ui,"LastCalibrationError"));
                object saved=storage.GetMethod("Load").Invoke(null,new object[]{manual,Path.Combine(manual,"ALL_TDR.cal"),true});
                Check((string)Get(saved,"ReflectionSelectionMethod")=="Manual","manual TDR provenance retained");
                object analysis=((IDictionary)Get(phase,"Analyses"))["TDR"];
                Check((int)Get(saved,"IncidentPeakIndex")== (int)Get(analysis,"IncidentIndex")&&
                    (int)Get(saved,"ReflectionPeakIndex")== (int)Get(analysis,"ReflectionIndex"),"peak indices use actual analysis rather than arrival timing");
            }
            using(var ui=Prepare(invalid,out capture,out wave)) {
                string before=File.ReadAllText(Path.Combine(invalid,"CABLE.in"));Set(capture,"Windows",null);
                Check(!(bool)Call(ui,"SaveMeasuredCableLength"),"missing pulse evidence rejected");
                Check(before==File.ReadAllText(Path.Combine(invalid,"CABLE.in")),"invalid evidence does not modify geometry");
            }
            string partial=Project(qa,"partial");
            using(var ui=Prepare(partial,out capture,out wave)) {
                Directory.CreateDirectory(Path.Combine(partial,"ALL_TDR.cal"));
                Check(!(bool)Call(ui,"SaveMeasuredCableLength"),"archive publication failure not reported success");
                Check(((string)Get(ui,"LastCalibrationError")).Contains("电缆长度已保存"),"partial outcome explicit");
                Check(!(bool)Get(ui,"SelectedCalibrationSaved"),"partial save remains unsaved");
                Check(File.Exists(Path.Combine(partial,"raw.bin")),"partial save preserves source");
            }
            Console.WriteLine("CALIBRATION_TDR_ARCHIVE_PASS="+checks+"; HARDWARE_CALLS=0; USER_RECORD_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
