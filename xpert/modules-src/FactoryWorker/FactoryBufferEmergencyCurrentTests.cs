using System;
using System.Collections.Generic;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // Local derivative: upstream buffer/HV assertions retained verbatim; only
    // the superseded P0.4 monitor timing block is tested by the new contract.
    internal static class FactoryBufferEmergencyCurrentTests
    {
        internal static string Run()
        {
            int n = 0;
            Action<bool,string> check = (ok,detail) => { if (!ok) throw new Exception("R10 regression: " + detail); n++; };
            var plan = new FactoryArtFeedbackPlan(new FactorySettings { TargetKv = 5, MaximumKv = 5 });
            var buffer = new Buffer(1,64,64,0);
            buffer.Values = (read,raw) => { raw[0] = read == 1 ? 4.9 : 0; raw[1] = read == 1 ? .02 : 0;
                raw[2] = read == 1 ? .03 : 0; if (read > 1) raw[raw.Length - 1] = .001; };
            var f = FactoryFeedbackStream.Read(plan,buffer,.3,() => 0);
            check(f.ReadBatchCount == 2 && f.SampleCount == 74 && f.BacklogBefore == 1 && f.BacklogAfter == 0,"10-point window then 64 queued points drained, retaining all extrema");
            check(f.ImonMaximumRawV == 4.9 && f.VmonMaximumRawV == .02 && f.DividerMaximumRawV == .03 && f.DividerRawV == .001,"all previous current/voltage peaks survive cache draining");
            check(f.AoV == .3 && FactoryFeedbackBounds.ImonAbs(f) > 4.9,"command and conservative peak bound retained");
            Expect(() => plan.Parse(new double[3],1,64,true,0)); n++;
            buffer = new Buffer(1,64,64,32,32,4);
            f = FactoryFeedbackStream.Read(plan,buffer,0,() => 0);
            check(f.ReadBatchCount == 3 && f.SampleCount == 106 && f.BacklogAfter == 4,"successive queued USB-sized batches handled with minimum feedback window");
            buffer = new Buffer(1001); var caught = Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0));
            check(buffer.Reads == 0 && caught.Message.Contains("1001"),"one-second queue limit rejects before read with actual count");
            buffer = new Buffer(64); caught = Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0));
            check(buffer.Reads == 8 && caught.Message.Contains("8组"),"persistent backlog cannot spin indefinitely");
            buffer = new Buffer(1,0); long elapsed = 0; buffer.AfterRead = () => elapsed = 100;
            caught = Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => elapsed));
            check(buffer.Reads == 1 && caught.Message.Contains("100 ms"),"read deadline checked after SDK returns");
            buffer = new Buffer(1,64); buffer.Values = (read,raw) => raw[1] = Double.NaN;
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0));
            check(buffer.Reads == 1,"NaN cannot be discarded while chasing newer frames");
            buffer = new Buffer(1,64); buffer.Values = (read,raw) => raw[2] = .999;
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0));
            check(buffer.Reads == 1,"saturation cannot be discarded while chasing newer frames");
            buffer = new Buffer(1,0) { High = false };
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0));
            check(buffer.Reads == 0,"low interlock stops before any analog read");
            buffer = new Buffer(1,0); buffer.AfterRead = () => buffer.High = false;
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0));
            check(buffer.Reads == 1,"low interlock after final read cannot return valid feedback");
            buffer = new Buffer(1,0) { WrongLength = true };
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,0,() => 0)); n++;
            buffer = new Buffer(1,20); f = FactoryFeedbackStream.Read(plan,buffer,0,() => 0);
            check(f.BacklogAfter == 20 && (DateTime.UtcNow - f.TimestampUtc).TotalMilliseconds >= 19,"existing 20-point limit and age correction unchanged");
            check(!f.FreshWindowAfterAoCommand && f.FreshWindowSamplesAfterBoundary==0,
                "legacy reads do not claim a post-AO queue boundary");

            // Independent fake-SDK fixture: visible old points have a different
            // level and a current peak. Only points after that software queue
            // boundary may form the post-AO control window; every old peak stays.
            foreach (int window in new[] { 3, 10, 40 })
            foreach (int oldQueued in new[] { 0, 31 })
            {
                var freshPlan = new FactoryArtFeedbackPlan(new FactorySettings { TargetKv=5,MaximumKv=5,FeedbackWindowMs=window });
                buffer = new Buffer((uint)oldQueued,0);
                int consumed=0; elapsed=0;
                buffer.Values = (read,raw) => {
                    for(int i=0;i<raw.Length/3;i++,consumed++)
                    {
                        bool old=consumed<oldQueued;
                        raw[i*3] = old ? 4.9 : .01;
                        raw[i*3+1] = old ? .2 : .002;
                        raw[i*3+2] = old ? -.3 : (consumed-oldQueued+1)*.001;
                    }
                };
                buffer.AfterRead = () => elapsed+=10;
                f=FactoryFeedbackStream.Read(freshPlan,buffer,.75,() => elapsed,true,true);
                check(f.WindowSampleCount==window && Math.Abs(f.DividerMedianRawV-(window+1)*.0005)<1e-10,
                    "post-AO tail uses only new points across chunks W="+window+" old="+oldQueued);
                check(f.SampleCount==oldQueued+window && f.ReadBatchCount==(window+9)/10 &&
                    f.FreshWindowAfterAoCommand && f.FreshWindowBoundaryQueued==oldQueued && f.FreshWindowSamplesAfterBoundary==window,
                    "post-AO boundary metadata and all point counts W="+window+" old="+oldQueued);
                check(buffer.MaximumNewWait<=FactoryFeedbackStream.MaximumNewSamplesPerRead && elapsed<100,
                    "post-AO chunks never require more than ten new points per native timeout W="+window);
                check(oldQueued==0 ? f.ImonMaximumRawV==.01 : f.ImonMaximumRawV==4.9 && f.DividerMinimumRawV==-.3,
                    "post-AO old-queue current and negative voltage peaks remain protected W="+window);
                if(oldQueued>0)
                {
                    caught=Expect(() => FactoryRunner.ValidateFeedback(f,new FactorySettings { ImonFullScaleMa=10,CurrentLimitMa=5,FeedbackWindowMs=window },true));
                    check(caught.Message.Contains("限流"),"old-queue current peak still trips ordinary overcurrent check W="+window);
                }
            }
            var longPlan=new FactoryArtFeedbackPlan(new FactorySettings { TargetKv=5,MaximumKv=5,FeedbackWindowMs=40 });
            buffer=new Buffer(31,64,64,0); int totalRead=0;
            buffer.Values=(read,raw)=>{for(int i=0;i<raw.Length/3;i++,totalRead++)raw[i*3+2]=totalRead<31?-.3:(totalRead-30)*.001;};
            f=FactoryFeedbackStream.Read(longPlan,buffer,.75,() => 0,true,true);
            check(f.ReadBatchCount==2 && f.FreshWindowSamplesAfterBoundary==84 && Math.Abs(f.DividerMedianRawV-.0645)<1e-10 && f.DividerMinimumRawV==-.3,
                "new queued burst replaces long tail correctly without dropping original old peak");
            check(buffer.MaximumNewWait<=10,"visible queued burst does not enlarge new-point wait budget");
            string snapshot=FactoryRunner.FeedbackSnapshot(f,new FactorySettings());
            check(snapshot.Contains("旧队列边界=31点") && snapshot.Contains("边界后已读=84点") && snapshot.Contains("不表示电源已稳定"),
                "fault diagnostic exposes software freshness boundary without claiming physical settling");
            using(var stream=new System.IO.MemoryStream())
            {
                var xml=new System.Xml.Serialization.XmlSerializer(typeof(FactoryFeedback));
                xml.Serialize(stream,f);stream.Position=0;var saved=(FactoryFeedback)xml.Deserialize(stream);
                check(saved.FreshWindowAfterAoCommand && saved.FreshWindowBoundaryQueued==31 && saved.FreshWindowSamplesAfterBoundary==84,
                    "archive retains post-AO queue boundary evidence");
            }
            buffer=new Buffer(31,0) { High=false };
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==0,"post-AO freshness never bypasses low DI before read");
            buffer=new Buffer(31,0); buffer.AfterRead=()=>buffer.High=false;
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==1,"post-AO freshness rejects low DI after native read");
            buffer=new Buffer(31,0); buffer.AfterRead=()=>buffer.High=false;
            Expect(() => FactoryFeedbackStream.Read(longPlan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==1,"low DI during incomplete long fresh window prevents another chunk");
            buffer=new Buffer(31,0); elapsed=0; buffer.AfterRead=()=>elapsed=100;
            caught=Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => elapsed,true,true));
            check(buffer.Reads==1 && caught.Message.Contains("100 ms"),"post-AO deadline remains 100 ms");
            buffer=new Buffer(31,0); buffer.AfterRead=()=>{throw new TimeoutException("fake native 20 ms timeout");};
            caught=Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(caught is TimeoutException && buffer.Reads==1,"native timeout propagates without retry or artificial fresh success");
            buffer=new Buffer(31,0); buffer.Values=(read,raw)=>raw[2]=Double.NaN;
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==1,"post-AO old-queue NaN cannot be dropped for a clean new tail");
            buffer=new Buffer(31,0); buffer.Values=(read,raw)=>raw[2]=.999;
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==1,"post-AO old-queue saturation cannot be dropped");
            buffer=new Buffer(1001);
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==0,"post-AO one-second visible queue limit unchanged");
            buffer=new Buffer(64);
            caught=Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==8 && caught.Message.Contains("8组"),"post-AO persistent backlog remains bounded to eight reads");
            buffer=new Buffer(31,0) { WrongLength=true };
            Expect(() => FactoryFeedbackStream.Read(plan,buffer,.75,() => 0,true,true));
            check(buffer.Reads==1,"post-AO incomplete native batch cannot form a valid window");

            // P0.4 timing coverage moved to FactoryEmergencyTimingContractTests.
            // The original AI buffer and HV ownership checks below are unchanged.
            var hv = new FactoryHvCommandState(); var levels = new List<bool>();
            check(!hv.Write(false,levels.Add) && levels.Count == 0 && !hv.MayBeOn,"default OFF does not issue startup HV OFF");
            hv.Write(true,levels.Add); hv.Write(false,levels.Add); hv.Write(false,levels.Add);
            check(levels.Count == 2 && levels[0] && !levels[1] && !hv.MayBeOn,"only attempted ON followed by OFF, no duplicate OFF");
            hv = new FactoryHvCommandState();
            Expect(() => hv.Write(true,high => { throw new Exception("partial ON"); }));
            check(hv.MayBeOn,"failed ON remains mandatory-shutdown owned");
            levels.Clear(); hv.Write(false,levels.Add);
            check(levels.Count == 1 && !levels[0] && !hv.MayBeOn,"failed ON still followed by attempted OFF");
            hv.Write(true,levels.Add);
            Expect(() => hv.Write(false,high => { throw new Exception("failed OFF"); }));
            check(hv.MayBeOn,"failed OFF cannot falsely clear ownership");
            hv.Write(false,levels.Add); check(!hv.MayBeOn,"cleanup can retry failed OFF");
            return "Current AI buffer/HV command: " + n + " offline checks PASS; fake SDK only; no hardware.";
        }
        private sealed class Buffer : IFactoryFeedbackBuffer
        {
            private readonly uint[] queue; private int index;
            internal int Reads, MaximumNewWait; internal bool High = true, WrongLength;
            private uint lastAvailable;
            private int availabilityPhase=1;
            internal Action<int,double[]> Values; internal Action AfterRead;
            internal Buffer(params uint[] queue) { this.queue = queue; }
            // Existing batch fixtures publish their next complete USB block
            // when the new pre-read availability poll asks for it. The first
            // boundary and post-read backlog sequences remain unchanged.
            public uint Available() { if(availabilityPhase==2)return lastAvailable+=40;
                availabilityPhase++;return lastAvailable=queue[Math.Min(index++,queue.Length - 1)]; }
            public double[] Read(int count)
            {
                Reads++; availabilityPhase=0; MaximumNewWait=Math.Max(MaximumNewWait,count-checked((int)lastAvailable));
                var raw = new double[count * 3 - (WrongLength ? 1 : 0)];
                if (Values != null) Values(Reads,raw); if (AfterRead != null) AfterRead(); return raw;
            }
            public bool EmergencyReleased() { return High; }
        }
        private static Exception Expect(Action operation)
        { try { operation(); } catch (Exception ex) { return ex; } throw new Exception("Expected R10 rejection."); }
    }
}
