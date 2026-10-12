using System;
using System.Threading;

namespace HVX.Controls
{
    internal sealed partial class PressureWaveformView
    {
        // Pure data preparation: no Control/Graphics access, safe on a worker.
        // Both synchronous and asynchronous entry points share these axis rules.
        internal sealed class PreparedData
        {
            internal double[] Voltage, DisplayVoltage, Discharge;
            internal double VoltageRate, DischargeRate, FullDuration, VoltageAbs, PdBaseline, PdMaximum, FirstCrossing,PdWindowMaximum;
            internal bool TimeKnown;
            internal PressurePdDisplayEnvelope PdEnvelope;
        }
        internal static PreparedData PrepareData(double[] voltage, double voltageRate, double[] discharge, double dischargeRate, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var data = new PreparedData { Voltage = CopyFinite(voltage), Discharge = CopyFinite(discharge),
                VoltageRate = IsFinitePositive(voltageRate) ? voltageRate : 0,
                DischargeRate = IsFinitePositive(dischargeRate) ? dischargeRate : 0 };
            cancel.ThrowIfCancellationRequested();
            data.TimeKnown = HasTimeBase(data.Voltage, data.VoltageRate, data.Discharge, data.DischargeRate);
            data.FullDuration = DisplayExtent(data.Voltage, data.VoltageRate, data.Discharge, data.DischargeRate, data.TimeKnown);
            data.VoltageAbs = VoltageDisplayMaximum(MaxAbs(data.Voltage));
            data.DisplayVoltage = PrepareDisplayVoltage(data.Voltage,cancel);
            data.PdBaseline = PressureDisplayMedian.Calculate(data.Discharge, cancel);
            data.PdEnvelope = new PressurePdDisplayEnvelope(data.Discharge, data.PdBaseline, cancel);
            cancel.ThrowIfCancellationRequested();
            data.PdMaximum = Math.Max(1.0, MaxAbsFromBaseline(data.Discharge, data.PdBaseline));
            data.FirstCrossing = FindDisplayCrossing(data.Voltage,data.VoltageRate);
            data.PdWindowMaximum=PressureMetricWindow.PeakAfterFirstCrossing(data.Voltage,data.VoltageRate,data.Discharge,data.DischargeRate);
            return data;
        }
        internal void InstallPreparedData(PreparedData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            CancelManualThresholdDrag(true);
            voltage = data.Voltage; displayVoltage=data.DisplayVoltage; discharge = data.Discharge;
            ResetVoltageDrawingCache();
            voltageRateHz = data.VoltageRate; dischargeRateHz = data.DischargeRate;
            cachedFullDuration = data.FullDuration; cachedVoltageAbs = data.VoltageAbs;
            cachedPdBaseline = data.PdBaseline; cachedPdMaximum = data.PdMaximum;
            cachedPdEnvelope = data.PdEnvelope;
            cachedTimeKnown = data.TimeKnown;
            cachedFirstCrossing=data.FirstCrossing;
            cachedPdWindowMaximum=data.PdWindowMaximum;
            Invalidate();
        }
        static double FindDisplayCrossing(double[] values,double rate)
        {
            if(!IsFinitePositive(rate))return Double.NaN;
            var window=Xiongxin.OWTS.Data.OscillationCycleWindow.Find(values,rate,1,0);
            return window.Success?window.CrossingSeconds[0]:Double.NaN;
        }
        static bool HasTimeBase(double[] v, double vr, double[] p, double pr)
        { return (v.Length > 0 || p.Length > 0) && (v.Length == 0 || IsFinitePositive(vr)) && (p.Length == 0 || IsFinitePositive(pr)); }
        static double DisplayExtent(double[] v, double vr, double[] p, double pr, bool known)
        {
            if (!known) return Math.Max(0, Math.Max(v.Length, p.Length) - 1);
            if (v.Length == 0) return Duration(p, pr);
            if (p.Length == 0) return Duration(v, vr);
            return Math.Min(Duration(v, vr), Duration(p, pr));
        }
    }
}
