using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationQualityContractSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly module; static Type quality; static int checks;
    static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
    static object Get(object o,string name){var p=o.GetType().GetProperty(name,All);return p!=null?p.GetValue(o,null):o.GetType().GetField(name,All).GetValue(o);}
    static void Set(object o,string name,object value){var p=o.GetType().GetProperty(name,All);if(p!=null)p.SetValue(o,value,null);else o.GetType().GetField(name,All).SetValue(o,value);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static double[] Wave(double noise,double peak=.15)
    {var wave=new double[2048];for(int i=0;i<200;i++)wave[i]=(i%2==0?1:-1)*noise;for(int i=336;i<465;i++)wave[i]=peak*Math.Exp(-Math.Pow((i-400)/8d,2));return wave;}
    static object Analyze(double[] wave,double charge=100,double range=.2,bool clipped=false,bool reference=false)
    {return quality.GetMethod(reference?"AnalyzeWithCircuitReference":"Analyze").Invoke(null,new object[]{Enumerable.Range(0,8).Select(i=>(double[])wave.Clone()).ToArray(),200,336,465,charge,range,clipped,"isolated-fixture",null});}
    static object Capture(double[] wave,object assessment)
    {
        object result=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.CalibrationCaptureResult",true),true);
        Set(result,"Average",wave);Set(result,"Windows",Enumerable.Range(0,8).Select(i=>(double[])wave.Clone()).ToArray());
        Set(result,"Assessment",assessment);Set(result,"SampleRateHz",100e6);Set(result,"RangeIndex",4);Set(result,"RawPaths",new string[0]);
        return result;
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            if(args.Length!=2)throw new ArgumentException("BuildRoot QAroot");
            string qa=Path.Combine(Path.GetFullPath(args[1]),"calibration-quality-contract-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));quality=module.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",true);
            var valid=Analyze(Wave(.001));Check((bool)Get(valid,"SaveRecommended"),"normal finite SNR remains eligible");
            Check(Math.Abs((double)Get(valid,"SnrDb")-20*Math.Log10(150))<1e-9,"positive noise SNR unchanged");
            var below=Analyze(Wave(.02));Check(!(bool)Get(below,"SaveRecommended")&&((string)Get(below,"Message")).Contains("20 dB"),"unchanged 20dB gate");
            var at=Analyze(Wave(.015));double atSnr=(double)Get(at,"SnrDb");
            Check(Math.Abs(atSnr-20)<1e-10&&(bool)Get(at,"SaveRecommended")== (atSnr>=20),"near-20dB uses unchanged exact gate, no added tolerance");
            var flat=Wave(0);var original=(double[])flat.Clone();var unknown=Analyze(flat);
            Check(double.IsNaN((double)Get(unknown,"SnrDb")),"zero unresolved noise is unknown, not arbitrary finite cap/infinity");
            Check(!(bool)Get(unknown,"SaveRecommended")&&!(bool)Get(unknown,"CircuitSuspected"),"unknown SNR never approves save or diagnoses circuit");
            Check(((string)Get(unknown,"Quality")).Contains("未能估计")&&((string)Get(unknown,"Quality")).Contains("待复测"),"explicit GUI quality state");
            Check(flat.SequenceEqual(original),"quality analysis preserves original waveform");
            var high=Analyze(Wave(.001),100000,20,false,true);
            Check((double)Get(high,"ExpectedPeakV")==150,"reference values not silently redefined");
            Check(!(bool)Get(high,"SaveRecommended")&&!(bool)Get(high,"CircuitSuspected"),"out-of-range reference refuses save without falsely diagnosing circuit");
            Check(((string)Get(high,"Message")).Contains("适用范围"),"high-charge limitation is explicit");
            var mismatch=Analyze(Wave(.001,.03),100,.2,false,true);
            Check((bool)Get(mismatch,"CircuitSuspected")&&!(bool)Get(mismatch,"SaveRecommended"),"applicable reference deviation gate unchanged");
            var clip=Analyze(Wave(.001),100000,20,true);
            Check(((string)Get(clip,"Message")).Contains("削顶"),"hardware clipping diagnosis retains priority");
            foreach(double charge in new[]{10d,100d,1000d,5000d,100000d,200000d}) {
                var deferred=Analyze(Wave(.001),charge,5);
                Check((bool)Get(deferred,"SaveRecommended")&&!(bool)Get(deferred,"CircuitSuspected"),"no universal gain gate for "+charge+" pC");
                Check(!(bool)Get(deferred,"CircuitReferenceChecked")&&double.IsNaN((double)Get(deferred,"ExpectedPeakV"))&&double.IsNaN((double)Get(deferred,"ReferenceDeviationFactor")),"skipped reference is not fabricated evidence");
                Check(((string)Get(deferred,"Message")).Contains("回路参考比对未启用"),"reference policy disclosed");
                Check(Math.Abs((double)Get(deferred,"CoefficientPcPerV")-charge/.15)<1e-6,"coefficient still uses actual charge/peak");
            }
            Application.EnableVisualStyles();
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.CalibrationProgramControl",true))) {
                control.GetType().GetField("ShowReflectionSelectionNoticeForTests",All).SetValue(control,(Action<string>)(message=>{}));
                control.Size=new Size(1280,720);Set(control,"EmbeddedMode",true);Set(control,"SelectedLevelCode","100pC");
                Call(control,"SetPhaseParameters",-1,180d,180d,false,false);
                Call(control,"SetSignalMetrics",55d,.15,.001);Call(control,"SetPhaseWaveform",-1,"100pC",flat,0d,1e-8);
                Call(control,"ReceiveLiveCapture",-1,"100pC",Capture(flat,unknown));
                Check(double.IsNaN((double)Get(control,"signalToNoiseDb")),"unknown capture clears previous 55dB display");
                Check((double)Get(control,"peakVoltageV")>.14&&(double)Get(control,"noiseVoltageV")==0,"measured peak and zero noise remain visible without fake noise floor");
                Check((string)Call(control,"SignalToNoiseMetricCaption",double.NaN)=="未能估计","card never displays raw NaN/Infinity");
                Call(control,"SavePng",Path.Combine(qa,"unknown-snr-waveform.png"),1280,720);
                string info=(string)Get(control,"measurementInformationText");Check(info.Contains("信噪比：未能估计 / 待复测")&&!info.Contains("NaN")&&!info.Contains("Infinity"),"quality details communicate uncertainty");
                Check(((IDictionary)Get(control,"liveSavedFiles")).Count==0,"unknown-quality receive does not auto-save");
                Set(control,"compactQualityTab",true);Call(control,"SavePng",Path.Combine(qa,"unknown-snr-noise-card.png"),1280,720);
            }
            Console.WriteLine("PASS "+checks+"; HARDWARE_COMMANDS=0; PROJECT_WRITES=0; QA="+qa);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
