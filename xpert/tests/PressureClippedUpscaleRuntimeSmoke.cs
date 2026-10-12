using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xiongxin.ZeroBench;
using Xiongxin.FactoryTest;

// Compiled with runtime sources but selects ONLY this managed fixture entry.
// No worker Main, resident server, native backend, SDK or hardware is invoked.
internal static class PressureClippedUpscaleRuntimeSmoke
{
    static int checks;
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static bool Reject(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
    static LocationRangeResult Quality(int goal,int clips,int run)
    {return new LocationRangeResult{WindowValid=true,FactoryCycleWindow=true,LocationSampleCount=10000,
        RecommendedIndex=goal,LocationClippedSamples=clips,LocationLongestClippedRun=run};}
    static FactoryResult ClippedCapture(FactorySettings request)
    {
        var result=FactoryBatchAuthorizationTests.Capture(request);
        // Use the runtime's own measured cycle-window selector, then place an
        // explicit clipped plateau inside that exact window. Preserve metadata.
        var before=FactoryRangeReview.EvaluateCapturedData(result);
        Check(before.WindowValid&&before.FactoryCycleWindow&&before.LocationSampleCount>=10,"fixture has a real selected cycle window");
        Array.Clear(result.Capture.PdAdc,0,result.Capture.PdAdc.Length);
        int center=before.WindowStartSample+before.LocationSampleCount/2;
        for(int i=0;i<3;i++)result.Capture.PdAdc[center+i]=(short)-result.Capture.MaximumAdc;
        result.Capture.Overflow=true;
        return result;
    }
    static void Authorization()
    {
        foreach(int target in new[]{6,7})
        {
            string session="bounded-two-step-"+target;
            var guard=new FactoryBatchAuthorization(session);var first=FactoryBatchAuthorizationTests.Request(3);
            first.Input.RangeIndex=5;first=guard.Authorize(first);
            first.BatchPermit.ConsumeForNative(session,first);first.BatchPermit.MarkNativeEnded();
            var archived=ClippedCapture(first);string raw=String.Join(",",archived.Capture.PdAdc);
            var q=FactoryRangeReview.EvaluateCapturedData(archived);
            Check(q.LocationLongestClippedRun==3&&FactoryPressureSequence.NextRange(5,q)==7,"runtime predicts exactly two for measured sustained clipping");
            guard.Complete(first,archived);
            var next=FactoryBatchAuthorizationTests.Next(first,2);next.Input.RangeIndex=target;
            var issued=guard.Authorize(next);
            Check(issued.BatchPermit.Matches(issued)&&issued.Input.RangeIndex==target,"server permits its bounded target or one-step calibrated fallback");
            Check(!issued.IndependentDischargeConfirmed&&!issued.FeedbackWiringConfirmed&&!issued.BatchStartConfirmed,"range permission cannot manufacture a fresh start/safety assertion");
            Check(raw==String.Join(",",archived.Capture.PdAdc),"authorization never changes clipped raw evidence");
            guard.Revoke();Check(!issued.BatchPermit.Matches(issued)&&Reject(()=>guard.Authorize(FactoryBatchAuthorizationTests.Next(issued,3))),"cancel revokes both bounded alternatives");
        }
        foreach(int target in new[]{3,4,8,9,10})
        {
            string session="deny-extra-range-"+target;var guard=new FactoryBatchAuthorization(session);
            var first=FactoryBatchAuthorizationTests.Request(3);first.Input.RangeIndex=5;first=guard.Authorize(first);
            first.BatchPermit.ConsumeForNative(session,first);first.BatchPermit.MarkNativeEnded();guard.Complete(first,ClippedCapture(first));
            var next=FactoryBatchAuthorizationTests.Next(first,2);next.Input.RangeIndex=target;
            Check(Reject(()=>guard.Authorize(next)),"no downrange or more than two-step continuation from a clipped round");
            next.Input.RangeIndex=7;Check(Reject(()=>guard.Authorize(next)),"invalid continuation revokes instead of silently recovering");
        }
    }
    static int Main()
    {
        try
        {
            for(int current=0;current<11;current++)for(int goal=0;goal<11;goal++)
            {
                int adjacent=current+Math.Sign(goal-current),two=goal>current?Math.Min(current+2,goal):current;
                Check(FactoryPressureSequence.NextRange(current,Quality(goal,3,3))==two,"runtime all 11x11 pairs match shared bounded execution");
                Check(FactoryPressureSequence.NextRange(current,Quality(goal,1,1))==(goal>current?adjacent:current),"runtime isolated clipping stays adjacent upward or holds");
                var clean=Quality(goal,0,0);Check(FactoryPressureSequence.NextRange(current,clean)==adjacent,"runtime low/clean input remains adjacent");
            }
            foreach(var q in new[]{new LocationRangeResult{WindowValid=false},new LocationRangeResult{AtMaximumAndClipped=true},
                new LocationRangeResult{DriverOverflowUncertain=true},new LocationRangeResult{MaximumRangeInsufficient=true}})
                Check(Reject(()=>FactoryPressureSequence.NextRange(10,q)),"invalid/uncertain/maximum result retains the existing stop gate");
            var outside=Quality(9,0,0);outside.FullClippedSamples=100;outside.OutsideClippedSamples=100;
            Check(FactoryPressureSequence.NextRange(5,outside)==6,"outside clipping alone never enables two upward steps");
            Console.WriteLine(FactoryBatchAuthorizationTests.Run());
            Authorization();
            Console.WriteLine("PRESSURE_CLIPPED_UPSCALE_RUNTIME_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;WorkersStarted=0;ProductStarted=false;PhysicalAcceptance=false");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
