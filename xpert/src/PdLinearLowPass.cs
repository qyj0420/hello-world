using System;

namespace HVX.SignalProcessing
{
    // Symmetric windowed-sinc FIR. Output is centred on the original sample time;
    // no causal delay is introduced into cursor timing. Calibration and consumers
    // must use the same taps and cutoff stored in the calibration record.
    public static class PdLinearLowPass
    {
        public const string FilterId="PD-LP-HAMMING129-v1";
        public static double[] Apply(double[] samples,double sampleIntervalSeconds,double cutoffMHz,int tapCount=129)
        {
            if(samples==null)throw new ArgumentNullException("samples");
            if(!Finite(sampleIntervalSeconds)||sampleIntervalSeconds<=0||!Finite(cutoffMHz)||cutoffMHz<1||cutoffMHz>60||tapCount<3||(tapCount&1)==0)throw new ArgumentOutOfRangeException("filter");
            foreach(double v in samples)if(!Finite(v))throw new ArgumentException("滤波样本含无效数值。");
            double fc=cutoffMHz*1e6*sampleIntervalSeconds;
            if(fc>=.5*(1-1e-12))return (double[])samples.Clone();
            if(samples.Length<3)throw new ArgumentException("滤波样本不足。");
            int taps=Math.Min(tapCount,(samples.Length&1)==1?samples.Length:samples.Length-1),center=taps/2;
            var kernel=new double[taps];double sum=0;
            for(int i=0;i<taps;i++){int m=i-center;kernel[i]=(m==0?2*fc:Math.Sin(2*Math.PI*fc*m)/(Math.PI*m))*(.54-.46*Math.Cos(2*Math.PI*i/(taps-1)));sum+=kernel[i];}
            for(int i=0;i<taps;i++)kernel[i]/=sum;
            var output=new double[samples.Length];
            for(int i=0;i<output.Length;i++){double value=0;for(int k=0;k<taps;k++){int at=i+k-center;while(at<0||at>=samples.Length){if(at<0)at=-at;if(at>=samples.Length)at=2*samples.Length-2-at;}value+=kernel[k]*samples[at];}output[i]=value;}
            return output;
        }
        private static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
    }
    public sealed class ReflectionBandwidthResult
    {
        public double CutoffMHz,Ratio;
        public bool Sufficient;
        public double[] Waveform;
        // Only the calibration display search sets these fields. Historical
        // records and the existing analysis search have no stability evidence.
        public bool? Stable;
        public double TargetRatio;
        public string StabilityReason;
        // A manual display fallback is not a successful target/stability test.
        // It must not replace the accepted raw packet anchors or metrology.
        public bool DisplayFallback;
    }
    public static class ReflectionBandwidth
    {
        public static ReflectionBandwidthResult Select(double[] samples,double sampleRate,int incident,int reflection)
        {
            if(samples==null||incident<64||reflection<=incident+16||reflection>=samples.Length-64||sampleRate<2e6||Double.IsNaN(sampleRate)||Double.IsInfinity(sampleRate))throw new ArgumentException("反射带宽评估窗口或采样率无效。");
            int radius=Math.Min(64,(reflection-incident)/4);
            var result=new ReflectionBandwidthResult{Waveform=(double[])samples.Clone()};
            result.Ratio=Ratio(samples,incident,reflection,radius);result.Sufficient=result.Ratio>=.1;
            if(result.Sufficient)return result;
            // Highest passing cutoff retains the most resolvable information.
            double start=Math.Min(60,Math.Floor(sampleRate*.5/1e6));
            for(double cutoff=start;cutoff>=1;cutoff-=1) {
                result.Waveform=PdLinearLowPass.Apply(samples,1/sampleRate,cutoff);result.CutoffMHz=cutoff;
                result.Ratio=Ratio(result.Waveform,incident,reflection,radius);result.Sufficient=result.Ratio>=.1;
                if(result.Sufficient)break;
            }
            if(!result.Sufficient) {
                result.CutoffMHz=sampleRate>14e6?7:0;
                result.Waveform=result.CutoffMHz>0?PdLinearLowPass.Apply(samples,1/sampleRate,7):(double[])samples.Clone();
                result.Ratio=Ratio(result.Waveform,incident,reflection,radius);
            }
            return result;
        }
        public static double Ratio(double[] samples,int incident,int reflection,int radius)
        {
            if(samples==null||radius<1||incident-radius<0||reflection-radius<0||incident+radius>=samples.Length||reflection+radius>=samples.Length)return 0;
            double baseline=0;int n=Math.Min(200,incident-radius);if(n<8)return 0;
            for(int i=0;i<n;i++)baseline+=samples[i]/n;
            double a=0,b=0;for(int i=-radius;i<=radius;i++){a=Math.Max(a,Math.Abs(samples[incident+i]-baseline));b=Math.Max(b,Math.Abs(samples[reflection+i]-baseline));}
            return a>1e-15?b/a:0;
        }
    }

    // Calibration-only display policy. This does not alter ReflectionBandwidth
    // or the FIR/FilterId used by analysis and saved charge coefficients.
    public static class CalibrationDisplayBandwidth
    {
        public const double TargetRatio = .5;
        public const string EngineeringPolicyId = "CAL-DISPLAY-PACKET-STABILITY-v1";

        public static ReflectionBandwidthResult Assess(double[] original,double[] display,
            int originalIncident,int originalReflection,int displayIncident,int displayReflection,double cutoffMHz)
        {
            var result=new ReflectionBandwidthResult{Waveform=display,CutoffMHz=cutoffMHz,TargetRatio=TargetRatio,Stable=false,StabilityReason="UnverifiedPacket"};
            if(original==null||display==null||original.Length!=display.Length)return result;
            if(originalIncident<0||originalIncident>=original.Length||originalReflection<0||originalReflection>=original.Length||
                displayIncident<0||displayIncident>=display.Length||displayReflection<0||displayReflection>=display.Length)return result;
            int gap=originalReflection-originalIncident;
            int radius=Math.Min(64,gap/4);
            if(gap<=16||radius<4||displayReflection<=displayIncident+16||originalIncident-radius<8||originalReflection+radius>=original.Length||
                displayIncident-radius<8||displayReflection+radius>=display.Length)return result;
            foreach(double value in original)if(!Finite(value))return result;
            foreach(double value in display)if(!Finite(value))return result;
            double baseline,noise,displayBaseline,displayNoise;
            Statistics(original,originalIncident-radius,out baseline,out noise);
            Statistics(display,displayIncident-radius,out displayBaseline,out displayNoise);
            result.Ratio=ReflectionBandwidth.Ratio(display,displayIncident,displayReflection,radius);
            // Thresholds are a conservative engineering display policy, not a
            // physical acceptance test. Check actual packets, not axis/gain.
            int drift=Math.Max(2,Math.Min(3,radius/20));
            string reason;
            if(!PacketStable(original,display,originalIncident,displayIncident,radius,drift,baseline,displayBaseline,noise,displayNoise,out reason)||
                !PacketStable(original,display,originalReflection,displayReflection,radius,drift,baseline,displayBaseline,noise,displayNoise,out reason))
            {result.StabilityReason=reason;return result;}
            if(Math.Abs(Correlation(original,original,originalIncident,originalReflection,radius,baseline,baseline))<.6)
            {result.StabilityReason="RawPairShapeUnverified";return result;}
            result.Stable=true;result.StabilityReason="PacketPolarityPeakLeadingEdgeShapePreserved";
            result.Sufficient=Finite(result.Ratio)&&result.Ratio>=TargetRatio;
            return result;
        }

        private static bool PacketStable(double[] original,double[] display,int rawAt,int displayAt,int radius,int drift,
            double baseline,double displayBaseline,double noise,double displayNoise,out string reason)
        {
            reason="PacketShapeChanged";
            int a=Peak(original,rawAt,radius,baseline),b=Peak(display,displayAt,radius,displayBaseline);
            double raw=original[a]-baseline,shown=display[b]-displayBaseline;
            if(Math.Abs(a-rawAt)>drift||Math.Abs(b-displayAt)>drift||Math.Abs(b-a)>drift){reason="PeakMovedOrPacketChanged";return false;}
            if(Math.Sign(raw)!=Math.Sign(shown)){reason="PolarityChanged";return false;}
            if(Math.Abs(raw)<=Math.Max(1e-12,noise*4)||Math.Abs(shown)<=Math.Max(1e-12,displayNoise*4))
            {reason="PacketNoiseUnverified";return false;}
            if(FlatPeak(original,a,baseline)||FlatPeak(display,b,displayBaseline))
            {reason="FlatPeakOrClippingSuspected";return false;}
            int rawWidth=Width(original,a,rawAt,radius,baseline,Math.Abs(raw)*.5);
            int displayWidth=Width(display,b,displayAt,radius,displayBaseline,Math.Abs(shown)*.5);
            if(rawWidth<3||displayWidth<3){reason="UnresolvedSpike";return false;}
            if(displayWidth>rawWidth*1.35+1||displayWidth<rawWidth*.75-1)
            {reason="PacketWidthChanged";return false;}
            int rawEdge=LeadingEdge(original,a,rawAt-radius,baseline,Math.Abs(raw)*.2);
            int displayEdge=LeadingEdge(display,b,displayAt-radius,displayBaseline,Math.Abs(shown)*.2);
            if(Math.Abs(displayEdge-rawEdge)>drift){reason="LeadingEdgeMoved";return false;}
            if(Math.Abs(Correlation(original,display,rawAt,rawAt,radius,baseline,displayBaseline))<.94)
            {reason="PacketShapeChanged";return false;}
            reason="PacketStable";return true;
        }
        private static int Peak(double[] values,int at,int radius,double baseline)
        {int best=at;for(int i=at-radius;i<=at+radius;i++)if(Math.Abs(values[i]-baseline)>Math.Abs(values[best]-baseline))best=i;return best;}
        private static bool FlatPeak(double[] values,int at,double baseline)
        {if(at<1||at>=values.Length-1)return true;double tolerance=Math.Abs(values[at]-baseline)*1e-9;int first=at,last=at;while(first>0&&Math.Abs(values[first-1]-values[at])<=tolerance)first--;while(last+1<values.Length&&Math.Abs(values[last+1]-values[at])<=tolerance)last++;return last-first+1>=3;}
        private static int Width(double[] values,int peak,int center,int radius,double baseline,double threshold)
        {int left=peak,right=peak;while(left>center-radius&&Math.Abs(values[left-1]-baseline)>=threshold)left--;while(right<center+radius&&Math.Abs(values[right+1]-baseline)>=threshold)right++;return right-left+1;}
        private static int LeadingEdge(double[] values,int peak,int first,double baseline,double threshold)
        {int edge=peak;while(edge>first&&Math.Abs(values[edge-1]-baseline)>=threshold)edge--;return edge;}
        private static double Correlation(double[] a,double[] b,int x,int y,int radius,double ab,double bb)
        {double xy=0,xx=0,yy=0;for(int j=-radius;j<=radius;j++){double av=a[x+j]-ab,bv=b[y+j]-bb;xy+=av*bv;xx+=av*av;yy+=bv*bv;}return xx>0&&yy>0?xy/Math.Sqrt(xx*yy):0;}
        private static void Statistics(double[] values,int before,out double baseline,out double noise)
        {int n=Math.Min(200,before);baseline=0;noise=0;for(int i=0;i<n;i++)baseline+=values[i]/n;for(int i=0;i<n;i++)noise+=(values[i]-baseline)*(values[i]-baseline)/n;noise=Math.Sqrt(noise);}
        private static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
