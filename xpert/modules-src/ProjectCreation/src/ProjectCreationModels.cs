using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.Controls
{
    internal static class ProjectModes
    {
        internal const string ThreeCore = "三芯";
        internal const string SinglePhase = "单相";
        internal const string ThreePhaseSingleCore = "三相单芯";
        internal const string Capacitor = "电容局放";
        internal static readonly string[] All = { ThreeCore, ThreePhaseSingleCore, SinglePhase, Capacitor };
        internal static readonly string[] Phases = { "A相", "B相", "C相" };

        internal static string Normalize(string value)
        {
            string text = (value ?? string.Empty).Trim();
            // Keep the historic bare "单芯" protocol alias three-phase. The
            // new, explicit display name "单芯电缆" means one single-core cable.
            if (text == "单芯" || text == "三相单芯电缆" || text == "单芯三相电缆" || text == "單芯三相電纜") return ThreePhaseSingleCore;
            if (text == "一体三芯" || text == "一体三芯电缆" || text == "一體三芯電纜") return ThreeCore;
            if (text == "单相单芯" || text == "单芯电缆" || text == "單芯電纜") return SinglePhase;
            if (text == "电容" || text == "电容器") return Capacitor;
            if (text.Equals("Three-core", StringComparison.OrdinalIgnoreCase) || text.Equals("Integrated three-core", StringComparison.OrdinalIgnoreCase) || text.Equals("Integrated three-core cable", StringComparison.OrdinalIgnoreCase)) return ThreeCore;
            if (text.Equals("Single-phase", StringComparison.OrdinalIgnoreCase) || text.Equals("Single-phase single-core", StringComparison.OrdinalIgnoreCase) || text.Equals("Single-core cable", StringComparison.OrdinalIgnoreCase)) return SinglePhase;
            if (text.Equals("Three-phase single-core", StringComparison.OrdinalIgnoreCase) || text.Equals("Three-phase single-core cable", StringComparison.OrdinalIgnoreCase)) return ThreePhaseSingleCore;
            if (text.Equals("Capacitor PD", StringComparison.OrdinalIgnoreCase) || text.Equals("Capacitor", StringComparison.OrdinalIgnoreCase)) return Capacitor;
            return All.Contains(text) ? text : ThreeCore;
        }

        internal static string DisplayName(string value)
        {
            string mode = Normalize(value);
            if (mode == ThreeCore) return "一体三芯电缆";
            if (mode == SinglePhase) return "单芯电缆";
            if (mode == Capacitor) return "电容";
            return "单芯三相电缆";
        }

        internal static string NormalizePhaseOrDefault(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (text == "B" || text == "B相" || text == "B PHASE" || text == "PHASE B") return "B相";
            if (text == "C" || text == "C相" || text == "C PHASE" || text == "PHASE C") return "C相";
            return "A相";
        }
    }

    internal static class ProjectPressureModes
    {
        internal const string ExistingCable = "旧电缆";
        internal const string NewCable = "新电缆";
        internal const string Custom = "自定义";
        internal const string Arbitrary = "任意值";
        internal static readonly string[] All = { ExistingCable, NewCable, Custom, Arbitrary };

        internal static string Normalize(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Equals("Existing cable", StringComparison.OrdinalIgnoreCase)) return ExistingCable;
            if (text.Equals("New cable", StringComparison.OrdinalIgnoreCase)) return NewCable;
            if (text.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return Custom;
            if (text.Equals("Arbitrary", StringComparison.OrdinalIgnoreCase) || text.Equals("Free value", StringComparison.OrdinalIgnoreCase)) return Arbitrary;
            return All.Contains(text) ? text : NewCable;
        }
    }

    internal sealed class JointData
    {
        internal string Phase = "A相";
        internal string Name = string.Empty;
        internal string AttachmentType = "户内冷缩";
        internal string Material = "塑料绝缘";
        internal double Position;
        // Persisted source identity, never inferred from a repeated phase label.
        internal int SourceBlockIndex = -1, SourceAccessoryIndex = -1;
        internal double SourceCableLength;
        internal string SourceMaterialBefore;

        internal JointData Clone()
        {
            return new JointData
            {
                Phase = Phase,
                Name = Name,
                AttachmentType = AttachmentType,
                Material = Material,
                Position = Position,
                SourceBlockIndex = SourceBlockIndex, SourceAccessoryIndex = SourceAccessoryIndex, SourceCableLength = SourceCableLength,
                SourceMaterialBefore = SourceMaterialBefore
            };
        }
    }

    internal sealed class PressureStep
    {
        internal double Multiplier;
        internal int Repetitions;

        internal PressureStep Clone()
        {
            return new PressureStep { Multiplier = Multiplier, Repetitions = Repetitions };
        }
    }

    internal sealed class MaterialData
    {
        internal string Name = string.Empty;
        internal double Velocity;

        internal MaterialData Clone()
        {
            return new MaterialData { Name = Name, Velocity = Velocity };
        }
    }

    internal sealed class ProjectDraft
    {
        internal string Organization = string.Empty;
        internal string OperatorName = string.Empty;
        internal string TestLocation = string.Empty;
        internal DateTime TestDateTime = DateTime.Now;
        internal bool DualEnded;
        internal double Humidity;
        internal double Temperature;
        internal string Weather = string.Empty;
        internal int WeatherCode = -1;
        internal string WeatherProvider = string.Empty;
        internal DateTime WeatherUpdatedAt;
        internal double Latitude;
        internal double Longitude;
        internal double Altitude;
        internal string BeidouFixStatus = "未采集";
        internal DateTime HardwareSampleTime;
        internal bool EnvironmentSampleValid;
        internal DateTime EnvironmentSampleTime;
        internal bool GnssFixValid;
        internal bool GnssAltitudeValid;
        internal DateTime GnssSampleTime;
        internal string StartName = string.Empty;
        internal string StartType = "户内冷缩";
        internal string DispatchNumberStart = string.Empty;
        internal string EndName = string.Empty;
        internal string EndType = "户内冷缩";
        internal string DispatchNumberEnd = string.Empty;
        internal string LineName = string.Empty;
        internal string CableModel = string.Empty;
        internal string CableMode = ProjectModes.ThreeCore;
        internal string DefaultMaterial = "塑料绝缘";
        internal double LengthA;
        internal double LengthB;
        internal double LengthC;
        internal string PhaseVoltage = "8.7kV";
        internal string PressureMode = "新电缆";
        internal DateTime LayingDate = DateTime.Today;
        internal string InsulationA1 = string.Empty;
        internal string InsulationA2 = string.Empty;
        internal string InsulationB1 = string.Empty;
        internal string InsulationB2 = string.Empty;
        internal string InsulationC1 = string.Empty;
        internal string InsulationC2 = string.Empty;
        internal string InsulationReferenceProfile = string.Empty;
        internal bool InsulationConditionsComparable;
        internal string Notes = string.Empty;
        internal readonly List<JointData> Joints = new List<JointData>();
        internal readonly Dictionary<string, string> FirstSegmentMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<PressureStep> PressureSteps = new List<PressureStep>();
        internal readonly Dictionary<string, string> UnknownItemValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal double SummaryLength
        {
            get { return Math.Max(LengthA, Math.Max(LengthB, LengthC)); }
        }

        internal IEnumerable<string> ActivePhases
        {
            get
            {
                if (CableMode == ProjectModes.SinglePhase || CableMode == ProjectModes.Capacitor)
                    return new[] { "A相" };
                return ProjectModes.Phases;
            }
        }

        internal double LengthFor(string phase)
        {
            if (phase == "B相") return LengthB;
            if (phase == "C相") return LengthC;
            return LengthA;
        }

        internal string FirstMaterialFor(string phase)
        {
            string material;
            return FirstSegmentMaterials.TryGetValue(phase, out material) && !string.IsNullOrWhiteSpace(material)
                ? material
                : DefaultMaterial;
        }

        internal void SetLength(string phase, double value)
        {
            if (phase == "B相") LengthB = value;
            else if (phase == "C相") LengthC = value;
            else LengthA = value;
        }

    }

    internal static class ProjectText
    {
        internal static string Number(double value)
        {
            return value.ToString("0.0###", CultureInfo.InvariantCulture);
        }

        internal static string LengthMetres(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        internal static double ParseNumber(string value, double fallback)
        {
            double parsed;
            string normalized = (value ?? string.Empty).Trim().TrimEnd('m', 'M').Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }
}
