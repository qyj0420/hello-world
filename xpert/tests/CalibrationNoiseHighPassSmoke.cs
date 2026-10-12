using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CalibrationNoiseHighPassSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object F(object item,string name){return item.GetType().GetField(name,All).GetValue(item);}
    static void SF(object item,string name,object value){item.GetType().GetField(name,All).SetValue(item,value);}
    static object Call(object item,string name,params object[] args){var type=item as Type??item.GetType();return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(item is Type?null:item,args);}
    static async Task Run(string root)
    {
        Assembly module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(root),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
        Type captureType=module.GetType("Xiongxin.Scope.Capture",true),replyType=module.GetType("Xiongxin.Scope.Reply",true),settingsType=module.GetType("Xiongxin.Scope.Settings",true);
        object capture=Activator.CreateInstance(captureType),reply=Activator.CreateInstance(replyType),settings=Activator.CreateInstance(settingsType);
        SF(F(settings,"A"),"Range",5);SF(F(settings,"A"),"Coupling",0);
        short[] adc=Enumerable.Range(0,50001).Select(i=>(short)(1000+3000*Math.Sin(2*Math.PI*10000*i/1000000))).ToArray();
        short[] original=(short[])adc.Clone();
        SF(reply,"Config",settings);SF(reply,"IntervalNs",1000d);SF(reply,"Samples",adc.Length);SF(reply,"MaxAdc",(short)32767);
        SF(capture,"Info",reply);SF(capture,"A",adc);
        var volts=(double[])Call(module.GetType("Xiongxin.Scope.CaptureVoltage",true),"Volts",capture,true);
        var filter=module.GetType("HVX.SignalProcessing.PdLinearHighPass",true);
        var expected=(double[])Call(filter,"Apply",volts,1000000d);
        Type formType=module.GetType("Xiongxin.Calibration.CalibrationForm",true),controlType=module.GetType("HVX.Controls.CalibrationProgramControl",true);
        using(var form=(Form)Activator.CreateInstance(formType,All,null,new object[]{null,true},null))
        using(var control=(Control)Activator.CreateInstance(controlType))
        {
            ((System.Windows.Forms.Timer)F(form,"safetyTimer")).Stop();
            Check(F(form,"box")==null&&F(form,"client")==null,"offline construction does not open hardware workers");
            double[] observation=null;int events=0;
            Action<double[],double,double> callback=(samples,x0,dx)=>{
                events++;observation=samples;
                Check(x0==0&&Math.Abs(dx-1e-6)<1e-18,"published observation retains acquired sample times");
                Call(control,"SetPhaseAcquiredNoiseWaveform",-1,samples,x0,dx);
            };
            formType.GetEvent("HostNoiseCaptured",All).GetAddMethod(true).Invoke(form,new object[]{callback});
            await (Task)Call(form,"ShowQualityAsync",capture);
            Check(events==1&&observation.Length==adc.Length,"one full-segment noise event published");
            Check(observation.Zip(expected,(a,b)=>Math.Abs(a-b)).Max()<1e-13,"noise display receives DC-removed then 10 kHz filtered samples");
            Check(adc.SequenceEqual(original),"original ADC values remain unchanged");
            object phase=Call(control,"GetPhaseStateNoLock",-1),wave=F(phase,"Noise");
            double[] display=(double[])F(wave,"Samples");
            Check((bool)F(wave,"AcquisitionNoiseHighPassed"),"live noise carries explicit processing state");
            Check(display.SequenceEqual(observation)&&!Object.ReferenceEquals(display,observation),"UI owns a stable copy at the same processing level");
            Check(Object.ReferenceEquals(F(control,"noiseFilteredSamples"),display),"template and match cache reuses displayed filtered samples");
            var selected=await (Task<double[]>)Call(control,"NoiseHighPassedSamplesAsync",wave);
            Check(Object.ReferenceEquals(selected,display),"template selection does not repeat high-pass");
            SF(control,"noiseFilteredWave",null);SF(control,"noiseFilteredSamples",null);
            selected=await (Task<double[]>)Call(control,"NoiseHighPassedSamplesAsync",wave);
            Check(Object.ReferenceEquals(selected,display),"phase/view cache invalidation cannot cause a second high-pass");
            var twice=(double[])Call(filter,"Apply",display,1000000d);
            Check(twice.Skip(1000).Take(40000).Zip(display.Skip(1000),(a,b)=>Math.Abs(a-b)).Max()>.005,"fixture detects double-filter attenuation");
            Call(control,"SetPhaseNoiseWaveform",0,volts,0d,1e-6);
            object legacy=F(Call(control,"GetPhaseStateNoLock",0),"Noise");
            Check(!(bool)F(legacy,"AcquisitionNoiseHighPassed"),"legacy/simulation API is not falsely marked prefiltered");
            selected=await (Task<double[]>)Call(control,"NoiseHighPassedSamplesAsync",legacy);
            Check(selected.Zip(expected,(a,b)=>Math.Abs(a-b)).Max()<1e-13,"legacy template input retains single derived high-pass");
            Check(F(form,"box")==null&&F(form,"client")==null,"noise publication never creates device interfaces");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        Exception failure=null;
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0}){
            pump.Shown+=async delegate{try{await Run(args[0]);}catch(Exception error){failure=error;}finally{pump.Close();}};
            Application.Run(pump);
        }
        if(failure!=null){Console.Error.WriteLine(failure is TargetInvocationException?((TargetInvocationException)failure).InnerException:failure);return 1;}
        Console.WriteLine("CALIBRATION_NOISE_HIGH_PASS_PASS="+checks+";hardware=false;raw_adc_unchanged=true");return 0;
    }
}
