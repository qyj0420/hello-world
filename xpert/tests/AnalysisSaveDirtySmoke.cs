using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class AnalysisSaveDirtySmoke
{
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static object Get(object o,string n){return o.GetType().GetProperty(n).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(o,a);}
    static bool Dirty(object o){return (bool)Get(o,"HasUnsavedAnalysisChanges");}
    static void CheckSaveDialog(Control review,string qa,int language)
    {
        Set(review,"LanguageIndex",language);Set(review,"AnalysisSaveNameProvider",null);Exception failure=null;
        using(var timer=new Timer{Interval=100})
        {
            timer.Tick+=(s,e)=>
            {
                var form=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Controls.Find("AnalysisName",true).Length>0);if(form==null)return;
                timer.Stop();
                try
                {
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(qa,"save-analysis-"+language+".png"));}
                    var input=form.Controls.Find("AnalysisName",true)[0];Check(input.Visible&&input.Width>300,"name input readable at actual DPI");
                    var footer=form.Controls.OfType<FlowLayoutPanel>().Single();
                    foreach(Button button in footer.Controls.OfType<Button>())
                    {
                        Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding).Width+12,"save dialog button text fits: "+button.Text+"; actual="+button.Width+"; text="+TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding).Width);
                        Check(footer.ClientRectangle.Contains(button.Bounds),"save dialog button remains inside footer");
                    }
                    foreach(Control sibling in form.Controls)if(sibling!=input)Check(!input.Bounds.IntersectsWith(sibling.Bounds),"name input does not overlap explanation or buttons");
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(qa,"save-analysis-"+language+".png"));}
                }
                catch(Exception ex){failure=ex;}
                finally{form.DialogResult=DialogResult.Cancel;form.Close();}
            };
            timer.Start();Check(!(bool)Call(review,"ShowSaveAnalysisDialog"),"actual dialog Cancel does not save");
        }
        if(failure!=null)throw failure;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"analysis-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string dll=Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll");if(!File.Exists(dll))dll=Path.Combine(root,"HVX.PdReview.V1.0.dll");
            var assembly=Assembly.LoadFrom(dll);var type=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            string project=Path.Combine(qa,"客户项目");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"始端地点\t起点\r\n终端地点\t终点\r\n电缆长度\t414\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);
            string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[256]);
            using(var review=(Control)Activator.CreateInstance(type))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Check(!Dirty(review),"fresh control clean");
                Set(review,"AnalysisCorrelationErrorPercent",12d);Check(!Dirty(review),"settings without analysis do not warn");
                Set(review,"ProjectPath",project);Check(!Dirty(review),"raw project load clean");
                string first=(string)Get(review,"SuggestedAnalysisName");Check(first=="客户项目 - 001","project name plus first sequence");
                object tree=Get(review,"AnalysisTree");var catalog=(double[,])Call(tree,"GetDataCatalog");Check(catalog.GetLength(0)==1,"synthetic LOC recognized");
                var p=new double[1,36];var w=new double[1,2048];p[0,0]=catalog[0,0];p[0,1]=catalog[0,1];p[0,2]=13.4;p[0,3]=45;p[0,5]=414;p[0,6]=170;p[0,7]=125000000;p[0,9]=0;p[0,10]=100;p[0,11]=710;p[0,12]=4.88;p[0,13]=200;p[0,15]=180;p[0,16]=180;p[0,20]=100;p[0,21]=.92;p[0,32]=2047;p[0,33]=.91;
                for(int i=0;i<2048;i++)w[0,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
                Check((bool)Call(review,"LoadDataMatrices",p,w),"synthetic analysis loaded");Call(tree,"MarkAnalyzed",new object[]{new[]{loc}});Check(Dirty(review),"new measurement analysis is unsaved");
                Set(review,"ProjectPath",project);Check(Dirty(review)&&(int)Get(review,"SourceRowCount")==1,"same project context preserves dirty data");
                int saves=0;type.GetEvent("AnalysisSaved").AddEventHandler(review,new EventHandler((s,e)=>{Check(!Dirty(review),"successful save event observes clean state");saves++;}));
                Set(review,"AnalysisSaveNameProvider",(Func<string,string>)(suggested=>null));Check(!(bool)Call(review,"ShowSaveAnalysisDialog")&&Dirty(review),"cancel leaves unsaved analysis");
                Set(review,"AnalysisSaveNameProvider",(Func<string,string>)(suggested=>"bad:name"));Check(!(bool)Call(review,"ShowSaveAnalysisDialog")&&Dirty(review)&&saves==0,"invalid name does not save");
                Set(review,"AnalysisSaveNameProvider",(Func<string,string>)(suggested=>suggested));
                Check((bool)Call(review,"ShowSaveAnalysisDialog"),"named save succeeds: "+Get(review,"LastError"));Check(!Dirty(review)&&saves==1,"success clears dirty once");
                string id=(string)Get(review,"LastSavedAnalysisId");Check((string)Get(review,"SuggestedAnalysisName")=="客户项目 - 002","next name scans existing archives");
                Set(review,"LanguageIndex",18);Check(!Dirty(review),"language alone does not dirty analysis");
                Call(review,"SelectSourceRow",0);Check(!Dirty(review),"browsing alone remains clean");
                Call(review,"MarkCurrentReviewed");Check(Dirty(review),"explicit confirmation is persisted as a change");
                Set(review,"AnalysisCorrelationErrorPercent",13d);Check(Dirty(review),"archive parameter change detected");
                Check(!(bool)Call(review,"SaveNamedAnalysis",first)&&Dirty(review)&&saves==1,"duplicate name never overwrites or clears dirty");
                Check(!(bool)Call(review,"LoadSavedAnalysis","missing-id")&&Dirty(review),"failed load preserves unsaved state");
                Check((bool)Call(review,"LoadSavedAnalysis",id),"saved analysis reloads: "+Get(review,"LastError"));Check(!Dirty(review)&&saves==1,"saved load clean and is not another save");
                var preview=(double[])Call(review,"GetCurrentParameterRow");preview[15]+=1;Call(review,"CommitRow",0,preview,true);Check(Dirty(review),"uncommitted drag preview cannot be lost on navigation");
                Set(review,"ProjectPath",project.ToUpperInvariant()+Path.DirectorySeparatorChar);Check(Dirty(review)&&(int)Get(review,"SourceRowCount")==1,"equivalent Windows project path preserves unsaved analysis");
                Check((bool)Call(review,"LoadSavedAnalysis",id)&&!Dirty(review),"reloading discards preview only on explicit load");
                Call(review,"ClearAllAnalysis");Check(Dirty(review),"deleting all analysis remains a change");
                Check((bool)Call(review,"SaveNamedAnalysis","客户项目 - 007"),"explicit unique sequence save");Check((string)Get(review,"SuggestedAnalysisName")=="客户项目 - 008","next number follows highest saved suffix");
                Set(review,"AnalysisProjectDisplayName","旧项目显示名");string second=Path.Combine(qa,"另一个项目");Directory.CreateDirectory(second);Set(review,"ProjectPath",second);Check(!Dirty(review),"new raw project starts clean");Check((string)Get(review,"SuggestedAnalysisName")=="另一个项目 - 001","new project does not inherit prior display caption");
                foreach(int language in new[]{0,1,18})CheckSaveDialog(review,qa,language);
            }
            Console.WriteLine("ANALYSIS_SAVE_DIRTY_PASS="+checks);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
