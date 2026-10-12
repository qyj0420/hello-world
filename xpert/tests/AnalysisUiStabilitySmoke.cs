using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Isolated UI state/layout regression: no hardware, source projects or model writes.
internal static class AnalysisUiStabilitySmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static void SetField(object o,string name,object value){o.GetType().GetField(name,All).SetValue(o,value);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Reject(Action action,string text)
    {bool rejected=false;try{action();}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException;}Check(rejected,text);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"analysis-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"isolated-preferences.json")));
            using(var review=(Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl",true)))
            using(var host=new Form{ClientSize=new Size(1200,780),Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
            {
                host.Controls.Add(review);review.Dock=DockStyle.Fill;Set(review,"EnableAiFeatures",false);Set(review,"PromptSavedAnalysisOnProjectLoad",false);host.Show();
                string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);Set(review,"ProjectPath",project);
                var load=(Button)Field(review,"evaluateButton");var save=(Button)Field(review,"reportButton");Call(review,"RefreshReviewUi");
                Check(load.Enabled,"load available when idle with project");SetField(review,"analysisBusy",true);Call(review,"RefreshReviewUi");Check(!load.Enabled&&!save.Enabled,"load/save disabled at analysis start");
                int transitions=0;load.EnabledChanged+=(s,e)=>transitions++;save.EnabledChanged+=(s,e)=>transitions++;
                for(int i=0;i<58;i++){Call(review,"RefreshReviewUi");Call(review,"RefreshEvaluationUi");Application.DoEvents();Check(!load.Enabled&&!save.Enabled,"stable disabled actions during LOC "+i);}
                Check(transitions==0,"zero enable/disable transitions during 58 LOC updates");
                Check(!(bool)Call(review,"ShowSavedAnalysisDialog")&&!(bool)Call(review,"ShowSaveAnalysisDialog"),"load/save direct commands cannot open mid-analysis");
                SetField(review,"analysisBusy",false);Call(review,"RefreshReviewUi");Check(load.Enabled&&transitions==1,"idle restores load exactly once");
                Call(review,"RefreshReviewUi");Check(transitions==1,"idempotent idle state");
                Set(review,"EnableDefectRecognition",false);Set(review,"EnableAiFeatures",true);
                Reject(()=>Call(review,"RecognizeCurrentDischarge"),"recognition gate blocks sync API before runtime/model access");
                Reject(()=>Call(review,"RecognizeCurrentDischargeAsync"),"recognition gate blocks async API before runtime/model access");
                Check(Field(review,"task12Runtime")==null&&Get(review,"CurrentDischargeRecognition")==null,"disabled recognition does not create runtime or expose stale result");
                Call(review,"QueueDischargeIdentification");Check(!((Timer)Field(review,"recognitionUiTimer")).Enabled,"recognition timer stays stopped while separate gate off");
                Check((bool)Get(review,"EnableAiFeatures"),"recognition gate does not disable independent AI feature switch");Set(review,"EnableAiFeatures",false);
                Set(review,"ProtectedAnalysisSettingsManaged",true);
                using(var dialog=(Form)Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisSettingsDialog",true),All,null,new object[]{review},null))
                {
                    dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-20000,-20000);dialog.Show();Application.DoEvents();
                    Check(!((Control)Field(dialog,"advancedGroup")).Visible,"host-managed old advanced entry hidden");
                    Check(((Control)Field(dialog,"managedSettingsNote")).Visible,"redirect notice visible");
                    Check(dialog.Controls.Find("AnalysisAdvancedPassword",true).Length==0,"no obsolete password input");Check(!(bool)Get(dialog,"AdvancedUnlocked"),"host ownership remains intact without passwords");
                    Check(!((Control)Field(dialog,"modelPath")).Enabled&&!((Control)Field(dialog,"maximum")).Enabled,"protected values remain uneditable");
                    using(var png=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(png,dialog.ClientRectangle);png.Save(Path.Combine(qa,"managed-analysis-settings.png"));}
                }
            }
            using(var spectrum=(Control)Activator.CreateInstance(assembly.GetType("HVX.UnifiedSpectrum.UnifiedSpectrumControl",true),All,null,new object[]{true},null))
            {
                var matrix=new double[120,36];for(int r=0;r<120;r++){matrix[r,0]=1+r%3;matrix[r,2]=12.2;matrix[r,3]=30+(r%6)*45;matrix[r,5]=414;matrix[r,13]=80+r%7;matrix[r,15]=r%2==0?150:-100;matrix[r,35]=1;}
                Set(spectrum,"UseHostStatusBar",true);Call(spectrum,"LoadDataMatrix",matrix);
                foreach(float size in new[]{9.5f,14f,20f})
                using(var font=new Font("Microsoft YaHei UI",size))
                {
                  spectrum.Font=font;spectrum.Size=new Size(1400,850);
                  foreach(int language in new[]{0,1,18})foreach(int mode in new[]{11,12})
                  {
                    Set(spectrum,"LanguageIndex",language);Set(spectrum,"DisplayModeIndex",mode);
                    var plot=(Rectangle)Call(spectrum,"PlotArea",spectrum.ClientRectangle);var title=(Rectangle)Call(spectrum,"DensityScaleTitleBounds",plot);
                    int h=(int)Get(spectrum,"DensityScaleTextHeight");Check(title.Bottom<=plot.Top-(h+1)/2-5,"colorbar title clears top tick "+mode+"/"+size+"/"+language);
                    var header=Call(spectrum,"HeaderLayout",spectrum.Width);
                    foreach(object item in (IEnumerable)Field(header,"Legend"))Check(title.Top>((Rectangle)Field(item,"Bounds")).Bottom,"colorbar title clears phase legend");
                    foreach(string name in new[]{"Title","Summary","Region","Classification","Order","Error","DischargeType"}){var rect=(Rectangle)Field(header,name);if(!rect.IsEmpty)Check(title.Top>rect.Bottom,"colorbar title clears header "+name);}
                    using(var png=new Bitmap(spectrum.Width,spectrum.Height)){spectrum.DrawToBitmap(png,spectrum.ClientRectangle);if(size==14&&language==0)png.Save(Path.Combine(qa,"density-mode-"+mode+".png"));}
                  }
                  spectrum.Font=SystemFonts.DefaultFont;
                }
            }
            Console.WriteLine("PASS AnalysisUiStabilitySmoke "+checks+" checks; QA="+qa);return 0;
        }
        catch(Exception error){Console.WriteLine(error);return 1;}
    }
}
