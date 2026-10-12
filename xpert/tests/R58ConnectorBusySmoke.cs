using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Xiongxin.HvDacXpert;
using Xiongxin.OWTS.Desktop;

internal static class R58ConnectorBusySmoke
{
    static int checks, configured, enumerated;
    const string Art="1234567890", Pico="AB123/1234", Mac="001122334455";
    const string ArtId="USB\\VID_3063&PID_3135\\1234567890", PicoId="USB\\VID_0CE9&PID_1218\\fixture";
    static void Assert(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static List<string> Enumerate(int configuration,uint status,short units=1)
    {
        configured=enumerated=0;
        return ModuleIdentity.EnumeratePicoSerials(new[]{"fake-sdk-a","fake-sdk-b"},p=>true,
            p=>{configured++;return configuration;},
            delegate(out short count,StringBuilder text,ref short length){enumerated++;count=units;text.Append(Pico);return status;});
    }
    static XDocument Inspect(Func<List<string>> units)
    {
        return Integration.CheckLocal(true,Mac,"1-1","1-2",Art,Pico,
            (v,p)=>new List<string>{v==0x3063?ArtId:PicoId},units,
            ()=>new List<ArtRecord>{new ArtRecord{Class=1,Bus=1,Type="USB3135",Name="Dev1",Serial=1234567890,Instance=ArtId,Status=8}});
    }
    static HardwareStatusSnapshot Validate(HardwareStatusAdapter adapter,XDocument doc,int exit)
    {
        var type=typeof(HardwareStatusAdapter);
        object parsed=type.GetMethod("ParseXml",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{doc.ToString()});
        return (HardwareStatusSnapshot)type.GetMethod("ValidateStatus",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,
            new object[]{parsed,(int?)exit,Mac,"1-1","1-2","",true});
    }
    static void Reject(HardwareStatusAdapter adapter,XDocument doc,int exit,string why)
    {
        try{Validate(adapter,doc,exit);throw new Exception("Accepted forged receipt: "+why);}
        catch(TargetInvocationException error){Assert(error.InnerException is InvalidDataException,why);}
    }
    static int Main()
    {
        try{
            foreach(int status in new[]{-4850003,-4850005}){
                var doc=Inspect(()=>Enumerate(status,0));
                Assert((string)doc.Root.Attribute("State")=="Busy","configure private busy preserved");
                Assert(configured==1&&enumerated==0,"busy configuration does not retry or enumerate");
                Assert(!doc.Root.Elements("Module").Any(),"busy never fabricates verified modules");
            }
            foreach(uint status in new[]{0xE4850003u,0xE4850005u}){
                var doc=Inspect(()=>Enumerate(0,status));
                Assert((string)doc.Root.Attribute("State")=="Busy","Pico family owner conflict preserved");
                Assert(configured==1&&enumerated==1,"busy enum does not retry another path");
                Assert(doc.Root.Element("InspectionDiagnostic")!=null,"raw private code retained internally");
            }
            foreach(uint status in new[]{3u,5u,0xE485000Bu,0xE485000Cu,0xFFFFFFFFu}){
                var doc=Inspect(()=>Enumerate(0,status));
                Assert((string)doc.Root.Attribute("State")=="Error","nonbusy status cannot become Busy or missing");
            }
            Assert((string)Inspect(()=>{throw new IOException("private bridge status=-4850003");}).Root.Attribute("State")=="Error",
                "busy-looking exception text cannot forge typed busy");
            Assert((string)Inspect(()=>Enumerate(0,0,0)).Root.Attribute("State")=="NotReady","successful zero units remains missing evidence");
            Assert((string)Inspect(()=>Enumerate(0,0)).Root.Attribute("State")=="Ready","successful exact pair unchanged");
            Assert((string)Inspect(()=>Enumerate(-4850006,0)).Root.Attribute("State")=="Error"&&enumerated==0,"invalid configure not false absence");
            int attempts=0;
            var fallback=ModuleIdentity.EnumeratePicoSerials(new[]{"fake-a","fake-b"},p=>true,p=>++attempts==1?-4850006:0,
                delegate(out short count,StringBuilder text,ref short length){count=1;text.Append(Pico);return 0;});
            Assert(attempts==2&&fallback.Single()==Pico,"known candidate fallback preserved");
            var settings=new AppSettings();settings.Hardware.Enabled=true;settings.Hardware.SelectedHubMac=Mac;
            settings.Hardware.ControllerLocation="1-1";settings.Hardware.AcquisitionLocation="1-2";
            settings.Hardware.ControllerSerial=Art;settings.Hardware.AcquisitionSerial=Pico;
            using(var adapter=new HardwareStatusAdapter(settings)){
                var doc=Inspect(()=>Enumerate(0,0xE4850003u));
                var snapshot=Validate(adapter,doc,10);
                Assert(snapshot.StatusCode=="Busy"&&!snapshot.Ready&&!snapshot.Connected,"host accepts Busy only as unavailable");
                Assert(snapshot.Message=="设备正在使用，暂时无法检查。","customer message is brief and not disconnected");
                HardwareStatusAdapter.MarkLiveReadback(snapshot,10,true,"fixture",Mac,"1-1","1-2");
                Assert(!snapshot.ConnectionReadbackVerified&&!snapshot.FromLiveIntegrationProcess,"Busy grants no live connection or electrical permit");
                Reject(adapter,doc,0,"Busy with success exit rejected");Reject(adapter,doc,40,"Busy with fatal exit rejected");
                var wrong=new XDocument(doc);wrong.Root.SetAttributeValue("Mac","FFFFFFFFFFFF");Reject(adapter,wrong,10,"wrong pair rejected");
                var stale=new XDocument(doc);stale.Root.SetAttributeValue("Utc",DateTime.UtcNow.AddMinutes(-2).ToString("O"));Reject(adapter,stale,10,"stale Busy rejected");
                var ready=new XDocument(doc);ready.Root.SetAttributeValue("State","Ready");Reject(adapter,ready,0,"Busy cannot forge Ready without modules");
            }
            Console.WriteLine("R58_CONNECTOR_BUSY_PASS="+checks+"; all native calls injected; SDK/HARDWARE/PRODUCT_MAIN=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
