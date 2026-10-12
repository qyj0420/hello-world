using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Xiongxin.OWTS.Desktop;

internal static class HardwareRawIdentitySnapshotSmoke
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "HVX-raw-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            HardwareStatusSnapshot empty = new HardwareStatusSnapshot();
            Check(empty.ControllerInstanceId == String.Empty && empty.AcquisitionInstanceId == String.Empty &&
                empty.ControllerTransportSerial == String.Empty && empty.AcquisitionTransportSerial == String.Empty,
                "New snapshot properties must default to empty strings.");

            Directory.CreateDirectory(root);
            string marker = Path.Combine(root, "driver.marker");
            string status = Path.Combine(root, "status.xml");
            File.WriteAllText(marker, "test marker", Encoding.ASCII);
            AppSettings settings = new AppSettings();
            settings.Hardware.Enabled = true;
            settings.Hardware.SelectedHubMac = "1CBCEC5A9C5C";
            settings.Hardware.ControllerLocation = "H11";
            settings.Hardware.AcquisitionLocation = "S51";
            settings.Hardware.DriverMarkerPath = marker;
            settings.Hardware.StatusJsonPath = status;
            settings.Hardware.IntegrationExecutablePath = String.Empty;
            settings.Hardware.ConnectorExecutablePath = String.Empty;

            using (HardwareStatusAdapter adapter = new HardwareStatusAdapter(settings))
            {
                WriteXml(status);
                HardwareStatusSnapshot xml = adapter.Refresh(false);
                Check(xml.Ready, "XML connector status must remain ready: " + xml.StatusCode + "; " + xml.Diagnostic);
                Check(xml.ControllerSerial == "CTRL-123" && xml.AcquisitionSerial == "ACQ-456",
                    "Existing serial normalization must remain unchanged.");
                Check(xml.ControllerTransportSerial == "Ctrl-Transport" &&
                    xml.ControllerInstanceId == "USB\\VID_3063&PID_3135\\Ctrl-Transport" &&
                    xml.AcquisitionTransportSerial == "Acq-Transport" &&
                    xml.AcquisitionInstanceId == "USB\\VID_0CE9&PID_1218\\Acq-Transport",
                    "XML transport serials and instance IDs must remain distinct and raw.");
                HardwareStatusSnapshot clone = xml.Clone();
                Check(clone.ControllerInstanceId == xml.ControllerInstanceId &&
                    clone.AcquisitionTransportSerial == xml.AcquisitionTransportSerial,
                    "Snapshot clone must retain raw identity fields.");

                WriteJson(status);
                HardwareStatusSnapshot json = adapter.Refresh(false);
                Check(json.Ready, "JSON legacy status must remain ready.");
                Check(json.ControllerTransportSerial == "Ctrl-Only" && json.ControllerInstanceId == String.Empty &&
                    json.AcquisitionTransportSerial == String.Empty &&
                    json.AcquisitionInstanceId == "USB\\VID_0CE9&PID_1218\\Acq-Only",
                    "Absent raw fields must remain empty and must not be inferred from the other field.");
                settings.Hardware.ExcludedAcquisitionSerial = "Acq-Only";
                HardwareStatusSnapshot excluded = adapter.Refresh(false);
                Check(!excluded.Ready && excluded.StatusCode == "ExcludedDevice",
                    "Existing transport-identity exclusion fallback must remain unchanged.");
            }

            Console.WriteLine("PASS HardwareRawIdentitySnapshotSmoke XML JSON clone defaults exclusion");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static void WriteXml(string path)
    {
        XElement root = new XElement("HardwareStatus",
            new XAttribute("Schema", 1), new XAttribute("State", "Ready"),
            new XAttribute("Mac", "1CBCEC5A9C5C"),
            new XAttribute("Utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
            new XAttribute("MaxAgeSeconds", 30),
            new XElement("Module", new XAttribute("Kind", "Controller"), new XAttribute("Location", "H11"),
                new XAttribute("Serial", "ctrl-123"), new XAttribute("TransportSerial", "Ctrl-Transport"),
                new XAttribute("InstanceId", "USB\\VID_3063&PID_3135\\Ctrl-Transport"),
                new XAttribute("Connected", true), new XAttribute("Ready", true)),
            new XElement("Module", new XAttribute("Kind", "Acquisition"), new XAttribute("Location", "S51"),
                new XAttribute("Serial", "acq-456"), new XAttribute("TransportSerial", "Acq-Transport"),
                new XAttribute("InstanceId", "USB\\VID_0CE9&PID_1218\\Acq-Transport"),
                new XAttribute("Connected", true), new XAttribute("Ready", true)));
        File.WriteAllText(path, root.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
    }

    private static void WriteJson(string path)
    {
        var modules = new object[]
        {
            new Dictionary<string, object>
            {
                { "Kind", "Controller" }, { "Location", "H11" }, { "Serial", "ctrl-123" },
                { "TransportSerial", "Ctrl-Only" }, { "Connected", true }, { "Ready", true }
            },
            new Dictionary<string, object>
            {
                { "Kind", "Acquisition" }, { "Location", "S51" }, { "Serial", "acq-456" },
                { "InstanceId", "USB\\VID_0CE9&PID_1218\\Acq-Only" },
                { "Connected", true }, { "Ready", true }
            }
        };
        var root = new Dictionary<string, object>
        {
            { "Schema", 1 }, { "State", "Ready" }, { "Mac", "1CBCEC5A9C5C" },
            { "Utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
            { "MaxAgeSeconds", 30 }, { "Modules", modules }
        };
        File.WriteAllText(path, new JavaScriptSerializer().Serialize(root), new UTF8Encoding(false));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
