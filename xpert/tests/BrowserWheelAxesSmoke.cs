using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class BrowserWheelAxesSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly assembly; static int checks;
    static object Call(object o,string method,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==method&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Get(object o,string name){var f=o.GetType().GetField(name,All);return f!=null?f.GetValue(o):o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){var f=o.GetType().GetField(name,All);if(f!=null)f.SetValue(o,value);else o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static double D(object o,string name){return Convert.ToDouble(Get(o,name));}
    static double[] Values(){return Enumerable.Range(0,2000).Select(i=>Math.Sin(i*.03)+(i==25?20:0)).ToArray();}
    static void Paint(Control c){using(var b=new Bitmap(c.Width,c.Height))c.DrawToBitmap(b,new Rectangle(Point.Empty,c.Size));}
    static void Verify(Control c,Rectangle p,string x0,string x1,string y0,string y1,Action reset)
    {
        Paint(c);double a=D(c,x0),b=D(c,x1),lo=D(c,y0),hi=D(c,y1);
        var e=new MouseEventArgs(MouseButtons.None,0,p.Left+p.Width*3/4,p.Top+p.Height/3,120);
        Call(c,"ApplyWheelZoom",e,false);Paint(c);
        Check(D(c,x1)-D(c,x0)<b-a,c.GetType().Name+" wheel reduces X span");
        Check(D(c,y0)==lo&&D(c,y1)==hi,c.GetType().Name+" wheel preserves exact Y limits");
        double newA=D(c,x0),newB=D(c,x1);
        Call(c,"ApplyWheelZoom",e,true);Paint(c);
        Check(D(c,x0)==newA&&D(c,x1)==newB,c.GetType().Name+" shift wheel preserves X limits");
        Check(D(c,y1)-D(c,y0)<hi-lo,c.GetType().Name+" shift wheel reduces Y span");
        reset();Paint(c);
        Check(Math.Abs(D(c,x0)-a)<1e-9&&Math.Abs(D(c,x1)-b)<1e-9,c.GetType().Name+" reset X");
        Check(Math.Abs(D(c,y0)-lo)<1e-9&&Math.Abs(D(c,y1)-hi)<1e-9,c.GetType().Name+" reset Y");
    }
    static Control Create(string name){var c=(Control)Activator.CreateInstance(assembly.GetTypes().Single(t=>t.Name==name),true);c.Size=new Size(1100,650);c.CreateControl();return c;}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var c=Create("BrowserPlot")) {
                Type t=assembly.GetType("HVX.Controls.BrowserSeries");object s=Activator.CreateInstance(t);Set(s,"Name","pulse");Set(s,"X",Enumerable.Range(0,2000).Select(i=>(double)i).ToArray());Set(s,"Y",Values());Set(s,"Color",Color.Teal);
                Array data=Array.CreateInstance(t,1);data.SetValue(s,0);Call(c,"SetData","Calibration","us","V","",data);Paint(c);
                Rectangle p=(Rectangle)Get(c,"Plot");Verify(c,p,"xmin","xmax","lo1","hi1",()=>Call(c,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,p.Left+20,p.Top+20,0)));
                Call(c,"ApplyWheelZoom",new MouseEventArgs(MouseButtons.None,0,p.Left+p.Width/2,p.Top+p.Height/2,120),true);Paint(c);
                double low=D(c,"lo1"),high=D(c,"hi1");
                Call(c,"UpdateData","Changed selection","us","V","",true,data);Paint(c);
                Check(D(c,"lo1")==low&&D(c,"hi1")==high,"comparison selection retains manual Y zoom with unchanged coordinates");
                Call(c,"UpdateData","Normalized","us","normalized","",true,data);Paint(c);
                Check(D(c,"hi1")-D(c,"lo1")>high-low,"comparison normalization resets Y range after units change");
            }
            using(var c=Create("BrowserLocationPlot")) {
                Call(c,"SetData","fixture",Values(),125e6);Rectangle p=(Rectangle)Get(c,"Plot");Verify(c,p,"start","end","viewYLow","viewYHigh",()=>Call(c,"ResetView"));
            }
            using(var c=Create("BrowserTestWaveformControl")) {
                object wave=Activator.CreateInstance(assembly.GetType("HVX.Controls.BrowserWaveform"));Set(wave,"Y",Values());Set(wave,"VoltageKv",Values());Set(wave,"SampleRateHz",125e6);Set(wave,"VoltageSampleRateHz",125e6);Call(c,"SetWaveform",wave,0);
                Rectangle p=(Rectangle)Get(c,"PlotBounds");Verify(c,p,"viewMin","viewMax","verticalMin","verticalMax",()=>Call(c,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,p.Left+20,p.Top+20,0)));
            }
            using(var c=Create("CalibrationWaveformControl")) {
                Call(c,"SetWaveform",Values(),0d,.008d);Paint(c);Rectangle p=(Rectangle)Call(c,"GetPlotRectangle",c.ClientRectangle);
                Verify(c,p,"viewXMin","viewXMax","viewYMin","viewYMax",()=>Call(c,"ResetView"));
                // Actual left mouse drag selects both dimensions away from editable cursors.
                Set(c,"AllowCursorEditing",false);double before=D(c,"viewXMax")-D(c,"viewXMin");
                Call(c,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,p.Left+p.Width/4,p.Top+p.Height/4,0));
                Call(c,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,1,p.Left+p.Width*3/4,p.Top+p.Height*3/4,0));
                Call(c,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,p.Left+p.Width*3/4,p.Top+p.Height*3/4,0));
                Check(D(c,"viewXMax")-D(c,"viewXMin")<before,"calibration box zoom");
            }
            using(var c=Create("OscillationPdControl")) {
                Call(c,"SetWaveforms",Values(),Values(),125e6);Paint(c);Rectangle p=(Rectangle)Call(c,"GetPlotRectangle",c.ClientRectangle);
                Set(c,"SeparateWaveBands",false);
                Verify(c,p,"viewXMin","viewXMax","pdYMin","pdYMax",()=>Call(c,"ResetView"));
                Set(c,"SeparateWaveBands",true);
                Verify(c,(Rectangle)Call(c,"PdBand",p),"viewXMin","viewXMax","pdYMin","pdYMax",()=>Call(c,"ResetView"));
            }
            Console.WriteLine("BROWSER_WHEEL_AXES_PASS="+checks+"; HARDWARE_ACCESSED=False");return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
