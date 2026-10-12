using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryWaveformInteractionTests
    {
        internal static int Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,text)=>{if(!ok)throw new Exception("Location interaction: "+text);++checks;};
            using(var host=new Form {Size=new Size(960,530),Opacity=0,ShowInTaskbar=false})
            using(var wave=new FactoryWaveformControl {Dock=DockStyle.Fill})
            {
                wave.WrapToolbar=true;wave.EnableLocationInteraction();host.Controls.Add(wave);host.Show();Application.DoEvents();
                var adc=new short[10000];adc[100]=30000;adc[3000]=100;adc[7000]=-200;
                wave.SetRaw(adc,1000,1,32767,32767,10000);wave.ConfigureSelection(2000,10000);
                wave.ResetView();Rectangle plot=wave.PlotBounds;
                Func<double,double,Point> at=(x,y)=>new Point(plot.Left+(int)(x*plot.Width),plot.Top+(int)(y*plot.Height));
                double originalSpan=wave.ChargeVisibleSpan;
                check(originalSpan<=1000,"auto scale excludes switching prefix but retains raw ADC");
                wave.ApplyBoxZoomForTest(at(.35,.2),at(.75,.8));
                check(wave.VisibleStartMs>3400 && wave.VisibleStartMs<3600 && wave.VisibleEndMs>7400 && wave.VisibleEndMs<7600,"left drag rectangle changes time view");
                check(wave.ChargeVisibleSpan<originalSpan*.62 && wave.ChargeVisibleSpan>originalSpan*.58,"rectangle also zooms charge axis");
                check(wave.SelectionStartMs==2000 && wave.SelectionEndMs==10000 && wave.AutoScaleStartMs==2000,"box zoom cannot change pulse selection or range start");
                double first=wave.VisibleStartMs,last=wave.VisibleEndMs;
                wave.ChargeRulerMaximumPc=1234;
                check(Math.Abs(wave.ChargeVisibleSpan-2468)<1e-7 && wave.VisibleStartMs==first && wave.VisibleEndMs==last,"manual symmetric ruler resets Y only, retaining time zoom");
                wave.ChargeRulerMaximumPc=0;check(Math.Abs(wave.ChargeVisibleSpan-originalSpan)<1e-7,"auto ruler restores post-crossing scale");
                wave.ResetView();wave.ApplyBoxZoomForTest(at(.75,.8),at(.35,.2));
                check(Math.Abs(wave.VisibleStartMs-first)<1e-7 && Math.Abs(wave.VisibleEndMs-last)<1e-7,"reverse direction rectangle equals forward");
                wave.ResetView();wave.ApplyBoxZoomForTest(at(.4,.4),new Point(at(.4,.4).X+2,at(.7,.8).Y));
                check(wave.VisibleStartMs==0 && wave.VisibleEndMs==10000,"tiny or accidental drag does not zoom");
                wave.ApplyBoxZoomForTest(at(.4,.4),new Point(plot.Right+100,plot.Bottom+100));
                check(wave.VisibleEndMs==10000 && wave.VisibleStartMs>=3990 && wave.VisibleStartMs<=4010,"drag outside plot clamps to actual data");
                wave.ResetView();wave.ApplyBoxZoomForTest(at(.2,.5),at(.3,.5));
                check(wave.SelectionStartMs>2990 && wave.SelectionStartMs<3010 && wave.VisibleStartMs==0 && wave.VisibleEndMs==10000,"dragging A line adjusts selection without zooming");
                wave.BoxZoomEnabled=false;wave.SetVisibleTime(3000,7000);wave.ApplyBoxZoomForTest(at(.5,.5),at(.6,.7));
                check(wave.VisibleStartMs<3000 && Math.Abs(wave.VisibleEndMs-wave.VisibleStartMs-4000)<1e-7,"unchecked mode retains left-button pan");
                bool rejected=false;try{wave.ChargeRulerMaximumPc=-1;}catch(ArgumentOutOfRangeException){rejected=true;}
                check(rejected,"invalid charge ruler rejected");
                wave.ResetView();check(wave.VisibleStartMs==0 && wave.VisibleEndMs==10000 && Math.Abs(wave.ChargeVisibleSpan-originalSpan)<1e-7,"reset returns complete time and charge view");
                check(adc[100]==30000 && adc[3000]==100 && adc[7000]==-200 && wave.SourceSampleCount==10000,"all interactions leave raw data intact");
                double requestedFirst=-1,requestedLast=-1;
                wave.PulseWindowRequested+=(a,b)=>{requestedFirst=a;requestedLast=b;};
                wave.PulseWindowEnabled=true;wave.ApplyBoxZoomForTest(at(.45,.5),at(.55,.5));
                check(!wave.BoxZoomEnabled && requestedFirst>4400 && requestedFirst<4600 && requestedLast>5400 && requestedLast<5600,"pulse window mode emits horizontal ROI instead of zoom/pan");
                check(wave.VisibleStartMs==0 && wave.VisibleEndMs==10000,"window request leaves viewport unchanged");
                host.Close();
            }
            return checks;
        }
    }
}
