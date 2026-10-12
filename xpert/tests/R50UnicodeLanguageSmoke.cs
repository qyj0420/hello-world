using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class R50UnicodeLanguageSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Call(object o,string n,params object[] a){var t=o as Type??o.GetType();return t.GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o is Type?null:o,a);}
    static object Field(object o,string n){return o.GetType().GetField(n,Flags).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,Flags).SetValue(o,v);}
    [STAThread] static int Main(string[] args)
    {
        try {
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var host=Assembly.LoadFrom(Path.Combine(build,"Xiongxin.OWTS.Desktop.exe"));
            var languages=host.GetType("Xiongxin.OWTS.Desktop.AppLanguages",true);
            var choices=(Array)languages.GetProperty("SelectionItems",Flags).GetValue(null,null);
            Check(choices.Length==40,"all existing languages remain selectable");
            Check((string)Call(languages,"ResolveCode","pl")=="pl","Polish locale is not silently replaced by English");
            foreach(var choice in choices){
                string code=(string)choice.GetType().GetProperty("Code",Flags).GetValue(choice,null);
                Check((string)Call(languages,"ResolveCode",code)==code,"existing language resolves without removal: "+code);
                Check((int)Call(languages,"SelectionIndex",code)==(int)Call(languages,"IndexOf",code),"selector preserves historical index: "+code);
            }
            Check((string)Call(languages,"ResolveCode","zh-CN")=="zh-CN","Chinese unchanged");
            using(var selector=new ComboBox()){
                foreach(var choice in choices)selector.Items.Add(choice);
                Call(languages,"InitializeSelection",selector,"pl");
                Check(selector.SelectedIndex==9,"legacy Polish preference selects Polish");
                Check((string)Call(languages,"CaptureSelectionCode",selector,"pl")=="pl","unrelated settings save does not erase legacy code");
            }
            var creation=Assembly.LoadFrom(Path.Combine(build,"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            var storage=creation.GetType("HVX.Controls.ProjectCreationStorage",true);
            var pressure=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            var page=pressure.GetType("HVX.Controls.PressureTestControl",true);
            var management=Assembly.LoadFrom(Path.Combine(build,"modules","ProjectManagement","Xiongxin.OWTS.ProjectManagement.dll"));
            var scanner=management.GetType("HVX.Controls.ProjectScanner",true);
            string value="中文项目 – Łódź – Киев – محطة – 😀";
            foreach(string culture in new[]{"en-US","pl-PL","de-DE","zh-CN"}){
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(culture);
                var draft=Activator.CreateInstance(creation.GetType("HVX.Controls.ProjectDraft",true),true);
                Field(draft,"StartName","始端");Field(draft,"EndName","终端");Field(draft,"Organization",value);Field(draft,"Notes",value);
                Field(draft,"LengthA",100d);Field(draft,"LengthB",100d);Field(draft,"LengthC",100d);
                string project=(string)Call(storage,"SaveNew",draft,Path.Combine(qa,culture));
                foreach(string name in new[]{"ITEM.in","CABLE.in"}){
                    string path=Path.Combine(project,name);var bytes=File.ReadAllBytes(path);
                    Check(bytes.Take(3).SequenceEqual(new byte[]{239,187,191}),culture+" explicit UTF8 BOM "+name);
                    var lines=(string[])Call(page,"ReadProjectLines",path);
                    Check(!String.Join("\n",lines).Contains("\uFFFD"),culture+" pressure reader no replacement characters");
                    string catalogue=(string)Call(scanner,"ReadTextSmart",path);
                    Check(catalogue.Length>0&&catalogue[0]!='\uFEFF'&&!catalogue.Contains("\uFFFD"),culture+" project catalogue decodes BOM without leaking it into field names");
                }
                object loaded=Call(storage,"Load",project);
                Check((string)Field(loaded,"Organization")==value&&(string)Field(loaded,"Notes")==value,culture+" multilingual fields lossless");
                string legacy=Path.Combine(project,"legacy-gb936.txt");
                string text="电缆长度: = \"100.0\"\r\n始端地点: = \"设备侧\"\r\n";
                File.WriteAllBytes(legacy,Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetBytes(text));
                Check(String.Join("\n",(string[])Call(page,"ReadProjectLines",legacy)).Contains("设备侧"),culture+" legacy GB936 decoded independently of OS ANSI code page");
                Check(((string)Call(scanner,"ReadTextSmart",legacy)).Contains("设备侧"),culture+" legacy catalogue uses explicit GB936");
                foreach(var encoding in new Encoding[]{new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true)}){
                    string unicode=Path.Combine(project,"legacy-unicode-"+encoding.CodePage+".txt");
                    File.WriteAllBytes(unicode,encoding.GetPreamble().Concat(encoding.GetBytes(text+value)).ToArray());
                    Check(((string)Call(scanner,"ReadTextSmart",unicode)).Contains(value),culture+" catalogue Unicode BOM "+encoding.CodePage);
                    Check(String.Join("\n",(string[])Call(page,"ReadProjectLines",unicode)).Contains(value),culture+" pressure Unicode BOM "+encoding.CodePage);
                }
            }
            Console.WriteLine("R50_UNICODE_LANGUAGE_PASS="+checks+";Hardware=False;UserProjects=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
