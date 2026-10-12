using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// Task17 对照可扫参数（来源：dac-source AppSettings.TaskMinus3 / ReviewAnalysisEngine / PressureBatchAnalysis）。
    /// .dac results/pd_map.parameters 不含这些调参；默认取原软件设置页默认值。
    /// </summary>
    public sealed class DacCompareTuning
    {
        public string Id { get; set; }
        public string Note { get; set; }
        public double AutomaticThresholdMultiplier { get; set; }
        public bool ApplyLinearFilter { get; set; }
        public double[] LinearFilterCutoffsHz { get; set; }
        public bool UseFirstArrivalTiming { get; set; }
        public int MaxPulses { get; set; }
        public int MultipleReflectionSuppressionCount { get; set; }
        public double AttenuationTolerancePercent { get; set; }
        /// <summary>Sixth 最小相关%；原 = 100 − CorrelationErrorPercent。</summary>
        public double MinimumCorrelationPercent { get; set; }
        /// <summary>Sixth useAttenuationReference。</summary>
        public bool UseAttenuationReference { get; set; }
        /// <summary>Sixth usePulseWidthComparison。</summary>
        public bool UsePulseWidthComparison { get; set; }
        /// <summary>Sixth pulseWidthErrorPercent；-1=引擎默认。</summary>
        public double PulseWidthErrorPercent { get; set; }
        public string EndpointPolicy { get; set; }
        public int AlgorithmIndex { get; set; }

        public DacCompareTuning()
        {
            // 2026-10-12 小网格最优：thr2（AutomaticThresholdMultiplier=2；其余对齐 TaskMinus3 + 滤镜关）
            // 2025 关键 mid 中位 nn_body：~126 → ~27.5 m；mid5 仍≈14。仍不可现场验收。
            Id = "thr2";
            Note = "sweep20261012 best; thr=2; filt=0; TaskMinus3 rest";
            AutomaticThresholdMultiplier = 2;
            ApplyLinearFilter = false;
            LinearFilterCutoffsHz = new double[] { 10000, 3000000 };
            UseFirstArrivalTiming = false;
            MaxPulses = 30;
            MultipleReflectionSuppressionCount = 1;
            AttenuationTolerancePercent = 15;
            MinimumCorrelationPercent = 70; // CorrelationErrorPercent=30
            UseAttenuationReference = true;
            UsePulseWidthComparison = true;
            PulseWidthErrorPercent = -1;
            EndpointPolicy = DacCalibrationScale.DefaultEndpointPolicy;
            AlgorithmIndex = 5;
        }

        public DacCompareTuning Clone()
        {
            return new DacCompareTuning
            {
                Id = Id,
                Note = Note,
                AutomaticThresholdMultiplier = AutomaticThresholdMultiplier,
                ApplyLinearFilter = ApplyLinearFilter,
                LinearFilterCutoffsHz = LinearFilterCutoffsHz != null
                    ? (double[])LinearFilterCutoffsHz.Clone()
                    : new double[] { 10000, 3000000 },
                UseFirstArrivalTiming = UseFirstArrivalTiming,
                MaxPulses = MaxPulses,
                MultipleReflectionSuppressionCount = MultipleReflectionSuppressionCount,
                AttenuationTolerancePercent = AttenuationTolerancePercent,
                MinimumCorrelationPercent = MinimumCorrelationPercent,
                UseAttenuationReference = UseAttenuationReference,
                UsePulseWidthComparison = UsePulseWidthComparison,
                PulseWidthErrorPercent = PulseWidthErrorPercent,
                EndpointPolicy = EndpointPolicy,
                AlgorithmIndex = AlgorithmIndex
            };
        }

        /// <summary>写入 LocationAnalysisRequest（不改波形/工程路径）。</summary>
        public void ApplyTo(LocationAnalysisRequest req)
        {
            if (req == null) return;
            req.AutomaticThresholdMultiplier = AutomaticThresholdMultiplier;
            req.ApplyLinearFilter = ApplyLinearFilter;
            req.LinearFilterCutoffsHz = LinearFilterCutoffsHz != null && LinearFilterCutoffsHz.Length >= 2
                ? (double[])LinearFilterCutoffsHz.Clone()
                : new double[] { 10000, 3000000 };
            req.UseFirstArrivalTiming = UseFirstArrivalTiming;
            req.MaxPulses = MaxPulses;
            req.MultipleReflectionSuppressionCount = MultipleReflectionSuppressionCount;
            req.AttenuationTolerancePercent = AttenuationTolerancePercent;
            req.MinimumCorrelationPercent = MinimumCorrelationPercent;
            req.UseAttenuationReference = UseAttenuationReference;
            req.UsePulseWidthComparison = UsePulseWidthComparison;
            req.PulseWidthErrorPercent = PulseWidthErrorPercent;
            req.EndpointPolicy = string.IsNullOrEmpty(EndpointPolicy)
                ? DacCalibrationScale.DefaultEndpointPolicy
                : EndpointPolicy;
            req.AlgorithmIndex = AlgorithmIndex == 0 ? 5 : AlgorithmIndex;
        }

        public string ToStatusToken()
        {
            return "tune=" + (Id ?? "?")
                + ";thr=" + AutomaticThresholdMultiplier.ToString("G4", CultureInfo.InvariantCulture)
                + ";filt=" + (ApplyLinearFilter ? "1" : "0")
                + ";first=" + (UseFirstArrivalTiming ? "1" : "0")
                + ";maxP=" + MaxPulses.ToString(CultureInfo.InvariantCulture)
                + ";supp=" + MultipleReflectionSuppressionCount.ToString(CultureInfo.InvariantCulture)
                + ";atten=" + AttenuationTolerancePercent.ToString("G4", CultureInfo.InvariantCulture)
                + ";minCorr=" + MinimumCorrelationPercent.ToString("G4", CultureInfo.InvariantCulture)
                + ";attRef=" + (UseAttenuationReference ? "1" : "0")
                + ";pwCmp=" + (UsePulseWidthComparison ? "1" : "0")
                + ";pwErr=" + PulseWidthErrorPercent.ToString("G4", CultureInfo.InvariantCulture)
                + ";ep=" + (EndpointPolicy ?? "")
                + ";algo=" + AlgorithmIndex.ToString(CultureInfo.InvariantCulture);
        }

        public string ToCompactCsv()
        {
            var sb = new StringBuilder();
            sb.Append(Id ?? "").Append('|')
                .Append(AutomaticThresholdMultiplier.ToString("G4", CultureInfo.InvariantCulture)).Append('|')
                .Append(ApplyLinearFilter ? "1" : "0").Append('|')
                .Append(UseFirstArrivalTiming ? "1" : "0").Append('|')
                .Append(MaxPulses).Append('|')
                .Append(MultipleReflectionSuppressionCount).Append('|')
                .Append(AttenuationTolerancePercent.ToString("G4", CultureInfo.InvariantCulture)).Append('|')
                .Append(MinimumCorrelationPercent.ToString("G4", CultureInfo.InvariantCulture)).Append('|')
                .Append(UseAttenuationReference ? "1" : "0").Append('|')
                .Append(UsePulseWidthComparison ? "1" : "0").Append('|')
                .Append(PulseWidthErrorPercent.ToString("G4", CultureInfo.InvariantCulture)).Append('|')
                .Append(EndpointPolicy ?? "").Append('|')
                .Append(AlgorithmIndex);
            return sb.ToString();
        }

        /// <summary>
        /// 对照默认：对齐原软件 TaskMinus3（滤镜关）。扫参前/后均可被 Best 覆盖写入。
        /// </summary>
        public static DacCompareTuning Default { get; set; } = new DacCompareTuning();

        /// <summary>本轮对照用的上一版（ApplyLinearFilter=true）快照，便于前后对比。</summary>
        public static DacCompareTuning PriorCompareV4()
        {
            var t = new DacCompareTuning();
            t.Id = "prior_v4_filtOn";
            t.Note = "DacLocationCompare/v4 BuildRequest: ApplyLinearFilter=true";
            t.ApplyLinearFilter = true;
            return t;
        }

        /// <summary>原软件 TaskMinus3 出厂默认（AutomaticThresholdMultiplier=3，滤镜关）。</summary>
        public static DacCompareTuning OrigSoftwareDefaults()
        {
            var t = new DacCompareTuning();
            t.Id = "orig_defaults";
            t.Note = "AppSettings.TaskMinus3 + WaveformFilterMode!=1";
            t.AutomaticThresholdMultiplier = 3;
            t.ApplyLinearFilter = false;
            t.UseFirstArrivalTiming = false;
            t.MaxPulses = 30;
            t.MultipleReflectionSuppressionCount = 1;
            t.AttenuationTolerancePercent = 15;
            t.MinimumCorrelationPercent = 70;
            t.EndpointPolicy = DacCalibrationScale.DefaultEndpointPolicy;
            t.AlgorithmIndex = 5;
            return t;
        }

        /// <summary>小网格：单维扰动 + 少量组合（约 16 组）。</summary>
        public static IList<DacCompareTuning> SmallGrid()
        {
            var list = new List<DacCompareTuning>();
            list.Add(PriorCompareV4());
            list.Add(OrigSoftwareDefaults());

            void Add(string id, string note, Action<DacCompareTuning> mut)
            {
                var t = OrigSoftwareDefaults();
                t.Id = id;
                t.Note = note;
                mut(t);
                list.Add(t);
            }

            Add("thr2", "threshold×2", t => t.AutomaticThresholdMultiplier = 2);
            Add("thr4", "threshold×4", t => t.AutomaticThresholdMultiplier = 4);
            Add("thr5", "threshold×5", t => t.AutomaticThresholdMultiplier = 5);
            Add("filtOn", "linear filter on (cutoffs 10k–3M)", t => t.ApplyLinearFilter = true);
            Add("firstArr", "useFirstArrivalTiming=true", t => t.UseFirstArrivalTiming = true);
            Add("max100", "maxPulses=100 (smoke/regression cap)", t => t.MaxPulses = 100);
            Add("supp0", "multipleReflectionSuppressionCount=0", t => t.MultipleReflectionSuppressionCount = 0);
            Add("suppAuto", "multipleReflectionSuppressionCount=-1 auto", t => t.MultipleReflectionSuppressionCount = -1);
            Add("corr50", "minimumCorrelation=50 (CorrelationError=50)", t => t.MinimumCorrelationPercent = 50);
            Add("corr85", "minimumCorrelation=85 (CorrelationError=15)", t => t.MinimumCorrelationPercent = 85);
            Add("atten25", "attenuationTolerance=25%", t => t.AttenuationTolerancePercent = 25);
            Add("epAnd", "EndpointPolicy 2;1.5;AND;OR", t => t.EndpointPolicy = "2;1.5;AND;OR");
            Add("epWide", "EndpointPolicy 3;2;OR;OR", t => t.EndpointPolicy = "3;2;OR;OR");
            Add("combo_events", "thr2+max100+supp0+corr50 (seek more events)", t =>
            {
                t.AutomaticThresholdMultiplier = 2;
                t.MaxPulses = 100;
                t.MultipleReflectionSuppressionCount = 0;
                t.MinimumCorrelationPercent = 50;
            });
            Add("combo_orig_max", "orig + max100", t => t.MaxPulses = 100);
            return list;
        }

        /// <summary>
        /// thr2 下配对/多峰发现小网格（禁止 thr1）。依据 mid5 StatusJson：
        /// cal 无效→attenuation gate；FWHM/SNR→pulseWidth；corr floor；supp；线性滤波。
        /// </summary>
        public static IList<DacCompareTuning> PairingGrid()
        {
            var list = new List<DacCompareTuning>();
            void Add(string id, string note, Action<DacCompareTuning> mut)
            {
                var t = new DacCompareTuning(); // thr2 base
                t.Id = id;
                t.Note = note;
                mut(t);
                list.Add(t);
            }
            Add("thr2_base", "thr2 baseline after cal-stub fix", t => { });
            Add("corr50", "minCorr=50", t => t.MinimumCorrelationPercent = 50);
            Add("corr60", "minCorr=60", t => t.MinimumCorrelationPercent = 60);
            Add("atten25", "attenTol=25", t => t.AttenuationTolerancePercent = 25);
            Add("atten40", "attenTol=40", t => t.AttenuationTolerancePercent = 40);
            Add("attOff", "useAttenuationReference=false (cal gate bypass)", t => t.UseAttenuationReference = false);
            Add("pwOff", "usePulseWidthComparison=false (FWHM gate bypass)", t => t.UsePulseWidthComparison = false);
            Add("pw80", "pulseWidthError=80", t => t.PulseWidthErrorPercent = 80);
            Add("supp0", "reflectionSuppression=0", t => t.MultipleReflectionSuppressionCount = 0);
            Add("filtOn", "linear filter on under thr2", t => t.ApplyLinearFilter = true);
            Add("quadAll", "phaseQuadrants all-true (explicit)", t => { });
            Add("combo_pair", "corr50+atten40+attOff+pwOff+supp0", t =>
            {
                t.MinimumCorrelationPercent = 50;
                t.AttenuationTolerancePercent = 40;
                t.UseAttenuationReference = false;
                t.UsePulseWidthComparison = false;
                t.MultipleReflectionSuppressionCount = 0;
            });
            Add("combo_soft", "corr60+atten25+pwOff+filtOn", t =>
            {
                t.MinimumCorrelationPercent = 60;
                t.AttenuationTolerancePercent = 25;
                t.UsePulseWidthComparison = false;
                t.ApplyLinearFilter = true;
            });
            Add("combo_cal", "corr50+atten40+pwOff (keep attRef for fixed cal)", t =>
            {
                t.MinimumCorrelationPercent = 50;
                t.AttenuationTolerancePercent = 40;
                t.UsePulseWidthComparison = false;
            });
            Add("algo7_pair", "Project algo7 + combo_pair", t =>
            {
                t.AlgorithmIndex = 7;
                t.MinimumCorrelationPercent = 50;
                t.AttenuationTolerancePercent = 40;
                t.UseAttenuationReference = false;
                t.UsePulseWidthComparison = false;
                t.MultipleReflectionSuppressionCount = 0;
            });
            return list;
        }
    }
}
