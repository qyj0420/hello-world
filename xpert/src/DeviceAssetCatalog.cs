using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class DeviceAssetCatalog
    {
        internal const string ExpectedSchema = "XIONGXIN.DeviceAssetCatalog.v1";
        internal const string RelativePath = "assets\\device-asset-catalog.v1.json";
        private const int MaximumCatalogBytes = 2 * 1024 * 1024;

        public string Schema { get; set; }
        public string CatalogVersion { get; set; }
        public DeviceAssetCatalogProvenance Provenance { get; set; }
        public DeviceAssetNumberingRules NumberingRules { get; set; }
        public DeviceAssetCatalogAudit Audit { get; set; }
        public List<DeviceAssetRecord> Records { get; set; }

        private Dictionary<string, List<DeviceAssetRecord>> picoIndex;
        private Dictionary<string, List<DeviceAssetRecord>> artIndex;

        public static DeviceAssetCatalog TryLoadDefault(out string error)
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativePath));
            return TryLoad(path, out error);
        }

        public static DeviceAssetCatalog TryLoad(string path, out string error)
        {
            error = String.Empty;
            try
            {
                return Load(path);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        public static DeviceAssetCatalog Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Catalog path is required.", "path");
            FileInfo info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumCatalogBytes)
                throw new InvalidDataException("Device asset catalog file size is invalid.");

            string json;
            using (FileStream stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(stream, true)) json = reader.ReadToEnd();
            DeviceAssetCatalog catalog = new JavaScriptSerializer { MaxJsonLength = MaximumCatalogBytes }
                .Deserialize<DeviceAssetCatalog>(json);
            if (catalog == null) throw new InvalidDataException("Device asset catalog is empty.");
            catalog.ValidateAndIndex();
            return catalog;
        }

        public DeviceAssetMatchResult Match(HardwareStatusSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Ready)
                return DeviceAssetMatchResult.NotMatched("NotLive", "非实机就绪状态，不自动应用设备参数。");

            string first = NormalizeIdentity(snapshot.ControllerSerial);
            string second = NormalizeIdentity(snapshot.AcquisitionSerial);
            if (first.Length == 0 || second.Length == 0)
                return DeviceAssetMatchResult.NotMatched(
                    "ActualSerialUnavailable", "连接器未提供实机序列号，已保留手工参数。");
            string pico;
            string art;
            if (IsPicoSerial(first) && IsArtSerial(second))
            {
                pico = first;
                art = second;
            }
            else if (IsArtSerial(first) && IsPicoSerial(second))
            {
                pico = second;
                art = first;
            }
            else
            {
                return DeviceAssetMatchResult.NotMatched(
                    "InvalidIdentity", "实机两端必须分别提供一个完整 Pico 序列号和一个完整 ART 序列号。");
            }

            List<DeviceAssetRecord> picoMatches;
            List<DeviceAssetRecord> artMatches;
            if (!picoIndex.TryGetValue(pico, out picoMatches) || !artIndex.TryGetValue(art, out artMatches))
                return DeviceAssetMatchResult.NotMatched("Unknown", "资产目录无匹配，已保留手工参数。");
            if (picoMatches.Count != 1 || artMatches.Count != 1)
                return DeviceAssetMatchResult.NotMatched("Conflict", "资产目录存在重复或冲突标识，已保留手工参数。");
            if (!Object.ReferenceEquals(picoMatches[0], artMatches[0]))
                return DeviceAssetMatchResult.NotMatched("Conflict", "Pico 与 ART 标识指向不同出厂记录，已保留手工参数。");

            DeviceAssetRecord record = picoMatches[0];
            if (!record.AutoMatchEligible)
                return DeviceAssetMatchResult.NotMatched("Rejected", "对应出厂记录未通过唯一性审计，已保留手工参数。");
            return DeviceAssetMatchResult.Matched(record);
        }

        private void ValidateAndIndex()
        {
            if (!String.Equals(Schema, ExpectedSchema, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported device asset catalog schema.");
            if (String.IsNullOrWhiteSpace(CatalogVersion))
                throw new InvalidDataException("Device asset catalog version is missing.");
            if (Provenance == null || !String.Equals(Provenance.Worksheet, "出厂记录", StringComparison.Ordinal) ||
                !String.Equals(Provenance.SourceRange, "A2:Y87", StringComparison.Ordinal))
                throw new InvalidDataException("Device asset catalog provenance is invalid.");
            if (NumberingRules == null || !String.Equals(NumberingRules.Version, "1", StringComparison.Ordinal) ||
                NumberingRules.MainDeviceSerial == null || NumberingRules.TypeNumber == null || NumberingRules.Codes == null)
                throw new InvalidDataException("Device asset numbering rules are missing or unsupported.");
            if (Records == null || Records.Count != 86)
                throw new InvalidDataException("Device asset catalog must contain all 86 privacy-minimized source records.");
            if (Audit == null || Audit.SourceRows != Records.Count || Audit.AutoMatchEligibleRows != 56)
                throw new InvalidDataException("Device asset catalog audit totals are invalid.");

            picoIndex = new Dictionary<string, List<DeviceAssetRecord>>(StringComparer.Ordinal);
            artIndex = new Dictionary<string, List<DeviceAssetRecord>>(StringComparer.Ordinal);
            Dictionary<string, int> eligibleFactories = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < Records.Count; index++)
            {
                DeviceAssetRecord record = Records[index];
                if (record == null) throw new InvalidDataException("Device asset catalog contains an empty record.");
                record.Normalize();
                AddIndex(picoIndex, record.PicoScope == null ? String.Empty : record.PicoScope.Serial, record);
                AddIndex(artIndex, record.Art == null ? String.Empty : record.Art.Serial, record);
                if (record.AutoMatchEligible)
                {
                    if (!IsPicoSerial(record.PicoScope.Serial) || !IsArtSerial(record.Art.Serial) ||
                        String.IsNullOrWhiteSpace(record.FactoryNumber))
                        throw new InvalidDataException("An eligible device record has an invalid identity.");
                    int count;
                    eligibleFactories.TryGetValue(record.FactoryNumber, out count);
                    eligibleFactories[record.FactoryNumber] = count + 1;
                    if (!ValidPositive(record.DividerRatio) || record.Inductor == null ||
                        !ValidPositive(record.Inductor.InductanceHenry) || !ValidNonNegative(record.Inductor.DcResistanceOhm))
                        throw new InvalidDataException("An eligible device record has invalid auto-application parameters.");
                }
            }

            foreach (KeyValuePair<string, int> pair in eligibleFactories)
                if (pair.Value != 1) throw new InvalidDataException("Eligible factory numbers must be unique.");
            foreach (DeviceAssetRecord record in Records)
            {
                if (!record.AutoMatchEligible) continue;
                if (picoIndex[record.PicoScope.Serial].Count != 1 || artIndex[record.Art.Serial].Count != 1)
                    throw new InvalidDataException("Eligible Pico and ART serials must be unique.");
            }
        }

        private static void AddIndex(Dictionary<string, List<DeviceAssetRecord>> index, string key, DeviceAssetRecord record)
        {
            if (String.IsNullOrWhiteSpace(key)) return;
            List<DeviceAssetRecord> records;
            if (!index.TryGetValue(key, out records))
            {
                records = new List<DeviceAssetRecord>();
                index.Add(key, records);
            }
            records.Add(record);
        }

        internal static string NormalizeIdentity(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            string normalized = value.Trim().ToUpperInvariant().Replace('／', '/');
            return Regex.Replace(normalized, "\\s+", String.Empty, RegexOptions.CultureInvariant);
        }

        internal static bool IsPicoSerial(string value)
        {
            return Regex.IsMatch(NormalizeIdentity(value), "^[A-Z0-9]{5}/[0-9]{4}$", RegexOptions.CultureInvariant);
        }

        internal static bool IsArtSerial(string value)
        {
            return Regex.IsMatch(NormalizeIdentity(value), "^[0-9]{10}$", RegexOptions.CultureInvariant);
        }

        private static bool ValidPositive(double? value)
        {
            return value.HasValue && value.Value > 0.0 && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value);
        }

        private static bool ValidNonNegative(double? value)
        {
            return value.HasValue && value.Value >= 0.0 && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value);
        }
    }

    internal static class DeviceAssetParameterApplier
    {
        public static bool TryApply(AppSettings settings, DeviceAssetRecord record, string settingsPath,
            out bool changed, out string error)
        {
            changed = false;
            error = String.Empty;
            if (settings == null || record == null || !record.AutoMatchEligible || record.Inductor == null)
            {
                error = "No eligible device asset record was supplied.";
                return false;
            }

            AppSettings candidate = settings.Clone();
            candidate.DividerRatio = record.DividerRatio.Value;
            candidate.InductanceHenry = record.Inductor.InductanceHenry.Value;
            candidate.InductorDcResistanceOhm = record.Inductor.DcResistanceOhm.Value;
            changed = !Same(candidate.DividerRatio, settings.DividerRatio) ||
                !Same(candidate.InductanceHenry, settings.InductanceHenry) ||
                !Same(candidate.InductorDcResistanceOhm, settings.InductorDcResistanceOhm);
            if (!changed) return true;

            try
            {
                string validation;
                if (!candidate.TryValidate(out validation)) throw new InvalidDataException(validation);
                candidate.Save(settingsPath);
                // Commit to the live shared settings object only after the atomic file
                // replacement succeeds. No unrelated/manual setting is changed.
                settings.DividerRatio = candidate.DividerRatio;
                settings.InductanceHenry = candidate.InductanceHenry;
                settings.InductorDcResistanceOhm = candidate.InductorDcResistanceOhm;
                return true;
            }
            catch (Exception exception)
            {
                changed = false;
                error = exception.Message;
                return false;
            }
        }

        private static bool Same(double first, double second)
        {
            return Math.Abs(first - second) <= Math.Max(1.0, Math.Max(Math.Abs(first), Math.Abs(second))) * 1.0e-12;
        }
    }

    internal sealed class DeviceAssetMatchResult
    {
        public string Status { get; private set; }
        public string Message { get; private set; }
        public DeviceAssetRecord Record { get; private set; }
        public bool IsMatch { get { return Record != null && String.Equals(Status, "Matched", StringComparison.Ordinal); } }

        private DeviceAssetMatchResult() { }

        public static DeviceAssetMatchResult Matched(DeviceAssetRecord record)
        {
            return new DeviceAssetMatchResult
            {
                Status = "Matched",
                Message = "资产目录唯一匹配。",
                Record = record
            };
        }

        public static DeviceAssetMatchResult NotMatched(string status, string message)
        {
            return new DeviceAssetMatchResult
            {
                Status = status ?? "Unknown",
                Message = message ?? String.Empty
            };
        }
    }

    internal sealed class DeviceAssetRecord
    {
        public int SourceRow { get; set; }
        public string FactoryNumber { get; set; }
        public string DeviceModel { get; set; }
        public DeviceModuleIdentity PicoScope { get; set; }
        public DeviceModuleIdentity Art { get; set; }
        public double? DividerRatio { get; set; }
        public DeviceInductorParameters Inductor { get; set; }
        public double? CouplingCapacitanceNanoFarad { get; set; }
        public DeviceCompensationParameters Compensation { get; set; }
        public string CalibrationCoefficientSource { get; set; }
        public double? CalibrationCoefficient { get; set; }
        public List<string> ValidationFlags { get; set; }
        public bool AutoMatchEligible { get; set; }

        internal void Normalize()
        {
            FactoryNumber = DeviceAssetCatalog.NormalizeIdentity(FactoryNumber);
            DeviceModel = String.IsNullOrWhiteSpace(DeviceModel) ? String.Empty : DeviceModel.Trim();
            if (PicoScope == null) PicoScope = new DeviceModuleIdentity();
            if (Art == null) Art = new DeviceModuleIdentity();
            PicoScope.Model = DeviceAssetCatalog.NormalizeIdentity(PicoScope.Model);
            PicoScope.Serial = DeviceAssetCatalog.NormalizeIdentity(PicoScope.Serial);
            Art.Model = DeviceAssetCatalog.NormalizeIdentity(Art.Model);
            Art.Serial = DeviceAssetCatalog.NormalizeIdentity(Art.Serial);
            CalibrationCoefficientSource = String.IsNullOrWhiteSpace(CalibrationCoefficientSource)
                ? String.Empty
                : CalibrationCoefficientSource.Trim();
            if (ValidationFlags == null) ValidationFlags = new List<string>();
        }
    }

    internal sealed class DeviceModuleIdentity
    {
        public string Model { get; set; }
        public string Serial { get; set; }
    }

    internal sealed class DeviceInductorParameters
    {
        public double? InductanceHenry { get; set; }
        public double? DcResistanceOhm { get; set; }
    }

    internal sealed class DeviceCompensationParameters
    {
        public bool? Selected { get; set; }
        public double? CapacitanceNanoFarad { get; set; }
        public double? VoltageKilovolt { get; set; }
    }

    internal sealed class DeviceAssetCatalogProvenance
    {
        public string SourceDocument { get; set; }
        public string SourceSha256 { get; set; }
        public string Worksheet { get; set; }
        public string SourceRange { get; set; }
        public string PrivacyPolicy { get; set; }
    }

    internal sealed class DeviceAssetNumberingRules
    {
        public string Version { get; set; }
        public object MainDeviceSerial { get; set; }
        public object TypeNumber { get; set; }
        public object Codes { get; set; }
        public string Usage { get; set; }
    }

    internal sealed class DeviceAssetCatalogAudit
    {
        public int SourceRows { get; set; }
        public DeviceIdentifierAudit Factory { get; set; }
        public DeviceIdentifierAudit Pico { get; set; }
        public DeviceIdentifierAudit Art { get; set; }
        public int PairedRows { get; set; }
        public int AutoMatchEligibleRows { get; set; }
        public int ExcludedRows { get; set; }
        public Dictionary<string, int> ExclusionReasonRows { get; set; }
    }

    internal sealed class DeviceIdentifierAudit
    {
        public int NonEmptyRows { get; set; }
        public int DistinctKeys { get; set; }
        public int UniqueKeys { get; set; }
        public int DuplicateKeys { get; set; }
        public int DuplicateRows { get; set; }
        public int ConflictKeys { get; set; }
        public int ConflictRows { get; set; }
    }
}
