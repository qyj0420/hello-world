using System;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryFinalAi4ObservationTests
    {
        internal static string Run()
        {
            int count = 0;
            Action<bool,string> check = (ok, message) => {
                if (!ok) throw new Exception("Final AI4 observation: " + message);
                count++;
            };
            var start = new DateTime(2026,10,8,0,0,0,DateTimeKind.Utc);
            Func<double[],FactoryFinalAi4Observation> evaluate = raw =>
                FactoryFinalAi4Observation.Evaluate(raw,80000,1000,false,start,start.AddMilliseconds(12));
            foreach (double voltage in new[] { 999.0, -999.0, 1000.0, -1000.0 })
            {
                var result = evaluate(Constant(voltage / 1000));
                check(result.Requested && result.Available && result.SampleCount == 800, "complete raw window " + voltage);
                check(Near(result.MeanV,voltage) && Near(result.AbsoluteMeanV,Math.Abs(voltage)), "signed mean and abs(mean) " + voltage);
                check(result.AbsMeanBelowThreshold == (Math.Abs(voltage) < 1000), "strict threshold equality " + voltage);
                check(Near(result.AbsolutePeakV,Math.Abs(voltage)) && Near(result.RmsV,Math.Abs(voltage)), "peak/RMS " + voltage);
            }
            var bipolar = Constant(2);
            for (int i = 0; i < bipolar.Length; i += 2) bipolar[i] = -2;
            var cancelled = evaluate(bipolar);
            check(cancelled.Available && cancelled.AbsMeanBelowThreshold && Near(cancelled.MeanV,0), "signed cancellation is mean-only evidence");
            check(Near(cancelled.AbsolutePeakV,2000) && Near(cancelled.RmsV,2000), "opposing high samples retain peak/RMS");
            check(cancelled.Describe().Contains("不是全部样点持续低于阈值") && cancelled.Describe().Contains("不作为中间轮或下一轮加压许可"), "cancellation limitation is explicit");
            bipolar[0] = 8;
            check(cancelled.RawVolts[0] == -2, "raw input is cloned, not aliased");
            var zero = evaluate(Constant(0));
            check(zero.Available && zero.AbsMeanBelowThreshold && zero.Detail.Contains("不证明接线有效"), "zero input not wiring/discharge evidence");

            foreach (double[] invalid in new[] { (double[])null, new double[0], new double[799], new double[801] })
                VerifyInvalid(evaluate(invalid),check,"missing or incomplete exact block");
            foreach (double value in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, 9.95, -9.95, 10.0, -10.0 })
            {
                var raw = Constant(0); raw[387] = value;
                var invalid = evaluate(raw);
                VerifyInvalid(invalid,check,"invalid/saturated single sample " + value);
                check(invalid.RawVolts.Length == 800 && (Double.IsNaN(value) ? Double.IsNaN(invalid.RawVolts[387]) : invalid.RawVolts[387] == value), "invalid raw sample retained");
            }
            check(evaluate(Constant(9.949)).Available, "strict saturation threshold below 9.95 raw V");
            foreach (double rate in new[] { 0.0, -80000.0, Double.NaN, Double.PositiveInfinity, 79999.49, 80000.51 })
                VerifyInvalid(FactoryFinalAi4Observation.Evaluate(Constant(.5),rate,1000,false,start,start.AddMilliseconds(12)),check,"bad actual rate");
            check(FactoryFinalAi4Observation.Evaluate(Constant(.5),79999.5,1000,false,start,start.AddMilliseconds(12)).Available, "documented rate tolerance boundary");
            foreach (double divider in new[] { 0.0, -1.0, Double.NaN, Double.PositiveInfinity, Double.MaxValue })
                VerifyInvalid(FactoryFinalAi4Observation.Evaluate(Constant(2),80000,divider,false,start,start.AddMilliseconds(12)),check,"bad or overflowing ratio");
            VerifyInvalid(FactoryFinalAi4Observation.Evaluate(Constant(.5),80000,1000,false,DateTime.MinValue,start),check,"missing host start");
            VerifyInvalid(FactoryFinalAi4Observation.Evaluate(Constant(.5),80000,1000,false,start,start.AddTicks(-1)),check,"backward host time");
            VerifyInvalid(FactoryFinalAi4Observation.Evaluate(Constant(.5),80000,1000,false,start,start.AddMilliseconds(100).AddTicks(1)),check,"more than 100ms host interval");
            VerifyInvalid(FactoryFinalAi4Observation.Evaluate(Constant(.5),80000,1000,false,DateTime.SpecifyKind(start,DateTimeKind.Unspecified),start.AddMilliseconds(12)),check,"ambiguous host timezone");
            check(FactoryFinalAi4Observation.Evaluate(Constant(.5),80000,1000,false,start,start.AddMilliseconds(100)).Available, "100ms inclusive host bound");
            var simulated = FactoryFinalAi4Observation.Evaluate(Constant(.5),80000,1000,true,start,start.AddMilliseconds(12));
            check(simulated.Simulation && simulated.Describe().StartsWith("仿真"), "simulation stays visibly labelled");
            var initial = new FactoryFinalAi4Observation();
            check(!initial.Requested && !initial.Available && !initial.AbsMeanBelowThreshold && Double.IsNaN(initial.MeanV), "absent/default observation never passes");
            check(initial.Describe().Contains("未请求"), "missing observation has honest display");

            var copy = RoundTrip(cancelled);
            check(copy.Available && copy.Requested && copy.RawVolts.Length == 800 && copy.RawVolts[0] == -2, "raw samples and validity XML roundtrip");
            check(copy.StartedUtc == start && copy.StartedUtc.Kind == DateTimeKind.Utc && copy.MeanV == cancelled.MeanV && copy.RmsV == cancelled.RmsV, "time and derived values XML roundtrip");
            var badCopy = RoundTrip(evaluate(new double[799]));
            check(!badCopy.Available && !badCopy.AbsMeanBelowThreshold && Double.IsNaN(badCopy.MeanV) && !String.IsNullOrEmpty(badCopy.Error), "invalid XML roundtrip cannot turn into a pass");
            using (var reader = new StringReader("<FactoryFinalAi4Observation />"))
            {
                var legacy = (FactoryFinalAi4Observation)new XmlSerializer(typeof(FactoryFinalAi4Observation)).Deserialize(reader);
                check(!legacy.Requested && !legacy.Available && !legacy.AbsMeanBelowThreshold && Double.IsNaN(legacy.MeanV), "missing XML fields remain unrequested and unmeasured");
            }
            var sourceFailure = new InvalidOperationException("test cleanup failure");
            var carried = new FactoryFinalAi4ObservationException(cancelled, sourceFailure);
            check(Object.ReferenceEquals(carried.PartialObservation,cancelled) && Object.ReferenceEquals(carried.InnerException,sourceFailure), "later failure retains already-read raw evidence and original error");
            check(carried.Message.Contains("不代表放电确认") && carried.PartialObservation.RawVolts[0] == -2, "partial observation does not hide failure semantics");
            check(new FactoryFinalAi4ObservationException(null,sourceFailure).PartialObservation == null, "pre-read failure does not fabricate observation");
            return "Final AI4 observation: " + count + " checks PASS; HardwareAccessed=False";
        }

        private static void VerifyInvalid(FactoryFinalAi4Observation value, Action<bool,string> check, string reason)
        {
            check(value.Requested && !value.Available && !value.AbsMeanBelowThreshold &&
                !String.IsNullOrEmpty(value.Error) && Double.IsNaN(value.MeanV), reason);
        }
        private static double[] Constant(double value)
        { var values = new double[800]; for (int i=0;i<values.Length;i++) values[i]=value; return values; }
        private static bool Near(double a, double b)
        { return Math.Abs(a-b) <= Math.Max(1,Math.Abs(b))*1e-10; }
        private static FactoryFinalAi4Observation RoundTrip(FactoryFinalAi4Observation value)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(FactoryFinalAi4Observation));
                serializer.Serialize(stream,value); stream.Position=0;
                return (FactoryFinalAi4Observation)serializer.Deserialize(stream);
            }
        }
    }
}
