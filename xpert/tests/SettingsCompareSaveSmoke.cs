using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.OWTS.Desktop;

internal static class SettingsCompareSaveSmoke
{
    private sealed class ClockHolder { internal long Ticks; }
    private static int count;
    private static void Check(bool condition,string label)
    { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    private static string Digest(string text)
    {
        using (var sha=SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-",String.Empty);
    }
    private static ProvisioningActivation VerifiedActivation(DateTime now)
    {
        string xml="<HardwareProvisioning Schema=\"1\" State=\"ReadyAtExport\" Mac=\"1CBCEC5A9C69\" IpAtExport=\"192.168.2.5\" PersistentMode=\"auto\" VerifiedUtc=\"" +
            now.AddMinutes(-1).ToString("O") + "\"><Module Kind=\"Controller\" Vid=\"3063\" Pid=\"3135\" Location=\"H11\" InstanceId=\"USB\\VID_3063&amp;PID_3135\\H\"/>" +
            "<Module Kind=\"Acquisition\" Vid=\"0CE9\" Pid=\"1218\" Location=\"S51\" InstanceId=\"USB\\VID_0CE9&amp;PID_1218\\S\"/></HardwareProvisioning>";
        ProvisioningCandidate candidate=ProvisioningImportAdapter.Parse(xml,now);
        return ProvisioningImportAdapter.Inspect(candidate,c=>new ExactInspection {
            FromExactConnectorInspect=true,Ready=true,StatusCode="Ready",
            HubMac=c.Mac,RequestedMac=c.Mac,
            RequestedControllerLocation=c.ControllerLocation,
            RequestedAcquisitionLocation=c.AcquisitionLocation,
            IntegrationRequestMode="--inspect",
            IntegrationExecutablePathUsed=@"C:\Product\modules\HardwareIntegration\HardwareIntegration.exe",
            ExpectedIntegrationExecutablePath=@"C:\Product\modules\HardwareIntegration\HardwareIntegration.exe",
            ControllerInstanceId=c.ControllerInstanceId,AcquisitionInstanceId=c.AcquisitionInstanceId,
            ControllerTransportSerial="H",AcquisitionTransportSerial="S",
            ControllerSerial="CTRL",AcquisitionSerial="ACQ",CheckedUtc=now
        },now);
    }
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--hold")
        {
            using (var gate=new FileStream(args[1],FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            { File.WriteAllText(args[2],"ready"); Thread.Sleep(1200); }
            return 0;
        }
        string root=Path.Combine(Path.GetTempPath(),"HVX-settings-cas-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"settings.xml");
            var initial=new AppSettings(); initial.Save(path);
            string old=AppSettings.FileDigest(path);
            var other=initial.Clone(); other.CopyrightText="second writer"; other.Save(path);
            string changed=AppSettings.FileDigest(path);
            var stale=initial.Clone(); stale.CopyrightText="stale writer";
            Check(!stale.TrySaveIfUnchanged(path,old,false),"stale digest rejected under cross-process gate");
            Check(AppSettings.FileDigest(path)==changed,"rejected stale save leaves exact bytes unchanged");
            var valid=other.Clone(); valid.CopyrightText="current writer";
            Check(valid.TrySaveIfUnchanged(path,changed,false),"matching digest commits once");

            string absent=Path.Combine(root,"new.xml");
            Check(initial.TrySaveIfUnchanged(absent,null,true),"absent-path compare-and-save creates configuration");
            byte[] first=File.ReadAllBytes(absent);
            Check(!valid.TrySaveIfUnchanged(absent,null,true),"expected-absent CAS rejects existing file");
            Check(first.SequenceEqual(File.ReadAllBytes(absent)),"failed expected-absent CAS preserves existing bytes");

            string raceDigest=AppSettings.FileDigest(path);
            var a=valid.Clone(); a.CopyrightText="race-A";
            var b=valid.Clone(); b.CopyrightText="race-B";
            Task<bool> one=Task.Run(()=>a.TrySaveIfUnchanged(path,raceDigest,false));
            Task<bool> two=Task.Run(()=>b.TrySaveIfUnchanged(path,raceDigest,false));
            Task.WaitAll(one,two);
            Check(one.Result!=two.Result,"concurrent contenders have exactly one CAS winner");

            string lockPath=path+".write.lock", ready=Path.Combine(root,"child-ready.txt");
            using (Process child=Process.Start(new ProcessStartInfo {
                FileName=System.Reflection.Assembly.GetExecutingAssembly().Location,
                Arguments="--hold \""+lockPath+"\" \""+ready+"\"",
                UseShellExecute=false,CreateNoWindow=true
            }))
            {
                DateTime limit=DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(ready) && DateTime.UtcNow<limit) Thread.Sleep(20);
                Check(File.Exists(ready),"other process acquired settings gate");
                string digest=AppSettings.FileDigest(path);
                var waited=valid.Clone(); waited.CopyrightText="after-child";
                Stopwatch clock=Stopwatch.StartNew();
                Check(waited.TrySaveIfUnchanged(path,digest,false),"save waits for other process gate and then succeeds");
                clock.Stop();
                Check(clock.ElapsedMilliseconds>=500,"cross-process gate really delayed save");
                child.WaitForExit();
                Check(child.ExitCode==0,"other process exited cleanly");
            }

            // The receipt is fresh at UI preflight, then expires while the
            // cross-process save lock is held. The guarded commit must reject.
            DateTime receiptNow=DateTime.UtcNow;
            ProvisioningActivation activation=VerifiedActivation(receiptNow);
            Check(ProvisioningImportAdapter.IsCurrentActivation(activation,receiptNow),
                "binding receipt fresh before contested save");
            var simulated=new ClockHolder { Ticks=receiptNow.Ticks };
            string beforeExpired=AppSettings.FileDigest(path);
            string delayedReady=Path.Combine(root,"delayed-ready.txt");
            using (Process child=Process.Start(new ProcessStartInfo {
                FileName=System.Reflection.Assembly.GetExecutingAssembly().Location,
                Arguments="--hold \""+lockPath+"\" \""+delayedReady+"\"",
                UseShellExecute=false,CreateNoWindow=true
            }))
            {
                DateTime limit=DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(delayedReady) && DateTime.UtcNow<limit) Thread.Sleep(20);
                Check(File.Exists(delayedReady),"other process holds lock before TTL commit");
                var expiredCandidate=valid.Clone(); expiredCandidate.CopyrightText="must not commit";
                Task<bool> attempted=Task.Run(()=>expiredCandidate.TrySaveIfUnchanged(path,beforeExpired,false,
                    ()=>ProvisioningImportAdapter.IsCurrentActivation(activation,
                        new DateTime(Interlocked.Read(ref simulated.Ticks),DateTimeKind.Utc))));
                Thread.Sleep(100);
                Interlocked.Exchange(ref simulated.Ticks,receiptNow.AddSeconds(31).Ticks);
                child.WaitForExit();
                bool expiredRejected=false;
                try { attempted.GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { expiredRejected=true; }
                Check(expiredRejected && AppSettings.FileDigest(path)==beforeExpired,
                    "binding TTL expiration during lock wait blocks atomic commit");
            }

            byte[] beforeReplace=File.ReadAllBytes(path);
            int guardCalls=0;
            var late=valid.Clone(); late.CopyrightText="also must not commit";
            bool preReplaceRejected=false;
            try { late.TrySaveIfUnchanged(path,AppSettings.FileDigest(path),false,
                ()=>++guardCalls==1); }
            catch (InvalidOperationException) { preReplaceRejected=true; }
            Check(preReplaceRejected && guardCalls==2 && beforeReplace.SequenceEqual(File.ReadAllBytes(path)),
                "receipt expiring after temporary XML write blocks Replace");

            var invalid=valid.Clone(); invalid.TaskMinus3.EndpointPolicy="invalid";
            byte[] before=File.ReadAllBytes(path);
            bool rejected=false;
            try { invalid.Save(path); } catch (InvalidDataException) { rejected=true; }
            Check(rejected && before.SequenceEqual(File.ReadAllBytes(path)),
                "validation failure leaves committed configuration unchanged");

            string oldPath=Path.Combine(root,"old-hardware.xml");
            var oldHardware=new AppSettings();
            oldHardware.Hardware.Enabled=true;
            oldHardware.Hardware.SelectedHubMac="1CBCEC5A9C69";
            oldHardware.Hardware.ControllerLocation="H11";
            oldHardware.Hardware.AcquisitionLocation="S51";
            oldHardware.Save(oldPath);
            AppSettings session; string diagnostic;
            Check(AppSettings.TryLoad(oldPath,out session,out diagnostic),"old enabled profile loads");
            session.SessionHardwareForcedOffline=true;
            session.Hardware.Enabled=false;
            session.CopyrightText="offline change";
            session.Save(oldPath);
            AppSettings kept;
            Check(AppSettings.TryLoad(oldPath,out kept,out diagnostic) && kept.Hardware.Enabled &&
                kept.Hardware.SelectedHubMac=="1CBCEC5A9C69" &&
                kept.CopyrightText=="offline change" && !session.Hardware.Enabled,
                "forced offline session saves other settings without erasing disk hardware identity");
            byte[] beforeHardwareEdit=File.ReadAllBytes(oldPath);
            session.Hardware.ControllerLocation="H12";
            rejected=false;
            try { session.Save(oldPath); } catch (InvalidOperationException) { rejected=true; }
            Check(rejected && beforeHardwareEdit.SequenceEqual(File.ReadAllBytes(oldPath)),
                "forced offline session rejects hidden hardware edit without changing disk bytes");
            session.Hardware.ControllerLocation="H11";
            session.Hardware.Enabled=true;
            rejected=false;
            try { session.Save(oldPath); } catch (InvalidOperationException) { rejected=true; }
            Check(rejected && beforeHardwareEdit.SequenceEqual(File.ReadAllBytes(oldPath)),
                "forced offline session cannot re-enable hardware through Save");

            string itemHash=new string('A',64);
            string row="main.exe|"+itemHash+"|always";
            string package=Digest(row);
            string manifest="<ParentManifest Schema=\"1\" PackageId=\""+package+"\"><File Path=\"main.exe\" Sha256=\""+itemHash+"\" Scope=\"always\"/></ParentManifest>";
            Func<string,string> state=(id)=>"<ParentInstallState Schema=\"1\" PackageId=\""+id+"\" Hardware=\"true\" Stage=\"Complete\"/>";
            string stage; bool hardware;
            Check(FirstHardwareProvisioning.ValidateParentStateDocuments(manifest,state(package),out stage,out hardware)=="" &&
                stage=="Complete" && hardware,"matching complete hardware package accepted");
            Check(FirstHardwareProvisioning.ValidateParentStateDocuments(manifest,state(new string('B',64)),out stage,out hardware).Length>0 &&
                stage=="","stale other-package Complete state rejected");
            Check(FirstHardwareProvisioning.ValidateParentStateDocuments(
                manifest.Replace(package,new string('C',64)),state(package),out stage,out hardware).Length>0,
                "tampered manifest fingerprint rejected");
            string offlineState="<ParentInstallState Schema=\"1\" PackageId=\""+package+"\" Hardware=\"false\" Stage=\"Complete\"/>";
            Check(FirstHardwareProvisioning.ValidateParentStateDocuments(manifest,offlineState,out stage,out hardware).Length>0 &&
                stage=="Complete" && !hardware,"matching completed offline package still disables hardware");

            int fullWaits=0, notices=0;
            Check(!FirstHardwareProvisioning.WaitForBindingExit(ms=>false,()=>fullWaits++,()=>notices++) &&
                fullWaits==1 && notices==1,
                "overtime child notifies UI and remains owned until it exits");
            Check(FirstHardwareProvisioning.WaitForBindingExit(ms=>true,()=>fullWaits++,()=>notices++) &&
                fullWaits==1 && notices==1,
                "normally exited child does not enter extra wait or timeout notice");
            Console.WriteLine("PASS SETTINGS_CAS_AND_INSTALL_GATE="+count);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(root,true); } catch {} }
    }
}
