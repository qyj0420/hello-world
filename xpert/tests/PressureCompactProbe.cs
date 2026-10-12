using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;
static class PressureCompactProbe
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static IEnumerable<Control> Walk(Control c){foreach(Control child in c.Controls){yield return child;foreach(var x in Walk(child))yield return x;}}
    static object Call(object c,string n,params object[] a){return c.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(c,a);}
    static void Set(object c,string n,object v){c.GetType().GetProperty(n).SetValue(c,v,null);}
    [STAThread]static void Main(string[] a)
    {
        Application.EnableVisualStyles();
        var asm=Assembly.LoadFrom(Path.Combine(a[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        using(var form=new Form{ClientSize=new Size(1065,416),AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
        using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl")))
        {
            Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);
            foreach(var x in Walk(c).Concat(new[]{c}).Select(x=>new{C=x,F=x.Font}).ToArray())x.C.Font=new Font(x.F.FontFamily,x.F.SizeInPoints*1.5f,x.F.Style);
            c.Bounds=new Rectangle(Point.Empty,form.ClientSize);form.Controls.Add(c);form.Show();c.Size=form.ClientSize;
            for(int i=0;i<3;i++){Call(c,"RefreshPressureSidebarLayout");c.PerformLayout();Application.DoEvents();}
            foreach(string n in new[]{"ActionCardHeight","ProgramSettingsHeight","RangeSettingsHeight"})Console.WriteLine(n+"="+Call(c,n));
            foreach(string n in new[]{"condensedPressureSettings","groupedPressureSettings","pressureMessageRowHeight"})Console.WriteLine(n+"="+c.GetType().GetField(n,Flags).GetValue(c));
            foreach(var x in Walk(c).Where(x=>x.Visible && new[]{"PressureSidebar","ActionLayout","PressureSettingsScroll","PressureSettingsHost","CompactProgramTab","CustomVoltageHintLabel","PressureActionButton","AutomaticPostProcessCheckBox"}.Contains(x.Name)))Console.WriteLine(x.Name+" size="+x.Size+" font="+x.Font.Height+" text="+x.Text.Replace('\n','|'));
            Directory.CreateDirectory(a[1]);using(var b=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(b,new Rectangle(Point.Empty,c.Size));b.Save(Path.Combine(a[1],"compact-probe.png"));}
        }
    }
}
