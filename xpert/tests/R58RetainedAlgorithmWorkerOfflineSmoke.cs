using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Algorithm-only integration QA. Never constructs a UI/product/hardware SDK.
static class R58RetainedAlgorithmWorkerOfflineSmoke
{
    const BindingFlags S=BindingFlags.Static|BindingFlags.NonPublic;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.NonPublic;
    static Assembly module;static Type runtime;static readonly List<object> checks=new List<object>();
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};}
    static object Call(string name,params object[] args){try{return runtime.GetMethod(name,S,null,Array.ConvertAll(args,x=>x.GetType()),null).Invoke(null,args);}catch(TargetInvocationException error){throw error.InnerException??error;}}
    static object Invoke(string name,params object[] args){try{return runtime.GetMethod(name,S).Invoke(null,args);}catch(TargetInvocationException error){throw error.InnerException??error;}}
    static object Reader(string loc,string field){var type=module.GetType("HVX.PdReview.ReviewLocationFileReader",true);object data=type.GetMethod("Read",S).Invoke(null,new object[]{loc,Path.GetDirectoryName(loc)});return data.GetType().GetField(field,I).GetValue(data);}
    static string Hash(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
    static Process WorkerProcess(){object worker=runtime.GetField("worker",S).GetValue(null);return worker==null?null:(Process)worker.GetType().GetField("process",I).GetValue(worker);}
    static int WorkerId(){var process=WorkerProcess();return process==null?-1:process.Id;}
    static Task<string> Run(string parent,Dictionary<string,object> payload,CancellationToken token,int timeout=180){return (Task<string>)Invoke("RunAsync",parent,null,payload,token,null,timeout);}
    static void Check(string name,bool passed,object details=null){checks.Add(new{name=name,passed=passed,details=details});if(!passed)throw new Exception("FAILED: "+name);Console.WriteLine("PASS "+name);}
    static void Wait(Func<bool> predicate){var timer=Stopwatch.StartNew();while(!predicate()){if(timer.ElapsedMilliseconds>6500)throw new TimeoutException("Offline worker release timeout");Thread.Sleep(10);}}
    static bool Writable(string path){try{using(var input=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))return true;}catch(IOException){return false;}}
    static bool Same(object left,object right)
    {
        if(left==null||right==null)return left==null&&right==null;
        var a=left as Dictionary<string,object>;var b=right as Dictionary<string,object>;
        if(a!=null||b!=null){if(a==null||b==null||a.Count!=b.Count)return false;foreach(var item in a){object value;if(!b.TryGetValue(item.Key,out value)||!Same(item.Value,value))return false;}return true;}
        var c=left as IList;var d=right as IList;if(c!=null||d!=null){if(c==null||d==null||c.Count!=d.Count)return false;for(int i=0;i<c.Count;i++)if(!Same(c[i],d[i]))return false;return true;}
        if(left is string||right is string||left is bool||right is bool)return Object.Equals(left,right);
        if(left is IConvertible&&right is IConvertible)return BitConverter.DoubleToInt64Bits(Convert.ToDouble(left,CultureInfo.InvariantCulture))==BitConverter.DoubleToInt64Bits(Convert.ToDouble(right,CultureInfo.InvariantCulture));
        return Object.Equals(left,right);
    }
    static Dictionary<string,object> Payload(string loc,string requestFile)
    {
        var input=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(requestFile));
        var record=new Dictionary<string,object>{{"record_id",Path.GetFileName(loc)},{"samples",(double[])Reader(loc,"ChargeSamples")}};
        input["records"]=new object[]{record};return input;
    }
    static string OneShot(string parent,Dictionary<string,object> payload)
    {
        string root=Path.Combine(parent,"r71"),python=Path.Combine(root,"python.exe");
        var record=new Dictionary<string,object>((Dictionary<string,object>)((object[])payload["records"])[0]);var samples=(double[])record["samples"];
        record.Remove("samples");record["sample_count"]=samples.Length;var header=new Dictionary<string,object>(payload);header["records"]=new object[]{record};header["transport"]="float64-le-v1";
        var start=new ProcessStartInfo{FileName=python,Arguments="-I -X utf8 -B -c \"import q73;q73.main()\"",WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false,true)};
        start.EnvironmentVariables.Clear();foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","LOCALAPPDATA","USERPROFILE"}){string value=Environment.GetEnvironmentVariable(key);if(value!=null)start.EnvironmentVariables[key]=value;}start.EnvironmentVariables["PATH"]=root+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
        using(var process=new Process{StartInfo=start}){
            process.Start();var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            try{var stream=process.StandardInput.BaseStream;byte[] bytes=Encoding.UTF8.GetBytes(Json().Serialize(header));stream.Write(BitConverter.GetBytes(bytes.Length),0,4);stream.Write(bytes,0,bytes.Length);bytes=new byte[samples.Length*8];Buffer.BlockCopy(samples,0,bytes,0,bytes.Length);stream.Write(bytes,0,bytes.Length);stream.Flush();process.StandardInput.Close();if(!process.WaitForExit(30000))throw new TimeoutException();string result=output.GetAwaiter().GetResult();if(process.ExitCode!=0)throw new IOException(errors.GetAwaiter().GetResult());if(result.Length>32*1024*1024)throw new InvalidDataException();return result;}
            finally{if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}
        }
    }
    static int Main(string[] args)
    {
        string qa=null;
        try{
            if(args.Length!=6)throw new ArgumentException("DLL, runtime parent, readonly LOC1, readonly LOC2, request template, fresh QA path required");
            Console.OutputEncoding=new UTF8Encoding(false);qa=Path.GetFullPath(args[5]);if(Directory.Exists(qa))throw new IOException("Fresh QA directory required");Directory.CreateDirectory(qa);
            string dll=Path.GetFullPath(args[0]),parent=Path.GetFullPath(args[1]),first=Path.GetFullPath(args[2]),second=Path.GetFullPath(args[3]);
            string before1=Hash(first),before2=Hash(second);module=Assembly.LoadFrom(dll);runtime=module.GetType("HVX.PdReview.ReviewERuntime",true);
            var one=Payload(first,args[4]);var two=Payload(second,args[4]);var timings=new List<object>();
            string reference1=OneShot(parent,one),reference2=OneShot(parent,two);
            string firstResult=null;int pid=-1;
            for(int trial=0;trial<4;trial++){
                var payload=trial==2?two:one;var timer=Stopwatch.StartNew();string result=Run(parent,payload,CancellationToken.None).GetAwaiter().GetResult();int currentPid=WorkerId();
                timings.Add(new{trial=trial,elapsed_ms=timer.Elapsed.TotalMilliseconds,worker_pid=currentPid,record_id=((Dictionary<string,object>)((object[])payload["records"])[0])["record_id"]});
                Check("trial_"+trial+"_all_values_match_one_shot",Same(Json().DeserializeObject(result),Json().DeserializeObject(trial==2?reference2:reference1)));
                if(trial==0){firstResult=result;pid=currentPid;}else Check("trial_"+trial+"_retains_same_process",pid==currentPid);
            }
            Check("distinct_LOC_waveform_identity",!Object.Equals(Json().Deserialize<Dictionary<string,object>>(reference1)["waveform_sha256"],Json().Deserialize<Dictionary<string,object>>(reference2)["waveform_sha256"]));
            var e=Payload(first,args[4]);e["algorithm"]="E";string eReference=OneShot(parent,e),eResult=Run(parent,e,CancellationToken.None).GetAwaiter().GetResult();
            Check("E_branch_all_values_match_one_shot",Same(Json().DeserializeObject(eResult),Json().DeserializeObject(eReference)));
            Check("E_and_Eplus_share_process_not_results",WorkerId()==pid&&Same(Json().DeserializeObject(Run(parent,one,CancellationToken.None).GetAwaiter().GetResult()),Json().DeserializeObject(reference1)));
            var manual=Payload(first,args[4]);var manualSettings=(Dictionary<string,object>)((Dictionary<string,object>)manual["config"])["settings"];manualSettings["manual_threshold_enabled"]=true;manualSettings["manual_threshold_pc"]=80d;
            string manualReference=OneShot(parent,manual),manualResult=Run(parent,manual,CancellationToken.None).GetAwaiter().GetResult();
            Check("changed_settings_same_LOC_matches_fresh_one_shot",Same(Json().DeserializeObject(manualResult),Json().DeserializeObject(manualReference)));
            Check("restore_auto_settings_does_not_reuse_manual_result",WorkerId()==pid&&Same(Json().DeserializeObject(Run(parent,one,CancellationToken.None).GetAwaiter().GetResult()),Json().DeserializeObject(reference1)));
            string component=Directory.GetFiles(Path.Combine(parent,"r71"),"q73*.pyd")[0];Check("retained_pinned_component_rejects_write_access",!Writable(component));
            using(var cancelled=new CancellationTokenSource()){
                Task<string> active=Run(parent,one,cancelled.Token);Check("idle_release_cannot_kill_active_request",!(bool)runtime.GetMethod("ReleaseIdleWorker",S,null,Type.EmptyTypes,null).Invoke(null,null));
                cancelled.CancelAfter(10);bool observed=false;try{active.GetAwaiter().GetResult();}catch(OperationCanceledException){observed=true;}Check("active_cancellation_is_not_success",observed);
            }
            Check("cancellation_releases_verified_components",Writable(component));
            string restarted=Run(parent,one,CancellationToken.None).GetAwaiter().GetResult();Check("explicit_next_request_restarts_and_matches",WorkerId()!=pid&&Same(Json().DeserializeObject(restarted),Json().DeserializeObject(reference1)));
            var invalid=Payload(first,args[4]);var invalidConfig=(Dictionary<string,object>)invalid["config"];var settings=(Dictionary<string,object>)invalidConfig["settings"];settings["detection_factor"]=3.5;
            bool rejected=false;try{Run(parent,invalid,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException error){rejected=error.Message.Contains("integer");}Check("algorithm_error_preserved_without_silent_fallback",rejected);Check("algorithm_error_releases_components",Writable(component));
            string afterError=Run(parent,two,CancellationToken.None).GetAwaiter().GetResult();Check("explicit_post_error_record_has_no_old_result",Same(Json().DeserializeObject(afterError),Json().DeserializeObject(reference2)));
            Check("idle_release_accepted",(bool)runtime.GetMethod("ReleaseIdleWorker",S,null,Type.EmptyTypes,null).Invoke(null,null));Wait(()=>Writable(component));Check("idle_release_releases_component_locks",Writable(component));
            string bad=Path.Combine(qa,"broken-runtime"),badRoot=Path.Combine(bad,"r71");Directory.CreateDirectory(badRoot);
            foreach(string file in Directory.GetFiles(Path.Combine(parent,"r71"),"*",SearchOption.AllDirectories)){string relative=file.Substring(Path.Combine(parent,"r71").Length+1),target=Path.Combine(badRoot,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target);}
            using(var append=new FileStream(Path.Combine(badRoot,Path.GetFileName(component)),FileMode.Append,FileAccess.Write))append.WriteByte(0);
            bool broken=false;try{Run(bad,one,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException error){broken=error.Message.Contains("component changed");}Check("broken_pin_rejected_before_launch",broken&&WorkerProcess()==null);
            string last=Run(parent,one,CancellationToken.None).GetAwaiter().GetResult();Check("good_runtime_still_matches_after_bad_pin",Same(Json().DeserializeObject(last),Json().DeserializeObject(reference1)));
            Invoke("Shutdown");Wait(()=>Writable(component));bool stopped=false;try{Run(parent,one,CancellationToken.None).GetAwaiter().GetResult();}catch(ObjectDisposedException){stopped=true;}Check("shutdown_releases_and_rejects_new_requests",stopped&&Writable(component));
            Check("both_user_LOCs_unchanged",before1==Hash(first)&&before2==Hash(second));
            File.WriteAllText(Path.Combine(qa,"offline-proof.json"),Json().Serialize(new{schema="HVX.R58.RetainedAlgorithmWorkerOffline/1",passed=true,module=dll,module_sha256=Hash(dll),runtime_parent=parent,loc1=first,loc1_sha256=before1,loc2=second,loc2_sha256=before2,timings=timings,checks=checks,product_started=false,ui_constructed=false,hardware_sdk_called=false,user_project_written=false,algorithm_rules_changed=false}),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(qa,"first-result.json"),firstResult,new UTF8Encoding(false));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);if(qa!=null&&Directory.Exists(qa))File.WriteAllText(Path.Combine(qa,"failure.txt"),error.ToString(),new UTF8Encoding(false));return 1;}
        finally{if(runtime!=null)try{Invoke("Shutdown");}catch{ }}
    }
}
