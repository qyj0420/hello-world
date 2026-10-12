using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Collections.Generic;

internal static class AnalysisInvariantDateSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type date;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static object Call(object o,string name,params object[] values){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(o,values);}
    static string DateCall(string method,object value){return (string)date.GetMethod(method,All).Invoke(null,new[]{value});}
    static Control Named(Control c,string name){if(c.Name==name)return c;foreach(Control child in c.Controls){var found=Named(child,name);if(found!=null)return found;}return null;}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"analysis-date-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));date=assembly.GetType("HVX.ProjectModel.AnalysisDateText",true);
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
            const string legacy="2026年05月05日16时31分 - 004",display="2026-05-05 16:31:00 - 004";
            foreach(string culture in new[]{"zh-CN","en-US","th-TH","ar-SA"})
            {
                Thread.CurrentThread.CurrentCulture=new CultureInfo(culture);Thread.CurrentThread.CurrentUICulture=new CultureInfo(culture);
                Check(DateCall("DisplayAutomaticName",legacy)==display,"legacy automatic display invariant: "+culture);
                Check(DateCall("FileSafeProjectPrefix","2026年05月05日16时31分")=="2026-05-05 16-31-00","new auto prefix invariant and filename-safe: "+culture);
                Check(DateCall("FormatLocalTime",new DateTime(2026,5,5,16,31,7))=="2026-05-05 16:31:07","Gregorian time column invariant: "+culture);
            }
            Check(DateCall("DisplayAutomaticName","2026-05-05 16-31-00 - 005")=="2026-05-05 16:31:00 - 005","new automatic name readable display");
            Check(DateCall("DisplayProjectName","2026-05-05_16-31-00")=="2026-05-05 16:31:00","new project directory numeric display");
            Check(DateCall("DisplayProjectName","2026年05月05日16时31分")=="2026-05-05 16:31:00","legacy pure date project numeric display");
            Check(DateCall("DisplayProjectName","人工项目 2026年05月05日16时31分")=="人工项目 2026年05月05日16时31分","custom project caption not transformed");
            foreach(string custom in new[]{"人工验收 2026年05月05日16时31分 - 004","2026年05月05日16时31分 - 004 复测","Feeder A - 004","2026年02月30日16时31分 - 004","2026年05月05日25时31分 - 004","2026年05月05日16时31分 - special","2026年05月05日16时31分","2026-05-05 16:31:00 - signed"})
                Check(DateCall("DisplayAutomaticName",custom)==custom,"custom/non-generated name preserved: "+custom);
            string project=Path.Combine(qa,"2026年05月05日16时31分");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度\t414\r\n",Encoding.UTF8);File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[256]);
            using(Control review=(Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);Set(review,"EnableAiFeatures",false);
                object tree=Get(review,"AnalysisTree");var catalog=(double[,])Call(tree,"GetDataCatalog");var p=new double[1,36];var w=new double[1,1024];
                p[0,0]=catalog[0,0];p[0,1]=catalog[0,1];p[0,2]=13.4;p[0,3]=30;p[0,5]=414;p[0,6]=170;p[0,7]=125e6;p[0,10]=100;p[0,11]=710;p[0,12]=610;p[0,13]=200;p[0,15]=180;p[0,23]=1;p[0,24]=1;p[0,32]=1023;p[0,33]=.9;
                for(int i=0;i<1024;i++)w[0,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
                Check((bool)Call(review,"LoadDataMatrices",p,w),"date fixture loads");Call(tree,"MarkAnalyzed",new object[]{new[]{loc}});
                Check((bool)Call(review,"SaveNamedAnalysis",legacy),"legacy exact stored name fixture saved");string id=(string)Get(review,"LoadedAnalysisId"),manifest=Path.Combine(project,"Analysis report",id,"analysis.json");byte[] before=File.ReadAllBytes(manifest);
                var json=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));
                Check((string)json["Name"]==legacy,"stored historical name unchanged");
                foreach(string key in new[]{"CreatedUtc","SavedUtc"}){DateTime instant;Check(((string)json[key]).EndsWith("Z",StringComparison.Ordinal)&&DateTime.TryParseExact((string)json[key],"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out instant),key+" remains invariant UTC ISO8601");}
                foreach(int language in new[]{0,1,5})
                {
                    Set(review,"LanguageIndex",language);Check((string)Get(review,"SuggestedAnalysisName")=="2026-05-05 16-31-00 - 005","new numbering follows legacy automatic sequence independent of language");
                    var entries=(Array)Call(review,"EnumerateSavedAnalyses");object entry=entries.GetValue(0);Check((string)Get(entry,"Name")==legacy&&(string)Get(entry,"DisplayName")==display&&(string)Get(entry,"Id")==id,"raw identity distinct from display name");
                    using(var dialog=(Form)Call(review,"CreateSavedAnalysisDialog",new object[]{entries}))
                    {
                        var list=(ListView)Named(dialog,"SavedAnalysisList");Check(list.Items[0].SubItems[1].Text==display,"picker name numeric for language "+language);Check(list.Items[0].Text==DateCall("FormatLocalTime",Get(entry,"CreatedLocal")),"picker time invariant for language "+language);
                        Check(Object.ReferenceEquals(list.Items[0].Tag,entry),"picker retains original record object for load/delete");
                    }
                }
                Check(before.SequenceEqual(File.ReadAllBytes(manifest))&&Directory.Exists(Path.GetDirectoryName(manifest)),"display/language changes do not rewrite archive or rename directory");
                Check((bool)Call(review,"LoadSavedAnalysis",id),"legacy archive remains loadable by original id");Check(((string)Get(review,"AnalysisArchiveStatus")).Contains(display),"loaded status uses display name");
                Set(review,"AnalysisProjectDisplayName","人工线路 A");Check((string)Get(review,"SuggestedAnalysisName")=="人工线路 A - 001","custom project name preserved");
                // Capture and persist an actual Traditional-Chinese chart, not just
                // a synthetic manifest: the writer validates its own archive too.
                Set(review,"LanguageIndex",18);var spectrum=(Control)Get(review,"Spectrum");spectrum.Size=new System.Drawing.Size(900,500);var handle=spectrum.Handle;Set(spectrum,"AcceptedOnly",false);
                Check((bool)Call(review,"RequestSpectrumReportChart"),"Traditional-Chinese spectrum attachment captured: "+Get(review,"ChartReportStatusMessage"));
                var attachments=(Array)Call(review,"GetPendingReportCharts");Check((int)Get(attachments.GetValue(0),"LanguageIndex")==18,"captured attachment retains language 18");
                Check((bool)Call(review,"SaveNamedAnalysis","Traditional chart smoke"),"language-18 chart archive saves: "+Get(review,"AnalysisArchiveStatus"));
                string chartId=(string)Get(review,"LoadedAnalysisId"),chartManifest=Path.Combine(project,"Analysis report",chartId,"analysis.json");string validManifest=File.ReadAllText(chartManifest);
                Check(((Array)Call(review,"EnumerateSavedAnalyses")).Cast<object>().Any(item=>(string)Get(item,"Id")==chartId),"language-18 archive is listed");
                Check((bool)Call(review,"LoadSavedAnalysis",chartId),"language-18 chart archive reloads");
                var competition=assembly.GetType("HVX.PdReview.PdReviewControl",true).GetMethod("CompetitionArchives",All);
                Check(((Array)competition.Invoke(null,new object[]{project})).Cast<object>().Any(item=>(string)Get(item,"Id")==chartId),"competition picker accepts Traditional-Chinese chart archive");
                var boundary=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(validManifest);
                var chart=((System.Collections.IEnumerable)boundary["Charts"]).Cast<object>().Select(item=>(Dictionary<string,object>)item).Single();
                foreach(int invalidLanguage in new[]{-1,19})
                {
                    chart["Language"]=invalidLanguage;File.WriteAllText(chartManifest,new JavaScriptSerializer().Serialize(boundary),new UTF8Encoding(false));
                    Check(!(bool)Call(review,"LoadSavedAnalysis",chartId),"out-of-range chart language rejected: "+invalidLanguage);
                    Check(!((Array)competition.Invoke(null,new object[]{project})).Cast<object>().Any(item=>(string)Get(item,"Id")==chartId),"competition skips invalid language: "+invalidLanguage);
                }
                File.WriteAllText(chartManifest,validManifest,new UTF8Encoding(false));Check((bool)Call(review,"LoadSavedAnalysis",chartId),"valid chart survives rejected boundary loads");
            }
            Console.WriteLine("ANALYSIS_INVARIANT_DATE_PASS="+checks);Console.WriteLine("EVIDENCE="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
