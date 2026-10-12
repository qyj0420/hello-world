using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.Scope
{
    internal static class Worker
    {
        static volatile bool cancel;
        static volatile bool shutdownFinished;
        static int shutdownRequested;
        internal static void Run(int parentId)
        {
            var queue = new BlockingCollection<Command>(); var engine = new Engine();
            var parent = Process.GetProcessById(parentId);
            Action requestShutdown=()=>
            {
                if(Interlocked.Exchange(ref shutdownRequested,1)!=0)return;
                cancel=true;queue.Add(new Command{Op="EXIT"});
                // Cleanup stays on the SDK-owning thread. A stuck native call is the last-resort case.
                new Thread(()=>{Thread.Sleep(6000);if(!shutdownFinished)Environment.Exit(91);}){IsBackground=true}.Start();
            };
            var watchdog = new Thread(() => { parent.WaitForExit(); requestShutdown(); }) { IsBackground = true }; watchdog.Start();
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = Console.ReadLine()) != null)
                    {
                        if (line == "CANCEL") { cancel = true; continue; }
                        var command = Wire.Decode<Command>(line);
                        if (command.Op == "CAPTURE") cancel = false;
                        queue.Add(command);
                    }
                }
                catch { }
                requestShutdown();
            }) { IsBackground = true }.Start();
            try { foreach (var cmd in queue.GetConsumingEnumerable())
            {
                Reply reply;
                try
                {
                    if (cmd.Op == "OPEN") reply = engine.Open(cmd.Config);
                    else if (cmd.Op == "SCAN") reply = engine.Scan(cmd.Config);
                    else if (cmd.Op == "PING") reply = engine.Ping(cmd.Config);
                    else if (cmd.Op == "CAPTURE")
                    {
                        string tracePath=String.IsNullOrEmpty(cmd.TracePath)?cmd.DataPath+".capture.log":cmd.TracePath;
                        engine.Trace=stage=>File.AppendAllText(tracePath,DateTime.UtcNow.ToString("O")+" "+stage+Environment.NewLine);
                        try { var capture = engine.Capture(cmd.Config, () => cancel); Wire.Save(capture, cmd.DataPath); reply = capture.Info; reply.DataPath = cmd.DataPath; }
                        finally { engine.Trace=null; }
                    }
                    else if (cmd.Op == "CLOSE" || cmd.Op == "EXIT") { engine.Close(); reply = engine.Status("设备已正常释放。"); }
                    else throw new ArgumentException("未知采集命令。");
                }
                catch (Exception ex)
                {
                    string error = ex is OperationCanceledException ? "已取消采集。" : ex.ToString();
                    if (cmd.Op == "CAPTURE" && engine.Connected && !engine.Faulted && !(ex is OperationCanceledException))
                    {
                        // An acquisition timeout is not itself a disconnection. Probe before recovery.
                        try { engine.Ping(cmd.Config); } catch (Exception probe) { error += "\r\n连接复核：" + probe; }
                    }
                    reply = engine.Status(error); reply.Ok = false;
                }
                if(shutdownRequested==0){Console.WriteLine(Wire.Encode(reply)); Console.Out.Flush();}
                if (cmd.Op == "EXIT" && reply.Ok) return;
            }}
            finally { try{engine.Close();}finally{shutdownFinished=true;} }
        }
    }
    internal interface ICalibrationWorker
    {
        bool Broken {get;}
        Task<Reply> Send(Command command,int timeoutMs);
        void Cancel();
        Task Close(int commandTimeout=5000,int exitTimeout=2000);
        Task<bool> RetireUnresponsive();
    }
    // The DI reader can cancel independently of the UI. A completed CANCEL
    // must not be followed by a CAPTURE which clears the worker's stop flag.
    internal sealed class CalibrationCommandWriter
    {
        readonly TextWriter writer;
        readonly object gate=new object();
        int cancelled;
        internal CalibrationCommandWriter(TextWriter writer){this.writer=writer??throw new ArgumentNullException("writer");}
        internal void Send(Command command)
        {
            string encoded=Wire.Encode(command);
            lock(gate){
                if(command.Op=="CAPTURE"&&Volatile.Read(ref cancelled)!=0)throw new OperationCanceledException("本次采集已取消，禁止再次下发采集命令。");
                writer.WriteLine(encoded);writer.Flush();
            }
        }
        internal void Cancel()
        {
            Interlocked.Exchange(ref cancelled,1);
            lock(gate){writer.WriteLine("CANCEL");writer.Flush();}
        }
        internal void Retire()
        {lock(gate){Cancel();Send(new Command{Op="EXIT"});}}
    }
    internal sealed class WorkerClient : ICalibrationWorker
    {
        readonly Process process;
        readonly CalibrationCommandWriter commandWriter;
        internal volatile bool Broken;
        bool ICalibrationWorker.Broken {get{return Broken;}}
        Task<Reply> ICalibrationWorker.Send(Command command,int timeoutMs){return Send(command,timeoutMs);}
        void ICalibrationWorker.Cancel(){Cancel();}
        Task ICalibrationWorker.Close(int commandTimeout,int exitTimeout){return Close(commandTimeout,exitTimeout);}
        Task<bool> ICalibrationWorker.RetireUnresponsive(){return RetireUnresponsive();}
        internal int Id { get { return process.Id; } }
        internal WorkerClient(string workerExecutable = null)
        {
            string executable = String.IsNullOrWhiteSpace(workerExecutable) ? Application.ExecutablePath : Path.GetFullPath(workerExecutable);
            if (!File.Exists(executable)) throw new FileNotFoundException("独立 Pico 采集进程不存在。", executable);
            process = new Process { StartInfo = new ProcessStartInfo(executable, "--worker " + Process.GetCurrentProcess().Id)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true } };
            process.Start();
            commandWriter=new CalibrationCommandWriter(process.StandardInput);
        }
        internal async Task<Reply> Send(Command command, int timeoutMs)
        {
            if (Broken) throw new InvalidOperationException("采集进程失去响应，请使用释放或强制恢复。");
            try
            {
                commandWriter.Send(command);
                Task<string> read = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(timeoutMs)) != read) throw new TimeoutException("采集进程响应超时，不能保证设备已释放。可尝试强制恢复本工具采集进程。");
                string line = await read; if (line == null) throw new IOException("采集进程已退出，设备状态待重新确认。");
                return Wire.Decode<Reply>(line);
            }
            catch { Broken = true; throw; }
        }
        internal void Cancel() { try { commandWriter.Cancel(); } catch { Broken = true; } }
        internal async Task Close(int commandTimeout = 5000, int exitTimeout = 2000)
        {
            Reply reply = await Send(new Command { Op = "EXIT" }, commandTimeout);
            if (!reply.Ok) throw new IOException(reply.Message);
            if (!await Task.Run(() => process.WaitForExit(exitTimeout))) { Broken = true; throw new TimeoutException("采集进程尚未退出。"); }
            process.Dispose();
        }
        internal async Task ForceStop()
        {
            // This object owns exactly this process. Never terminate LabVIEW or other applications.
            if (!process.HasExited) process.Kill();
            if (!await Task.Run(() => process.WaitForExit(3000))) throw new TimeoutException("本工具采集进程仍未退出；不要创建新连接。");
            process.Dispose(); Broken = true;
        }
        internal async Task<bool> RetireUnresponsive()
        {
            // Only this exact Process object is owned. Never search/kill by name.
            // Cancellation and EXIT are queued; no second thread calls the SDK.
            if(!process.HasExited)
            {
                try { commandWriter.Retire(); } catch { }
                if(await Task.Run(()=>process.WaitForExit(1000))) { process.Dispose(); Broken=true; return false; }
            }
            else { process.Dispose(); Broken=true; return false; }
            await ForceStop(); return true;
        }
    }
}
