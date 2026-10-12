using System;
using System.Collections.Generic;

namespace HVX.Acquisition
{
    // This exact pure source is compiled by both the UI and resident runtime.
    // It selects one adjacent index only; it grants no acquisition/HV permit
    // and does not replace each caller's clipping or calibrated-range checks.
    internal static class AdjacentHardwareRangePolicy
    {
        internal static int Next(int current, int recommended, int count)
        {
            if (count <= 0 || current < 0 || current >= count || recommended < 0 || recommended >= count)
                throw new ArgumentOutOfRangeException("recommended", "Invalid hardware range index.");
            return current + Math.Sign(recommended - current);
        }
    }

    // A bounded execution wrapper around the frozen amplitude advisor. Clip
    // counts describe lost samples, not the hidden true peak. These thresholds
    // only distinguish isolated clipping from sustained/distributed clipping;
    // neither band certifies that the next capture will fit its input range.
    internal static class BoundedClippedRangePolicy
    {
        internal const int MaximumIncrease=2;
        internal const int SustainedRunSamples=3;
        internal const int DistributedClipFractionDenominator=1000; // 0.1%.

        internal static int Next(int current,int recommended,int count,bool localizedWindow,
            int sampleCount,int clippedSamples,int longestClippedRun)
        {
            int adjacent=AdjacentHardwareRangePolicy.Next(current,recommended,count);
            if(localizedWindow&&clippedSamples>0&&recommended<=current)return current;
            if(recommended<=current || !localizedWindow || sampleCount<=0 || clippedSamples<=0 ||
                clippedSamples>sampleCount || longestClippedRun<=0 || longestClippedRun>clippedSamples)
                return adjacent;
            bool sustained=longestClippedRun>=SustainedRunSamples ||
                clippedSamples>=SustainedRunSamples && (long)clippedSamples*DistributedClipFractionDenominator>=sampleCount;
            return sustained && recommended>=current+MaximumIncrease && current+MaximumIncrease<count
                ? current+MaximumIncrease : adjacent;
        }

        internal static int NextCalibrated(double[] calibratedPc,int current,int recommended,ISet<int> visited,
            int retries,bool localizedWindow,int sampleCount,int clippedSamples,int longestClippedRun)
        {
            if(calibratedPc==null || current<0 || current>=calibratedPc.Length || visited==null || retries<0 ||
                retries>=calibratedPc.Length-1 || !Available(calibratedPc[current]))return -1;
            int adjacent=current+1;
            if(adjacent>=calibratedPc.Length || !Available(calibratedPc[adjacent]) ||
                calibratedPc[adjacent]<=calibratedPc[current] || visited.Contains(adjacent))return -1;
            // An absent/invalid optimum cannot manufacture a two-step request.
            int goal=recommended>current && recommended<calibratedPc.Length ? recommended : adjacent;
            int next=Next(current,goal,calibratedPc.Length,localizedWindow,sampleCount,clippedSamples,longestClippedRun);
            if(next==current+MaximumIncrease && Available(calibratedPc[next]) &&
                calibratedPc[next]>calibratedPc[adjacent] && !visited.Contains(next))return next;
            return adjacent;
        }

        static bool Available(double pc){return !Double.IsNaN(pc)&&!Double.IsInfinity(pc)&&pc>=100;}
    }
}
