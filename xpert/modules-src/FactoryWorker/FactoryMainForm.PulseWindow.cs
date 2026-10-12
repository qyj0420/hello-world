using System;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        // Measurement/label analysis may use a normally completed capture-only
        // result. This does NOT assign accepted-result or discharge status.
        private FactoryResult pulseAnalysisRound;
        private void PreparePulseWindow(FactoryResult round)
        {
            pulseAnalysisRound=round;
            var pendingTimeline=LoadProcessTimeline(round.DirectoryPath,round);
            if(round.Kind!=FactoryRunKind.Pressure)return;
            var metrics=round.Metrics ?? FactoryAnalysis.Analyze(round);round.Metrics=metrics;
            var reference=metrics.ReferenceAnalysis;
            referenceSummary.Text="原始采集记录："+round.DirectoryPath+"\n"+
                (round.CaptureOnlyAvailable()?FactoryRangeReview.EvaluateCapturedData(round):FactoryRangeReview.Evaluate(round)).WindowDefinition+
                (reference==null?"":"\n"+reference.Detail);
            var phase=FactoryRangePhase.Analyze(round.Capture);
            double first=round.Settings.TargetKv==0?0:phase.Valid?phase.FirstZeroMs:Double.NaN;
            if(round.Capture.Settings.PreTriggerPercent==100)first=0; // explicit DC-only label selection, not an oscillation/range claim
            rawPd.ConfigureSelection(first,round.Capture.PdAdc.Length*1000.0/round.Capture.PicoRateHz);
            var estimate=FactoryTfAnalysis.Estimate(round.Capture);
            rawPd.SetThreshold((double)tfThresholdPc.Value,estimate.BaselineV*round.Capture.Settings.PdCalibrationPcPerV);
            if(!rawPd.HasSelection)tfSummary.Text="未识别有效振荡首零点，未自动选窗；不把切换过冲当作振荡脉冲。";
            UpdatePostProcessingButtons();
        }
        private void PulseWindowChanged()
        {
            currentTf=null;RefreshTfView();
            tfSummary.Text="选区 A="+rawPd.SelectionStartMs.ToString("0.######")+" ～ B="+rawPd.SelectionEndMs.ToString("0.######")+" ms；点击确认阈值提取。左游标可移至导通过冲之前用于标签。";
        }
        private async Task SelectPulseEnvelope(double firstMs,double lastMs)
        {
            if(busy || scanning || exporting || pulseAnalysisRound==null)return;
            var round=pulseAnalysisRound; exporting=true;SetBusy(true);
            var cancel=new CancellationTokenSource();analysisCancellation=cancel;stop.Enabled=true;var token=cancel.Token;
            try
            {
                var c=round.Capture;
                var window=await Task.Run(()=>{
                    var signal=FactoryPdSignal.Get(c);var estimate=FactoryTfAnalysis.Estimate(c);
                    int first=Math.Max(0,(int)Math.Floor(firstMs*c.PicoRateHz/1000));
                    int last=Math.Min(signal.Length,(int)Math.Ceiling(lastMs*c.PicoRateHz/1000));
                    return FactoryPulseEnvelopeWindow.Select(signal,estimate.BaselineV,0,signal.Length,first,last);
                },token);
                token.ThrowIfCancellationRequested();
                if(round!=pulseAnalysisRound)return;
                int guard=HVX.SignalProcessing.PdLinearHighPass.GuardSamples(c.PicoRateHz);
                if(window==null || window.Start<guard || window.End>=c.PdAdc.Length-guard)
                {tfSummary.Text="开窗未完成：缺少完整前后包络或过于接近滤波边界；原选区保留。";return;}
                if(!rawPd.HasSelection)rawPd.ConfigureSelection(0,c.PdAdc.Length*1000.0/c.PicoRateHz);
                rawPd.SetSelection(window.Start*1000.0/c.PicoRateHz,(window.End+1)*1000.0/c.PicoRateHz);
                tfSummary.Text="已按100+101校准信号过滤方式开窗：3%峰值、两侧16点静隙；点击确认阈值提取，可用于导通过冲标签。";
            }
            catch(OperationCanceledException){tfSummary.Text="开窗分析已停止；不应用迟到结果，原选区保留。";}
            catch(Exception ex){tfSummary.Text="开窗失败："+ex.Message;}
            finally{if(ReferenceEquals(analysisCancellation,cancel))analysisCancellation=null;cancel.Dispose();exporting=false;SetBusy(false);if(closeAfterStop)Close();}
        }
    }
}
