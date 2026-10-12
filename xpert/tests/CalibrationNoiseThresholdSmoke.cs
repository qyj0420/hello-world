using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationNoiseThresholdSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object F(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void SF(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object G(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void P(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool v,string label){checks++;if(!v)throw new Exception(label);}
    static void Mouse(Control c,string method,MouseButtons b,float x,float y,int delta=0){Call(c,method,new MouseEventArgs(b,1,(int)Math.Round(x),(int)Math.Round(y),delta));}
    [STAThread]static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[1]);Application.EnableVisualStyles();
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            var type=asm.GetType("HVX.Controls.CalibrationProgramControl",true);
            double[] raw=Enumerable.Range(0,20000).Select(i=>.002*Math.Sin(i*.031)+.01*Math.Exp(-Math.Pow((i-5000)/12d,2))).ToArray();
            using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-6000,-6000),ClientSize=new Size(1500,950)})
            using(var c=(Control)Activator.CreateInstance(type)) {
                c.Dock=DockStyle.Fill;host.Controls.Add(c);P(c,"EmbeddedMode",true);host.Show();
                Call(c,"SetPhaseNoiseWaveform",-1,raw,0d,8e-9);SF(c,"compactQualityTab",true);P(c,"NoiseBaselineVisible",true);
                Call(c,"SavePng",Path.Combine(args[1],"threshold-initial.png"),1500,950);
                var plot=(RectangleF)G(c,"CurrentNoisePlotRectangle");
                float x=plot.Left+plot.Width*.65f,y=Convert.ToSingle(Call(c,"NoiseValueY",0d));
                double x0=(double)F(c,"noiseX0"),x1=(double)F(c,"noiseX1"),y0=(double)F(c,"noiseY0"),y1=(double)F(c,"noiseY1");
                Mouse(c,"OnMouseDown",MouseButtons.Left,x,y);
                Check((bool)F(c,"noiseThresholdDragging")&&!(bool)F(c,"noiseZooming")&&!(bool)F(c,"noiseSelecting"),"line hit captures only threshold gesture");
                Mouse(c,"OnMouseMove",MouseButtons.Left,x,y-80);
                Mouse(c,"OnMouseWheel",MouseButtons.None,x,y-80,120);
                Mouse(c,"OnMouseUp",MouseButtons.Left,x,y-80);
                double value=(double)Call(c,"ManualNoiseThreshold",-1);
                Check(value>0&&Math.Abs(value-80*(y1-y0)/plot.Height)<(y1-y0)/plot.Height*2,"threshold drag maps actual volts");
                Check((double)F(c,"noiseX0")==x0&&(double)F(c,"noiseX1")==x1&&(double)F(c,"noiseY0")==y0&&(double)F(c,"noiseY1")==y1,"threshold drag and wheel during drag cannot zoom");
                Check((int)F(c,"noiseSelectionStart")==-1&&!c.Capture&&!(bool)G(c,"NoiseGestureActive"),"threshold drag does not select interference and releases capture");
                y=Convert.ToSingle(Call(c,"NoiseValueY",-value));
                Mouse(c,"OnMouseDown",MouseButtons.Left,x,y);Mouse(c,"OnMouseMove",MouseButtons.Left,x,y+25);Mouse(c,"OnMouseUp",MouseButtons.Left,x,y+25);
                double negative=(double)Call(c,"ManualNoiseThreshold",-1);
                Check(negative>value,"negative threshold handle changes the same absolute screening value");
                y=Convert.ToSingle(Call(c,"NoiseValueY",negative));
                Mouse(c,"OnMouseDown",MouseButtons.Left,x,y);Mouse(c,"OnMouseMove",MouseButtons.Left,x,y-20);
                Call(c,"ProcessCmdKey",new Message(),Keys.Escape);
                Check((double)Call(c,"ManualNoiseThreshold",-1)==negative&&!c.Capture,"Escape restores committed threshold");
                Mouse(c,"OnMouseDown",MouseButtons.Left,x,y);Mouse(c,"OnMouseMove",MouseButtons.Left,x,y-20);c.Capture=false;
                Check((double)Call(c,"ManualNoiseThreshold",-1)==negative&&!(bool)G(c,"NoiseGestureActive"),"capture loss rolls back threshold preview");
                float zx=plot.Left+plot.Width*.15f,zy=plot.Top+15;
                Mouse(c,"OnMouseDown",MouseButtons.Left,zx,zy);
                Check((bool)F(c,"noiseZooming")&&!(bool)F(c,"noiseThresholdDragging"),"outside threshold starts box zoom");
                Mouse(c,"OnMouseMove",MouseButtons.Left,plot.Left+plot.Width*.85f,plot.Bottom-15);
                Mouse(c,"OnMouseUp",MouseButtons.Left,plot.Left+plot.Width*.85f,plot.Bottom-15);
                Check((double)F(c,"noiseX1")-(double)F(c,"noiseX0")<x1-x0&&(double)F(c,"noiseY1")-(double)F(c,"noiseY0")<y1-y0,"box zoom applies both axes");
                Check((double)Call(c,"ManualNoiseThreshold",-1)==negative&&(int)F(c,"noiseSelectionStart")==-1,"box crossing threshold neither moves it nor selects template");
                Call(c,"ResetNoiseView");Call(c,"SavePng",Path.Combine(args[1],"threshold-adjusted.png"),1500,950);
                Check((double)Call(c,"ManualNoiseThreshold",-1)==negative,"reset view preserves real threshold");
                SF(c,"noiseTemplateMode",true);
                Mouse(c,"OnMouseDown",MouseButtons.Left,zx,zy);Mouse(c,"OnMouseMove",MouseButtons.Left,zx+70,zy+10);Mouse(c,"OnMouseUp",MouseButtons.Left,zx+70,zy+10);
                Check((int)F(c,"noiseSelectionPeak")>=0&&(double)Call(c,"ManualNoiseThreshold",-1)==negative,"explicit template mode selects pulse without changing threshold");
                var phase=Call(c,"GetPhaseStateNoLock",-1);Check(raw.SequenceEqual((double[])F(F(phase,"Noise"),"Samples")),"gestures never modify samples");
                foreach(int width in new[]{1024,1280,1500}) {
                    host.ClientSize=new Size(width,800);Application.DoEvents();var ui=G(c,"CompactLayout");
                    var bounds=new[]{(RectangleF)Call(c,"NoiseModeBounds",ui,0),(RectangleF)Call(c,"NoiseModeBounds",ui,1),(RectangleF)Call(c,"NoiseModeBounds",ui,2),(RectangleF)Call(c,"NoiseResetBounds",ui),(RectangleF)Call(c,"NoiseThresholdCheckBounds",ui)};
                    Check(!bounds.Where((a,i)=>bounds.Skip(i+1).Any(b=>a.IntersectsWith(b))).Any(),"noise toolbar hit regions do not overlap at "+width);
                }
                Call(c,"RestoreAutomaticNoiseThreshold");Check(Call(c,"ManualNoiseThreshold",-1)==null,"automatic threshold restores acquisition decision");
                P(c,"SelectedPhaseIndex",0);Check(Call(c,"ManualNoiseThreshold",0)==null,"new phase does not inherit manual threshold");
                host.Close();
            }
            var resolve=asm.GetType("Xiongxin.Calibration.AcquisitionPlanning",true).GetMethod("ResolveScreeningThreshold",B);
            Check((double)resolve.Invoke(null,new object[]{.005,.015,.5})==.005,"manual screening value is not silently replaced by automatic trigger");
            Check((double)resolve.Invoke(null,new object[]{null,.015,.5})==.015,"automatic screening uses preflight result");
            foreach(double invalid in new[]{0,-1,.5,double.NaN,double.PositiveInfinity}) {
                bool rejected=false;try{resolve.Invoke(null,new object[]{invalid,.015,.5});}catch(TargetInvocationException){rejected=true;}
                Check(rejected,"invalid/out-of-range threshold rejected");
            }
            Console.WriteLine("CALIBRATION_NOISE_THRESHOLD_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
