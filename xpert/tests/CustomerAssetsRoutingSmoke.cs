using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class CustomerAssetsRoutingSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    [STAThread] static int Main(string[] args)
    {
        try {
            string root=AppDomain.CurrentDomain.BaseDirectory,fixture=Path.GetFullPath(args[0]);Directory.CreateDirectory(fixture);
            Check(OemBranding.AssetDirectory==Path.Combine(root,"OEMpic"),"OEM path is installation-local");
            string logo=Path.Combine(fixture,"customer-logo.png");
            using(var bitmap=new Bitmap(48,24)){using(var g=Graphics.FromImage(bitmap))g.Clear(Color.MediumPurple);bitmap.Save(logo,ImageFormat.Png);}
            byte[] bytes=File.ReadAllBytes(logo);
            string copy=OemBranding.ImportAssetAsync(logo,false).GetAwaiter().GetResult();
            Check(Path.GetDirectoryName(copy)==OemBranding.AssetDirectory,"individual load copies to OEMpic");
            Check(bytes.SequenceEqual(File.ReadAllBytes(copy))&&bytes.SequenceEqual(File.ReadAllBytes(logo)),"copy preserves exact source bytes");
            Check(OemBranding.ImportAssetAsync(copy,false).GetAwaiter().GetResult()==copy,"reloading stored copy is idempotent");
            string invalid=Path.Combine(fixture,"invalid.png");File.WriteAllText(invalid,"not an image");
            bool rejected=false;try{OemBranding.ImportAssetAsync(invalid,false).GetAwaiter().GetResult();}catch{rejected=true;}
            Check(rejected&&bytes.SequenceEqual(File.ReadAllBytes(copy)),"invalid input does not replace stored asset");
            string manual=Path.Combine(root,"说明书"),terms=Path.Combine(root,"专业术语");Directory.CreateDirectory(manual);Directory.CreateDirectory(terms);
            string zh=Path.Combine(manual,"HV-DAC_Xpert_Operation_Manual_ZH.pdf"),en=Path.Combine(manual,"HV-DAC_Xpert_Operation_Manual_EN.pdf"),glossary=Path.Combine(terms,"Electrical_Testing_Terminology_EN-ZH.pdf");
            // Routing fixtures only: no PDF renderer/viewer or product is started.
            File.WriteAllText(zh,"routing-zh");File.WriteAllText(en,"routing-en");File.WriteAllText(glossary,"routing-terms");
            foreach(string language in new[]{"zh-CN","zh-TW","en-US","de-DE","fr-FR","ja-JP","es-ES"}) {
                var settings=new AppSettings{LanguageCode=language,ManualPath=Path.Combine(fixture,"stale-custom-manual.html")};
                using(var help=new HelpForm(settings)) {
                    string selected=(string)typeof(HelpForm).GetMethod("ResolveManualPath",All).Invoke(help,null);
                    Check(selected==(AppLanguages.IsChinese(language)?zh:en),"manual follows active language: "+language);
                    Check((string)typeof(HelpForm).GetMethod("ResolveGlossaryPath",All).Invoke(help,null)==glossary,"packaged terminology preferred: "+language);
                }
            }
            Console.WriteLine("CUSTOMER_ASSETS_ROUTING_PASS="+checks+"; HARDWARE_ACCESSED=False; PRODUCT_STARTED=False; USER_SETTINGS_WRITTEN=False");return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
