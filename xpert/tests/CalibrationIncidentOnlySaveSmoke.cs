using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Actual save/load APIs driven only by synthetic pulse and raw-file fixtures.
internal static class CalibrationIncidentOnlySaveSmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Assembly module;
    static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object value){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,value,null);else o.GetType().GetField(n,B).SetValue(o,value);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object New(string name){return Activator.CreateInstance(module.GetType(name,true),true);}
    static string RecordPath(string project){return (string)module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true).GetMethod("RecordPath").Invoke(null,new object[]{project,"ALL","100pC"});}
    static bool Rejects(Action action){try{action();return false;}catch(TargetInvocationException e){return e.InnerException is InvalidDataException;}catch(InvalidDataException){return true;}}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"incident-only");Directory.CreateDirectory(qa);
            File.WriteAllText(Path.Combine(qa,"ITEM.in"),"start\tQA\r\n");File.WriteAllText(Path.Combine(qa,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t180m\r\n");
            string[] raw=Enumerable.Range(0,8).Select(i=>{string path=Path.Combine(qa,"raw-"+i+".bin");File.WriteAllBytes(path,new byte[]{1,2,3,(byte)i});return path;}).ToArray();
            module=Assembly.LoadFrom(Path.Combine(root,"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            double[] wave=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))).ToArray();
            var windows=Enumerable.Range(0,8).Select(i=>(double[])wave.Clone()).ToArray();
            object quality=module.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",true).GetMethod("Analyze").Invoke(null,new object[]{windows,200,336,465,100d,.2,false,"offline",null});
            Check((bool)Get(quality,"SaveRecommended")&&(double)Get(quality,"SnrDb")>40,"synthetic incident quality passes independently of missing reflection");
            object capture=New("Xiongxin.Calibration.CalibrationCaptureResult");
            Set(capture,"Average",wave);Set(capture,"Windows",windows);Set(capture,"SampleRateHz",100e6);Set(capture,"Assessment",quality);Set(capture,"RawPaths",raw);
            Set(capture,"DeviceSerial","OFFLINE-FIXTURE");Set(capture,"DeviceModel","synthetic");Set(capture,"AcquisitionMode","Pulse");Set(capture,"RangeIndex",4);
            object reflection=New("Xiongxin.Calibration.TdrCorrelationResult");Set(reflection,"Valid",false);Set(reflection,"IncidentIndex",400);Set(reflection,"ReflectionIndex",-1);Set(reflection,"Message","未检出可靠反射。");Set(capture,"Reflection",reflection);
            string file=RecordPath(qa);object record=null;int warnings=0,notices=0;
            Check(file==Path.Combine(qa,"ALL_100pC.cal"),"new formal record uses project-root .cal storage");
            using(var control=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(control,"ShowReflectionSelectionNoticeForTests",(Action<string>)(message=>{notices++;Check(message.Contains("拖")&&message.Contains("保存"),"notice describes manual selection and optional coefficient save");}));
                Set(control,"ProjectPath",qa);Set(control,"SelectedLevelCode","100pC");Call(control,"SetPhaseParameters",-1,180d,180d,false,false);
                Call(control,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);Call(control,"ReceiveLiveCapture",-1,"100pC",capture);
                object phase=Call(control,"GetPhaseStateNoLock",-1),analysis=((IDictionary)Get(phase,"Analyses"))["100pC"];
                Check((int)Get(analysis,"ReflectionIndex")==-1&&!((IDictionary)Get(phase,"Markers")).Contains("100pC"),"no reflection pair fabricated before saving");
                Check(!File.Exists(file),"missing-reflection capture never auto-saves");
                Check(notices==1,"new incident-only result prompts once");
                Call(control,"ReceiveLiveCapture",-1,"100pC",capture);Call(control,"ResetView");
                Check(notices==1,"duplicate callback and view refresh never prompt again");
                Set(control,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(message=>{warnings++;return false;}));
                bool saved=(bool)Call(control,"SaveLiveCalibration",-1,"100pC");
                Check(saved,"valid incident-only coefficient saves without repeated reflection confirmation: "+Get(control,"LastCalibrationError"));
                Check(warnings==0&&notices==1&&File.Exists(file),"manual save never re-prompts solely for missing reflection");
                record=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("Load").Invoke(null,new object[]{qa,file,true});
                Check((string)Get(record,"ReflectionStatus")=="NotDetected"&&!(bool)Get(record,"HasMeasuredReflection"),"archive explicitly retains unconfirmed reflection");
                Check((string)Get(record,"VelocitySource")=="Configured"&&(double)Get(record,"VelocityMPerUs")==180d,"configured propagation speed not mislabeled measured");
                foreach(string n in new[]{"ReflectionSeconds","AttenuationDb","Correlation"})Check(Double.IsNaN((double)Get(record,n)),"unknown "+n+" stays unknown");
                Check((int)Get(record,"ReflectionPeakIndex")==-1&&!(bool)Get(record,"QualityWarningAcknowledged")&&(string)Get(record,"QualityWarning")=="","no fake reflection index or quality acknowledgement");
                Check(((string)Get(record,"ReflectionNotice")).Contains("待确认"),"unconfirmed reflection is independently recorded");
                Check(Math.Abs((double)Get(record,"CoefficientPcPerV")-100/.15)<1,"coefficient uses measured incident amplitude");
                double hpCoefficient=(double)Get(record,"CoefficientPcPerV");
                Check((string)Get(record,"CoefficientProcessing")=="HP10K_NO_LP","new coefficient has explicit HP-only provenance");
                Call(control,"SetPhaseBandwidth",-1,true,7d);
                Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"localization lowpass does not prevent valid HP-only coefficient save");
                record=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("Load").Invoke(null,new object[]{qa,file,true});
                Check(Math.Abs((double)Get(record,"CoefficientPcPerV")-hpCoefficient)<1e-10,"7 MHz localization filter leaves exact charge coefficient unchanged");
                Call(control,"SetPhaseBandwidth",-1,false,60d);
                Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"switching localization filter off preserves charge calibration");
                Check((bool)Get(control,"SelectedCalibrationSaved")&&(bool)Get(control,"IsCalibrationSavedValid"),"UI sees valid saved coefficient");
                Check(!((IDictionary)Get(phase,"Markers")).Contains("100pC")&&Double.IsNaN((double)Call(control,"GetMeasuredVelocityMPerUs",-1,"100pC")),"save never promotes configured velocity into measurement");
                Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC")&&warnings==0&&notices==1,"repeat save still requires no reflection confirmation");
                string archiveHash=(string)module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("HashFile").Invoke(null,new object[]{file});
                Set(capture,"QualityOverrideForReview",true);
                Check(!(bool)Call(control,"SaveLiveCalibration",-1,"100pC")&&warnings==1,"operator can still cancel a genuine low-quality override");
                Check((string)module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("HashFile").Invoke(null,new object[]{file})==archiveHash,"cancel preserves saved record");
                Set(control,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(message=>{warnings++;return true;}));
                Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"genuine quality warning still supports explicit acceptance");Set(capture,"QualityOverrideForReview",false);
                Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"clean current quality replaces old override only after explicit save");
                string serial=(string)Get(capture,"DeviceSerial");Set(capture,"DeviceSerial","");Check(!(bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"missing device identity still blocks");Set(capture,"DeviceSerial",serial);
                Check((string)Get(capture,"SavedRecordPath")==file,"fresh V2 save immediately adopts formal source");
                Set(capture,"RawPaths",new string[0]);Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"same page may save again using verified formal source without raw references");
                Set(capture,"SavedRecordPath",null);Check(!(bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"unsaved source missing raw evidence still blocks");Set(capture,"RawPaths",raw);Set(capture,"SavedRecordPath",file);
                byte[] intact=File.ReadAllBytes(file);File.SetAttributes(file,FileAttributes.Normal);File.AppendAllText(file,"changed");
                Check(!(bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"changed formal source cannot fall back to cached raw files");File.WriteAllBytes(file,intact);
                analysis=((IDictionary)Get(phase,"Analyses"))["100pC"];
                double magnitude=(double)Get(analysis,"IncidentMagnitude");Set(analysis,"IncidentMagnitude",double.NaN);Check(!(bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"no finite coefficient still blocks");Set(analysis,"IncidentMagnitude",magnitude);
            }
            using(var reloaded=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(reloaded,"ShowReflectionSelectionNoticeForTests",(Action<string>)(message=>{throw new Exception("reload must not prompt");}));
                Set(reloaded,"ProjectPath",qa);Set(reloaded,"SelectedLevelCode","100pC");
                Check((bool)Get(reloaded,"IsCalibrationSavedValid"),"incident-only archive reload remains valid: "+Get(reloaded,"LastCalibrationError"));
                object phase=Call(reloaded,"GetPhaseStateNoLock",-1),analysis=((IDictionary)Get(phase,"Analyses"))["100pC"];
                Check((int)Get(analysis,"ReflectionIndex")==-1&&!((IDictionary)Get(phase,"Markers")).Contains("100pC"),"reload has no fabricated pair");
                string readiness=(string)Call(reloaded,"GetCalibrationReadiness","A",false,false);
                Check(readiness==String.Empty,"saved incident coefficient is usable for equal-length phase sharing: "+readiness);
                Set(reloaded,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(message=>{throw new Exception("reflection-only reload must not ask quality confirmation");}));Check((bool)Call(reloaded,"SaveLiveCalibration",-1,"100pC"),"unchanged loaded incident-only record can be saved without pulse cache or extra confirmation");
            }
            string independent=Path.Combine(qa,"cache-free-project");Directory.CreateDirectory(independent);
            File.Copy(Path.Combine(qa,"ITEM.in"),Path.Combine(independent,"ITEM.in"));File.Copy(Path.Combine(qa,"CABLE.in"),Path.Combine(independent,"CABLE.in"));
            string independentFile=RecordPath(independent);File.Copy(file,independentFile);
            using(var independentUi=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(independentUi,"ShowReflectionSelectionNoticeForTests",(Action<string>)(_=>{throw new Exception("independent reload prompt");}));
                Set(independentUi,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(_=>{throw new Exception("independent save quality changed");}));
                Set(independentUi,"ProjectPath",independent);Set(independentUi,"SelectedLevelCode","100pC");
                Check((bool)Get(independentUi,"IsCalibrationSavedValid"),"formal record alone reloads without Acquisition or raw files");
                Check((bool)Call(independentUi,"SaveLiveCalibration",-1,"100pC"),"loaded effective windows can be saved without raw cache");
                object independentRecord=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("Load").Invoke(null,new object[]{independent,independentFile,true});
                Check((int)Get(independentRecord,"Version")==3&&Get(independentRecord,"EffectiveWindows")==null&&(int)Get(independentRecord,"PulseCount")==windows.Length,"average-only save retains actual count without pulse copies");
                Check((double)Get(independentRecord,"CoefficientPcPerV")== (double)Get(record,"CoefficientPcPerV"),"independent recalculation preserves exact coefficient");
            }
            foreach(string consumer in new[]{"DataBrowser","ReportGenerator"}) {
                var consumerAssembly=Assembly.LoadFrom(Path.Combine(root,"modules",consumer,"Xiongxin.OWTS."+consumer+".dll"));
                Type reader=consumerAssembly.GetType("HVX.Controls.BrowserReader",true);
                var load=reader.GetMethods(B).Single(m=>m.Name=="LoadProject"&&m.GetParameters().Length==1);
                object project=load.Invoke(null,new object[]{independent});
                var records=((IEnumerable)Get(project,"Records")).Cast<object>().Where(r=>(string)Get(r,"Kind")=="cal").ToArray();
                Check(records.Length==1&&(bool)Get(records[0],"IsValid"),consumer+" reads independent calibration metadata");
                var readWave=reader.GetMethods(B).Single(m=>m.Name=="LoadWaveform"&&m.GetParameters().Length==1);
                Check(readWave.Invoke(null,new[]{records[0]})!=null,consumer+" reads independent calibration waveform");
            }
            Check(!Rejects(()=>Call(record,"Validate",qa,true)),"explicit unknown reflection needs no invented quality acknowledgement");
            Set(record,"SnrDb",10d);Check(Rejects(()=>Call(record,"Validate",qa,true)),"genuinely low SNR still requires explicit warning");Set(record,"SnrDb",(double)Get(quality,"SnrDb"));
            Set(record,"QualityWarningAcknowledged",true);Set(record,"QualityWarning",module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetField("UnconfirmedReflectionNotice").GetValue(null));
            Check(!(bool)Get(record,"HasIncidentQualityWarning"),"exact legacy reflection-only warning does not become an incident quality failure");
            Call(record,"Save",qa);
            using(var oldReload=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(oldReload,"ShowReflectionSelectionNoticeForTests",(Action<string>)(message=>{throw new Exception("legacy reload prompt");}));
                Set(oldReload,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(message=>{throw new Exception("legacy reflection-only quality prompt");}));
                Set(oldReload,"ProjectPath",qa);Set(oldReload,"SelectedLevelCode","100pC");
                Check((bool)Call(oldReload,"SaveLiveCalibration",-1,"100pC"),"legacy reflection-only archive reload and explicit save do not repeat confirmation");
            }
            Set(record,"QualityWarning",(string)Get(record,"QualityWarning")+"\r\n低信噪比");Check((bool)Get(record,"HasIncidentQualityWarning"),"legacy mixed quality warning remains actionable");
            Set(record,"QualityWarningAcknowledged",false);Set(record,"QualityWarning","");
            Set(record,"ReflectionSeconds",6e-6);Check(Rejects(()=>Call(record,"Validate",qa,true)),"unknown record cannot contain invented finite reflection");Set(record,"ReflectionSeconds",double.NaN);
            Set(record,"ReflectionStatus","Unspecified");Check(Rejects(()=>Call(record,"Validate",qa,true)),"old unmarked records retain strict paired validation");Set(record,"ReflectionStatus","NotDetected");
            Set(record,"CoefficientPcPerV",double.NaN);Check(Rejects(()=>Call(record,"Validate",qa,true)),"unknown coefficient never admitted");
            string log=String.Join("",Directory.GetFiles(Path.Combine(qa,"Calibration","Logs"),"*.xml.log").Select(File.ReadAllText));
            Check(log.Contains("CalibrationQualityWarning")&&log.Contains("Accepted")&&log.Contains("Cancelled")&&log.Contains("NotSaved"),"project logs preserve warnings, decisions and failures");
            foreach(int count in new[]{1,2,257}) {
                string segmentRoot=Path.Combine(qa,"segment"+count);Directory.CreateDirectory(segmentRoot);
                File.Copy(Path.Combine(qa,"ITEM.in"),Path.Combine(segmentRoot,"ITEM.in"));File.Copy(Path.Combine(qa,"CABLE.in"),Path.Combine(segmentRoot,"CABLE.in"));
                string[] segmentRaw=raw.Take(3).Select((p,i)=>{string dst=Path.Combine(segmentRoot,"raw"+i+".bin");File.Copy(p,dst);return dst;}).ToArray();
                var segmentWindows=Enumerable.Range(0,count).Select(i=>(double[])wave.Clone()).ToArray();
                object segmentQuality=module.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",true).GetMethod("Analyze").Invoke(null,new object[]{segmentWindows,200,336,465,100d,.2,false,"offline-segment",null});
                object segmentCapture=New("Xiongxin.Calibration.CalibrationCaptureResult");
                foreach(string n in new[]{"DeviceSerial","DeviceModel","RangeIndex","SampleRateHz","Reflection"})Set(segmentCapture,n,Get(capture,n));
                Set(segmentCapture,"Average",wave);Set(segmentCapture,"Windows",segmentWindows);Set(segmentCapture,"Assessment",segmentQuality);Set(segmentCapture,"RawPaths",segmentRaw);Set(segmentCapture,"AcquisitionMode","Segment");
                int asked=0;using(var control=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                    Set(control,"ShowReflectionSelectionNoticeForTests",(Action<string>)(_=>{}));Set(control,"ProjectPath",segmentRoot);Set(control,"SelectedLevelCode","100pC");Call(control,"SetPhaseParameters",-1,180d,180d,false,false);
                    Call(control,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);Call(control,"ReceiveLiveCapture",-1,"100pC",segmentCapture);
                    Set(control,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(message=>{asked++;Check(count<3&&message.Contains("脉冲"),"only real few-pulse uncertainty needs acknowledgement");return true;}));
                    Check((bool)Call(control,"SaveLiveCalibration",-1,"100pC"),"actual segment windows save after applicable quality acknowledgement: "+Get(control,"LastCalibrationError"));
                    string segmentFile=RecordPath(segmentRoot);
                    var savedSegment=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("Load").Invoke(null,new object[]{segmentRoot,segmentFile,true});
                    Check((int)Get(savedSegment,"PulseCount")==count&&(string)Get(savedSegment,"AcquisitionMode")=="Segment","archive retains all actual segment pulse count");
                    Check((bool)Get(savedSegment,"QualityWarningAcknowledged")==(count<3)&&asked==(count<3?1:0),"quality acknowledgement is genuine, not manufactured due to absent reflection or N");
                    Check(((double[])Get(savedSegment,"Average")).SequenceEqual(wave),"segment save preserves mapped averaged waveform samples");
                }
            }
            Console.WriteLine("CALIBRATION_INCIDENT_ONLY_SAVE_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
