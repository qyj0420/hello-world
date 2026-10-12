using System;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryPulseEnvelopeWindow
    {
        internal const string Protocol = "100101-noise-envelope-3pct-16quiet-v1";
        internal int Start, End, Peak;
        // Reference: HVXUnifiedDesktop/CalibrationProgramControl.Noise.cs,
        // SelectNoiseEnvelope. Preserve the 3%, 16-point and 32760/side semantics.
        // Unlike the UI reference, reject an incomplete envelope instead of
        // treating an array/selection edge or search cap as a quiet boundary.
        internal static FactoryPulseEnvelopeWindow Select(double[] signal, double baseline,
            int first, int endExclusive, int roiFirst, int roiEndExclusive)
        {
            if(signal==null || !FactorySettings.Finite(baseline) || first<0 || endExclusive>signal.Length ||
                roiFirst<first || roiEndExclusive>endExclusive || roiEndExclusive<=roiFirst)
                throw new ArgumentException("脉冲开窗边界无效。");
            int peak=roiFirst;
            for(int i=roiFirst;i<roiEndExclusive;i++)
            {
                if(!FactorySettings.Finite(signal[i]))throw new ArgumentException("脉冲样点非有限。");
                if(Math.Abs(signal[i]-baseline)>Math.Abs(signal[peak]-baseline))peak=i;
            }
            double amplitude=Math.Abs(signal[peak]-baseline);
            if(!(amplitude>1e-12))return null;
            double gate=Math.Max(1e-12,amplitude*.03);
            int left=peak,right=peak,quiet=0;
            while(left>first && peak-left<32760)
            { left--; if(!FactorySettings.Finite(signal[left]))return null; quiet=Math.Abs(signal[left]-baseline)<gate?quiet+1:0;if(quiet>=16)break; }
            if(quiet<16)return null;
            quiet=0;
            while(right<endExclusive-1 && right-peak<32760)
            { right++;if(!FactorySettings.Finite(signal[right]))return null;quiet=Math.Abs(signal[right]-baseline)<gate?quiet+1:0;if(quiet>=16)break; }
            if(quiet<16)return null;
            return new FactoryPulseEnvelopeWindow {Start=left,End=right,Peak=peak};
        }
    }
}
