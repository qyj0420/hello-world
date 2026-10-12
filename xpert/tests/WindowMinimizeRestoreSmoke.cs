using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class WindowMinimizeRestoreSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static bool baseline;
    static void Check(bool value,string reason){checks++;if(!value){if(baseline)Console.WriteLine("BASELINE_FAILED="+reason);else throw new Exception(reason);}}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static string Geometry(Control c){var sb=new StringBuilder();foreach(Control d in All(c))sb.Append(d.Name).Append(':').Append(d.Bounds).Append(':').Append(d.Visible).Append(';');return sb.ToString();}
    static void SameGeometry(Control shell,string expected,string reason){string actual=Geometry(shell);if(actual!=expected){var before=expected.Split(';');var after=actual.Split(';');for(int i=0;i<Math.Min(before.Length,after.Length);i++)if(before[i]!=after[i])Console.WriteLine("GEOMETRY_DIFFERENCE="+i+";BEFORE="+before[i]+";AFTER="+after[i]+";TYPE="+All(shell).ElementAt(i).GetType().FullName);var table=shell as TableLayoutPanel;if(table!=null)Console.WriteLine("SETTLED_ROWS="+String.Join(",",table.GetRowHeights())+";ROW_STYLE1="+table.RowStyles[1].Height+";MIN="+shell.Controls[1].MinimumSize);}Check(actual==expected,reason);}
    static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(Control d in All(child))yield return d;}
    [STAThread]static int Main(string[] args)
    {
        try{
            Console.OutputEncoding=new UTF8Encoding(false);Application.EnableVisualStyles();Control.CheckForIllegalCrossThreadCalls=true;
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);baseline=args.Length>2&&args[2]=="baseline";
            if(Directory.Exists(qa))throw new Exception("Fresh fixture required.");Directory.CreateDirectory(qa);
            string hostFile=Path.Combine(root,"CurrentUiHost.dll");if(!File.Exists(hostFile))hostFile=Path.Combine(root,"Xiongxin.OWTS.Desktop.exe");
            var hostAsm=Assembly.LoadFrom(hostFile);var settings=Activator.CreateInstance(hostAsm.GetType("Xiongxin.OWTS.Desktop.AppSettings",true));
            settings.GetType().GetProperty("DataRoot",B).SetValue(settings,qa,null);
            var state=Activator.CreateInstance(hostAsm.GetType("Xiongxin.OWTS.Desktop.ApplicationState",true));
            var hostType=hostAsm.GetType("Xiongxin.OWTS.Desktop.MainForm",true);
            using(var host=(Form)Activator.CreateInstance(hostType,B,null,new object[]{settings,state,true},null)){
                host.Opacity=0;host.ShowInTaskbar=false;host.Show();Application.DoEvents();host.ClientSize=new Size(1260,790);Application.DoEvents();Call(host,"ApplyResponsiveChrome");
                var shell=(Control)Get(host,"rootLayout");int count=(int)Get(host,"ResponsiveChromeBuildCount");bool compact=(bool)Get(host,"compactDisplayChrome");string geometry=Geometry(shell);
                var command=(Panel)Get(host,"commandBarPanel");Console.WriteLine("HOME_INITIAL="+command.Bounds+";AUTO="+command.AutoSize+";MIN="+command.MinimumSize+";PREFERRED="+command.PreferredSize+";ROW="+((TableLayoutPanel)shell).RowStyles[1].Height);
                for(int cycle=0;cycle<3;cycle++){
                    host.WindowState=FormWindowState.Minimized;Application.DoEvents();int before=(int)Get(host,"ResponsiveChromeBuildCount");
                    for(int i=0;i<30;i++)Call(host,"ApplyResponsiveChrome");
                    Check((int)Get(host,"ResponsiveChromeBuildCount")==before,"Home does not relayout while minimized");
                    host.WindowState=FormWindowState.Normal;Application.DoEvents();Call(host,"ApplyResponsiveChrome");
                    Check((bool)Get(host,"compactDisplayChrome")==compact,"Home compact decision survives minimized restore");
                    Console.WriteLine("HOME_RESTORED="+command.Bounds+";AUTO="+command.AutoSize+";MIN="+command.MinimumSize+";PREFERRED="+command.PreferredSize+";ROW="+((TableLayoutPanel)shell).RowStyles[1].Height);
                    SameGeometry(shell,geometry,"Home settled geometry survives minimized restore");
                }
                count=(int)Get(host,"ResponsiveChromeBuildCount");for(int i=0;i<100;i++)Call(host,"ApplyResponsiveChrome");
                Check((int)Get(host,"ResponsiveChromeBuildCount")==count,"Home unchanged geometry uses cache");
                var title=(Control)Get(host,"productTitle");var buttons=(Control)Get(host,"headerActions");
                Check(!title.Parent.Bounds.IntersectsWith(buttons.Bounds),"Home title and top buttons do not overlap");
                host.ClientSize=new Size(1150,730);Application.DoEvents();Call(host,"ApplyResponsiveChrome");
                Check((int)Get(host,"ResponsiveChromeBuildCount")>count,"Actual home resize invalidates cached chrome");host.Close();
            }
            string projectFile=Path.Combine(root,"ProjectCreation.dll");if(!File.Exists(projectFile))projectFile=Path.Combine(root,"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll");
            var asm=Assembly.LoadFrom(projectFile);
            using(var page=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectCreationControl",true)))
            using(var host=new Form{Opacity=0,ShowInTaskbar=false,ClientSize=new Size(1420,900)}){
                page.GetType().GetProperty("DataRootDirectory",B).SetValue(page,qa,null);
                page.Dock=DockStyle.Fill;host.Controls.Add(page);host.Show();Application.DoEvents();
                ((TextBox)Get(page,"startNameText")).Text="Synthetic unsaved start";((TextBox)Get(page,"notesText")).Text="Synthetic unsaved note";
                Call(page,"SetAccordionSectionExpanded","InsulationAndNotes",true);Call(page,"ApplyResponsiveLayout");Application.DoEvents();
                string tab=(string)Get(page,"expandedOptionalSectionCode");bool dirty=(bool)Get(page,"IsDirty");
                var shell=(Control)Get(page,"rootLayout");string beforeGeometry=Geometry(shell);int layouts=0;shell.Layout+=(s,e)=>layouts++;
                for(int i=0;i<100;i++)Call(page,"ApplyResponsiveLayout");
                Check(layouts==0,"Editor unchanged viewport does not repeat whole-page layout");
                for(int cycle=0;cycle<3;cycle++){
                    host.WindowState=FormWindowState.Minimized;Application.DoEvents();layouts=0;
                    for(int i=0;i<20;i++)Call(page,"ApplyResponsiveLayout");Check(layouts==0,"Editor ignores transient minimized viewport");
                    host.WindowState=FormWindowState.Normal;Application.DoEvents();Call(page,"ApplyResponsiveLayout");
                    Check(Geometry(shell)==beforeGeometry,"Editor restored settled geometry matches prior page");
                    Check((string)Get(page,"expandedOptionalSectionCode")==tab&&((TextBox)Get(page,"notesText")).Text=="Synthetic unsaved note"&&((TextBox)Get(page,"startNameText")).Text=="Synthetic unsaved start"&&(bool)Get(page,"IsDirty")==dirty,"Editor current tab and unsaved edits survive restore");
                }
                host.ClientSize=new Size(1000,650);Application.DoEvents();Call(page,"ApplyResponsiveLayout");
                Check(Geometry(shell)!=beforeGeometry,"Real editor resize uses new viewport geometry");
                Check((string)Get(page,"expandedOptionalSectionCode")==tab,"Real resize does not select initial tab");
                layouts=0;for(int i=0;i<100;i++)Call(page,"ApplyResponsiveLayout");Check(layouts==0,"New editor viewport settles and reuses layout");
                var previous=page.Font;using(var font=new Font(previous.FontFamily,previous.SizeInPoints+2,previous.Style)){
                    layouts=0;page.Font=font;Call(page,"ApplyResponsiveLayout");Check(layouts>0,"Font/DPI metrics change invalidates editor layout");page.Font=previous;
                }
                page.GetType().GetProperty("LanguageIndex",B).SetValue(page,1,null);Call(page,"ApplyResponsiveLayout");
                Check((string)Get(page,"expandedOptionalSectionCode")==tab,"Language refresh preserves current tab");host.Close();
            }
            Console.WriteLine("WINDOW_MINIMIZE_RESTORE_PASS="+checks+";BASELINE="+baseline+";HARDWARE=False;SDK=False;PHYSICAL_DPI=False;QA="+qa);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
