using System;
using System.Collections.Generic;

namespace HVX.Task17.Location
{

// First-arrival timing. The chosen physical packet/peak identity is immutable.
// Separate main-packet isolation from low-level onset measurement: a weak
// bipolar precursor must not bridge the low threshold into the main packet.
internal static class PeakAnchoredLeadingEdgeMath
{
    // Shared by calibration and location. Caller supplies baseline-corrected
    // original samples and the fixed selected packet/peak, never filtered display.
    internal static int FindPacket(int sampleCount,int start,int end,int peak,int roundTrip,double sigma,Func<int,double> signed)
    {
        if(signed==null||sampleCount<1||!Finite(sigma)||sigma<0)return -1;
        start=Math.Max(0,start);end=Math.Min(sampleCount-1,end);
        if(start>end||peak<start||peak>end)return -1;
        for(int i=start;i<=end;i++)if(!Finite(signed(i)))return -1;
        sigma=Math.Max(1e-15,sigma);double amplitude=Math.Abs(signed(peak));
        if(amplitude<3*sigma)return -1;
        int scaleEnd=(int)Math.Min(end,(long)peak+Math.Max(12,roundTrip/8));
        int period=Period(peak,scaleEnd,Math.Max(2*sigma,.05*amplitude),signed);
        int hold=Math.Max(6,Math.Min(12,(period+3)/4));
        return Find(Math.Max(0,start-2*hold),peak,hold,Math.Max(3*sigma,.02*amplitude),Math.Max(6*sigma,.05*amplitude),signed,3*sigma);
    }
    static int Period(int begin,int end,double level,Func<int,double> signed)
    {
        int main=begin,opposite=-1;double maximum=Math.Abs(signed(begin)),oppositeAmplitude=0;
        for(int i=begin+1;i<=end;i++){double value=Math.Abs(signed(i));if(value>maximum){main=i;maximum=value;}}
        double mainValue=signed(main);
        for(int i=main+1;i<=end;i++){double value=signed(i);if(value*mainValue<0&&Math.Abs(value)>oppositeAmplitude){opposite=i;oppositeAmplitude=Math.Abs(value);}}
        if(opposite>=0&&oppositeAmplitude>=Math.Max(level,Math.Abs(mainValue)*.10))return Math.Max(4,Math.Min(80,2*(opposite-main)));
        var crossings=new List<int>();int previous=0;
        for(int i=begin;i<=end;i++){double value=signed(i);if(Math.Abs(value)<level)continue;int sign=value>=0?1:-1;if(previous!=0&&sign!=previous)crossings.Add(i);previous=sign;}
        if(crossings.Count<2)return 8;
        var gaps=new List<double>();for(int i=1;i<crossings.Count;i++)gaps.Add(crossings[i]-crossings[i-1]);gaps.Sort();
        double median=gaps.Count%2==0?(gaps[gaps.Count/2-1]+gaps[gaps.Count/2])/2:gaps[gaps.Count/2];
        return Math.Max(4,Math.Min(80,(int)Math.Round(2*median)));
    }
    internal static int Find(int guardStart,int peak,int hold,double low,double high,Func<int,double> signed)
    { return Find(guardStart,peak,hold,low,high,signed,low); }
    // Research r01: a significant reversal while walking backwards is a
    // precursor/valley, not a continuous rising foot. Noise tolerance comes
    // from the caller's robust sigma, not the selected expected position.
    internal static int Find(int guardStart,int peak,int hold,double low,double high,Func<int,double> signed,double reversalTolerance)
    {
        if(signed==null||guardStart<0||peak<=guardStart||hold<1||!Finite(low)||!Finite(high)||!Finite(reversalTolerance)||reversalTolerance<0||low<=0||high<low)return -1;
        // Validate the entire bounded support before any early return.
        for(int k=guardStart;k<=peak;k++)if(!Finite(signed(k)))return -1;
        double peakValue=signed(peak);
        if(!Finite(peakValue)||Math.Abs(peakValue)<high)return -1;
        int quiet=0;
        for(int i=peak-1;i>=guardStart;i--)
        {
            double value=signed(i);if(!Finite(value))return -1;
            if(Math.Abs(value)<high)quiet++;else quiet=0;
            if(quiet<hold)continue;
            // The first significant lobe after the nearest high-threshold quiet
            // guard can have the opposite sign to the selected absolute peak.
            // This preserves a genuine bipolar packet; lobe count is not events.
            int entry=i+hold;
            if(entry>peak)return -1;
            double entryValue=signed(entry);
            if(!Finite(entryValue)||Math.Abs(entryValue)<high)return -1;
            int sign=Math.Sign(entryValue),onset=entry;
            double valley=Math.Abs(entryValue);int valleyIndex=entry;
            // Follow this connected lobe to its original LOW threshold. This
            // can pass back through the high guard during a slow monotonic rise,
            // so raising the isolation threshold does not truncate a real foot.
            while(onset>guardStart)
            {
                double before=signed(onset-1);if(!Finite(before))return -1;
                if(Math.Sign(before)!=sign||Math.Abs(before)<low)return onset;
                double magnitude=Math.Abs(before);
                if(magnitude>valley+reversalTolerance)
                {
                    // A bridged positive floor cannot establish the true zero
                    // onset. Re-arm only on the main-side rise above that floor.
                    // Same rule is used for CAL and LOC; no peak is relocated.
                    for(int k=valleyIndex+1;k<=peak;k++)
                        if(signed(k)*sign>=valley+low)return k;
                    return -1;
                }
                if(magnitude<valley){valley=magnitude;valleyIndex=onset-1;}
                onset--;
            }
            // Truncated support never establishes an onset at the crop edge.
            return -1;
        }
        return -1;
    }
    private static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
}
}
