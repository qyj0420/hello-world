using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    // One-time, explicit hand-off to the private binding UI. Normal application
    // startup never calls this class and never opens the full first-bind tool.
    internal static class FirstHardwareProvisioning
    {
        private static readonly string AppRoot = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string FirstBindExe = Path.GetFullPath(Path.Combine(
            AppRoot, "private", "FirstBind", "HV-DAC-Xpert.exe"));
        private static string ExactConnectorExe { get { return GetVerifiedConnectorPath(); } }
        private static readonly string ParentHostExe = Path.GetFullPath(Path.Combine(
            AppRoot, "private", "ParentHost.exe"));
        private static readonly string ParentManifest = Path.GetFullPath(Path.Combine(
            AppRoot, "private", "parent-manifest.xml"));
        private static readonly string ModuleManifest = Path.GetFullPath(Path.Combine(
            AppRoot, "module-manifest.json"));
        private static readonly string ParentState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
#if PUBLIC_BETA_NO_DRIVER_V112_20261011
            "XIONGXIN", "HVXPublicBetaNoDriver11220261011", "install-state.xml");
#elif PUBLIC_BETA_NO_DRIVER_20261011
            "XIONGXIN", "HVXPublicBetaNoDriver20261011", "install-state.xml");
#elif PRESSURE_STANDALONE_20261008
            "XIONGXIN", "HVXPressureStandalone", "install-state.xml");
#elif V30_BATCH_VALIDATION_20261008
            "XIONGXIN", "HVXValidationV30Batch", "install-state.xml");
#elif V30_VALIDATION_20261007
            "XIONGXIN", "HVXValidationV30", "install-state.xml");
#elif R27_VALIDATION_20261007
            // This standalone validation installation never consumes another
            // package's global completion marker or hardware-mode consent.
            "XIONGXIN", "HVXValidationR27", "install-state.xml");
#else
            "XIONGXIN", "HVX100UnifiedParent", "install-state.xml");
#endif

        internal static string InstallationGate()
        {
            // A standalone development build must never elevate an unverified
            // first-bind executable. Unified parent completion is mandatory.
            if (!File.Exists(ParentManifest))
                return "一体化安装清单不存在；请运行完整安装包后再做首次绑定。";
            if (!File.Exists(ParentState))
                return "一体化安装尚未记录完成状态；请重新运行安装包。";
            try
            {
                if (new FileInfo(ParentManifest).Length > 4 * 1024 * 1024 ||
                    new FileInfo(ParentState).Length > 16384)
                    return "一体化安装清单或状态文件过大。";
                string stage; bool hardware;
                return ValidateParentStateDocuments(File.ReadAllText(ParentManifest),
                    File.ReadAllText(ParentState),out stage,out hardware);
            }
            catch (Exception error)
            {
                return "无法确认一体化安装状态：" + error.Message;
            }
        }

        // Ordinary discovery/reconnection is not first-time elevated binding.
        // Standalone payloads may use their verified connector without a parent installer.
        internal static string DiscoveryGate()
        {
            if (RequiresSessionOffline()) return InstallationGate();
            try { VerifyExactConnector(); return String.Empty; }
            catch (Exception error)
            {
                return "设备连接组件未通过检查，请使用完整程序目录重试。 / Device connection component validation failed; use the complete application folder.\r\n" + error.Message;
            }
        }

        internal static bool CanResumeInstallation()
        {
            if (!File.Exists(ParentHostExe) || !File.Exists(ParentState) ||
                !File.Exists(ParentManifest)) return false;
            try
            {
                string stage; bool hardware;
                string gate = ValidateParentStateDocuments(File.ReadAllText(ParentManifest),
                    File.ReadAllText(ParentState),out stage,out hardware);
                return gate.Length > 0 && stage.Length > 0 && stage != "Complete" && hardware;
            }
            catch { return false; }
        }

        internal static bool RequiresSessionOffline()
        {
            // A damaged/partial unified installation may have lost its manifest
            // while retaining the parent helper or state. Never treat that as
            // permission to fall back to an older Enabled=true profile.
            bool hasParentFootprint = File.Exists(ParentManifest) || File.Exists(ParentHostExe) ||
                File.Exists(ParentState);
            return hasParentFootprint && InstallationGate().Length > 0;
        }

        // Pure document gate: a stale ProgramData state from a different package
        // cannot authorize a newly staged payload, even if it says Complete.
        internal static string ValidateParentStateDocuments(string manifestXml, string stateXml,
            out string stage, out bool hardware)
        {
            stage = String.Empty; hardware = false;
            try
            {
                XmlDocument manifest = ReadSmallXml(manifestXml,4 * 1024 * 1024);
                XmlDocument state = ReadSmallXml(stateXml,16384);
                XmlElement m = manifest.DocumentElement, s = state.DocumentElement;
                if (m == null || m.Name != "ParentManifest" || m.GetAttribute("Schema") != "1" ||
                    s == null || s.Name != "ParentInstallState" || s.GetAttribute("Schema") != "1")
                    return "一体化安装清单或状态格式无效。";
                string packageId = m.GetAttribute("PackageId");
                if (packageId.Length != 64 || !packageId.All(Uri.IsHexDigit))
                    return "一体化安装清单缺少有效包身份。";
                string[] rows = m.SelectNodes("File").OfType<XmlElement>().Select(item =>
                    item.GetAttribute("Path") + "|" + item.GetAttribute("Sha256") + "|" + item.GetAttribute("Scope"))
                    .OrderBy(item=>item,StringComparer.OrdinalIgnoreCase).ToArray();
                using (var sha=SHA256.Create())
                {
                    string actual=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                        String.Join("\n",rows)))).Replace("-",String.Empty);
                    if (!String.Equals(packageId,actual,StringComparison.OrdinalIgnoreCase))
                        return "一体化安装清单包身份校验失败。";
                }
                if (!String.Equals(packageId,s.GetAttribute("PackageId"),StringComparison.OrdinalIgnoreCase))
                    return "安装状态属于另一安装包；不能沿用旧完成标记。";
                string step=s.GetAttribute("Stage");
                bool hasHardware;
                if (!new[] {"Verify","DotNet","Hub","Art","Pico","Complete"}.Contains(step) ||
                    !Boolean.TryParse(s.GetAttribute("Hardware"),out hasHardware))
                    return "一体化安装阶段或硬件选择无效。";
                stage=step; hardware=hasHardware;
                if (step != "Complete")
                    return "一体化安装尚未完成（" + step + "）；请以管理员身份继续安装。";
                if (!hasHardware)
                    return "安装时选择了离线模式；请重新运行同一安装包并选择安装硬件组件。";
                return String.Empty;
            }
            catch (Exception error)
            {
                return "无法确认一体化安装状态：" + error.Message;
            }
        }

        private static XmlDocument ReadSmallXml(string value, int limit)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > limit)
                throw new InvalidDataException("安装文档为空或超过长度限制。");
            var options=new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,
                XmlResolver=null, MaxCharactersInDocument=limit };
            var document=new XmlDocument { XmlResolver=null };
            using (var source=new StringReader(value))
            using (var reader=XmlReader.Create(source,options)) document.Load(reader);
            return document;
        }

        internal static void ResumeInstallation()
        {
            if (!CanResumeInstallation())
                throw new InvalidOperationException("没有可续装的一体化安装阶段。");
            Process.Start(new ProcessStartInfo {
                FileName = ParentHostExe, Arguments = "--resume",
                WorkingDirectory = Path.GetDirectoryName(ParentHostExe),
                UseShellExecute = true, Verb = "runas"
            });
        }

        internal static ProvisioningActivation Run(AppSettings basis, Action notifyOvertime = null)
        {
            if (basis == null || basis.Hardware == null)
                throw new ArgumentException("缺少当前离线配置。", "basis");
            if (basis.SessionHardwareForcedOffline)
                throw new InvalidOperationException("本次软件会话被安装状态强制离线；完成安装后请重新打开软件。");
            if (basis.Hardware.Enabled)
                throw new InvalidOperationException("设备已配置；更换硬件必须走显式切换流程，不能重复使用首次绑定。");
            string gate = InstallationGate();
            if (gate.Length > 0) throw new InvalidOperationException(gate);
            if (!File.Exists(FirstBindExe) || !File.Exists(ExactConnectorExe))
                throw new FileNotFoundException("首次绑定组件或当前精确连接器不存在；请重新运行安装包。");
            VerifyPrivatePayload();
            VerifyExactConnector();

            string resultDirectory = Path.Combine(AppPaths.LocalDataDirectory, "first-bind-results");
            Directory.CreateDirectory(resultDirectory);
            string resultPath = Path.Combine(resultDirectory, "provision-" + Guid.NewGuid().ToString("N") + ".xml");
            string bindGatePath = Path.Combine(resultDirectory, "first-bind.write.lock");
            // Held for the entire elevated child lifetime, including an overtime
            // child. Another app instance may not begin a second Apply meanwhile.
            using (var bindGate = new FileStream(bindGatePath,FileMode.OpenOrCreate,
                FileAccess.ReadWrite,FileShare.None))
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo {
                    FileName = FirstBindExe,
                    Arguments = "--provision-output=\"" + resultPath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(FirstBindExe),
                    UseShellExecute = true,
                    Verb = "runas"
                }))
                {
                    if (process == null) throw new InvalidOperationException("首次绑定程序未启动。");
                    if (!WaitForBindingExit(process.WaitForExit,process.WaitForExit,notifyOvertime))
                    {
                        throw new TimeoutException("首次绑定窗口超过 15 分钟；已等候它退出，本次结果不会导入。");
                    }
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("首次绑定未完成（退出码 " + process.ExitCode + "）；保持离线。");
                }
                // The output path is unique for this attempt. A failed/cancelled
                // child never causes a previous result to be read.
                ProvisioningCandidate candidate = ProvisioningImportAdapter.ReadFile(resultPath, DateTime.UtcNow);
                AppSettings probe = basis.Clone();
                probe.Hardware.Enabled = true; // only in memory
                probe.Hardware.SelectedHubMac = candidate.Mac;
                probe.Hardware.ControllerLocation = candidate.ControllerLocation;
                probe.Hardware.AcquisitionLocation = candidate.AcquisitionLocation;
                probe.Hardware.StatusJsonPath = String.Empty;
                probe.Hardware.DriverMarkerPath = String.Empty;
                probe.Hardware.ConnectorExecutablePath = String.Empty;
                probe.Hardware.IntegrationExecutablePath = ExactConnectorExe;
                string validation;
                if (!probe.TryValidate(out validation)) throw new InvalidDataException(validation);
                VerifyExactConnector();

                // The 100 connector's --inspect is read-only. The bridge already
                // requested Apply; no second connection request occurs here.
                HardwareStatusSnapshot snapshot = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    using (var adapter = new HardwareStatusAdapter(probe))
                        snapshot = adapter.Refresh(false);
                    if (snapshot != null && snapshot.Ready) break;
                    if (attempt < 3) Thread.Sleep(1500);
                }
                HardwareStatusSnapshot exact = snapshot;
                ProvisioningActivation activation = ProvisioningImportAdapter.Inspect(candidate, ignored =>
                {
                    return new ExactInspection {
                        FromExactConnectorInspect = exact != null && exact.FromLiveIntegrationProcess,
                        Ready = exact != null && exact.Ready,
                        StatusCode = exact == null ? String.Empty : exact.StatusCode,
                        HubMac = exact == null ? String.Empty : exact.HubMac,
                        RequestedMac = exact == null ? String.Empty : exact.RequestedHubMac,
                        RequestedControllerLocation = exact == null ? String.Empty : exact.RequestedControllerLocation,
                        RequestedAcquisitionLocation = exact == null ? String.Empty : exact.RequestedAcquisitionLocation,
                        IntegrationExecutablePathUsed = exact == null ? String.Empty : exact.IntegrationExecutablePathUsed,
                        ExpectedIntegrationExecutablePath = ExactConnectorExe,
                        IntegrationRequestMode = exact == null ? String.Empty : exact.IntegrationRequestMode,
                        ControllerSerial = exact == null ? String.Empty : exact.ControllerSerial,
                        AcquisitionSerial = exact == null ? String.Empty : exact.AcquisitionSerial,
                        ControllerInstanceId = exact == null ? String.Empty : exact.ControllerInstanceId,
                        AcquisitionInstanceId = exact == null ? String.Empty : exact.AcquisitionInstanceId,
                        ControllerTransportSerial = exact == null ? String.Empty : exact.ControllerTransportSerial,
                        AcquisitionTransportSerial = exact == null ? String.Empty : exact.AcquisitionTransportSerial,
                        CheckedUtc = exact == null ? DateTime.MinValue : exact.CheckedUtc
                    };
                }, () => DateTime.UtcNow);
                if (!activation.HardwareEnabled)
                    throw new InvalidOperationException("100 精确连接器未完成当前设备核验：" + activation.Reason);
                if (!activation.StrongTransportIdentity)
                    throw new InvalidOperationException("当前两张卡的 PnP 传输身份未同时核验；保持离线，请刷新设备并重试。");
                return activation;
            }
            finally
            {
                try { if (File.Exists(resultPath)) File.Delete(resultPath); }
                catch { /* Unique path is never reused; cleanup is best effort. */ }
            }
        }

        internal static bool WaitForBindingExit(Func<int,bool> waitTimed, Action waitUntilExit,
            Action notifyOvertime = null)
        {
            if (waitTimed == null || waitUntilExit == null)
                throw new ArgumentNullException("Binding process waiter is missing.");
            if (waitTimed((int)TimeSpan.FromMinutes(15).TotalMilliseconds)) return true;
            try { if (notifyOvertime != null) notifyOvertime(); }
            catch { /* Notification failure must not release an active elevated child. */ }
            // Never abandon a still-running elevated Apply or blindly kill its
            // driver work. Caller keeps its busy flag and exclusive gate until
            // the user closes the actual child process.
            waitUntilExit();
            return false;
        }

        internal static Task<ProvisioningActivation> RunAsync(AppSettings basis,
            Action notifyOvertime = null)
        {
            var completion = new TaskCompletionSource<ProvisioningActivation>();
            var worker = new Thread(new ThreadStart(delegate {
                try { completion.SetResult(Run(basis,notifyOvertime)); }
                catch (Exception error) { completion.SetException(error); }
            }));
            worker.Name = "HVX first binding";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA); // Shell elevation owns COM dialogs.
            worker.Start();
            return completion.Task;
        }

        internal static AppSettings ApplyToClone(AppSettings current, ProvisioningActivation verified)
        {
            if (current == null || verified == null || !verified.HardwareEnabled ||
                !verified.StrongTransportIdentity || verified.Candidate == null)
                throw new InvalidOperationException("没有已通过精确检查的首次绑定结果。");
            if (current.Hardware == null || current.Hardware.Enabled)
                throw new InvalidOperationException("当前已启用硬件；禁止首次绑定覆盖原设备身份。");
            if (!ProvisioningImportAdapter.IsCurrentActivation(verified,DateTime.UtcNow))
                throw new InvalidOperationException("首绑结果已过期或核验凭据已变化；保持离线，请重新绑定。");
            VerifyExactConnector();
            AppSettings result = current.Clone();
            result.Hardware.Enabled = true;
            result.Hardware.SelectedHubMac = verified.Candidate.Mac;
            result.Hardware.ControllerLocation = verified.Candidate.ControllerLocation;
            result.Hardware.AcquisitionLocation = verified.Candidate.AcquisitionLocation;
            result.Hardware.ControllerSerial = verified.ControllerSerial;
            result.Hardware.AcquisitionSerial = verified.AcquisitionSerial;
            result.Hardware.StatusJsonPath = String.Empty;
            result.Hardware.DriverMarkerPath = String.Empty;
            result.Hardware.ConnectorExecutablePath = String.Empty;
            result.Hardware.IntegrationExecutablePath = ExactConnectorExe;
            result.Hardware.FirstConfigurationToolPath = FirstBindExe;
            result.DeviceParametersReviewedHash = String.Empty;
            result.DeviceParametersReviewedIdentity = String.Empty;
            result.DeviceParametersReviewedUtc = String.Empty;
            string validation;
            if (!result.TryValidate(out validation)) throw new InvalidDataException(validation);
            return result;
        }

        internal static void VerifyExactConnector()
        {
            if(EmbeddedModulePayload.HasEmbeddedPayload){GetVerifiedConnectorPath();return;}
            if (!File.Exists(ModuleManifest) || !File.Exists(ExactConnectorExe))
                throw new FileNotFoundException("当前发布版缺少连接器或模块校验清单。");
            if (new FileInfo(ModuleManifest).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("模块清单过大。");
            var root = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 }
                .DeserializeObject(File.ReadAllText(ModuleManifest)) as Dictionary<string, object>;
            if (root == null || Convert.ToString(root["Schema"]) != "XIONGXIN.UnifiedDesktop.Modules.v2")
                throw new InvalidDataException("模块清单版本无效。");
            object modulesValue;
            if (!root.TryGetValue("Modules",out modulesValue))
                throw new InvalidDataException("模块清单缺少模块列表。");
            object[] modules = modulesValue as object[];
            if (modules == null) throw new InvalidDataException("模块清单结构无效。");
            const string relative = "modules/HardwareIntegration/HardwareIntegration.exe";
            string expected = null;
            long expectedLength = -1;
            foreach (object item in modules)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null) continue;
                object path;
                if (!entry.TryGetValue("Path",out path) ||
                    !String.Equals(Convert.ToString(path),relative,StringComparison.OrdinalIgnoreCase)) continue;
                if (expected != null) throw new InvalidDataException("模块清单含有重复连接器。");
                object sha, bytes;
                if (!entry.TryGetValue("Sha256",out sha) || !entry.TryGetValue("Bytes",out bytes))
                    throw new InvalidDataException("连接器清单项不完整。");
                expected = Convert.ToString(sha);
                expectedLength = Convert.ToInt64(bytes);
            }
            if (String.IsNullOrWhiteSpace(expected) || expected.Length != 64 ||
                new FileInfo(ExactConnectorExe).Length != expectedLength)
                throw new InvalidDataException("连接器文件与模块清单不一致。");
            if (File.Exists(ParentManifest))
            {
                var xml = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
                var document = new XmlDocument { XmlResolver = null };
                using (var stream = File.OpenRead(ParentManifest))
                using (var reader = XmlReader.Create(stream,xml)) document.Load(reader);
                int matches = 0;
                foreach (XmlNode node in document.SelectNodes("/ParentManifest/File"))
                {
                    var entry = node as XmlElement;
                    if (entry == null || !String.Equals(entry.GetAttribute("Path"),relative,StringComparison.OrdinalIgnoreCase)) continue;
                    matches++;
                    if (!String.Equals(entry.GetAttribute("Sha256"),expected,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装清单与模块清单的连接器哈希不一致。");
                }
                if (matches != 1) throw new InvalidDataException("安装清单必须唯一列出当前连接器。");
            }
            using (var stream = File.OpenRead(ExactConnectorExe))
            using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",String.Empty);
                if (!String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("当前连接器文件 SHA-256 校验失败。");
            }
        }

        internal static string GetVerifiedConnectorPath()
        {
            return ConnectorInModules(EmbeddedModulePayload.ResolveModulesRoot());
        }
        // Isolated offline packaging check: extraction/validation only, never
        // start the connector or inspect devices. No external path fallback.
        internal static string GetVerifiedConnectorPath(Assembly owner,string cacheRoot)
        {
            return ConnectorInModules(EmbeddedModulePayload.ResolveModulesRoot(owner,cacheRoot));
        }
        static string ConnectorInModules(string modules)
        {
            string path=Path.GetFullPath(Path.Combine(modules,"HardwareIntegration","HardwareIntegration.exe"));
            if(!File.Exists(path))throw new FileNotFoundException("设备连接组件缺失，请修复安装。");
            return path;
        }

        private static void VerifyPrivatePayload()
        {
            if (!File.Exists(ParentManifest))
                throw new InvalidDataException("未找到一体化安装清单，禁止以管理员身份启动首次绑定组件。");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            var document = new XmlDocument { XmlResolver = null };
            using (var stream = File.OpenRead(ParentManifest))
            using (var reader = XmlReader.Create(stream,settings)) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "ParentManifest" ||
                document.DocumentElement.GetAttribute("Schema") != "1")
                throw new InvalidDataException("一体化安装清单格式无效。");
            foreach (string relative in new[] {
                "private/FirstBind/HV-DAC-Xpert.exe",
                "private/FirstBind/runtime/SxConnectDev.exe",
                "private/FirstBind/runtime/Sxuptp.dll" })
            {
                string full = Path.GetFullPath(Path.Combine(AppRoot,relative.Replace('/',Path.DirectorySeparatorChar)));
                if (!File.Exists(full)) throw new FileNotFoundException("首次绑定组件不完整：" + relative);
                string expected = null;
                foreach (XmlNode node in document.SelectNodes("/ParentManifest/File"))
                {
                    var item = node as XmlElement;
                    if (item == null || !String.Equals(item.GetAttribute("Path"),relative,StringComparison.OrdinalIgnoreCase)) continue;
                    if (expected != null) throw new InvalidDataException("首次绑定组件在安装清单中重复：" + relative);
                    expected = item.GetAttribute("Sha256");
                }
                if (String.IsNullOrEmpty(expected) || expected.Length != 64)
                    throw new InvalidDataException("首次绑定组件在安装清单中缺失：" + relative);
                using (var stream = File.OpenRead(full))
                using (var sha = SHA256.Create())
                {
                    string actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",String.Empty);
                    if (!String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("首次绑定组件 SHA-256 校验失败：" + relative);
                }
            }
        }
    }
}
