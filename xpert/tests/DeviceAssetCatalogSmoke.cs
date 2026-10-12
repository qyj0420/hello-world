using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Desktop;

internal static class DeviceAssetCatalogSmoke
{
    private static int Main(string[] args)
    {
        try
        {
            if (args == null || args.Length != 2)
                throw new ArgumentException("Usage: DeviceAssetCatalogSmoke <catalog-json> <source-xlsx>");
            string catalogPath = Path.GetFullPath(args[0]);
            string workbookPath = Path.GetFullPath(args[1]);
            string raw = File.ReadAllText(catalogPath, Encoding.UTF8);
            Require(raw.IndexOf('\uFFFD') < 0, "Catalog contains Unicode replacement characters.");

            DeviceAssetCatalog catalog = DeviceAssetCatalog.Load(catalogPath);
            Require(catalog.Provenance.SourceDocument == "振荡波出厂编号标定240827(已自动还原).xlsx", "Source filename provenance changed.");
            Require(catalog.Provenance.Worksheet == "出厂记录", "Worksheet provenance changed.");
            Require(catalog.Provenance.SourceRange == "A2:Y87", "Source range provenance changed.");
            Require(catalog.NumberingRules != null && catalog.NumberingRules.Version == "1", "Numbering rules are missing.");
            Require(String.Equals(catalog.Provenance.SourceSha256, Sha256(workbookPath), StringComparison.Ordinal), "Source SHA-256 does not match.");
            Require(catalog.Audit.SourceRows == 86, "Unexpected source row count.");
            Require(catalog.Audit.Pico.NonEmptyRows == 73 && catalog.Audit.Pico.UniqueKeys == 67 &&
                catalog.Audit.Pico.DuplicateKeys == 3 && catalog.Audit.Pico.DuplicateRows == 6 &&
                catalog.Audit.Pico.ConflictKeys == 3 && catalog.Audit.Pico.ConflictRows == 6,
                "Unexpected Pico audit totals.");
            Require(catalog.Audit.Art.NonEmptyRows == 64 && catalog.Audit.Art.UniqueKeys == 58 &&
                catalog.Audit.Art.DuplicateKeys == 3 && catalog.Audit.Art.DuplicateRows == 6 &&
                catalog.Audit.Art.ConflictKeys == 3 && catalog.Audit.Art.ConflictRows == 6,
                "Unexpected ART audit totals.");
            Require(catalog.Audit.PairedRows == 64 && catalog.Audit.AutoMatchEligibleRows == 56 && catalog.Audit.ExcludedRows == 30,
                "Unexpected catalog eligibility totals.");
            Require(catalog.Records.All(record => record.DeviceModel == null || record.DeviceModel.IndexOf('\uFFFD') < 0),
                "A device model contains invalid Unicode.");
            Require(catalog.Records.Where(record => record.AutoMatchEligible).All(record =>
                !String.IsNullOrWhiteSpace(record.DeviceModel) && record.DividerRatio.HasValue && record.Inductor != null &&
                record.Inductor.InductanceHenry.HasValue && record.Inductor.DcResistanceOhm.HasValue &&
                record.CouplingCapacitanceNanoFarad.HasValue && record.Compensation != null &&
                record.Compensation.Selected.HasValue && !String.IsNullOrWhiteSpace(record.CalibrationCoefficientSource)),
                "An eligible record is missing required audit metadata.");

            DeviceAssetRecord eligible = catalog.Records.First(record => record.AutoMatchEligible);
            HardwareStatusSnapshot ready = Ready(eligible.PicoScope.Serial, eligible.Art.Serial);
            DeviceAssetMatchResult match = catalog.Match(ready);
            Require(match.IsMatch && Object.ReferenceEquals(match.Record, eligible), "Unique real serial pair did not match.");

            HardwareStatusSnapshot swapped = Ready(eligible.Art.Serial, eligible.PicoScope.Serial);
            Require(catalog.Match(swapped).IsMatch, "Valid endpoints should match regardless of module role order.");
            Require(catalog.Match(Ready("ZZZZZ/9999", "9999999999")).Status == "Unknown", "Unknown identities must not match.");

            DeviceAssetRecord duplicate = catalog.Records.First(record =>
                record.ValidationFlags.Contains("DuplicatePicoSerial") || record.ValidationFlags.Contains("DuplicateArtSerial"));
            DeviceAssetMatchResult ambiguous = catalog.Match(Ready(duplicate.PicoScope.Serial, duplicate.Art.Serial));
            Require(!ambiguous.IsMatch && (ambiguous.Status == "Conflict" || ambiguous.Status == "Rejected"),
                "Duplicate/conflicting identities must not match.");

            string temporaryRoot = Path.Combine(Path.GetTempPath(), "HVX-asset-catalog-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                VerifyAtomicApplication(eligible, temporaryRoot);
                VerifyActualHardwareIdentities(catalog, eligible, temporaryRoot);
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, true); } catch { }
            }

            Console.WriteLine("PASS DeviceAssetCatalogSmoke rows=86 paired=64 eligible=56 excluded=30 pico(nonempty=73,uniqueKeys=67,duplicateKeys=3,duplicateRows=6,conflictKeys=3,conflictRows=6) art(nonempty=64,uniqueKeys=58,duplicateKeys=3,duplicateRows=6,conflictKeys=3,conflictRows=6) replacementChars=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyAtomicApplication(DeviceAssetRecord eligible, string root)
    {
        string settingsPath = Path.Combine(root, "settings.xml");
        AppSettings settings = new AppSettings
        {
            DividerRatio = 123.0,
            InductanceHenry = 4.5,
            InductorDcResistanceOhm = 6.7,
            PdCalibrationCorrectionFactor = 1.2345
        };
        double manualCalibrationMultiplier = settings.PdCalibrationCorrectionFactor;
        bool changed;
        string error;
        Require(DeviceAssetParameterApplier.TryApply(settings, eligible, settingsPath, out changed, out error),
            "Eligible parameters were not applied: " + error);
        Require(changed && File.Exists(settingsPath), "Changed parameters were not atomically persisted.");
        Require(settings.DividerRatio == eligible.DividerRatio.Value &&
            settings.InductanceHenry == eligible.Inductor.InductanceHenry.Value &&
            settings.InductorDcResistanceOhm == eligible.Inductor.DcResistanceOhm.Value,
            "The three synonymous parameters were not applied.");
        Require(settings.PdCalibrationCorrectionFactor == manualCalibrationMultiplier,
            "Workbook calibration coefficient must not overwrite the manual PD correction multiplier.");

        AppSettings reloaded;
        string message;
        Require(AppSettings.TryLoad(settingsPath, out reloaded, out message), "Persisted settings could not be reloaded: " + message);
        Require(reloaded.DividerRatio == settings.DividerRatio && reloaded.InductanceHenry == settings.InductanceHenry &&
            reloaded.InductorDcResistanceOhm == settings.InductorDcResistanceOhm,
            "Persisted auto-applied parameters changed after reload.");
    }

    private static void VerifyActualHardwareIdentities(DeviceAssetCatalog catalog, DeviceAssetRecord eligible, string root)
    {
        string markerPath = Path.Combine(root, "driver.marker");
        string statusPath = Path.Combine(root, "hardware-status.json");
        File.WriteAllText(markerPath, "ready", Encoding.ASCII);

        AppSettings settings = new AppSettings();
        settings.Hardware.Enabled = true;
        settings.Hardware.SelectedHubMac = "1CBCEC5A9C5C";
        settings.Hardware.ControllerLocation = "H11";
        settings.Hardware.AcquisitionLocation = "S51";
        settings.Hardware.ControllerSerial = "CONFIGURED-CONTROLLER-NOTE";
        settings.Hardware.AcquisitionSerial = "CONFIGURED-ACQUISITION-NOTE";
        settings.Hardware.ExcludedAcquisitionSerial = String.Empty;
        settings.Hardware.DriverMarkerPath = markerPath;
        settings.Hardware.StatusJsonPath = statusPath;
        settings.Hardware.IntegrationExecutablePath = String.Empty;
        settings.Hardware.ConnectorExecutablePath = String.Empty;

        WriteStatus(statusPath, eligible.PicoScope.Serial, eligible.Art.Serial, true);
        using (HardwareStatusAdapter adapter = new HardwareStatusAdapter(settings))
        {
            HardwareStatusSnapshot snapshot = adapter.Refresh(false);
            Require(snapshot.Ready, "Fresh real-identity status was not accepted.");
            Require(snapshot.Message == "指定设备已连接。" && snapshot.Message.IndexOf("DS600", StringComparison.OrdinalIgnoreCase) < 0,
                "Raw connector message leaked into customer-facing hardware status.");
            Require(snapshot.ControllerSerial == DeviceAssetCatalog.NormalizeIdentity(eligible.PicoScope.Serial) &&
                snapshot.AcquisitionSerial == DeviceAssetCatalog.NormalizeIdentity(eligible.Art.Serial),
                "Snapshot did not use ParsedModule.Identity actual serials.");
            Require(snapshot.ControllerSerial != DeviceAssetCatalog.NormalizeIdentity(settings.Hardware.ControllerSerial) &&
                snapshot.AcquisitionSerial != DeviceAssetCatalog.NormalizeIdentity(settings.Hardware.AcquisitionSerial),
                "Configured notes leaked into the actual hardware snapshot.");

            WriteStatus(statusPath, eligible.PicoScope.Serial, eligible.Art.Serial, false);
            HardwareStatusSnapshot missing = adapter.Refresh(false);
            Require(missing.Ready && missing.ControllerSerial.Length == 0 && missing.AcquisitionSerial.Length == 0,
                "A module Location was accepted as an actual serial.");
            Require(catalog.Match(missing).Status == "ActualSerialUnavailable",
                "Missing real serials were not reported explicitly.");

            settings.Hardware.ExcludedAcquisitionSerial = eligible.Art.Serial;
            HardwareStatusSnapshot excludedByTransport = adapter.Refresh(false);
            Require(!excludedByTransport.Ready && excludedByTransport.StatusCode == "ExcludedDevice",
                "Configured transport exclusion did not reject the matching PnP instance identity.");
        }
    }

    private static HardwareStatusSnapshot Ready(string first, string second)
    {
        return new HardwareStatusSnapshot
        {
            HardwareEnabled = true,
            DriverInstalled = true,
            Connected = true,
            IdentityValid = true,
            StatusCode = "Ready",
            ControllerSerial = first,
            AcquisitionSerial = second,
            CheckedUtc = DateTime.UtcNow
        };
    }

    private static void WriteStatus(string path, string controllerSerial, string acquisitionSerial, bool includeSerials)
    {
        Dictionary<string, object> controller = new Dictionary<string, object>
        {
            { "Kind", "Controller" }, { "Location", "H11" }, { "Connected", true }, { "Ready", true },
            { "InstanceId", "USB\\VID_0000&PID_0000\\" + controllerSerial }
        };
        Dictionary<string, object> acquisition = new Dictionary<string, object>
        {
            { "Kind", "Acquisition" }, { "Location", "S51" }, { "Connected", true }, { "Ready", true },
            { "InstanceId", "USB\\VID_0000&PID_0000\\" + acquisitionSerial }
        };
        if (includeSerials)
        {
            controller.Add("Serial", controllerSerial);
            acquisition.Add("Serial", acquisitionSerial);
        }
        Dictionary<string, object> root = new Dictionary<string, object>
        {
            { "Schema", 1 }, { "State", "Ready" }, { "Mac", "1CBCEC5A9C5C" },
            { "Message", "DS600 internal device not found" },
            { "Utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "MaxAgeSeconds", 30 },
            { "Modules", new object[] { controller, acquisition } }
        };
        File.WriteAllText(path, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
    }

    private static string Sha256(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (SHA256 algorithm = SHA256.Create())
        {
            byte[] hash = algorithm.ComputeHash(stream);
            StringBuilder text = new StringBuilder(hash.Length * 2);
            for (int index = 0; index < hash.Length; index++) text.Append(hash[index].ToString("X2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
