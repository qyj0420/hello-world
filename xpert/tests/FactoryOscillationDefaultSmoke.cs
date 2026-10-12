using System;
using Xiongxin.FactoryTest;
using Xiongxin.ZeroBench;

// This entry runs only synthetic arrays / explicitly named managed fixtures.
// Compiling worker sources does not execute a worker, SDK or product entry.
internal static class FactoryOscillationDefaultSmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static int Main()
    {
        try {
            var request=new FactoryTests.Fixture().Settings;
            request.Input.RequestedPicoRateHz=1000000;request.Input.CaptureDurationMs=20;
            request.Input.OscillationCircuitConnected=false;request.Input.RangeIndex=5;
            request.EndAfterPressureCapture=true;
            var result=FactoryPressureSequenceTests.Capture(request);
            var phase=FactoryRangePhase.Analyze(result.Capture);
            Check(phase.Valid,"legacy false evaluates the real oscillation instead of rejecting a checkbox");
            var quality=FactoryRangeReview.EvaluateCapturedData(result);
            Check(quality.WindowValid&&quality.FactoryCycleWindow&&quality.LocationSampleCount>0,"real first crossing produces the configured N-cycle window");
            Check(!result.Capture.Settings.OscillationCircuitConnected&&!request.Input.OscillationCircuitConnected,"evaluation never rewrites historical feature metadata as a site confirmation");
            bool independent=result.Settings.IndependentDischargeConfirmed,feedback=result.Settings.FeedbackWiringConfirmed,completed=result.Completed,residual=result.ResidualSafe;
            result.Capture.Settings.OscillationCircuitConnected=true;
            var enabled=FactoryRangePhase.Analyze(result.Capture);
            var enabledQuality=FactoryRangeReview.EvaluateCapturedData(result);
            Check(enabled.FirstZeroMs==phase.FirstZeroMs&&enabled.PeriodMs==phase.PeriodMs,"true/false legacy switch gives identical measured phase");
            Check(enabledQuality.WindowStartSample==quality.WindowStartSample&&enabledQuality.WindowEndExclusive==quality.WindowEndExclusive,
                "true/false legacy switch gives identical measured save/range window");
            Check(result.Settings.IndependentDischargeConfirmed==independent&&result.Settings.FeedbackWiringConfirmed==feedback&&
                result.Completed==completed&&result.ResidualSafe==residual,"diagnostic evaluation never grants electrical or completion permission");
            result.Capture.Settings.OscillationCircuitConnected=false;
            double raw=result.Capture.VoltageRaw[10];result.Capture.VoltageRaw[10]=Double.NaN;
            bool corruptRejected=false;try{FactoryRangeReview.EvaluateCapturedData(result);}catch(System.IO.InvalidDataException){corruptRejected=true;}
            Check(corruptRejected&&!FactoryRangePhase.Analyze(result.Capture).Valid,"nonfinite ART evidence still rejects the capture/window");
            result.Capture.VoltageRaw[10]=10;
            Check(!FactoryRangeReview.EvaluateCapturedData(result).WindowValid,"actual voltage input rail still rejects phase");
            result.Capture.VoltageRaw[10]=raw;result.Capture.ArtRateHz=0;
            Check(!FactoryRangePhase.Analyze(result.Capture).Valid,"invalid ART timebase is not enabled by the default");
            result.Capture.ArtRateHz=80000;Array.Clear(result.Capture.VoltageRaw,0,result.Capture.VoltageRaw.Length);
            quality=FactoryRangeReview.EvaluateCapturedData(result);
            Check(!quality.WindowValid&&!quality.WindowDefinition.Contains("未核对振荡回路"),"no oscillation stays invalid with the real reason");
            bool nextDenied=false;try{FactoryPressureSequence.NextRange(5,quality);}catch(InvalidOperationException){nextDenied=true;}
            Check(nextDenied,"a genuine invalid window never authorizes an automatic range continuation");
            FactoryPressureStartPrompt.ValidateRangePreflight(request,2);
            request.Input.PreTriggerPercent=100;bool dcDenied=false;
            try{FactoryPressureStartPrompt.ValidateRangePreflight(request,2);}catch(ArgumentException){dcDenied=true;}
            Check(dcDenied,"known all-DC capture placement remains rejected for repeated range evaluation");
            Console.WriteLine(FactoryAnalysisTests.Run());
            Console.WriteLine(FactoryPressureSequenceTests.Run());
            Console.WriteLine("FACTORY_OSCILLATION_DEFAULT_PASS="+checks+"; HardwareAccessed=false; SDKCalled=false; WorkersStarted=0; ProductStarted=false");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
