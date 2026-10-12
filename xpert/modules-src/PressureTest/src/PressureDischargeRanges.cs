using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // PS5000A_RANGE order, Pico official API: bipolar full scale, not peak-to-peak.
        // https://www.picotech.com/download/manuals/picoscope-5000-series-a-api-programmers-guide.pdf
        static readonly double[] PicoFullScaleVoltages={.01,.02,.05,.1,.2,.5,1,2,5,10,20};
        double calibrationPcPerVolt=Double.NaN;
        double calibrationReferenceChargePc=Double.NaN;
        string calibrationSourceHash="";
        string calibrationSource="",calibrationPhase="";
        DateTimeOffset calibrationAcquiredUtc;
        bool calibrationSimulation;
        [Browsable(false)] public bool IsDischargeCalibrated {get{lock(stateLock)return HasDischargeCalibrationNoLock();}}
        [Browsable(false)] public double DischargeCalibrationPcPerVolt {get{lock(stateLock)return HasDischargeCalibrationNoLock()?calibrationPcPerVolt:Double.NaN;}}
        [Browsable(false)] public string DischargeCalibrationSource {get{lock(stateLock)return calibrationSource;}}
        [Browsable(false)] public string DischargeCalibrationPhaseCode {get{lock(stateLock)return calibrationPhase;}}
        [Browsable(false)] public DateTimeOffset DischargeCalibrationAcquiredUtc {get{lock(stateLock)return calibrationAcquiredUtc;}}
        [Browsable(false)] public bool DischargeCalibrationIsSimulation {get{lock(stateLock)return calibrationSimulation;}}
        [Browsable(false)] public int SelectedPicoRangeCode {get{lock(stateLock)return HasDischargeCalibrationNoLock()?Array.IndexOf(RangeCodes,selectedRangeCode):-1;}}
        [Browsable(false)] public double SelectedFullScaleVoltageV {get{int code=SelectedPicoRangeCode;return code<0?Double.NaN:PicoFullScaleVoltages[code];}}
        [Browsable(false)] public double SelectedExactFullScalePc {get{lock(stateLock){int code=HasDischargeCalibrationNoLock()?Array.IndexOf(RangeCodes,selectedRangeCode):-1;return code<0?Double.NaN:PicoFullScaleVoltages[code]*calibrationPcPerVolt;}}}
        public double[] GetPicoFullScaleVoltages(){return (double[])PicoFullScaleVoltages.Clone();}
        public double[] GetExactDischargeRangesPc(){lock(stateLock)return HasDischargeCalibrationNoLock()?PicoFullScaleVoltages.Select(v=>v*calibrationPcPerVolt).ToArray():Enumerable.Repeat(Double.NaN,PicoFullScaleVoltages.Length).ToArray();}
        public double[] GetDisplayDischargeRangesPc(){return GetExactDischargeRangesPc().Select(RoundedDisplayPc).ToArray();}
        public bool[] GetVisibleDischargeRanges(){return GetExactDischargeRangesPc().Select(pc=>IsFinite(pc)&&pc>=100).ToArray();}
        public void SetDischargeCalibration(double effectivePcPerVolt,string source,DateTimeOffset acquiredUtc,string phaseCode,bool simulation)
        {
            SetDischargeCalibrationCore(effectivePcPerVolt,source,acquiredUtc,phaseCode,simulation,Double.NaN,"");
        }
        public void SetDischargeCalibrationWithEvidence(double effectivePcPerVolt,string source,DateTimeOffset acquiredUtc,string phaseCode,bool simulation,double referenceChargePc,string sourceHash)
        {
            if(!IsFinite(referenceChargePc)||referenceChargePc<=0)throw new ArgumentOutOfRangeException("referenceChargePc");
            if(String.IsNullOrWhiteSpace(sourceHash)||Convert.FromBase64String(sourceHash).Length!=32)throw new ArgumentException("校准来源校验值无效。","sourceHash");
            SetDischargeCalibrationCore(effectivePcPerVolt,source,acquiredUtc,phaseCode,simulation,referenceChargePc,sourceHash);
        }
        void SetDischargeCalibrationCore(double effectivePcPerVolt,string source,DateTimeOffset acquiredUtc,string phaseCode,bool simulation,double referenceChargePc,string sourceHash)
        {
            if(!IsFinite(effectivePcPerVolt)||effectivePcPerVolt<=0||!IsFinite(effectivePcPerVolt*20))throw new ArgumentOutOfRangeException("effectivePcPerVolt");
            int minimumRange=PressureChargeRangeFloor.MinimumIndex(effectivePcPerVolt);
            if(minimumRange<0)throw new ArgumentException("该校准在20 V最大量程下仍不足100 pC，无法设置加压测试量程。","effectivePcPerVolt");
            if(String.IsNullOrWhiteSpace(source))throw new ArgumentException("An explicit calibration source is required.","source");
            if(acquiredUtc==default(DateTimeOffset)||acquiredUtc.ToUniversalTime()>DateTimeOffset.UtcNow.AddMinutes(5))throw new ArgumentOutOfRangeException("acquiredUtc");
            string phase=NormalizePhase(phaseCode);
            RunOnUiThread(delegate{
                if(PressureParametersLocked)return;
                lock(stateLock)
                {
                    if(phase!=selectedPhaseCode||simulation!=simulationMode)throw new ArgumentException("Calibration phase and simulation provenance must match the active measurement context.");
                    calibrationPcPerVolt=effectivePcPerVolt;calibrationSource=source.Trim();calibrationPhase=phase;calibrationAcquiredUtc=acquiredUtc.ToUniversalTime();calibrationSimulation=simulation;
                    calibrationReferenceChargePc=referenceChargePc;calibrationSourceHash=sourceHash;
                    if(Array.IndexOf(RangeCodes,selectedRangeCode)<0)selectedRangeCode="PICO:5";
                    if(Array.IndexOf(RangeCodes,selectedRangeCode)<minimumRange)selectedRangeCode=RangeCodes[minimumRange];
                    rangeChangedSequence++;
                }
                InvalidateNoiseReferenceContext();UpdateRangeUi();Raise(RangeChanged);
            });
        }
        public void ClearDischargeCalibration(){RunOnUiThread(delegate{if(PressureParametersLocked)return;lock(stateLock){InvalidateDischargeCalibrationNoLock();rangeChangedSequence++;}InvalidateNoiseReferenceContext();UpdateRangeUi();Raise(RangeChanged);});}
        bool HasDischargeCalibrationNoLock(){return IsFinite(calibrationPcPerVolt)&&calibrationPcPerVolt>0&&calibrationSource.Length>0&&calibrationAcquiredUtc!=default(DateTimeOffset)&&calibrationPhase==selectedPhaseCode&&calibrationSimulation==simulationMode;}
        void InvalidateDischargeCalibrationNoLock(){calibrationPcPerVolt=calibrationReferenceChargePc=Double.NaN;calibrationSourceHash="";calibrationSource=calibrationPhase="";calibrationAcquiredUtc=default(DateTimeOffset);selectedRangeCode="UNCALIBRATED";}
        static double RoundedDisplayPc(double exact)
        {
            if(!IsFinite(exact)||exact<100)return Double.NaN;
            double step=exact<1000?50:100, scaled=exact/step, lower=Math.Floor(scaled);
            // Nearest display step; an exact half rounds DOWN (1.25 nC -> 1.2 nC).
            // This is a label only: SDK range codes and exact pC/V are unchanged.
            return (lower+(scaled-lower>0.5?1:0))*step;
        }
        string PressureUiText(string chinese,string english){return Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex,chinese,english);}
        string UncalibratedRangeLabel {get{return PressureUiText("未校准","Not calibrated");}}
        static string ChargeRangeLabel(double pc)
        {
            if(!IsFinite(pc))return String.Empty;
            return "±"+(pc>=1000?(pc/1000).ToString("0.0",CultureInfo.InvariantCulture)+" nC":pc.ToString("0",CultureInfo.InvariantCulture)+" pC");
        }
        string[] CreateCalibratedRangeLabels()
        {
            lock(stateLock)
            {
                if(!HasDischargeCalibrationNoLock())return Enumerable.Repeat(UncalibratedRangeLabel,PicoFullScaleVoltages.Length).ToArray();
                return PicoFullScaleVoltages.Select(v=>ChargeRangeLabel(RoundedDisplayPc(v*calibrationPcPerVolt))).ToArray();
            }
        }
    }
}
