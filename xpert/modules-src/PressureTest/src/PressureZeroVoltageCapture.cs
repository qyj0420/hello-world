using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;
using HVX.SignalProcessing;

namespace HVX.Controls
{
    // Driver-free adapter for the separately owned Task39 input worker. It cannot
    // energize a source, enumerate devices, or weaken the worker's 0 V restriction.
    internal static class PressureZeroVoltageCapture
    {
        internal static BenchSettings ReadSettings(string xml)
        {
            if (String.IsNullOrWhiteSpace(xml) || xml.Length > 65536) throw new ArgumentException("A bounded, explicit Task39 settings XML is required.");
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
            {
                var settings = (BenchSettings)new XmlSerializer(typeof(BenchSettings)).Deserialize(reader);
                settings.Validate();
                if (!settings.RealHardware) throw new ArgumentException("The live backend requires real, explicitly identified devices; no simulation substitution is allowed.");
                if (settings.TargetVoltageV != 0) throw new ArgumentException("This backend supports zero-voltage input capture only.");
                return settings;
            }
        }

        internal static string SettingsXml(BenchSettings settings)
        {
            settings.Validate();
            using (var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            { new XmlSerializer(typeof(BenchSettings)).Serialize(text, settings); return text.ToString(); }
        }

        internal static BenchSettings Clone(BenchSettings value) { return ReadSettings(SettingsXml(value)); }

        internal static string CaptureDirectory(string project, string returned)
        {
            if (String.IsNullOrWhiteSpace(project) || !Directory.Exists(project) || String.IsNullOrWhiteSpace(returned))
                throw new InvalidDataException("Missing project or raw capture directory.");
            string root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string path = Path.GetFullPath(returned).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
                throw new InvalidDataException("The worker must return a new capture directory inside the locked project.");
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Raw capture paths cannot traverse a reparse point.");
                if (String.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
            }
            foreach (string name in new[] { "settings.xml", "capture.bin" })
            {
                string file = Path.Combine(path, name);
                if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Missing or redirected raw capture file: " + name);
            }
            return path;
        }

        internal static PressureZeroVoltageResult Read(string directory, BenchSettings requested, DateTime startedUtc, CancellationToken cancellation)
        {return Read(directory,requested,startedUtc,cancellation,0,Double.NaN);}

        internal static PressureZeroVoltageResult Read(string directory, BenchSettings requested, DateTime startedUtc, CancellationToken cancellation,double targetKv,double phaseVoltageRms)
        { return Read(directory,requested,startedUtc,cancellation,targetKv,phaseVoltageRms,false); }

        // Initialization needs raw maximum/overflow only, not display arrays,
        // high-pass filtering, noise statistics or voltage diagnostic fitting.
        internal static PressureZeroVoltageResult ReadInitialRange(string directory, BenchSettings requested, DateTime startedUtc, CancellationToken cancellation)
        { return Read(directory,requested,startedUtc,cancellation,0,Double.NaN,true); }

        static PressureZeroVoltageResult Read(string directory, BenchSettings requested, DateTime startedUtc, CancellationToken cancellation,double targetKv,double phaseVoltageRms,bool rawRangeOnly)
        {
            cancellation.ThrowIfCancellationRequested();
            // Keep the actual settings and ADC files non-writable throughout validation.
            using (var settingsLock = new FileStream(Path.Combine(directory, "settings.xml"), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var rawLock = new FileStream(Path.Combine(directory, "capture.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var capture = BenchCapture.Load(directory);
                ApplyFactoryPressRangeFeed(directory, requested, capture);
                foreach (var field in typeof(BenchSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (!Object.Equals(field.GetValue(requested), field.GetValue(capture.Settings)))
                        throw new InvalidDataException("Capture configuration changed during acquisition: " + field.Name);
                if (capture.Simulation || capture.CapturedUtc < startedUtc || capture.CapturedUtc > DateTime.UtcNow)
                    throw new InvalidDataException("The worker returned simulated, previous-session, or future-dated data.");
                cancellation.ThrowIfCancellationRequested();
                var window = LocationRangeWindow.Select(0, 0, capture.PdAdc.Length, capture.PicoRateHz, 0, Double.NaN, Double.NaN, false);
                var quality = LocationRangeAdvisor.Evaluate(capture.PdAdc, window, capture.MaximumAdc,
                    capture.PicoFullScaleV, requested.RangeIndex, requested.CalibrationPulsePc, requested.PdCalibrationPcPerV, capture.Overflow);
                var result = new PressureZeroVoltageResult { Capture = capture, Quality = quality, Directory = directory };
                if(rawRangeOnly) {
                    using(var hash=SHA256.Create()) result.RawSha256=BitConverter.ToString(hash.ComputeHash(rawLock)).Replace("-", "");
                    cancellation.ThrowIfCancellationRequested();
                    return result;
                }
                result.VoltageKv = new double[capture.VoltageRaw.Length];
                result.ChargePc = new double[capture.PdAdc.Length];
                for (int i = 0; i < result.VoltageKv.Length; i++)
                {
                    double value = capture.VoltageRaw[i] * (requested.DividerVPerV / 1000);
                    if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("Voltage conversion is not finite.");
                    result.VoltageKv[i] = value;
                }
                double scale = capture.PicoFullScaleV / capture.MaximumAdc * requested.PdCalibrationPcPerV;
                // Remove ADC DC before any PD conversion/filter/averaging. The
                // histogram median is robust to sparse pulses; original ADC and
                // clipping checks above remain untouched. Never apply to voltage.
                var histogram=new int[65536];
                foreach(short sample in capture.PdAdc)histogram[sample-Int16.MinValue]++;
                int cumulative=0,median=0;
                for(int bin=0;bin<histogram.Length;bin++){cumulative+=histogram[bin];if(cumulative>capture.PdAdc.Length/2){median=bin+Int16.MinValue;break;}}
                result.RemovedDcVolts=median*capture.PicoFullScaleV/capture.MaximumAdc;
                for (int i = 0; i < result.ChargePc.Length; i++)
                {
                    if ((i & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                    double value = (capture.PdAdc[i]-median) * scale;
                    if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("Charge conversion is not finite.");
                    result.ChargePc[i] = value;
                }
                // The pressure page consumes original ADC, not the factory
                // envelope. Apply the same linear-phase filter as calibration
                // once to the full segment, before any location-cycle crop.
                // Range/overflow evidence above continues to use native ADC.
                result.ChargePc=PdLinearHighPass.ApplyCancellable(result.ChargePc,capture.PicoRateHz,cancellation);
                result.HighPassFilterId=PdLinearHighPass.FilterId;
                for(int i=0;i<result.ChargePc.Length;i++) {
                    if((i&65535)==0)cancellation.ThrowIfCancellationRequested();
                    result.PeakPc=Math.Max(result.PeakPc,Math.Abs(result.ChargePc[i]));
                }
                result.MeasurementPeakPc=targetKv>0?PressureMetricWindow.PeakAfterFirstCrossing(result.VoltageKv,capture.ArtRateHz,result.ChargePc,capture.PicoRateHz):result.PeakPc;
                bool tanEligible=targetKv>0&&PressureMetricWindow.TanDeltaEligible(result.VoltageKv,capture.ArtRateHz,phaseVoltageRms);
                result.VoltageMetrics=PressureTestAnalysis.Analyze(result.VoltageKv,new double[0],capture.ArtRateHz,requested.InductanceH,PdPolarity.Both,
                    new PressureTestAnalysisOptions{SkipDissipationFactor=!tanEligible});
                using (var hash = SHA256.Create()) result.RawSha256 = BitConverter.ToString(hash.ComputeHash(rawLock)).Replace("-", "");
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
        }

        // Factory-press rounds may settle a Pico range inside RunFactoryAlignedPress.
        // The workbench still compares every BenchSettings field, so the in-memory
        // request adopts the recorded range before that comparison. E+ math is unchanged.
        static void ApplyFactoryPressRangeFeed(string directory, BenchSettings requested, BenchCapture capture)
        {
            string manifest = Path.Combine(directory, "factory-press-manifest.json");
            if (!File.Exists(manifest)) return;
            string text = File.ReadAllText(manifest);
            Match match = Regex.Match(text, "\"rangeIndexUsed\"\\s*:\\s*(-?\\d+)");
            if (!match.Success) return;
            int used;
            if (!Int32.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out used)
                || used < 0 || used >= BenchSettings.Ranges.Length)
                throw new InvalidDataException("出厂加压量程索引无效。");
            if (capture.Settings == null || capture.Settings.RangeIndex != used)
                throw new InvalidDataException("出厂加压记录的量程与清单不一致。");
            requested.RangeIndex = used;
        }

        internal static bool NeedsDecision(BenchSettings settings, LocationRangeResult quality)
        {
            // The maximum range has no adjustment UI. Its unsafe/incomplete
            // evidence is held separately by the owner, never auto-accepted.
            if(settings.RangeIndex==BenchSettings.Ranges.Length-1)return false;
            if(PressureChargeRangeFloor.IsLowOnlyWithoutAdjustment(quality,settings.RangeIndex))return false;
            bool maximumRisk = settings.RangeIndex == BenchSettings.Ranges.Length - 1 &&
                (quality.LocationClippedSamples > 0 || quality.LocationHighOccupancy || quality.DriverOverflowUncertain || quality.DriverOverflow);
            return maximumRisk || quality.ExpectedCalibrationVolts > BenchSettings.Ranges[BenchSettings.Ranges.Length - 1] ||
                settings.AutoRange && (quality.Change || !quality.LocationQualityPassed || quality.MaximumRangeInsufficient);
        }

        internal static bool ShouldWarnSmallReference(PressureZeroVoltageResult result)
        {
            var quality=result==null?null:result.Quality;
            return quality!=null&&quality.WindowValid&&result.ChargePc!=null&&result.ChargePc.Length>0&&
                result.PeakPc>0&&result.PeakPc<1&&!Double.IsNaN(result.PeakPc)&&!Double.IsInfinity(result.PeakPc)&&
                quality.LocationClippedSamples==0&&quality.FullClippedSamples==0&&quality.OutsideClippedSamples==0&&
                !quality.LocationHighOccupancy&&!quality.DriverOverflow&&!quality.DriverOverflowUncertain&&
                !quality.RecommendationUncertain&&!quality.MaximumRangeInsufficient&&!quality.AtMaximumAndClipped;
        }

        internal static void WriteEvidence(PressureZeroVoltageResult result, string acquisitionId, string phase, string calibrationSource, RangeDecisionRecord decision,double targetKv=0,bool analyzeZeroVoltageData=false)
        {
            var record = new PressureZeroVoltageEvidence { AcquisitionId = acquisitionId, Phase = phase, CalibrationSource = calibrationSource,
                RawSha256 = result.RawSha256, CapturedUtc = result.Capture.CapturedUtc, Quality = result.Quality, OriginalRangeQuality=result.OriginalRangeQuality, Decision = decision,
                ArtSerial = result.Capture.ArtSerial, PicoSerial = result.Capture.PicoSerial,
                ArtSampleRateHz = result.Capture.ArtRateHz, PicoSampleRateHz = result.Capture.PicoRateHz, RemovedDcVolts=result.RemovedDcVolts,
                HighPassFilterId=result.HighPassFilterId,AnalyzeZeroVoltageData=targetKv==0&&analyzeZeroVoltageData,
                Schema=targetKv==0?"HVX.Pressure.ZeroVoltage/1":"HVX.Pressure.CaptureQuality/1",TargetPeakKv=targetKv,
                DisplayPreparationMilliseconds=result.DisplayPreparationMilliseconds,RangeReviewMilliseconds=result.RangeReviewMilliseconds,
                PostReadPreparationMilliseconds=result.PostReadPreparationMilliseconds };
            string path = Path.Combine(result.Directory, decision == null ? "pressure-range-quality.xml" : "pressure-range-decision.xml");
            // Publish a complete new derived record, never rewrite original data
            // or an earlier decision. Failure leaves the raw round recoverable.
            string temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    new XmlSerializer(typeof(PressureZeroVoltageEvidence)).Serialize(stream, record);
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public sealed class PressureZeroVoltageEvidence
    {
        public string Schema = "HVX.Pressure.ZeroVoltage/1";
        public string AcquisitionId, Phase, CalibrationSource, RawSha256, ArtSerial, PicoSerial;
        public DateTime CapturedUtc;
        public double ArtSampleRateHz, PicoSampleRateHz;
        public double RemovedDcVolts;
        public string HighPassFilterId;
        public double TargetPeakKv;
        public bool IncludedInDischargeStatistics = false;
        public bool AnalyzeZeroVoltageData = false;
        public bool ProvesHighVoltageSafe = false;
        // Monotonic CPU/task elapsed time, not a physical voltage or release measurement.
        public long DisplayPreparationMilliseconds, RangeReviewMilliseconds, PostReadPreparationMilliseconds;
        public LocationRangeResult Quality;
        public LocationRangeResult OriginalRangeQuality;
        public RangeDecisionRecord Decision;
    }
    internal sealed class PressureZeroVoltageResult
    {
        internal PressureRangeReviewSnapshot RangeReview;
        internal BenchCapture Capture;
        internal LocationRangeResult Quality;
        internal LocationRangeResult OriginalRangeQuality;
        internal string Directory, RawSha256;
        internal double[] VoltageKv, ChargePc;
        internal double RemovedDcVolts;
        internal string HighPassFilterId;
        internal double PeakPc;
        internal double MeasurementPeakPc;
        internal PressureTestAnalysisResult VoltageMetrics;
        internal long DisplayPreparationMilliseconds, RangeReviewMilliseconds, PostReadPreparationMilliseconds;
    }
}
