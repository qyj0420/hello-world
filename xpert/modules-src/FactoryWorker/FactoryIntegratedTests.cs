using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryIntegratedTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Integrated R21: "+why);n++;};
            var fixture=new FactoryTests.Fixture(); fixture.Settings.InitialResidualV=300; fixture.Settings.DividerOnlyFeedback=true;
            var round=fixture.Run(); round.Completed=true;
            var quality=FactoryRangeReview.Evaluate(round);
            quality.WindowValid=true; quality.Change=true; quality.RecommendedIndex=6;
            int current=round.Capture.Settings.RangeIndex;
            check(FactoryNextRangeReview.CanApply(round,quality,current),"valid completed round suggests next only");
            int hvCount=fixture.Backend.HvHighCount,lightCount=fixture.Backend.OpticalHighCount;
            var decision=FactoryNextRangeReview.Decide(round,quality,current,true);
            check(decision.ApplyNextRound && !decision.AutomaticRetryRequested,"never requests retry");
            check(round.Capture.Settings.RangeIndex==current && round.Settings.Input.RangeIndex==current,"source settings immutable");
            check(fixture.Backend.HvHighCount==hvCount && fixture.Backend.OpticalHighCount==lightCount,"decision emits no hardware calls");
            check(!FactoryNextRangeReview.Decide(round,quality,current,false).ApplyNextRound,"dismiss retains range");
            quality.Reason="changed after decision";
            check(decision.OriginalQuality.Reason!=quality.Reason,"quality evidence frozen");
            round.Completed=false;
            check(!FactoryNextRangeReview.CanApply(round,quality,current),"incomplete cannot apply");
            round.Completed=true;round.ResidualSafe=false;
            check(!FactoryNextRangeReview.CanApply(round,quality,current),"failed discharge cannot apply");
            round.ResidualSafe=true;quality.WindowValid=false;
            check(!FactoryNextRangeReview.CanApply(round,quality,current),"invalid window no recommendation");
            quality.WindowValid=true;quality.RecommendedIndex=-1;
            check(!FactoryNextRangeReview.CanApply(round,quality,current),"invalid index no recommendation");
            quality.RecommendedIndex=6;
            check(!FactoryNextRangeReview.CanApply(round,quality,current+1),"edited UI range rejects stale suggestion");
            string directory=Path.Combine(Path.GetTempPath(),"Factory-R21-next-range-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);round.DirectoryPath=directory;
            FactoryNextRangeReview.Save(round,decision); FactoryNextRangeReview.Save(round,decision);
            check(Directory.GetFiles(directory,"next-range-decision-*.xml").Length==2,"repeated decisions append instead of overwriting");
            using(var stream=File.OpenRead(Directory.GetFiles(directory)[0]))
            { var saved=(FactoryNextRangeDecision)new XmlSerializer(typeof(FactoryNextRangeDecision)).Deserialize(stream);
              check(saved.ApplyNextRound && !saved.AutomaticRetryRequested,"serialized decision preserves no-retry semantics"); }

            fixture=new FactoryTests.Fixture();fixture.Settings.InitialResidualV=300;fixture.Settings.DischargeTimeoutSeconds=.1;
            fixture.Backend.FeedbackRule=(b,f)=>{if(b.PositiveAoWrites>0&&!b.Hv)f.DividerRawV=200/fixture.Settings.Input.DividerVPerV;return f;};
            bool failed=false;try{fixture.Run();}catch(FactoryRunException ex){failed=true;check(!ex.PartialResult.ResidualSafe,"200 V cannot pass final 50 V");}
            check(failed && fixture.Backend.OpticalHighCount==1,"final residual failure has no second light");
            fixture=new FactoryTests.Fixture();fixture.Settings.InitialResidualV=300;
            fixture.Backend.FeedbackRule=(b,f)=>{if(b.Hv&&b.PositiveAoWrites==0)f.DividerRawV=200/fixture.Settings.Input.DividerVPerV;return f;};
            failed=false;try{fixture.Run();}catch(FactoryRunException){failed=true;}
            check(failed && fixture.Backend.PositiveAoWrites==0,"HV ON zero-output check remains 50 V");
            fixture=new FactoryTests.Fixture();fixture.Settings.InitialResidualV=300;
            fixture.Backend.FeedbackRule=(b,f)=>{f.DividerRawV=200/fixture.Settings.Input.DividerVPerV;return f;};
            failed=false;try{fixture.Run(null,FactoryRunKind.Calibration);}catch(FactoryRunException){failed=true;}
            check(failed && fixture.Backend.ArmCount==0,"calibration does not inherit pressure 300 V");
            return "Integrated R21 threshold/range: "+n+" offline checks PASS; HardwareAccessed=False";
        }
    }
}
