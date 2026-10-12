using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ReviewReportCombinedSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new InvalidOperationException(label);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){var t=o as Type??o.GetType();return t.GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o is Type?null:o,a);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.GetFullPath(args[0]),fixture=Path.Combine(qa,"review-report-"+Guid.NewGuid().ToString("N")),project=Path.Combine(fixture,"project");Directory.CreateDirectory(project);
            var analysis=Assembly.LoadFrom(Path.Combine(qa,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var reports=Assembly.LoadFrom(Path.Combine(qa,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(fixture,"prefs.json")));
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"414\"\r\n相电压: = \"8.7\"\r\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t接头1\t冷缩\t80m\r\n1\t终端\t终端\t414m\r\n",new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(project,"A_13.40kV_1.loc"),new byte[256]);
            using(var review=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
            using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(1600,1000)})
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);host.Controls.Add(review);review.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                var catalog=(double[,])Call(Get(review,"AnalysisTree"),"GetDataCatalog");var p=new double[4,36];var w=new double[4,4096];
                for(int r=0;r<4;r++)
                {
                    p[r,0]=catalog[0,0];p[r,1]=catalog[0,1];p[r,2]=catalog[0,2];p[r,3]=45+r;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;p[r,9]=r;
                    p[r,10]=100;p[r,11]=592;p[r,12]=492;p[r,13]=79.5+r*.1;p[r,15]=p[r,16]=180+r*10;p[r,20]=100;p[r,21]=.92;p[r,32]=4095;p[r,33]=.91;p[r,35]=r==0?1:0;
                    for(int i=0;i<4096;i++)w[r,i]=(180+r*10)*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-592)/23d,2));
                }
                Check((bool)Call(review,"LoadDataMatrices",p,w),"load complete synthetic waveforms");
                Call(Get(review,"PdMap"),"SetConnectors",new double[]{80});Set(Get(review,"Spectrum"),"OperatingVoltageKv",8.7d);
                Check((bool)Call(review,"SelectSourceRow",1)&&(bool)Call(review,"MarkCurrentUncertain"),"explicit uncertainty");
                Check((bool)Call(review,"SelectSourceRow",2)&&(bool)Call(review,"DeleteCurrent"),"explicit removal");
                Check((bool)Call(review,"SelectSourceRow",3)&&(string)Get(review,"CurrentReviewState")=="Pending","navigation remains pending");
                Check((bool)Call(review,"SaveNamedAnalysis","combined-final"),"save actual named analysis: "+Get(review,"LastError"));
                string id=(string)Get(review,"LastSavedAnalysisId");string archive=Path.GetDirectoryName(Directory.GetFiles(project,"analysis.json",SearchOption.AllDirectories).Single());
                Check((int)Get(review,"EvaluationAnalysisCount")==0,"four-state decisions and save do not calculate conclusions");
                string session=File.ReadAllText(Path.Combine(archive,"session.json"));Check(session.Contains("ReviewDecisionVersion")&&session.Contains("ReportInputContext"),"both merged session contexts saved together");
                object package=Call(reports.GetType("HVX.ReportGenerator.AnalysisPackageReader",true),"Read",archive);
                Check((bool)Get(package,"HasEffectiveSelectionSnapshot")&&(bool)Get(package,"HasReportInputSnapshot"),"actual named archive validates in report: "+Get(package,"ReportInputStatus"));
                var original=(double[,])Get(package,"LocationMatrix");var stats=(double[,])Get(package,"StatisticsMatrix");var input=(double[,])Get(package,"ConclusionInput");
                Check(original.GetLength(0)==4&&original[0,13]==79.5,"all original states and raw coordinate retained");
                Check(stats.GetLength(0)==3&&stats.Cast<double>().Any(),"deleted row excluded from report display scope");
                Check(input.GetLength(0)==1&&input[0,35]==1,"only accepted row enters report conclusion");
                Check(input[0,13]==80&&stats[0,13]==80,"report plot and conclusion inherit same joint approximation");
                Check((bool)Call(review,"LoadSavedAnalysis",id),"reload actual archive with both contexts");
                foreach(var row in new[]{0,1,2,3})
                {
                    Check((bool)Call(review,"SelectSourceRow",row),"select restored row "+row);
                    Check((string)Get(review,"CurrentReviewState")==new[]{"Accepted","Uncertain","Removed","Pending"}[row],"restored state matches report source "+row);
                }
                Check((int)Get(review,"EvaluationAnalysisCount")==0,"reload and navigation do not run conclusion algorithm");
                File.WriteAllText(Path.Combine(fixture,"verification.json"),new JavaScriptSerializer().Serialize(new{Status="PASS",Checks=checks,Archive=archive,HardwareAccessed=false,ProductMainInvoked=false,UserProjectsModified=false}),Encoding.UTF8);
            }
            Console.WriteLine("REVIEW_REPORT_COMBINED_PASS="+checks+"; fixture="+fixture+"; hardware=false; product_main=false");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e is TargetInvocationException?((TargetInvocationException)e).InnerException:e);return 1;}
    }
}
