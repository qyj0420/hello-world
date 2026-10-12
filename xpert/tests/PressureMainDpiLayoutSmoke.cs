using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Built with the desktop manifest and config by Test-UiRepairFocused. This
// exercises the actual ModuleHost at the monitor DPI, not enlarged point fonts.
internal static class PressureMainDpiLayoutSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly host;static int checks;static readonly List<string> failures=new List<string>();
    static object New(string n,params object[] args){return host.GetType("Xiongxin.OWTS.Desktop."+n,true).GetConstructors(All).Single(c=>c.GetParameters().Length==args.Length).Invoke(args);}
    static object Call(object c,string n,params object[] args){return c.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(c,args);}
    static object Field(object c,string n){return c.GetType().GetField(n,All).GetValue(c);}
    static void Set(object c,string n,object value){c.GetType().GetProperty(n).SetValue(c,value,null);}
    static object Get(object c,string n){return c.GetType().GetProperty(n).GetValue(c,null);}
    static void Check(bool ok,string message){checks++;if(!ok)failures.Add(message);}
    static IEnumerable<Control> Descendants(Control c){foreach(Control child in c.Controls){yield return child;foreach(var d in Descendants(child))yield return d;}}
    static void Settle(Control c){for(int i=0;i<5;i++){Call(c,"RefreshPressureSidebarLayout");c.PerformLayout();Application.DoEvents();}}
    static void Fits(Control c,Control root,string tag){var r=c.RectangleToScreen(c.ClientRectangle);Check(r.Width>0&&r.Height>0,tag+" positive size "+c.Name);for(var p=c.Parent;p!=null;p=p.Parent){Check(p.RectangleToScreen(p.ClientRectangle).Contains(r),tag+" ancestor clips "+c.Name+" in "+p.Name);if(p==root)break;}}
    static void Inspect(Control c,string tag){
        var right=c.Controls.Find("PressureSidebar",true).Single();
        foreach(var x in Descendants(right).Where(x=>x.Visible&&(x is ButtonBase||x is Label||x is TextBox))){
            if(String.IsNullOrWhiteSpace(x.Text))continue;Fits(x,c,tag);
            if(x is ButtonBase||x is Label){var flags=x is Label&&((Label)x).AutoEllipsis?TextFormatFlags.SingleLine:TextFormatFlags.WordBreak;
                var need=TextRenderer.MeasureText(x.Text,x.Font,new Size(Math.Max(1,x.ClientSize.Width-(x is ButtonBase?14:0)),Int32.MaxValue),flags|TextFormatFlags.NoPadding);
                Check(x.Height>=need.Height+(x is ButtonBase?6:0),tag+" text clips "+x.Name+" "+x.Text);}
        }
        foreach(var p in Descendants(right).OfType<ScrollableControl>())Check(!p.AutoScroll&&!p.VerticalScroll.Visible&&!p.HorizontalScroll.Visible,tag+" no sidebar scroll "+p.Name);
        Check(!(bool)Get(c,"IsBusy")&&(long)Get(c,"PressureRequestSequence")==0,tag+" no hardware request");
    }
    static void InspectDebugEntry(Control c,object app,object state,string qa,string message,string tag){
        // Exercise the real top-level entry and its snapshot delegate. The
        // verification shell suppresses hardware discovery; AppPaths is scoped
        // to this test before construction, including the default debug log.
        using(var shell=(Form)New("MainForm",app,state,true,true)){
            shell.ShowInTaskbar=false;shell.StartPosition=FormStartPosition.Manual;shell.Location=new Point(-30000,-30000);
            shell.GetType().GetField("activeModule",All).SetValue(shell,c);shell.Show();Application.DoEvents();
            var entry=(Button)Field(shell,"standaloneDebugButton");
            Check(entry.Name=="StandaloneDebug"&&entry.Visible&&entry.Enabled,tag+" top diagnostics entry accessible");
            entry.PerformClick();Application.DoEvents();
            using(var debug=(Form)Field(shell,"pressureDebugWindow")){
                var stateBox=(TextBox)Field(debug,"stateBox");
                Check(debug.Visible,tag+" top entry opens diagnostics window");
                Check(stateBox.Text.Contains(message),tag+" full current message and LastError survive empty post-process error");
                Check(stateBox.ReadOnly&&stateBox.ScrollBars==ScrollBars.Vertical,tag+" full diagnostics readable without editing");
                var watch=System.Diagnostics.Stopwatch.StartNew();
                while((bool)Field(debug,"reading")&&watch.ElapsedMilliseconds<3000){Application.DoEvents();System.Threading.Thread.Sleep(10);}
                Check(!((bool)Field(debug,"reading")),tag+" diagnostics read completes");
                Check(((TextBox)Field(debug,"pathBox")).Text.StartsWith(qa,StringComparison.OrdinalIgnoreCase),tag+" isolated diagnostics path");
                const string postError="SYNTHETIC_POSTPROCESS_ERROR_42",rawError="SYNTHETIC_RAW_ERROR_91";
                c.GetType().GetField("lastPostProcessError",All).SetValue(c,postError);
                c.GetType().GetField("lastError",All).SetValue(c,rawError);
                Call(debug,"RefreshLog");Application.DoEvents();
                Check(stateBox.Text.Contains(message)&&stateBox.Text.Contains(postError)&&stateBox.Text.Contains(rawError),tag+" current hint and both original error channels retained");
                debug.Close();
            }
            Check(!((Timer)Field(shell,"devicePollTimer")).Enabled,tag+" diagnostics never starts device polling");
            shell.GetType().GetField("activeModule",All).SetValue(shell,null);shell.Close();
        }
    }
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
        host=Assembly.LoadFrom(Path.Combine(build,"Xiongxin.OWTS.Desktop.exe"));
        host.GetType("Xiongxin.OWTS.Desktop.AppPaths",true).GetField("LocalDataDirectory",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,Path.Combine(qa,"profile"));
        foreach(var size in new[]{new Size(1180,720),new Size(1065,416)}){
            object app=New("AppSettings"),state=New("ApplicationState"),log=New("Diagnostics",Path.Combine(qa,"logs"));Set(Get(app,"Hardware"),"Enabled",false);Set(app,"LanguageCode","zh-CN");
            using(var owner=(IDisposable)New("ModuleHost",app,state,log,Path.Combine(build,"modules"))) {
                object[] create={"PressureTest",null,null};Check((bool)Call(owner,"TryCreateControl",create),"host creates PressureTest: "+create[2]);
                using(var c=(Control)create[1])using(var form=new Form{ClientSize=size,FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)}){
                    Set(c,"SimulationMode",true);c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();Settle(c);
                    string tag="dpi"+c.DeviceDpi+"-"+size.Width+"x"+size.Height;Check(c.DeviceDpi==form.DeviceDpi,tag+" host/control DPI matches");
                    using(var g=c.CreateGraphics())Check(Math.Abs(g.DpiX-c.DeviceDpi)<1,tag+" actual graphics DPI, not simulated font scale");
                    var right=c.Controls.Find("PressureSidebar",true).Single();Console.WriteLine(tag+" RIGHT="+right.Width+" FONT_PT="+c.Font.SizeInPoints+" FONT_HEIGHT="+c.Font.Height);
                    int customWidth=right.Width;
                    Call(c,"SetPressurePlan",new[]{0d,.5d,1d},new[]{1,2,1},new[]{0d,4.35d,8.7d});
                    Call(c,"SetPressureProgramMode","PLAN");Settle(c);Inspect(c,tag+" plan");
                    Check(right.Width==customWidth,tag+" actual-DPI plan/custom sidebars have the same width");
                    Call(c,"SetPressureProgramMode","CUSTOM");Settle(c);
                    Check(right.Width==customWidth,tag+" returning to custom retains sidebar width");
                    Check(Math.Abs(((Control)Field(c,"customVoltageValueButton")).Font.SizeInPoints-16f)<.01,tag+" unchanged 16pt value");Inspect(c,tag);
                    foreach(var name in new[]{"u0VoltageModeButton","rmsVoltageModeButton","peakVoltageModeButton"}){var x=(Control)Field(c,name);Check(x.Width-14>=TextRenderer.MeasureText(x.Text,x.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width,tag+" complete one-line voltage unit "+name);}
                    Call(c,"ReportP04Status",true,false,DateTime.UtcNow);Settle(c);Inspect(c,tag+" emergency stopped");
                    Call(c,"ReportP04Status",false,false,DateTime.UtcNow);Settle(c);Inspect(c,tag+" emergency unknown");
                    string fullMessage=String.Concat(Enumerable.Repeat("项目计划参数需要核对，请检查电压及次数。",100));
                    c.GetType().GetField("projectPlanError",All).SetValue(c,fullMessage);
                    c.GetType().GetField("lastError",All).SetValue(c,fullMessage); // real LoadProjectPlan failure writes both
                    Call(c,"UpdatePostProcessUi");Settle(c);Inspect(c,tag+" long message");
                    using(var bitmap=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bitmap,new Rectangle(Point.Empty,c.Size));bitmap.Save(Path.Combine(qa,tag+"-custom.png"));}
                    Check(!c.Controls.Find("PressureMessageDetailsButton",true).Single().Visible,tag+" obsolete details row remains hidden");
                    var messageProperty=c.GetType().GetProperty("PressureActionFullMessage");
                    Check(messageProperty!=null&&!messageProperty.CanWrite&&(string)messageProperty.GetValue(c,null)==fullMessage,tag+" full current message has readonly public diagnostic outlet");
                    InspectDebugEntry(c,app,state,qa,fullMessage,tag);
                    Check(!(bool)Get(Get(app,"Hardware"),"Enabled"),tag+" hardware stays disabled");form.Controls.Remove(c);form.Close();
                }
            }
        }
        foreach(var failure in failures)Console.WriteLine("FAIL "+failure);
        Console.WriteLine("PRESSURE_MAIN_DPI_LAYOUT_CHECKS="+checks+";FAILURES="+failures.Count+";HardwareAccessed=false;WorkersStarted=0;QA="+qa);return failures.Count==0?0:1;
    }catch(Exception e){Console.Error.WriteLine(e);return 2;}}
}
