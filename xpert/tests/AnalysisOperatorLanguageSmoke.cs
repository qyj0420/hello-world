using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

static class AnalysisOperatorLanguageSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void Call(object o,string name){o.GetType().GetMethod(name,All).Invoke(o,null);}
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Console.WriteLine("Creating offline review control");var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
        using(var review=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.PdReviewControl",true)))
        using(var form=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000),ClientSize=new Size(1500,950)}){
            Console.WriteLine("Attaching offline review control");
            review.GetType().GetProperty("EnableAiFeatures").SetValue(review,false,null);
            form.Controls.Add(review);review.Dock=DockStyle.Fill;Console.WriteLine("Showing offline review fixture");form.Show();Application.DoEvents();Console.WriteLine("Fixture ready");
            var entry=(Button)Field(review,"localizationComparisonButton");
            Check(!entry.Visible&&!entry.TabStop,"cross-check entry is hidden and excluded from keyboard navigation");
            int requests=0;review.GetType().GetEvent("LocalizationComparisonRequested").AddEventHandler(review,new EventHandler((s,e)=>requests++));
            entry.PerformClick();Check(requests==0,"hidden entry cannot be activated by ordinary button interaction");
            typeof(Button).GetMethod("OnClick",All).Invoke(entry,new object[]{EventArgs.Empty});
            Check(requests==1,"underlying cross-check integration remains available to offline verification");
            var history=(CheckBox)Field(review,"aHistoryCheck");var historyState=(Label)Field(review,"aHistoryStateLabel");
            Check(!history.Visible&&!history.TabStop&&!historyState.Visible,"history aid entry and description are hidden");
            review.GetType().GetProperty("AHistoryAuxiliaryEnabled").SetValue(review,true,null);
            Check((bool)review.GetType().GetProperty("AHistoryAuxiliaryEnabled").GetValue(review,null)&&!history.Visible&&!historyState.Visible,"history backend is retained without showing its controls");
            var advice=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewDisplayRecommendation",true));
            advice.GetType().GetProperty("ReasonCode").SetValue(advice,"MixedStable",null);
            advice.GetType().GetProperty("AcceptedRows").SetValue(advice,10,null);advice.GetType().GetProperty("RetainedRows").SetValue(advice,7,null);
            review.GetType().GetField("displayRecommendation",All).SetValue(review,advice);Call(review,"RefreshDisplayAdviceUi");
            var label=(Label)Field(review,"displayRecommendationLabel");var details=(LinkLabel)Field(review,"displayAdviceDetails");
            Check(!label.Visible&&!details.Visible&&!details.TabStop,"recommendation explanation and details link are hidden");
            Check(label.AccessibleDescription.Contains("7/10"),"recommendation evidence retained internally");
            var auto=(CheckBox)Field(review,"autoDisplayBox");
            Check(auto.Visible,"automatic recommendation switch remains available");
            foreach(var size in new[]{new Size(1100,560),new Size(1500,950)}){
                form.ClientSize=size;Application.DoEvents();Call(review,"RefreshWorkspaceUi");Call(review,"ApplyCompactSidebarVisibility");
                Check(!entry.Visible&&!history.Visible&&!historyState.Visible&&!label.Visible&&!details.Visible,"resize and workspace refresh cannot restore hidden controls");
            }
            Directory.CreateDirectory(args[1]);using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(args[1],"analysis-operator-language.png"));}
        }
        var pressure=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        using(var dashboard=(Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureSpectrumDashboardView",true),true))
        using(var form=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000),ClientSize=new Size(1100,700)}){
            form.Controls.Add(dashboard);dashboard.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            var label=(Label)Field(dashboard,"adviceLabel");var auto=(CheckBox)Field(dashboard,"automaticCheck");
            Call(dashboard,"RefreshAdvice");
            Check(!label.Visible&&auto.Visible,"pressure page hides explanation but retains automatic recommendation switch");
            Check(dashboard.GetType().GetProperty("DisplayRecommendationJson",All).GetValue(dashboard,null)!=null,"pressure recommendation record remains available");
        }
        Console.WriteLine("ANALYSIS_OPERATOR_LANGUAGE_PASS="+checks+"; HARDWARE=False; ALGORITHM_CHANGED=False");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
