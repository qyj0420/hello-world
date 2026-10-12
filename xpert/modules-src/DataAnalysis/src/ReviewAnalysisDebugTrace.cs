using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HVX.PdReview
{
    // Explicit diagnostic opt-in only. The marker belongs beside the host EXE,
    // not the shadow-copied module. Never record waveform values or alter results.
    // All writes and binary hashing run off the UI/analysis caller thread.
    internal static class ReviewAnalysisDebugTrace
    {
        const int QueueLimit=512,LineLimit=65536;
        const long SegmentLimit=16L*1024*1024;
        static readonly object gate=new object();
        static readonly Queue<string> pending=new Queue<string>();
        static readonly bool enabled=ReadEnabled();
        static readonly string session=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"-"+Process.GetCurrentProcess().Id+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
        static bool draining;
        static int dropped,identityQueued,segment;
        static long segmentBytes;
        internal static bool Enabled {get{return enabled;}}
        internal static string LogDirectory {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XIONGXIN","HVXUnifiedDesktop","Debug-20261010","analysis");}}
        static bool ReadEnabled()
        {
            try{return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"analysis-debug.enabled"));}
            catch(Exception error){if(IsFatal(error))throw;return false;}
        }
        static bool IsFatal(Exception error)
        {return error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException||error is ThreadAbortException;}
        internal sealed class Scope : IDisposable
        {
            readonly string id,kind;
            readonly Stopwatch timer;
            long previous;
            int finished;
            internal Scope(string operation,string detail,bool active)
            {
                kind=operation;
                if(!active)return;
                id=Guid.NewGuid().ToString("N");
                timer=Stopwatch.StartNew();Emit(id,kind,"begin",0,0,detail);
            }
            internal void Point(string stage,string detail="")
            {
                if(timer==null||Volatile.Read(ref finished)!=0)return;
                long now=timer.ElapsedTicks,last=Interlocked.Exchange(ref previous,now);
                Emit(id,kind,stage,1000d*now/Stopwatch.Frequency,1000d*Math.Max(0,now-last)/Stopwatch.Frequency,detail);
            }
            internal void Failure(Exception error)
            {if(timer!=null)Point("exception",error==null?"<null>":error.ToString());}
            internal void End(string outcome)
            {
                if(timer==null||Interlocked.Exchange(ref finished,1)!=0)return;
                long now=timer.ElapsedTicks,last=Interlocked.Exchange(ref previous,now);
                Emit(id,kind,"end",1000d*now/Stopwatch.Frequency,1000d*Math.Max(0,now-last)/Stopwatch.Frequency,outcome);
            }
            public void Dispose(){End("scope-exited");}
        }
        internal static Scope Begin(string operation,string detail="")
        {
            if(enabled)QueueIdentity();
            return new Scope(operation,detail,enabled);
        }
        internal static void Event(string stage,string detail="")
        {if(enabled){QueueIdentity();Emit("process","diagnostic",stage,0,0,detail);}}
        static string Escape(string value)
        {return (value??"").Replace("\\","\\\\").Replace("\t","\\t").Replace("\r","\\r").Replace("\n","\\n");}
        static void Emit(string id,string operation,string stage,double totalMs,double stageMs,string detail)
        {
            if(!enabled)return;
            try
            {
                string line=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+"\t"+Thread.CurrentThread.ManagedThreadId+"\t"+Escape(id)+"\t"+Escape(operation)+"\t"+Escape(stage)+"\t"+
                    totalMs.ToString("F3",CultureInfo.InvariantCulture)+"\t"+stageMs.ToString("F3",CultureInfo.InvariantCulture)+"\t"+Escape(detail);
                if(line.Length>LineLimit)line=line.Substring(0,LineLimit)+" [diagnostic truncated at 65536 characters]";
                lock(gate)
                {
                    if(pending.Count>=QueueLimit){dropped++;return;}
                    if(dropped>0){line+="\tprevious_dropped_events="+dropped;dropped=0;}
                    pending.Enqueue(line);
                    if(draining)return;
                    draining=true;ThreadPool.QueueUserWorkItem(_=>Drain());
                }
            }
            catch(Exception error){if(IsFatal(error))throw;}
        }
        static void QueueIdentity()
        {
            if(Interlocked.Exchange(ref identityQueued,1)!=0)return;
            ThreadPool.QueueUserWorkItem(_=>{
                try
                {
                    Emit("process","identity","runtime",0,0,"HostBase="+AppDomain.CurrentDomain.BaseDirectory+"; CLR="+Environment.Version+"; bitness="+(IntPtr.Size*8)+"; assemblyVersion="+typeof(ReviewAnalysisDebugTrace).Assembly.GetName().Version);
                    Assembly module=typeof(ReviewAnalysisDebugTrace).Assembly;
                    string path=ReviewAnalysisEngine.OriginalAssemblyPath(module);
                    EmitIdentity("control",path);
                    string algorithm=ReviewAnalysisEngine.ExpectedAlgorithmPath;
                    if(File.Exists(algorithm))EmitIdentity("location-algorithm",algorithm);
                    else Emit("process","identity","location-algorithm",0,0,"Missing="+algorithm);
                }
                catch(Exception error){if(IsFatal(error))throw;Emit("process","identity","exception",0,0,error.ToString());}
                finally{Interlocked.Exchange(ref identityQueued,2);lock(gate){Monitor.PulseAll(gate);}}
            });
        }
        static void EmitIdentity(string role,string path)
        {
            string hash;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
            Emit("process","identity",role,0,0,"Path="+path+"; FileVersion="+FileVersionInfo.GetVersionInfo(path).FileVersion+"; SHA256="+hash);
        }
        static void Drain()
        {
            while(true)
            {
                string line;
                lock(gate){if(pending.Count==0){draining=false;Monitor.PulseAll(gate);return;}line=pending.Dequeue();}
                try
                {
                    // Bound one process to at most three segments (48 MiB).
                    // No existing diagnostics or user data are deleted/overwritten.
                    if(segment>=3)continue;
                    byte[] bytes=new UTF8Encoding(false).GetBytes(line+Environment.NewLine);
                    if(segmentBytes+bytes.Length>SegmentLimit){segment++;segmentBytes=0;}
                    if(segment>=3)continue;
                    Directory.CreateDirectory(LogDirectory);
                    string file=Path.Combine(LogDirectory,"analysis-"+session+"-"+segment+".tsv");
                    using(var output=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.Read)){output.Write(bytes,0,bytes.Length);}
                    segmentBytes+=bytes.Length;
                }
                catch(Exception error){if(IsFatal(error))throw;/* Logging must never fail analysis or block its UI. */}
            }
        }
        // Used only by isolated managed tests; normal UI/analysis never waits.
        internal static bool WaitForIdle(int timeoutMs)
        {
            var watch=Stopwatch.StartNew();lock(gate)
            {
                while(draining||pending.Count>0||Volatile.Read(ref identityQueued)==1){int left=timeoutMs-(int)watch.ElapsedMilliseconds;if(left<=0)return false;Monitor.Wait(gate,left);}
                return true;
            }
        }
    }
}
