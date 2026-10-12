using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class AnalysisStartAndSettingsSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string name){return o.GetType().GetProperty(name,Flags).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,Flags).SetValue(o,value,null);}
    static object Field(object o,string name){return o.GetType().GetField(name,Flags).GetValue(o);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,Flags).Invoke(o,args);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static void Pump(){for(int i=0;i<4;i++)Application.DoEvents();}
    static void Save(Control control,string path){using(var bitmap=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(bitmap,control.ClientRectangle);bitmap.Save(path);}}
    static bool Prepare(Control review,Func<string,bool> confirmation,out string reason)
    {
        object[] args={confirmation,null};bool ready=(bool)Call(review,"PrepareSelectedAnalysisFromUi",args);reason=(string)args[1];return ready;
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass isolated release root and evidence directory.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var type=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"))
                .GetType("HVX.PdReview.PdReviewControl",true);
            using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1280,720),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var review=(Control)Activator.CreateInstance(type))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"EmbeddedMode",true);
                Set(review,"LocalAiLearningDirectory",Path.Combine(output,"synthetic-learning"));
                review.Dock=DockStyle.Fill;form.Controls.Add(review);form.Show();Pump();
                var auto=(Button)Field(review,"automaticAnalysisButton");var manual=(Button)Field(review,"manualAnalysisButton");
                var learning=(CheckBox)Field(review,"aiLearningModeBox");
                string reason;int prompts=0;
                Check(!Prepare(review,m=>{prompts++;return true;},out reason)&&reason.Length>0&&prompts==0,"Missing selection explains without offering to leave learning");
                Call(review,"RefreshWorkspaceUi");Check(auto.Enabled&&manual.Enabled,"Blocked commands remain reachable for an explanation");
                Check(auto.AccessibleDescription==reason,"Disabled reason is available to assistive technology");
                object tree=Get(review,"AnalysisTree");string path=Path.Combine(output,"synthetic-unanalysed.loc");
                Check((bool)Call(tree,"LoadRecords",new[]{path},new[]{1},new[]{12.2},new[]{1},new[]{101d}),"Synthetic LOC catalog loads without project files");
                Check((bool)Call(tree,"SetDataSelected",path,true),"Pending LOC explicitly checked");
                Check(Prepare(review,m=>{prompts++;return false;},out reason)&&reason.Length==0&&prompts==0,"Checked normal mode ready without a prompt");
                Set(review,"AiLearningMode",true);Check(learning.Checked&&learning.Text.Contains("开启"),"Learning button shows explicit ON state");
                Check(!Prepare(review,m=>{prompts++;return false;},out reason)&&prompts==1&&(bool)Get(review,"AiLearningMode"),"Declining prompt preserves learning mode");
                Check(auto.Enabled&&manual.Enabled&&auto.AccessibleDescription.Contains("学习"),"Learning-blocked analysis remains explainable");
                Check(!(bool)Call(review,"StartSelectedAnalysis",true)&&(bool)Get(review,"AiLearningMode"),"Direct analysis API retains learning protection");
                Save(review,Path.Combine(output,"analysis-learning-on.png"));
                Check(Prepare(review,m=>{prompts++;return true;},out reason)&&prompts==2&&!(bool)Get(review,"AiLearningMode"),"Explicit confirmation exits learning before returning ready");
                Check(!learning.Checked&&learning.Text.Contains("关闭"),"Learning button shows explicit OFF state");
                Set(review,"AiLearningMode",true);
                review.GetType().GetField("analysisBusy",Flags).SetValue(review,true);
                Check(!Prepare(review,m=>{prompts++;return true;},out reason)&&prompts==2&&(bool)Get(review,"AiLearningMode")&&reason.Contains("运行"),"Busy gate neither exits learning nor starts a second job");
                review.GetType().GetField("analysisBusy",Flags).SetValue(review,false);
                Check((bool)Call(tree,"SetDataSelected",path,false),"Synthetic selection cleared");
                Check(!Prepare(review,m=>{prompts++;return true;},out reason)&&prompts==2&&(bool)Get(review,"AiLearningMode"),"No-selection gate never turns learning off");
                Call(tree,"SetDataSelected",path,true);
                Check(!Prepare(review,m=>{Call(tree,"SetDataSelected",path,false);return true;},out reason)&&(bool)Get(review,"AiLearningMode"),"Selection changed during prompt cannot silently leave learning");
                Set(review,"AiLearningMode",false);Call(tree,"SetDataSelected",path,true);Call(review,"RefreshWorkspaceUi");
                foreach(int height in new[]{600,720})foreach(float points in new[]{9.5f,14.25f,19f})
                using(var font=new Font("Microsoft YaHei UI",points,FontStyle.Regular))
                using(var bold=new Font("Microsoft YaHei UI",points+1,FontStyle.Bold))
                {
                    form.ClientSize=new Size(1280,height);
                    Control settings=(Control)Field(review,"analysisSettingsButton"),suggestion=(Control)Field(review,"localAiSuggestionButton");
                    Font originalSettings=settings.Font,originalLearning=learning.Font,originalSuggestion=suggestion.Font;
                    settings.Font=bold;learning.Font=suggestion.Font=font;
                    try
                    {
                        Call(review,"SelectCompactSidebar",true,true);Call(review,"ApplySplitLayout");Pump();
                        Control[] controls={settings,learning,suggestion};
                        foreach(Control c in controls)
                        {
                            Size text=TextRenderer.MeasureText(c.Text,c.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
                            Check(c.Width>=text.Width+24&&c.Height>=text.Height+10,"Actual top-setting font fits: "+c.Name+" "+points);
                        }
                        Check(learning.Top>=settings.Bottom&&suggestion.Top>=learning.Bottom,"Three settings buttons do not overlap at "+points+"pt");
                        Save(review,Path.Combine(output,"analysis-settings-"+height+"-"+points.ToString(System.Globalization.CultureInfo.InvariantCulture)+"pt.png"));
                    }
                    finally{settings.Font=originalSettings;learning.Font=originalLearning;suggestion.Font=originalSuggestion;}
                }
                form.Close();
            }
            Console.WriteLine("ANALYSIS_START_SETTINGS_PASS="+checks);Console.WriteLine("EVIDENCE="+output);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
