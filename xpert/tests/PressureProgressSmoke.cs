using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureProgressSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Get(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void Field(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static object Call(object value,string name,params object[] data){return value.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==data.Length).Invoke(value,data);}
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static void Feedback(Control c,double actual,string state="RUNNING"){Call(c,"SetPressureState",state,actual);Application.DoEvents();}
    static Color Sample(Button button,double fraction,string path)
    {
        using(var image=new Bitmap(button.Width,button.Height))
        {
            button.DrawToBitmap(image,new Rectangle(Point.Empty,button.Size));
            if(path!=null)image.Save(path);
            return image.GetPixel((int)((button.Width-1)*fraction),button.Height-12);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"pressure-progress-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1280,600),StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ShowInTaskbar=false})
            using(var c=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                c.GetType().GetProperty("EmbeddedMode").SetValue(c,true,null);c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();
                var button=(Button)c.Controls.Find("PressureActionButton",true).Single();
                long pressure=(long)Get(c,"PressureRequestSequence"),save=(long)Get(c,"SaveRequestSequence");
                Field(c,"requestedTargetPeakVoltageKv",20d);
                Field(c,"actionBusy",true); // host feedback while a request is outstanding
                foreach(double actual in new[]{0d,5d,10d,20d,27d})
                {
                    Feedback(c,actual);Check((bool)Get(button,"MeasurementProgressVisible"),"feedback visible");
                    Check(Math.Abs((double)Get(button,"MeasurementProgress")-Math.Min(.95,actual/20))<1e-12,"ratio uses same peak units; unfinished round remains below full "+actual);
                    Check(button.Text.Contains(actual.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" / 20.0 kV"),"actual unrounded-overlimit feedback remains numeric "+actual);
                    Check(!button.Enabled,"in-progress button cannot issue another request");
                }
                Feedback(c,10);Color left=Sample(button,.15,Path.Combine(qa,"half.png")),right=Sample(button,.85,null);
                Check(left.ToArgb()!=right.ToArgb(),"half feedback paints only half background even while disabled");
                Feedback(c,27);Check(Sample(button,.15,Path.Combine(qa,"over-target.png")).ToArgb()==Sample(button,.85,null).ToArgb(),"over-target background saturates but value remains 27");
                Feedback(c,4);Call(c,"SetCustomVoltageMode","RMS");Call(c,"SetCustomVoltageValue",8d);
                Check(Math.Abs((double)Get(button,"MeasurementProgress")-.2)<1e-12,"later RMS editor changes do not replace immutable requested peak target");
                foreach(double unknown in new[]{Double.NaN,Double.PositiveInfinity,-1d})
                {
                    Feedback(c,unknown);Check(Double.IsNaN((double)Get(button,"MeasurementProgress")),"invalid feedback has no progress");
                    Check(button.Text.Contains("— / 20.0"),"invalid feedback is not a fake zero");
                    Check(Sample(button,.15,null).ToArgb()==Sample(button,.85,null).ToArgb(),"unknown has no fill");
                }
                foreach(double target in new[]{0d,Double.NaN,Double.PositiveInfinity,-1d})
                {Field(c,"requestedTargetPeakVoltageKv",target);Feedback(c,0);Check(Double.IsNaN((double)Get(button,"MeasurementProgress")),"non-positive or unknown target has no division/fake progress");}
                Field(c,"requestedTargetPeakVoltageKv",20d);Feedback(c,12,"FAULT");Check((bool)Get(button,"Danger"),"fault remains dangerous");Sample(button,.15,Path.Combine(qa,"fault.png"));
                Feedback(c,10,"WAITING");Check(!(bool)Get(button,"MeasurementProgressVisible"),"waiting clears progress");Check(Double.IsNaN((double)Get(button,"MeasurementProgress")),"waiting clears old ratio");
                foreach(string state in new[]{"RAMPING","HOLDING"}){Feedback(c,6,state);Check(Math.Abs((double)Get(button,"MeasurementProgress")-.3)<1e-12,state+" never invents completed fill");}
                Feedback(c,6,"COMPLETE");Check((double)Get(button,"MeasurementProgress")==1,"explicit successful round completion fills the bar, not the measured number");
                Check((long)Get(c,"PressureRequestSequence")==pressure&&(long)Get(c,"SaveRequestSequence")==save,"feedback and drawing do not issue hardware/save requests");
                form.Close();
            }
            Console.WriteLine("PRESSURE_PROGRESS_CHECKS="+checks+";HARDWARE=False;QA="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
