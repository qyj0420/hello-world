using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
static class PressureExitCleanupSmoke
{
    static int Main(string[] args)
    {
        try {
            string dll=Path.Combine(args[0],"modules","PressureRuntime","Xiongxin.FactoryTest.exe");
            if(!File.Exists(dll))dll=Directory.GetFiles(Path.Combine(args[0],"modules","PressureRuntime"),"*.exe")[0];
            var a=Assembly.LoadFrom(dll);
            var method=a.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true).GetMethod("WaitForExitCleanup",BindingFlags.Static|BindingFlags.NonPublic);
            Func<Task,bool> call=t=>((Task<bool>)method.Invoke(null,new object[]{t,40})).GetAwaiter().GetResult();
            if(!call(Task.FromResult(0)))throw new Exception("successful cleanup not acknowledged");
            var failed=new TaskCompletionSource<int>();failed.SetException(new IOException("synthetic cleanup failure"));
            if(call(failed.Task))throw new Exception("failed cleanup falsely acknowledged");
            var pending=new TaskCompletionSource<int>();var watch=Stopwatch.StartNew();
            if(call(pending.Task)||watch.ElapsedMilliseconds>2000||pending.Task.IsCompleted)throw new Exception("timeout did not preserve pending ownership");
            pending.SetException(new IOException("late synthetic cleanup error"));
            Console.WriteLine("EXIT_CLEANUP_PASS=3; HARDWARE_CALLS=0; no worker process launched");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
