using System;

namespace HVX.HardwareLinkStress
{
    // These amounts are application-side raw sample payloads, not measured
    // Ethernet frames or a claim about the DS600 link's usable bandwidth.
    public enum TransferTier { Low, Medium, High }

    public sealed class DiagnosticCapacity
    {
        public double PicoSamplesPerSecond;
        public int PicoBytesPerSample;
        public int PicoMaximumSamples;
        public int PicoBufferBudgetBytes;
        public double ArtSamplesPerSecond;
        public int ArtBytesPerSample;
        public int ArtChannelCount;
        public int ArtMaximumSamples;
        public double MinimumDurationMilliseconds;
        public double MaximumDurationMilliseconds;
    }

    public static class TransferTierPlanner
    {
        // At 125 MS/s, 16-bit Pico data, these correspond to approximately
        // 10, 40 and 80 ms before the shared 60 ms cap. The actual duration is recalculated from the
        // verified rate and finite buffer limits when a run is started.
        public static TransferPlan Create(TransferTier tier, DiagnosticCapacity capacity)
        {
            if (capacity == null) throw new ArgumentNullException("capacity");
            Validate(capacity);
            int requestedPicoBytes;
            TimeSpan minimumCyclePeriod;
            string tierName;
            switch (tier)
            {
                case TransferTier.Low:
                    requestedPicoBytes = 2500000;
                    minimumCyclePeriod = TimeSpan.FromMilliseconds(2000);
                    tierName = "低";
                    break;
                case TransferTier.Medium:
                    requestedPicoBytes = 10000000;
                    minimumCyclePeriod = TimeSpan.FromMilliseconds(2000);
                    tierName = "中";
                    break;
                case TransferTier.High:
                    requestedPicoBytes = 20000000;
                    minimumCyclePeriod = TimeSpan.FromMilliseconds(2000);
                    tierName = "高";
                    break;
                default: throw new ArgumentOutOfRangeException("tier");
            }

            long limitByBuffer = Math.Min((long)capacity.PicoMaximumSamples,
                (long)capacity.PicoBufferBudgetBytes / capacity.PicoBytesPerSample);
            long limitByDuration = (long)Math.Floor(capacity.PicoSamplesPerSecond *
                Math.Min(60,capacity.MaximumDurationMilliseconds) / 1000.0);
            long limitByArt = (long)Math.Floor(capacity.PicoSamplesPerSecond *
                capacity.ArtMaximumSamples / capacity.ArtSamplesPerSecond);
            long maximumSamples = Math.Min(limitByBuffer, Math.Min(limitByDuration, limitByArt));
            long minimumSamples = (long)Math.Ceiling(capacity.PicoSamplesPerSecond *
                capacity.MinimumDurationMilliseconds / 1000.0);
            if (maximumSamples < minimumSamples || maximumSamples < 1)
                throw new InvalidOperationException("已核实的设备容量不能满足最短诊断段；没有启动设备读写。");

            long desiredSamples = (long)Math.Ceiling((double)requestedPicoBytes / capacity.PicoBytesPerSample);
            long picoSamples = Math.Max(minimumSamples, Math.Min(maximumSamples, desiredSamples));
            double durationMs = picoSamples * 1000.0 / capacity.PicoSamplesPerSecond;
            long artSamples = (long)Math.Ceiling(capacity.ArtSamplesPerSecond * durationMs / 1000.0);
            if (artSamples < 1 || artSamples > capacity.ArtMaximumSamples)
                throw new InvalidOperationException("ART 输入段超过已核实的诊断容量；没有启动设备读写。");
            long artBytes = checked(artSamples * capacity.ArtChannelCount * capacity.ArtBytesPerSample);
            long picoBytes = checked(picoSamples * capacity.PicoBytesPerSample);
            if (artBytes > Int32.MaxValue || picoBytes > Int32.MaxValue)
                throw new InvalidOperationException("诊断段超出当前接口的单段字节上限。");

            return new TransferPlan
            {
                TierName = tierName,
                NominalPicoBytesPerCycle = requestedPicoBytes,
                LimitedByCapacity = picoBytes < requestedPicoBytes,
                ArtBytesPerCycle = (int)artBytes,
                PicoBytesPerCycle = (int)picoBytes,
                ArtSamplesPerCycle = (int)artSamples,
                PicoSamplesPerCycle = (int)picoSamples,
                ArtBytesPerSample = capacity.ArtBytesPerSample * capacity.ArtChannelCount,
                PicoBytesPerSample = capacity.PicoBytesPerSample,
                RequestedDurationMilliseconds = durationMs,
                CyclePeriod = minimumCyclePeriod
            };
        }

        private static void Validate(DiagnosticCapacity c)
        {
            if (!PositiveFinite(c.PicoSamplesPerSecond) || !PositiveFinite(c.ArtSamplesPerSecond) ||
                !PositiveFinite(c.MinimumDurationMilliseconds) ||
                !PositiveFinite(c.MaximumDurationMilliseconds) ||
                c.MaximumDurationMilliseconds < c.MinimumDurationMilliseconds ||
                c.PicoSamplesPerSecond > 1000000000.0 || c.ArtSamplesPerSecond > 10000000.0 ||
                c.MaximumDurationMilliseconds > 1000.0 ||
                c.PicoBytesPerSample < 1 || c.ArtBytesPerSample < 1 || c.ArtChannelCount < 1 ||
                c.PicoMaximumSamples < 1 || c.ArtMaximumSamples < 1 || c.PicoBufferBudgetBytes < 1)
                throw new ArgumentException("诊断设备容量尚未核实或无效；拒绝生成压力测试档位。", "c");
        }

        private static bool PositiveFinite(double value)
        { return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
