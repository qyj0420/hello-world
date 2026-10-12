using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class BrowserLayoutReuseSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,F);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,F).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
    static string Fixture(string qa,string name)
    {
        string path=Path.Combine(qa,name);Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"ITEM.in"),"[ITEM]\r\n始端地点 = \""+name+"\"\r\n终端地点 = \"End\"\r\n电缆长度 = \"176.8\"\r\n电缆芯数 = \"三相单芯\"\r\n相电压 = \"8.7\"\r\n备注 = \"Synthetic metadata\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0m\r\n1\t终端\t冷缩\t176.8m\r\n",new UTF8Encoding(false));return path;
    }
    static void Arrange(Control browser){Call(browser,"ArrangeProjectInformation");Application.DoEvents();Call(browser,"ArrangeProjectInformation");}
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Control.CheckForIllegalCrossThreadCalls=true;
            string qa=Path.GetFullPath(args[1]),build=Path.GetFullPath(args[0]);
            Check(qa.StartsWith(build+Path.DirectorySeparatorChar+"qa"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"fixtures remain in candidate QA directory");
            string a=Fixture(qa,"Project-A"),b=Fixture(qa,"Project-B");byte[] original=File.ReadAllBytes(Path.Combine(a,"ITEM.in"));
            Assembly assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var browser=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2400,-1800),ClientSize=new Size(1420,850)}){
                browser.Dock=DockStyle.Fill;host.Controls.Add(browser);host.Show();Application.DoEvents();
                Check((bool)Call(browser,"LoadProject",a),"synthetic project loads");Arrange(browser);
                var item=(Control)Get(browser,"item");var shell=(Control)Get(browser,"shellLayout");var nav=(Label)Get(browser,"navigationSelection");var split=(SplitContainer)Get(browser,"navigationSplit");var viewport=(Panel)Get(browser,"itemViewport");
                Call(browser,"UpdateNavigationSelection");object navSnapshot=Get(browser,"navigationMeasure");
                for(int i=0;i<250;i++)Call(browser,"UpdateNavigationSelection");
                Check(ReferenceEquals(navSnapshot,Get(browser,"navigationMeasure")),"unchanged navigation reuses one text measurement");
                int width=Math.Max(80,split.Panel1.ClientSize.Width-split.Panel1.Padding.Horizontal-nav.Padding.Horizontal);
                int measured=TextRenderer.MeasureText(nav.Text,nav.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;
                Check(nav.Height==Math.Max(76,Math.Min(Math.Max(100,split.Panel1.Height/3),measured+nav.Padding.Vertical+8)),"cached navigation height equals uncached reference");
                Check(!(bool)Call(navSnapshot,"Matches",nav.Text,width,nav.Font,nav.DeviceDpi+48,0),"DPI mismatch invalidates measurement (synthetic key check)");
                split.SplitterDistance+=36;Call(browser,"UpdateNavigationSelection");
                Check(!ReferenceEquals(navSnapshot,Get(browser,"navigationMeasure")),"real split resize invalidates navigation width");
                navSnapshot=Get(browser,"navigationMeasure");using(var font=new Font(nav.Font.FontFamily,nav.Font.Size+2,nav.Font.Style)){
                    nav.Font=font;Call(browser,"UpdateNavigationSelection");Check(!ReferenceEquals(navSnapshot,Get(browser,"navigationMeasure")),"font change invalidates text measurement");nav.Font=browser.Font;
                }
                navSnapshot=Get(browser,"navigationMeasure");var tree=(TreeView)Get(browser,"tree");tree.SelectedNode.Text="Changed navigation heading";Call(browser,"UpdateNavigationSelection");
                Check(!ReferenceEquals(navSnapshot,Get(browser,"navigationMeasure"))&&nav.Text.Contains("Changed navigation heading"),"changed node text appears without stale cache");
                Arrange(browser);object layout=Get(browser,"projectInformationLayout");int itemLayouts=0,shellLayouts=0;
                item.Layout+=(s,e)=>itemLayouts++;shell.Layout+=(s,e)=>shellLayouts++;
                for(int i=0;i<100;i++){Call(browser,"ArrangeProjectInformation");Call(browser,"RevealProjectContent");}
                Check(ReferenceEquals(layout,Get(browser,"projectInformationLayout")),"unchanged project geometry reuses layout snapshot");
                Check(itemLayouts==0&&shellLayouts==0,"unchanged reveal/arrange adds no item or shell layouts");
                Call(item,"SetFieldValue","Remarks","Updated synthetic metadata");Arrange(browser);
                Check(!ReferenceEquals(layout,Get(browser,"projectInformationLayout"))&&(string)Call(item,"GetFieldValue","Remarks")=="Updated synthetic metadata","content sequence invalidates without losing new fields");
                layout=Get(browser,"projectInformationLayout");Call(item,"SetLanguage",1);Arrange(browser);
                Check(!ReferenceEquals(layout,Get(browser,"projectInformationLayout")),"item language changes invalidate geometry");
                layout=Get(browser,"projectInformationLayout");browser.GetType().GetProperty("LanguageIndex").SetValue(browser,1,null);Arrange(browser);
                Check(!ReferenceEquals(layout,Get(browser,"projectInformationLayout")),"browser language changes invalidate geometry");
                Check(nav.Text.StartsWith("Selected",StringComparison.Ordinal),"language change updates navigation text");
                foreach(var size in new[]{new Size(1000,650),new Size(1480,900)}){
                    layout=Get(browser,"projectInformationLayout");host.ClientSize=size;Arrange(browser);
                    Check(!ReferenceEquals(layout,Get(browser,"projectInformationLayout")),"real window resize reflows metadata");
                    Check(viewport.AutoScrollMinSize.Height>=item.Height&&item.Width>=viewport.Width-SystemInformation.VerticalScrollBarWidth-4,"resized metadata remains reachable");
                }
                host.ClientSize=new Size(960,570);Arrange(browser);viewport.AutoScrollPosition=new Point(0,100);Application.DoEvents();
                layout=Get(browser,"projectInformationLayout");Point scroll=viewport.AutoScrollPosition;
                for(int i=0;i<30;i++)Call(browser,"ArrangeProjectInformation");
                Check(ReferenceEquals(layout,Get(browser,"projectInformationLayout")),"narrow viewport and scrolling reuse geometry");
                Check(viewport.AutoScrollPosition==scroll,"layout reuse preserves user's scroll position");
                layout=Get(browser,"projectInformationLayout");Check((bool)Call(browser,"LoadProject",b),"second synthetic project loads");Arrange(browser);
                Check(!ReferenceEquals(layout,Get(browser,"projectInformationLayout"))&&(string)Call(item,"GetFieldValue","StartName")=="Project-B","same-size new project does not reuse stale content");
                Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(a,"ITEM.in"))),"display edits do not write original fixture");
                Call(browser,"ShowProjectLoadingView","Synthetic loading");Check(!shell.Visible,"loading cover hides previous content");Call(browser,"RevealProjectContent");
                Check(shell.Visible&&!((Control)Get(browser,"projectLoadingView")).Visible,"covered content reveal remains functional");
            }
            Console.WriteLine("BrowserLayoutReuseSmoke PASS: "+checks+"; physical DPI and user dataset responsiveness not tested");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
