using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Xiongxin.HvDacXpert
{
    static partial class Integration
    {
        // Inspect the exact pair previously imported from the vendor binding UI.
        // No Silex scan/status/Connect/Apply/Disconnect call and no task/OpenUnit.
        // MAC/locations describe the saved association, not fresh hub evidence.
        internal static XDocument CheckLocal(bool enabled,string mac,string controllerLocation,string acquisitionLocation,
            string artSerial,string picoSerial,Func<ushort,ushort,List<string>> present=null,
            Func<List<string>> picoUnits=null,Func<List<ArtRecord>> artRecords=null)
        {
            mac=Normalize(mac);artSerial=(artSerial??"").Trim();picoSerial=(picoSerial??"").Trim().ToUpperInvariant();
            Func<string,string,XDocument> result=(state,message)=>{
                var doc=Result(state,mac,message);doc.Root.Add(new XAttribute("InspectionMode","LocalBoundPair"));return doc;
            };
            if(!enabled)return result("Disabled","Offline mode; no inspection attempted.");
            if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$")||!ValidDisconnectLocation(controllerLocation??"")||
                !ValidDisconnectLocation(acquisitionLocation??"")||String.Equals(controllerLocation,acquisitionLocation,StringComparison.OrdinalIgnoreCase)||
                !Regex.IsMatch(artSerial,"^[0-9]{10}$")||!Regex.IsMatch(picoSerial,"^[A-Z0-9]{5}/[0-9]{4}$"))
                return result("NeedsConfiguration","Use device management to import the vendor-bound ART/Pico identities first.");
            try {
                present=present??ModuleIdentity.PresentInstances;picoUnits=picoUnits??ModuleIdentity.EnumeratePicoSerials;artRecords=artRecords??ArtAliases.Read;
                string artInstance="USB\\VID_3063&PID_3135\\"+artSerial;
                var artPresent=present(0x3063,0x3135);
                if(artPresent.Count(x=>String.Equals(x,artInstance,StringComparison.OrdinalIgnoreCase))!=1)
                    return result("NotReady","The saved ART device is not present.");
                string alias=ModuleIdentity.ResolveArtDeviceName(artSerial,artInstance,artRecords());
                if(alias.Length==0)return result("NotReady","The saved ART device does not have a unique started driver alias.");
                var picoPresent=present(0x0CE9,0x1218);
                var units=picoUnits();
                if(picoPresent.Count!=1||!Regex.IsMatch(picoPresent[0]??"","^USB\\\\VID_0CE9&PID_1218\\\\[^\\\\]+$",RegexOptions.IgnoreCase)||
                    units.Count!=1||!String.Equals((units[0]??"").Trim(),picoSerial,StringComparison.OrdinalIgnoreCase))
                    return result("NotReady","Pico presence or serial association is absent or ambiguous.");
                // With more than one matching PnP node the SDK serial-to-node
                // association is ambiguous. Never guess a node for the saved unit.
                var pico=ModuleIdentity.ResolveEvidence(0x0CE9,0x1218,picoPresent,picoPresent,units);
                if(pico.Serial!=picoSerial)return result("NotReady","The saved Pico identity is absent or ambiguous.");
                var ready=result("Ready","Saved ART/Pico pair verified locally. Hub transport and electrical interlocks are not asserted.");
                ready.Root.Add(new XElement("Module",new XAttribute("Kind","Controller"),new XAttribute("Location",controllerLocation),
                    new XAttribute("Connected",true),new XAttribute("Ready",true),new XAttribute("Serial",artSerial),
                    new XAttribute("TransportSerial",artSerial),new XAttribute("InstanceId",artInstance),new XAttribute("DeviceName",alias)));
                ready.Root.Add(new XElement("Module",new XAttribute("Kind","Acquisition"),new XAttribute("Location",acquisitionLocation),
                    new XAttribute("Connected",true),new XAttribute("Ready",true),new XAttribute("Serial",pico.Serial),
                    new XAttribute("TransportSerial",pico.TransportSerial),new XAttribute("InstanceId",pico.InstanceId)));
                return ready;
            }catch(PicoInspectionBusyException error){
                var busy=result("Busy","Device inspection is temporarily unavailable while the device is in use.");
                busy.Root.Add(new XElement("InspectionDiagnostic",error.Message));return busy;
            }catch(Exception error){return result("Error",error.Message);}
        }
    }
}
