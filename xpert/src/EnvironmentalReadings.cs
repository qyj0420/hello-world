using System;

namespace Xiongxin.OWTS.Desktop
{
    // Stable error codes are translated by the host. Diagnostic contains service details,
    // never customer-facing text, paths, or a substitute for the independent validity flags.
    internal sealed class TemperatureHumidityReading
    {
        public bool Success { get { return HumiditySuccess && TemperatureSuccess && ErrorCode == String.Empty; } }
        public bool HumiditySuccess { get; internal set; }
        public bool TemperatureSuccess { get; internal set; }
        public double? HumidityPercent { get; internal set; }
        public double? TemperatureC { get; internal set; }
        public double? HumidityVoltage { get; internal set; }
        public double? TemperatureVoltage { get; internal set; }
        public DateTimeOffset AttemptedUtc { get; internal set; }
        public DateTimeOffset? CapturedUtc { get; internal set; }
        public int SamplesPerChannel { get; internal set; }
        public string ErrorCode { get; internal set; }
        public string Diagnostic { get; internal set; }
        public TemperatureHumidityReading() { AttemptedUtc = DateTimeOffset.UtcNow; ErrorCode = String.Empty; Diagnostic = String.Empty; }
    }

    internal sealed class GnssReading
    {
        public bool Success { get; internal set; }
        public double? Latitude { get; internal set; }
        public double? Longitude { get; internal set; }
        // RMC does not contain altitude. Optional GGA height is mean-sea-level height.
        public double? AltitudeMeters { get; internal set; }
        public DateTimeOffset? UtcTime { get; internal set; }
        public DateTimeOffset? ChinaTime { get { return UtcTime.HasValue ? UtcTime.Value.ToOffset(TimeSpan.FromHours(8)) : (DateTimeOffset?)null; } }
        public double? SpeedKnots { get; internal set; }
        public double? CourseDegrees { get; internal set; }
        public DateTimeOffset AttemptedUtc { get; internal set; }
        public DateTimeOffset? CapturedUtc { get; internal set; }
        public string ErrorCode { get; internal set; }
        public string Diagnostic { get; internal set; }
        public GnssReading() { AttemptedUtc = DateTimeOffset.UtcNow; ErrorCode = String.Empty; Diagnostic = String.Empty; }
        internal static GnssReading Failure(string code, string diagnostic)
        { return new GnssReading { ErrorCode = code, Diagnostic = diagnostic }; }
    }
}
