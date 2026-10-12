using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Managed samples only: no native backend, device discovery or output.
    internal static class FactoryLocationCycleTests
    {
        internal static string Run() { return FactoryOfflineAsyncTest.Run(Check); }
        private static async Task<string> Check()
        {
            int checks=0;
            Action<bool,string> ok=(pass,why)=>{if(!pass)throw new Exception("Location N cycles: "+why);checks++;};
            var phase=new FactoryRangePhaseResult { Valid=true,FirstZeroMs=.625,PeriodMs=2.5 };
            var w=FactoryMeasuredRangeWindow.SelectCycles(false,20000,1000000,0,phase,1);
            ok(w.Valid && !w.Shortened && w.StartSample==625 && w.EndExclusive==3125,"one exact cycle");
            w=FactoryMeasuredRangeWindow.SelectCycles(false,20000,1000000,0,phase,3);
            ok(w.Valid && !w.Shortened && w.EndExclusive==8125,"three exact cycles");
            w=FactoryMeasuredRangeWindow.SelectCycles(false,20000,1000000,0,phase,100);
            ok(w.Valid && w.Shortened && w.StartSample==625 && w.EndExclusive==20000,"N insufficient uses available tail");
            ok(w.Definition.Contains("250.625") && w.Definition.Contains("19.375") && w.Definition.Contains("有限长度"),"requested and actual span disclosed");
            ok(!FactoryMeasuredRangeWindow.SelectCycles(false,20000,1000000,0,null,1).Valid,"missing phase not fabricated");
            foreach(int bad in new[]{0,101})ok(!FactoryMeasuredRangeWindow.SelectCycles(false,20000,1000000,0,phase,bad).Valid,"cycle bounds");

            var s=new FactoryTests.Fixture().Settings;
            s.Simulation=true;s.EndAfterPressureCapture=true;s.TargetKv=5;s.Input.ArtSerial="SIM-ART";s.Input.PicoSerial="SIM-PICO";
            s.Input.OscillationCircuitConnected=true;s.Input.CaptureDurationMs=20;s.Input.RequestedPicoRateHz=1000000;
            s.Input.PreTriggerPercent=0;s.Input.RangeIndex=5;s.Input.AutoRange=false;s.LocationSaveCycleCount=1;
            var r=Round(s);var q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(q.WindowValid && Math.Abs(q.WindowStartSample-625)<=1 && Math.Abs(q.WindowEndExclusive-3125)<=1,"measured zero and cycle instead of damping phase");
            ok(!r.Metrics.ReferenceAnalysis.PhaseValid && !r.Metrics.ReferenceAnalysis.DecayValid,"ranging does not change fit acceptance");
            int start=q.WindowStartSample,end=q.WindowEndExclusive;
            r.Capture.PdAdc[0]=32767;r.Capture.PdAdc[end+50]=Int16.MinValue;r.Capture.Overflow=true;
            q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(q.OutsideCycleClippingExcluded && q.FullClippedSamples==2 && q.LocationClippedSamples==0 && q.DriverOverflow,"outside ADC evidence preserved, excluded from N decision");
            ok(FactoryPressureSequence.NextRange(5,q)<5 && FactoryNextRangeReview.CanApply(r,q,5),"outside clipping does not suppress valid decrease");
            r.Capture.PdAdc[start+50]=Int16.MinValue;
            q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(q.LocationClippedSamples==1 && FactoryPressureSequence.NextRange(5,q)>5,"negative in-window clipping increases range");
            r=Round(s);r.Capture.Overflow=true;q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(FactoryTransientRangePolicy.BlocksForOverflow(q,5),"unlocalized overflow remains blocked");
            using(var dialog=new FactoryNextRangeDialog(r,q,5,null))
                ok(!dialog.ApplyButton.Enabled && !dialog.KeepButton.Enabled,"invalid warning cannot silently apply/continue");

            foreach(short peak in new short[]{8191,8192,29490,32766})
            {
                r=Round(s);r.Capture.PdAdc[start+10]=(short)-peak;q=FactoryRangeReview.EvaluateCapturedData(r);
                if(peak<8192)ok(q.Change && q.RecommendedIndex<5,"below 25% decreases");
                else ok(!q.Change && q.RecommendedIndex==5 && FactoryTransientRangePolicy.WindowQualityPassed(q),"25%-unclipped full scale retained");
            }
            var largest=s.Clone();largest.Input.RangeIndex=10;r=Round(largest);r.Capture.PdAdc[start+10]=-29490;
            q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(!q.MaximumRangeInsufficient && FactoryPressureSequence.NextRange(10,q)==10,"90% at largest range not rejected solely by 65% display target");
            r.Capture.PdAdc[start+10]=-32767;q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(q.AtMaximumAndClipped && !FactoryNextRangeReview.CanApply(r,q,10),"largest range actual clipping remains blocked");
            r=Round(s);r.Capture.Settings.CalibrationPulsePc=1000000;q=FactoryRangeReview.EvaluateCapturedData(r);
            ok(q.ExpectedCalibrationVolts==0 && q.RecommendedIndex<5,"pressure recommendation is measured not injection preset");
            r.Kind=FactoryRunKind.Calibration;r.Completed=true;r.ResidualSafe=true;q=FactoryRangeReview.Evaluate(r);
            ok(q.WindowStartSample==0 && q.WindowEndExclusive==r.Capture.PdAdc.Length && q.ExpectedCalibrationVolts>0,"calibration remains whole segment and injection-aware");
            r=Round(s);Array.Clear(r.Capture.VoltageRaw,0,r.Capture.VoltageRaw.Length);
            ok(!FactoryRangeReview.EvaluateCapturedData(r).WindowValid,"zero voltage cannot fabricate oscillation phase");
            for(int i=0;i<r.Capture.VoltageRaw.Length;i++)r.Capture.VoltageRaw[i]=1;
            ok(!FactoryRangeReview.EvaluateCapturedData(r).WindowValid,"DC cannot fabricate oscillation phase");

            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-location-cycles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            s.LocationSaveCycleCount=100;r=Round(s);q=FactoryRangeReview.EvaluateCapturedData(r);r.Metrics.RangeQuality=q;
            var original=(short[])r.Capture.PdAdc.Clone();FactoryLocationCycleArchive.Save(r,root);
            FactoryLocationCycleManifest manifest;
            using(var f=File.OpenRead(Path.Combine(root,"location-window.xml")))manifest=(FactoryLocationCycleManifest)new XmlSerializer(typeof(FactoryLocationCycleManifest)).Deserialize(f);
            ok(manifest.Valid && manifest.Shortened && manifest.Cycles==100 && manifest.EndExclusive==original.Length,"archive records requested N and finite length");
            using(var b=new BinaryReader(File.OpenRead(Path.Combine(root,"location-ncycles.bin"))))
            {
                ok(b.ReadString()=="XIONGXIN-LOCATION-NCYCLES-1" && b.ReadInt32()==q.WindowStartSample && b.ReadInt32()==q.LocationSampleCount,"derived segment exact bounds");
                ok(b.ReadDouble()==r.Capture.PicoRateHz && b.ReadInt32()==r.Capture.MaximumAdc && b.ReadDouble()==r.Capture.PicoFullScaleV,"derived segment carries ADC conversion");
                bool exact=true;for(int i=q.WindowStartSample;i<q.WindowEndExclusive;i++)exact&=b.ReadInt16()==original[i];
                ok(exact && b.BaseStream.Position==b.BaseStream.Length && original.SequenceEqual(r.Capture.PdAdc),"every saved ADC matches source and original unchanged");
            }
            var seen=new List<int>();var logs=new List<FactoryNextRangeDecision>();
            var progress=new FactorySequenceProgress(s,5);
            int completed=await FactoryPressureSequence.Run(s,5,next=>{
                seen.Add(next.Input.RangeIndex);var result=Round(next);
                result.DirectoryPath=Path.Combine(root,"round-"+next.SequenceRound);Directory.CreateDirectory(result.DirectoryPath);
                return Task.FromResult(result);
            },null,null,CancellationToken.None,(result,quality)=>{
                ok(quality.WindowValid && quality.WindowShortened,"short N window remains valid in queue");
                var d=FactoryNextRangeReview.Silent(result,quality,true);FactoryNextRangeReview.Save(result,d);logs.Add(d);
                return Task.FromResult(d.ApplyNextRound);
            },progress);
            ok(completed==5 && progress.Remaining==0 && seen.Count==5,"short N five-round queue completes");
            ok(seen.SequenceEqual(new[]{5,4,3,2,2}),"fixed .02V signal descends one adjacent range per round then retains .05V range");
            ok(logs.Count==4 && logs.All(x=>x.WasAutomaticDecision && !x.ReminderEnabled && x.EvaluationWindowMs>0 && x.OriginalQuality.WindowShortened),"silent range decisions include shortened evidence");
            ok(s.Input.RangeIndex==5 && s.LocationSaveCycleCount==100,"initial request not overwritten");
            return "Location N-cycle hotfix: "+checks+" checks PASS; five finite-length captures; ranges="+String.Join(",",seen)+"; HardwareAccessed=False; "+root;
        }
        private static FactoryResult Round(FactorySettings s)
        {
            var r=FactoryPressureSequenceTests.Capture(s);var c=r.Capture;
            for(int i=0;i<c.VoltageRaw.Length;i++)c.VoltageRaw[i]=-Math.Cos(2*Math.PI*400*i/c.ArtRateHz);
            Array.Clear(c.PdAdc,0,c.PdAdc.Length);
            // Fixed physical .02 V signal, correctly requantized for each next range.
            for(int i=1000;i<c.PdAdc.Length;i+=1000)c.PdAdc[i]=(short)Math.Min(c.MaximumAdc,Math.Round(.02/c.PicoFullScaleV*c.MaximumAdc));
            c.Overflow=false;r.Metrics.ReferenceAnalysis=new FactoryReferenceResult {PhaseValid=false,DecayValid=false};
            return r;
        }
    }
}
