using System;
using System.Linq;

namespace Xiongxin.FactoryTest
{
    // Pure numerical replay and the existing fake backend only. No SDK, native
    // task, resident session, or product entry point is opened by this test.
    internal static class FactoryArtNegativeFeedbackTests
    {
        internal static string Run()
        {
            int n=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("ART signed feedback: "+why);n++;};
            // Numerical all-channel cases model the unchanged calibration/
            // legacy guard, not a pressure round's selected-source allowance.
            var s=Settings();s.PressureFeedbackContext=false;var plan=new FactoryArtFeedbackPlan(s);
            check(plan.RequiredVoltageChannel==-1,"calibration/all-channel fixture retains strict channel guards");
            check(plan.ImonRangeV==5.5 && plan.VmonRangeV==5.5 && Math.Abs(plan.DividerRangeV-3.6)<1e-12,
                "requested unipolar DC spans and bipolar divider span unchanged");
            check(plan.ImonNegativeLimitRawV==s.CurrentLimitMa*s.FeedbackFullScaleV/s.ImonFullScaleMa &&
                plan.VmonNegativeLimitRawV==s.VmonNegativeLimitV*s.FeedbackFullScaleV/(s.VmonFullScaleKv*1000),
                "low-side limits derive only from existing protection settings");
            foreach(double[] raw in Replays())
            {
                double[] original=(double[])raw.Clone();
                var f=plan.Parse(raw,10,0,true,0);
                check(f.SampleCount==10 && f.WindowSampleCount==10,"real batch has a full feedback window");
                for(int channel=0;channel<3;channel++)
                {
                    var values=Enumerable.Range(0,10).Select(i=>raw[i*3+channel]).ToArray();
                    double minimum=channel==0?f.ImonMinimumRawV:channel==1?f.VmonMinimumRawV:f.DividerMinimumRawV;
                    double maximum=channel==0?f.ImonMaximumRawV:channel==1?f.VmonMaximumRawV:f.DividerMaximumRawV;
                    double latest=channel==0?f.ImonRawV:channel==1?f.VmonRawV:f.DividerRawV;
                    check(minimum==values.Min() && maximum==values.Max() && latest==values[9],
                        "signed original extrema/latest retained channel "+channel);
                }
                check(raw.SequenceEqual(original),"raw input is not clamped, zeroed, or rewritten");
                check(f.ImonMinimumRawV<0 && f.VmonMinimumRawV<0,"negative samples survive parsing");
                FactoryRunner.ValidateFeedback(f,s,true);FactoryRunner.CheckRampVoltage(f,s,30000);
                check(true,"existing emergency/current/voltage guards accept recorded background");
                check(FactoryFeedbackBounds.VmonAbs(f)*6200<=500 && FactoryFeedbackBounds.DividerAbs(f)*12500<=500,
                    "recorded startup extrema including half-code stay within configured 500 V");
                check(!FactoryRunner.ResidualOk(f,s),"same record is not declared residual-safe at 50 V");
                var old=s.Clone();old.ZeroBackgroundLimitV=0;
                check(Failure(()=>new FactoryArtFeedbackPlan(old).Parse(raw,10,0,true,0)).Message.Contains("负向"),
                    "old records retain their configured 50 V negative limit");
                var fixture=new FactoryTests.Fixture();CopySettings(s,fixture.Settings);
                fixture.Backend.FeedbackRule=(b,x)=>b.Hv && b.Ao==0?plan.Parse(raw,10,0,x.EmergencyReleased,b.Ao):x;
                var result=fixture.Run();
                check(result.Completed && fixture.Backend.PositiveAoWrites>0 && fixture.Backend.OpticalHighCount==1,
                    "recorded zero-output batch completes existing fake startup and pressure flow");
                check(result.StartupFeedback.Any(x=>x.VmonMinimumRawV<0) && fixture.Backend.MaximumAo<=fixture.Settings.AoCommandCeilingV,
                    "startup evidence signed and AO envelope unchanged");
            }
            double half=FactoryArtFeedbackPlan.HalfCode(5.5)/2;
            foreach(int channel in new[]{0,1})
            {
                double limit=channel==0?plan.ImonNegativeLimitRawV:plan.VmonNegativeLimitRawV;
                var near=new double[3];near[channel]=-limit+half+1e-6;
                plan.Parse(near,1,0,true,0);check(true,"configured low side inside boundary "+channel);
                var outside=new double[6];outside[channel]=-limit+half-1e-6;
                check(Failure(()=>plan.Parse(outside,2,0,true,0)).Message.Contains("负向"),
                    "earlier low-side anomaly cannot be hidden by last zero sample "+channel);
                var nominal=new double[3];nominal[channel]=-limit;
                check(Failure(()=>plan.Parse(nominal,1,0,true,0)).Message.Contains("负向"),
                    "existing half-code remains conservative at configured limit "+channel);
            }
            var frozen=s.Clone();var frozenPlan=new FactoryArtFeedbackPlan(frozen);
            frozen.ZeroBackgroundLimitV=1000;frozen.CurrentLimitMa=13;
            check(Failure(()=>frozenPlan.Parse(new[]{0.0,-600.0/6200,0.0},1,0,true,0)).Message.Contains("负向") &&
                Failure(()=>frozenPlan.Parse(new[]{-11.0/2.6,0.0,0.0},1,0,true,0)).Message.Contains("负向"),
                "later setting mutation cannot widen live plan limits");
            foreach(int channel in new[]{0,1,2}) foreach(double bad in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity})
            {
                var raw=new double[3];raw[channel]=bad;
                check(Failure(()=>plan.Parse(raw,1,0,true,0)).Message.Contains("非有限"),"non-finite rejected channel "+channel);
            }
            foreach(int channel in new[]{0,1,2})
            {
                var raw=new double[3];raw[channel]=(channel==2?plan.DividerRangeV:5.5)*.995;
                check(Failure(()=>plan.Parse(raw,1,0,true,0)).Message.Contains("饱和"),"positive saturation unchanged "+channel);
                if(channel==2){raw[2]*=-1;check(Failure(()=>plan.Parse(raw,1,0,true,0)).Message.Contains("饱和"),"negative bipolar saturation unchanged");}
            }
            check(plan.Parse(new double[3],1,20,true,0).VmonRawV==0,"zero and unchanged 20-point queue boundary pass");
            check(Failure(()=>plan.Parse(new double[3],1,21,true,0))!=null,"queue beyond 20 remains rejected");
            check(Failure(()=>plan.Parse(new double[2],1,0,true,0))!=null,"incomplete scan remains rejected");
            var emergency=plan.Parse(new double[3],1,0,false,0);
            check(Failure(()=>FactoryRunner.ValidateFeedback(emergency,s,true)).Message.Contains("急停"),"parser never grants emergency permission");
            var positiveCurrent=plan.Parse(new[]{s.CurrentLimitMa/2.6,0.0,0.0},1,0,true,0);
            check(Failure(()=>FactoryRunner.ValidateFeedback(positiveCurrent,s,true)).Message.Contains("限流"),"existing positive overcurrent guard unchanged");
            var overvoltage=plan.Parse(new[]{0.0,0.0,30001.0/12500},1,0,true,0);
            check(Failure(()=>FactoryRunner.CheckRampVoltage(overvoltage,s,30000)).Message.Contains("AI4"),"independent actual maximum voltage guard unchanged");
            foreach(int channel in new[]{1,2})
            {
                // Actual pressure run chooses precisely this channel. The
                // other voltage is only diagnostic, even in HV-ON background.
                var pressure=s.Clone();pressure.PressureFeedbackContext=true;pressure.DividerOnlyFeedback=channel==2;
                var pressurePlan=new FactoryArtFeedbackPlan(pressure);
                var fixture=new FactoryTests.Fixture();CopySettings(pressure,fixture.Settings);
                fixture.Backend.FeedbackRule=(b,x)=>
                {
                    if(!b.Hv || b.Ao!=0)return x;
                    var raw=new double[30];for(int i=0;i<10;i++)raw[i*3+channel]=channel==1?501/6200.0:501/12500.0;
                    return pressurePlan.Parse(raw,10,0,x.EmergencyReleased,b.Ao);
                };
                check(Failure(()=>fixture.Run()).ToString().Contains("选中 "+(channel==1?"VMON":"DIV")+"批次绝对上界"),
                    "selected pressure startup background still checked channel "+channel);
                check(fixture.Backend.PositiveAoWrites==0 && fixture.Backend.OpticalHighCount==0 && !fixture.Backend.Hv,
                    "selected startup failure remains fail-closed channel "+channel);
                int diagnosticChannel=channel==1?2:1;
                var diagnostic=new FactoryTests.Fixture();CopySettings(pressure,diagnostic.Settings);
                diagnostic.Backend.FeedbackRule=(b,x)=>
                {
                    if(!b.Hv || b.Ao!=0)return x;
                    var raw=new double[30];for(int i=0;i<10;i++)raw[i*3+diagnosticChannel]=diagnosticChannel==1?501/6200.0:501/12500.0;
                    return pressurePlan.Parse(raw,10,0,x.EmergencyReleased,b.Ao);
                };
                var diagnosticResult=diagnostic.Run();
                check(diagnosticResult.Completed && diagnosticResult.ResidualSafe,
                    "unselected pressure startup voltage is diagnostic only channel "+diagnosticChannel);
                check(diagnosticResult.StartupFeedback.Any(x=>diagnosticChannel==1?x.VmonRawV>0:x.DividerRawV>0),
                    "unselected voltage retained in startup evidence channel "+diagnosticChannel);
            }
            foreach(double bad in new[]{0.0,Double.NaN,Double.PositiveInfinity})
            {
                var invalid=s.Clone();invalid.CurrentLimitMa=bad;
                check(Failure(()=>new FactoryArtFeedbackPlan(invalid)) is ArgumentException,"invalid current conversion rejected");
                invalid=s.Clone();invalid.VmonFullScaleKv=bad;
                check(Failure(()=>new FactoryArtFeedbackPlan(invalid)) is ArgumentException,"invalid voltage conversion rejected");
            }
            return "ART negative DC feedback: "+n+" raw replay / configured low-side protection / fake-flow checks PASS; HardwareAccessed=False; SDKCalled=False; PhysicalAcceptance=False";
        }
        private static FactorySettings Settings()
        {
            var s=new FactorySettings {TargetKv=.5,MaximumKv=30,MaximumFeedbackKv=30,
                StartupVmonLimitV=500,ZeroBackgroundLimitV=500,SafeResidualV=50,
                VmonAfterHvStartup=true,DividerOnlyFeedback=true};
            s.Input.DividerVPerV=12500;return s;
        }
        private static void CopySettings(FactorySettings source,FactorySettings target)
        {
            target.MaximumKv=source.MaximumKv;target.MaximumFeedbackKv=source.MaximumFeedbackKv;
            target.StartupVmonLimitV=source.StartupVmonLimitV;target.ZeroBackgroundLimitV=source.ZeroBackgroundLimitV;
            target.SafeResidualV=source.SafeResidualV;target.VmonAfterHvStartup=true;target.DividerOnlyFeedback=source.DividerOnlyFeedback;
            target.Input.DividerVPerV=source.Input.DividerVPerV;target.DischargeTimeoutSeconds=.12;
        }
        private static Exception Failure(Action action)
        {try{action();}catch(Exception e){return e;}throw new Exception("Expected ART signed-feedback guard failure");}
        private static double[][] Replays()
        {
            // ai-monitor-raw.csv, project 2026-10-07_13-23-28, pressure runs
            // 20261009-045811-793-9f551523068142ea9905089b2e93f67f and
            // 20261009-045923-917-5d32c2a98e8246baa6b9564f32b6fd82.
            // Exact first/only recorded 10 interleaved AI0/AI2/AI4 scans.
            return new[]{new[]{
                -.009877451536573538,-.019740455313707914,-.014796364857942196,
                -.00494594964800635,-.019740455313707914,-.014796364857942196,
                .0049170541291280265,-.019740455313707914,-.014796364857942196,
                .0098485560176952147,-.019740455313707914,-.014796364857942196,
                .0049170541291280265,-.014808953425140726,.0049273062699176154,
                .024643061683396777,-.019740455313707914,.019720059615812472,
                .019711559794829591,.0098485560176952147,.019720059615812472,
                .0049170541291280265,.0098485560176952147,.019720059615812472,
                .014780057906262403,.014780057906262403,.022185518506794951,
                .0049170541291280265,.0098485560176952147,.022185518506794951
            },new[]{
                .034506065460531157,.0049170541291280265,.019720059615812472,
                .014780057906262403,.0049170541291280265,.022185518506794951,
                .014780057906262403,-.014808953425140726,.017254600724829997,
                .014780057906262403,-.024671957202275104,.014789141833847521,
                .0098485560176952147,-.024671957202275104,-.0073999881849947666,
                .0049170541291280265,-.014808953425140726,-.017261823748924671,
                -1.4447759439161684E-05,-.019740455313707914,-.01233090596695972,
                .0098485560176952147,-.019740455313707914,-.014796364857942196,
                .014780057906262403,-.019740455313707914,-.017261823748924671,
                .0049170541291280265,-.014808953425140726,-.014796364857942196
            }};
        }
    }
}
