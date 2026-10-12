using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalProcessing;

namespace Xiongxin.Calibration
{
    internal sealed class CalibrationQualityReviewException : InvalidOperationException
    {
        internal CalibrationQualityReviewException(string message) : base(message) { }
    }
    internal sealed class PreflightDecision
    {
        internal int RangeIndex;
        internal double ThresholdV, PeakV, FilteredPeakV, NoiseV;
        internal bool Falling;
    }

    // A range hint only: never retain a previous waveform/trigger or grant input
    // readiness. Each Start still captures and validates a new 40 ms observation.
    internal sealed class CalibrationPreflightMemory
    {
        object worker, inputOwner;
        string context;
        int range=-1;
        internal int StartingRange(object currentWorker,object currentInputOwner,string currentContext)
        {
            return range>=0 && Object.ReferenceEquals(worker,currentWorker) &&
                Object.ReferenceEquals(inputOwner,currentInputOwner) && context==currentContext && !String.IsNullOrEmpty(context)
                ? range : Scope.Settings.Ranges.Length-1;
        }
        internal void Remember(object currentWorker,object currentInputOwner,string currentContext,int measuredRange)
        {
            if(currentWorker==null||currentInputOwner==null||String.IsNullOrEmpty(currentContext)||measuredRange<0||measuredRange>=Scope.Settings.Ranges.Length)
                throw new ArgumentException("量程预采来源无效。");
            worker=currentWorker;inputOwner=currentInputOwner;context=currentContext;range=measuredRange;
        }
        internal void Forget(){worker=null;inputOwner=null;context=null;range=-1;}
    }

    internal static class AcquisitionPlanning
    {
        internal const double QualityDurationMs = 40;

        // Noise/preflight is one fixed finite observation. It must not inherit
        // the UI's measurement segment length (historically defaulted to 60 ms).
        internal static Scope.Settings QualitySettings(Scope.Settings requested)
        {
            if(requested==null)throw new ArgumentNullException("requested");
            var actual=Scope.Wire.Decode<Scope.Settings>(Scope.Wire.Encode(requested));
            actual.CalibrationPulse=false;
            actual.TriggerMode=0;actual.PrePercent=0;actual.DurationMs=QualityDurationMs;
            actual.Validate();
            return actual;
        }

        internal static void RequireQualityRecord(Scope.Capture wave,Scope.Settings requested)
        {
            if(wave==null||wave.Info==null||wave.Info.Config==null||wave.Info.Config.A==null||wave.A==null||
                wave.Info.Samples<2||wave.A.Length!=wave.Info.Samples||!Scope.Settings.Finite(wave.Info.IntervalNs)||wave.Info.IntervalNs<=0)
                throw new InvalidOperationException("预采波形或实际采样时间无效。");
            if(wave.Info.Config.A.Coupling!=Scope.CaptureVoltage.AcCoupling)
                throw new InvalidOperationException("预采返回记录未使用本次要求的 AC 耦合，请重新采集。");
            if(requested==null||wave.Info.Config.A.Range!=requested.A.Range)
                throw new InvalidOperationException("预采返回的实际量程与请求不一致，不能确认量程与触发。");
            // N samples represent N sampling intervals, consistent with Settings.Count
            // and SignalQuality. Last timestamp is (N-1)*dt: do not silently lengthen
            // a requested 40 ms record just to make that last timestamp reach 40 ms.
            double coveredNs=wave.Info.Samples*wave.Info.IntervalNs;
            if(coveredNs+0.000001<QualityDurationMs*1000000 || coveredNs>=QualityDurationMs*1000000+wave.Info.IntervalNs+0.000001)
                throw new InvalidOperationException("预采实际记录不符合 40 ms 采样预算，不能作为本次质量记录。");
        }

        internal static double ResolveScreeningThreshold(double? manual, double automatic, double fullScale)
        {
            double value=manual.HasValue?manual.Value:automatic;
            if(Double.IsNaN(value)||Double.IsInfinity(value)||value<=0||Double.IsNaN(fullScale)||Double.IsInfinity(fullScale)||fullScale<=0||value>=fullScale)
                throw new InvalidOperationException("脉冲筛选阈值须大于 0 且小于当前量程。请调整阈值或恢复自动阈值后重新采集。");
            return value;
        }
        internal static PreflightDecision Plan(double[] volts, int currentRange, bool overflow)
        {
            if(volts==null||volts.Length<16||currentRange<0||currentRange>=Scope.Settings.Ranges.Length)
                throw new ArgumentException("预采数据或量程无效。");
            double peak=0; int peakIndex=0;
            // Deterministic bounded median estimates the noise floor, independent
            // of the small duty-cycle calibration peaks.
            int count=Math.Min(10001,volts.Length); var background=new double[count];
            for(int i=0;i<count;i++)background[i]=Math.Abs(volts[(int)((long)i*(volts.Length-1)/(count-1))]);
            Array.Sort(background); double noise=background[count/2]/.67448975;
            for(int i=0;i<volts.Length;i++) {
                if(Double.IsNaN(volts[i])||Double.IsInfinity(volts[i]))throw new ArgumentException("预采波形包含无效数值。");
                if(Math.Abs(volts[i])>peak){peak=Math.Abs(volts[i]);peakIndex=i;}
            }
            int selected=currentRange;
            if(overflow || peak>=Scope.Settings.Ranges[currentRange]*.97) selected=Math.Min(Scope.Settings.Ranges.Length-1,currentRange+1);
            else {
                selected=0;
                while(selected<Scope.Settings.Ranges.Length-1 && Scope.Settings.Ranges[selected]<peak/0.70)selected++;
                // Hysteresis: do not alternate between neighbouring ranges.
                if(selected<currentRange && peak>Scope.Settings.Ranges[selected]*.65)selected=currentRange;
            }
            double trigger=Math.Max(6*noise,peak*.20);
            trigger=Math.Max(Scope.Settings.Ranges[selected]/32767*8,trigger);
            return new PreflightDecision {RangeIndex=selected,ThresholdV=trigger,PeakV=peak,NoiseV=noise,Falling=volts[peakIndex]<0};
        }

        internal static HVX.OWTS.Task38.CalibrationWindowResult RejectOutliers(HVX.OWTS.Task38.CalibrationWindowResult input)
        {
            var all=input.Windows;
            if(all.Length<3)throw new CalibrationQualityReviewException("有效脉冲不足 3 次，不能完成质量判断和平均。");
            var selected=SelectConsistentPulses(input);
            if(selected.Windows.Length<3||selected.Windows.Length<all.Length*.5)
                throw new CalibrationQualityReviewException("脉冲形态不一致或异常比例过高；已保存原始记录，请检查源与干扰。");
            return selected;
        }

        internal static HVX.OWTS.Task38.CalibrationWindowResult SelectConsistentPulses(HVX.OWTS.Task38.CalibrationWindowResult input)
        {
            if(input==null||input.Windows==null)throw new InvalidOperationException("缺少校准脉冲数据。");
            var all=input.Windows;
            if(all.Length==0)return new HVX.OWTS.Task38.CalibrationWindowResult{Windows=new double[0][],Average=new double[0],Message="尚无有效脉冲。",CandidateCount=input.CandidateCount,RejectedCount=input.RejectedCount};
            if(all[0]==null||all[0].Length<2||all.Any(w=>w==null||w.Length!=all[0].Length||w.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v))))
                throw new InvalidOperationException("脉冲数据不完整，不能累计计算。");
            if(all.Length>4096||(long)all.Length*all[0].Length>64000000)
                throw new InvalidOperationException("本次校准脉冲数据超出处理上限，请检查采集与开窗设置。");
            int length=all[0].Length; var median=new double[length]; var column=new double[all.Length];
            for(int j=0;j<length;j++) {for(int i=0;i<all.Length;i++)column[i]=all[i][j];Array.Sort(column);median[j]=column[column.Length/2];}
            double medPeak=median.Max(v=>Math.Abs(v)); var accepted=new List<double[]>();
            foreach(var wave in all) {
                double xy=0,xx=0,yy=0,peak=0;
                for(int j=0;j<length;j++){xy+=wave[j]*median[j];xx+=wave[j]*wave[j];yy+=median[j]*median[j];peak=Math.Max(peak,Math.Abs(wave[j]));}
                double correlation=xx>0&&yy>0?xy/Math.Sqrt(xx*yy):0;
                if(correlation>=.90 && peak>=medPeak*.5 && peak<=medPeak*1.5)accepted.Add(wave);
            }
            var average=new double[length];foreach(var wave in accepted)for(int j=0;j<length;j++)average[j]+=wave[j]/accepted.Count;
            return new HVX.OWTS.Task38.CalibrationWindowResult {Windows=accepted.ToArray(),Average=average,
                CandidateCount=input.CandidateCount,RejectedCount=input.RejectedCount+all.Length-accepted.Count,
                Message=input.Message+"；形态/幅值异常另剔除 "+(all.Length-accepted.Count)+"，最终平均 "+accepted.Count+" 次。"};
        }

        internal static HVX.OWTS.Task38.CalibrationWindowResult ContinueForReview(HVX.OWTS.Task38.CalibrationWindowResult input)
        {
            if(input==null||input.Windows==null||input.Windows.Length==0||input.Windows[0]==null||input.Windows[0].Length<2)
                throw new InvalidOperationException("没有可计算的有效脉冲。原始记录已保留，请检查校准源后重试。");
            int length=input.Windows[0].Length;
            if(input.Windows.Any(w=>w==null||w.Length!=length||w.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v))))
                throw new InvalidOperationException("脉冲数据不完整，不能继续计算。原始记录已保留。");
            var average=new double[length];
            foreach(var wave in input.Windows)for(int i=0;i<length;i++)average[i]+=wave[i]/input.Windows.Length;
            if(average.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v)))throw new InvalidOperationException("平均波形超出有效数值范围。");
            return new HVX.OWTS.Task38.CalibrationWindowResult {Windows=input.Windows,Average=average,
                CandidateCount=input.CandidateCount,RejectedCount=input.RejectedCount,
                Message=input.Message+"；操作员选择继续计算："+input.Windows.Length+" 次脉冲，结果待复核，未通过校准。"};
        }

        internal static double[] Volts(Scope.Capture capture)
        {
            return Scope.CaptureVoltage.Volts(capture,true);
        }
        internal static double[] RawVolts(Scope.Capture capture) { return Scope.CaptureVoltage.Volts(capture,false); }
        internal static double[] NoiseObservation(Scope.Capture capture)
        {
            if(capture==null||capture.Info==null||capture.Info.IntervalNs<=0)
                throw new ArgumentException("噪声采样时间无效。");
            return PdLinearHighPass.Apply(Volts(capture),1e9/capture.Info.IntervalNs);
        }
    }
}
