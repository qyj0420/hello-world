using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVX.SignalProcessing;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private sealed class NoiseMatchRegion { internal int First,Last,Peak; }
        private NoiseMatchRegion[] noiseMatchResults=new NoiseMatchRegion[0];
        private WaveformData noiseMatchWave,noiseFilteredWave;
        private double[] noiseFilteredSamples;
        private NoisePulseTemplate[] noiseMatchTemplates;
        private int noiseMatchFirst=-1,noiseMatchLast=-1;
        private double noiseMatchTolerance=Double.NaN;
        private CancellationTokenSource noiseMatchCancellation;
        private bool noiseMatchBusy;
        private string noiseMatchStatus="";

        // Same normalized physical-time correlation as acquisition filtering.
        // Scan packet peaks, not screen-decimated pixels; a matched envelope is
        // represented once and never changes or deletes the acquired samples.
        private static NoiseMatchRegion[] FindNoiseMatches(double[] raw,double[] filtered,NoisePulseTemplate[] templates,
            double rate,int first,int last,double tolerance,CancellationToken cancel)
        {
            var result=new List<NoiseMatchRegion>();
            if(raw==null||filtered==null||raw.Length!=filtered.Length||templates==null)return result.ToArray();
            if(Double.IsNaN(tolerance)||tolerance<0||tolerance>10)throw new ArgumentOutOfRangeException("tolerance");
            first=Math.Max(1,first);last=Math.Min(raw.Length-2,last);
            int previousEnd=-1;
            for(int i=first;i<=last;i++) {
                if((i&127)==0)cancel.ThrowIfCancellationRequested();
                bool peak=Math.Abs(raw[i])>Math.Abs(raw[i-1])&&Math.Abs(raw[i])>=Math.Abs(raw[i+1]);
                peak|=Math.Abs(filtered[i])>Math.Abs(filtered[i-1])&&Math.Abs(filtered[i])>=Math.Abs(filtered[i+1]);
                if(!peak||i<=previousEnd)continue;
                foreach(var t in templates) {
                    cancel.ThrowIfCancellationRequested();
                    if(NoisePulseTemplates.Similarity(t,filtered,i,rate)<1-tolerance/100)continue;
                    int start=i-(int)Math.Ceiling(t.PeakIndex*rate/t.SampleRateHz);
                    int end=i+(int)Math.Ceiling((t.Samples.Length-1-t.PeakIndex)*rate/t.SampleRateHz);
                    result.Add(new NoiseMatchRegion{First=Math.Max(0,start),Last=Math.Min(raw.Length-1,end),Peak=i});
                    previousEnd=end;break;
                }
            }
            return result.ToArray();
        }

        private async void EnsureNoiseMatchOverlay(WaveformData wave)
        {
            if(!IsHandleCreated||IsDisposed||Disposing)return;
            int first=NoiseSample(CurrentNoisePlotRectangle.Left,wave),last=NoiseSample(CurrentNoisePlotRectangle.Right,wave);
            var templates=noiseTemplates;double tolerance=NoiseSimilarityTolerancePercent;
            bool same=wave==noiseMatchWave&&Object.ReferenceEquals(templates,noiseMatchTemplates)&&first==noiseMatchFirst&&last==noiseMatchLast&&tolerance==noiseMatchTolerance;
            if(same)return;
            if(noiseMatchBusy){noiseMatchResults=new NoiseMatchRegion[0];if(noiseMatchCancellation!=null)noiseMatchCancellation.Cancel();return;}
            noiseMatchWave=wave;noiseMatchTemplates=templates;noiseMatchFirst=first;noiseMatchLast=last;noiseMatchTolerance=tolerance;
            noiseMatchResults=new NoiseMatchRegion[0];
            if(templates.Length==0){noiseMatchStatus="";return;}
            noiseMatchBusy=true;var cancellation=new CancellationTokenSource();noiseMatchCancellation=cancellation;
            noiseMatchStatus=CalibrationText("正在标注相似干扰脉冲…","Finding matching interference pulses…");
            UpdateNoiseBusyFeedback();
            try {
                var filtered=await NoiseHighPassedSamplesAsync(wave);
                cancellation.Token.ThrowIfCancellationRequested();
                noiseFilteredWave=wave;noiseFilteredSamples=filtered;
                var matches=await Task.Run(()=>FindNoiseMatches(wave.Samples,filtered,templates,1/wave.DxSeconds,first,last,tolerance,cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                if(!IsDisposed&&!Disposing) {
                    noiseMatchResults=matches;
                    noiseMatchStatus=CalibrationText("当前视窗：匹配干扰脉冲 ","Matching interference pulses in view: ")+matches.Length+
                        CalibrationText("；相似度 ≥ ","; similarity ≥ ")+(100-tolerance).ToString("0.##")+"%";
                }
            }catch(OperationCanceledException){noiseMatchWave=null;noiseMatchStatus="";}
            catch(Exception ex){noiseMatchResults=new NoiseMatchRegion[0];noiseMatchStatus=RecordCalibrationCustomerFailure("Noise match marking",ex);}
            finally {
                noiseMatchBusy=false;if(Object.ReferenceEquals(noiseMatchCancellation,cancellation))noiseMatchCancellation=null;cancellation.Dispose();
                UpdateNoiseBusyFeedback();
            }
        }
        private Task<double[]> NoiseHighPassedSamplesAsync(WaveformData wave)
        {
            if(wave.AcquisitionNoiseHighPassed)return Task.FromResult(wave.Samples);
            if(noiseFilteredWave==wave&&noiseFilteredSamples!=null)return Task.FromResult(noiseFilteredSamples);
            return Task.Run(()=>PdLinearHighPass.Apply(wave.Samples,1/wave.DxSeconds));
        }
    }
}
