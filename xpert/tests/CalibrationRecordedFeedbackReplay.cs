using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// Explicit read-only replay of the user's reported capture. All generated
// archives live in QA and carry OFFLINE-REPLAY identity, never in the real project.
static class CalibrationRecordedFeedbackReplay
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Static(string t,string n,params object[] a){return module.GetType(t,true).GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(null,a);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object New(string t){return Activator.CreateInstance(module.GetType(t,true),true);}
    static string Hash(string p){using(var sha=SHA256.Create())using(var s=File.OpenRead(p))return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","");}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            module=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            string source=@"D:\OWTSData\11-22\2025年07月08日16时16分\Calibration\Acquisition\20261006_115214_34579d";
            string[] files=Directory.GetFiles(source,"capture_*.xxs").OrderBy(p=>p).ToArray();Check(files.Length==16,"reported 5nC capture contains 16 blocks");
            string[] hashes=files.Select(Hash).ToArray();var blocks=new List<double[]>();object info=null;double rate=0;
            foreach(string file in files) {
                object capture=Static("Xiongxin.Scope.Wire","Load",file);info=Get(capture,"Info");
                rate=1e9/(double)Get(info,"IntervalNs");Check((short)Get(info,"Overflow")==0,"raw capture not clipped");
                double[] volts=(double[])Static("Xiongxin.Calibration.AcquisitionPlanning","Volts",capture);
                blocks.Add((double[])Static("HVX.SignalProcessing.PdLinearHighPass","Apply",volts,rate));
            }
            object settings=New("HVX.OWTS.Task38.CalibrationWindowSettings");
            Set(settings,"SampleRateHz",rate);Set(settings,"CableLengthM",172.04);Set(settings,"VelocityMPerUs",170d);
            Set(settings,"ThresholdV",.39840356730379678);Set(settings,"FullScaleV",5d);
            int guard=(int)Static("HVX.SignalProcessing.PdLinearHighPass","GuardSamples",rate);
            object extracted=Static("HVX.OWTS.Task38.CalibrationWindowing","FromFilteredTriggeredBlocks",blocks.ToArray(),settings,guard);
            extracted=Static("Xiongxin.Calibration.AcquisitionPlanning","RejectOutliers",extracted);
            double[] average=(double[])Get(extracted,"Average");double[][] windows=(double[][])Get(extracted,"Windows");
            var search=Static("Xiongxin.Calibration.CalibrationReflectionBandwidth","SearchTerminal",average,400,rate,172.04,170d,.15,false,null);
            object reflection=Get(search,"Reflection"),bandwidth=Get(search,"Bandwidth");double cutoff=(double)Get(bandwidth,"CutoffMHz");
            var qualityWindows=cutoff>0?windows.Select(w=>(double[])Static("HVX.SignalProcessing.PdLinearLowPass","Apply",w,1/rate,cutoff)).ToArray():windows;
            object quality=Static("HVX.OWTS.Task38.CalibrationIncidentQuality","Analyze",qualityWindows,200,336,464,5000d,5d,false,"offline-replay",null);
            Console.WriteLine("REPLAY: pulses="+windows.Length+" peakV="+Get(quality,"IncidentPeakV")+" qualitySave="+Get(quality,"SaveRecommended")+
                " reflection="+Get(reflection,"Valid")+" distanceM="+Get(reflection,"DistanceM")+" correlation="+Get(reflection,"Correlation")+" cutoffMHz="+cutoff+" ratio="+Get(bandwidth,"Ratio"));
            Check((bool)Get(quality,"SaveRecommended"),"reported waveform passes acquisition-quality gates without invalid external reference");
            Check((bool)Get(reflection,"Valid")&&(bool)Get(bandwidth,"Sufficient"),"recorded terminal candidate retains correlation and amplitude evidence");
            string project=Path.Combine(args[1],"OFFLINE-REPLAY-PROJECT");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"172.04\"\r\n",Encoding.GetEncoding(936));
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t末端\t终端\t172.04m\r\n",Encoding.GetEncoding(936));
            var copies=new List<string>();foreach(string file in files){string p=Path.Combine(project,Path.GetFileName(file));File.Copy(file,p);copies.Add(p);}
            using(var c=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                // Simulate an explicit operator acknowledgement if the real
                // replay exposes a quality advisory; never open a modal test UI.
                Set(c,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(_=>true));
                Set(c,"ProjectPath",project);Set(c,"EmbeddedMode",true);Set(c,"SelectedLevelCode","5nC");Set(c,"UseFirstArrivalTiming",true);c.Size=new Size(1500,950);c.CreateControl();
                Call(c,"SetPhaseParameters",-1,172.04,170d,false,false);Call(c,"SetPhaseWaveform",-1,"5nC",average,0d,1/rate);
                if(cutoff>0)Call(c,"SetPhaseBandwidth",-1,true,cutoff);
                object phase=Call(c,"GetPhaseStateNoLock",-1);object wave=((IDictionary)Get(phase,"Waveforms"))["5nC"];
                object active=Call(c,"ActiveWaveformNoLock",phase,"5nC",wave);
                object a=Call(c,"BuildManualResult",active,phase,Get(reflection,"IncidentIndex"),Get(reflection,"ReflectionIndex"),null);
                Call(c,"StoreAnalysisNoLock",phase,"5nC",active,a);
                double arrivalLength=(double)Get(((IDictionary)Get(phase,"Markers"))["5nC"],"DistanceMeters");
                Console.WriteLine("TERMINAL_ARRIVAL_LENGTH="+arrivalLength);
                Check(arrivalLength>165&&arrivalLength<180,"first arrival belongs to terminal packet, not interior 145m oscillation");
                var capture=New("Xiongxin.Calibration.CalibrationCaptureResult");
                Set(capture,"Average",average);Set(capture,"Windows",windows);Set(capture,"SampleRateHz",rate);Set(capture,"Assessment",quality);
                Set(capture,"Reflection",reflection);Set(capture,"Bandwidth",bandwidth);Set(capture,"RawPaths",copies.ToArray());
                Set(capture,"DeviceSerial","OFFLINE-REPLAY-NOT-HARDWARE");Set(capture,"DeviceModel","TEST");Set(capture,"AcquisitionMode","Replay");
                Set(capture,"PicoCoupling",Get(Get(Get(info,"Config"),"A"),"Coupling"));Set(capture,"DcRemoval","full-record-adc-mean-v1");
                Set(capture,"RangeIndex",Get(Get(Get(info,"Config"),"A"),"Range"));
                ((IDictionary)Get(c,"liveCaptures"))["-1:5nC"]=capture;((IDictionary)Get(c,"liveWaveSources"))["-1:5nC"]=wave;
                bool saved=(bool)Call(c,"SaveLiveCalibration",-1,"5nC");
                Console.WriteLine("ISOLATED_SAVE="+saved+"; ERROR="+Get(c,"lastCalibrationError"));
                Check(saved,"real captured waveform can save through actual UI path in isolated project");
                string path=(string)Static("HVX.CalibrationStorage.LiveCalibrationRecord","RecordPath",project,"ALL","5nC");
                Check(path==Path.Combine(project,"ALL_5nC.cal"),"New replay archive uses the actual project-root .cal contract");
                var record=Static("HVX.CalibrationStorage.LiveCalibrationRecord","Load",project,path,true);
                Check((string)Get(record,"CircuitReferenceStatus")=="NotChecked","formal record explicitly records skipped reference");
                Check((int)Get(record,"PicoCoupling")== (int)Get(Get(Get(info,"Config"),"A"),"Coupling")&&(string)Get(record,"DcRemoval")=="full-record-adc-mean-v1","replay retains historical hardware coupling and new software preprocessing separately");
                string archiveHash=Hash(path);
                Set(capture,"QualityOverrideForReview",true);
                int declinedWarnings=0;
                Set(c,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(warning=>{
                    declinedWarnings++;Check(warning.Contains("低质量脉冲"),"actual low-quality override advisory is retained");return false;
                }));
                Check(!(bool)Call(c,"SaveLiveCalibration",-1,"5nC"),"declining the actual quality warning prevents formal-save replacement");
                Check(declinedWarnings==1,"review-only replacement requires an explicit quality decision");
                Check(Hash(path)==archiveHash,"rejected save never overwrites existing coefficient");
                var audit=new System.Xml.XmlDocument{XmlResolver=null};
                audit.LoadXml("<events>"+String.Join("",Directory.GetFiles(Path.Combine(project,"Calibration","Logs"),"*.xml.log").Select(File.ReadAllText))+"</events>");
                Check(audit.SelectNodes("/events/event[@action='CalibrationSave'][@outcome='Requested']").Count==2,"both save attempts recorded");
                Check(audit.SelectSingleNode("/events/event[@outcome='Saved']/detail").InnerText.Contains("CircuitReferenceStatus=NotChecked"),"successful save records reference status");
                Check(audit.SelectSingleNode("/events/event[@action='CalibrationQualityWarning'][@outcome='Cancelled']/detail").InnerText.Contains("低质量脉冲"),"declined save retains actual quality reason and cancellation audit");
                Set(capture,"QualityOverrideForReview",false);
                Console.WriteLine("SAVED_REPLAY_COEFFICIENT="+Get(record,"CoefficientPcPerV")+"; ARRIVAL_LENGTH="+Get(((IDictionary)Get(phase,"Markers"))["5nC"],"DistanceMeters"));
                Call(c,"SavePng",Path.Combine(args[1],"recorded-5nC-replay.png"),1500,950);
            }
            Check(files.Select(Hash).SequenceEqual(hashes),"all original capture bytes remain unchanged");
            File.WriteAllLines(Path.Combine(args[1],"read-only-input-sha256.txt"),files.Select((f,i)=>Path.GetFileName(f)+" "+hashes[i]));
            Console.WriteLine("RECORDED_FEEDBACK_REPLAY_PASS="+checks+"; HARDWARE_COMMANDS=0; ORIGINAL_PROJECT_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
