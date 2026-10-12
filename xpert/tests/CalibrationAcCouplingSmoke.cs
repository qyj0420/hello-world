using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationAcCouplingSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Assembly a;static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object New(string name){return Activator.CreateInstance(a.GetType(name,true),true);}
    static object F(object o,string name){return o.GetType().GetField(name,B).GetValue(o);}
    static void S(object o,string name,object value){o.GetType().GetField(name,B).SetValue(o,value);}
    static object Call(string name,string method,params object[] args){return a.GetType(name,true).GetMethod(method,B).Invoke(null,args);}
    static object Capture(short[] adc)
    {
        var c=New("Xiongxin.Scope.Capture");var r=New("Xiongxin.Scope.Reply");var settings=New("Xiongxin.Scope.Settings");
        S(F(settings,"A"),"Range",6);S(r,"Config",settings);S(r,"MaxAdc",(short)32767);S(r,"Samples",adc.Length);S(r,"IntervalNs",100d);S(r,"Overflow",(short)1);
        S(c,"Info",r);S(c,"A",adc);return c;
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            a=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            using(var form=(Form)Activator.CreateInstance(a.GetType("Xiongxin.Calibration.CalibrationForm",true),B,null,new object[]{null,true},null)) {
                var settings=form.GetType().GetMethod("Settings",B).Invoke(form,null);
                Check((int)F(F(settings,"A"),"Coupling")==0,"live calibration settings request SDK AC enum 0");
                Check(!(bool)F(F(settings,"B"),"Enabled"),"unused B channel remains disabled");
            }
            short[] centered=Enumerable.Range(0,200000).Select(i=>(short)(100*Math.Sin(i*.1)+(i==100000?8000:0)-(i==100100?8000:0))).ToArray();
            double[] zero=null;
            foreach(short offset in new short[]{0,10000,-10000}) {
                var adc=centered.Select(v=>(short)(v+offset)).ToArray();var copy=(short[])adc.Clone();var c=Capture(adc);
                double[] volts=(double[])Call("Xiongxin.Calibration.AcquisitionPlanning","Volts",c);
                double mean=adc.Average(v=>(double)v),scale=1d/32767;
                Check(volts.Select((v,i)=>Math.Abs(v-(adc[i]-mean)*scale)).Max()<1e-15,"each sample subtracts full-record mean before scaling");
                Check(Math.Abs(volts.Average())<1e-13,"processed full record is zero mean");
                if(zero==null)zero=volts;else Check(volts.Select((v,i)=>Math.Abs(v-zero[i])).Max()<1e-12,"constant positive/negative ADC offset cannot change waveform timing or amplitude within floating point tolerance");
                Check(adc.SequenceEqual(copy)&&(short)F(F(c,"Info"),"Overflow")==1,"raw ADC and overflow flag remain unchanged");
                var raw=(double[])Call("Xiongxin.Calibration.AcquisitionPlanning","RawVolts",c);
                Check(raw.Select((v,i)=>Math.Abs(v-adc[i]*scale)).Max()<1e-15,"physical range evidence retains original bias");
                object q=Call("Xiongxin.Calibration.SignalQuality","FromCapture",c,.03d);
                Check(Math.Abs((double)F(q,"MaxAbsV")-volts.Where((v,i)=>i%Math.Max(1,adc.Length/200000)==0).Max(Math.Abs))<1e-12,"quality peak uses same centered samples as preview");
                using(var plot=(Control)New("Xiongxin.Scope.ScopePlot")) {
                    plot.GetType().GetMethod("SetData",B).Invoke(plot,new[]{c});
                    Check(Math.Abs(((double[])F(plot,"dcMeanCodes"))[0]-mean)<1e-12,"standalone plot uses identical offset");
                    plot.GetType().GetMethod("ResetView",B).Invoke(plot,null);
                    Check(((double[])F(plot,"dcMeanCodes"))[0]==mean,"zoom reset does not re-estimate from cropped view");
                }
                var filtered=(double[])Call("HVX.SignalProcessing.PdLinearHighPass","Apply",volts,10e6);
                var reference=(double[])Call("HVX.SignalProcessing.PdLinearHighPass","Apply",zero,10e6);
                Check(filtered.Select((v,i)=>Math.Abs(v-reference[i])).Max()<1e-12,"DC removal precedes high pass and is offset invariant");
            }
            foreach(short value in new short[]{32767,-32768,1234}) {
                var c=Capture(Enumerable.Repeat(value,131073).ToArray());
                Check(((double[])Call("Xiongxin.Calibration.AcquisitionPlanning","Volts",c)).All(v=>v==0),"large constant block removes DC with 64-bit accumulation");
                Check(((double[])Call("Xiongxin.Calibration.AcquisitionPlanning","RawVolts",c)).All(v=>Math.Abs(v-value/32767d)<1e-15),"near-rail input remains available for clipping/range checks");
            }
            Console.WriteLine("CALIBRATION_AC_DC_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
