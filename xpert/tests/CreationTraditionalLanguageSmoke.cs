using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class CreationTraditionalLanguageSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static object Get(object value,string name) {return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void Set(object value,string name,object data) {value.GetType().GetProperty(name,All).SetValue(value,data,null);}
    static object Field(object value,string name) {return value.GetType().GetField(name,All).GetValue(value);}
    static object Call(object value,string name,params object[] args)
    {Type type=value as Type??value.GetType();return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value is Type?null:value,args);}
    static void Check(bool condition,string text) {checks++;if(!condition)throw new InvalidOperationException(text);}
    sealed class QaForm:Form
    {protected override bool ShowWithoutActivation {get{return true;}}protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000;return p;}}}
    [STAThread] static int Main(string[] args)
    {
        CultureInfo original=Thread.CurrentThread.CurrentUICulture;
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass release root and QA output directory.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Assembly assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            Type contract=assembly.GetType("HVX.Controls.ProjectCreationLanguageContract",true);
            Type localization=assembly.GetType("HVX.Controls.ProjectCreationLocalization",true);
            Application.EnableVisualStyles();
            using(Control module=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl",true)))
            using(var form=new QaForm {AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1256,620),ShowInTaskbar=false})
            {
                Set(module,"ShowDialogMessages",false);Set(module,"OnlineWeatherEnabled",false);Set(module,"AutoRefreshWeatherOnHardwareSample",false);
                Set(module,"EmbeddedMode",true);Set(module,"SettingsPath",Path.Combine(output,"qa.ini"));
                string[] codes=(string[])Call(module,"GetLanguageCodes"),names=(string[])Call(module,"GetLanguageNames");
                string[] expected="zh-CN|en|bg|hr|cs|fr|de|hu|it|pl|ro|ru|sr|sk|sl|es|tr|vi".Split('|');
                Check(codes.Length==40&&names.Length==40,"40 locales with stable legacy IDs");
                for(int i=0;i<expected.Length;i++)Check(codes[i]==expected[i],"Legacy language index remains stable: "+i);
                Check(codes[18]=="zh-TW"&&names[18]=="繁體中文","Traditional language appended");
                foreach(string code in new[]{"zh-TW","zh-HK","zh-MO","zh-Hant","zh-Hant-HK","zh_TW"})Check((int)Call(contract,"FindCode",code)==18,"Traditional culture recognized: "+code);
                Check((int)Call(contract,"FindCode","zh-SG")==0,"Simplified Chinese culture retained");
                foreach(string culture in new[]{"zh-TW","en-GB","de-DE","ja-JP","zh-CN"})
                {
                    Thread.CurrentThread.CurrentUICulture=CultureInfo.GetCultureInfo(culture);Set(module,"LanguageIndex",-1);
                    int effective=culture=="zh-TW"?18:culture=="de-DE"?6:culture=="zh-CN"?0:culture=="ja-JP"?20:culture=="en-GB"?24:1;
                    Check((int)Get(module,"LanguageIndex")==-1&&(string)Get(module,"LanguageCode")=="system","Follow-system preference retained");
                    Check((int)Get(module,"EffectiveLanguageIndex")==effective,"System culture resolved: "+culture);
                    ComboBox choices=(ComboBox)Field(module,"languageCombo");
                    Check(choices.Items.Count==41&&choices.SelectedIndex==0,"Follow system is selector's first item");
                }
                Check(!(bool)Call(module,"SetLanguageCode","not-a-language"),"Unknown language rejected without fallback mutation");
                string user="客户电缆 后台记录 / 用戶原文";
                Set(module,"StartName",user);Set(module,"EndName",user);Set(module,"Notes",user);
                module.Dock=DockStyle.Fill;form.Controls.Add(module);form.Show();Application.DoEvents();
                foreach(int language in new[]{0,18,1,18,0})
                {
                    Set(module,"LanguageIndex",language);Application.DoEvents();
                    Check((string)Get(module,"StartName")==user&&(string)Get(module,"EndName")==user&&(string)Get(module,"Notes")==user,"Language never rewrites project/customer input");
                    string expectedCaption=language==0?"终端名称":language==18?"終端名稱":"End name";
                    Check(((Label)Field(module,"endNameCaptionLabel")).Text==expectedCaption,"Canonical label restores after switching: "+language);
                    Check((string)Call(localization,"TranslateCanonical",language,user)==user,"Custom catalog entry remains untouched");
                }
                Set(module,"LanguageIndex",18);
                Check((string)Call(localization,"TranslateCanonical",18,"塑料绝缘")=="塑料絕緣","Built-in catalog choice translated");
                Check((string)Call(localization,"TranslateCanonical",18,"A相")=="A相","Traditional Chinese retains Chinese phase notation");
                string path=@"D:\客户电缆\后台记录";
                Check((string)Call(module,"TranslateDynamic","已加载 · "+path)=="已加載 · "+path,"Dynamic status leaves file path exactly unchanged");
                Check((string)Call(localization,"Weather",18,2)=="局部多雲","Weather built-in text is Traditional");
                Call(module,"SetOptionalSectionExpanded","InsulationAndNotes",true);Application.DoEvents();
                Check(((Label)Field(module,"insulationReferenceCaption")).Text=="參考標準","Reference panel switches to Traditional");
                Check(((Button)Field(module,"insulationDetailsButton")).Text=="參考說明","Reference action switches to Traditional");
                Check(((Label)Field(module,"insulationGuidanceStatus")).Text.Contains("測量"),"Reference state not English fallback");
                using(var bitmap=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(bitmap,module.ClientRectangle);bitmap.Save(Path.Combine(output,"creation-traditional-144dpi.png"));}
                using(Form weather=(Form)Activator.CreateInstance(assembly.GetType("HVX.Controls.WeatherLocationDialog",true),All,null,new object[]{18,false,39.9,116.4},CultureInfo.InvariantCulture))
                {
                    Check(weather.Text=="天氣位置","Weather position dialog title translated");
                    Check(((TextBox)Field(weather,"latitude")).Text=="39.900","Weather numeric entry unchanged");
                    Check(((CheckBox)Field(weather,"agreement")).Text.Contains("聯網天氣"),"Weather first-use disclosure translated");
                }
                form.Close();
            }
            Console.WriteLine("CREATION_TRADITIONAL_LANGUAGE_PASS="+checks+"; HARDWARE_COMMANDS=0; NETWORK_REQUESTS=0");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Thread.CurrentThread.CurrentUICulture=original;}
    }
}
