using System;
using System.Drawing;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryFeedbackOverlayTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,t)=>{if(!ok)throw new Exception("R28 overlay: "+t);++n;};
            using(var combined=new FactoryFeedbackTrendControl { OverlayChannels=true,Size=new Size(380,220) })
            using(var detail=new FactoryFeedbackTrendControl())
            {
                check(combined.OverlayVoltageBound==5 && combined.OverlayCurrentBound==.1,"empty axes retain minimum span");
                var sample=new FactoryFeedbackTrendSample { Values=new[]{100.0,-.03,.01,-.025,0,-.2,.2,-.03,.02,-.2,.3,10.0,.5} };
                combined.Add(sample.Encode());detail.Add(sample.Encode());
                check(combined.OverlayVoltageBound==5,"background voltage never exaggerated");
                check(combined.LastEncoded==detail.LastEncoded,"overlay preserves signed values and raw extrema");
                sample.Values=new[]{200.0,10.0,2.0,-12.0,1.6,9.0,11.0,1.0,3.0,-13.0,-11.0,10.0,.5};
                combined.Add(sample.Encode());detail.Add(sample.Encode());
                check(Math.Abs(combined.OverlayVoltageBound-14.04)<1e-10,"voltage bound includes both raw voltage channels");
                check(Math.Abs(combined.OverlayCurrentBound-3.24)<1e-10,"current uses independent mA axis");
                check(combined.LastEncoded==detail.LastEncoded && combined.SampleCount==detail.SampleCount,"identical data feed in both views");
                var original=combined.LastEncoded;double originalVoltageBound=combined.OverlayVoltageBound,originalCurrentBound=combined.OverlayCurrentBound;
                combined.ShowIntervalExtrema=false;
                using(var hidden=new Bitmap(380,220))combined.DrawToBitmap(hidden,new Rectangle(0,0,380,220));
                check(combined.LastEncoded==original && combined.OverlayVoltageBound==originalVoltageBound && combined.OverlayCurrentBound==originalCurrentBound,"hiding interval strokes preserves samples and complete raw bounds");
                combined.ShowIntervalExtrema=true;
                using(var bitmap=new Bitmap(380,220))combined.DrawToBitmap(bitmap,new Rectangle(0,0,380,220));
                check(sample.Values[3]==-12 && sample.Values[9]==-13,"painting never rectifies or mutates source");
                combined.Add("not valid");check(combined.SampleCount==2,"invalid packets rejected");
                combined.ClearTrend();check(combined.SampleCount==0 && combined.LastEncoded=="" && combined.OverlayVoltageBound==5,"round reset cannot leave stale overlay");
            }
            return "R28 combined voltage/current: "+n+" checks PASS; display only; HardwareAccessed=False";
        }
    }
}
