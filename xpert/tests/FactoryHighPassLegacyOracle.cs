// Test-only exact frozen Factory FIR oracle; never compiled into the worker closure.
// Frozen SHA256: 28780887CE26F71C84115A0E32E06DB9AFB9F85DD861E21E6C5F5B6FC6823A22
using System;

namespace HVX.SignalProcessing
{
    // Symmetric finite-impulse-response filter: delta minus four cascaded
    // centred box filters. The symmetric kernel has zero phase after delay
    // compensation; its -3 dB high-pass corner is calibrated to 10 kHz.
    // Always process the full acquisition BEFORE cutting pulse windows.
    internal static class FactoryHighPassLegacyOracle
    {
        public const string FilterId = "PD-HP10K-SYMMETRIC-FIR4-v1";
        public const double CutoffHz = 10000;

        public static int WindowLength(double sampleRateHz)
        {
            if (Double.IsNaN(sampleRateHz) || Double.IsInfinity(sampleRateHz) || sampleRateHz < 100000 || sampleRateHz > 1e9)
                throw new ArgumentOutOfRangeException("sampleRateHz", "10 kHz 高通需要 100 kS/s 至 1 GS/s 的实际采样率。");
            // Solve sinc(x)^4 = 1 - 1/sqrt(2); nearest odd window preserves symmetry.
            double low=0, high=Math.PI;
            for(int i=0;i<60;i++) {
                double x=(low+high)/2, response=Math.Pow(Math.Sin(x)/x,4);
                if(response > 1-1/Math.Sqrt(2)) low=x; else high=x;
            }
            return Math.Max(3,2*(int)Math.Ceiling(((low+high)/2*sampleRateHz/(Math.PI*CutoffHz)-1)/2)+1);
        }

        public static int GuardSamples(double sampleRateHz) { return 2*(WindowLength(sampleRateHz)-1); }

        public static double[] Apply(double[] raw, double sampleRateHz)
        {
            if(raw==null) throw new ArgumentNullException("raw");
            int window=WindowLength(sampleRateHz)-2, radius=window/2;
            double omega=2*Math.PI*CutoffHz/sampleRateHz, targetResponse=Math.Pow(1-1/Math.Sqrt(2),.25);
            // Fractional endpoint weights remove the integer-window corner error
            // while preserving a symmetric, unit-DC-gain FIR kernel.
            double sumResponse=Math.Sin(window*omega/2)/Math.Sin(omega/2);
            double edgeWeight=(sumResponse-targetResponse*window)/(2*(targetResponse-Math.Cos((radius+1)*omega)));
            edgeWeight=Math.Max(0,Math.Min(1,edgeWeight));double normalization=window+2*edgeWeight;
            var source=(double[])raw.Clone(); var target=new double[raw.Length];
            for(int i=0;i<raw.Length;i++) if(Double.IsNaN(raw[i])||Double.IsInfinity(raw[i])) throw new ArgumentException("波形含非有限数值。");
            if(raw.Length==0) return target;
            for(int pass=0;pass<4;pass++) {
                double sum=0;
                for(int j=-radius;j<=radius;j++) sum+=source[Reflect(j,source.Length)];
                for(int i=0;i<source.Length;i++) {
                    target[i]=(sum+edgeWeight*(source[Reflect(i-radius-1,source.Length)]+source[Reflect(i+radius+1,source.Length)]))/normalization;
                    sum+=source[Reflect(i+radius+1,source.Length)]-source[Reflect(i-radius,source.Length)];
                }
                var swap=source; source=target; target=swap;
            }
            for(int i=0;i<raw.Length;i++) target[i]=raw[i]-source[i];
            return target;
        }

        private static int Reflect(int index,int count)
        {
            if(count==1)return 0;
            int period=2*(count-1); index%=period; if(index<0)index+=period;
            return index<count?index:period-index;
        }
    }
}

