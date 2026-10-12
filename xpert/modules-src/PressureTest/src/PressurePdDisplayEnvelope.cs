using System;
using System.Threading;

namespace HVX.Controls
{
    // Only accelerates the existing horizontal-bucket drawing rule. Every
    // native display sample contributes; no resampling/filtering/quality data
    // is produced. The owning view keeps this with the immutable sample copy.
    internal sealed class PressurePdDisplayEnvelope
    {
        const int BlockSize = 256;
        readonly double[] samples;
        readonly double baseline;
        readonly int[] maxima;
        internal PressurePdDisplayEnvelope(double[] samples, double baseline, CancellationToken cancel)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            cancel.ThrowIfCancellationRequested();
            this.samples = samples; this.baseline = baseline;
            maxima = new int[samples.Length / BlockSize + (samples.Length % BlockSize == 0 ? 0 : 1)];
            for (int block = 0; block < maxima.Length; block++)
            {
                if ((block & 63) == 0) cancel.ThrowIfCancellationRequested();
                int first = block * BlockSize, last = first + Math.Min(BlockSize, samples.Length - first) - 1;
                int best = first;
                for (int i = first + 1; i <= last; i++)
                    if (Magnitude(i) > Magnitude(best)) best = i;
                maxima[block] = best;
            }
            cancel.ThrowIfCancellationRequested();
        }
        double Magnitude(int index) { return Math.Abs(samples[index] - baseline); }
        internal int MaximumIndex(int first, int last)
        {
            if (first < 0 || last < first || last >= samples.Length) throw new ArgumentOutOfRangeException("first");
            int best = first, at = first;
            while (at <= last && at % BlockSize != 0)
            { if (Magnitude(at) > Magnitude(best)) best = at; at++; }
            while (at <= last && last - at + 1 >= BlockSize)
            {
                int candidate = maxima[at / BlockSize];
                if (Magnitude(candidate) > Magnitude(best)) best = candidate;
                at += BlockSize;
            }
            while (at <= last)
            { if (Magnitude(at) > Magnitude(best)) best = at; at++; }
            return best;
        }
    }
}
