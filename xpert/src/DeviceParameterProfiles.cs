using System;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    // Profiles and current settings share ONE atomic settings transaction. The
    // profile key is a board pair, not a transport address or connection time.
    internal static class DeviceParameterProfiles
    {
        static readonly string[] Fields = { "DividerRatio", "DeviceTriggerVolts", "InductanceHenry",
            "InductorDcResistanceOhm", "MaximumVoltageKv", "CompensationCapacitanceUf",
            "SupportsCosineVlf", "SupportsCombinedVlf", "EnableFinePressureRamp", "FinePressureThresholdAutomatic", "FinePressureThresholdKv",
            "PressureRampStepV", "PressureRampIntervalMs", "EndResidualPercent" };

        internal static double PressureRatioFor(double maximumKv)
        {
            if (Double.IsNaN(maximumKv) || Double.IsInfinity(maximumKv) || maximumKv <= 0 || maximumKv > 1000000)
                throw new ArgumentException("最高限压必须为有效的正数。");
            return maximumKv * 1000.0 / 5.0;
        }
        internal static string Pair(string first, string second)
        {
            first = DeviceAssetCatalog.NormalizeIdentity(first); second = DeviceAssetCatalog.NormalizeIdentity(second);
            if (DeviceAssetCatalog.IsArtSerial(first) && DeviceAssetCatalog.IsPicoSerial(second)) return first + "|" + second;
            if (DeviceAssetCatalog.IsArtSerial(second) && DeviceAssetCatalog.IsPicoSerial(first)) return second + "|" + first;
            return String.Empty;
        }
        internal static string Key(HardwareStatusSnapshot actual)
        { return actual != null && actual.Ready && DeviceSetupStatus.ActualIdentity(actual).Length != 0 ? Pair(actual.ControllerSerial, actual.AcquisitionSerial) : String.Empty; }
        internal static string ConfiguredKey(AppSettings s)
        { return s == null || s.Hardware == null ? String.Empty : Pair(s.Hardware.ControllerSerial, s.Hardware.AcquisitionSerial); }
        static XmlDocument Read(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            if (String.IsNullOrEmpty(xml)) { doc.LoadXml("<DeviceProfiles version=\"1\"/>"); return doc; }
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 })) doc.Load(reader);
            if (doc.DocumentElement == null || doc.DocumentElement.Name != "DeviceProfiles" || doc.DocumentElement.GetAttribute("version") != "1") throw new InvalidDataException("设备配置记录无法读取，请恢复有效配置。");
            var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                var e = node as XmlElement;
                if (e == null || e.Name != "Device" || !keys.Add(e.GetAttribute("key"))) throw new InvalidDataException("设备配置记录重复或不完整。");
                Validate(e);
            }
            return doc;
        }
        static XmlElement Find(XmlDocument doc, string key)
        {
            foreach (XmlElement e in doc.DocumentElement.ChildNodes) if (e.GetAttribute("key") == key) return e;
            return null;
        }
        static string Text(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture); }
        static void CopyFrom(XmlElement e, AppSettings s)
        {
            // Old pair records never silently enable a new control law, or borrow
            // a threshold from the previously selected device. A partially present
            // new pair of attributes is corruption, not a legacy record.
            bool fine = e.HasAttribute("EnableFinePressureRamp"), threshold = e.HasAttribute("FinePressureThresholdKv"), automatic = e.HasAttribute("FinePressureThresholdAutomatic");
            if (fine != threshold || fine != automatic) throw new InvalidDataException("设备精细化加压配置不完整，请核对后保存。");
            bool ramp=e.HasAttribute("PressureRampStepV");
            if(ramp!=e.HasAttribute("PressureRampIntervalMs"))throw new InvalidDataException("设备普通加压速度配置不完整，请核对后保存。");
            foreach (string name in Fields)
            {
                if(name=="EndResidualPercent"&&!e.HasAttribute(name)){s.EndResidualPercent=4;continue;}
                if(!ramp&&(name=="PressureRampStepV"||name=="PressureRampIntervalMs")){
                    if(name=="PressureRampStepV")s.PressureRampStepV=.01;else s.PressureRampIntervalMs=1;
                    continue;
                }
                if (!fine && (name == "EnableFinePressureRamp" || name == "FinePressureThresholdKv" || name == "FinePressureThresholdAutomatic")) {
                    if (name == "EnableFinePressureRamp") s.EnableFinePressureRamp = false;
                    else if (name == "FinePressureThresholdAutomatic") s.FinePressureThresholdAutomatic = true;
                    else s.FinePressureThresholdKv = s.MaximumVoltageKv * .15;
                    continue;
                }
                var p = typeof(AppSettings).GetProperty(name); string text = e.GetAttribute(name);
                object value = p.PropertyType == typeof(bool) ? (object)Boolean.Parse(text) :
                    p.PropertyType == typeof(int) ? (object)Int32.Parse(text,CultureInfo.InvariantCulture) : Double.Parse(text, CultureInfo.InvariantCulture);
                if (name == "FinePressureThresholdKv" &&
                    (Double.IsNaN((double)value) || Double.IsInfinity((double)value) || (double)value <= 0 || (double)value > 1000000))
                    throw new InvalidDataException("设备精细化加压阈值无效，请核对后保存。");
                p.SetValue(s, value, null);
            }
            // Stored automatic values are snapshots, not an independently editable
            // override. The current device's maximum remains their sole source.
            s.PressureRatio = PressureRatioFor(s.MaximumVoltageKv);
        }
        static void Validate(XmlElement e)
        {
            string[] ids = e.GetAttribute("key").Split('|'); DateTime when;
            if (ids.Length != 2 || Pair(ids[0], ids[1]) != e.GetAttribute("key") ||
                !DateTime.TryParse(e.GetAttribute("updatedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when) || when == DateTime.MinValue || when.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5))
                throw new InvalidDataException("设备配置的身份或保存时间无效。");
            var s = new AppSettings(); CopyFrom(e, s); string message;
            if (!s.TryValidate(out message)) throw new InvalidDataException(message);
        }
        internal static bool Has(AppSettings s, string key)
        { return !String.IsNullOrEmpty(key) && Find(Read(s.DeviceProfilesXml), key) != null; }
        internal static bool SameValues(AppSettings a, AppSettings b)
        {
            foreach (string name in Fields) { var p = typeof(AppSettings).GetProperty(name); if (!Object.Equals(p.GetValue(a, null), p.GetValue(b, null))) return false; }
            return true;
        }
        internal static bool ValuesMatch(AppSettings s, string key)
        {
            var e = Find(Read(s.DeviceProfilesXml), key); if (e == null) return false;
            var record = s.Clone(); CopyFrom(e, record);
            return SameValues(s, record) && s.PressureRatio == record.PressureRatio;
        }
        static bool ExpertOnlyChange(AppSettings candidate, string key)
        {
            // Compare to the unchanged loaded pair record, not to another
            // window's draft. Expert authorization cannot replace that record
            // or edit limits, divider, identity or fine-ramp configuration.
            if (!String.Equals(candidate.DeviceProfilesXml ?? String.Empty,
                candidate.DeviceProfilesOriginXml ?? String.Empty, StringComparison.Ordinal)) return false;
            var e = Find(Read(candidate.DeviceProfilesXml), key);
            if (e == null) return false;
            var record = candidate.Clone(); CopyFrom(e, record);
            foreach (string name in Fields) {
                if (name == "PressureRampStepV" || name == "PressureRampIntervalMs") continue;
                var p = typeof(AppSettings).GetProperty(name);
                if (!Object.Equals(p.GetValue(candidate, null), p.GetValue(record, null))) return false;
            }
            return candidate.PressureRatio == record.PressureRatio;
        }
        internal static bool IsConfigured(AppSettings s, HardwareStatusSnapshot actual)
        {
            try {
                string key = Key(actual);
                return key.Length != 0 && key == ConfiguredKey(s) && key == s.ActiveDeviceProfileKey &&
                    ValuesMatch(s, key) && DeviceSetupStatus.IdentityMatches(s, actual);
            } catch { return false; }
        }
        internal static bool Load(AppSettings candidate, HardwareStatusSnapshot actual)
        {
            string key = Key(actual);
            if (key.Length == 0 || key != ConfiguredKey(candidate)) return false;
            XmlElement record = Find(Read(candidate.DeviceProfilesXml), key); if (record == null) return false;
            CopyFrom(record, candidate); candidate.ActiveDeviceProfileKey = key;
            DeviceSetupStatus.ConfirmParameters(candidate, actual);
            return true;
        }
        internal static void Confirm(AppSettings candidate, HardwareStatusSnapshot actual)
        {
            string key = Key(actual);
            if (key.Length == 0 || key != ConfiguredKey(candidate)) throw new InvalidOperationException("设备身份已改变，请重新连接后设置。");
            bool exists = Has(candidate, key);
            if (exists && !candidate.DeviceProfileEditAuthorized) throw new InvalidOperationException("请在参数窗口确认并保存修改。");
            candidate.PressureRatio = PressureRatioFor(candidate.MaximumVoltageKv);
            DeviceSetupStatus.ConfirmParameters(candidate, actual);
            if (!DeviceSetupStatus.IdentityMatches(candidate, actual)) throw new InvalidOperationException("当前设备与指定设备不一致，未保存配置。");
            candidate.ActiveDeviceProfileKey = key; candidate.DeviceProfileEditAuthorized = true;
            Write(candidate, key);
        }
        static void Write(AppSettings s, string key)
        {
            var doc = Read(s.DeviceProfilesXml); var e = Find(doc, key);
            if (e == null) { e = doc.CreateElement("Device"); doc.DocumentElement.AppendChild(e); e.SetAttribute("key", key); }
            foreach (string name in Fields) e.SetAttribute(name, Text(typeof(AppSettings).GetProperty(name).GetValue(s, null)));
            e.SetAttribute("updatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); Validate(e);
            s.DeviceProfilesXml = doc.OuterXml;
        }
        internal static void PrepareSave(AppSettings candidate, string path)
        {
            // Compare the profile generation even for ordinary settings saves.
            // A stale window must not revert another window's device record.
            if (File.Exists(path)) {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4194304 })) doc.Load(reader);
                var stored = doc.DocumentElement.SelectSingleNode("DeviceProfilesXml");
                if ((stored == null ? String.Empty : stored.InnerText) != (candidate.DeviceProfilesOriginXml ?? String.Empty))
                    throw new InvalidOperationException("设备参数已由另一窗口更新，请重新打开设置。");
            }
            Read(candidate.DeviceProfilesXml);
            string key = candidate.ActiveDeviceProfileKey ?? String.Empty;
            if (key.Length == 0) return;
            if (key != ConfiguredKey(candidate)) {
                candidate.ActiveDeviceProfileKey = String.Empty; candidate.DeviceParametersReviewedIdentity = String.Empty;
                candidate.DeviceParametersReviewedHash = String.Empty; return;
            }
            candidate.PressureRatio = PressureRatioFor(candidate.MaximumVoltageKv);
            if (!Has(candidate, key)) throw new InvalidDataException("当前设备配置记录缺失，请重新设置。");
            if (!ValuesMatch(candidate, key)) {
                if (!candidate.DeviceProfileEditAuthorized &&
                    !(candidate.ExpertDeviceProfileEditAuthorized && ExpertOnlyChange(candidate, key)))
                    throw new InvalidOperationException("请在参数窗口确认并保存修改。");
                Write(candidate, key);
                if (!String.IsNullOrEmpty(candidate.DeviceParametersReviewedIdentity)) {
                    candidate.DeviceParametersReviewedHash = DeviceSetupStatus.ParameterHash(candidate);
                    candidate.DeviceParametersReviewedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                }
            }
        }
    }
}
