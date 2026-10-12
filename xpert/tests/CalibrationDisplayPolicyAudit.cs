using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using HVX.SignalProcessing;
using Xiongxin.Calibration;

// Pure-fixture regression. No form/control, SDK, worker or product startup.
internal static class CalibrationDisplayPolicyAudit
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static double[] Wave(double ratio,double width=8,double reflectedWidth=8)
    {return Enumerable.Range(0,2200).Select(i=>Math.Exp(-Math.Pow((i-400)/width,2))+ratio*Math.Exp(-Math.Pow((i-1000)/reflectedWidth,2))).ToArray();}
    static CalibrationBandwidthSearchResult Search(double[] wave)
    {return CalibrationReflectionBandwidth.Search(wave,400,100e6,300,100,.15,false,null);}
    static object Describe(string name,CalibrationBandwidthSearchResult found)
    {return new {name,found.Reflection.Valid,found.Reflection.IncidentIndex,found.Reflection.ReflectionIndex,
        found.Bandwidth.CutoffMHz,found.Bandwidth.Ratio,found.Bandwidth.Sufficient,found.Bandwidth.Stable,found.Bandwidth.DisplayFallback,found.Bandwidth.StabilityReason};}
    static int Main()
    {
        try {
            var observations=new List<object>();
            var weak=Wave(.2);var before=(double[])weak.Clone();var weakResult=Search(weak);
            observations.Add(Describe("weak-equal-shape",weakResult));
            Check(weak.SequenceEqual(before),"Search must not mutate source.");
            Check(weakResult.Reflection.Valid&&weakResult.Bandwidth.Stable==false&&!weakResult.Bandwidth.Sufficient,"Broadened manual fallback remains truthfully unverified.");
            Check(weakResult.Bandwidth.CutoffMHz==3&&weakResult.Bandwidth.DisplayFallback,"Sub-target display falls back to manual 3MHz.");
            Check(weakResult.Reflection.IncidentIndex==400&&weakResult.Reflection.ReflectionIndex==1000,"Fallback cannot replace raw packet timing anchors.");
            Check(weakResult.Bandwidth.Waveform.SequenceEqual(PdLinearLowPass.Apply(weak,1/100e6,3)),"Fallback is exactly 3MHz original-source FIR, not gain/cascade.");
            var strong=Search(Wave(.7));observations.Add(Describe("already-sufficient",strong));
            Check(strong.Bandwidth.Sufficient&&strong.Bandwidth.CutoffMHz==0&&!strong.Bandwidth.DisplayFallback,"Already sufficient signal remains unfiltered.");
            var near=Search(Wave(.48,8,10));observations.Add(Describe("near-target-differential-width",near));
            Check(!near.Bandwidth.Sufficient||near.Bandwidth.Ratio>=.5,"No fabricated target success.");
            var negative=Wave(-.7);var negativeResult=Search(negative);observations.Add(Describe("opposite-polarity-reflection",negativeResult));
            Check(negativeResult.Reflection.Valid&&negativeResult.Bandwidth.Sufficient,"Real inverted reflection is not categorically discarded.");
            Check(negativeResult.Bandwidth.Waveform[negativeResult.Reflection.ReflectionIndex]<0,"Actual negative reflection polarity is retained.");
            var shifted=new double[weak.Length];Array.Copy(weak,0,shifted,8,weak.Length-8);
            var moved=CalibrationDisplayBandwidth.Assess(weak,shifted,400,1000,408,1008,3);
            Check(moved.Stable==false&&!moved.Sufficient&&moved.StabilityReason=="PeakMovedOrPacketChanged","Eight-sample anchor shift must not be stable.");
            var changedSign=weak.Select(x=>-x).ToArray();
            var reversed=CalibrationDisplayBandwidth.Assess(weak,changedSign,400,1000,400,1000,3);
            Check(reversed.Stable==false&&!reversed.Sufficient&&reversed.StabilityReason=="PolarityChanged","Display-only polarity inversion rejected.");
            var filtered=PdLinearLowPass.Apply(weak,1/100e6,3);
            var at3=CalibrationDisplayBandwidth.Assess(weak,filtered,400,1000,400,1000,3);
            observations.Add(new {name="explicit-3MHz-assessment",at3.Ratio,at3.Sufficient,at3.Stable,at3.StabilityReason});
            var missing=Search(Wave(0));observations.Add(Describe("absent-reflection",missing));
            Check(!missing.Reflection.Valid&&missing.Reflection.ReflectionIndex==-1&&!missing.Bandwidth.Sufficient,"No fabricated reflection.");
            var clipped=Wave(.7);for(int i=397;i<=403;i++)clipped[i]=clipped[397];
            var flat=Search(clipped);observations.Add(Describe("flat-topped-incident",flat));
            Check(flat.Bandwidth.Stable==false&&!flat.Bandwidth.Sufficient,"Filtering cannot erase clipping evidence.");
            bool cancelled=false;try{CalibrationReflectionBandwidth.Search(weak,400,100e6,300,100,.15,false,()=>true);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"Cancellation still observed.");
            int calls=0;CalibrationReflectionBandwidth.Search(weak,400,100e6,300,100,.15,false,()=>{calls++;return false;});
            Check(calls<=12,"Search remains bounded.");
            var lowRate=CalibrationReflectionBandwidth.Search(weak,400,6e6,5000,100,.15,false,null);
            Check(lowRate.Bandwidth.CutoffMHz==0&&!lowRate.Bandwidth.DisplayFallback,"No unsupported 3MHz lowpass claimed at Nyquist.");
            var invalid=(double[])weak.Clone();invalid[100]=Double.NaN;bool rejected=false;
            try{Search(invalid);}catch(ArgumentException){rejected=true;}
            Check(rejected,"Nonfinite source still rejected.");
            Console.WriteLine(new JavaScriptSerializer().Serialize(new {status="PASS-REGRESSION",checks,
                latestRequestedFallbackImplemented=true,observations,
                productStarted=false,uiConstructed=false,algorithmProcessStarted=false,
                workerStarted=false,sdkCalled=false,hardwareUsed=false,physicalAcceptance=false}));
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
