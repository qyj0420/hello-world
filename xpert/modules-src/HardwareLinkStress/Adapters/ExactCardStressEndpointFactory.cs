using System;

namespace HVX.HardwareLinkStress.Adapters
{
    // The selected DS600 MAC/port identity belongs to StressRunOptions and
    // the host's exclusive lease/monitor. This factory binds exact card serials
    // only; it cannot discover, bind, reconnect or steal another device.
    public sealed class ExactCardStressEndpointFactory : ITransferEndpointFactory
    {
        private readonly string artDllPath;
        private readonly string artDeviceName;
        private readonly string artSerial;
        private readonly int artMaximumSamples;
        private readonly double artSampleRateHz;
        private readonly string picoDllPath;
        private readonly string picoSerial;
        private readonly int picoMaximumSamples;
        private readonly int picoInputRangeIndex;

        public ExactCardStressEndpointFactory(string artDaqDllPath, string exactArtDeviceName,
            string exactArtSerial, int artMaximumSamplesPerCycle, double artSampleRate,
            string ps5000aDllPath, string exactPicoSerial,
            int picoMaximumSamplesPerCycle, int picoRangeIndex)
        {
            if (String.IsNullOrWhiteSpace(artDaqDllPath) ||
                String.IsNullOrWhiteSpace(exactArtDeviceName) ||
                String.IsNullOrWhiteSpace(exactArtSerial) ||
                String.IsNullOrWhiteSpace(ps5000aDllPath) ||
                String.IsNullOrWhiteSpace(exactPicoSerial))
                throw new ArgumentException("Explicit vendor runtime paths and both selected card identities are required.");
            artDllPath = artDaqDllPath;
            artDeviceName = exactArtDeviceName;
            artSerial = exactArtSerial;
            artMaximumSamples = artMaximumSamplesPerCycle;
            artSampleRateHz = artSampleRate;
            picoDllPath = ps5000aDllPath;
            picoSerial = exactPicoSerial;
            picoMaximumSamples = picoMaximumSamplesPerCycle;
            picoInputRangeIndex = picoRangeIndex;
        }

        public ITransferEndpoint CreateArt()
        {
            return new ArtAiOnlyStressEndpoint(artDllPath, artDeviceName, artSerial,
                artMaximumSamples, artSampleRateHz);
        }

        public ITransferEndpoint CreatePico()
        {
            return new PicoInputOnlyStressEndpoint(picoDllPath, picoSerial,
                picoMaximumSamples, picoInputRangeIndex);
        }

        // These are conservative application-side limits from the existing
        // Task38 60 ms configuration, NOT proof of measured DS600 throughput.
        // InitializeAsync still queries/validates the actual driver and card.
        public DiagnosticCapacity CreateConservativeApplicationCapacity()
        {
            if (artMaximumSamples < 1 || artMaximumSamples > 4800 ||
                Double.IsNaN(artSampleRateHz) || Double.IsInfinity(artSampleRateHz) ||
                artSampleRateHz < 1 || artSampleRateHz > 80000.0 ||
                picoMaximumSamples < 1 || picoMaximumSamples > 7500000)
                throw new InvalidOperationException("Diagnostic limits exceed the existing application acquisition envelope.");
            double duration = Math.Min(60.0,
                Math.Min(artMaximumSamples * 1000.0 / artSampleRateHz,
                    picoMaximumSamples * 1000.0 / 125000000.0));
            if (duration < 1.0)
                throw new InvalidOperationException("Selected conservative limits cannot provide a 1 ms diagnostic segment.");
            return new DiagnosticCapacity
            {
                ArtSamplesPerSecond = artSampleRateHz,
                ArtBytesPerSample = sizeof(double),
                ArtChannelCount = 1,
                ArtMaximumSamples = artMaximumSamples,
                PicoSamplesPerSecond = 125000000.0,
                PicoBytesPerSample = sizeof(short),
                PicoMaximumSamples = picoMaximumSamples,
                PicoBufferBudgetBytes = checked(picoMaximumSamples * sizeof(short)),
                MinimumDurationMilliseconds = 1.0,
                MaximumDurationMilliseconds = duration
            };
        }
    }
}
