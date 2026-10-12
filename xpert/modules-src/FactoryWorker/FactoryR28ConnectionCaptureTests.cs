using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryR28ConnectionCaptureTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("V1.0.28: "+why);++n;};
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-R28-LinkCapture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var s=new FactoryTests.Fixture().Settings;s.EndAfterPressureCapture=true;s.Input.OscillationCircuitConnected=true;
            s.Input.CaptureDurationMs=60;s.Input.RequestedPicoRateHz=1000000;s.Input.RangeIndex=6;
            foreach(int percent in new[]{0,1,25,50,75,99,100})
            {
                var request=s.Clone();request.Input.PreTriggerPercent=percent;request.Validate(FactoryRunKind.Pressure);
                check(request.Input.ArtPreTriggerCount==48*percent && request.Input.PicoPreTriggerCount==600*percent,"exact shared pretrigger counts within 60 ms");
                var clock=new FactoryTests.FakeClock();var backend=new SimulationFactoryBackend(clock);
                var round=FactoryRunner.Run(request,FactoryRunKind.Pressure,backend,clock,(k,t)=>{},CancellationToken.None);
                round.Capture.ValidateCapture();check(round.Capture.Settings.PreTriggerPercent==percent,"requested split archived");
                if(percent==100)check(!round.Metrics.ReferenceAnalysis.PhaseValid,"DC-only has no invented first zero");
                if(percent<=50)
                {
                    check(round.Metrics.ReferenceAnalysis.PhaseValid,"post-trigger phase valid");
                    check(Math.Abs(round.Metrics.ReferenceAnalysis.FirstZeroMs-(percent*.6+.625))<.08,"zero mapped into full-record time");
                    var q=FactoryRangeReview.EvaluateCapturedData(round);
                    check(q.WindowValid && q.WindowStartSample>=round.Capture.Settings.PicoPreTriggerCount,"range starts after switch");
                    var original=(short[])round.Capture.PdAdc.Clone();
                    round.Capture.PdAdc[round.Capture.Settings.PicoPreTriggerCount+10]=32767;
                    q=FactoryRangeReview.EvaluateCapturedData(round);
                    check(q.OutsideCycleClippingExcluded,"known switch clipping outside N-cycle window excluded");
                    if(percent>0){round.Capture.PdAdc[1]=32767;q=FactoryRangeReview.EvaluateCapturedData(round);check(!q.SwitchingTransientExcluded && q.OutsideCycleClippingExcluded,"pretrigger clipping outside N window retained, not falsely called switching transient");}
                    round.Capture.PdAdc=original;
                }
            }
            check(new BenchSettings().PreTriggerPercent==0,"old records all post-trigger");
            foreach(double duration in new[]{45.0,50,60})
            {
                var request=s.Clone();request.Input.CaptureDurationMs=duration;request.Input.PreTriggerPercent=50;
                var clock=new FactoryTests.FakeClock();
                var round=FactoryRunner.Run(request,FactoryRunKind.Pressure,new SimulationFactoryBackend(clock),clock,(k,t)=>{},CancellationToken.None);
                check(round.Metrics.FrequencyValid && Math.Abs(round.Metrics.FrequencyHz-400)<1,"finite midpoint record frequency");
                check(FactoryRangeReview.EvaluateCapturedData(round).WindowValid,"default eight-cycle midpoint range window");
            }
            foreach(int invalid in new[]{-1,101})
            {var bad=s.Clone();bad.Input.PreTriggerPercent=invalid;Reject(()=>bad.Validate(FactoryRunKind.Pressure));n++;}
            var misaligned=s.Clone();misaligned.Input.CaptureDurationMs=59.0125;misaligned.Input.RequestedPicoRateHz=125000000;misaligned.Input.PreTriggerPercent=50;
            Reject(()=>misaligned.Validate(FactoryRunKind.Pressure));n++;
            var tooLong=s.Clone();tooLong.Input.CaptureDurationMs=1000;tooLong.Input.PreTriggerPercent=50;Reject(()=>tooLong.Validate(FactoryRunKind.Pressure));n++;

            var fixture=(FactoryResult)typeof(FactoryPostProcessingTests).GetMethod("Round",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Path.Combine(root,"pulse"),1000000.0,20.0});
            var add=typeof(FactoryPostProcessingTests).GetMethod("AddPulse",BindingFlags.Static|BindingFlags.NonPublic);
            add.Invoke(null,new object[]{fixture.Capture,1000,10000.0});add.Invoke(null,new object[]{fixture.Capture,10000,5000.0});
            var selected=FactoryTfAnalysis.ExtractSelected(fixture,100,5,19);
            check(selected.Pulses.Count==1 && FactoryTfAnalysis.ExtractSelected(fixture,100,.1,19).Pulses.Count==2,"manual ROI includes transition independent of range");
            foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,-1,5,21})
            {selected.SelectionEndMs=invalid;Reject(selected.Validate);n++;}
            selected.SelectionEndMs=19;selected.SelectionStartMs=11;Reject(selected.Validate);n++;
            selected.SelectionStartMs=5;selected.Validate();check(true,"valid saved ROI accepted");
            selected.HasSelectionWindow=false;selected.SelectionStartMs=Double.NaN;selected.Validate();check(true,"legacy non-ROI metadata preserved");

            var journal=new FactoryConnectionJournal(root,"1023417009","13000/0059");journal.TaskName="连续加压 第3/6轮";journal.Progress="升压条件";
            var at=DateTime.UtcNow;journal.Fault("ART",at,"读取失败 code=-1");journal.Fault("ART",at.AddSeconds(1),"重复读取失败");
            check(journal.Incidents==1 && journal.Lines.Count==1,"one outage not a flood");
            journal.Fault("Pico",at.AddSeconds(1),"设备不可用");journal.Online("ART",at.AddSeconds(3));journal.Online("ART",at.AddSeconds(4));
            check(journal.Lines.Count==3 && journal.Lines[2].Contains("3.000"),"independent recovery timestamp/duration");
            journal.Result("关断调用报错；退出队列，未确认物理输出");
            string saved=File.ReadAllText(journal.FilePath);check(saved.Contains("1023417009") && saved.Contains("13000/0059") && saved.Contains("升压条件") && saved.Contains("未确认物理输出"),"serial/task/phase/result durable");
            check(Path.GetFullPath(journal.FilePath).StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"serial slash cannot escape directory");
            var progress=new FactorySequenceProgress(s,6);progress.Begin(s,6);progress.Starting(s);progress.Observe("CONNECTION_LOST");Reject(progress.Captured);progress.End();progress.RememberStoppedUi(s);
            check(progress.CanResume && progress.Completed==0 && progress.Matches(s,6),"communication failure checkpoint, not completion");
            progress.Observe("LINK_ART_HIGH");check(!progress.Running,"reconnection does not resume output");
            var changed=s.Clone();changed.Input.PreTriggerPercent=50;check(!progress.Matches(changed,6),"slider change disables old-sequence continue");
            var between=new FactorySequenceProgress(s,6);between.Begin(s,6);between.Starting(s);between.Captured();between.Prepared(s);
            between.Observe("CONNECTION_LOST");between.End();between.RememberStoppedUi(s);
            check(between.CanResume && between.Completed==1 && between.NextRound==2,"between-round disconnect preserves remaining checkpoint");
            var input=new BenchSettings {RealHardware=true,ArtSerial="123",PicoSerial="pico-123"};
            var found=new DiscoveryResult();found.ArtDevices.Add(new ArtDeviceIdentity {Name="Dev2",Serial="123",Model="USB3135"});found.PicoSerials.Add("pico-123");
            var native=new FakeInput();var events=new List<string>();
            using(var stop=new CancellationTokenSource())
            {
                FactoryConnectionWatch.Run(input,(k,t)=>{events.Add(k);if(k=="LINK_ART_LOW")stop.Cancel();},stop.Token,native,()=>found);
            }
            check(events.Contains("LINK_ART_HIGH") && events.Contains("LINK_ART_LOW") && !events.Contains("LINK_ART_ERROR"),"low P0.4 means emergency, not disconnect");
            check(native.OpenedName=="Dev2" && native.Closed,"same serial on remapped module; input handle released");
            native=new FakeInput {Fail=true};events.Clear();
            using(var stop=new CancellationTokenSource())
            {FactoryConnectionWatch.Run(input,(k,t)=>{events.Add(k);if(k=="LINK_ART_ERROR")stop.Cancel();},stop.Token,native,()=>found);}
            check(events.Contains("LINK_ART_ERROR") && !events.Contains("LINK_ART_LOW"),"driver read exception distinct from emergency");
            check(native.Closed,"failed input owner always cleaned");
            var picoOnly=new DiscoveryResult();picoOnly.PicoSerials.Add(input.PicoSerial);int picoHeartbeats=0;
            using(var stop=new CancellationTokenSource(5000))
            {FactoryConnectionWatch.Run(input,(k,t)=>{if(k=="LINK_PICO_ONLINE" && ++picoHeartbeats==2)stop.Cancel();},stop.Token,new FakeInput(),()=>picoOnly);}
            check(picoHeartbeats==2,"Pico heartbeat refreshes independently when ART absent");
            string progressFile=Path.Combine(root,"progress.log");long offset=0;var delivered=new List<string>();
            var bytes=System.Text.Encoding.UTF8.GetBytes("time\tLINK_ART_HIGH\t中文状态\nnext\tLINK_ART_LOW\t急停\n");
            int partial=Array.IndexOf(bytes,(byte)10)+8;
            using(var f=File.Create(progressFile))f.Write(bytes,0,partial);
            Program.Drain(progressFile,(k,t)=>delivered.Add(k+":"+t),ref offset);
            check(delivered.Count==1 && delivered[0].EndsWith("中文状态"),"tail reader yields only complete UTF-8 lines");
            using(var f=new FileStream(progressFile,FileMode.Append))f.Write(bytes,partial,bytes.Length-partial);
            Program.Drain(progressFile,(k,t)=>delivered.Add(k+":"+t),ref offset);Program.Drain(progressFile,(k,t)=>delivered.Add(k),ref offset);
            check(delivered.Count==2 && offset==bytes.Length && delivered[1].EndsWith("急停"),"tail no duplicates or discarded partial lines");
            n+=FactoryMainForm.TestConnectionDisplay(root);
            return "V1.0.28 connection/real-pretrigger contracts/ROI: "+n+" checks PASS; fake SDK + synthetic data only; HardwareAccessed=False; "+root;
        }
        private static void Reject(Action action){bool rejected=false;try{action();}catch{rejected=true;}if(!rejected)throw new Exception("invalid case accepted");}
        private sealed class FakeInput:IP04ReadOnlyNative
        {
            internal bool Fail,Closed;internal string OpenedName;private int reads;
            public string Load(string path){return "FAKE";}public string Identity(string device){return "USB3135 / 123";}
            public void OpenLine(string device,int group){OpenedName=device;}
            public P04RawRead ReadLine(){if(Fail)throw new IOException("simulated USB disconnect");return new P04RawRead {Status=0,Samples=1,Bytes=1,Raw=++reads<2?1u:0u};}
            public P04RawRead ReadPort(string device){throw new Exception("port-wide read not expected");}
            public void CloseLine(){Closed=true;}
        }
    }
}
