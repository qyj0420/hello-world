using System;

namespace HVX.HardwareLinkStress.Tests
{
    public static class TransferTierPlannerTests
    {
        public static string Run()
        {
            DiagnosticCapacity c = DefaultCapacity();
            TransferPlan low = TransferTierPlanner.Create(TransferTier.Low, c);
            TransferPlan middle = TransferTierPlanner.Create(TransferTier.Medium, c);
            TransferPlan high = TransferTierPlanner.Create(TransferTier.High, c);
            Require(low.PicoBytesPerCycle == 2500000 && low.ArtSamplesPerCycle == 800,
                "low payload/sample count");
            Require(middle.PicoBytesPerCycle == 10000000 && middle.ArtSamplesPerCycle == 3200,
                "medium payload/sample count");
            Require(high.PicoBytesPerCycle == 20000000 && high.ArtSamplesPerCycle == 6400,
                "high payload/sample count");
            Require(!low.LimitedByCapacity && !middle.LimitedByCapacity && !high.LimitedByCapacity,
                "uncapped default capacity");
            Require(low.PicoBytesPerCycle < middle.PicoBytesPerCycle &&
                middle.PicoBytesPerCycle < high.PicoBytesPerCycle,
                "tier payload order");
            Require(low.CyclePeriod > middle.CyclePeriod && middle.CyclePeriod > high.CyclePeriod,
                "tier rest order");

            c = DefaultCapacity();
            c.PicoMaximumSamples = 7500000;
            c.PicoBufferBudgetBytes = 15000000;
            c.ArtChannelCount = 1;
            c.ArtMaximumSamples = 4800;
            c.MaximumDurationMilliseconds = 60;
            TransferPlan conservativeHigh = TransferTierPlanner.Create(TransferTier.High, c);
            Require(conservativeHigh.PicoBytesPerCycle == 15000000 &&
                conservativeHigh.ArtBytesPerCycle == 38400 &&
                Math.Abs(conservativeHigh.RequestedDurationMilliseconds - 60.0) < 0.001 &&
                conservativeHigh.LimitedByCapacity,
                "high tier visibly capped by current adapter envelope");
            c = DefaultCapacity();
            c.PicoBufferBudgetBytes = 6000000;
            TransferPlan capped = TransferTierPlanner.Create(TransferTier.High, c);
            Require(capped.PicoBytesPerCycle == 6000000 && capped.LimitedByCapacity &&
                capped.NominalPicoBytesPerCycle == 20000000,
                "capacity cap must be explicit");
            c = DefaultCapacity();
            c.ArtMaximumSamples = 10;
            Rejected(delegate { TransferTierPlanner.Create(TransferTier.Low, c); },
                "insufficient ART capacity");
            c = DefaultCapacity();
            c.PicoSamplesPerSecond = Double.NaN;
            Rejected(delegate { TransferTierPlanner.Create(TransferTier.High, c); },
                "unverified rate");
            return "TRANSFER_TIER_PLANNER=PASS";
        }

        public static DiagnosticCapacity DefaultCapacity()
        {
            return new DiagnosticCapacity
            {
                PicoSamplesPerSecond = 125000000,
                PicoBytesPerSample = 2,
                PicoMaximumSamples = 50000000,
                PicoBufferBudgetBytes = 64000000,
                ArtSamplesPerSecond = 80000,
                ArtBytesPerSample = 8,
                ArtChannelCount = 3,
                ArtMaximumSamples = 80000,
                MinimumDurationMilliseconds = 1,
                MaximumDurationMilliseconds = 400
            };
        }

        private static void Rejected(Action action, string name)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected rejection: " + name);
        }

        private static void Require(bool condition, string name)
        { if (!condition) throw new Exception("Transfer-tier test failed: " + name); }
    }
}
