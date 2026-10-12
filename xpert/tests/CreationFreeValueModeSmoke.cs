using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationFreeValueModeSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Type editorType, pressureType; static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool v,string n){checks++;if(!v)throw new Exception(n);}
    static Control Editor(string settings){var c=(Control)Activator.CreateInstance(editorType);Set(c,"ShowDialogMessages",false);Set(c,"EmbeddedMode",true);Set(c,"SettingsPath",settings);Check((bool)Call(c,"ReloadSettings"),"isolated settings load");Set(c,"LanguageIndex",0);return c;}
    static void Valid(Control c){Set(c,"StartName","模式测试起点");Set(c,"EndName","模式测试终点");Set(c,"CableMode","一体三芯");Call(c,"SetPhaseCableLengths",100d,100d,100d);Set(c,"PhaseVoltage","6.0kV");}
    static string Save(Control c,string root){Check((bool)Call(c,"SaveProjectToRoot",root),"project saves: "+Get(c,"LastError"));return (string)Get(c,"LastSavedProjectPath");}
    static int Steps(Control c){return ((IList)Get(c,"currentPressureSteps")).Count;}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"free-mode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            editorType=Assembly.LoadFrom(Path.Combine(build,"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll")).GetType("HVX.Controls.ProjectCreationControl",true);
            pressureType=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
            string settings=Path.Combine(qa,"settings.ini"),freeProject,oldProject;
            using(var c=Editor(settings))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(1100,720),StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)}) {
                c.Dock=DockStyle.Fill;host.Controls.Add(c);host.Show();Call(c,"SetOptionalSectionExpanded","TestConfiguration",true);Application.DoEvents();
                Check((string)Get(c,"PressureMode")=="新电缆"&&Steps(c)==11,"first use retains new-cable default");
                Set(c,"PressureMode","任意值");var grid=(DataGridView)Get(c,"pressurePreviewGrid");
                Check(Steps(c)==0&&grid.Rows.Count==0&&!grid.Visible,"free value has no phantom step or visible table");
                Check(!((Control)Get(c,"pressureEditButton")).Visible&&!((Control)Get(c,"pressureSaveAsButton")).Visible,"irrelevant step edit/save actions hidden");
                Check(((Control)Get(c,"pressureLoadButton")).Visible,"loading a different plan remains possible");
                foreach(int lang in new[]{0,1,2}){Set(c,"LanguageIndex",lang);Application.DoEvents();Check(Steps(c)==0&&!grid.Visible,"language switch never restores arbitrary rows");}
                Set(c,"LanguageIndex",1);Check(((Label)Get(c,"pressurePlanSummary")).Text.Contains("Free value:"),"free-value hint is translated");Set(c,"LanguageIndex",0);
                Set(c,"PressureMode","旧电缆");Check(Steps(c)==9&&grid.Visible&&((Control)Get(c,"pressureEditButton")).Visible,"switch back restores preset UI");
                Call(c,"ClearProject");Check((string)Get(c,"PressureMode")=="新电缆","unsaved mode choice is not the last used project");
                Valid(c);Set(c,"PressureMode","任意值");Check(((string[])Call(c,"ValidateProject")).Length==0,"no plan is valid for free-value project");
                freeProject=Save(c,Path.Combine(qa,"projects-free"));
                string item=File.ReadAllText(Path.Combine(freeProject,"ITEM.in"),Encoding.GetEncoding(936));
                Check(item.Contains("任意值")&&!item.Contains("加压倍数： = \"1")&&!item.Contains("加压次数： = \"1"),"saved free-value project has no one-step preset");
                Set(c,"PressureMode","旧电缆");Call(c,"ClearProject");Check((string)Get(c,"PressureMode")=="任意值"&&Steps(c)==0,"clear inherits last successful project mode");
                Valid(c);Set(c,"PressureMode","旧电缆");oldProject=Save(c,Path.Combine(qa,"projects-old"));
                Set(c,"PressureMode","新电缆");Set(c,"StartName","");Check(!(bool)Call(c,"SaveProjectToRoot",Path.Combine(qa,"must-not-save")),"failed project save rejected");
                Call(c,"ClearProject");Check((string)Get(c,"PressureMode")=="旧电缆","failed save cannot overwrite last used mode");
                using(var reopened=Editor(settings)){Check((string)Get(reopened,"PressureMode")=="旧电缆"&&Steps(reopened)==9,"recreated page restores persisted preference");}
                Check((bool)Call(c,"LoadProject",freeProject)&&Steps(c)==0&&(string)Get(c,"PressureMode")=="任意值","existing project overrides other remembered mode");
                Set(c,"PressureMode","自定义");int custom=Steps(c);Call(c,"ReloadSettings");Check((string)Get(c,"PressureMode")=="自定义"&&Steps(c)==custom,"settings reload does not replace edited draft");
                Check((bool)Call(c,"LoadProject",oldProject)&&(string)Get(c,"PressureMode")=="旧电缆"&&Steps(c)==9,"existing planned project still loads its exact preset");
                Call(c,"ClearProject");Valid(c);Set(c,"PressureMode","自定义");
                var savedCustom=((IList)Get(c,"currentPressureSteps"))[1];savedCustom.GetType().GetField("Multiplier",All).SetValue(savedCustom,0.75d);
                string customProject=Save(c,Path.Combine(qa,"projects-custom"));
                Check((bool)Call(c,"LoadProject",customProject)&&Math.Abs((double)Get(((IList)Get(c,"currentPressureSteps"))[1],"Multiplier")-0.75)<1e-12,"custom project rows remain unchanged when reopened");
                Call(c,"ClearProject");Check((string)Get(c,"PressureMode")=="自定义","custom mode is remembered without copying project results");
                Set(c,"PressureMode","任意值");Application.DoEvents();using(var bmp=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bmp,c.ClientRectangle);bmp.Save(Path.Combine(qa,"free-value.png"));}
            }
            // Legacy Free value files carried a dummy row. Ignore it without changing disk bytes.
            string legacy=Path.Combine(qa,"legacy");Directory.CreateDirectory(legacy);
            File.Copy(Path.Combine(freeProject,"CABLE.in"),Path.Combine(legacy,"CABLE.in"));
            string original=File.ReadAllText(Path.Combine(freeProject,"ITEM.in"),Encoding.GetEncoding(936));
            string legacyText=original.Replace("加压倍数： = \"\"","加压倍数： = \"1\"").Replace("加压次数： = \"\"","加压次数： = \"1\"");
            // Use the format actually written by the project writer, not assumed spacing.
            legacyText=string.Join("\r\n",legacyText.Replace("\r\n","\n").Split('\n').Select(line=>line.StartsWith("加压倍数")?"加压倍数： = \"1\"":line.StartsWith("加压次数")?"加压次数： = \"1\"":line));
            File.WriteAllText(Path.Combine(legacy,"ITEM.in"),legacyText,Encoding.GetEncoding(936));byte[] legacyBytes=File.ReadAllBytes(Path.Combine(legacy,"ITEM.in"));
            using(var c=Editor(Path.Combine(qa,"legacy-settings.ini"))){Check((bool)Call(c,"LoadProject",legacy)&&Steps(c)==0,"legacy dummy step ignored by editor");Check(legacyBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(legacy,"ITEM.in"))),"opening legacy project does not rewrite its ITEM");}
            using(var p=(Control)Activator.CreateInstance(pressureType)) {
                var handle=p.Handle;
                Check((bool)Call(p,"LoadProjectPlan",oldProject)&&((double[])Call(p,"GetPressurePlanMultipliers")).Length==9,"real planned project loads into pressure UI");
                long requests=(long)Get(p,"PressureRequestSequence");
                Check((bool)Call(p,"LoadProjectPlan",freeProject),"free-value project accepted by pressure UI without plan error: "+Get(p,"LastError"));
                Check(((double[])Call(p,"GetPressurePlanMultipliers")).Length==0&&(string)Get(p,"PressureProgramModeCode")=="CUSTOM","old plan cleared and custom voltage mode selected");
                Check((double)Get(p,"BaseU0VoltageKv")==6d&&string.IsNullOrEmpty((string)Get(p,"LastError")),"free value preserves valid U0 and has no failure warning");
                Check((bool)Call(p,"LoadProjectPlan",legacy)&&((double[])Call(p,"GetPressurePlanMultipliers")).Length==0,"pressure reader ignores legacy arbitrary dummy rows");
                Check((long)Get(p,"PressureRequestSequence")==requests,"loading preferences or plans never initiates output");
                string bad=Path.Combine(qa,"invalid-item.in");File.WriteAllText(bad,"加压模式： = \"任意值\"\r\n相电压： = \"0kV\"",Encoding.GetEncoding(936));
                Check(!(bool)Call(p,"LoadProjectPlan",bad),"free mode does not bypass invalid phase-voltage validation");
                File.WriteAllText(bad,"加压模式： = \"任意值\"\r\n加压模式： = \"新电缆\"\r\n相电压： = \"6kV\"",Encoding.GetEncoding(936));
                Check(!(bool)Call(p,"LoadProjectPlan",bad),"conflicting declarations cannot activate a plan");
            }
            using(var c=Editor(Path.Combine(qa,"legacy-settings.ini"))){Check((string)Get(c,"PressureMode")=="任意值"&&Steps(c)==0,"loaded project mode persists across page restart");}
            Console.WriteLine("CREATION_FREE_VALUE_MODE_PASS="+checks+"; QA="+qa+"; HardwareAccessed=False");return 0;
        }catch(Exception e){Console.Error.WriteLine("FAIL "+checks+": "+e);return 1;}
    }
}
