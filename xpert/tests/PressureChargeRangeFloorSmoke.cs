using System;
using System.IO;
using System.Reflection;

internal static class PressureChargeRangeFloorSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type policy,quality;static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static object Call(string method,params object[] args){return policy.GetMethod(method,F).Invoke(null,args);}
    static void Set(object obj,string field,object value){quality.GetField(field,F).SetValue(obj,value);}
    static object Get(object obj,string field){return quality.GetField(field,F).GetValue(obj);}
    static bool Rejects(Action action){try{action();return false;}catch(TargetInvocationException e){return e.InnerException is InvalidDataException;}}
    static int Main(string[] args)
    {
        try{
            var a=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
            policy=a.GetType("HVX.Controls.PressureChargeRangeFloor",true);quality=a.GetType("Xiongxin.ZeroBench.LocationRangeResult",true);
            Check((int)Call("MinimumIndex",1000d)==3,"100 pC at 0.1 V");
            Check((int)Call("MinimumIndex",999d)==4,"rounding display does not lower exact hardware floor");
            Check((int)Call("MinimumIndex",10000d)==0,"native smallest range when exactly 100 pC");
            Check((int)Call("MinimumIndex",5d)==10,"20 V remains absolute maximum");
            foreach(double bad in new[]{0d,-1d,4.99,double.NaN,double.PositiveInfinity})Check((int)Call("MinimumIndex",bad)==-1,"no invented out-of-device range");
            var q=Activator.CreateInstance(quality);Set(q,"RecommendedIndex",0);Set(q,"RecommendedFullScaleVolts",.01);Set(q,"Change",true);
            Set(q,"LocationLowOccupancy",true);Set(q,"LocationQualityPassed",false);Set(q,"LocationPeakFraction",.0263);
            object bounded=Call("Bound",q,5,1000d);
            Check(!Object.ReferenceEquals(q,bounded),"original advisor evidence not mutated");
            Check((int)Get(q,"RecommendedIndex")==0&&(double)Get(q,"RecommendedFullScaleVolts")==.01,"original recommendation preserved");
            Check((int)Get(bounded,"RecommendedIndex")==4&&(bool)Get(bounded,"Change"),"downrange moves only one hardware step above the charge floor");
            Check(!(bool)Get(bounded,"LocationQualityPassed")&&(double)Get(bounded,"LocationPeakFraction")==.0263,"quality not promoted to passed");
            Check((bool)Call("IsOrdinaryLowRangeAdvice",bounded,5),"clean low-only downrange suggestion is ordinary advice");
            foreach(string flag in new[]{"LocationHighOccupancy","DriverOverflow","DriverOverflowUncertain","RecommendationUncertain","MaximumRangeInsufficient","AtMaximumAndClipped"}){
                Set(bounded,flag,true);Check(!(bool)Call("IsOrdinaryLowRangeAdvice",bounded,5),flag+" remains outside ordinary reminder suppression");Set(bounded,flag,false);
            }
            foreach(string flag in new[]{"LocationClippedSamples","FullClippedSamples","OutsideClippedSamples"}){
                Set(bounded,flag,1);Check(!(bool)Call("IsOrdinaryLowRangeAdvice",bounded,5),flag+" cannot be hidden as low amplitude");Set(bounded,flag,0);
            }
            Set(bounded,"WindowValid",false);Check(!(bool)Call("IsOrdinaryLowRangeAdvice",bounded,5),"invalid window is not ordinary low advice");Set(bounded,"WindowValid",true);
            Set(bounded,"ExpectedCalibrationVolts",20.1);Check(!(bool)Call("IsOrdinaryLowRangeAdvice",bounded,5),"calibration demand above maximum is not suppressed");Set(bounded,"ExpectedCalibrationVolts",0d);
            Set(bounded,"RecommendedIndex",6);Check(!(bool)Call("IsOrdinaryLowRangeAdvice",bounded,5),"an upward demand is not a range-too-large reminder");Set(bounded,"RecommendedIndex",4);
            Check(!(bool)Call("IsLowOnlyAtFloor",bounded,5,1000d),"above floor still evaluates retry");
            bounded=Call("Bound",q,3,1000d);
            Check(!(bool)Get(bounded,"Change")&&(bool)Call("IsLowOnlyAtFloor",bounded,3,1000d),"at floor low-only no repeated downsizing or prompt");
            foreach(string flag in new[]{"LocationHighOccupancy","DriverOverflow","DriverOverflowUncertain","RecommendationUncertain","MaximumRangeInsufficient","AtMaximumAndClipped"}){
                Set(bounded,flag,true);Check(!(bool)Call("IsLowOnlyAtFloor",bounded,3,1000d),flag+" cannot be suppressed by floor");Set(bounded,flag,false);
            }
            foreach(string flag in new[]{"LocationClippedSamples","FullClippedSamples","OutsideClippedSamples"}){
                Set(bounded,flag,1);Check(!(bool)Call("IsLowOnlyAtFloor",bounded,3,1000d),flag+" retained");Set(bounded,flag,0);
            }
            Set(bounded,"WindowValid",false);Check(!(bool)Call("IsLowOnlyAtFloor",bounded,3,1000d),"invalid window never silently accepted");Set(bounded,"WindowValid",true);
            Set(bounded,"ExpectedCalibrationVolts",.11);Check((bool)Call("IsLowOnlyAtFloor",bounded,3,1000d),"unused expected calibration input alone does not enlarge low-only pressure floor");
            Set(bounded,"ExpectedCalibrationVolts",20.1);Check(!(bool)Call("IsLowOnlyAtFloor",bounded,3,1000d),"expected input beyond device maximum is never suppressed");
            Set(bounded,"ExpectedCalibrationVolts",.11);
            Check(!(bool)Call("IsLowOnlyAtFloor",bounded,-1,0d),"invalid indices safely rejected");
            Check(Rejects(()=>Call("Bound",q,2,1000d)),"existing below-floor capture fails instead of relabeling");
            Check(Rejects(()=>Call("Bound",q,10,4d)),"no valid physical range fails");
            Set(q,"RecommendedIndex",10);Set(q,"AtMaximumAndClipped",true);
            Check(Object.ReferenceEquals(q,Call("Bound",q,10,1000d)),"top-range risk remains exact original evidence");
            Set(q,"RecommendedIndex",-1);Check(Object.ReferenceEquals(q,Call("Bound",q,3,1000d)),"missing recommendation not invented");
            var same=Activator.CreateInstance(quality);Set(same,"LocationLowOccupancy",true);Set(same,"LocationQualityPassed",false);
            Set(same,"RecommendedIndex",5);Set(same,"ExpectedCalibrationVolts",.25);
            Check((bool)Call("IsLowOnlyWithoutAdjustment",same,5),"same recommendation low-only above floor does not ask impossible adjustment");
            Check(!(bool)Get(same,"LocationQualityPassed"),"ordinary prompt suppression does not change quality");
            foreach(string flag in new[]{"DriverOverflow","DriverOverflowUncertain","RecommendationUncertain"}){
                Set(same,flag,true);Check(!(bool)Call("IsLowOnlyWithoutAdjustment",same,5),flag+" is not a low-only result");Set(same,flag,false);
            }
            Check(!(bool)Call("MustHoldAtMaximum",same,10),"maximum low-only is still a reference, not a clipping risk");
            foreach(string flag in new[]{"DriverOverflow","DriverOverflowUncertain","RecommendationUncertain","MaximumRangeInsufficient","AtMaximumAndClipped","LocationHighOccupancy"}){
                Set(same,flag,true);Check((bool)Call("MustHoldAtMaximum",same,10),"maximum "+flag+" remains a hold without adjustment UI");Set(same,flag,false);
            }
            Set(same,"WindowValid",false);Check((bool)Call("MustHoldAtMaximum",same,10),"maximum invalid window remains blocked");Set(same,"WindowValid",true);
            Set(same,"RecommendedIndex",4);Set(same,"Change",true);
            Check((bool)Call("IsLowOnlyAtFloor",same,3,1000d),"actual low input at floor ignores unused predicted increase but retains evidence");
            Check((int)Get(same,"RecommendedIndex")==4&&(bool)Get(same,"Change")&&!(bool)Get(same,"LocationQualityPassed"),"predicted range and failed quality are not rewritten");
            var rt=a.GetType("HVX.Controls.PressureRangeReviewSnapshot",true);var review=Activator.CreateInstance(rt);
            var original=Activator.CreateInstance(quality);Set(original,"RecommendedIndex",0);Set(original,"Change",true);Set(original,"LocationQualityPassed",false);
            rt.GetField("Quality").SetValue(review,original);rt.GetField("NextRangeIndex").SetValue(review,0);rt.GetField("CanApply").SetValue(review,true);
            Call("BoundReview",review,5,1000d);
            Check((int)rt.GetField("NextRangeIndex").GetValue(review)==4&&(bool)rt.GetField("CanApply").GetValue(review),"nonzero next-range review downshifts only one step and remains above 100 pC");
            Check(Object.ReferenceEquals(original,rt.GetField("OriginalQuality").GetValue(review))&&!(bool)Get(rt.GetField("Quality").GetValue(review),"LocationQualityPassed"),"nonzero clamp preserves actual failed quality and original evidence");
            rt.GetField("NextRangeIndex").SetValue(review,0);rt.GetField("CanApply").SetValue(review,true);Call("BoundReview",review,3,1000d);
            Check((int)rt.GetField("NextRangeIndex").GetValue(review)==3&&!(bool)rt.GetField("CanApply").GetValue(review),"nonzero already at 100 pC floor has no adjustment to repeat");
            rt.GetField("Quality").SetValue(review,same);rt.GetField("NextRangeIndex").SetValue(review,4);rt.GetField("CanApply").SetValue(review,true);Call("BoundReview",review,3,1000d);
            Check((int)rt.GetField("NextRangeIndex").GetValue(review)==3&&!(bool)rt.GetField("CanApply").GetValue(review),"nonzero low-only floor ignores predicted calibration-driven increase");
            var resultType=a.GetType("HVX.Controls.PressureZeroVoltageResult",true);var result=Activator.CreateInstance(resultType,true);
            resultType.GetField("Quality",F).SetValue(result,same);resultType.GetField("ChargePc",F).SetValue(result,new[]{.5});
            var warn=a.GetType("HVX.Controls.PressureZeroVoltageCapture",true).GetMethod("ShouldWarnSmallReference",F);
            foreach(double peak in new[]{0d,1d,-.5,double.NaN,double.PositiveInfinity}){
                resultType.GetField("PeakPc",F).SetValue(result,peak);Check(!(bool)warn.Invoke(null,new[]{result}),"only a finite measured peak strictly between 0 and 1 pC warns");
            }
            resultType.GetField("PeakPc",F).SetValue(result,.5);Check((bool)warn.Invoke(null,new[]{result}),"valid small reference warns without claiming a proven calibration failure");
            Set(same,"WindowValid",false);Check(!(bool)warn.Invoke(null,new[]{result}),"no valid window does not speculate on range confusion");Set(same,"WindowValid",true);
            Set(same,"DriverOverflow",true);Check(!(bool)warn.Invoke(null,new[]{result}),"overflow does not masquerade as a small reference");Set(same,"DriverOverflow",false);
            Console.WriteLine("PASS "+checks+" checks; pure managed range policy; no hardware.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
