using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Xiongxin.OWTS.Data
{
    // A phase reference belongs to one cropped record, never to the global UI frequency.
    internal sealed class LocationCycleReference
    {
        internal const string HeaderKey = "HVX_CycleReference\t";
        internal double[] CrossingSeconds;
        internal bool FirstCrossingPositive;
        internal double SampleRateHz;
        internal int SampleCount;
        internal double CaptureStartSeconds;
        // Version 2 retains an integer-millisecond acquired span. Samples after
        // the final measured crossing have unknown phase, never extrapolated.
        internal double SavedDurationSeconds;
        // Optional acquisition provenance. Absence means unknown/legacy, never
        // an implicit assertion that the stored samples were high-pass filtered.
        internal string PdAcquisitionFilterId = String.Empty;
        internal const string PdAcquisitionFilterScope = "full-segment-before-cycle-crop";
#if HYBRID_CORE
        private readonly object nativeGate = new object();
        private volatile HybridPhaseCore.CycleSession nativeSession;
        private HybridPhaseCore.CycleSession NativeSession()
        {
            var current = nativeSession;
            int phaseSamples=PhaseSampleCount;
            if (current != null && current.Matches(SampleRateHz, phaseSamples, CrossingSeconds, FirstCrossingPositive)) return current;
            lock (nativeGate)
            {
                current = nativeSession;
                if (current == null || !current.Matches(SampleRateHz, phaseSamples, CrossingSeconds, FirstCrossingPositive))
                    nativeSession = current = new HybridPhaseCore.CycleSession(SampleRateHz, phaseSamples, CrossingSeconds, FirstCrossingPositive);
                return current;
            }
        }
        // Bind once for one analysis/review operation. The ordinary PhaseAt API
        // below retains full mutation detection; only the operation's immutable
        // session bypasses per-candidate Matches and interop overhead.
        internal HybridPhaseCore.CycleSession AcquireHybridPhaseSession() { return NativeSession(); }
#endif
        internal double InitialPhaseDegrees { get { return PhaseAt(0); } }
        internal int CycleCount { get { return CrossingSeconds == null ? 0 : (CrossingSeconds.Length - 1) / 2; } }
        int PhaseSampleCount { get { return SavedDurationSeconds>0?(int)Math.Floor(CrossingSeconds[CrossingSeconds.Length-1]*SampleRateHz+1e-9)+1:SampleCount; } }
        internal double AverageFrequencyHz { get { return (CrossingSeconds.Length - 1) / (2 * (CrossingSeconds[CrossingSeconds.Length - 1] - CrossingSeconds[0])); } }

        internal double PhaseAt(double sampleIndex)
        {
#if HYBRID_CORE
            if (!Finite(sampleIndex) || sampleIndex < 0 || sampleIndex >= SampleCount) return Double.NaN;
            return NativeSession().PhaseAt(sampleIndex);
#else
            if (!Finite(sampleIndex) || sampleIndex < 0 || sampleIndex >= SampleCount) return Double.NaN;
            double t = sampleIndex / SampleRateHz;
            if (t < CrossingSeconds[0] - 1e-12 || t > CrossingSeconds[CrossingSeconds.Length - 1] + 1e-12) return Double.NaN;
            int lo = 0, hi = CrossingSeconds.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (CrossingSeconds[mid] <= t) lo = mid; else hi = mid; }
            double phase = (FirstCrossingPositive ? 0 : 180) + 180 * (lo + (t - CrossingSeconds[lo]) / (CrossingSeconds[hi] - CrossingSeconds[lo]));
            phase %= 360; return phase < 0 ? phase + 360 : phase;
#endif
        }

        internal double[] PhasesAt(double[] sampleIndices)
        {
            if (sampleIndices == null || sampleIndices.Length > 16000000) throw new ArgumentException("Invalid cycle phase batch.");
#if HYBRID_CORE
            var result = NativeSession().PhasesAt(sampleIndices);
            // The native reference can include a virtual endpoint needed by its
            // complete-cycle contract; it is not an acquired/saved PD sample.
            for (int i = 0; i < result.Length; i++)
                if (!Finite(sampleIndices[i]) || sampleIndices[i] < 0 || sampleIndices[i] >= SampleCount)
                    result[i] = Double.NaN;
            return result;
#else
            var result = new double[sampleIndices.Length];
            for (int i = 0; i < result.Length; i++) result[i] = PhaseAt(sampleIndices[i]);
            return result;
#endif
        }

        internal string HeaderLine()
        {
            Validate();
            var record = new Dictionary<string, object> {
                { "schema", SavedDurationSeconds>0?"HVX.LocationCycles/2":"HVX.LocationCycles/1" }, { "sampleRateHz", SampleRateHz }, { "sampleCount", SampleCount }, { "cycleCount", CycleCount },
                { "captureStartSeconds", CaptureStartSeconds }, { "firstCrossingPositive", FirstCrossingPositive },
                { "crossingSeconds", CrossingSeconds }
            };
            if(SavedDurationSeconds>0)record.Add("savedDurationSeconds",SavedDurationSeconds);
            if (!String.IsNullOrEmpty(PdAcquisitionFilterId))
            {
                record.Add("pdAcquisitionFilterId", PdAcquisitionFilterId);
                record.Add("pdAcquisitionFilterScope", PdAcquisitionFilterScope);
            }
            return HeaderKey + new JavaScriptSerializer().Serialize(record);
        }

        internal static LocationCycleReference ParseHeader(string[] lines, int sampleCount, double sampleRateHz)
        {
            string json = null;
            for (int i = 0; i < Math.Min(11, lines == null ? 0 : lines.Length); i++)
            {
                if (!lines[i].StartsWith(HeaderKey, StringComparison.Ordinal)) continue;
                if (json != null) throw new InvalidDataException("Duplicate LOC cycle reference.");
                json = lines[i].Substring(HeaderKey.Length);
            }
            if (json == null) return null; // Legacy files retain their original phase contract.
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 8 }.Deserialize<Dictionary<string, object>>(json);
                string schema=(string)data["schema"];
                if (schema != "HVX.LocationCycles/1"&&schema!="HVX.LocationCycles/2") throw new InvalidDataException("Unsupported LOC cycle reference.");
                var values = (System.Collections.IEnumerable)data["crossingSeconds"];
                var times = new List<double>(); foreach (object value in values) times.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                var result = new LocationCycleReference {
                    SampleRateHz = Convert.ToDouble(data["sampleRateHz"], CultureInfo.InvariantCulture),
                    SampleCount = Convert.ToInt32(data["sampleCount"], CultureInfo.InvariantCulture),
                    CaptureStartSeconds = Convert.ToDouble(data["captureStartSeconds"], CultureInfo.InvariantCulture),
                    FirstCrossingPositive = (bool)data["firstCrossingPositive"], CrossingSeconds = times.ToArray()
                };
                if(schema=="HVX.LocationCycles/2"){
                    result.SavedDurationSeconds=Convert.ToDouble(data["savedDurationSeconds"],CultureInfo.InvariantCulture);
                    if(!Finite(result.SavedDurationSeconds)||result.SavedDurationSeconds<=0)throw new InvalidDataException("Missing integer-millisecond span.");
                }
                else if(data.ContainsKey("savedDurationSeconds"))throw new InvalidDataException("Unexpected saved-span field.");
                object filterId;
                if (data.TryGetValue("pdAcquisitionFilterId", out filterId))
                {
                    object scope;
                    if (!(filterId is string) || String.IsNullOrEmpty((string)filterId) ||
                        !data.TryGetValue("pdAcquisitionFilterScope", out scope) ||
                        !Object.Equals(scope, PdAcquisitionFilterScope))
                        throw new InvalidDataException("Invalid LOC acquisition filter provenance.");
                    result.PdAcquisitionFilterId = (string)filterId;
                }
                else if (data.ContainsKey("pdAcquisitionFilterScope"))
                    throw new InvalidDataException("LOC acquisition filter scope has no filter identity.");
                result.Validate();
                if (data.ContainsKey("cycleCount") && Convert.ToDouble(data["cycleCount"], CultureInfo.InvariantCulture) != result.CycleCount)
                    throw new InvalidDataException("LOC cycle count does not match its actual crossing references.");
                if (result.SampleCount != sampleCount || !Finite(sampleRateHz) || sampleRateHz <= 0 ||
                    Math.Abs(result.SampleRateHz - sampleRateHz) > 1e-9 * sampleRateHz)
                    throw new InvalidDataException("LOC cycle reference does not match its samples/rate.");
                return result;
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException) throw;
                throw new InvalidDataException("Invalid LOC cycle reference; phase must not fall back to a fixed frequency.", ex);
            }
        }

        internal void Validate()
        {
            if (PdAcquisitionFilterId == null || PdAcquisitionFilterId.Length > 128)
                throw new InvalidDataException("Invalid LOC acquisition filter identity.");
            foreach (char value in PdAcquisitionFilterId)
                if (Char.IsControl(value)) throw new InvalidDataException("Invalid LOC acquisition filter identity.");
            if (!Finite(SampleRateHz) || SampleRateHz <= 0 || SampleCount < 16 || SampleCount > 16000000 ||
                !Finite(CaptureStartSeconds) || CaptureStartSeconds < 0 || CrossingSeconds == null ||
                CrossingSeconds.Length < 3 || CrossingSeconds.Length > 201 || CrossingSeconds.Length % 2 != 1)
                throw new InvalidDataException("Invalid LOC cycle-reference dimensions.");
            for (int i = 0; i < CrossingSeconds.Length; i++)
                if (!Finite(CrossingSeconds[i]) || (i > 0 && CrossingSeconds[i] <= CrossingSeconds[i - 1]))
                    throw new InvalidDataException("LOC crossings must be finite and strictly increasing.");
            if(SavedDurationSeconds!=0){
                double milliseconds=SavedDurationSeconds*1000;
                if(!Finite(SavedDurationSeconds)||SavedDurationSeconds<.001||SavedDurationSeconds>.060||
                    Math.Abs(milliseconds-Math.Round(milliseconds))>1e-8||
                    Math.Abs(SampleCount-SavedDurationSeconds*SampleRateHz)>1.000001||
                    CrossingSeconds[0]>1e-12||CrossingSeconds[0]<-1/SampleRateHz-1e-12||
                    CrossingSeconds[CrossingSeconds.Length-1]>SavedDurationSeconds+1e-12||PhaseSampleCount<16)
                    throw new InvalidDataException("Invalid integer-millisecond LOC span or measured phase reference.");
                return;
            }
            // Integer sample cropping may start <1 sample after the fractional crossing.
            if (CrossingSeconds[0] > 1e-12 || CrossingSeconds[0] < -1 / SampleRateHz - 1e-12 ||
                CrossingSeconds[CrossingSeconds.Length - 1] < (SampleCount - 1) / SampleRateHz - 1e-12 ||
                CrossingSeconds[CrossingSeconds.Length - 1] >= SampleCount / SampleRateHz + 1e-12)
                throw new InvalidDataException("LOC samples do not cover the stated complete cycle window.");
        }
        internal static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
    }

    internal sealed class OscillationCycleWindow
    {
        internal double[] CrossingSeconds;
        internal bool FirstCrossingPositive;
        internal string FailureCode, FailureReason;
        internal double SavedDurationSeconds;
        internal int CycleCount { get { return CrossingSeconds == null ? 0 : (CrossingSeconds.Length - 1) / 2; } }
        internal bool Success { get { return String.IsNullOrEmpty(FailureCode); } }

        internal static OscillationCycleWindow Find(double[] voltage, double rateHz, int cycles, double expectedPeakKv)
        {
            if (cycles < 1 || cycles > 100) throw new ArgumentOutOfRangeException("cycles");
            if (!LocationCycleReference.Finite(rateHz) || rateHz <= 0) throw new ArgumentOutOfRangeException("rateHz");
            if (!LocationCycleReference.Finite(expectedPeakKv) || expectedPeakKv < 0) throw new ArgumentOutOfRangeException("expectedPeakKv");
            if (voltage == null || voltage.Length < 3) return Failed("NO_REFERENCE", "未获得有效电压参考波形 / No voltage reference waveform.");
            double peak = 0;
            foreach (double v in voltage) {
                if (!LocationCycleReference.Finite(v)) return Failed("INVALID_REFERENCE", "电压参考含无效样本 / Non-finite voltage reference.");
                peak = Math.Max(peak, Math.Abs(v));
            }
            if (peak <= Math.Max(1e-9, Math.Abs(expectedPeakKv) * .01))
                return Failed("NO_EFFECTIVE_VOLTAGE", "未检测到足够的电压参考信号 / Insufficient voltage reference signal.");
            // Hysteresis plus two actual consecutive opposite-polarity samples
            // reject a single-sample bounce. Keep the FIRST actual zero bracket
            // while that transition is being resolved; a one-sample return to
            // the old polarity must not shift the measured boundary. This is
            // qualification of raw samples, not waveform smoothing or fitting.
            double hysteresis = peak * .01;
            var crossings = new List<double>();
            int stableSign = 0, lastNonzero = -1;
            int oppositeRun = 0, returnRun = 0;
            bool oppositeRunResolved = false, returnRunResolved = false;
            double pendingCrossing = Double.NaN;
            bool firstPositive = false, unresolvedTransition = false;
            for (int i = 0; i < voltage.Length; i++)
            {
                double v = voltage[i]; int sign = Math.Sign(v);
                if (stableSign != 0 && sign == -stableSign && lastNonzero >= 0 &&
                    Math.Sign(voltage[lastNonzero]) == stableSign && Double.IsNaN(pendingCrossing))
                {
                    double left = voltage[lastNonzero];
                    double scale = Math.Max(Math.Abs(left), Math.Abs(v));
                    pendingCrossing = (lastNonzero + (i - lastNonzero) * (Math.Abs(left)/scale) / (Math.Abs(left)/scale + Math.Abs(v)/scale)) / rateHz;
                }
                if (stableSign == 0 && Math.Abs(v) >= hysteresis)
                    stableSign = sign;
                else if (stableSign != 0 && LocationCycleReference.Finite(pendingCrossing))
                {
                    if (sign == -stableSign) {
                        ++oppositeRun; returnRun = 0; returnRunResolved = false;
                        oppositeRunResolved |= Math.Abs(v) >= hysteresis;
                    }
                    else if (sign == stableSign) {
                        ++returnRun; oppositeRun = 0; oppositeRunResolved = false;
                        returnRunResolved |= Math.Abs(v) >= hysteresis;
                    }
                    else { oppositeRun = returnRun = 0; oppositeRunResolved = returnRunResolved = false; }
                    unresolvedTransition = true;
                    if (oppositeRun >= 2 && oppositeRunResolved)
                    {
                        if (crossings.Count == 0) firstPositive = sign > 0;
                        // Adjacent half cycles need at least two actual samples, not duplicate zero chatter.
                        // Seconds-to-samples round trips can put an exact two-sample
                        // spacing a few ulps below 2. Preserve the boundary without
                        // accepting a materially shorter half cycle.
                        if (crossings.Count > 0 && (pendingCrossing - crossings[crossings.Count - 1]) * rateHz < 2 - 1e-9)
                            return Failed("UNRESOLVED_CROSSINGS", "过零间隔过短，参考采样不足 / Unresolved zero crossings.");
                        crossings.Add(pendingCrossing);
                        if (crossings.Count == 2 * cycles + 1)
                            return new OscillationCycleWindow { CrossingSeconds = crossings.ToArray(), FirstCrossingPositive = firstPositive };
                        stableSign = sign;
                        pendingCrossing = Double.NaN;
                        oppositeRun = returnRun = 0;
                        oppositeRunResolved = returnRunResolved = false;
                        unresolvedTransition = false;
                    }
                    else if (returnRun >= 2 && returnRunResolved)
                    {
                        // The old polarity itself must be re-established before
                        // discarding the pending bracket, not merely one bounce.
                        pendingCrossing = Double.NaN;
                        oppositeRun = returnRun = 0;
                        oppositeRunResolved = returnRunResolved = false;
                        unresolvedTransition = false;
                    }
                }
                if (sign != 0) lastNonzero = i;
            }
            if (crossings.Count == 0 && unresolvedTransition)
                return Failed("UNRESOLVED_CROSSINGS", "过零极性无法由连续原始样本确认 / Unresolved zero-crossing polarity.");
            return crossings.Count == 0 ? Failed("NO_ZERO_CROSSING", "未检出有效过零点 / No effective zero crossing.") :
                Failed("INCOMPLETE_CYCLES", "采集数据不足指定的完整振荡周期 / Acquisition does not contain the requested complete cycles.");
        }

        // Separate from strict Find: select only real, complete cycles within
        // both streams' acquired sample times. Never pad or extrapolate a tail.
        internal static OscillationCycleWindow FindUpTo(double[] voltage, double voltageRate, int maximumCycles,
            int pdCount, double pdRate, double pdStartSeconds, double expectedPeakKv)
        {
            if (maximumCycles < 1 || maximumCycles > 5) throw new ArgumentOutOfRangeException("maximumCycles");
            if (!LocationCycleReference.Finite(pdRate) || pdRate <= 0 || pdCount < 16 || pdCount > 16000000 ||
                !LocationCycleReference.Finite(pdStartSeconds) || pdStartSeconds < 0)
                return Failed("NO_PD_TIMEBASE", "局放采样时间基准或记录范围无效 / Invalid PD time base or record extent.");
            var first = Find(voltage, voltageRate, 1, expectedPeakKv);
            if (!first.Success) return first;
            double commonEnd = Math.Min((voltage.Length - 1) / voltageRate, pdStartSeconds + (pdCount - 1) / pdRate);
            if (!LocationCycleReference.Finite(commonEnd) || first.CrossingSeconds[0] < pdStartSeconds - 1e-12 ||
                first.CrossingSeconds[2] > commonEnd + 1e-12)
                return Failed("INCOMPLETE_CYCLES", "两路真实采样共同范围不足一个完整周期 / Shared acquired interval does not contain one complete cycle.");
            var selected = first;
            for (int cycles = 2; cycles <= maximumCycles; ++cycles)
            {
                var next = Find(voltage, voltageRate, cycles, expectedPeakKv);
                if (!next.Success || next.CrossingSeconds[next.CrossingSeconds.Length - 1] > commonEnd + 1e-12) break;
                selected = next;
            }
            int firstSample = (int)Math.Ceiling((selected.CrossingSeconds[0] - pdStartSeconds) * pdRate - 1e-9);
            int lastSample = (int)Math.Floor((selected.CrossingSeconds[selected.CrossingSeconds.Length - 1] - pdStartSeconds) * pdRate + 1e-9);
            if (firstSample < 0 || lastSample >= pdCount || lastSample - firstSample + 1 < 16)
                return Failed("SHORT_SPAN", "完整周期内局放真实样本不足 / Too few acquired PD samples in the complete cycle window.");
            return selected;
        }

        internal static OscillationCycleWindow FindWholeMilliseconds(double[] voltage,double voltageRate,int pdCount,double pdRate,double expectedPeakKv)
        {
            var first=Find(voltage,voltageRate,1,expectedPeakKv);
            if(!first.Success)return first;
            if(!LocationCycleReference.Finite(pdRate)||pdRate<=0||pdCount<16)return Failed("NO_PD_TIMEBASE","局放采样时间基准无效。");
            double end=Math.Min((voltage.Length-1)/voltageRate,(pdCount-1)/pdRate);
            double duration=Math.Floor((end-first.CrossingSeconds[0])*1000+1e-9)/1000;
            if(duration<.001)return Failed("SHORT_SPAN","首个过零点后的有效数据不足1 ms。");
            double limit=first.CrossingSeconds[0]+duration;
            if(first.CrossingSeconds[first.CrossingSeconds.Length-1]>limit+1e-12)return Failed("INCOMPLETE_CYCLES","整毫秒范围内不足一个有效振荡周期。");
            var selected=first;
            for(int cycles=2;cycles<=100;cycles++){
                var next=Find(voltage,voltageRate,cycles,expectedPeakKv);
                if(!next.Success||next.CrossingSeconds[next.CrossingSeconds.Length-1]>limit+1e-12)break;
                selected=next;
            }
            selected.SavedDurationSeconds=duration;return selected;
        }

        internal double[] Crop(double[] samples, double sampleRateHz, double streamStartSeconds, out LocationCycleReference reference)
        {
            reference = null;
            if (!Success) throw new InvalidDataException(FailureReason);
            if (samples == null || !LocationCycleReference.Finite(sampleRateHz) || sampleRateHz <= 0 || !LocationCycleReference.Finite(streamStartSeconds))
                throw new ArgumentException("Invalid location sample stream or alignment.");
            double start = (CrossingSeconds[0] - streamStartSeconds) * sampleRateHz;
            double end = ((SavedDurationSeconds>0?CrossingSeconds[0]+SavedDurationSeconds:CrossingSeconds[CrossingSeconds.Length - 1]) - streamStartSeconds) * sampleRateHz;
            // No resampling, padding, repetition or extrapolation of PD samples.
            if (start < -1e-7 || end > samples.Length - 1 + 1e-7)
                throw new InvalidDataException("定位数据没有覆盖完整周期 / Location samples do not cover the full cycle window.");
            int first = (int)Math.Ceiling(start - 1e-9), last = SavedDurationSeconds>0?(int)Math.Ceiling(end-1e-9)-1:(int)Math.Floor(end + 1e-9);
            int count = last - first + 1;
            if (count < 16 || count > 16000000) throw new InvalidDataException("LOC cycle sample count is outside 16..16000000.");
            var copy = new double[count]; Array.Copy(samples, first, copy, 0, count);
            foreach (double value in copy) if (!LocationCycleReference.Finite(value) || Math.Abs(value) > 1e100)
                throw new InvalidDataException("Invalid location charge sample.");
            double origin = streamStartSeconds + first / sampleRateHz;
            var times = new double[CrossingSeconds.Length];
            for (int i = 0; i < times.Length; i++) times[i] = CrossingSeconds[i] - origin;
            reference = new LocationCycleReference { SampleRateHz = sampleRateHz, SampleCount = count,
                CaptureStartSeconds = origin, FirstCrossingPositive = FirstCrossingPositive, CrossingSeconds = times,SavedDurationSeconds=SavedDurationSeconds };
            reference.Validate(); return copy;
        }
        private static OscillationCycleWindow Failed(string code, string reason) { return new OscillationCycleWindow { FailureCode = code, FailureReason = reason }; }
    }
}
