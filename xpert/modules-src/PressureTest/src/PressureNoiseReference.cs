using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Xiongxin.OWTS.Noise;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        readonly NoiseReferenceStore noiseReferences=new NoiseReferenceStore();
        ZeroVoltageNoiseReference currentNoiseReference;
        bool currentAcquisitionReferenceOnly;
        bool currentAcquisitionReferencePreview;
        double[] noisePeakSamples;
        double noisePeakPc=Double.NaN;
        string noiseReferenceState="None",noiseProjectPath="";
        [Browsable(false)] public string ProjectPath
        {
            get{return noiseProjectPath;}
            set
            {
                string path=String.IsNullOrWhiteSpace(value)?"":Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
                if(String.Equals(path,noiseProjectPath,StringComparison.OrdinalIgnoreCase))return;
                RunOnUiThread(delegate{if(PressureParametersLocked)return;ResetP04FeedbackSession();ClearPressureInitialRangeCache();pressureNoiseInitializationReady=false;noiseProjectPath=path;noiseReferences.Clear();spectrumDashboardView.ResetAnalysisOptions();ClearDisplayData();});
            }
        }
        [Browsable(false)] public bool CurrentAcquisitionIsNoiseReference{get{return currentAcquisitionReferenceOnly;}}
        [Browsable(false)] public bool CurrentAcquisitionIsReferencePreview{get{return currentAcquisitionReferencePreview;}}
        public void SetReferencePreviewStatus(string message)
        {
            if(!SimulationMode||!currentAcquisitionReferencePreview)throw new InvalidOperationException("No simulation reference preview is active.");
            RunOnUiThread(delegate { ApplyReferencePreviewMetrics();spectrumDashboardView.SetReferencePreviewMessage(message); });
        }
        void ApplyReferencePreviewMetrics()
        {
            lock(stateLock){maxDischargePc=equivalentCapacitanceUf=oscillationFrequencyHz=dissipationFactorPercent=locationConfidencePercent=Double.NaN;analysisStatus="REFERENCE_PREVIEW_ONLY";}
            UpdateMetricsUi();
        }
        void InstallReferencePreviewContext(IDictionary<string,object> metadata)
        {
            object value;
            currentAcquisitionReferencePreview=SimulationMode&&metadata!=null&&metadata.TryGetValue("referencePreview",out value)&&value is bool&&(bool)value;
            spectrumDashboardView.SetReferencePreviewMessage(currentAcquisitionReferencePreview?PressureUiText("未提供经过核验的定位事件，仅显示参考波形。","No verified location events are provided; reference waveforms only."):String.Empty);
            if(currentAcquisitionReferencePreview)spectrumDashboardView.ClearData();
        }
        [Browsable(false)] public string NoiseReferenceStateCode{get{return noiseReferenceState;}}
        public IDictionary<string,object> GetNoiseReferenceSummary(){return currentNoiseReference==null?new Dictionary<string,object>{{"state",noiseReferenceState},{"automaticFilteringAllowed",false}}:currentNoiseReference.Summary();}
        public double[] GetNoiseReferenceSamples(){return currentNoiseReference==null?new double[0]:currentNoiseReference.CopySamples();}
        public void SetAcquisitionWaveforms(double[] voltageKv,double[] partialDischargePc,double sampleRateHz,IDictionary<string,object> metadata)
        {InstallAcquisitionWaveforms(voltageKv,partialDischargePc,sampleRateHz,metadata);}
        public bool RegisterZeroVoltageNoiseReference(double[] samples,IDictionary<string,object> metadata)
        {
            if(!NoiseAcquisitionContext.IsExplicitZero(metadata))return false;
            bool result=false;RunOnUiThread(delegate{result=InstallNoiseContext(samples,Double.NaN,metadata,false);});return result;
        }
        bool InstallNoiseContext(double[] samples,double sampleRate,IDictionary<string,object> metadata,bool newAcquisition=true)
        {
            currentNoiseReference=null;noiseReferenceState="None";if(newAcquisition){currentAcquisitionReferenceOnly=NoiseAcquisitionContext.IsExplicitZero(metadata);currentZeroVoltageAnalysis=false;zeroAnalysisSource="";}
            // Clear statistics before any waveform/host metrics can be published.
            if(newAcquisition&&currentAcquisitionReferenceOnly&&spectrumDashboardView!=null)spectrumDashboardView.ClearData();
            if(metadata==null){RefreshNoiseReferenceView();return false;}
            try
            {
                var context=NoiseAcquisitionContext.Read(metadata);
                if(noiseProjectPath.Length==0||!String.Equals(context.Project,noiseProjectPath,StringComparison.OrdinalIgnoreCase)||context.Phase!=SelectedPhaseCode||context.Simulation!=SimulationMode||(!Double.IsNaN(sampleRate)&&context.Rate!=sampleRate))throw new ArgumentException("context_mismatch");
                if(context.Target==0){currentNoiseReference=new ZeroVoltageNoiseReference(samples,context);noiseReferences.Add(currentNoiseReference);}
                else currentNoiseReference=noiseReferences.Find(context);
                noiseReferenceState=currentNoiseReference==null?"None":currentNoiseReference.State;
                if(newAcquisition&&context.Target==0&&cycleZeroAnalysisRequested&&cycleIdentityAccepted&&!currentAcquisitionReferencePreview){currentZeroVoltageAnalysis=true;currentAcquisitionReferenceOnly=false;zeroAnalysisSource=context.Source;}
                RefreshNoiseReferenceView();return currentNoiseReference!=null;
            }
            catch(ArgumentException){noiseReferenceState="Pending";RefreshNoiseReferenceView();return false;}
            catch(NotSupportedException){noiseReferenceState="Pending";RefreshNoiseReferenceView();return false;}
        }
        void RefreshNoiseReferenceView()
        {
            if(spectrumDashboardView!=null)spectrumDashboardView.SetZeroVoltageAnalysis(currentZeroVoltageAnalysis);
            if(spectrumDashboardView!=null)spectrumDashboardView.SetNoiseReference(currentNoiseReference,currentAcquisitionReferenceOnly&&currentNoiseReference==null,currentAcquisitionReferenceOnly,LanguageIndex);
        }
        void ResetNoiseReferenceView(){noisePeakSamples=null;noisePeakPc=Double.NaN;currentAcquisitionReferenceOnly=false;currentAcquisitionReferencePreview=false;currentZeroVoltageAnalysis=false;zeroAnalysisSource="";currentNoiseReference=null;noiseReferenceState="None";if(spectrumDashboardView!=null)spectrumDashboardView.SetReferencePreviewMessage(String.Empty);RefreshNoiseReferenceView();}
        // Changing a selector cannot reclassify the already captured zero data.
        // Keep its exclusion latch until a new waveform arrives or data is cleared.
        void InvalidateNoiseReferenceContext(){currentNoiseReference=null;noiseReferenceState="None";RefreshNoiseReferenceView();}
        void ApplyNoiseReferenceMetrics(double knownPeak=Double.NaN)
        {
            // Reference-only excludes diagnostic accumulation, not the measured
            // injection amplitude. Cache by the immutable acquisition array so
            // UI refresh never scans a multi-million-sample segment repeatedly.
            if(IsFinite(knownPeak)&&knownPeak>=0){noisePeakSamples=lastDischargePc;noisePeakPc=knownPeak;}
            else if(!Object.ReferenceEquals(noisePeakSamples,lastDischargePc)){
                noisePeakSamples=lastDischargePc;noisePeakPc=ReferenceCapturePeak(lastDischargePc);
            }
            lock(stateLock){maxDischargePc=noisePeakPc;equivalentCapacitanceUf=oscillationFrequencyHz=dissipationFactorPercent=Double.NaN;analysisStatus="NOISE_REFERENCE_ONLY";
                metricFrequencyAvailable=metricCapacitanceAvailable=metricTanDeltaAvailable=false;
                rawMeasuredCapacitanceUf=rawMeasuredTanDeltaPercent=Double.NaN;}
            UpdateMetricsUi();
        }
        static double ReferenceCapturePeak(double[] samples)
        {
            if(samples==null||samples.Length==0)return Double.NaN;
            double peak=0;
            foreach(double sample in samples){if(!IsFinite(sample))return Double.NaN;peak=Math.Max(peak,Math.Abs(sample));}
            return peak;
        }
    }
}
