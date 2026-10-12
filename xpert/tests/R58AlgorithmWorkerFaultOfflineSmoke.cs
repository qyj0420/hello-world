using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Deliberately malformed algorithm-pipe fixtures. No algorithm is replaced in
// the product and no product, UI, acquisition process or SDK is started.
static class R58AlgorithmWorkerFaultOfflineSmoke
{
    const BindingFlags S=BindingFlags.Static|BindingFlags.NonPublic,I=BindingFlags.Instance|BindingFlags.NonPublic;
    static Type runtime,retained;static readonly List<object> checks=new List<object>();
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};}
    static Dictionary<string,object> Payload(int count=32){return new Dictionary<string,object>{{"algorithm","E+"},{"config",new Dictionary<string,object>()},{"records",new object[]{new Dictionary<string,object>{{"record_id","fault-fixture"},{"samples",new double[count]}}}}};}
    static Task<string> Run(string parent,Dictionary<string,object> payload,CancellationToken token,int timeout){try{return (Task<string>)runtime.GetMethod("RunAsync",S).Invoke(null,new object[]{parent,null,payload,token,null,timeout});}catch(TargetInvocationException error){throw error.InnerException??error;}}
    static void Check(string name,bool okay,object detail){checks.Add(new{name=name,passed=okay,detail=detail});if(!okay)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static Process Fixture(string parent,string script)
    {
        string root=Path.Combine(parent,"r71");
        var start=new ProcessStartInfo{FileName=Path.Combine(root,"python.exe"),Arguments="-I -X utf8 -B -c \""+script+"\"",WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false,true)};
        start.EnvironmentVariables.Clear();foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","LOCALAPPDATA","USERPROFILE"}){string value=Environment.GetEnvironmentVariable(key);if(value!=null)start.EnvironmentVariables[key]=value;}start.EnvironmentVariables["PATH"]=root+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
        var process=new Process{StartInfo=start};process.Start();
        object owner=Activator.CreateInstance(retained,I,null,new object[]{root,new List<FileStream>(),process},null);
        retained.GetField("started",I).SetValue(owner,true);retained.GetField("errors",I).SetValue(owner,Task.FromResult(""));
        // Explicit test-only injection exercises the real private Exchange and
        // RunAsync failure paths. These fixtures cannot run in normal builds.
        runtime.GetField("worker",S).SetValue(null,owner);return process;
    }
    static int Main(string[] args)
    {
        try{
            if(args.Length!=3)throw new ArgumentException("DLL, pinned runtime parent, fresh QA path required");
            string qa=Path.GetFullPath(args[2]);if(Directory.Exists(qa))throw new IOException("Fresh QA output required");Directory.CreateDirectory(qa);
            Assembly module=Assembly.LoadFrom(args[0]);runtime=module.GetType("HVX.PdReview.ReviewERuntime",true);retained=runtime.GetNestedType("RetainedWorker",BindingFlags.NonPublic);
            // Verify the exact runtime executable before controlled pipe tests.
            string python=Path.Combine(args[1],"r71","python.exe");string manifest=Path.Combine(Path.GetDirectoryName(args[1]),"research-pins.json");
            var pins=Json().Deserialize<Dictionary<string,string>>(File.ReadAllText(manifest));
            using(var input=File.OpenRead(python))using(var sha=System.Security.Cryptography.SHA256.Create())if(!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),pins["python.exe"],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Fixture Python pin mismatch");
            string oversized="import sys,struct,time;sys.stdout.buffer.write(struct.pack('<I',4*32*1024*1024+4097));sys.stdout.buffer.flush();time.sleep(30)";
            Process failed=Fixture(args[1],oversized);int failedId=failed.Id;var watch=Stopwatch.StartNew();bool invalid=false;
            try{Run(args[1],Payload(16000000),CancellationToken.None,20).GetAwaiter().GetResult();}catch(InvalidDataException){invalid=true;}
            Check("bad_frame_reader_aborts_blocked_128MB_writer_promptly",invalid&&watch.ElapsedMilliseconds<5000,new{elapsed_ms=watch.Elapsed.TotalMilliseconds,pid=failedId});
            Process timeout=Fixture(args[1],"import time;time.sleep(30)");int timeoutId=timeout.Id;watch.Restart();bool timed=false;
            try{Run(args[1],Payload(),CancellationToken.None,1).GetAwaiter().GetResult();}catch(TimeoutException){timed=true;}
            Check("request_deadline_kills_worker_and_is_timeout",timed&&watch.ElapsedMilliseconds<5000,new{elapsed_ms=watch.Elapsed.TotalMilliseconds,pid=timeoutId});
            string wrong="import sys,struct,json,time;h=struct.unpack('<I',sys.stdin.buffer.read(4))[0];r=json.loads(sys.stdin.buffer.read(h));sys.stdin.buffer.read(r['records'][0]['sample_count']*8);e=json.dumps({'schema':'HVX.LocationE.Worker/1','request_id':'0'*32,'status':'ok','result':{}}).encode();sys.stdout.buffer.write(struct.pack('<I',len(e))+e);sys.stdout.buffer.flush();time.sleep(30)";
            Fixture(args[1],wrong);bool identity=false;try{Run(args[1],Payload(),CancellationToken.None,20).GetAwaiter().GetResult();}catch(InvalidDataException error){identity=error.Message.Contains("identity");}
            Check("mismatched_request_identity_is_not_committed",identity,null);
            Fixture(args[1],"import sys,struct,time;sys.stdout.buffer.write(struct.pack('<I',2)+bytes([255,255]));sys.stdout.buffer.flush();time.sleep(30)");bool utf8=false;try{Run(args[1],Payload(),CancellationToken.None,20).GetAwaiter().GetResult();}catch(DecoderFallbackException){utf8=true;}
            Check("malformed_UTF8_is_rejected",utf8,null);
            Process waiting=Fixture(args[1],"import time;time.sleep(30)");Task<string> active=Run(args[1],Payload(),CancellationToken.None,1);
            using(var queuedCancel=new CancellationTokenSource(50)){
                bool cancelled=false;try{Run(args[1],Payload(),queuedCancel.Token,20).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                Check("cancelled_queued_request_does_not_kill_active_owner",cancelled&&!waiting.HasExited,null);
            }
            timed=false;try{active.GetAwaiter().GetResult();}catch(TimeoutException){timed=true;}Check("active_owner_keeps_own_independent_deadline",timed,null);
            Fixture(args[1],"import time;time.sleep(30)");using(var cancel=new CancellationTokenSource(100)){
                bool cancelled=false;watch.Restart();try{Run(args[1],Payload(),cancel.Token,20).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
                Check("active_cancel_kills_pipe_worker_without_success",cancelled&&watch.ElapsedMilliseconds<5000,new{elapsed_ms=watch.Elapsed.TotalMilliseconds});
            }
            Check("failed_requests_leave_no_live_worker",runtime.GetField("worker",S).GetValue(null)==null&&runtime.GetField("pendingRelease",S).GetValue(null)==null,null);
            File.WriteAllText(Path.Combine(qa,"offline-proof.json"),Json().Serialize(new{schema="HVX.R58.AlgorithmWorkerFaultOffline/1",passed=true,checks=checks,pinned_python=python,product_started=false,ui_constructed=false,hardware_sdk_called=false,user_project_written=false,fixture_scope="Controlled Python pipe children injected through reflection only in isolated offline test process; not product runtime."}),new UTF8Encoding(false));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(runtime!=null)try{runtime.GetMethod("Shutdown",S).Invoke(null,null);}catch{ }}
    }
}
