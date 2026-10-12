using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.Acquisition
{
    // Shared by the desktop and SDK-owning calibration worker. This is a
    // scheduling/ownership boundary, never an electrical safety permission.
    internal sealed class PicoCaptureCadence
    {
        internal const int MinimumGapMs = 2000;
        internal const int CalibrationSegmentGapMs = 500;
        internal const double MaximumDurationMs = 60;
        readonly string root;
        readonly Func<long> now;
        readonly long frequency;
        readonly Func<int,CancellationToken,Task> delay;
        static readonly string ProcessClockDomain=Guid.NewGuid().ToString("N");
        readonly string clockDomain;
        internal static readonly PicoCaptureCadence Shared = new PicoCaptureCadence(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HVXRuntime", "PicoCaptureCadence"),
            Stopwatch.GetTimestamp, Stopwatch.Frequency, (ms, token) => Task.Delay(ms, token));

        internal PicoCaptureCadence(string directory, Func<long> timestamp, long ticksPerSecond, Func<int,CancellationToken,Task> wait,string domain=null)
        {
            if (String.IsNullOrWhiteSpace(directory) || timestamp == null || ticksPerSecond <= 0 || wait == null) throw new ArgumentException("Invalid cadence clock.");
            root=Path.GetFullPath(directory); now=timestamp; frequency=ticksPerSecond; delay=wait;clockDomain=domain??ProcessClockDomain;
        }
        internal static int CooldownMilliseconds(double durationMs)
        {
            if(Double.IsNaN(durationMs)||Double.IsInfinity(durationMs)||durationMs<0)throw new ArgumentException("Invalid capture duration.");
            return checked(MinimumGapMs+(int)Math.Ceiling(Math.Max(0,durationMs-60)/10)*500);
        }
        internal static void RequireDuration(double milliseconds)
        {
            if (Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds) || milliseconds <= 0 || milliseconds > MaximumDurationMs)
                throw new ArgumentException("单段采集时长须大于 0 且不超过 60 ms，请修改采集设置。");
        }
        internal static void RequireSamples(int samples, double actualRateHz)
        {
            if (samples < 2 || Double.IsNaN(actualRateHz) || Double.IsInfinity(actualRateHz) || actualRateHz <= 0)
                throw new ArgumentException("采样点数或实际采样率无效。");
            RequireDuration(samples / actualRateHz * 1000.0);
        }
        internal static double PressureDuration(double requestedMs,double requestedRateHz)
        {
            RequireDuration(requestedMs);
            if(Double.IsNaN(requestedRateHz)||Double.IsInfinity(requestedRateHz)||requestedRateHz<=0)throw new ArgumentException("采样率无效。");
            foreach(double rate in new[]{80000.0,requestedRateHz})
            {
                double requestedSamples=rate*requestedMs/1000.0;
                if(Math.Abs(requestedSamples-Math.Round(requestedSamples,MidpointRounding.AwayFromZero))>Math.Max(1e-7,Math.Abs(requestedSamples)*1e-12))
                    throw new ArgumentException("采集时长须与两路实际采样点对齐，请修改采集设置。");
            }
            // Frozen worker rejects rate error above 1e-9 relative. Preserve the
            // sample rate; select the nearest common ART/Pico integer boundary.
            // This is the actual requested duration, not post-capture cropping.
            for(long art=(long)Math.Floor(requestedMs*80+1e-8);art>=1;art--)
            {
                double duration=art/80.0, raw=duration*requestedRateHz/1000.0;
                double count=Math.Round(raw,MidpointRounding.AwayFromZero);
                if(Math.Abs(raw-count)>Math.Max(1e-7,Math.Abs(raw)*1e-12))continue;
                if(count<2)continue; // Existing worker memory limits still reject; never shorten to hide them.
                if(count/(requestedRateHz*(1-1e-9))*1000.0<=MaximumDurationMs)return duration;
            }
            throw new ArgumentException("两路采样率没有可用的 60 ms 内共同采样边界，请修改采集设置。");
        }
        internal string StatePath(string serial)
        {
            string normalized=(serial??"").Trim().ToUpperInvariant();
            if (normalized.Length==0) throw new ArgumentException("采集设备身份尚未确认。");
            using (var hash=SHA256.Create()) return Path.Combine(root,BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-","")+".state");
        }
        static void RejectLinks(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("采集间隔记录不可使用链接文件。");
            for(var d=new DirectoryInfo(Path.GetDirectoryName(path));d!=null;d=d.Parent)
                if(d.Exists && (d.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("采集间隔记录不可使用链接目录。");
        }
        async Task<FileStream> LockAsync(string serial,CancellationToken token,Func<bool> cancelled)
        {
            string path=StatePath(serial); RejectLinks(path);Directory.CreateDirectory(root);RejectLinks(path);
            for(;;)
            {
                CheckCancel(token,cancelled);
                try { return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,4096,FileOptions.WriteThrough); }
                catch(IOException error)
                {
                    int code=error.HResult & 0xffff;
                    if(code!=32 && code!=33) throw;
                    await delay(25,token).ConfigureAwait(false);
                }
            }
        }
        static void CheckCancel(CancellationToken token,Func<bool> cancelled)
        { token.ThrowIfCancellationRequested();if(cancelled!=null&&cancelled())throw new OperationCanceledException(); }
        internal async Task<Lease> EnterAsync(string serial,CancellationToken token,Action<string> trace=null,Func<bool> cancelled=null,Action<int> remainingMilliseconds=null)
        {
            var stream=await LockAsync(serial,token,cancelled).ConfigureAwait(false);
            try
            {
                var state=Read(stream);
                if(state!=null && state[0]!="RELEASED" && state[0]!="RELEASED_PULSE" && state[0]!="RELEASED_CALIBRATION")
                    throw new InvalidOperationException("上次采集的停止或资源释放尚未确认，不能继续采集。请先完成设备释放并重新连接。");
                long elapsed=MinimumGapMs;
                if(state!=null)
                {
                    long ended=Int64.Parse(state[1],CultureInfo.InvariantCulture), recordedFrequency=Int64.Parse(state[2],CultureInfo.InvariantCulture);
                    int required=Int32.Parse(state[4],CultureInfo.InvariantCulture);
                    // Unknown/restarted clock: wait a fresh full interval, not zero.
                    long start=now();
                    // Cross-process/restart clocks are conservatively treated
                    // as unknown even on the same boot. A fresh full wait avoids
                    // relying on UTC, boot IDs, or persisted QPC comparability.
                    bool same=recordedFrequency==frequency && state[3]==clockDomain && start>=ended;
                    for(;;)
                    {
                        CheckCancel(token,cancelled);
                        long current=now();
                        elapsed=current<(same?ended:start)?0:(long)Math.Floor((current-(same?ended:start))*1000.0/frequency);
                        if(elapsed>=required)break;
                        if(remainingMilliseconds!=null)remainingMilliseconds((int)(required-elapsed));
                        Emit(trace,"Cadence.wait remainingMs="+(required-elapsed).ToString(CultureInfo.InvariantCulture));
                        await delay((int)Math.Min(50,required-elapsed),token).ConfigureAwait(false);
                    }
                }
                CheckCancel(token,cancelled);
                if(remainingMilliseconds!=null)remainingMilliseconds(0);
                Emit(trace,"Cadence.ready gapMs="+elapsed.ToString(CultureInfo.InvariantCulture));
                return new Lease(this,stream,trace);
            }
            catch
            {
                stream.Dispose();
                try{if(remainingMilliseconds!=null)remainingMilliseconds(0);}catch{ /* Preserve the capture/cancellation error. */ }
                throw;
            }
        }
        // Only call after the caller has proved Stop/Close plus buffer release.
        // This never opens, stops, closes or reconnects any device itself.
        internal async Task ConfirmReleasedAsync(string serial,Action<string> trace=null)
        {
            using(var stream=await LockAsync(serial,CancellationToken.None,null).ConfigureAwait(false))
                Write(stream,"RELEASED",trace,MinimumGapMs);
        }
        static string[] Read(FileStream stream)
        {
            if(stream.Length==0)return null;
            if(stream.Length>512)throw new InvalidDataException("采集间隔记录损坏，未开始采集。");
            stream.Position=0;byte[] bytes=new byte[(int)stream.Length];int count=stream.Read(bytes,0,bytes.Length);
            string[] fields=Encoding.UTF8.GetString(bytes,0,count).Trim().Split('|');long t,f;int gap;
            if(fields.Length!=6 || (fields[0]!="RELEASED"&&fields[0]!="RELEASED_PULSE"&&fields[0]!="RELEASED_CALIBRATION"&&fields[0]!="ACTIVE"&&fields[0]!="BLOCKED") ||
                !Int64.TryParse(fields[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out t) || t<0 ||
                !Int64.TryParse(fields[2],NumberStyles.Integer,CultureInfo.InvariantCulture,out f) || f<=0 || fields[3].Length==0 ||
                !Int32.TryParse(fields[4],NumberStyles.Integer,CultureInfo.InvariantCulture,out gap) ||
                (fields[0]=="RELEASED_PULSE" ? gap!=0 : fields[0]=="RELEASED_CALIBRATION" ? gap!=CalibrationSegmentGapMs : gap<MinimumGapMs))
                throw new InvalidDataException("采集间隔记录损坏，未开始采集。");
            return fields;
        }
        void Write(FileStream stream,string state,Action<string> trace,int cooldown=MinimumGapMs)
        {
            long ticks=now();string text=state+"|"+ticks.ToString(CultureInfo.InvariantCulture)+"|"+frequency.ToString(CultureInfo.InvariantCulture)+"|"+clockDomain+"|"+cooldown.ToString(CultureInfo.InvariantCulture)+"|"+DateTime.UtcNow.ToString("O");
            byte[] bytes=Encoding.UTF8.GetBytes(text);stream.Position=0;stream.Write(bytes,0,bytes.Length);stream.SetLength(bytes.Length);stream.Flush(true);
            Emit(trace,"Cadence."+state+" timestamp="+ticks.ToString(CultureInfo.InvariantCulture)+"; cooldownMs="+cooldown.ToString(CultureInfo.InvariantCulture));
        }
        static void Emit(Action<string> trace,string message){if(trace!=null)trace(DateTime.UtcNow.ToString("O")+" "+message);}
        internal sealed class Lease:IDisposable
        {
            readonly PicoCaptureCadence owner;FileStream stream;readonly Action<string> trace;bool started,released;
            internal Lease(PicoCaptureCadence owner,FileStream stream,Action<string> trace){this.owner=owner;this.stream=stream;this.trace=trace;}
            internal bool IsDisposed { get { return stream==null; } }
            internal void BeginCapture(){if(stream==null||started)throw new InvalidOperationException("Invalid capture lease.");owner.Write(stream,"ACTIVE",trace);started=true;}
            internal void Released(double actualDurationMs=60, bool calibrationPulse=false, bool calibrationSegment=false)
            {
                if(stream==null)throw new ObjectDisposedException("Capture lease");
                if(calibrationPulse&&calibrationSegment)throw new ArgumentException("Capture cadence purpose is ambiguous.");
                if(calibrationPulse||calibrationSegment)RequireDuration(actualDurationMs);
                owner.Write(stream,calibrationPulse?"RELEASED_PULSE":calibrationSegment?"RELEASED_CALIBRATION":"RELEASED",trace,
                    calibrationPulse?0:calibrationSegment?CalibrationSegmentGapMs:CooldownMilliseconds(actualDurationMs));released=true;
            }
            public void Dispose()
            {
                if(stream==null)return;
                try{if(started&&!released)owner.Write(stream,"BLOCKED",trace);}
                finally{stream.Dispose();stream=null;}
            }
        }
    }
}
