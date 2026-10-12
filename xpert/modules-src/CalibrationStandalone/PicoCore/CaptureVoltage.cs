using System;

namespace Xiongxin.Scope
{
    // Processing copies only. Original ADC codes, overflow and capture metadata
    // remain unchanged for range/clipping checks and raw evidence.
    internal static class CaptureVoltage
    {
        internal const int AcCoupling = 0;
        internal const string DcRemovalId = "full-record-adc-mean-v1";
        internal static double MeanAdc(short[] samples)
        {
            if(samples==null)throw new ArgumentNullException("samples");
            if(samples.Length==0)return 0;
            long sum=0;foreach(short sample in samples)sum+=sample;
            return (double)sum/samples.Length;
        }
        internal static double[] Volts(Capture capture,bool removeDc)
        {
            if(capture==null||capture.Info==null||capture.Info.Config==null||capture.Info.Config.A==null||
                capture.Info.MaxAdc<=0||capture.A==null||capture.Info.Config.A.Range<0||capture.Info.Config.A.Range>=Settings.Ranges.Length)
                throw new ArgumentException("采集元数据缺失或量程无效。");
            double scale=Settings.Ranges[capture.Info.Config.A.Range]/capture.Info.MaxAdc;
            double mean=removeDc?MeanAdc(capture.A):0;
            var result=new double[capture.A.Length];
            for(int i=0;i<result.Length;i++)result[i]=(capture.A[i]-mean)*scale;
            return result;
        }
    }
}
