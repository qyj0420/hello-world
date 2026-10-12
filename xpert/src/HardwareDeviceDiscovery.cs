using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    // Discovery is a short-lived list, never a Ready status or an HV permit.
    internal sealed class DetectionDevice
    {
        internal string Mac { get; private set; }
        internal string Ip { get; private set; }
        internal string ControllerLocation { get; private set; }
        internal string AcquisitionLocation { get; private set; }
        internal bool PersistentBound { get; private set; }
        internal bool Complete { get; private set; }
        internal string State { get; private set; }
        internal bool Selectable { get { return Complete && State == "Available"; } }
        internal string Key { get { return Mac + "|" + ControllerLocation + "|" + AcquisitionLocation; } }
        internal DetectionDevice(string mac, string ip, string controller, string acquisition, bool bound, bool complete, string state)
        { Mac=mac; Ip=ip; ControllerLocation=controller; AcquisitionLocation=acquisition; PersistentBound=bound; Complete=complete; State=state; }
    }

    internal sealed class DetectionDeviceList
    {
        internal DateTime ProducedUtc { get; private set; }
        internal ReadOnlyCollection<DetectionDevice> Devices { get; private set; }
        internal DetectionDeviceList(DateTime utc, List<DetectionDevice> devices)
        { if(devices==null) throw new ArgumentNullException("devices"); ProducedUtc=utc; Devices=new List<DetectionDevice>(devices).AsReadOnly(); }
        internal bool IsFresh(DateTime utc)
        { return ProducedUtc.Kind==DateTimeKind.Utc && utc.Kind==DateTimeKind.Utc && utc>=ProducedUtc && utc-ProducedUtc<=TimeSpan.FromSeconds(30); }
        internal bool CanSelect(DetectionDevice device, DateTime utc)
        { return IsFresh(utc) && device!=null && device.Selectable && Devices.Any(d=>Object.ReferenceEquals(d,device)); }
    }

    internal static class HardwareDeviceDiscovery
    {
        internal const string SystemName="HV DAC Xpert System";
        internal const string ControllerName="HV DAC Xpert Controller";
        internal const string AcquisitionName="HV DAC Xpert Acquisition";
        internal static DetectionDeviceList Parse(string xml, DateTime utc)
        {
            if (xml==null || xml.Length>1024*1024) throw new InvalidDataException("Device discovery exceeds the permitted size.");
            var options=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=1024*1024 };
            var document=new XmlDocument { XmlResolver=null };
            using (var source=new StringReader(xml)) using(var reader=XmlReader.Create(source,options)) document.Load(reader);
            var root=document.DocumentElement;
            DateTime produced;
            if(root==null || root.Name!="HardwareDiscovery" || root.GetAttribute("Schema")!="1" || root.GetAttribute("State")!="Complete" ||
                root.GetAttribute("MaxAgeSeconds")!="30" || !DateTime.TryParse(root.GetAttribute("Utc"),CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,out produced) || produced.Kind!=DateTimeKind.Utc)
                throw new InvalidDataException("Device discovery is incomplete or invalid.");
            var devices=new List<DetectionDevice>(); var hubs=new HashSet<string>(StringComparer.Ordinal);
            bool messageSeen=false;
            foreach(XmlNode node in root.ChildNodes)
            {
                var element=node as XmlElement;
                if(element==null) continue;
                if(element.Name=="Message")
                { if(messageSeen || element.InnerText.Length>2048 || element.ChildNodes.OfType<XmlElement>().Any()) throw new InvalidDataException("Invalid discovery message."); messageSeen=true; continue; }
                if(element.Name!="Device" || element.ChildNodes.OfType<XmlElement>().Any() || devices.Count>=256)
                    throw new InvalidDataException("Device discovery has an invalid entry.");
                string mac=element.GetAttribute("Mac"), ip=element.GetAttribute("Ip");
                string controller=element.GetAttribute("ControllerLocation"), acquisition=element.GetAttribute("AcquisitionLocation"), state=element.GetAttribute("State");
                bool bound=StrictBoolean(element,"PersistentBound"), complete=StrictBoolean(element,"Complete");
                System.Net.IPAddress address;
                if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$") || !hubs.Add(mac) || !System.Net.IPAddress.TryParse(ip,out address) ||
                    address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork ||
                    !new[]{"Available","InUse","Incomplete","Ambiguous"}.Contains(state) ||
                    (complete && (!Location(controller) || !Location(acquisition) || controller==acquisition)) ||
                    (state=="Available" && !complete) || (state=="Ambiguous" && complete) || (bound && !complete) ||
                    (!complete && (controller.Length>64 || acquisition.Length>64)))
                    throw new InvalidDataException("Device discovery identity is invalid or ambiguous.");
                devices.Add(new DetectionDevice(mac,ip,controller,acquisition,bound,complete,state));
            }
            var result=new DetectionDeviceList(produced,devices);
            if(!result.IsFresh(utc)) throw new InvalidDataException("Device discovery expired; refresh the list.");
            return result;
        }
        static bool StrictBoolean(XmlElement node,string name)
        { string value=node.GetAttribute(name); if(value!="true" && value!="false") throw new InvalidDataException("Invalid discovery flag."); return value=="true"; }
        static bool Location(string value)
        { return value!=null && value.Length>0 && value.Length<=64 && Regex.IsMatch(value,"^[A-Z0-9_.:-]+$"); }

        static bool SelectableIdentity(DetectionDevice device)
        { return device!=null && device.Selectable && device.Mac!=null && Regex.IsMatch(device.Mac,"^[0-9A-F]{12}$") &&
            Location(device.ControllerLocation) && Location(device.AcquisitionLocation) && device.ControllerLocation!=device.AcquisitionLocation; }

        internal static DetectionDeviceList ParseFreshResult(string xml,DateTime startedUtc,DateTime completedUtc)
        {
            if(startedUtc.Kind!=DateTimeKind.Utc || completedUtc.Kind!=DateTimeKind.Utc || completedUtc<startedUtc)
                throw new InvalidDataException("Discovery request clock is invalid.");
            var receipt=Parse(xml,completedUtc);
            if(receipt.ProducedUtc<startedUtc) throw new InvalidDataException("Discovery output predates this request; refresh the list.");
            return receipt;
        }

        internal static Task<DetectionDeviceList> DiscoverAsync(CancellationToken token)
        {
            return Task.Run(delegate {
                token.ThrowIfCancellationRequested();
                FirstHardwareProvisioning.VerifyExactConnector();
                string executable=FirstHardwareProvisioning.GetVerifiedConnectorPath();
                string directory=Path.Combine(AppPaths.LocalDataDirectory,"hardware-discovery"); Directory.CreateDirectory(directory);
                string output=Path.Combine(directory,"d-"+Guid.NewGuid().ToString("N")+".xml");
                try
                {
                    DateTime startedUtc=DateTime.UtcNow;
                    using(var process=Process.Start(new ProcessStartInfo { FileName=executable, WorkingDirectory=Path.GetDirectoryName(executable),
                        Arguments="--discover --output=\""+output+"\"", UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden }))
                    {
                        if(process==null) throw new IOException("Discovery did not start.");
                        var clock=Stopwatch.StartNew();
                        while(!process.WaitForExit(100))
                            if(token.IsCancellationRequested || clock.ElapsedMilliseconds>15000)
                            { try { process.Kill(); process.WaitForExit(2000); } catch { } token.ThrowIfCancellationRequested(); throw new TimeoutException("Discovery timed out."); }
                        token.ThrowIfCancellationRequested();
                        if(process.ExitCode!=0 || !File.Exists(output) || new FileInfo(output).Length>1024*1024)
                            throw new InvalidDataException("No complete device discovery was returned.");
                    }
                    FirstHardwareProvisioning.VerifyExactConnector();
                    token.ThrowIfCancellationRequested();
                    if((File.GetAttributes(output)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Discovery output cannot be redirected.");
                    string xml;
                    using(var stream=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        if(stream.Length<=0 || stream.Length>1024*1024) throw new InvalidDataException("Device discovery exceeds the permitted size.");
                        using(var reader=new StreamReader(stream)) xml=reader.ReadToEnd();
                    }
                    return ParseFreshResult(xml,startedUtc,DateTime.UtcNow);
                }
                finally { try { if(File.Exists(output)) File.Delete(output); } catch { } }
            },token);
        }

        internal static AppSettings SelectionProbe(AppSettings basis, DetectionDevice selected)
        {
            if(basis==null || basis.Hardware==null || !SelectableIdentity(selected)) throw new InvalidOperationException("Select a complete available device.");
            var next=basis.Clone(); next.Hardware.Enabled=true;
            next.Hardware.SelectedHubMac=selected.Mac; next.Hardware.ControllerLocation=selected.ControllerLocation; next.Hardware.AcquisitionLocation=selected.AcquisitionLocation;
            next.Hardware.ControllerSerial=next.Hardware.AcquisitionSerial=String.Empty;
            next.Hardware.StatusJsonPath=next.Hardware.DriverMarkerPath=next.Hardware.ConnectorExecutablePath=String.Empty;
            next.Hardware.IntegrationExecutablePath=FirstHardwareProvisioning.GetVerifiedConnectorPath();
            next.DeviceParametersReviewedHash=next.DeviceParametersReviewedIdentity=next.DeviceParametersReviewedUtc=String.Empty;
            return next;
        }
        internal static bool ExactSelection(DetectionDevice device,HardwareStatusSnapshot snapshot,DateTime utc)
        {
            return SelectableIdentity(device) && snapshot!=null && snapshot.Ready && snapshot.HasLiveIdentityReadback &&
                snapshot.CheckedUtc.Kind==DateTimeKind.Utc && utc.Kind==DateTimeKind.Utc && utc>=snapshot.CheckedUtc && utc-snapshot.CheckedUtc<=TimeSpan.FromSeconds(30) &&
                snapshot.HubMac==device.Mac && snapshot.RequestedHubMac==device.Mac && snapshot.RequestedControllerLocation==device.ControllerLocation &&
                snapshot.RequestedAcquisitionLocation==device.AcquisitionLocation && !String.IsNullOrWhiteSpace(snapshot.ControllerInstanceId) &&
                !String.IsNullOrWhiteSpace(snapshot.AcquisitionInstanceId) && !String.IsNullOrWhiteSpace(snapshot.ControllerTransportSerial) &&
                !String.IsNullOrWhiteSpace(snapshot.AcquisitionTransportSerial) &&
                !String.IsNullOrWhiteSpace(snapshot.ControllerSerial) && !String.IsNullOrWhiteSpace(snapshot.AcquisitionSerial) &&
                MatchingInstance(snapshot.ControllerInstanceId,"3063","3135") && MatchingInstance(snapshot.AcquisitionInstanceId,"0CE9","1218") &&
                MatchingTransport(snapshot.ControllerInstanceId,snapshot.ControllerTransportSerial) && MatchingTransport(snapshot.AcquisitionInstanceId,snapshot.AcquisitionTransportSerial) &&
                SameConnectorPath(snapshot.IntegrationExecutablePathUsed) && !String.Equals(snapshot.ControllerInstanceId,snapshot.AcquisitionInstanceId,StringComparison.OrdinalIgnoreCase);
        }
        static bool MatchingInstance(string value,string vid,string pid)
        { return value.Length<=512 && Regex.IsMatch(value,"^USB\\\\VID_"+vid+"&PID_"+pid+"(?:&[^\\\\]+)?\\\\[^\\\\]+$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant); }
        static bool MatchingTransport(string instance,string serial)
        { int index=instance.LastIndexOf('\\'); return serial.Length<=512 && index>=0 && String.Equals(instance.Substring(index+1),serial,StringComparison.OrdinalIgnoreCase); }
        internal static bool SameConfiguredPair(AppSettings basis,DetectionDevice device)
        { return basis!=null && basis.Hardware!=null && device!=null && NormalizeIdentity(basis.Hardware.SelectedHubMac).Replace(":",String.Empty).Replace("-",String.Empty)==device.Mac &&
            NormalizeIdentity(basis.Hardware.ControllerLocation)==device.ControllerLocation && NormalizeIdentity(basis.Hardware.AcquisitionLocation)==device.AcquisitionLocation; }
        static string NormalizeIdentity(string value) { return (value??String.Empty).Trim().ToUpperInvariant(); }
        internal static bool SavedSerialsMatch(AppSettings basis,DetectionDevice device,HardwareStatusSnapshot snapshot)
        {
            if(basis==null || basis.Hardware==null || snapshot==null) return false;
            if(!SameConfiguredPair(basis,device)) return true; // New device requires parameter review; it cannot inherit it.
            return (String.IsNullOrWhiteSpace(basis.Hardware.ControllerSerial) || NormalizeIdentity(basis.Hardware.ControllerSerial)==NormalizeIdentity(snapshot.ControllerSerial)) &&
                (String.IsNullOrWhiteSpace(basis.Hardware.AcquisitionSerial) || NormalizeIdentity(basis.Hardware.AcquisitionSerial)==NormalizeIdentity(snapshot.AcquisitionSerial));
        }
        static bool SameConnectorPath(string value)
        {
            try { return !String.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) && String.Equals(Path.GetFullPath(value),
                FirstHardwareProvisioning.GetVerifiedConnectorPath(),StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
