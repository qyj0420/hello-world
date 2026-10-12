using System;
using System.IO;
using System.Linq;
using System.Reflection;

// The child process is this fixture executable, not the connector or SDK.
internal static class HardwareReadOnlyConnectSmoke
{
    static void Set(object value,string name,object data){value.GetType().GetProperty(name).SetValue(value,data,null);}
    static int Main(string[] args)
    {
        if(args.Any(x=>x=="--inspect"||x=="--connect")){
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-inspection-arguments.txt"),args);
            string output=args.Single(x=>x.StartsWith("--output=")).Substring(9);
            string modePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-response-mode.txt");
            string mode=File.Exists(modePath)?File.ReadAllText(modePath):"not-ready";
            bool ready=mode!="not-ready";
            string modules=ready?"<Module Kind=\"Controller\" Location=\"1-1\" Connected=\"true\" Ready=\"true\" Serial=\""+(mode=="wrong-art"?"9876543210":"1234567890")+"\" TransportSerial=\"1234567890\" InstanceId=\"USB\\VID_3063&amp;PID_3135\\1234567890\" DeviceName=\"Dev1\"/><Module Kind=\"Acquisition\" Location=\"1-2\" Connected=\"true\" Ready=\"true\" Serial=\"AB123/1234\" TransportSerial=\"fixture\" InstanceId=\"USB\\VID_0CE9&amp;PID_1218\\fixture\"/>":"";
            File.WriteAllText(output,"<HardwareStatus Schema=\"1\" "+(mode=="old-connector"?"":"InspectionMode=\"LocalBoundPair\" ")+"State=\""+(ready?"Ready":"NotReady")+"\" Mac=\"001122334455\" Utc=\""+DateTime.UtcNow.ToString("O")+"\" MaxAgeSeconds=\"30\"><Message>Offline fake only</Message>"+modules+"</HardwareStatus>");
            return ready?0:10;
        }
        try{
            if(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"modules","HardwareIntegration","HardwareIntegration.exe")))
                throw new InvalidOperationException("Offline fixture must run in an isolated directory with no staged hardware connector.");
            var host=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"Xiongxin.OWTS.Desktop.exe"));
            var settings=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.AppSettings",true),true);
            var hw=settings.GetType().GetProperty("Hardware").GetValue(settings,null);
            Set(hw,"Enabled",true);Set(hw,"SelectedHubMac","001122334455");Set(hw,"ControllerLocation","1-1");Set(hw,"AcquisitionLocation","1-2");
            Set(hw,"ControllerSerial","1234567890");Set(hw,"AcquisitionSerial","AB123/1234");
            string fixture=Assembly.GetExecutingAssembly().Location;
            Set(hw,"IntegrationExecutablePath",fixture);Set(hw,"DriverMarkerPath",fixture);
            var type=host.GetType("Xiongxin.OWTS.Desktop.HardwareStatusAdapter",true);int checks=0;
            using(var adapter=(IDisposable)Activator.CreateInstance(type,new[]{settings})){
                string resolved=(string)type.GetMethod("ResolveIntegrationExecutable",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(adapter,null);
                if(!String.Equals(Path.GetFullPath(resolved),fixture,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refusing non-fixture connector before process start.");
                foreach(bool explicitConnect in new[]{false,true}){
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-response-mode.txt"),"not-ready");
                    object result=type.GetMethod("Refresh",new[]{typeof(bool)}).Invoke(adapter,new object[]{explicitConnect});
                    string[] sent=File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-inspection-arguments.txt"));
                    if(!sent.Contains("--inspect")||sent.Contains("--connect")||sent.Contains("--disconnect"))throw new Exception("Ordinary connection attempted binding or disconnect.");checks++;
                    if(!sent.Contains("--local-only")||!sent.Contains("--art-serial=1234567890")||!sent.Contains("--pico-serial=AB123/1234"))throw new Exception("Ordinary connection did not request the saved local pair.");checks++;
                    if((bool)result.GetType().GetProperty("Ready").GetValue(result,null))throw new Exception("NotReady fixture was accepted as ready.");checks++;
                }
                foreach(string mode in new[]{"ready","old-connector","wrong-art"}){
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-response-mode.txt"),mode);
                    object result=type.GetMethod("Refresh",new[]{typeof(bool)}).Invoke(adapter,new object[]{true});
                    if((bool)result.GetType().GetProperty("Ready").GetValue(result,null)!=(mode=="ready"))throw new Exception("Incorrect readiness for "+mode);checks++;
                }
                Set(hw,"ControllerSerial","");
                string argumentsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-inspection-arguments.txt");
                File.Delete(argumentsPath);
                object missing=type.GetMethod("Refresh",new[]{typeof(bool)}).Invoke(adapter,new object[]{true});
                if(File.Exists(argumentsPath)||(bool)missing.GetType().GetProperty("Ready").GetValue(missing,null))throw new Exception("Missing saved pair launched an inspection.");checks++;
            }
            Console.WriteLine("PASS "+checks+" ordinary-connect checks; fixture child process only; SDK/HARDWARE_CALLS=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
