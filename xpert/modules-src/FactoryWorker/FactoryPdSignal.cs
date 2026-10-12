using System;
using System.Runtime.CompilerServices;
using HVX.SignalProcessing;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // One full-segment, native-rate product shared by display/calibration/TF.
    // Never filter independently cut pulse windows or modify capture.PdAdc.
    internal static class FactoryPdSignal
    {
        internal const string FilterId=PdLinearHighPass.FilterId;
        private sealed class Cached
        {
            internal short[] Snapshot;
            internal double Rate,Scale;
            internal double[] Filtered;
        }
        private static readonly ConditionalWeakTable<BenchCapture,Cached> cache=new ConditionalWeakTable<BenchCapture,Cached>();
        internal static double[] Get(BenchCapture capture)
        {
            if(capture==null || capture.PdAdc==null || capture.MaximumAdc<=0 || !FactorySettings.Positive(capture.PicoFullScaleV))
                throw new ArgumentException("10 kHz高通需要实际ADC、量程和采样率。");
            PdLinearHighPass.WindowLength(capture.PicoRateHz);
            var item=cache.GetValue(capture,c=>new Cached());double scale=capture.PicoFullScaleV/capture.MaximumAdc;
            lock(item)
            {
                bool same=item.Snapshot!=null && item.Rate==capture.PicoRateHz && item.Scale==scale && item.Snapshot.Length==capture.PdAdc.Length;
                // Exact comparison, not identity alone: mutable imported/test
                // captures must never silently reuse an older filtered product.
                if(same)for(int i=0;i<item.Snapshot.Length;i++)if(item.Snapshot[i]!=capture.PdAdc[i]){same=false;break;}
                if(!same)
                {
                    var snapshot=(short[])capture.PdAdc.Clone();var volts=new double[snapshot.Length];
                    for(int i=0;i<volts.Length;i++)volts[i]=snapshot[i]*scale;
                    var filtered=PdLinearHighPass.Apply(volts,capture.PicoRateHz);
                    item.Snapshot=snapshot;item.Rate=capture.PicoRateHz;item.Scale=scale;item.Filtered=filtered;
                }
                // Consumers only read this product; no filter result is written
                // back into hardware data, range/clipping or protection checks.
                return item.Filtered;
            }
        }
    }
}
