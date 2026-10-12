using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class Program
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 5 && args[0] == "--worker") return Worker(args);
                if (args.Length == 4 && args[0] == "--device-session") return FactoryResidentDeviceServer.Run(args);
                if (args.Length == 2 && args[0] == "--simulation-check") return WorkerChecks.Run(args[1]).GetAwaiter().GetResult();
                if(args.Length==3 && args[0]=="--r29-record-replay") {FactoryR29Tests.Replay(args[1],args[2]);return 0;}
                if (args.Length == 3 && args[0] == "--transient-range-replay")
                { FactoryTransientRangeTests.Replay(args[1],args[2]).GetAwaiter().GetResult(); return 0; }
                if (args.Length == 2 && args[0] == "--self-test")
                {
                    // Offline test entry has no Application.Run message loop.
                    // Do not let temporary controls install an unpumped UI context
                    // that deadlocks the new cancellable inter-round delay.
                    WindowsFormsSynchronizationContext.AutoInstall=false;
                    SynchronizationContext.SetSynchronizationContext(null);
                    using (var writer = new StreamWriter(args[1], false, new UTF8Encoding(false)))
                    {
                        writer.AutoFlush=true;
                        FactoryTests.Run(writer); writer.WriteLine(FactoryAnalysisTests.Run());
                        writer.WriteLine(FactoryWaveformTests.Run());
                        writer.WriteLine(FactoryPdSignalTests.Run());
                        writer.WriteLine(FactoryReferenceAnalysisTests.Run());
                        writer.WriteLine(FactoryCompatibility100Tests.Run());
                        writer.WriteLine(FactoryRevisionSafetyTests.Run());
                        writer.WriteLine(FactoryAcceptanceChecksTests.Run());
                        writer.WriteLine(FactoryPostProcessingTests.Run());
                        writer.WriteLine(FactoryPulseLabelTests.Run());
                        writer.WriteLine(FactoryP04DiagnosticTests.Run());
                        writer.WriteLine(FactoryZeroInputTests.Run());
                        writer.WriteLine(FactoryZeroTriggerTests.Run());
                        writer.WriteLine(FactoryStartupFeedbackTests.Run());
                        writer.WriteLine(FactoryBufferEmergencyTests.Run());
                        writer.WriteLine(FactoryIoTimingTests.Run());
                        writer.WriteLine(FactoryPicoTimingTests.Run());
                        writer.WriteLine(FactoryCaptureCooldownTests.Run());
                        writer.WriteLine(FactoryCooldownWorkflowTests.Run());
                        writer.WriteLine(FactoryPicoLifecycleTests.Run());
                        writer.WriteLine(FactoryNativeSessionTests.Run());
                        writer.WriteLine(FactoryResidentDeviceServerTests.Run());
                        writer.WriteLine(FactoryDeviceLeaseTests.Run());
                        writer.WriteLine(FactoryResidentClientTests.Run());
                        writer.WriteLine(FactorySessionFaultTests.Run());
                        writer.WriteLine(FactoryMainForm.TestSessionFaultDisplay());
                        writer.WriteLine(FactoryIdleEmergencyRetryTests.Run());
                        writer.WriteLine(FactoryFinalAi4ObservationTests.Run());
                        writer.WriteLine(FactoryFinalAi4WorkflowTests.Run());
                        writer.WriteLine(FactoryManualTests.Run());
                        writer.WriteLine(FactoryPolarityTests.Run());
                        writer.WriteLine(FactoryDividerTraceTests.Run());
                        writer.WriteLine(FactoryShutdownDiagnosticTests.Run());
                        writer.WriteLine(FactoryRampCompletionTests.Run());
                        writer.WriteLine(FactoryTargetHoldTests.Run());
                        writer.WriteLine(FactoryFaultResidualTests.Run());
                        writer.WriteLine(FactoryFeedbackWindowTests.Run());
                        writer.WriteLine(FactoryAoEnvelopeTests.Run());
                        writer.WriteLine(FactoryResidualStageTests.Run());
                        writer.WriteLine(FactoryIntegratedTests.Run());
                        writer.WriteLine(FactoryPostHvStartupTests.Run());
                        writer.WriteLine(FactoryBackgroundNoiseTests.Run());
                        writer.WriteLine(FactoryCapturePreviewTests.Run());
                        writer.WriteLine(FactoryCaptureEndTests.Run());
                        writer.WriteLine(FactoryPressureSequenceTests.Run());
                        writer.WriteLine(FactoryBatchAuthorizationTests.Run());
                        writer.WriteLine(FactoryBatchServerTests.Run());
                        writer.WriteLine(FactoryAutomaticBatchTests.Run());
                        writer.WriteLine(FactoryMainForm.TestAutomaticBatchRouting());
                        writer.WriteLine(FactoryTransientRangeTests.Run());
                        writer.WriteLine(FactorySequenceResumeTests.Run());
                        writer.WriteLine(FactoryFeedbackOverlayTests.Run());
                        writer.WriteLine(FactoryMainForm.TestParameterPersistence());
                        writer.WriteLine(FactoryR28ConnectionCaptureTests.Run());
                        writer.WriteLine(FactoryR29Tests.Run());
                        writer.WriteLine(FactoryR30Tests.Run());
                        writer.WriteLine(FactoryLocationCycleTests.Run());
                        writer.WriteLine(FactoryMainForm.TestInteractionChanges());
                        writer.WriteLine(FactoryMainForm.TestRepeatPrompts());
                        writer.WriteLine("HardwareAccessed=False; PhysicalAcceptance=False; OVERALL=PASS");
                    }
                    return 0;
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if(args.Length==2 && args[0]=="--location-cycle-smoke")
                {
                    Directory.CreateDirectory(args[1]);int exit=0;
                    using(var form=new FactoryMainForm(Path.Combine(args[1],"isolated-labels")))
                    {
                        form.Shown+=async(s,e)=>{try{await form.SmokeLocationCycles(args[1]);}
                            catch(Exception ex){File.WriteAllText(Path.Combine(args[1],"error.txt"),ex.ToString());exit=1;}
                            finally{form.Close();}};
                        Application.Run(form);
                    }
                    return exit;
                }
                if (args.Length == 3 && args[0] == "--sequence-replay-smoke")
                {
                    Directory.CreateDirectory(args[1]); int exit=0;
                    using(var form=new FactoryMainForm(Path.Combine(args[1],"isolated-labels")))
                    {
                        form.Shown+=async(s,e)=>{try{await form.SmokeSequenceReplay(args[1],args[2]);}
                            catch(Exception ex){File.WriteAllText(Path.Combine(args[1],"sequence-replay-error.txt"),ex.ToString());exit=1;}
                            finally{form.Close();}};
                        Application.Run(form);
                    }
                    return exit;
                }
                if (args.Length == 3 && args[0] == "--capture-preview-smoke")
                {
                    Directory.CreateDirectory(args[1]); int exit = 0;
                    using (var previewForm = new FactoryMainForm(Path.Combine(args[1], "isolated-labels")))
                    {
                        previewForm.Shown += async (s,e) => { try { await previewForm.SmokeCapturePreview(args[1],args[2]); }
                            catch (Exception ex) { File.WriteAllText(Path.Combine(args[1],"preview-error.txt"),ex.ToString()); exit = 1; }
                            finally { previewForm.Close(); } };
                        Application.Run(previewForm);
                    }
                    return exit;
                }
                if (Path.GetFileName(Application.ExecutablePath).Equals("Xiongxin.FactoryDebug.exe",StringComparison.OrdinalIgnoreCase) ||
                    (args.Length==2 && args[0]=="--debug-ui-smoke"))
                {
                    bool test=args.Length==2 && args[0]=="--debug-ui-smoke";
                    FactoryDebugStartForm.Workspace selected;
                    using(var entry=new FactoryDebugStartForm())
                    {
                        if(test)
                        {
                            int exit=0;
                            entry.Shown+=(s,e)=>{try{entry.Smoke(args[1]);}catch(Exception ex){Directory.CreateDirectory(args[1]);File.WriteAllText(Path.Combine(args[1],"debug-entry-error.txt"),ex.ToString());exit=1;}finally{entry.Close();}};
                            Application.Run(entry);return exit;
                        }
                        if(entry.ShowDialog()!=DialogResult.OK)return 0;
                        selected=entry.Selected;
                    }
                    Form workspace=selected==FactoryDebugStartForm.Workspace.Automatic ? (Form)new FactorySimpleForm() :
                        selected==FactoryDebugStartForm.Workspace.Manual ? (Form)new FactoryManualForm() :
                        selected==FactoryDebugStartForm.Workspace.Full ? (Form)new FactoryMainForm() : null;
                    if(workspace!=null)using(workspace){FactoryResidentDevices.AttachLifetime(workspace);Application.Run(workspace);}
                    return 0;
                }
                if (Path.GetFileName(Application.ExecutablePath).Equals("Xiongxin.ManualHvBench.exe",StringComparison.OrdinalIgnoreCase) || (args.Length == 2 && args[0] == "--manual-ui-smoke"))
                {
                    bool test = args.Length == 2 && args[0] == "--manual-ui-smoke";
                    using (var manual = new FactoryManualForm(test))
                    {
                        int exit = 0;
                        if (test) manual.Shown += async (s,e) => { try { await manual.Smoke(args[1]); } catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1],"manual-ui-error.txt"),ex.ToString()); exit = 1; } finally { manual.Close(); } };
                        Application.Run(manual); return exit;
                    }
                }
                if (Path.GetFileName(Application.ExecutablePath).Equals("Xiongxin.FiveKvBench.exe", StringComparison.OrdinalIgnoreCase) ||
                    (args.Length == 2 && args[0] == "--simple-ui-smoke"))
                {
                    bool test = args.Length == 2 && args[0] == "--simple-ui-smoke";
                    using (var simple = new FactorySimpleForm(test))
                    {
                        if(!test)FactoryResidentDevices.AttachLifetime(simple);
                        int exit = 0;
                        if (test) simple.Shown += async (s,e) => { try { await simple.Smoke(args[1]); } catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1],"simple-ui-error.txt"),ex.ToString()); exit = 1; } finally { simple.Close(); } };
                        Application.Run(simple); return exit;
                    }
                }
                using (var form = new FactoryMainForm(args.Length == 2 && args[0] == "--ui-smoke" ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "isolated-labels-" + Guid.NewGuid().ToString("N")) : null))
                {
                    if (args.Length == 2 && args[0] == "--ui-smoke")
                    {
                        int exit = 0;
                        form.Shown += async (s,e) => { try { await form.Smoke(args[1]); } catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); exit = 1; } finally { form.Close(); } };
                        Application.Run(form); return exit;
                    }
                    FactoryResidentDevices.AttachLifetime(form);Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length == 2 && args[0] == "--self-test") File.AppendAllText(args[1], "\r\nFAIL " + ex);
                else if (args.Length == 2 && args[0] == "--simulation-check") File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString());
                else if (args.Length == 0) MessageBox.Show(ex.ToString(), "出厂测试启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        internal static Task<FactoryResult> StartWorker(FactorySettings settings, FactoryRunKind kind, string directory,
            Action<string,string> report, CancellationToken cancellation)
        {
            return Task.Run(async () =>
            {
                try
                {
                    if(!settings.Simulation && !(kind==FactoryRunKind.Pressure && settings.TargetKv==0 && settings.ZeroSynchronizedOptical))
                        await FactoryResidentDevices.RunRound(settings,kind,directory,report,cancellation);
                    else await RunChild(settings, kind.ToString(), directory, report, cancellation, IsReadOnlyPressure(settings,kind));
                }
                catch(Exception error)
                {
                    FactoryResult failed=null;
                    if(error is FactoryWorkerExitedException && !cancellation.IsCancellationRequested && settings.UseOpticalTimeoutRetry && kind==FactoryRunKind.Pressure)
                    {
                        try { failed=LoadResult(Path.Combine(directory,"partial-result.xml")); }
                        catch(Exception) { /* Preserve original worker/IO failure, never infer a trigger timeout from text. */ }
                    }
                    if(failed!=null && FactoryCapturePreview.Matches(settings,kind,failed) &&
                        failed.Settings.SequenceId==settings.SequenceId && failed.Settings.SequenceRound==settings.SequenceRound &&
                        failed.Settings.SequenceAttempt==settings.SequenceAttempt && failed.Settings.Input.ExternalTriggerVolts==settings.Input.ExternalTriggerVolts &&
                        failed.TriggerRecovery!=null && failed.TriggerRecovery.InitialTimeout && failed.TriggerRecovery.EligibleForThresholdAdjustment)
                    { failed.DirectoryPath=directory;throw new FactoryTriggerFailureException(failed,error); }
                    throw;
                }
                var result = LoadResult(Path.Combine(directory, "result.xml"));
                result.Capture = BenchCapture.Load(directory);
                if (!result.Completed && !result.CaptureOnlyAvailable()) throw new InvalidDataException("本轮未完整完成，禁止按合格数据自动继续。");
                if (result.CaptureOnlyAvailable() && (!(settings.UseCaptureOnlyEnd || settings.SkipPressureResidualMeasurement) || kind != FactoryRunKind.Pressure ||
                    !FactoryCapturePreview.Matches(settings, kind, result)))
                    throw new InvalidDataException("采集结束模式与本轮请求不一致。");
                if(kind==FactoryRunKind.Pressure && settings.UseOpticalTimeoutRetry)
                    FactoryTriggerThresholdPolicy.Shared.Success(settings);
                return result;
            });
        }
        internal static Task<DiscoveryResult> Discover(FactorySettings settings, string directory,
            Action<string,string> report, CancellationToken cancellation)
        {
            return Task.Run(async () =>
            {
                await RunChild(settings, "Discover", directory, report, cancellation, true);
                return DiscoveryResult.Load(Path.Combine(directory, "discovery.xml"));
            });
        }
        internal static Task WatchConnection(FactorySettings settings,string directory,Action<string,string> report,CancellationToken token)
        {return Task.Run(()=>FactoryResidentDevices.Watch(settings,report,token));}
        internal static bool IsReadOnlyPressure(FactorySettings settings,FactoryRunKind kind)
        {return kind==FactoryRunKind.Pressure && settings.TargetKv==0 && !settings.ZeroSynchronizedOptical;}
        internal static Task<string> DiagnoseP04(FactorySettings settings, string directory,
            Action<string,string> report, CancellationToken cancellation)
        {
            FactoryP04Diagnostic.Validate(settings.Input);
            return Task.Run(async () =>
            {
                await RunChild(settings, "DiagnoseP04", directory, report, cancellation, true);
                return File.ReadAllText(Path.Combine(directory, "p04-diagnostic.txt"));
            });
        }
        internal static Task<string> DiagnoseFeedback(FactorySettings settings, string directory,
            Action<string,string> report, CancellationToken cancellation)
        {
            FactoryAiDiagnostic.Validate(settings);
            return Task.Run(async () =>
            {
                await RunChild(settings,"DiagnoseFeedback",directory,report,cancellation,true);
                return File.ReadAllText(Path.Combine(directory,"ai-diagnostic.txt"));
            });
        }
        private static async Task RunChild(FactorySettings settings, string command, string directory,
            Action<string,string> report, CancellationToken token, bool discovery)
        {
            if(!settings.Simulation)
                await FactoryResidentDevices.ExclusiveLegacy(()=>RunChildCore(settings,command,directory,report,token,discovery));
            else await RunChildCore(settings,command,directory,report,token,discovery);
        }
        private static async Task RunChildCore(FactorySettings settings, string command, string directory,
            Action<string,string> report, CancellationToken token, bool discovery)
        {
            Directory.CreateDirectory(directory);
            if (Directory.GetFileSystemEntries(directory).Length != 0)
                throw new InvalidOperationException("记录目录已被使用，请新建本轮目录，不能覆盖以前的采集。");
            string settingsPath = Path.Combine(directory, "factory-settings.xml"); settings.Save(settingsPath);
            string cancelPath = Path.Combine(directory, "cancel.flag");
            string progressPath = Path.Combine(directory, "progress.log");
            string heartbeat = Path.Combine(directory, "supervisor.heartbeat"); File.WriteAllText(heartbeat, "alive");
            var launchEvidence=UnifiedWorkerLaunchEvidence.Create(directory,settingsPath,Process.GetCurrentProcess().Id);
            using (var process = new Process())
            {
                process.StartInfo = CreateWorkerStartInfo(command,settingsPath,directory,Process.GetCurrentProcess().Id);
                // No redirected handles: SDK descendants cannot keep stdout/stderr pipes alive.
                if(token.IsCancellationRequested)throw new FactoryNativeNotStartedException("ParentCanceledBeforeLaunch");
                process.Start(); long delivered = 0; bool stopped = false; var elapsed = Stopwatch.StartNew(); Exception supervisionFailure = null;
                DateTime? cancelAt=null;
                while (!process.HasExited)
                {
                    try
                    {
                        if (supervisionFailure == null) { File.WriteAllText(heartbeat, "alive"); Drain(progressPath, report, ref delivered); }
                        if (token.IsCancellationRequested && !stopped)
                        { File.WriteAllText(cancelPath, "operator stop"); stopped = true; cancelAt=DateTime.UtcNow; if(command!="WatchConnection") report("STOP", discovery ? "已请求停止只读操作；未创建输出任务。" : "已请求安全关断；不强杀输出工作进程。请等待并观察实际设备状态。"); }
                        // This process contains DI/enumeration only. Never kill an output owner.
                        if(command=="WatchConnection" && cancelAt.HasValue && DateTime.UtcNow-cancelAt.Value>TimeSpan.FromSeconds(2))
                        {process.Kill();process.WaitForExit();break;}
                        if (command!="WatchConnection" && elapsed.ElapsedMilliseconds >= (discovery ? 30000 : (settings.RampTimeoutSeconds + settings.DischargeTimeoutSeconds + settings.Input.TriggerTimeoutMs / 1000.0 + 30) * 1000))
                        {
                            if (discovery) { process.Kill(); process.WaitForExit(); throw new TimeoutException("只读设备操作超时，未创建输出任务。"); }
                            if (!stopped) { File.WriteAllText(cancelPath, "deadline stop"); stopped = true; }
                            if (elapsed.ElapsedMilliseconds % 5000 < 100)
                                report("SAFETY_ERROR", "工作进程未及时完成安全关断，可能驱动阻塞；勿靠近设备，使用现场硬件急停及放电程序。不会强杀后声称输出已关闭。");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (supervisionFailure == null) supervisionFailure = ex;
                        stopped = true;
                        try { File.WriteAllText(cancelPath, "supervision failure stop"); } catch { }
                        // Stop refreshing the lease. The child independently cancels
                        // on expired lease even if the cancel-file write itself fails.
                        // Do not return/unlock the UI while the output child is alive.
                    }
                    await Task.Delay(80);
                }
                if (supervisionFailure != null) throw new InvalidOperationException("监督/诊断失败，已请求工作进程安全停止并等待其退出；输出及残压仍需现场确认。", supervisionFailure);
                Drain(progressPath, report, ref delivered);
                if(command=="WatchConnection" && token.IsCancellationRequested)return;
                string errorPath = Path.Combine(directory, "error.txt");
                if(token.IsCancellationRequested&&UnifiedWorkerLaunchEvidence.VerifyCanceledBeforeNative(launchEvidence,settingsPath,process.Id,process.HasExited,process.ExitCode))
                    throw new FactoryNativeNotStartedException("VerifiedWrapperCanceledBeforeNative");
                if (process.ExitCode != 0)
                    throw new FactoryWorkerExitedException(File.Exists(errorPath) ? File.ReadAllText(errorPath) :
                        "工作进程异常退出 " + process.ExitCode + (discovery ? "；只读操作未完成，没有创建输出任务。" : "；未证实输出已关断，请按现场安全程序处理。"));
                if (stopped) token.ThrowIfCancellationRequested();
            }
        }
        // This assembly may be loaded by the unified desktop. Application.ExecutablePath
        // would relaunch that UI (and its single-instance dialog), not the task39 worker.
        internal static ProcessStartInfo CreateWorkerStartInfo(string command,string settingsPath,string directory,int parent)
        {
            return new ProcessStartInfo(typeof(Program).Assembly.Location,
                "--worker " + command + " " + Quote(settingsPath) + " " + Quote(directory) + " " + parent)
                { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
        }
        internal static void AttachRoundConnectionReport(NativeFactoryBackend backend,Action<string,string> roundReport)
        {
            if(backend==null)return;
            var sessionReport=backend.ConnectionReport;
            // Keep the resident journal and typed fault tracker subscribed while
            // the round also receives progress. Neither reads the native input.
            backend.ConnectionReport=(key,detail)=>{
                try{if(sessionReport!=null)sessionReport(key,detail);}
                finally{roundReport(key,detail);}
            };
        }

        internal static int Worker(string[] args,NativeFactoryBackend resident=null,
            FactorySettings frozenSettings=null,Action<FactoryResult> savedResult=null,Action inputStopped=null,Action healthyRoundStopped=null)
        {return Worker(args,resident,frozenSettings,savedResult,inputStopped,healthyRoundStopped,null);}

        internal static int Worker(string[] args,NativeFactoryBackend resident,
            FactorySettings frozenSettings,Action<FactoryResult> savedResult,Action inputStopped,Action healthyRoundStopped,
            Action emergencyRoundStopped)
        {
            string directory = args[3]; Directory.CreateDirectory(directory);
            var settings = frozenSettings==null ? FactorySettings.Load(args[2]) : frozenSettings.Clone();
            using(var deviceLease=resident==null && !settings.Simulation ? AcquireDeviceLease(directory) : null)
            using (var cancellation = new CancellationTokenSource())
            {
                if(File.Exists(Path.Combine(directory,"cancel.flag")))cancellation.Cancel();
                int parent; if (!Int32.TryParse(args[4], out parent)) return 1;
                var linkEvents=new System.Collections.Concurrent.ConcurrentQueue<Tuple<string,string>>();
                NativeFactoryBackend monitoredBackend=null;Action<string,string> writeLink=null;
                var watch = Task.Run(async () =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(directory, "cancel.flag"))) cancellation.Cancel();
                            string lease = Path.Combine(directory, "supervisor.heartbeat");
                            if (!File.Exists(lease) || DateTime.UtcNow - File.GetLastWriteTimeUtc(lease) > TimeSpan.FromSeconds(2)) cancellation.Cancel();
                            using (var p = Process.GetProcessById(parent)) if (p.HasExited) cancellation.Cancel();
                            // Native owns the unique managed DI snapshot loop.
                            // This file supervisor drains only queued callbacks.
                            Tuple<string,string> item;
                            if(writeLink!=null)while(linkEvents.TryDequeue(out item))writeLink(item.Item1,item.Item2);
                        }
                        catch { cancellation.Cancel(); }
                        await Task.Delay(20);
                    }
                });
                var progress = new FactoryProgressJournal(directory, error => cancellation.Cancel(), cancellation.Token);
                Action<string,string> report = progress.Report;
                writeLink=report;
                var capturePublisher = new FactoryCapturePublisher(directory, report);
                Exception originalFailure = null;
                try
                {
                    if(resident!=null)report("DEVICE_SESSION","复用常驻设备会话 SessionId="+resident.SessionId+"；本轮独立参数与记录，不代表输出许可。");
                    if(args[1]=="WatchConnection")
                    {FactoryConnectionWatch.Run(settings.Input,report,cancellation.Token);return 0;}
                    if (args[1] == "Discover")
                    {
                        DeviceDiscovery.Run(settings.Input, report, cancellation.Token).Save(Path.Combine(directory, "discovery.xml"));
                        return 0;
                    }
                    if (args[1] == "DiagnoseP04")
                    {
                        string diagnostic = FactoryP04Diagnostic.Run(settings.Input, new P04ReadOnlyNative(), report, cancellation.Token);
                        File.WriteAllText(Path.Combine(directory, "p04-diagnostic.txt"), diagnostic, new UTF8Encoding(true));
                        return 0;
                    }
                    if (args[1] == "DiagnoseFeedback")
                    {
                        string diagnostic = FactoryAiDiagnostic.Run(settings,new FactoryAiReadOnlyNative(),Path.Combine(directory,"ai-raw.csv"),report,cancellation.Token);
                        File.WriteAllText(Path.Combine(directory,"ai-diagnostic.txt"),diagnostic,new UTF8Encoding(true));
                        return 0;
                    }
                    FactoryRunKind kind;
                    if (!Enum.TryParse(args[1], out kind) || !Enum.IsDefined(typeof(FactoryRunKind), kind)) throw new ArgumentException("工作命令无效。");
                    var clock = new FactoryClock();
                    FactoryResult result;
                    if (kind == FactoryRunKind.Pressure && settings.TargetKv == 0 && !settings.ZeroSynchronizedOptical)
                    {
                        // Same resident Pico handle and sole P0.4 reader across
                        // positive/zero rounds; no legacy close/open handoff.
                        monitoredBackend=resident;
                        AttachRoundConnectionReport(monitoredBackend,(key,text)=>linkEvents.Enqueue(Tuple.Create(key,text)));
                        result = FactoryZeroInputRunner.Run(settings, settings.Simulation ? (IFactoryZeroInput)new SimulationFactoryZeroInput(clock) :
                            resident!=null ? (IFactoryZeroInput)new ResidentFactoryZeroInput(resident) : new NativeFactoryZeroInput(), clock, report, cancellation.Token);
                    }
                    else
                    {
                        IFactoryBackend backend = settings.Simulation ? (IFactoryBackend)new SimulationFactoryBackend(clock) : resident ?? new NativeFactoryBackend();
                        monitoredBackend=backend as NativeFactoryBackend;
                        if(monitoredBackend!=null)monitoredBackend.FeedbackReadCancellation=cancellation.Token;
                        AttachRoundConnectionReport(monitoredBackend,(key,text)=>linkEvents.Enqueue(Tuple.Create(key,text)));
                        using (var recorded = new FactoryAiRecordingBackend(backend,directory,settings,clock,report,resident!=null))
                            result = settings.ZeroSynchronizedOptical
                                ? FactoryZeroTriggerRunner.Run(settings,recorded,clock,(key,text)=>{recorded.ObserveStage(key);report(key,text);},cancellation.Token)
                                : FactoryRunner.Run(settings, kind, recorded, clock,
                                    (key,text) => { recorded.ObserveStage(key); report(key,text); }, cancellation.Token,
                                    kind==FactoryRunKind.Pressure&&settings.DesktopPostProcessing ? (Action<FactoryResult>)null : capturePublisher.Queue);
                    }
                    result.DirectoryPath = directory;
                    // The watcher is asynchronous. Check the owned round marker
                    // synchronously at completion edges as well, without granting
                    // a receipt: the server still requires exact STOP binding and
                    // actual EndRound/session facts.
                    if(File.Exists(Path.Combine(directory,"cancel.flag")))cancellation.Cancel();
                    FactoryHealthyRoundStopEvidence.ThrowIfCanceledAfterRound(result,cancellation.Token);
                    var rangeQuality = result.CaptureOnlyAvailable()
                        ? FactoryRangeReview.EvaluateCapturedData(result) : FactoryRangeReview.Evaluate(result);
                    result.Metrics.RangeQuality = rangeQuality;
                    result.Metrics.RecommendedRangeIndex = rangeQuality.WindowValid ? rangeQuality.RecommendedIndex : -1;
                    result.Metrics.RangeDetail = rangeQuality.WindowDefinition + "；" + rangeQuality.Reason + "；" + rangeQuality.LocationQuality;
                    SaveResult(result, directory, false);
                    report("RECORD", "原始 ADC、电压、参数、诊断和结果已保存：" + directory);
                    if(File.Exists(Path.Combine(directory,"cancel.flag")))cancellation.Cancel();
                    FactoryHealthyRoundStopEvidence.ThrowIfCanceledAfterRound(result,cancellation.Token);
                    if(savedResult!=null)savedResult(result);
                    return 0;
                }
                catch (Exception ex)
                {
                    originalFailure = ex;
                    var stoppedInput=ex as FactoryInputStopCompletedException;
                    if(stoppedInput!=null&&resident!=null&&inputStopped!=null)
                    {
                        // This local fact is not the cross-process proof. The
                        // resident server still verifies fresh DI/session health
                        // and the exact accepted request before publishing it.
                        stoppedInput.PartialResult.DirectoryPath=directory;
                        stoppedInput.PartialResult.Completed=false;
                        SaveResult(stoppedInput.PartialResult,directory,true);
                        report("STEP_COMPLETE_STOPPED",stoppedInput.Message);
                        inputStopped();return FactoryInputStopEvidence.ResultCode;
                    }
                    var runFailure = ex as FactoryRunException;
                    var partial = runFailure == null ? null : runFailure.PartialResult;
                    if (partial != null)
                    {
                        partial.DirectoryPath = directory; partial.Completed = false;
                        try { SaveResult(partial, directory, true); if(partial.Capture!=null) report("FAILED_CAPTURE_FILE",directory); }
                        catch (Exception saveError)
                        {
                            originalFailure = new AggregateException("原始失败与失败记录保存错误均保留。", originalFailure, saveError);
                            try { report("RECORD_ERROR", "失败记录保存错误：" + saveError); }
                            catch (Exception publishError) { originalFailure = new AggregateException(originalFailure, publishError); }
                        }
                    }
                    try { report(args[1] == "DiagnoseP04" ? "P04_DIAG_ERROR" : args[1] == "DiagnoseFeedback" ? "AI_DIAG_ERROR" : "STEP_COMPLETE_ERROR", originalFailure.ToString()); }
                    catch (Exception publishError) { originalFailure = new AggregateException("原始失败与错误进度发布失败均保留。", originalFailure, publishError); }
                    File.WriteAllText(Path.Combine(directory, "error.txt"), originalFailure.ToString(), new UTF8Encoding(false));
                    // Mark only a pure canceled runner whose own EndRound facts
                    // have returned. Record/journal/finally failures still throw
                    // to the server and prevent a cross-process stop receipt.
                    if(resident!=null&&healthyRoundStopped!=null&&cancellation.IsCancellationRequested&&
                        Object.ReferenceEquals(originalFailure,ex)&&FactoryHealthyRoundStopEvidence.IsOnlyCanceledFailure(ex)&&
                        resident.HealthyRoundStopFacts!=null&&resident.HealthyRoundStopFacts.Complete&&resident.CanReuseSession)
                        healthyRoundStopped();
                    if(resident!=null&&emergencyRoundStopped!=null&&Object.ReferenceEquals(originalFailure,ex)&&
                        FactoryEmergencyRoundStopEvidence.IsOnlyStopFailure(ex)&&resident.CanRetainEmergencyDisplaySession)
                        emergencyRoundStopped();
                    return 1;
                }
                finally
                {
                    try
                    {
                        try { capturePublisher.Dispose(); }
                        finally { cancellation.Cancel(); try { watch.Wait(1000); } catch { } }
                        Tuple<string,string> item;while(linkEvents.TryDequeue(out item))report(item.Item1,item.Item2);
                    }
                    finally
                    {
                        try { if(monitoredBackend!=null) SaveIoTiming(monitoredBackend.IoTiming,directory,report); }
                        finally
                        {
                            // Native Stop/cleanup and capture publication have
                            // already been attempted. Only now drain the journal;
                            // a failed drain must not turn a pending return into 0.
                            if(monitoredBackend!=null)monitoredBackend.FeedbackReadCancellation=CancellationToken.None;
                            try { progress.Dispose(); }
                            catch (Exception journalError)
                            {
                                Exception completeError = originalFailure == null ? journalError :
                                    new AggregateException("本轮原始失败与进度记录收尾错误均保留。", originalFailure, journalError);
                                try { File.WriteAllText(Path.Combine(directory,"error.txt"),completeError.ToString(),new UTF8Encoding(false)); }
                                catch (Exception saveError) { completeError = new AggregateException(completeError,saveError); }
                                throw completeError;
                            }
                        }
                    }
                }
            }
        }
        // Executed by the worker only after the runner's disposal path, never
        // by the DI reader. A failed diagnostic save cannot mask the run result.
        private static FactoryDeviceLease AcquireDeviceLease(string directory)
        {
            try {return FactoryDeviceLease.Acquire();}
            catch(Exception ex)
            {
                try {File.WriteAllText(Path.Combine(directory,"error.txt"),ex.ToString(),new UTF8Encoding(false));}catch{}
                throw;
            }
        }
        internal static void SaveIoTiming(FactoryIoTimingDiagnostics timing,string directory,Action<string,string> report)
        {
            string path;
            try
            {
                path=Path.Combine(directory,"io-timing.tsv");
                timing.WriteSnapshot(path);
            }
            catch(Exception ex)
            {
                try { if(report!=null)report("IO_TIMING_ERROR","耗时诊断保存失败（不改本轮结果）："+ex.Message); }
                catch { /* A diagnostic-report failure must not replace control errors. */ }
                return;
            }
            try
            {
                if(report!=null)report("IO_TIMING_FILE",path+"；仅主机耗时诊断，保护与输出流程未改。");
            }
            catch(Exception ex)
            {
                try { if(report!=null)report("IO_TIMING_REPORT_ERROR","耗时诊断文件已保存："+path+"；保存通知失败（不改本轮结果）："+ex.Message); }
                catch { /* A notification failure cannot undo a successful save. */ }
            }
        }
        internal static void Drain(string path, Action<string,string> report, ref long delivered)
        {
            if (!File.Exists(path)) return;
            // Incremental byte cursor: a long-lived idle watcher must not reread
            // its entire history every 80 ms. Incomplete UTF-8 lines stay pending.
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if(f.Length<delivered)throw new IOException("进度日志被截断");
                f.Position=delivered;
                while(f.Position<f.Length)
                {
                    var bytes=new byte[(int)Math.Min(1048576,f.Length-f.Position)];
                    int count=f.Read(bytes,0,bytes.Length);if(count==0)break;
                    int last=Array.LastIndexOf(bytes,(byte)10,count-1,count);
                    if(last<0){if(count==1048576)throw new IOException("进度日志单行过长");break;}
                    foreach(string line in Encoding.UTF8.GetString(bytes,0,last+1).Split('\n'))
                    {
                        string[] fields=line.TrimEnd('\r').Split(new[]{'\t'},3);
                        if(fields.Length==3)report(fields[1],fields[2]);
                    }
                    delivered+=last+1;f.Position=delivered;
                }
            }
        }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "") + "\""; }
        internal static void SaveResult(FactoryResult result, string directory, bool partial)
        {
            if (result.Capture != null) result.Capture.Save(directory);
            result.Settings.Save(Path.Combine(directory, "factory-settings.xml"));
            string output = Path.Combine(directory, partial ? "partial-result.xml" : "result.xml");
            string temporary = output + ".pending";
            using (var f = File.Create(temporary)) new XmlSerializer(typeof(FactoryResult)).Serialize(f, result);
            File.Move(temporary, output);
            if(!partial)FactoryLocationCycleArchive.Save(result,directory);
        }
        private static FactoryResult LoadResult(string path)
        { using (var f = File.OpenRead(path)) return (FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(f); }
        internal static void Export(FactoryResult result, string path)
        {
            if (result == null || !result.Completed || result.Capture == null) throw new InvalidOperationException("只能导出本轮完整结果。");
            if (result.FullMetricsDeferred || result.Metrics == null || result.Metrics.Detected == null)
                throw new InvalidOperationException("检测波形尚未计算，不能导出为已分析结果。");
            var detected = result.Metrics.Detected;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("time_ms,divider_kV,pd_detected_pC,simulation,overflow");
                for (int i = 0; i < detected.OutputCount; i++)
                    w.WriteLine((i * 1000 / detected.OutputRateHz).ToString("G17", CultureInfo.InvariantCulture) + "," +
                        detected.VoltageKv[i].ToString("G17", CultureInfo.InvariantCulture) + "," +
                        detected.PdEnvelopePc[i].ToString("G17", CultureInfo.InvariantCulture) + "," + result.Settings.Simulation + "," + result.Capture.Overflow);
            }
        }
    }
}
