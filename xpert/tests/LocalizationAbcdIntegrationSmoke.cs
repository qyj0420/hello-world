using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class LocalizationAbcdIntegrationSmoke
{
    static int checks;
    static void Check(bool ok,string note){if(!ok)throw new Exception(note);checks++;Console.WriteLine("PASS "+note);}
    static string Hash(string file){using(var s=File.OpenRead(file))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s));}
    static Dictionary<string,object> Map(object value){return (Dictionary<string,object>)value;}
    [STAThread]static int Main(string[] args){try{
        string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]),modules=Path.Combine(build,"modules");
        AlgorithmProtectionRegression.Fixture(qa,680,1000);
        string loc=Path.Combine(qa,"A_12.0kV_1.loc"),before=Hash(loc);
        var host=Assembly.LoadFrom(Path.Combine(build,"Xiongxin.OWTS.Desktop.exe"));
        var bridge=host.GetType("Xiongxin.OWTS.Desktop.LocalizationCBridge",true);
        var make=bridge.GetMethod("BuildRequest",BindingFlags.NonPublic|BindingFlags.Static);
        var run=bridge.GetMethod("RunAsync",BindingFlags.NonPublic|BindingFlags.Static);
        var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};
        var reviewAssembly=Assembly.LoadFrom(Path.Combine(modules,"DataAnalysis","HVX.PdReview.V1.0.dll"));
        using(var review=(Control)Activator.CreateInstance(reviewAssembly.GetType("HVX.PdReview.PdReviewControl",true))){
            var entry=review.Controls.Find("ReviewLocalizationComparison",true).Single() as Button;
            Check(entry!=null&&!entry.Visible&&!entry.TabStop,"comparison UI is hidden while the underlying bridge remains testable");
            int requests=0;
            review.GetType().GetEvent("LocalizationComparisonRequested").AddEventHandler(review,new EventHandler((s,e)=>requests++));
            typeof(Button).GetMethod("OnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(entry,new object[]{EventArgs.Empty});
            Check(requests==1,"comparison click raises exactly one request");
            review.GetType().GetField("analysisBusy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(review,true);
            typeof(Button).GetMethod("OnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(entry,new object[]{EventArgs.Empty});
            Check(requests==1,"comparison cannot interrupt an active analysis");
            review.GetType().GetField("analysisBusy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(review,false);
        }
        var dialogType=host.GetType("Xiongxin.OWTS.Desktop.LocalizationCForm",true);
        using(var dialog=(Form)Activator.CreateInstance(dialogType,BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{0},null)){
            dialogType.GetMethod("SetInitialRecord",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(dialog,new object[]{loc});
            Check(((TextBox)dialogType.GetField("loc",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(dialog)).Text==loc,"selected LOC carried into review dialog");
            Check(!((CheckBox)dialogType.GetField("reference",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(dialog)).Checked,"reference plane not silently confirmed");
        }
        foreach(string method in new[]{"C","D"}){
            object[] input={modules,loc,170d,170d,false,"A",CancellationToken.None,null,method};
            var request=Map(make.Invoke(null,input));
            var record=Map(((object[])request["records"])[0]);
            Check((double)record["voltage_kv"]==12,"actual voltage metadata "+method);
            Check(((double[])record["samples"]).Length==4096,"full samples "+method);
            Check((double)Map(request["config"])["sample_rate_hz"]==100000000,"actual sample rate "+method);
            var task=(Task<string>)run.Invoke(null,new object[]{modules,"",request,CancellationToken.None,null,120});
            string output=task.GetAwaiter().GetResult();File.WriteAllText(Path.Combine(qa,method+".json"),output);
            var result=Map(json.DeserializeObject(output));
            Check((string)result["status"]=="ok","native host round trip "+method);
            Check((string)result["version"]==(method=="C"?"C-0.3.0":"D-0.1.1"),"pinned version "+method);
            if(method=="D"){
                var evidence=(object[])result["source_ab_evidence"];
                Check(evidence.Length==2,"both real A/B evidence retained");
                Check(evidence.All(x=>Object.Equals(Map(x)["complete"],false)),"incomplete evidence not promoted");
                Check(result.ContainsKey("source_c_result"),"full C alternatives retained");
                Check(!output.Contains("\"physical_accepted\": true"),"no physical acceptance asserted");
            }
            Check(before==Hash(loc),"source unchanged "+method);
        }
        using(var canceled=new CancellationTokenSource()){
            canceled.Cancel();bool caught=false;
            try{make.Invoke(null,new object[]{modules,loc,170d,170d,false,"A",canceled.Token,null,"C"});}
            catch(TargetInvocationException ex){caught=ex.InnerException is OperationCanceledException;}
            Check(caught,"canceled build does not analyze");
        }
        bool unknown=false;
        try{make.Invoke(null,new object[]{modules,loc,170d,170d,false,"ALL",CancellationToken.None,null,"D"});}
        catch(TargetInvocationException){unknown=true;}
        Check(unknown,"D rejects unknown phase without guessing");
        File.WriteAllText(loc,File.ReadAllText(loc).Replace("Voltage\t12.0","Voltage\t0.0"));
        var zero=Map(make.Invoke(null,new object[]{modules,loc,170d,170d,false,"A",CancellationToken.None,null,"C"}));
        Check((double)Map(((object[])zero["records"])[0])["voltage_kv"]==0,"known zero voltage is not missing");
        Console.WriteLine("ABCD_HOST_INTEGRATION_PASS="+checks+"; HardwareAccessed=False; PhysicalAcceptance=False");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
