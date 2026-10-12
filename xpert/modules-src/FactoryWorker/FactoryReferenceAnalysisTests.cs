using System;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryReferenceAnalysisTests
    {
        internal static string Run()
        {
            int checks = 0;
            const double rate = 80000, c = 200e-9, l = .7, resistance = 22, loss = .0024;
            // Independent physical fixture: alpha=(R/L + G/C)/2 and
            // omega_d^2=1/(LC)+(R/L)*(G/C)-alpha^2. Choose G/C, then
            // derive measured frequency and the tan delta it implies.
            double dielectricRate = 2 * Math.PI * 425.4 * loss;
            double alpha = .5 * (resistance / l + dielectricRate);
            double omega = Math.Sqrt(1 / (l * c) + resistance / l * dielectricRate - alpha * alpha);
            double frequency = omega / (2 * Math.PI), actualLoss = dielectricRate / omega;
            double[] wave = Wave(rate, 60, frequency, alpha, 2, .015, .3, false);
            double[] original = (double[])wave.Clone();
            FactoryReferenceResult r = Analyze(wave, rate, frequency, alpha, actualLoss, resistance);
            Require(r.Valid && r.DecayValid, "physical standard capacitor has verified 8-cycle attenuation: " + r.Detail); checks++;
            Near(r.InductanceH, l, 1e-12, "known C reconstructs L with dielectric parallel conductance"); checks++;
            Near(r.MeasuredTotalSeriesResistanceOhms, resistance, 1e-10, "known dielectric loss removed from fitted coil resistance"); checks++;
            Near(r.OtherSeriesResistanceOhms, 0, 1e-10, "no invented additional fixture loss"); checks++;
            Near(r.ExpectedAlphaPerSecond, alpha, 1e-10, "predicted decay alpha uses R and dielectric conductance"); checks++;
            Require(r.CyclePeakKv.Length == 9 && r.CyclePeakMs.Length == 9, "eight periods require nine same-polarity peaks"); checks++;
            Near(r.CyclePeakMs[8] - r.CyclePeakMs[0], 8000 / frequency, .03, "measured peak spacing really spans eight periods"); checks++;
            Near(r.MeasuredDecayPercent, 100 * (1 - Math.Exp(-alpha * 8 / frequency)), .08,
                "eight-cycle attenuation does not accidentally span only seven cycles"); checks++;
            Near(r.ExpectedDecayPercent, r.MeasuredDecayPercent, .08, "expected and measured eight-period attenuation agree"); checks++;
            Near(r.BaselineV, .015, 1e-12, "DC offset fitted independently and removed from peak attenuation"); checks++;
            Require(r.FirstZeroMs > 0 && r.FirstZeroMs < 1000 / frequency, "first genuine zero crossing retained"); checks++;
            Require(r.PhaseValid, "valid waveform phase has its own independent quality flag"); checks++;
            var withoutReference = FactoryReferenceAnalysis.Analyze(wave, rate, 11000, .5, frequency, alpha,
                true, Double.NaN, Double.NaN, Double.NaN, 8, false);
            Require(!withoutReference.Valid && withoutReference.PhaseValid && withoutReference.DecayValid &&
                Double.IsNaN(withoutReference.InductanceH), "disabling reference capacitor leaves actual phase and decay usable without invented L"); checks++;
            Near(withoutReference.FirstZeroMs, r.FirstZeroMs, 1e-12, "reference setting cannot shift measured first phase"); checks++;
            Near(withoutReference.MeasuredDecayPercent, r.MeasuredDecayPercent, 1e-12,
                "reference setting cannot alter raw same-polarity peak attenuation"); checks++;
            var inconsistentReference = FactoryReferenceAnalysis.Analyze(wave, rate, 11000, .5, frequency,
                alpha, true, 200, .08, resistance);
            Require(!inconsistentReference.Valid && inconsistentReference.PhaseValid && inconsistentReference.DecayValid,
                "incompatible known dielectric loss invalidates only the reference model, not genuine phase or measured decay"); checks++;
            Near(inconsistentReference.FirstZeroMs, r.FirstZeroMs, 1e-12, "unphysical reference model does not move phase window"); checks++;
            for (int i = 0; i < original.Length; i++) Require(original[i] == wave[i], "analysis leaves original samples unchanged"); checks++;

            r = Analyze(Wave(rate, 60, frequency, alpha, 2, .015, .3, true), rate, frequency, alpha, actualLoss, resistance);
            Require(r.Valid && r.DecayValid, "negative starting polarity can still measure same-polarity attenuation"); checks++;
            Near(r.MeasuredDecayPercent, 100 * (1 - Math.Exp(-alpha * 8 / frequency)), .08, "polarity does not change attenuation"); checks++;

            r = Analyze(Wave(rate, 19, frequency, alpha, 2, 0, .3, false), rate, frequency, alpha, actualLoss, resistance);
            Require(r.Valid && !r.DecayValid && Double.IsNaN(r.MeasuredDecayPercent),
                "short record may reconstruct L but does not fabricate eight-period decay"); checks++;
            Require(r.CyclePeakKv.Length <= 8 && r.Detail.Contains("需要 9"), "insufficient peak reason explicit"); checks++;

            double extra = 13, otherAlpha = .5 * ((resistance + extra) / l + dielectricRate);
            double otherOmega = Math.Sqrt(1 / (l * c) + (resistance + extra) / l * dielectricRate - otherAlpha * otherAlpha);
            double otherFrequency = otherOmega / (2 * Math.PI);
            r = Analyze(Wave(rate, 60, otherFrequency, otherAlpha, 2, 0, .3, false), rate, otherFrequency,
                otherAlpha, dielectricRate / otherOmega, resistance);
            Near(r.InductanceH, l, 1e-12, "added coil/fixture resistance does not change actual inductance"); checks++;
            Near(r.OtherSeriesResistanceOhms, extra, 1e-10, "extra resistance is not attributed to standard capacitor tan delta"); checks++;
            Require(r.MeasuredDecayPercent > r.ExpectedDecayPercent, "extra loss raises measured attenuation beyond known-capacitor prediction"); checks++;

            r = Analyze(new double[4800], rate, frequency, alpha, actualLoss, resistance);
            Require(!r.Valid && !r.DecayValid, "flat background never returns standard capacitor result"); checks++;
            double[] invalid = (double[])wave.Clone(); invalid[50] = Double.NaN;
            Require(!Analyze(invalid, rate, frequency, alpha, actualLoss, resistance).Valid, "NaN raw voltage rejected"); checks++;
            invalid = (double[])wave.Clone(); invalid[20] = 10;
            Require(!Analyze(invalid, rate, frequency, alpha, actualLoss, resistance).Valid, "raw ADC boundary rejects reference result even in excluded transient"); checks++;
            Require(!FactoryReferenceAnalysis.Analyze(wave, rate, 11000, .5, frequency, alpha, false,
                200, actualLoss, resistance).Valid, "unvalidated oscillation model cannot become valid in reference page"); checks++;
            Require(!Analyze(wave, 1000, frequency, alpha, actualLoss, resistance).Valid, "insufficient samples per period rejected"); checks++;
            Require(!FactoryReferenceAnalysis.Analyze(wave, rate, 11000, .5, frequency, alpha, true,
                0, actualLoss, resistance).Valid, "missing known capacitance rejected"); checks++;
            Require(!FactoryReferenceAnalysis.Analyze(wave, rate, 11000, .5, frequency, alpha, true,
                200, -.001, resistance).Valid, "negative loss reference rejected"); checks++;
            Require(!FactoryReferenceAnalysis.Analyze(Wave(rate, 60, frequency, 1, 2, 0, .3, false),
                rate, 11000, .5, frequency, 1, true, 200, actualLoss, resistance).Valid,
                "loss reference exceeding measured damping cannot imply negative passive resistance"); checks++;

            double[] multifrequency = (double[])wave.Clone();
            for (int i = 0; i < multifrequency.Length; i++) multifrequency[i] += 1.3 * Math.Sin(2 * Math.PI * 713 * i / rate);
            Require(!Analyze(multifrequency, rate, frequency, alpha, actualLoss, resistance).Valid,
                "multifrequency residual cannot be accepted as one passive reference circuit"); checks++;
            return "Factory reference capacitor analysis passed " + checks + " offline checks; no hardware accessed.";
        }

        private static FactoryReferenceResult Analyze(double[] wave, double rate, double frequency, double alpha, double loss, double resistance)
        { return FactoryReferenceAnalysis.Analyze(wave, rate, 11000, .5, frequency, alpha, true, 200, loss, resistance, 8); }
        private static double[] Wave(double rate, double durationMs, double frequency, double alpha, double amplitude, double baseline, double phase, bool negative)
        {
            var wave = new double[(int)Math.Round(rate * durationMs / 1000)];
            for (int i = 0; i < wave.Length; i++)
            {
                double t = i / rate;
                wave[i] = baseline + (negative ? -1 : 1) * amplitude * Math.Exp(-alpha * t) * Math.Cos(2 * Math.PI * frequency * t + phase);
            }
            return wave;
        }
        private static void Near(double actual, double expected, double tolerance, string detail)
        { Require(!Double.IsNaN(actual) && Math.Abs(actual - expected) <= tolerance, detail + " actual=" + actual + " expected=" + expected); }
        private static void Require(bool pass, string detail)
        { if (!pass) throw new InvalidOperationException("Factory reference analysis test failed: " + detail); }
    }
}
