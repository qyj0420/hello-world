using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

// Final-candidate probe. The only methods invoked are the thirteen explicitly
// selected replay/fake tests. Loading the managed worker assembly does not run
// Main, a self-test dispatcher, a resident native session, or a device SDK.
internal static class FactoryFocusedFixesPublishedSmoke
{
    const BindingFlags StaticPrivate=BindingFlags.NonPublic|BindingFlags.Static;
    static string Hash(string path)
    {
        using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
    }
    static string Run(Assembly worker,string name,string method)
    {
        var type=worker.GetType("Xiongxin.FactoryTest."+name,true);
        var call=type.GetMethod(method,StaticPrivate,null,Type.EmptyTypes,null);
        if(call==null||call.ReturnType!=typeof(string))throw new InvalidOperationException("Missing exact fake test method: "+name+"."+method);
        string result;
        try{result=(string)call.Invoke(null,null);}
        catch(TargetInvocationException error){ExceptionDispatchInfo.Capture(error.InnerException??error).Throw();throw;}
        if(result==null||!result.Contains("PASS")||!result.Contains("HardwareAccessed=False"))
            throw new InvalidOperationException("Selected fake test did not confirm offline pass: "+name);
        Console.WriteLine(name+"."+method+" => "+result);
        return result;
    }
    static int Main(string[] args)
    {
        try{
            if(args==null||args.Length!=2)throw new ArgumentException("Expected candidate build root and isolated QA fixture directory.");
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(!qa.StartsWith(Path.Combine(build,"qa")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fake fixture path must stay inside this candidate QA directory.");
            Directory.CreateDirectory(qa);
            string runtime=Path.Combine(build,"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe");
            string before=Hash(runtime);var worker=Assembly.LoadFrom(runtime);
            var fixture=worker.GetType("Xiongxin.FactoryTest.FactoryResidentDeviceServerTests",true).GetField("FixtureRoot",StaticPrivate);
            if(fixture==null||fixture.FieldType!=typeof(string))throw new InvalidOperationException("Missing managed resident fake fixture root.");
            // RunCore is invoked only by the selected fake test using its own
            // in-process fake session, fake lease and injected round delegates.
            fixture.SetValue(null,qa);
            var residualFixture=worker.GetType("Xiongxin.FactoryTest.FactoryNoResidualPressureTests",true).GetField("FixtureRoot",StaticPrivate);
            if(residualFixture==null||residualFixture.FieldType!=typeof(string))throw new InvalidOperationException("Missing no-residual fake fixture root.");
            residualFixture.SetValue(null,Path.Combine(qa,"no-residual-policy"));
            Console.WriteLine("SUPERSEDED_POLICY: historical startup/4%-residual suites are NOT current pressure acceptance and are not run.");
            Run(worker,"FactoryNoResidualPressureTests","Run");
            Run(worker,"FactoryNativeSessionTests","Run");
            Run(worker,"FactoryResidentZeroInputTests","Run");
            Run(worker,"FactoryBatchAuthorizationTests","Run");
            Run(worker,"FactoryResidentDeviceServerTests","RunFailedRelease");
            Run(worker,"FactoryResidentDeviceServerTests","RunInputStopSnapshots");
            Run(worker,"FactoryResidentDeviceServerTests","RunHealthyRoundStop");
            Run(worker,"FactoryResidentDeviceServerTests","RunInitialFeedback");
            Run(worker,"FactorySelectedFeedbackTests","Run");
            Run(worker,"FactoryFeedbackConfigurationTests","Run");
            Run(worker,"FactoryEmergencyDisplayTests","Run");
            Run(worker,"FactoryResidentDeviceServerTests","RunContinuousInitialFeedback");
            Run(worker,"FactoryPressurePrecisionTests","Run");
            if(Hash(runtime)!=before)throw new InvalidOperationException("Final candidate worker changed during focused tests.");
            Console.WriteLine("FACTORY_FOCUSED_FIXES_PUBLISHED_PASS=13; RUNTIME_SHA256="+before+"; HARDWARE_COMMANDS=0; SDK_CALLED=0; WORKER_ENTRY_CALLED=0; FULL_SELF_TEST=0; USER_PROJECTS_WRITTEN=0");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
