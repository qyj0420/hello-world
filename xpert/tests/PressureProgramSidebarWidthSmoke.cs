using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Synthetic WinForms layout only; no product entry point or hardware calls.
internal static class PressureProgramSidebarWidthSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string description){checks++;if(!value)throw new Exception(description);}
    static void Set(object target,string name,object value){target.GetType().GetProperty(name,All).SetValue(target,value,null);}
    static object Get(object target,string name){return target.GetType().GetProperty(name,All).GetValue(target,null);}
    static object Call(object target,string name,params object[] values){return target.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(target,values);}
    static System.Collections.Generic.IEnumerable<Control> Descendants(Control control)
    {foreach(Control child in control.Controls){yield return child;foreach(Control nested in Descendants(child))yield return nested;}}
    static void Settle(Control control)
    {for(int i=0;i<3;i++){Call(control,"RefreshPressureSidebarLayout");control.PerformLayout();Application.DoEvents();}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass isolated build root and QA root.");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            foreach(int language in new[]{0,1})foreach(float scale in new[]{1f,1.5f})
            using(var host=new Form{ClientSize=new Size(1366,768),AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(control,"EmbeddedMode",true);Set(control,"SimulationMode",true);Set(control,"FrequencyReminderEnabled",false);
                Set(control,"MaximumPeakVoltageKv",300d);Set(control,"BaseU0VoltageKv",8.7d);Set(control,"LanguageIndex",language);
                Call(control,"SetPressurePlan",new[]{0d,.5d,1d},new[]{1,1,3},new[]{0d,4.35d,8.7d});
                foreach(var item in Descendants(control).Concat(new[]{control}).Select(c=>new{Control=c,Font=c.Font}).ToArray())
                    item.Control.Font=new Font(item.Font.FontFamily,item.Font.SizeInPoints*scale,item.Font.Style);
                control.Dock=DockStyle.Fill;host.Controls.Add(control);host.Show();Settle(control);
                var sidebar=control.Controls.Find("PressureSidebar",true).Single();
                var left=control.Controls.Find("RealtimeSidebar",true).Single();
                foreach(Size size in new[]{new Size(1366,768),new Size(1920,1080)})
                {
                    host.ClientSize=size;Call(control,"SetPressureProgramMode","PLAN");Settle(control);
                    int planWidth=sidebar.Width,leftWidth=left.Width;Rectangle planBounds=sidebar.Bounds;
                    string tag=size+" language="+language+" fontScale="+scale;
                    Check((string)Get(control,"PressureProgramModeCode")=="PLAN",tag+" actual plan mode");
                    Check(planWidth>0,tag+" nonempty existing plan width");
                    Call(control,"SetPressureProgramMode","CUSTOM");Settle(control);
                    Console.WriteLine(tag+" PLAN="+planWidth+" CUSTOM="+sidebar.Width);
                    Check((string)Get(control,"PressureProgramModeCode")=="CUSTOM",tag+" actual custom mode");
                    Check(sidebar.Width==planWidth,tag+" custom width equals plan width");
                    Check(sidebar.Bounds.X==planBounds.X&&left.Width==leftWidth,tag+" no unrelated horizontal pane movement");
                    Call(control,"SetCustomVoltageValue",9.9d);Settle(control);
                    Check(sidebar.Width==planWidth,tag+" custom value does not resize right column");
                    Call(control,"SetPressureProgramMode","PLAN");Settle(control);
                    Check(sidebar.Width==planWidth,tag+" return retains original plan width");
                    Check(((double[])Call(control,"GetPressurePlanMultipliers")).SequenceEqual(new[]{0d,.5d,1d}),tag+" plan data unchanged");
                    Check((long)Get(control,"PressureRequestSequence")==0&&!(bool)Get(control,"IsBusy"),tag+" layout sends no pressure command");
                }
            }
            Console.WriteLine("PRESSURE_PROGRAM_SIDEBAR_WIDTH_PASS="+checks+"; HARDWARE=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
