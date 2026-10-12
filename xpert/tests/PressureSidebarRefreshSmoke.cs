using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureSidebarRefreshSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
            using(var host=new Form{ClientSize=new Size(1366,768),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);c.Dock=DockStyle.Fill;host.Controls.Add(c);host.Show();
                Call(c,"SetPressurePlan",new double[]{0,.5,1,1.5},new int[]{1,1,3,3});Call(c,"SetPressureProgramMode","PLAN");
                Call(c,"SetSelectedPlanStep",2);Call(c,"UpdatePostProcessUi");Application.DoEvents();
                int arrangements=0;
                foreach(string name in new[]{"arrangePressureBody","arrangeDisplaySettings","arrangePressureActions","arrangePressureSettings","arrangeRealtimeSidebar"})
                {
                    var f=c.GetType().GetField(name,All);var original=(Action)f.GetValue(c);
                    f.SetValue(c,(Action)(()=>{arrangements++;original?.Invoke();}));
                }
                for(int i=0;i<20;i++)
                {
                    int row=1+i%3;Call(c,"SetSelectedPlanStep",row);Call(c,"SetAutomaticPressure",i%2==0);
                    Check((int)Get(c,"SelectedPlanStepIndex")==row,"plan selection retained");
                    Check((bool)Get(c,"AutomaticPressureEnabled")== (i%2==0),"automatic save preference retained");
                }
                Console.WriteLine("UNCHANGED_LAYOUT_ARRANGEMENTS="+arrangements);
                Check(arrangements==0,"plan/save value-only changes must not rearrange whole sidebars");
                var hint=(Label)c.Controls.Find("PostProcessHintLabel",true).Single();
                var action=(Control)c.GetType().GetField("pressureActionButton",All).GetValue(c);
                var actionBounds=action.RectangleToScreen(action.ClientRectangle);
                c.GetType().GetField("projectPlanError",All).SetValue(c,"QA safety/plan diagnostic");Call(c,"UpdatePostProcessUi");Application.DoEvents();
                Check(hint.AccessibleDescription=="QA safety/plan diagnostic","new diagnostic remains accessible");
                Check(arrangements==0,"diagnostic content uses reserved space without rearranging sidebars");
                Check(action.RectangleToScreen(action.ClientRectangle)==actionBounds,"new diagnostic does not move action button");
                Check(!c.Controls.Find("PressureMessageDetailsButton",true).Single().Visible&&!hint.Visible,"diagnostic strip removed; complete text remains available to debug/accessibility");
                Check((string)Get(c,"PressureStateCode")!="RUNNING","no pressure was started");
            }
            Console.WriteLine("PRESSURE_SIDEBAR_REFRESH_PASS="+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
