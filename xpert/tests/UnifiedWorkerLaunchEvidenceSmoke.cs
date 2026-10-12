using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using HVX.Acquisition;
using Xiongxin.FactoryTest;

// Protocol files only. Does not spawn a process or call wrapper/SDK entry.
internal static class UnifiedWorkerLaunchEvidenceSmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static void Save(string path,UnifiedWorkerLaunchRecord value){using(var w=File.Create(path))new XmlSerializer(typeof(UnifiedWorkerLaunchRecord)).Serialize(w,value);}
    static UnifiedWorkerLaunchRecord Load(string path){using(var r=File.OpenRead(path))return (UnifiedWorkerLaunchRecord)new XmlSerializer(typeof(UnifiedWorkerLaunchRecord)).Deserialize(r);}
    static int Main(string[] args){try{
        string root=Path.Combine(Path.GetFullPath(args[0]),"launch-evidence-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string settings=Path.Combine(root,"factory-settings.xml");File.WriteAllText(settings,"<synthetic>not-device-settings</synthetic>");
        var request=UnifiedWorkerLaunchEvidence.Create(root,settings,123);string proof=Path.Combine(root,UnifiedWorkerLaunchEvidence.ProofFile);
        Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2),"exit2 without proof stays unknown");
        UnifiedWorkerLaunchEvidence.WriteCanceledBeforeNative(root,settings,123,456);
        Check(UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2),"current nonce+hash+directory+parent+worker+exit2 match");
        Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,false,2),"live worker cannot release");
        Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,1),"other exit code cannot be relabeled");
        Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,457,true,2),"other worker PID rejected");
        var original=Load(proof);
        Action<string,Action<UnifiedWorkerLaunchRecord>> reject=(name,change)=>{var p=Load(proof);change(p);Save(proof,p);Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2),name);Save(proof,original);};
        reject("old nonce replay rejected",p=>p.Nonce=Guid.NewGuid().ToString("N"));
        reject("different parent PID rejected",p=>p.ParentProcessId=999);
        reject("another directory rejected",p=>p.Directory=Path.Combine(root,"other"));
        reject("changed configuration hash rejected",p=>p.SettingsSHA256=new string('0',64));
        reject("SDK-entered outcome rejected",p=>p.Reason="EnteredNative");
        reject("invalid guard outcome is not operator cancellation",p=>p.Reason="InvalidHeartbeat");
        reject("unknown schema rejected",p=>p.Schema="other");
        File.WriteAllText(settings,"changed-config");Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2),"settings mutation after proof rejected");
        File.WriteAllText(settings,"<synthetic>not-device-settings</synthetic>");
        File.WriteAllText(proof,"<bad>");Check(!UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2),"malformed proof rejected");
        string wrapper=File.ReadAllText(Path.Combine(args[1],"modules-src","FactoryWorker","UnifiedWorkerEntry.cs"));
        Check(wrapper.IndexOf("WriteCanceledBeforeNative",StringComparison.Ordinal)<wrapper.IndexOf("return Program.Main(args)",StringComparison.Ordinal),"proof emission precedes native-capable entry");
        Check(wrapper.Contains("if (File.Exists(Path.Combine(directory,resident?\"close.flag\":\"cancel.flag\")))"),"only cancellation guard emits wrapper proof");
        string parent=File.ReadAllText(Path.Combine(args[1],"modules-src","FactoryWorker","Program.cs"));
        int create=parent.IndexOf("var launchEvidence=UnifiedWorkerLaunchEvidence.Create",StringComparison.Ordinal);
        int cancel=parent.IndexOf("if(token.IsCancellationRequested)throw new FactoryNativeNotStartedException",create,StringComparison.Ordinal);
        int start=parent.IndexOf("process.Start();",create,StringComparison.Ordinal);
        Check(create>=0&&cancel>create&&start>cancel,"parent prelaunch cancellation precedes process creation");
        Save(proof,original);
        long now=0;int waited=0;
        var cadence=new PicoCaptureCadence(Path.Combine(root,"cadence"),()=>now,1000,(ms,token)=>{token.ThrowIfCancellationRequested();now+=ms;waited+=ms;return Task.CompletedTask;});
        using(var lease=cadence.EnterAsync("synthetic-owned",CancellationToken.None).GetAwaiter().GetResult()) {
            lease.BeginCapture();
            if(UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2))lease.Released(0);
        }
        Check(File.ReadAllText(cadence.StatePath("synthetic-owned")).StartsWith("RELEASED|"),"verified never-native current lease does not become BLOCKED");
        using(var lease=cadence.EnterAsync("synthetic-owned",CancellationToken.None).GetAwaiter().GetResult())
            Check(waited==2000,"benign pre-native cancel does not bypass the cadence cooldown");
        using(var lease=cadence.EnterAsync("synthetic-unknown",CancellationToken.None).GetAwaiter().GetResult()) {
            lease.BeginCapture();
            if(UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,999,true,2))lease.Released(0);
        }
        string blocked=File.ReadAllText(cadence.StatePath("synthetic-unknown"));
        Check(blocked.StartsWith("BLOCKED|"),"unknown worker exit retains fail-closed ownership");
        bool rejected=false;
        try {using(var lease=cadence.EnterAsync("synthetic-unknown",CancellationToken.None).GetAwaiter().GetResult()) {
            // A previous proof cannot grant an active lease: Enter rejects before this branch.
            if(UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(request,settings,456,true,2))lease.Released(0);
        }}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&File.ReadAllText(cadence.StatePath("synthetic-unknown"))==blocked,"old proof cannot erase a previous BLOCKED session");
        Console.WriteLine("UNIFIED_WORKER_LAUNCH_EVIDENCE_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0; ProductStarted=false; Fixture="+root);return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
