using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class PressureManualThresholdGuideSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){++checks;if(!value)throw new Exception(why);}
    static object Get(object value,string name)
    {var type=value.GetType();var property=type.GetProperty(name,B);return property!=null?property.GetValue(value,null):type.GetField(name,B).GetValue(value);}
    static object Call(object value,string name,params object[] arguments)
    {return value.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length).Invoke(value,arguments);}
    static Bitmap Render(Control waveform)
    {var bitmap=new Bitmap(waveform.Width,waveform.Height);waveform.DrawToBitmap(bitmap,waveform.ClientRectangle);return bitmap;}
    static int ThresholdPixels(Bitmap image,int y,int left,int right)
    {
        int count=0;
        for(int x=left;x<right;x++)for(int row=Math.Max(0,y-2);row<=Math.Min(image.Height-1,y+2);row++){
            var pixel=image.GetPixel(x,row);
            if(pixel.R>205&&pixel.G>110&&pixel.G<200&&pixel.B<90)++count;
        }
        return count;
    }
    [STAThread]static int Main(string[] arguments)
    {
        try{
            Application.EnableVisualStyles();string root=Path.GetFullPath(arguments[0]),qa=Path.GetFullPath(arguments[1]);
            if(Directory.Exists(qa))throw new Exception("fresh fixture required");Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var page=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            using(var form=new Form{ClientSize=new Size(900,420),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000)}){
                var waveform=(Control)Get(page,"waveformView");var dashboard=(Control)Get(page,"spectrumDashboardView");
                Check(!(bool)Get(waveform,"ManualThresholdGuideEnabled"),"unchecked default hides guide");
                Call(waveform,"SetData",new[]{0d,5d,-5d,0d},1000d,new[]{0d,20d,80d,0d},1000d);
                var voltage=(double[])Get(waveform,"voltage");var discharge=(double[])Get(waveform,"discharge");
                double maximum=(double)Get(waveform,"cachedPdMaximum");
                Call(page,"SetPressureAnalysisThreshold",true,42.02,true);
                Check((bool)Get(waveform,"ManualThresholdGuideEnabled")&&(double)Get(waveform,"ManualThresholdGuidePc")==42.02,"public setter synchronizes exact guide value");
                Check(!(bool)Get(page,"ActionPending")&&(long)Get(page,"PressureRequestSequence")==0,"guide edits never publish acquisition or pressure action");
                waveform.Parent=null;form.Controls.Add(waveform);waveform.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                Call(waveform,"SetDischargeScale",true,100d);
                using(var image=Render(waveform)){
                    var axis=(Font)Call(waveform,"CreateAxisFont");Rectangle plot;
                    using(axis)using(var graphics=Graphics.FromImage(image))plot=(Rectangle)Call(waveform,"CreatePlotBounds",graphics,100d,(double)Get(waveform,"cachedVoltageAbs"),axis);
                    int expectedY=plot.Bottom-(int)Math.Round(42.02/100d*plot.Height);
                    Check(ThresholdPixels(image,expectedY,plot.Left+plot.Width/2,plot.Right-4)>40,"dashed guide is drawn at true pC coordinate");
                    image.Save(Path.Combine(qa,"manual-threshold.png"));
                }
                var manual=(CheckBox)dashboard.Controls.Find("PressureManualThreshold",true).Single();
                var numeric=(NumericUpDown)dashboard.Controls.Find("PressureThresholdPc",true).Single();
                numeric.Value=23.45m;
                Check((double)Get(waveform,"ManualThresholdGuidePc")==23.45,"real numeric edits synchronize guide");
                manual.Checked=false;Check(!(bool)Get(waveform,"ManualThresholdGuideEnabled"),"unchecking hides guide immediately");
                using(var image=Render(waveform)){
                    var axis=(Font)Call(waveform,"CreateAxisFont");Rectangle plot;
                    using(axis)using(var graphics=Graphics.FromImage(image))plot=(Rectangle)Call(waveform,"CreatePlotBounds",graphics,100d,(double)Get(waveform,"cachedVoltageAbs"),axis);
                    Check(ThresholdPixels(image,plot.Bottom-(int)Math.Round(23.45/100d*plot.Height),plot.Left+plot.Width/2,plot.Right-4)==0,"unchecked plot has no threshold pixels");
                }
                manual.Checked=true;numeric.Value=1200m;
                Check((double)Get(waveform,"ManualThresholdGuidePc")==1200&&(double)Get(waveform,"DisplayPdMaximum")==100,"manual full scale is not rewritten to fit threshold");
                Call(waveform,"SetDischargeScale",false,double.NaN);
                Check((double)Get(waveform,"DisplayPdMaximum")>=1200,"automatic display includes checked threshold without clipping");
                Check(Object.ReferenceEquals(voltage,Get(waveform,"voltage"))&&Object.ReferenceEquals(discharge,Get(waveform,"discharge"))&&(double)Get(waveform,"cachedPdMaximum")==maximum,"threshold guide never mutates samples or calculated statistics");
                Call(page,"SetPressureAnalysisThreshold",true,0d,false);
                Check((bool)Get(waveform,"ManualThresholdGuideEnabled")&&(double)Get(waveform,"ManualThresholdGuidePc")==0,"zero threshold retains exact zero coordinate");
                page.GetType().GetProperty("ProjectPath").SetValue(page,qa,null);
                Check(!(bool)Get(waveform,"ManualThresholdGuideEnabled"),"project reset clears manual guide with analysis options");
                form.Close();
            }
            Console.WriteLine("PRESSURE_MANUAL_THRESHOLD_GUIDE_PASS="+checks+";HARDWARE_ACCESSED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
