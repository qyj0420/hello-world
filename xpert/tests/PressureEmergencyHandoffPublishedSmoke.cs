using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Load the actual candidate's managed worker, never its Main, dispatchers,
// native session entry, SDK or simulation entry. The six exact fake suites
// below are the complete invocation whitelist; no discovery-based self-test.
internal static class PressureEmergencyHandoffPublishedSmoke
{
    const BindingFlags StaticPrivate=BindingFlags.NonPublic|BindingFlags.Static;
    static readonly string[] Allowed={
        "FactoryEmergencyDisplayTests.Run",
        "FactoryEmergencyTimingContractTests.Run",
        "FactoryResidentDeviceServerTests.RunInitialFeedback",
        "FactoryResidentDeviceServerTests.RunContinuousInitialFeedback",
        "FactoryResidentDeviceServerTests.RunFailedRelease",
        "FactoryResidentDeviceServerTests.RunInputStopSnapshots"
    };
    static string Hash(string path)
    {
        using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",String.Empty);
    }
    static int Run(Assembly worker,string exactName)
    {
        if(Array.IndexOf(Allowed,exactName)<0)throw new InvalidOperationException("Fake method is not whitelisted.");
        int separator=exactName.LastIndexOf('.');
        string className=exactName.Substring(0,separator),methodName=exactName.Substring(separator+1);
        var type=worker.GetType("Xiongxin.FactoryTest."+className,true);
        var method=type.GetMethod(methodName,StaticPrivate,null,Type.EmptyTypes,null);
        bool timing=exactName=="FactoryEmergencyTimingContractTests.Run";
        if(method==null||method.ReturnType!=(timing?typeof(int):typeof(string)))
            throw new InvalidOperationException("Missing exact fake contract: "+exactName);
        object value;
        try{value=method.Invoke(null,null);}
        catch(TargetInvocationException error){ExceptionDispatchInfo.Capture(error.InnerException??error).Throw();throw;}
        int checks;
        string result;
        if(timing)
        {
            checks=(int)value;
            result=checks.ToString(CultureInfo.InvariantCulture)+" managed fake checks PASS; HardwareAccessed=False; SDKCalled=False";
        }
        else
        {
            result=(string)value;
            if(String.IsNullOrWhiteSpace(result)||!result.Contains("checks PASS")||!result.Contains("HardwareAccessed=False"))
                throw new InvalidOperationException("Selected fake suite did not confirm offline pass: "+exactName);
            var count=Regex.Match(result,@":\s*(\d+)\s");
            if(!count.Success||!Int32.TryParse(count.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out checks))
                throw new InvalidOperationException("Missing explicit fake check count: "+exactName);
        }
        if(checks<=0)throw new InvalidOperationException("Empty fake suite: "+exactName);
        Console.WriteLine(exactName+" => "+result);
        return checks;
    }
    static int Main(string[] args)
    {
        try
        {
            if(args==null||args.Length!=2||!Path.IsPathRooted(args[0])||!Path.IsPathRooted(args[1]))
                throw new ArgumentException("Expected absolute candidate root and isolated QA fixture directory.");
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(!qa.StartsWith(Path.Combine(build,"qa")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.Exists(qa))
                throw new InvalidOperationException("A fresh fake fixture must stay inside this candidate's QA directory.");
            string runtime=Path.Combine(build,"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe");
            string before=Hash(runtime);
            var worker=Assembly.LoadFrom(runtime);
            if(!String.Equals(Path.GetFullPath(worker.Location),runtime,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Loaded worker is not the exact candidate runtime.");
            var handoffCase=worker.GetType("Xiongxin.FactoryTest.FactoryEmergencyDisplayTests",true)
                .GetMethod("ReadModeDisplay",StaticPrivate,null,Type.EmptyTypes,null);
            if(handoffCase==null||handoffCase.ReturnType!=typeof(void))
                throw new InvalidOperationException("Candidate lacks the current read-mode/display/permit handoff regression.");
            Directory.CreateDirectory(qa);
            var fixture=worker.GetType("Xiongxin.FactoryTest.FactoryResidentDeviceServerTests",true).GetField("FixtureRoot",StaticPrivate);
            if(fixture==null||fixture.FieldType!=typeof(string))throw new InvalidOperationException("Missing exact managed fake fixture root.");
            // These selected resident tests alone call RunCore with in-process
            // fake devices, leases and injected delegates. Never the product.
            fixture.SetValue(null,qa);
            Console.WriteLine("PRESSURE_EMERGENCY_SOURCE="+runtime+"; RUNTIME_SHA256="+before+"; MANAGED_ASSEMBLY_ONLY=True; WORKER_ENTRY_CALLED=0");
            int total=0;
            foreach(string exactName in Allowed)total+=Run(worker,exactName);
            if(Hash(runtime)!=before)throw new InvalidOperationException("Candidate runtime changed during focused tests.");
            Console.WriteLine("PRESSURE_EMERGENCY_HANDOFF_PUBLISHED_PASS="+Allowed.Length+"; CHECKS="+total+
                "; RUNTIME_SHA256="+before+"; RuntimeUnchanged=True; HardwareAccessed=False; SDKCalled=False; ProductEntryExecuted=False; UserProjectsWritten=False; FullRegression=False; FULL_SELF_TEST=0; PhysicalAcceptance=False");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
