using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class SettingsUiLanguageTreeSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(Control d in All(child))yield return d;}
    static bool Chinese(string text){return (text??"").Any(c=>c>='\u4e00'&&c<='\u9fff');}
    static void Audit(Control page,int index,Func<string,string> traditional,List<string> errors)
    {
        foreach(Control c in All(page)){
            if(c is Label||c is ButtonBase||c is TabPage||c is GroupBox||c is Form){
                string s=c.Text??"";if(index==1?Chinese(s):s!=traditional(s))errors.Add(c.Name+" / "+s);
            }
            foreach(var f in c.GetType().GetFields(B))if(f.FieldType==typeof(ToolTip)){
                var tip=(ToolTip)f.GetValue(c);if(tip!=null)foreach(Control target in All(c)){
                    string s=tip.GetToolTip(target);if(index==1?Chinese(s):s!=traditional(s))errors.Add("tip "+target.Name+" / "+s);
                }
            }
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);if(Directory.Exists(qa))throw new Exception("Fresh fixture required.");Directory.CreateDirectory(qa);
            string hostFile=Path.Combine(root,"CurrentUiHost.dll");if(!File.Exists(hostFile))hostFile=Path.Combine(root,"Xiongxin.OWTS.Desktop.exe");
            var asm=Assembly.LoadFrom(hostFile);var settings=Activator.CreateInstance(asm.GetType("Xiongxin.OWTS.Desktop.AppSettings",true));
            asm.GetType("Xiongxin.OWTS.Desktop.AppSettings",true).GetProperty("DataRoot",B).SetValue(settings,qa,null);
            var type=asm.GetType("Xiongxin.OWTS.Desktop.SettingsForm",true);var section=asm.GetType("Xiongxin.OWTS.Desktop.SettingsSection",true);
            var trad=asm.GetType("Xiongxin.OWTS.Localization.UiLanguage",true).GetMethod("ToTraditional",B);
            using(var form=(Form)Activator.CreateInstance(type,new object[]{settings})){
                // Synthetic grants belong only to this detached, hidden test
                // window. This fixture audits captions, not credentials; it
                // never invokes a verifier, reads a secret, saves or connects.
                // Guarded release pages must actually be mounted as well.
                object grants=type.GetField("maintenanceGrants",B).GetValue(form);
                foreach(string name in Enum.GetNames(section)){
                    object value=Enum.Parse(section,name);
                    grants.GetType().GetMethod("Add").Invoke(grants,new[]{value});
                    type.GetMethod("OpenSection",B).Invoke(form,new[]{value});
                }
                var language=(ComboBox)Get(form,"language");var values=new Dictionary<NumericUpDown,decimal>();var selections=new Dictionary<ComboBox,int>();
                // Every lazy page is mounted or retained in its own field. Do not
                // invoke Save, device-connect, browse, installation or OEM writes.
                var pages=new List<Control>{form};foreach(string n in new[]{"deviceTab","expertTab","oemTab","systemAdminTab","plusDataTab"})pages.Add((Control)Get(form,n));
                foreach(var c in pages.SelectMany(All).Distinct()){
                    var number=c as NumericUpDown;if(number!=null){Check(c.GetType().Name=="SettingsNumber","All settings numeric editors use wheel-safe constructor: "+c.Name);values[number]=number.Value;}
                    var box=c as ComboBox;if(box!=null){Check(c.GetType().Name=="SettingsComboBox","All settings enums use wheel-safe constructor: "+c.Name);selections[box]=box.SelectedIndex;}
                }
                Check(values.Count>20&&selections.Count>=7,"General and all lazy settings pages are covered");
                foreach(var entry in values){entry.Key.GetType().GetMethod("OnMouseWheel",B).Invoke(entry.Key,new object[]{new HandledMouseEventArgs(MouseButtons.None,0,0,0,-120)});Check(entry.Key.Value==entry.Value,"Numeric wheel leaves saved precision: "+entry.Key.Name);}
                foreach(var entry in selections){entry.Key.GetType().GetMethod("OnMouseWheel",B).Invoke(entry.Key,new object[]{new HandledMouseEventArgs(MouseButtons.None,0,0,0,-120)});Check(entry.Key.SelectedIndex==entry.Value,"Enum wheel leaves selection: "+entry.Key.Name);}
                Check(language.Items.Count==40,"All existing locales remain selectable; caption completeness is audited separately");
                foreach(var locale in new[]{new{Code="zh-CN",Index=0},new{Code="en",Index=1}}){
                    int selected=-1;for(int i=0;i<language.Items.Count;i++)if((string)Get(language.Items[i],"Code")==locale.Code){selected=i;break;}
                    Check(selected>=0,"Language selector has "+locale.Code);language.SelectedIndex=selected;
                    var errors=new List<string>();
                    if(locale.Index==1)foreach(Control page in pages)Audit(page,locale.Index,s=>(string)trad.Invoke(null,new object[]{s}),errors);
                    else Check(pages.SelectMany(All).Any(c=>Chinese(c.Text)),"Chinese settings captions remain available");
                    Check(errors.Count==0,locale.Code+" complete captions/tooltips: "+String.Join(" | ",errors));
                    var captionMap=(System.Collections.IDictionary)Get(form,"settingsCaptions");
                    if(locale.Index==0)foreach(System.Collections.DictionaryEntry entry in captionMap){var c=(Control)entry.Key;Check(c.Text==(string)entry.Value,"Chinese caption exactly follows application-owned source: "+c.Name);}
                    foreach(string n in new[]{"reportFormat","endpointFirstOperator","endpointSecondOperator","defectModelKind"}){
                        var box=(ComboBox)Get(form,n);foreach(object item in box.Items){string text=item.ToString();Check(locale.Index==1?!Chinese(text):!String.IsNullOrWhiteSpace(text),locale.Code+" enum captions: "+n+" / "+text);}
                    }
                }
            }
            Console.WriteLine("SETTINGS_UI_LANGUAGE_TREE_PASS="+checks+";HARDWARE_ACCESSED=False;SDK_CALLED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
