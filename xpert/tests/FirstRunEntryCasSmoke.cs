using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class FirstRunEntryCasSmoke
{
    private sealed class ClockHolder { internal long Ticks; }
    private static int assertions;
    private static void Check(bool result,string label)
    { if (!result) throw new Exception(label); assertions++; Console.WriteLine("PASS "+label); }
    private static void ClickSave(FirstRunForm form)
    {
        MethodInfo handler=typeof(FirstRunForm).GetMethod("SaveClicked",
            BindingFlags.Instance|BindingFlags.NonPublic);
        if (handler==null) throw new Exception("First-run save handler not found.");
        handler.Invoke(form,new object[] { null,EventArgs.Empty });
    }
    private static ProvisioningActivation VerifiedActivation(DateTime now)
    {
        string xml="<HardwareProvisioning Schema=\"1\" State=\"ReadyAtExport\" Mac=\"1CBCEC5A9C69\" IpAtExport=\"192.168.2.5\" PersistentMode=\"auto\" VerifiedUtc=\""+
            now.AddMinutes(-1).ToString("O")+"\"><Module Kind=\"Controller\" Vid=\"3063\" Pid=\"3135\" Location=\"H11\" InstanceId=\"USB\\VID_3063&amp;PID_3135\\H\"/>"+
            "<Module Kind=\"Acquisition\" Vid=\"0CE9\" Pid=\"1218\" Location=\"S51\" InstanceId=\"USB\\VID_0CE9&amp;PID_1218\\S\"/></HardwareProvisioning>";
        ProvisioningCandidate selected=ProvisioningImportAdapter.Parse(xml,now);
        return ProvisioningImportAdapter.Inspect(selected,c=>new ExactInspection {
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
    private static void SelectHardware(FirstRunForm form,ProvisioningActivation activation,Func<DateTime> clock)
    {
        FieldInfo checkbox=typeof(FirstRunForm).GetField("enableHardware",
            BindingFlags.Instance|BindingFlags.NonPublic);
        if (checkbox==null) throw new Exception("First-run hardware checkbox not found.");
        ((CheckBox)checkbox.GetValue(form)).Checked=true;
        form.FirstBindingForTests=(settings,notice)=>Task.FromResult(activation);
        form.ApplyBindingForTests=(settings,verified)=> {
            var prepared=settings.Clone();
            prepared.Hardware.Enabled=true;
            prepared.Hardware.SelectedHubMac=verified.Candidate.Mac;
            prepared.Hardware.ControllerLocation=verified.Candidate.ControllerLocation;
            prepared.Hardware.AcquisitionLocation=verified.Candidate.AcquisitionLocation;
            return prepared;
        };
        form.CommitClockForTests=clock;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length==3 && args[0]=="--hold")
        {
            using (var gate=new FileStream(args[1],FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            { File.WriteAllText(args[2],"ready"); Thread.Sleep(1200); }
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string folder=Path.Combine(Path.GetTempPath(),"HVX-first-run-cas-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string fresh=Path.Combine(folder,"fresh.xml");
            string failure=null;
            using (var form=new FirstRunForm(new AppSettings(),fresh))
            {
                form.SaveFailureNoticeForTests=message=>failure=message;
                ClickSave(form); // Actual SaveClicked handler; hardware stays unchecked.
                Check(failure==null && File.Exists(fresh) && form.DialogResult==DialogResult.OK,
                    "first-run UI handler atomically saves a previously absent profile");
            }

            string raced=Path.Combine(folder,"raced.xml");
            byte[] competing=null;
            failure=null;
            using (var form=new FirstRunForm(new AppSettings(),raced))
            {
                form.SaveFailureNoticeForTests=message=>failure=message;
                form.BeforeFirstSaveForTests=delegate {
                    var other=new AppSettings(); other.CopyrightText="other process";
                    other.Save(raced); competing=File.ReadAllBytes(raced);
                };
                ClickSave(form); // Conflict appears after File.Exists but before CAS.
                Check(failure!=null && failure.Contains("提交前配置已由其他程序创建"),
                    "real first-run handler reports the CAS conflict");
                Check(form.DialogResult!=DialogResult.OK && competing.SequenceEqual(File.ReadAllBytes(raced)),
                    "real first-run handler does not overwrite another process's configuration");
            }

            DateTime now=DateTime.UtcNow;
            ProvisioningActivation activation=VerifiedActivation(now);
            Check(ProvisioningImportAdapter.IsCurrentActivation(activation,now),
                "synthetic dual-PnP receipt is valid before first-run commit");
            string hardware=Path.Combine(folder,"hardware.xml");
            failure=null;
            using (var form=new FirstRunForm(new AppSettings(),hardware))
            {
                form.SaveFailureNoticeForTests=message=>failure=message;
                SelectHardware(form,activation,()=>now);
                ClickSave(form);
                AppSettings saved; string diagnostic;
                Check(failure==null && form.DialogResult==DialogResult.OK &&
                    AppSettings.TryLoad(hardware,out saved,out diagnostic) && saved.Hardware.Enabled,
                    "real first-run hardware handler accepts a still-current guarded receipt");
            }

            string expired=Path.Combine(folder,"expired.xml");
            string lockPath=expired+".write.lock";
            string ready=Path.Combine(folder,"lock-ready.txt");
            var simulated=new ClockHolder { Ticks=now.Ticks };
            Process child=null;
            Task expireClock=null;
            failure=null;
            try
            {
                using (var form=new FirstRunForm(new AppSettings(),expired))
                {
                    form.SaveFailureNoticeForTests=message=>failure=message;
                    SelectHardware(form,activation,()=>new DateTime(Interlocked.Read(ref simulated.Ticks),DateTimeKind.Utc));
                    form.BeforeFirstSaveForTests=delegate {
                        child=Process.Start(new ProcessStartInfo {
                            FileName=Assembly.GetExecutingAssembly().Location,
                            Arguments="--hold \""+lockPath+"\" \""+ready+"\"",
                            UseShellExecute=false,CreateNoWindow=true
                        });
                        DateTime limit=DateTime.UtcNow.AddSeconds(5);
                        while (!File.Exists(ready) && DateTime.UtcNow<limit) Thread.Sleep(20);
                        if (!File.Exists(ready)) throw new Exception("Child did not acquire settings gate.");
                        expireClock=Task.Run(()=> {
                            Thread.Sleep(150);
                            Interlocked.Exchange(ref simulated.Ticks,now.AddSeconds(31).Ticks);
                        });
                    };
                    Stopwatch elapsed=Stopwatch.StartNew();
                    ClickSave(form); // Actual hardware SaveClicked, contested by a separate process.
                    elapsed.Stop();
                    Check(elapsed.ElapsedMilliseconds>=500 && expireClock!=null && expireClock.IsCompleted,
                        "first-run hardware save waited on the cross-process lock while receipt expired");
                    Check(failure!=null && failure.Contains("凭据已过期") &&
                        form.DialogResult!=DialogResult.OK && !File.Exists(expired),
                        "real first-run hardware handler rejects expired activation without writing settings");
                }
            }
            finally
            {
                if (child!=null) { child.WaitForExit(); child.Dispose(); }
                if (expireClock!=null) expireClock.Wait();
            }
            Console.WriteLine("PASS FIRST_RUN_ENTRY_CAS="+assertions);
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(folder,true); } catch {} }
    }
}
