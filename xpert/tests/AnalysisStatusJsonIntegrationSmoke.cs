using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class AnalysisStatusJsonIntegrationSmoke
{
    static int checks;
    const BindingFlags All=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static bool BudgetRejected(Action action)
    {try{action();return false;}catch(TargetInvocationException e){return e.InnerException is InvalidDataException;}}
    static int Main(string[] args)
    {
        try {
            string file=Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll");
            var assembly=Assembly.LoadFrom(file);
            var engine=assembly.GetType("HVX.PdReview.ReviewAnalysisEngine",true);
            var threshold=engine.GetMethod("Threshold",All);
            Check((double)threshold.Invoke(null,new object[]{"{\"current_analysis\":{\"output_threshold\":45}}"})==45,"actual engine parses normal threshold");
            Check(double.IsNaN((double)threshold.Invoke(null,new object[]{"{}"})),"missing threshold remains unknown");
            string oversized=new string(' ',16*1024*1024+1);
            Check(BudgetRejected(()=>threshold.Invoke(null,new object[]{oversized})),"actual threshold rejects oversize instead of returning NaN");
            string deep=new string('[',25)+"0"+new string(']',25);
            Check(BudgetRejected(()=>threshold.Invoke(null,new object[]{deep})),"actual threshold rejects excessive nesting");
            Check(BudgetRejected(()=>threshold.Invoke(null,new object[]{"{\"current_analysis\":}"})),"malformed status is a failed record, not a missing threshold");
            var scopeType=assembly.GetType("HVX.PdReview.ReviewCalibrationInputScope",true);
            var scope=Activator.CreateInstance(scopeType,true);
            // Managed projection metadata only; never create/read a real CAL,
            // scratch directory or source project and never call Dispose on it.
            scopeType.GetField("ownedRoot",All).SetValue(scope,"test-only-not-a-file");
            var append=scopeType.GetMethod("AppendStatus",All);
            string projected=(string)append.Invoke(scope,new object[]{"{\"current_analysis\":{\"output_threshold\":45}}"});
            var parsed=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(projected);
            Check(parsed.ContainsKey("calibration_input_adapter")&&parsed.ContainsKey("current_analysis"),"actual calibration augmentation preserves status and evidence");
            Check(BudgetRejected(()=>append.Invoke(scope,new object[]{oversized})),"actual calibration status rejects oversize");
            Check(BudgetRejected(()=>append.Invoke(scope,new object[]{deep})),"actual calibration status rejects excessive nesting");
            var archive=assembly.GetType("HVX.PdReview.PdReviewControl",true).GetMethod("ArchiveJson",All);
            var serializer=(JavaScriptSerializer)archive.Invoke(null,null);
            Check(serializer.MaxJsonLength==16*1024*1024&&serializer.RecursionLimit==24,"archive and runtime use same bound");
            Console.WriteLine("ANALYSIS_STATUS_JSON_INTEGRATION_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;ProductEntryStarted=false;AlgorithmInvoked=false;UserProjectsWritten=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
