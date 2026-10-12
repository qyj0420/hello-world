using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;
using Xiongxin.OWTS.Localization;

internal static class InternationalLanguageSmoke
{
    const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    sealed class TestHost : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    static void Check(bool valid, string message) { checks++; if (!valid) throw new Exception(message); }
    static object Call(Type t, string name, params object[] args)
    { return t.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args.Select(a => a.GetType()))).Invoke(null,args); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Check(UiLanguage.Codes.Length == 40 && UiLanguage.Names.Length == 40 && UiLanguage.CultureNames.Length == 40, "Catalog dimensions");
            Check(UiLanguage.Codes[18] == "zh-TW" && UiLanguage.Codes[11] == "ru", "Existing persisted IDs retained");
            foreach(string pair in new[]{"en-GB|en-GB","de-CH|de-CH","fr-CH|fr-CH","it-CH|it-CH","es-AR|es-419","es-ES|es","pt_PT|pt-PT","ja-JP|ja","uz-Latn-UZ|uz","az-Latn-AZ|az","tg-Cyrl-TJ|tg","zh-HK|zh-TW","invalid|en"})
            { string[] p = pair.Split('|'); Check(UiLanguage.ResolveCode(p[0]) == p[1], "Locale resolution " + pair); }
            Check(UiLanguage.FindCode("not-a-language") == -1,"Unsupported setters reject unknown languages");
            Check(AppLanguages.Text("PressureTest","en") == "Voltage test", "Electrical voltage, not mechanical pressure");
            Check(UiLanguage.BuiltIn(UiLanguage.Index("ja"),"关闭")=="閉じる","Chinese Close is not translated as an Off state");
            string[] modules = {"ProjectCreation|Xiongxin.OWTS.ProjectCreation|HVX.Controls.ProjectCreationControl|HVX.Controls.ProjectCreationLanguageContract", "ProjectManagement|Xiongxin.OWTS.ProjectManagement|HVX.Controls.ProjectManagementControl|HVX.Controls.ProjectManagementLanguages", "DataBrowser|Xiongxin.OWTS.DataBrowser|HVX.Controls.ProjectDataBrowserControl|HVX.Controls.BrowserLanguages", "DataAnalysis|HVX.PdReview.V1.0|HVX.PdReview.PdReviewControl|HVX.PdReview.ReviewLanguages", "ReportGenerator|Xiongxin.OWTS.ReportGenerator|HVX.ReportGenerator.ReportGeneratorControl|HVX.ReportGenerator.ReportLanguages", "PressureTest|Xiongxin.OWTS.PressureTest|HVX.Controls.PressureTestControl|", "CalibrationTest|Xiongxin.OWTS.CalibrationTest|HVX.Controls.CalibrationProgramControl|"};
            for(int i=0;i<40;i++)
            {
                string code=UiLanguage.Codes[i];
                Check(UiLanguage.Index(code)==i, "Roundtrip " + code);
                CultureInfo.GetCultureInfo(UiLanguage.CultureNames[i]);
                var settings=new AppSettings{LanguageCode=code}; string file=Path.Combine(output,code+".xml"); settings.Save(file);
                Check(AppSettings.Load(file).LanguageCode==code,"Persistent locale " + code);
                string original="客户_Projects/PDIV_{0}_007.31 / voltage 1,234.56";
                Check(UiLanguage.Translate(i,original)==original,"Unknown text preserved " + code);
                Check(UiLanguage.Translate(i,"{0:N2} kV / {1} pC")=="{0:N2} kV / {1} pC","Measurement formats preserved " + code);
                Check(UiLanguage.Translate(i,"PRPD")=="PRPD","International abbreviations preserved " + code);
                foreach(string symbol in new[]{"RMS","DAC","VLF","HFCT","PDIV","PDEV","pC","nC","kV","MΩ","tan δ"})
                    Check(UiLanguage.Translate(i,symbol)==symbol,"Technical code remains invariant: "+code+" "+symbol);
                if(i>=19) Check(!String.IsNullOrWhiteSpace(AppLanguages.Text("Home",code)),"Translated home " + code);
                using(var form=new MainForm(settings,new ApplicationState(),true))
                {
                    form.CreateControl(); form.PerformLayout();
                    foreach(string name in new[]{"settingsButton","helpButton","openProjectButton","refreshDeviceButton","backButton"})
                    {
                        var button=(Button)typeof(MainForm).GetField(name,All).GetValue(form);
                        int needed=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width;
                        Check(button.Width>=needed+button.Padding.Horizontal,"Top button text fits "+code+" "+name);
                    }
                    if(new[]{"ja","pt-BR","de-CH","hy","ka","kk"}.Contains(code))
                    {
                        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
                        using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,form.ClientRectangle);bitmap.Save(Path.Combine(output,"home-"+code+".png"));}
                        form.Hide();
                    }
                }
                using(var form=new SettingsForm(settings))
                {
                    var selector=(ComboBox)typeof(SettingsForm).GetField("language",All).GetValue(form);
                    Check(((LanguageItem)selector.SelectedItem).Code==code,"Settings selection " + code);
                }
            }
            foreach(string module in modules)
            {
                string[] p=module.Split('|'); Console.WriteLine("MODULE="+p[0]);
                var assembly=Assembly.LoadFrom(Path.Combine(root,"modules",p[0],p[1]+".dll"));
                // Verify the source-shared contract embedded in each actual DLL.
                Type local=assembly.GetType("Xiongxin.OWTS.Localization.UiLanguage",true);
                for(int i=0;i<40;i++) Check((int)Call(local,"Index",UiLanguage.Codes[i])==i,p[0]+" ID "+i);
                Type type=assembly.GetType(p[2],false);
                if(type==null) type=assembly.GetExportedTypes().First(t=>typeof(Control).IsAssignableFrom(t)&&t.GetProperty("LanguageIndex")!=null&&!t.IsAbstract);
                using(var control=(Control)Activator.CreateInstance(type))
                {
                    using(var host=new TestHost{ClientSize=new Size(1280,800),StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ShowInTaskbar=false})
                    {
                    host.Controls.Add(control);control.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                    PropertyInfo language=type.GetProperty("LanguageIndex");
                    foreach(int i in Enumerable.Range(0,40).Concat(new[]{0,18,1}))
                    {
                        language.SetValue(control,i,null); control.PerformLayout();
                        Check((int)language.GetValue(control,null)==i,p[0]+" language switch "+i);
                        using(var bitmap=new Bitmap(1280,800)) { control.DrawToBitmap(bitmap,new Rectangle(0,0,1280,800)); if(i==20||i==33) bitmap.Save(Path.Combine(output,p[0]+"-"+UiLanguage.Codes[i]+".png")); }
                    }
                    host.Controls.Remove(control);host.Hide();
                    }
                }
                if(p[0]=="ProjectCreation")
                {
                    var contract=assembly.GetType(p[3]);
                    for(int i=0;i<40;i++) Check((string)Call(contract,"Canonical",i,"客户自定义绝缘材料","客户自定义绝缘材料")=="客户自定义绝缘材料","User materials preserved "+i);
                }
                if(p[0]=="DataBrowser")
                {
                    var item=assembly.GetType("LabVIEWItemInformation.UiText");
                    for(int i=0;i<40;i++) Check(!String.IsNullOrEmpty((string)Call(item,"Get","Title",i)),"Item caption "+i);
                }
            }
            File.WriteAllText(Path.Combine(output,"result.txt"),"PASS\r\nAssertions="+checks+"\r\nLocales=40\r\nModules=7\r\nHardwareConnected=false\r\nFullTranslationCertified=false\r\n");
            Console.WriteLine("INTERNATIONAL_LANGUAGE_PASS="+checks); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
