using System;
using System.Diagnostics;
using HVX.PD.Filtering;

internal static class BandpassFilterSmokeTest
{
    private const double LowCutoffHz = 3.0e6;
    private const double HighCutoffHz = 30.0e6;
    private const double PassFrequencyHz = 10.0e6;
    private const double LowStopFrequencyHz = 0.5e6;
    private const double HighStopFrequencyHz = 45.0e6;

    private static int Main()
    {
        try
        {
            RunForSampleRate(125.0e6);
            RunForSampleRate(250.0e6);
            VerifyValidation();
            Console.WriteLine("PASS: all bandpass-filter tests completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void RunForSampleRate(double sampleRateHz)
    {
        PdBandpassFilter filter = new PdBandpassFilter();
        int shortLength = (int)Math.Round(sampleRateHz * 100.0e-6);
        int longLength = (int)Math.Round(sampleRateHz * 50.0e-3);

        double[] shortInput = CreateSignal(shortLength, sampleRateHz);
        double[] longInput = CreateSignal(longLength, sampleRateHz);

        Stopwatch stopwatch = Stopwatch.StartNew();
        double[] shortOutput = filter.Filter(shortInput, sampleRateHz, LowCutoffHz, HighCutoffHz);
        stopwatch.Stop();
        long shortMilliseconds = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        double[] longOutput = filter.Filter(longInput, sampleRateHz, LowCutoffHz, HighCutoffHz);
        stopwatch.Stop();

        Assert(shortOutput.Length == shortLength, "Short output length mismatch.");
        Assert(longOutput.Length == longLength, "Long output length mismatch.");

        int edge = (int)Math.Ceiling(sampleRateHz * 5.0e-6);
        double passAmplitude = ProjectAmplitude(shortOutput, sampleRateHz, PassFrequencyHz, edge, shortLength - edge);
        double lowStopAmplitude = ProjectAmplitude(shortOutput, sampleRateHz, LowStopFrequencyHz, edge, shortLength - edge);
        double highStopAmplitude = ProjectAmplitude(shortOutput, sampleRateHz, HighStopFrequencyHz, edge, shortLength - edge);

        Assert(passAmplitude > 0.85 && passAmplitude < 1.15, "Passband amplitude is outside tolerance: " + passAmplitude);
        Assert(lowStopAmplitude < 0.08, "Low stopband attenuation is insufficient: " + lowStopAmplitude);
        Assert(highStopAmplitude < 0.08, "High stopband attenuation is insufficient: " + highStopAmplitude);

        // The beginning of both generated records is identical. Excluding the short-record
        // edge regions, the result must not depend materially on total acquisition length.
        double differenceRms = DifferenceRms(shortOutput, longOutput, edge, shortLength - edge);
        double referenceRms = Rms(shortOutput, edge, shortLength - edge);
        Assert(differenceRms / referenceRms < 0.02, "Short/long duration mismatch is too large: " + differenceRms / referenceRms);

        VerifyImpulseTiming(filter, sampleRateHz, shortLength);

        Console.WriteLine(
            "Fs={0:F0} MS/s, Nshort={1}, Nlong={2}, pass={3:F4}, stopLow={4:F4}, stopHigh={5:F4}, short={6} ms, long={7} ms",
            sampleRateHz / 1.0e6,
            shortLength,
            longLength,
            passAmplitude,
            lowStopAmplitude,
            highStopAmplitude,
            shortMilliseconds,
            stopwatch.ElapsedMilliseconds);
    }

    private static double[] CreateSignal(int length, double sampleRateHz)
    {
        double[] data = new double[length];
        for (int i = 0; i < length; i++)
        {
            double t = i / sampleRateHz;
            data[i] =
                Math.Sin(2.0 * Math.PI * PassFrequencyHz * t) +
                0.7 * Math.Sin(2.0 * Math.PI * LowStopFrequencyHz * t + 0.21) +
                0.5 * Math.Sin(2.0 * Math.PI * HighStopFrequencyHz * t - 0.37);
        }

        return data;
    }

    private static void VerifyImpulseTiming(PdBandpassFilter filter, double sampleRateHz, int length)
    {
        double[] impulse = new double[length];
        int peakIndex = length / 2;
        impulse[peakIndex] = 1.0;
        double[] result = filter.Filter(impulse, sampleRateHz, LowCutoffHz, HighCutoffHz);

        int maximumIndex = 0;
        double maximum = 0.0;
        for (int i = 0; i < result.Length; i++)
        {
            double magnitude = Math.Abs(result[i]);
            if (magnitude > maximum)
            {
                maximum = magnitude;
                maximumIndex = i;
            }
        }

        Assert(maximumIndex == peakIndex, "Zero-phase impulse peak shifted from " + peakIndex + " to " + maximumIndex + ".");
    }

    private static void VerifyValidation()
    {
        PdBandpassFilter filter = new PdBandpassFilter();
        bool threw = false;
        try
        {
            filter.Filter(new double[32], 125.0e6, 3.0e6, 70.0e6);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert(threw, "Nyquist validation did not reject an invalid high cutoff.");
    }

    private static double ProjectAmplitude(double[] data, double sampleRateHz, double frequencyHz, int start, int end)
    {
        double sin = 0.0;
        double cos = 0.0;
        int count = end - start;
        for (int i = start; i < end; i++)
        {
            double angle = 2.0 * Math.PI * frequencyHz * i / sampleRateHz;
            sin += data[i] * Math.Sin(angle);
            cos += data[i] * Math.Cos(angle);
        }

        return 2.0 * Math.Sqrt(sin * sin + cos * cos) / count;
    }

    private static double DifferenceRms(double[] left, double[] right, int start, int end)
    {
        double sum = 0.0;
        for (int i = start; i < end; i++)
        {
            double difference = left[i] - right[i];
            sum += difference * difference;
        }

        return Math.Sqrt(sum / (end - start));
    }

    private static double Rms(double[] data, int start, int end)
    {
        double sum = 0.0;
        for (int i = start; i < end; i++)
        {
            sum += data[i] * data[i];
        }

        return Math.Sqrt(sum / (end - start));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
