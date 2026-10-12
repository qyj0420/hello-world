using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using HVX.Controls;
using Xiongxin.FactoryTest;

internal static class PressureDesktopDataflowSmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static void Rejected(Action action,string text){bool failed=false;try{action();}catch{failed=true;}Check(failed,text);}
    static FactoryResult Run(bool deferred,out FactoryTests.Fixture fixture)
    {
        fixture=new FactoryTests.Fixture();fixture.Settings.EndAfterPressureCapture=true;
        fixture.Settings.DesktopPostProcessing=deferred;
        return fixture.Run();
    }
    static void FactoryCases(string output)
    {
        Check(!new FactorySettings().DesktopPostProcessing,"historical XML/API defaults retain synchronous analysis");
        FactoryTests.Fixture legacyFixture,newFixture;
        var legacy=Run(false,out legacyFixture);var current=Run(true,out newFixture);
        Check(legacy.Metrics!=null&&legacy.Metrics.Detected!=null&&!legacy.FullMetricsDeferred,"default Factory computes real detected metrics");
        Check(current.FullMetricsDeferred&&current.Metrics!=null&&current.Metrics.Detected==null,"desktop explicitly marks absent full metrics");
        Check(Double.IsNaN(current.Metrics.MaxPdPc)&&Double.IsNaN(current.Metrics.FrequencyHz)&&!current.Metrics.FrequencyValid,
            "absent numeric metrics stay NaN/invalid, not invented zero measurements");
        Check(current.CaptureOnlyAvailable()&&!current.Completed&&!current.ResidualSafe,"deferred metrics do not confer completion/discharge permission");
        Check(newFixture.Backend.DisposeCount==1&&!newFixture.Backend.Hv&&!newFixture.Backend.Optical&&newFixture.Backend.Ao==0,
            "actual fake stop/shutdown/disposal still precede return");
        Check(newFixture.Clock.ElapsedMilliseconds==legacyFixture.Clock.ElapsedMilliseconds,"defer does not change any fake device/output wait");
        Check(current.Capture.PdAdc.SequenceEqual(legacy.Capture.PdAdc)&&current.Capture.VoltageRaw.SequenceEqual(legacy.Capture.VoltageRaw),
            "full original ADC and voltage unchanged");
        // The generic backend returns zero arrays. Supply the same explicit
        // synthetic oscillation to both raw-review paths (not device evidence).
        legacy.Capture.Settings.OscillationCircuitConnected=current.Capture.Settings.OscillationCircuitConnected=true;
        for(int i=0;i<current.Capture.VoltageRaw.Length;i++)
            legacy.Capture.VoltageRaw[i]=current.Capture.VoltageRaw[i]=.3*Math.Cos(2*Math.PI*390*i/current.Capture.ArtRateHz);
        var oldRange=FactoryRangeReview.EvaluateCapturedData(legacy);var newRange=FactoryRangeReview.EvaluateCapturedData(current);
        Check(oldRange.WindowValid&&newRange.WindowValid&&oldRange.RecommendedIndex==newRange.RecommendedIndex&&
            oldRange.FullClippedSamples==newRange.FullClippedSamples&&oldRange.WindowDefinition==newRange.WindowDefinition,
            "raw N-window range review remains independent of omitted full diagnostics");
        current.Capture.PdAdc[0]=(short)current.Capture.MaximumAdc;current.Capture.Overflow=true;
        var clipped=FactoryRangeReview.EvaluateCapturedData(current);
        Check(clipped.DriverOverflow&&clipped.FullClippedSamples>0,"whole-segment clipping/overflow preserved outside or inside N window");
        using(var memory=new MemoryStream()){
            new XmlSerializer(typeof(FactoryResult)).Serialize(memory,current);memory.Position=0;
            var read=(FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(memory);
            Check(read.FullMetricsDeferred&&read.Settings.DesktopPostProcessing&&read.Metrics.Detected==null,"deferred marker survives saved metadata roundtrip");
        }
        current.Completed=true;
        Rejected(()=>Program.Export(current,Path.Combine(output,"must-not-export.csv")),"pending metrics cannot masquerade as an analyzed export");
        current.Completed=false;
        Check(!File.Exists(Path.Combine(output,"must-not-export.csv")),"rejected export leaves no fabricated file");
        foreach(string fault in new[]{"read","dispose"}){
            var bad=new FactoryTests.Fixture();bad.Settings.DesktopPostProcessing=true;bad.Settings.EndAfterPressureCapture=true;
            bad.Backend.FailRead=fault=="read";bad.Backend.FailDispose=fault=="dispose";
            FactoryRunException failure=null;try{bad.Run();}catch(FactoryRunException error){failure=error;}
            Check(failure!=null&&!failure.PartialResult.CaptureOnlyAvailable(),fault+" remains a real failure, never early acquisition ACK");
        }
        var calibration=new FactoryTests.Fixture();calibration.Settings.DesktopPostProcessing=true;
        var calibrated=calibration.Run(null,FactoryRunKind.Calibration);
        Check(!calibrated.FullMetricsDeferred&&calibrated.Metrics!=null&&calibrated.Metrics.Detected!=null,"calibration ignores desktop-pressure opt-in");
        foreach(bool defer in new[]{false,true}){
            var settings=new FactorySettings{DesktopPostProcessing=defer};settings.Input.RequestedPicoRateHz=1000000;
            settings.Input.CaptureDurationMs=20;settings.Input.RangeIndex=5;
            var clock=new FactoryTests.FakeClock();
            var result=FactoryZeroInputRunner.Run(settings,new SimulationFactoryZeroInput(clock),clock,null,CancellationToken.None);
            Check(result.ReadOnlyAcquisition&&result.Completed&&!result.ResidualSafe,"zero input remains input-only, never discharge confirmation");
            Check(result.FullMetricsDeferred==defer&&(result.Metrics.Detected==null)==defer,"zero input obeys explicit processing flag only");
        }
        var optical=new FactoryTests.Fixture();optical.Settings.TargetKv=0;optical.Settings.ZeroSynchronizedOptical=true;optical.Settings.DesktopPostProcessing=true;
        var opticalResult=FactoryZeroTriggerRunner.Run(optical.Settings,optical.Backend,optical.Clock,null,CancellationToken.None);
        Check(opticalResult.FullMetricsDeferred&&opticalResult.Metrics.Detected==null&&!opticalResult.ResidualSafe,"optical zero reference defers diagnostics without inventing safety");
    }
    static async Task PairCases()
    {
        foreach(int first in new[]{0,1}){
            var loc=new TaskCompletionSource<bool>();var dem=new TaskCompletionSource<bool>();bool locStarted=false,demStarted=false,committed=false;
            var run=PressureRoundPairPreparation.RunAsync(()=>{locStarted=true;return loc.Task;},()=>{demStarted=true;return dem.Task;},ok=>committed=ok);
            Check(locStarted&&demStarted&&!run.IsCompleted&&!committed,"both derived writers start without waiting for their peer");
            if(first==0)loc.SetResult(true);else dem.SetResult(true);
            Check(!run.IsCompleted&&!committed,"one derived record cannot acknowledge a complete pair");
            if(first==0)dem.SetResult(true);else loc.SetResult(true);
            Check(await run&&committed,"both derived records complete before caller can publish/ACK");
        }
        var location=new TaskCompletionSource<bool>();var detector=new TaskCompletionSource<bool>();bool locationCommitted=false;
        var failing=PressureRoundPairPreparation.RunAsync(()=>location.Task,()=>detector.Task,ok=>locationCommitted=ok);
        detector.SetException(new IOException("synthetic detector write failure"));
        Check(!failing.IsCompleted&&!locationCommitted,"failed DEM still drains ongoing LOC before cleanup");
        location.SetResult(true);bool failed=false;try{await failing;}catch(IOException){failed=true;}
        Check(failed&&locationCommitted,"partial LOC is identified for recovery, never successful pair ACK");
        bool locFalse=await PressureRoundPairPreparation.RunAsync(()=>Task.FromResult(false),()=>Task.FromResult(true),ok=>Check(!ok,"failed location reports no commit"));
        Check(!locFalse,"false location result cannot authorize publication");
        var peer=new TaskCompletionSource<bool>();bool peerStarted=false;
        var setupFailure=PressureRoundPairPreparation.RunAsync(()=>{throw new IOException("synthetic context rejection");},()=>{peerStarted=true;return peer.Task;},ok=>{});
        Check(peerStarted&&!setupFailure.IsCompleted,"synchronous branch rejection still observes peer task");
        peer.SetResult(true);failed=false;try{await setupFailure;}catch(IOException){failed=true;}Check(failed,"synchronous failure propagates after join");
        var canceled=new TaskCompletionSource<bool>();var waiting=new TaskCompletionSource<bool>();
        var cancellation=PressureRoundPairPreparation.RunAsync(()=>canceled.Task,()=>waiting.Task,ok=>{});canceled.SetCanceled();
        Check(!cancellation.IsCompleted,"cancellation does not abandon the peer writer");waiting.SetResult(true);
        failed=false;try{await cancellation;}catch(OperationCanceledException){failed=true;}Check(failed,"canceled derived pair never publishes success");
    }
    static int Main(string[] args)
    {
        try{
            if(args.Length!=1||!Directory.Exists(args[0]))throw new ArgumentException("isolated QA directory required");
            FactoryCases(args[0]);PairCases().GetAwaiter().GetResult();
            Console.WriteLine("PASS desktop-pressure-dataflow checks="+checks+"; managed fakes only; HardwareAccessed=False; SDKCalled=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
