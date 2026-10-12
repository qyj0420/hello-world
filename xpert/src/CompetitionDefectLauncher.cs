using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class CompetitionDefectLauncher : IDisposable
    {
        private readonly IWin32Window owner;
        private readonly object gate = new object();
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private NamedPipeServerStream channel;
        private Process child;
        private string latest;
        private long revision, activation;
        private volatile bool disposed;

        internal CompetitionDefectLauncher(IWin32Window ownerWindow) { owner = ownerWindow; }

        internal void Open(AppSettings settings, ApplicationState state)
        {
            if (disposed) throw new ObjectDisposedException(GetType().Name);
            if (settings == null || !settings.EnableCompetitionDefectModule)
                throw new InvalidOperationException("厂家维护尚未启用独立缺陷识别模块。");
            string normalized, error;
            if (state == null || !ProjectValidator.TryValidate(state.ProjectPath, out normalized, out error))
                throw new InvalidOperationException("请先在主程序选择有效项目，再打开独立缺陷识别。");
            activation++;
            UpdateContext(settings, state);
            if (child != null && !child.HasExited) return;
            CloseChannel();
            if (child != null) { child.Dispose(); child = null; }
            string pipeName = "HVX.Defect." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
            byte[] random = new byte[32]; using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            StringBuilder token = new StringBuilder(64); foreach (byte item in random) token.Append(item.ToString("x2"));
            NamedPipeServerStream pipe = CompetitionDefectProtocol.CreateServer(pipeName);
            channel = pipe;
            try
            {
                // Only this bundled host is launched. No configured executable or hardware helper is used.
                child = Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    "--defect-recognition " + pipeName + " " + token)
                { UseShellExecute = false, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
                if (child == null) throw new IOException("Unable to start the standalone module.");
                int childId = child.Id;
                Task.Run(delegate { Serve(pipe, childId, token.ToString()); });
            }
            catch { CloseChannel(); throw; }
        }

        internal void UpdateContext(AppSettings settings, ApplicationState state)
        {
            if (disposed || settings == null) return;
            CompetitionDefectContext context = CompetitionDefectContext.Create(settings, state);
            lock (gate)
            {
                context.Revision = ++revision; context.Activation = activation;
                latest = CompetitionDefectProtocol.Serialize(context);
            }
            changed.Set();
        }

        private void Serve(NamedPipeServerStream pipe, int childId, string token)
        {
            try
            {
                using (System.Threading.Timer timeout = new System.Threading.Timer(delegate { try { pipe.Dispose(); } catch { } }, null, 15000, Timeout.Infinite))
                {
                    pipe.WaitForConnection();
                    CompetitionDefectProtocol.VerifyPeer(pipe, childId, true);
                    if (!String.Equals(CompetitionDefectProtocol.ReadText(pipe), token, StringComparison.Ordinal))
                        throw new UnauthorizedAccessException("Invalid standalone module launch token.");
                    timeout.Change(Timeout.Infinite, Timeout.Infinite);
                }
                while (!disposed && ReferenceEquals(channel, pipe))
                {
                    string snapshot; lock (gate) snapshot = latest;
                    if (!String.IsNullOrEmpty(snapshot)) CompetitionDefectProtocol.WriteText(pipe, snapshot);
                    changed.WaitOne(1000); // Heartbeat reports whether project synchronization is still connected.
                }
            }
            catch (Exception error)
            {
                if (!disposed && ReferenceEquals(channel, pipe))
                    new Diagnostics().Warning("CompetitionDefect", "Standalone module channel closed: " + error.Message);
            }
            finally { try { pipe.Dispose(); } catch { } }
        }

        private void CloseChannel()
        {
            NamedPipeServerStream previous = channel; channel = null;
            if (previous != null) try { previous.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; changed.Set(); CloseChannel();
            // The independently running child keeps its last project on EOF. Never kill it with the parent.
            if (child != null) { child.Dispose(); child = null; }
            changed.Dispose();
        }

        internal static int RunChild(string pipeName, string token)
        {
            int parentId = CompetitionDefectProtocol.ValidateArguments(pipeName, token);
            if (!AppSettings.Load().EnableCompetitionDefectModule)
                throw new InvalidOperationException("厂家维护已关闭独立缺陷识别模块，无法直接运行。");
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(10000);
                CompetitionDefectProtocol.VerifyPeer(pipe, parentId, false);
                CompetitionDefectProtocol.WriteText(pipe, token);
                CompetitionDefectContext initial;
                using (System.Threading.Timer timeout = new System.Threading.Timer(delegate { try { pipe.Dispose(); } catch { } }, null, 10000, Timeout.Infinite))
                {
                    initial = CompetitionDefectProtocol.Deserialize(CompetitionDefectProtocol.ReadText(pipe));
                    timeout.Change(Timeout.Infinite, Timeout.Infinite);
                }
                using (CompetitionDefectHostForm form = new CompetitionDefectHostForm(initial))
                {
                    form.Shown += delegate { form.BeginReceive(pipe); };
                    Application.Run(form);
                }
            }
            return 0;
        }
    }
}
