using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using Xiongxin.OWTS.Desktop;

internal static class SettingsParserBoundarySmoke
{
    static int checks; static string qa;
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static string Input(string name,string content){string path=Path.Combine(qa,name+".xml");File.WriteAllText(path,content,new UTF8Encoding(false));return path;}
    static AppSettings Load(string path,bool expected)
    {
        byte[] before=File.ReadAllBytes(path);AppSettings loaded;string message;
        Check(AppSettings.TryLoad(path,out loaded,out message)==expected,"expected parser decision: "+Path.GetFileName(path));
        Check(before.SequenceEqual(File.ReadAllBytes(path)),"loading preserves original configuration");
        Check(!String.IsNullOrWhiteSpace(message),"parser supplies a diagnostic");
        if(!expected)Check(!loaded.Hardware.Enabled,"failure remains offline");return loaded;
    }
    static string Wrap(string content){return "<HVXUnifiedDesktopSettings>"+content+"</HVXUnifiedDesktopSettings>";}
    static void Reject(string name,string body){Load(Input(name,Wrap(body)),false);}
    static string Serialize(AppSettings settings)
    {
        using(var output=new StringWriter()){
            using(var writer=XmlWriter.Create(output)){writer.WriteStartElement("HVXUnifiedDesktopSettings");typeof(AppSettings).GetMethod("WriteObject",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{writer,settings});writer.WriteEndElement();}
            var doc=new XmlDocument();doc.LoadXml(output.ToString());return doc.DocumentElement.OuterXml;
        }
    }
    static int Main(string[] args)
    {
        try{
            qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            typeof(AppPaths).GetField("LocalDataDirectory",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,Path.Combine(qa,"profile"));
            var empty=Load(Input("old-missing",Wrap("")),true);Check(empty.MaximumVoltageKv==30&&!empty.AnalyzeZeroVoltageData&&empty.IconPath==String.Empty,"old missing optional fields keep defaults");
            foreach(string field in new[]{"MaximumVoltageKv","DividerRatio","DeviceTriggerVolts","AnalyzeZeroVoltageData","PdSampleBits","HvupVoltageSource"}){
                Reject("invalid-"+field,"<"+field+">invalid</"+field+">");
                Reject("empty-"+field,"<"+field+"/>");
            }
            Reject("nested-bool","<Hardware><Enabled>invalid</Enabled></Hardware>");
            Reject("section-scalar","<Hardware>invalid</Hardware>");
            Reject("nested-number","<TaskMinus3><MaximumPulseCount>invalid</MaximumPulseCount></TaskMinus3>");
            Reject("duplicate-number","<MaximumVoltageKv>15</MaximumVoltageKv><MaximumVoltageKv>30</MaximumVoltageKv>");
            Reject("duplicate-bool","<AnalyzeZeroVoltageData>true</AnalyzeZeroVoltageData><AnalyzeZeroVoltageData>false</AnalyzeZeroVoltageData>");
            Reject("duplicate-section","<Hardware/><Hardware/>");
            Reject("duplicate-nested","<TaskMinus3><MaximumPulseCount>30</MaximumPulseCount><MaximumPulseCount>31</MaximumPulseCount></TaskMinus3>");
            Reject("scalar-markup","<MaximumVoltageKv><value>15</value></MaximumVoltageKv>");
            Reject("legacy-bad-number","<TaskMinus3><AttenuationDb>invalid</AttenuationDb></TaskMinus3>");
            Reject("legacy-bad-bool","<TaskMinus3><UseFirstArrival>invalid</UseFirstArrival></TaskMinus3>");
            Reject("legacy-nonfinite","<TaskMinus3><ReflectionSuppression>NaN</ReflectionSuppression></TaskMinus3>");
            Reject("legacy-fractional","<TaskMinus3><ReflectionSuppression>1.5</ReflectionSuppression></TaskMinus3>");
            Reject("legacy-duplicate","<TaskMinus3><UseFirstArrival>true</UseFirstArrival><UseFirstArrival>false</UseFirstArrival></TaskMinus3>");
            var legacy=Load(Input("legacy-valid",Wrap("<TaskMinus3><AttenuationDb>12.5</AttenuationDb><UseFirstArrival>false</UseFirstArrival></TaskMinus3>")),true);
            Check(legacy.TaskMinus3.AttenuationTolerancePercent==12.5&&!legacy.TaskMinus3.UseFirstArrivalTiming,"valid legacy migration retained");
            Check(empty.OscillatingPdCaptureDurationMs==60&&String.IsNullOrEmpty(empty.SessionConfigurationNotice),"new or absent duration uses 60 ms without a false migration notice");
            foreach(string duration in new[]{"60.0001","100","86400000"}){
                var migrated=Load(Input("duration-"+duration.Replace('.','_'),Wrap("<OscillatingPdCaptureDurationMs>"+duration+"</OscillatingPdCaptureDurationMs><DividerRatio>11000</DividerRatio><MaximumVoltageKv>15</MaximumVoltageKv>")),true);
                Check(migrated.OscillatingPdCaptureDurationMs==60&&migrated.DividerRatio==11000&&migrated.MaximumVoltageKv==15,"duration migration retains unrelated valid values");
                Check(migrated.SessionConfigurationNotice.Contains(duration)&&migrated.SessionConfigurationNotice.Contains("60 ms")&&migrated.SessionConfigurationNotice.Contains("原配置文件未改动"),"migration notice states original and new value plus preserved source");
                Check(migrated.Clone().SessionConfigurationNotice==migrated.SessionConfigurationNotice&&!Serialize(migrated).Contains("SessionConfigurationNotice"),"clone keeps session notice but serialization excludes it");
            }
            foreach(string duration in new[]{"invalid","NaN","Infinity","-1","0","86400001"})Reject("duration-reject-"+duration,"<OscillatingPdCaptureDurationMs>"+duration+"</OscillatingPdCaptureDurationMs>");
            string dtd="<!DOCTYPE HVXUnifiedDesktopSettings [<!ENTITY b 'SMALL'>]>"+Wrap("<SoftwareModel>&b;</SoftwareModel>");
            Load(Input("internal-dtd",dtd),false);
            string external=Path.Combine(qa,"nonexistent-never-opened.txt");
            Load(Input("external-dtd","<!DOCTYPE HVXUnifiedDesktopSettings SYSTEM '"+new Uri(external).AbsoluteUri+"'>"+Wrap("")),false);
            Check(!File.Exists(external),"external DTD fixture is not created or read from real paths");
            Reject("title-long","<SoftwareModel>"+new string('x',513)+"</SoftwareModel>");
            Reject("copyright-long","<CopyrightText>"+new string('x',2049)+"</CopyrightText>");
            Reject("generic-string-long","<ReportTemplatePath>"+new string('x',32769)+"</ReportTemplatePath>");
            Reject("profiles-long","<DeviceProfilesXml>"+new string('x',1024*1024+1)+"</DeviceProfilesXml>");
            Load(Input("document-budget",Wrap("<FutureIgnored>"+new string('x',AppSettings.MaximumSettingsBytes)+"</FutureIgnored>")),false);
            var future=Load(Input("future-small",Wrap("<FutureIgnored>kept compatible</FutureIgnored>")),true);Check(future!=null,"small unknown future setting stays compatible");
            string good=Path.Combine(qa,"roundtrip.xml");var settings=new AppSettings{DataRoot=qa,MaximumVoltageKv=15,AnalyzeZeroVoltageData=true,SoftwareModel=new string('A',512),CopyrightText=new string('C',2048),IconPath=Path.Combine(qa,"not-installed-yet.ico")};settings.Save(good);
            var roundtrip=Load(good,true);Check(roundtrip.IconPath==settings.IconPath&&roundtrip.SoftwareModel==settings.SoftwareModel&&roundtrip.AnalyzeZeroVoltageData,"valid current settings including missing local icon round-trip");
            byte[] saved=File.ReadAllBytes(good);settings.SoftwareModel=new string('B',513);bool failed=false;try{settings.Save(good);}catch(InvalidDataException){failed=true;}
            Check(failed&&saved.SequenceEqual(File.ReadAllBytes(good)),"invalid save preserves previous bytes");
            settings=roundtrip.Clone();settings.OscillatingPdCaptureDurationMs=60.0001;failed=false;try{settings.Save(good);}catch(InvalidDataException){failed=true;}
            Check(failed&&saved.SequenceEqual(File.ReadAllBytes(good)),"new over-limit duration is rejected on explicit save, not migrated as if historic");
            settings=roundtrip.Clone();settings.SoftwareModel=new string('&',512);settings.Save(good);
            Check(Load(good,true).SoftwareModel==settings.SoftwareModel,"bounded XML-escaped strings save and reload unchanged");
            var actual=new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",FromLiveIntegrationProcess=true,ControllerSerial="0000000001",AcquisitionSerial="QA000/0001",HubMac="001122334455",CheckedUtc=DateTime.UtcNow};
            var bound=new AppSettings{DataRoot=qa,MaximumVoltageKv=15,DividerRatio=11000,DeviceTriggerVolts=1.6,AnalyzeZeroVoltageData=true};bound.Hardware.Enabled=true;bound.Hardware.SelectedHubMac=actual.HubMac;bound.Hardware.ControllerSerial=actual.ControllerSerial;bound.Hardware.AcquisitionSerial=actual.AcquisitionSerial;bound.Hardware.ControllerLocation="TEST-ART";bound.Hardware.AcquisitionLocation="TEST-PICO";DeviceParameterProfiles.Confirm(bound,actual);
            Check(DeviceParameterProfiles.IsConfigured(bound,actual),"synthetic confirmed baseline is applicable");
            var durationDoc=new XmlDocument();durationDoc.LoadXml(Serialize(bound));durationDoc.DocumentElement.SelectSingleNode("OscillatingPdCaptureDurationMs").InnerText="100";
            // The preserved reviewed hash deliberately belongs to the former
            // duration, so migration must not silently sign a fresh confirmation.
            var historic=bound.Clone();historic.OscillatingPdCaptureDurationMs=100;string previousReview=DeviceSetupStatus.ParameterHash(historic);durationDoc.DocumentElement.SelectSingleNode("DeviceParametersReviewedHash").InnerText=previousReview;
            var boundMigration=Load(Input("bound-duration-migration",durationDoc.OuterXml),true);
            Check(boundMigration.Hardware.ControllerSerial==bound.Hardware.ControllerSerial&&boundMigration.Hardware.AcquisitionSerial==bound.Hardware.AcquisitionSerial&&boundMigration.DeviceProfilesXml==bound.DeviceProfilesXml&&boundMigration.MaximumVoltageKv==15,"valid historic duration migration preserves identity and recorded device values");
            Check(boundMigration.DeviceParametersReviewedHash==previousReview&&!DeviceParameterProfiles.IsConfigured(boundMigration,actual),"migration does not forge current parameter confirmation");
            foreach(string field in new[]{"MaximumVoltageKv","DividerRatio","DeviceTriggerVolts","AnalyzeZeroVoltageData"}){var doc=new XmlDocument();doc.LoadXml(Serialize(bound));doc.DocumentElement.SelectSingleNode(field).InnerText="invalid";var result=Load(Input("bound-invalid-"+field,doc.OuterXml),false);Check(!DeviceParameterProfiles.IsConfigured(result,actual),"damaged bound configuration is not applicable");}
            Console.WriteLine("SETTINGS_PARSER_BOUNDARY_PASS="+checks+";HARDWARE_ACCESSED=False;USER_SETTINGS_WRITTEN=False;MAX_SINGLE_FIXTURE_BYTES="+(AppSettings.MaximumSettingsBytes+128));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
