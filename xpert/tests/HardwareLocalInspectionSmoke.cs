using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xiongxin.HvDacXpert;

internal static class HardwareLocalInspectionSmoke
{
    static int checks,calls;
    const string Art="1234567890",Pico="AB123/1234",ArtId="USB\\VID_3063&PID_3135\\1234567890",PicoId="USB\\VID_0CE9&PID_1218\\fixture";
    static List<string> arts,picos,units;static List<ArtRecord> records;
    static void Reset(){calls=0;arts=new List<string>{ArtId};picos=new List<string>{PicoId};units=new List<string>{Pico};records=new List<ArtRecord>{new ArtRecord{Class=1,Bus=1,Type="USB3135",Name="Dev1",Serial=1234567890,Instance=ArtId,Status=8}};}
    static XDocument Check(bool enabled=true,string art=Art,string pico=Pico){return Integration.CheckLocal(enabled,"001122334455","1-1","1-2",art,pico,
        (v,p)=>{calls++;return v==0x3063?arts:picos;},()=>{calls++;return units;},()=>{calls++;return records;});}
    static void Assert(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void State(string expected){var d=Check();Assert((string)d.Root.Attribute("State")==expected,expected);Assert((string)d.Root.Attribute("InspectionMode")=="LocalBoundPair","explicit local evidence");}
    static int Main(){try{
        Reset();var d=Check();Assert((string)d.Root.Attribute("State")=="Ready","exact saved pair accepted");
        Assert(d.Root.Elements("Module").Count()==2&&d.Root.Elements("Module").First().Attribute("DeviceName").Value=="Dev1","ART actual alias preserved");
        Assert(d.Root.Attribute("Ip")==null,"no fabricated live hub address");
        Reset();Assert((string)Check(false).Root.Attribute("State")=="Disabled"&&calls==0,"disabled performs no enumeration");
        Reset();Assert((string)Check(art:"").Root.Attribute("State")=="NeedsConfiguration"&&calls==0,"missing saved binding performs no enumeration");
        Reset();arts.Clear();State("NotReady");
        Reset();arts[0]="USB\\VID_3063&PID_3135\\9876543210";State("NotReady");
        Reset();records[0]=new ArtRecord{Class=1,Bus=1,Type="USB3135",Name="Dev1",Serial=1234567890,Instance=ArtId,Status=0};State("NotReady");
        Reset();records.Add(records[0]);State("NotReady");
        Reset();picos.Clear();State("NotReady");
        Reset();picos.Add(PicoId+"2");State("NotReady");
        Reset();picos[0]="USB\\VID_0000&PID_0000\\wrong";State("NotReady");
        Reset();units[0]="XY123/9999";State("NotReady");
        Reset();units.Add("bad identity");State("NotReady");
        Reset();units.Add(Pico);State("NotReady");
        Reset();var failure=Integration.CheckLocal(true,"001122334455","1-1","1-2",Art,Pico,(v,p)=>{throw new Exception("fixture driver unavailable");},()=>units,()=>records);
        Assert((string)failure.Root.Attribute("State")=="Error","enumeration error cannot become Ready");
        Console.WriteLine("LOCAL_INSPECTION_PASS="+checks+"; all device enumerators injected; SDK/HARDWARE_CALLS=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
