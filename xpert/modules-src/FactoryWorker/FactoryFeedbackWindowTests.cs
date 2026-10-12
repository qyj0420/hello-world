using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryFeedbackWindowTests
    {
        internal static string Run()
        {
            int n = 0;
            Action<bool,string> check = (ok,why) => { if (!ok) throw new Exception("R19 window: " + why); n++; };
            var s = new FactorySettings { TargetKv = 4, DividerOnlyFeedback = true, FeedbackWindowMs = 10 };
            var plan = new FactoryArtFeedbackPlan(s);
            double[] raw = Block(10,.02,.64,-.36); raw[29] = -.383;
            var original = (double[])raw.Clone();
            var f = plan.Parse(raw,10,0,true,.6);
            check(f.WindowSampleCount == 10 && Math.Abs(f.DividerMedianRawV + .36) < 1e-12,"small tail glitch excluded from median");
            check(f.DividerRawV == -.383 && f.DividerMinimumRawV == -.383 && raw.SequenceEqual(original),"last, extrema and source array remain raw");
            check(Math.Abs(FactoryFeedbackWindow.Divider(f)*11000+3960)<1e-8,"median preserves negative polarity before control magnitude");
            raw = Block(10,.02,.64,.36); raw[2] = .383;
            f = plan.Parse(raw,10,0,true,.6);
            check(f.DividerMedianRawV == .36 && f.DividerMaximumRawV == .383,"positive early glitch retained for protection");
            raw = Block(10,.02,.64,.36); for(int i=0;i<5;i++)raw[i*3+2]=.34;
            f = plan.Parse(raw,10,0,true,0);
            check(Math.Abs(f.DividerMedianRawV-.35)<1e-12,"even window uses central pair");
            raw = Block(20,.02,.64,.3); for(int i=10;i<20;i++)raw[i*3+2]=.4;
            f = plan.Parse(raw,20,0,true,0);
            check(f.DividerMedianRawV == .4 && f.DividerMinimumRawV == .3,"fresh tail window with complete batch protection");
            foreach(int length in new[]{3,5,10,20,40})
            {
                s.FeedbackWindowMs=length; plan=new FactoryArtFeedbackPlan(s);
                var buffer=new Buffer { Divider=.36 };
                f=FactoryFeedbackStream.Read(plan,buffer,.2,()=>0);
                check(buffer.Requested==length && f.WindowSampleCount==length,"requested full block "+length);
                buffer.Divider=.1; f=FactoryFeedbackStream.Read(plan,buffer,.2,()=>0);
                check(f.DividerMedianRawV==.1,"no retained window across reads "+length);
                check(s.Clone().FeedbackWindowMs==length,"window XML round trip "+length);
            }
            foreach(int invalid in new[]{0,1,2,41,1000})
            { s.FeedbackWindowMs=invalid; check(Fail(()=>s.Validate(FactoryRunKind.Pressure))!=null,"invalid length "+invalid); }
            s.FeedbackWindowMs=10; plan=new FactoryArtFeedbackPlan(s);
            check(plan.Parse(Block(2,.02,.64,.36),2,0,true,0).WindowSampleCount==0,"short parser data not represented as full window");
            raw=Block(10,.02,.64,.36);raw[0]=Double.NaN;
            check(Fail(()=>plan.Parse(raw,10,0,true,0))!=null,"NaN cannot be filtered away");
            raw=Block(10,.02,.64,.36);raw[0]=9.99;
            check(Fail(()=>plan.Parse(raw,10,0,true,0))!=null,"ADC saturation cannot be filtered away");
            raw=Block(10,.02,.64,.36);raw[0]=4;
            f=plan.Parse(raw,10,0,true,0);
            check(f.ImonMedianRawV==.02 && Fail(()=>FactoryRunner.ValidateFeedback(f,s,true)).Message.Contains("限流"),"isolated current above 10 mA still stops");
            raw=Block(10,.02,.64,.36);raw[0]=6.603266581;
            f=plan.Parse(raw,10,0,true,0);
            check(Fail(()=>FactoryRunner.ValidateFeedback(f,s,true)).Message.Contains("批次"),"reported 6.603 V spike still rejected");
            raw=Block(10,.02,.64,.36);raw[2]=.51;
            f=plan.Parse(raw,10,0,true,0);
            check(Fail(()=>FactoryRunner.CheckRampVoltage(f,s,5000))!=null,"raw divider overvoltage not hidden by median");
            raw=Block(10,.02,.01,0);raw[2]=.01;
            f=plan.Parse(raw,10,0,true,0);
            check(!FactoryRunner.ResidualOk(f,s),"median zero does not clear raw residual warning");
            f=plan.Parse(Block(10,.02,.64,.36),10,0,false,0);
            check(Fail(()=>FactoryRunner.ValidateFeedback(f,s,true)).Message.Contains("急停"),"emergency remains enforced");
            f=plan.Parse(Block(10,.02,.64,.36),10,0,true,0);f.DividerMedianRawV=Double.NaN;
            check(Fail(()=>FactoryRunner.ValidateFeedback(f,s,true)).Message.Contains("窗口"),"invalid derived metadata rejected");
            var late=new Buffer();long elapsed=0;late.AfterRead=()=>elapsed=100;
            check(Fail(()=>FactoryFeedbackStream.Read(plan,late,0,()=>elapsed)).Message.Contains("100 ms"),"window read timeout remains bounded");
            // Full runner uses the window statistic for reaching target; still
            // exits through shutdown without optical on genuine protection faults.
            foreach(int sign in new[]{-1,1})
            {
                var fixture=Fixture();var p=new FactoryArtFeedbackPlan(fixture.Settings);
                fixture.Backend.FeedbackRule=(b,v)=> !b.Hv || b.Ao==0 ? v : p.Parse(WithTail(sign*.36,sign*.383),10,0,true,b.Ao);
                var result=fixture.Run();
                check(result.Completed && result.RampStatistics.ExitCode=="Reached","window in band despite tail overshoot sign="+sign);
                check(Math.Abs(result.RampStatistics.LastDividerV-sign*3960)<1e-6,"record actual control statistic");
            }
            var spikeOnly=Fixture();var spikePlan=new FactoryArtFeedbackPlan(spikeOnly.Settings);int reads=0;
            spikeOnly.Backend.FeedbackRule=(b,v)=> {
                if(!b.Hv || b.Ao==0)return v;
                reads++;return spikePlan.Parse(reads==1?WithTail(.30,.36):WithTail(.36,.36),10,0,true,b.Ao);
            };
            spikeOnly.Run();check(reads>=2,"single target spike does not prematurely complete ramp");
            var over=Fixture();var op=new FactoryArtFeedbackPlan(over.Settings);
            over.Backend.FeedbackRule=(b,v)=>{if(!b.Hv || b.Ao==0)return v;var a=WithTail(.36,.36);a[0]=4;return op.Parse(a,10,0,true,b.Ao);};
            check(Fail(()=>over.Run())!=null && over.Backend.OpticalHighCount==0,"real overcurrent still prevents optical continuation");
            var clock=new FactoryTests.FakeClock();var messages=new List<string>();
            var publisher=new FactoryFeedbackTrendPublisher(s,clock,(key,value)=>messages.Add(value));
            f=plan.Parse(WithTail(-.36,-.383),10,0,true,.6); publisher.Observe(f);
            var sample=FactoryFeedbackTrendSample.Parse(messages[0]);
            check(sample.Values.Length==13 && sample.Values[11]==10 && sample.Values[12]==0 && Math.Abs(sample.Values[3]+3.96)<1e-9 && sample.ValueSourceLabel.Contains("10点中位数"),"trend transmits median/window length, signed raw values and zero-display allowance");
            check(Math.Abs(sample.Values[9]+4.213)<1e-9,"trend retains raw extremum alongside median");
            check(FactoryFeedbackTrendSample.Parse("0;1;2;3;4;1;1;2;2;3;3").ValueSourceLabel.Contains("原始末点"),"legacy trend readable and not mislabeled as filtered");
            string imagePath=Path.Combine(Path.GetTempPath(),"Xiongxin-R19-window-"+Guid.NewGuid().ToString("N")+".png");
            using(var host=new Form { Width=1100,Height=680 })
            using(var control=new FactoryFeedbackTrendControl())
            {
                host.Controls.Add(control);host.Show();
                for(int i=0;i<30;i++){sample.Values[0]=i*100;control.Add(sample.Encode());}
                Application.DoEvents();
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(imagePath);}
                check(control.SampleCount==30,"filtered UI renders");control.ClearTrend();check(control.SampleCount==0,"new round resets trend");host.Close();
            }
            return "R19 window feedback: "+n+" offline checks PASS; HardwareAccessed=False; image="+imagePath;
        }
        private static FactoryTests.Fixture Fixture()
        { var f=new FactoryTests.Fixture();f.Settings.TargetKv=4;f.Settings.DividerOnlyFeedback=true;return f; }
        private static double[] Block(int count,double current,double vmon,double divider)
        {var a=new double[count*3];for(int i=0;i<count;i++){a[i*3]=current;a[i*3+1]=vmon;a[i*3+2]=divider;}return a;}
        private static double[] WithTail(double main,double last)
        {var a=Block(10,.02,.64,main);a[a.Length-1]=last;return a;}
        private static Exception Fail(Action action)
        {try{action();return null;}catch(Exception ex){return ex;}}
        private sealed class Buffer : IFactoryFeedbackBuffer
        {
            internal int Requested;internal double Divider;internal Action AfterRead;
            private int availabilityPhase=1;
            public uint Available(){if(availabilityPhase==2)return 40;availabilityPhase++;return 0;}
            public bool EmergencyReleased(){return true;}
            public double[] Read(int count){availabilityPhase=0;Requested=count;if(AfterRead!=null)AfterRead();return Block(count,.02,.64,Divider);}
        }
    }
}
