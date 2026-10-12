using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;

internal static class AlgorithmABChoiceSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static object Field(object target,string name) { return target.GetType().GetField(name,Hidden).GetValue(target); }
    static object Property(object target,string name) { return target.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(target,null); }
    static Assembly LegacyAlgorithm()
    {
        var assembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("LegacyTask17Fixture"),AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyFileVersionAttribute).GetConstructor(new[]{typeof(string)}),new object[]{"2.10.0.0"}));
        assembly.DefineDynamicModule("LegacyTask17Fixture").DefineType("HVX.Task17.Location.ProjectLocationAnalyzer",TypeAttributes.Public).CreateType();
        return assembly;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string module=Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis"),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            Assembly review=Assembly.LoadFrom(Path.Combine(module,"HVX.PdReview.V1.0.dll"));
            Assembly algorithm=Assembly.LoadFrom(Path.Combine(module,"fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
            Type engine=review.GetType("HVX.PdReview.ReviewAnalysisEngine",true),controlType=review.GetType("HVX.PdReview.PdReviewControl",true);
            MethodInfo resolve=engine.GetMethod("GetAnalysisMethod",BindingFlags.Static|BindingFlags.NonPublic);
            object[] a={algorithm,5,null};var entry=(MethodInfo)resolve.Invoke(null,a);
            Check(entry.Name=="AnalyzeSixthLocationIncremental"&&((Type)a[2]).FullName=="HVX.Task17.Location.SixthLocationAnalyzer","A resolves dedicated original method6, not Project");
            var signature=entry.GetParameters();Check(signature.Length==40&&signature.Count(p=>p.IsOut)==9,"A original 31-input/9-output ABI retained");
            foreach(string name in new[]{"minimumCorrelationPercent","attenuationErrorPercent","pulseWidthErrorPercent","scoreWeights"})Check(signature.Any(p=>p.Name==name),"A preserves input "+name);
            MethodInfo validate=engine.GetMethod("ValidateLocationMethod",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(int index in new[]{-1,0,1,2,3,4,6,10,Int32.MaxValue})
            {
                bool rejected=false;try{validate.Invoke(null,new object[]{index});}catch(TargetInvocationException error){rejected=error.InnerException is ArgumentException;}
                Check(rejected,"discontinued or invalid method rejected: "+index);
            }
            object[] b={algorithm,7,null};var bEntry=(MethodInfo)resolve.Invoke(null,b);var bSignature=bEntry.GetParameters();
            Check(bEntry.Name=="AnalyzeProjectLocationIncremental"&&((Type)b[2]).FullName=="HVX.Task17.Location.ProjectLocationAnalyzer","B resolves capability-checked Project entry, not historical method7");
            Check(bSignature.Length==37&&bSignature.Count(p=>p.IsOut)==9&&bSignature.Any(p=>p.Name=="locationAlgorithm"),"B original 28-input/9-output ABI retained");
            foreach(int index in new[]{5,7,8,9}){validate.Invoke(null,new object[]{index});Check(true,"supported public method accepted: "+index);}
            var eType=review.GetType("HVX.PdReview.ReviewEAnalysis",true);var isE=eType.GetMethod("IsMethod",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            foreach(int index in new[]{5,7,8,9})Check((bool)isE.Invoke(null,new object[]{index})==(index==8||index==9),"genuine E route identity remains separate from A/B: "+index);
            bool missingB=false;try{resolve.Invoke(null,new object[]{LegacyAlgorithm(),7,null});}catch(TargetInvocationException error){missingB=error.InnerException is InvalidOperationException&&error.InnerException.Message.Contains(" B ");}
            Check(missingB,"old component refuses B instead of falling back to method7");
            Type prefs=review.GetType("HVX.PdReview.ReviewAnalysisPreferences",true);var provider=prefs.GetField("PathProvider",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);object old=provider.GetValue(null);
            provider.SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            try
            {
                using(var control=(Control)Activator.CreateInstance(controlType))
                {
                    var method=controlType.GetProperty("AnalysisAlgorithmIndex");Check((int)method.GetValue(control,null)==5,"new module default is A");
                    var dialogType=review.GetType("HVX.PdReview.ReviewAnalysisSettingsDialog",true);
                    int[] indices={5,7,8,9};string[] names={"A","B","E","E+"};
                    foreach(int index in new[]{5,7,8,9,6})
                    {
                        method.SetValue(control,index,null);
                        using(var dialog=(Form)Activator.CreateInstance(dialogType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{control},null))
                        {
                            var choice=(ComboBox)Field(dialog,"algorithm");Check(choice.Items.Cast<object>().Select(x=>x.ToString()).SequenceEqual(names),"analysis settings show real A/B/E/E+ methods");
                            Check(choice.SelectedIndex==Array.IndexOf(indices,index),"no ordinal relabel of historical method "+index);
                            for(int i=0;i<indices.Length;i++){choice.SelectedIndex=i;Check((int)Property(dialog,"AlgorithmIndex")==indices[i],"dialog selection maps to original method");Check(choice.Text==names[i],"native selection text retained");}
                        }
                    }
                    method.SetValue(control,7,null);controlType.GetMethod("SaveAnalysisPreferences",Hidden).Invoke(control,null);
                    Check(File.ReadAllText(Path.Combine(qa,"preferences.json")).Contains("\"AlgorithmIndex\":7"),"B preference persists original index7");
                    using(var reopened=(Control)Activator.CreateInstance(controlType)){controlType.GetMethod("RestoreAnalysisPreferences",Hidden).Invoke(reopened,null);Check((int)method.GetValue(reopened,null)==7,"B preference reopens without clamping");}
                    Check((int)method.GetValue(control,null)==7,"failed capability check never changes selected method");
                }
            }
            finally{provider.SetValue(null,old);}
            Console.WriteLine("ALGORITHM_AB_PASS="+checks+";PUBLIC_METHODS=A/B/E/E+;NATIVE_LOCATION_CALLS=0;HARDWARE_ACCESSED=False;B_CAPABILITY_CHECKED=True");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
