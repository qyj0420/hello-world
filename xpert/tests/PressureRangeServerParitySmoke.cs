using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class PressureRangeServerParitySmoke
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int Main(string[] args)
    {
        string oldTemp=Environment.GetEnvironmentVariable("TEMP"),oldTmp=Environment.GetEnvironmentVariable("TMP");
        try{
            string root=Path.GetFullPath(args[0]),qa=Path.Combine(root,"qa","range-parity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            Environment.SetEnvironmentVariable("TEMP",qa);Environment.SetEnvironmentVariable("TMP",qa);
            WindowsFormsSynchronizationContext.AutoInstall=false;SynchronizationContext.SetSynchronizationContext(null);
            var runtime=Assembly.LoadFrom(Path.Combine(root,"modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe"));
            var ui=Assembly.LoadFrom(Path.Combine(root,"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
            var server=runtime.GetType("Xiongxin.FactoryTest.FactoryBatchAuthorization",true).GetMethod("ApplyChargeFloor",F);
            var minimum=ui.GetType("HVX.Controls.PressureChargeRangeFloor",true).GetMethod("MinimumIndex",F);
            int checks=0;
            foreach(double coefficient in new[]{5d,12.345,123.456,999,1000,2100,2500,10000,1e6}){
                int floor=(int)minimum.Invoke(null,new object[]{coefficient});
                for(int i=0;i<11;i++){
                    int actual=(int)server.Invoke(null,new object[]{i,coefficient});
                    if(actual!=Math.Max(i,floor))throw new Exception("UI/server disagree: "+coefficient+" / "+i);
                    checks++;
                }
            }
            foreach(string name in new[]{"FactoryBatchAuthorizationTests","FactoryBatchServerTests","FactorySessionFaultTests"}){
                // Only pure managed fixture entry points; never runtime Main,
                // native backend, discovery, resident launch or SDK calls.
                var type=runtime.GetType("Xiongxin.FactoryTest."+name,true);
                Console.WriteLine(type.GetMethod("Run",F).Invoke(null,null));
            }
            var summary=runtime.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true).GetMethod("CleanupFailureSummary",F);
            var failure=new InvalidOperationException("Shutdown: ART -88709: aborted\n Status Code: -88709\n at NativeFactoryBackend.ShutdownCore\n"+new string('x',40000));
            string original=failure.Message;
            string display=(string)summary.Invoke(null,new object[]{failure});
            if(display.Length>220||!display.Contains("-88709")||display.IndexOf("-88709")!=display.LastIndexOf("-88709")||
                display.Contains("ShutdownCore")||!display.Contains("输出状态未知")||!display.Contains("禁止继续加压")||failure.Message!=original)
                throw new Exception("cleanup summary changes evidence, expands stack, or hides safety uncertainty");
            checks++;
            if(!((string)summary.Invoke(null,new object[]{null})).Contains("输出状态未知"))throw new Exception("unknown cleanup must remain unknown");checks++;
            Console.WriteLine("PASS "+checks+" UI/server range parity checks; managed fake suites; no hardware; "+qa);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Environment.SetEnvironmentVariable("TEMP",oldTemp);Environment.SetEnvironmentVariable("TMP",oldTmp);}
    }
}
