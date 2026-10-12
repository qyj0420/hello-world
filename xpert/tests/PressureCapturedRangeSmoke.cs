using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureCapturedRangeSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static void Field(object owner,string name,object value){owner.GetType().GetField(name,F).SetValue(owner,value);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name,F).GetValue(owner,null);}
    [STAThread]static int Main(string[] args)
    {
        try{
            string build=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>{
                string path=Path.Combine(build,"modules","PressureTest",new AssemblyName(eventArgs.Name).Name+".dll");
                return File.Exists(path)?Assembly.LoadFrom(path):null;
            };
            var module=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.PressureTestControl",true))){
                Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"no capture has no actual range");
                var result=Activator.CreateInstance(module.GetType("HVX.Controls.PressureZeroVoltageResult",true),true);
                var capture=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.BenchCapture",true));
                var settings=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.BenchSettings",true));
                Field(settings,"RangeIndex",5);Field(capture,"Settings",settings);Field(result,"Capture",capture);
                Field(control,"lastPressureCapture",result);Field(control,"lastPressureAcquisitionId","round-current");
                Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"unmatched capture cannot supply a range");
                Field(control,"cycleAcquisitionId","round-current");
                Check((int)Get(control,"CapturedPicoRangeIndex")==5,"matched actual capture range is exported");
                module.GetType("HVX.Controls.PressureTestControl",true).GetMethod("SetDischargeCalibration",F).Invoke(control,new object[]{1000d,"fixture-A.cal",DateTimeOffset.UtcNow,"A",false});
                module.GetType("HVX.Controls.PressureTestControl",true).GetMethod("SetSelectedRange",F).Invoke(control,new object[]{"PICO:10"});
                Check((int)Get(control,"SelectedPicoRangeCode")==10&&(int)Get(control,"CapturedPicoRangeIndex")==5,"changing UI selection does not rewrite captured range");
                Field(settings,"RangeIndex",10);Check((int)Get(control,"CapturedPicoRangeIndex")==10,"real maximum capture is identified");
                Field(control,"cycleAcquisitionId","next-round");Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"previous capture cannot classify the next round");
                Field(control,"cycleAcquisitionId","round-current");Field(settings,"RangeIndex",11);Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"range outside device contract is rejected");
                Field(settings,"RangeIndex",-1);Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"negative capture range is rejected");
                Field(settings,"RangeIndex",0);Check((int)Get(control,"CapturedPicoRangeIndex")==0,"valid smallest capture range is preserved");
                Field(capture,"Settings",null);Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"missing capture settings are not replaced with UI values");
                Field(result,"Capture",null);Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"missing capture payload is not guessed");
                Field(result,"Capture",capture);Field(capture,"Settings",settings);Field(control,"lastPressureAcquisitionId","");Field(control,"cycleAcquisitionId","");
                Check((int)Get(control,"CapturedPicoRangeIndex")==-1,"two empty identities are not a matched capture");
            }
            Console.WriteLine("PRESSURE_CAPTURED_RANGE_PASS="+checks+"; HardwareAccessed=false; Input=synthetic; ProductMainStarted=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
