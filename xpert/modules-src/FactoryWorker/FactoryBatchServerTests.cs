using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryBatchServerTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Batch server: "+why);checks++;};
            using(var f=new Fixture())
            {
                var first=Request(2);FactorySettings retained=null;
                f.Round=(request,session)=>
                {
                    Interlocked.Increment(ref f.RoundCount);
                    var changed=FactorySettings.Load(request.SettingsPath);changed.TargetKv+=1;changed.Save(request.SettingsPath);
                    check(request.FrozenSettings.TargetKv==first.TargetKv,"file mutation after acceptance cannot replace frozen execution settings");
                    retained=request.FrozenSettings;
                    return Finish(request,session.SessionId);
                };
                f.Start();f.WaitReady();string one=f.Submit("one",first);f.WaitResult(one);
                check(File.ReadAllText(Path.Combine(one,"session-result.txt"))=="0","first batch round completes after managed native/archive receipt");
                check(retained!=null && !retained.BatchPermit.Matches(retained),"server revokes each completed one-use permit");
                string two=f.Submit("two",FactoryBatchAuthorizationTests.Next(first,2));f.WaitResult(two);
                check(f.RoundCount==2 && f.Session.Opens==1 && f.Session.Disposes==0,"normal second batch round reuses one healthy device owner");
                check(File.ReadAllText(Path.Combine(two,"session-result.txt"))=="0","final batch round succeeds without another start assertion");
            }
            using(var f=new Fixture())
            using(var entered=new ManualResetEvent(false))
            {
                var first=Request(2);
                f.Round=(request,session)=>
                {
                    int number=Interlocked.Increment(ref f.RoundCount);
                    if(number!=1)return Finish(request,session.SessionId);
                    request.FrozenSettings.BatchPermit.ConsumeForNative(session.SessionId,request.FrozenSettings);
                    entered.Set();var elapsed=Stopwatch.StartNew();
                    while(!File.Exists(Path.Combine(request.Directory,"cancel.flag")))
                    {if(elapsed.ElapsedMilliseconds>5000)throw new TimeoutException("fake cancel propagation");Thread.Sleep(5);}
                    request.FrozenSettings.BatchPermit.MarkNativeEnded();return 1;
                };
                f.Start();f.WaitReady();string cancelled=f.Submit("cancelled",first);
                f.Wait(()=>entered.WaitOne(0),"fake round entered");f.Cancel();f.WaitResult(cancelled);
                check(File.ReadAllText(Path.Combine(cancelled,"session-result.txt"))=="1","Stop never publishes a successful batch round");
                check(f.Session.Disposes==0 && f.Exit==-999,"normal Stop retains healthy handles in read-only idle");
                var restarted=Request(1);restarted.SequenceId="fresh-explicit-start";
                string fresh=f.Submit("fresh",restarted);f.WaitResult(fresh);
                check(File.ReadAllText(Path.Combine(fresh,"session-result.txt"))=="0" && f.RoundCount==2,"fresh explicit start can replace cancelled batch authorization");
                check(!File.Exists(Path.Combine(f.Directory,"batch.cancel")) && System.IO.Directory.GetFiles(f.Directory,"batch.cancel.accepted.*").Length==1,
                    "only explicit start acknowledges old cancel sentinel with retained audit file");
            }
            using(var f=new Fixture())
            {
                var first=Request(2);
                f.Start();f.WaitReady();f.WaitResult(f.Submit("first",first));f.Cancel();
                f.Wait(()=>f.ProgressContains("BATCH_REVOKED"),"idle cancellation observed");
                string denied=f.Submit("denied-continuation",FactoryBatchAuthorizationTests.Next(first,2));f.WaitExit();
                check(f.RoundCount==1 && File.Exists(Path.Combine(f.Directory,"batch.cancel")),"continuation cannot acknowledge or clear a Stop sentinel");
                check(File.ReadAllText(Path.Combine(denied,"session-result.txt"))!="0","stopped continuation cannot manufacture success");
            }
            using(var f=new Fixture())
            {
                var first=Request(2);
                f.Start();f.WaitReady();f.WaitResult(f.Submit("first",first));f.Submit("replayed-start",first);f.WaitExit();
                check(f.RoundCount==1 && f.Exit!=0,"new request GUID cannot replay an old batch start attempt");
            }
            using(var f=new Fixture())
            {
                f.Round=(request,session)=>
                {
                    Interlocked.Increment(ref f.RoundCount);
                    request.FrozenSettings.BatchPermit.ConsumeForNative(session.SessionId,request.FrozenSettings);
                    request.FrozenSettings.BatchPermit.MarkNativeEnded();return 0;
                };
                f.Start();f.WaitReady();string incomplete=f.Submit("missing-receipt",Request(2));f.WaitExit();
                check(f.RoundCount==1 && f.Exit!=0 && File.ReadAllText(Path.Combine(incomplete,"session-result.txt"))!="0",
                    "native cleanup without a successful archive receipt never commits a batch round");
            }
            return "Batch server: "+checks+" real RunCore fake-device checks PASS; frozen settings/2 rounds/Stop idle/restart/replay/missing receipt; HardwareAccessed=False";
        }
        private static int Finish(FactorySessionRequest request,string sessionId)
        {
            var issued=request.FrozenSettings;issued.BatchPermit.ConsumeForNative(sessionId,issued);
            issued.BatchPermit.MarkNativeEnded();
            var archived=FactoryBatchAuthorizationTests.Capture(issued);archived.DirectoryPath=request.Directory;
            Program.SaveResult(archived,request.Directory,false);request.ArchivedResult=archived;return 0;
        }
        private static FactorySettings Request(int count)
        {var settings=FactoryBatchAuthorizationTests.Request(count);settings.Input.ArtSerial="1023417009";return settings;}
        private sealed class Fixture : IDisposable
        {
            internal readonly string Directory=Path.Combine(Path.GetTempPath(),"Xiongxin-Factory-BatchServer-"+Guid.NewGuid().ToString("N"));
            internal readonly FakeSession Session=new FakeSession();
            internal Func<FactorySessionRequest,IFactoryResidentSession,int> Round;
            internal int Exit=-999,RoundCount;private Thread worker;private Exception error;
            internal Fixture()
            {
                System.IO.Directory.CreateDirectory(Directory);
                Request(2).Save(Path.Combine(Directory,"session-settings.xml"));
                Round=(request,session)=>{Interlocked.Increment(ref RoundCount);return Finish(request,session.SessionId);};
            }
            internal void Start()
            {
                Beat();worker=new Thread(()=>{try{Exit=FactoryResidentDeviceServer.RunCore(
                    new[]{"--device-session",Path.Combine(Directory,"session-settings.xml"),Directory,Process.GetCurrentProcess().Id.ToString()},
                    ()=>Session,Round,()=>new FakeLease());}catch(Exception ex){error=ex;}}){IsBackground=true};worker.Start();
            }
            internal void WaitReady(){Wait(()=>File.Exists(Path.Combine(Directory,"ready.flag")),"ready");}
            internal string Submit(string name,FactorySettings settings)
            {
                string record=Path.Combine(Directory,name);System.IO.Directory.CreateDirectory(record);
                string path=Path.Combine(record,"factory-settings.xml");settings.Save(path);
                var request=new FactorySessionRequest {Id=Guid.NewGuid().ToString("N"),Command="Pressure",SettingsPath=path,Directory=record};
                string pending=Path.Combine(Directory,"request.pending.xml");
                using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write))new XmlSerializer(typeof(FactorySessionRequest)).Serialize(stream,request);
                File.Move(pending,Path.Combine(Directory,"request.xml"));return record;
            }
            internal void Cancel(){File.WriteAllText(Path.Combine(Directory,"batch.cancel"),"offline explicit Stop");}
            internal void WaitResult(string record){Wait(()=>File.Exists(Path.Combine(record,"session-result.txt")),"round result");}
            internal void WaitExit(){Wait(()=>!worker.IsAlive,"server exit");if(error!=null)throw error;}
            internal bool ProgressContains(string text)
            {
                string path=Path.Combine(Directory,"progress.log");if(!File.Exists(path))return false;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                using(var reader=new StreamReader(stream))return reader.ReadToEnd().Contains(text);
            }
            internal void Wait(Func<bool> condition,string why)
            {
                var elapsed=Stopwatch.StartNew();while(!condition())
                {if(error!=null)throw error;if(elapsed.ElapsedMilliseconds>12000)throw new TimeoutException("Batch server fixture "+why+"; "+Directory);Beat();Thread.Sleep(10);}
            }
            private void Beat(){File.WriteAllText(Path.Combine(Directory,"session.heartbeat"),"offline parent alive");}
            public void Dispose()
            {File.WriteAllText(Path.Combine(Directory,"close.flag"),"offline close");if(worker!=null && worker.IsAlive)worker.Join(6000);}
        }
        private sealed class FakeSession : IFactoryResidentSession
        {
            internal int Opens,Disposes;private int owner;
            public string SessionId {get{return "OFFLINE-BATCH-SESSION";}}
            public bool Faulted {get{return false;}}
            public bool CanReuseSession {get{return Disposes==0;}}
            public Action<string,string> ConnectionReport {set{}}
            public void OpenSession(FactorySettings settings){owner=Thread.CurrentThread.ManagedThreadId;Opens++;}
            public void PollIdle(){AssertOwner();}
            public void EndRound(){AssertOwner();}
            public void DetachRoundCallbacks(){AssertOwner();}
            public void Dispose(){AssertOwner();Disposes++;}
            private void AssertOwner(){if(owner!=Thread.CurrentThread.ManagedThreadId)throw new Exception("Fake SDK escaped serialized owner");}
        }
        private sealed class FakeLease : IDisposable{public void Dispose(){}}
    }
}
