using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.FactoryTest;

// Files/fake actions only. Never starts the product, worker, SDK or a device.
internal static class UnifiedResidentLaunchEvidenceSmoke
{
    static int checks;
    static void Check(bool ok,string detail){checks++;if(!ok)throw new Exception(detail);}
    static UnifiedResidentLaunchRecord Load(string path)
    {using(var stream=File.OpenRead(path))return (UnifiedResidentLaunchRecord)new XmlSerializer(typeof(UnifiedResidentLaunchRecord)).Deserialize(stream);}
    static void Save(string path,UnifiedResidentLaunchRecord value)
    {using(var stream=File.Create(path))new XmlSerializer(typeof(UnifiedResidentLaunchRecord)).Serialize(stream,value);}
    static int Main(string[] args)
    {
        try {
            string root=Path.Combine(Path.GetFullPath(args[0]),"resident-start-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string settings=Path.Combine(root,"session-settings.xml"),worker=Path.Combine(root,"synthetic-worker.bin");
            File.WriteAllText(settings,"<synthetic>no-device-values</synthetic>");File.WriteAllText(worker,"not-an-executable");
            var request=UnifiedResidentLaunchEvidence.Create(root,settings,123,worker);
            string proof=Path.Combine(root,UnifiedResidentLaunchEvidence.ProofFile);
            Func<int,bool,int,UnifiedResidentLaunchRecord> verify=(pid,exited,code)=>UnifiedResidentLaunchEvidence.VerifyNeverEntered(request,settings,request.WorkerSHA256,pid,exited,code);
            Check(verify(456,true,2)==null,"exit 2 alone does not release");
            UnifiedResidentLaunchEvidence.WriteRejectedBeforeNative(root,settings,123,456,worker,UnifiedResidentLaunchEvidence.Canceled);
            Check(verify(456,true,2)!=null,"full child proof releases never-entered launch");
            Check(verify(456,false,2)==null,"live worker never releases");
            Check(verify(456,true,0)==null&&verify(456,true,1)==null,"only designated wrapper rejection exit accepted");
            Check(verify(457,true,2)==null,"different worker rejected");
            var original=Load(proof);
            Action<string,Action<UnifiedResidentLaunchRecord>> reject=(label,change)=>{var altered=Load(proof);change(altered);Save(proof,altered);Check(verify(456,true,2)==null,label);Save(proof,original);};
            reject("schema mismatch",r=>r.Schema="old");
            reject("nonce replay",r=>r.Nonce=Guid.NewGuid().ToString("N"));
            reject("parent mismatch",r=>r.ParentProcessId=999);
            reject("worker mismatch",r=>r.WorkerProcessId=999);
            reject("directory mismatch",r=>r.Directory=Path.Combine(root,"other"));
            reject("settings hash mismatch",r=>r.SettingsSHA256=new string('0',64));
            reject("worker image hash mismatch",r=>r.WorkerSHA256=new string('0',64));
            reject("unknown reason",r=>r.Reason="EnteredNative");
            Check(UnifiedResidentLaunchEvidence.VerifyNeverEntered(request,settings,new string('0',64),456,true,2)==null,"expected image binding mismatch");
            File.WriteAllText(settings,"changed");Check(verify(456,true,2)==null,"current settings changed");
            File.WriteAllText(settings,"<synthetic>no-device-values</synthetic>");
            foreach(var name in new[]{UnifiedResidentLaunchEvidence.EnteredFile,"ready.flag","progress.log","session-fault.xml","cleanup-fault.xml","cleanup-error.txt","closed.flag","error.txt"}) {
                string path=Path.Combine(root,name);File.WriteAllText(path,"synthetic");Check(verify(456,true,2)==null,"contradictory evidence wins: "+name);File.Delete(path);
            }
            File.WriteAllText(proof,"<bad>");Check(verify(456,true,2)==null,"malformed proof");
            File.WriteAllText(proof,"<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///not-accessed'>]><x>&a;</x>");Check(verify(456,true,2)==null,"DTD rejected");
            File.WriteAllText(proof,new string('x',17000));Check(verify(456,true,2)==null,"bounded proof read");
            Save(proof,original);
            foreach(var reason in new[]{UnifiedResidentLaunchEvidence.Canceled,UnifiedResidentLaunchEvidence.ParentUnavailable,UnifiedResidentLaunchEvidence.HeartbeatInvalid,UnifiedResidentLaunchEvidence.GuardFailed}) {
                File.Delete(proof);
                UnifiedResidentLaunchEvidence.WriteRejectedBeforeNative(root,settings,123,456,worker,reason,new IOException("synthetic detail not saved"));
                Check(verify(456,true,2)!=null,"preflight outcome bound: "+reason);
                Check(Load(proof).FailureType==typeof(IOException).FullName,"bounded exception type retained, no settings/detail dump");
            }
            File.Delete(proof);
            UnifiedResidentLaunchEvidence.MarkServerEntry(root,settings,123,456,worker);
            bool refused=false;try{UnifiedResidentLaunchEvidence.WriteRejectedBeforeNative(root,settings,123,456,worker,UnifiedResidentLaunchEvidence.GuardFailed);}catch(InvalidDataException){refused=true;}
            Check(refused&&verify(456,true,2)==null,"server entry exception cannot become never-native");
            File.Delete(Path.Combine(root,UnifiedResidentLaunchEvidence.EnteredFile));
            File.WriteAllText(worker,"modified-image");
            refused=false;try{UnifiedResidentLaunchEvidence.WriteRejectedBeforeNative(root,settings,123,456,worker,UnifiedResidentLaunchEvidence.Canceled);}catch(InvalidDataException){refused=true;}
            Check(refused,"worker image change cannot write child proof");
            File.WriteAllText(worker,"not-an-executable");
            var canceled=new CancellationToken(true);int starts=0,waits=0,closes=0;
            Func<Task> close=()=>{closes++;return Task.CompletedTask;};
            try {FactoryResidentLaunchLifecycle.StartAsync(()=>starts++,()=>{waits++;return Task.CompletedTask;},close,canceled).GetAwaiter().GetResult();}catch(OperationCanceledException){}
            Check(starts==0&&waits==0&&closes==1,"cancel before Start never launches and cleans only unstarted owner");
            var startError=new IOException("synthetic Start failure");Exception captured=null;
            try {FactoryResidentLaunchLifecycle.StartAsync(()=>{starts++;throw startError;},()=>{waits++;return Task.CompletedTask;},close,CancellationToken.None).GetAwaiter().GetResult();}catch(Exception error){captured=error;}
            Check(ReferenceEquals(captured,startError)&&starts==1&&waits==0&&closes==2,"Start exception preserved; no WaitReady");
            var readyError=new IOException("synthetic preflight failure");var cleanupError=new IOException("synthetic release uncertainty");
            try {FactoryResidentLaunchLifecycle.StartAsync(()=>starts++,()=>Task.FromException(readyError),()=>Task.FromException(cleanupError),CancellationToken.None).GetAwaiter().GetResult();}catch(Exception error){captured=error;}
            var aggregate=captured as AggregateException;
            Check(aggregate!=null&&ReferenceEquals(aggregate.InnerExceptions[0],readyError)&&ReferenceEquals(aggregate.InnerExceptions[1],cleanupError),"original startup and cleanup failures both retained in order");
            captured=null;try {FactoryResidentLaunchLifecycle.StartAsync(()=>starts++,()=>Task.FromException(readyError),close,CancellationToken.None).GetAwaiter().GetResult();}catch(Exception error){captured=error;}
            Check(ReferenceEquals(captured,readyError),"successful cleanup does not suppress startup failure");
            int beforeClose=closes;
            FactoryResidentLaunchLifecycle.StartAsync(()=>starts++,()=>Task.CompletedTask,close,CancellationToken.None).GetAwaiter().GetResult();
            Check(closes==beforeClose,"healthy startup not closed");
            string wrapper=File.ReadAllText(Path.Combine(args[1],"modules-src","FactoryWorker","UnifiedWorkerEntry.cs"));
            int boundary=wrapper.IndexOf("UnifiedResidentLaunchEvidence.MarkServerEntry",StringComparison.Ordinal),entered=wrapper.IndexOf("residentEntered=true",boundary,StringComparison.Ordinal),server=wrapper.IndexOf("return RunResidentCore",entered,StringComparison.Ordinal);
            Check(boundary>=0&&entered>boundary&&server>entered,"actual wrapper marks conservative boundary before server entry");
            Check(wrapper.Contains("if(resident&&!residentEntered&&directory!=null&&parent>0)"),"actual wrapper cannot emit proof after server entry");
            string client=File.ReadAllText(Path.Combine(args[1],"modules-src","FactoryWorker","FactoryResidentDevices.cs"));
            Check(client.Contains("FactoryResidentLaunchLifecycle.StartAsync(starting.Start"),"actual client uses tested startup error ordering");
            Check(client.Contains("UnifiedResidentLaunchEvidence.VerifyNeverEntered(launchEvidence,settingsPath"),"actual close requires bound child proof");
            Check(client.Contains("WorkerExitCode="),"real exit code retained in diagnostics");
            Console.WriteLine("UNIFIED_RESIDENT_LAUNCH_EVIDENCE_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0; ProductStarted=false; Fixture="+root);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
