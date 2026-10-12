using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    internal static class ReviewERuntime
    {
        internal const int MaximumRequestBytes=16*1024*1024,MaximumOutputChars=32*1024*1024,MaximumSamples=16000000;
        const int MaximumFrameBytes=4*MaximumOutputChars+4096,MaximumEnvelopeChars=MaximumOutputChars+4096;
        const int IdleMilliseconds=60000;
        static readonly SemaphoreSlim Serial=new SemaphoreSlim(1,1);
        static readonly object Ownership=new object();
        static RetainedWorker worker,pendingRelease;
        static bool busy,shuttingDown;
        static readonly Timer IdleTimer=new Timer(ReleaseExpiredWorker,null,15000,15000);
        static ReviewERuntime()
        {
            AppDomain.CurrentDomain.ProcessExit+=(sender,args)=>Shutdown();
            AppDomain.CurrentDomain.DomainUnload+=(sender,args)=>Shutdown();
        }
        static Dictionary<string,string> NativePins()
        {
            using(var input=Assembly.GetEntryAssembly().GetManifestResourceStream("HVX.Research.Pins")){
                if(input==null)throw new InvalidDataException("缺少定位复核组件校验清单。");
                using(var reader=new StreamReader(input))return Serializer().Deserialize<Dictionary<string,string>>(reader.ReadToEnd());
            }
        }
        internal static string Hash(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
        static void RequireUnlinked(string path){
            for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked comparison files are not permitted.");
        }
        internal static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumOutputChars,RecursionLimit=128};}
        static string ReadBounded(StreamReader reader,int maximum,CancellationToken cancel){
            var result=new StringBuilder();var buffer=new char[4096];int count;
            while((count=reader.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();if((long)result.Length+count>maximum)throw new InvalidDataException("Comparison output exceeded its limit.");result.Append(buffer,0,count);}
            return result.ToString();
        }
        // A single algorithm-only owner; no hardware task or result cache lives
        // here. Verified files remain read-shared (not write/delete-shared) for
        // exactly this process lifetime, so re-use never skips verification of
        // mutable bytes. Failed cleanup blocks replacement rather than losing
        // ownership and starting a second process.
        static void RetirePending()
        {
            RetainedWorker old;lock(Ownership)old=pendingRelease;
            if(old==null)return;
            if(!old.StopAndRelease())throw new IOException("Comparison worker has not stopped; no replacement was started.");
            lock(Ownership)if(Object.ReferenceEquals(pendingRelease,old))pendingRelease=null;
        }
        static void QueueRelease(RetainedWorker old,bool ownsSerial=false)
        {
            old.RequestStop();
            Task.Run(()=>{if(!ownsSerial)Serial.Wait();try{RetirePending();}catch(Exception error){Debug.WriteLine(error);ReviewAnalysisDebugTrace.Event("worker-cleanup-failed",error.ToString());}finally{Serial.Release();}});
        }
        internal static bool ReleaseIdleWorker()
        {
            return ReleaseIdleWorker(false);
        }
        static bool ReleaseIdleWorker(bool expiredOnly)
        {
            if(!Serial.Wait(0))return false;
            RetainedWorker old;
            lock(Ownership){
                if(busy||worker==null||(expiredOnly&&worker.IdleMilliseconds<IdleMilliseconds)){Serial.Release();return false;}
                old=worker;worker=null;pendingRelease=old;
            }
            // Transfer the gate to asynchronous cleanup, preventing a new owner
            // from being created until the prior process has confirmed exit.
            QueueRelease(old,true);return true;
        }
        static void ReleaseExpiredWorker(object state){ReleaseIdleWorker(true);}
        internal static void Shutdown()
        {
            RetainedWorker old;bool active;
            lock(Ownership){shuttingDown=true;active=busy;old=worker??pendingRelease;if(!active&&worker!=null){pendingRelease=worker;worker=null;}}
            IdleTimer.Change(Timeout.Infinite,Timeout.Infinite);
            if(old!=null){old.RequestStop();if(!active)QueueRelease(old);}
        }
        internal static async Task<string> RunAsync(string modules,string python,Dictionary<string,object> requested,CancellationToken cancel,Action<string> progress=null,int timeoutSeconds=180)
        {
            using(var trace=ReviewAnalysisDebugTrace.Begin("E-runtime")){
                if(timeoutSeconds<1||timeoutSeconds>600)throw new ArgumentOutOfRangeException("timeoutSeconds");
                var records=(object[])requested["records"];
                if(records.Length!=1)throw new InvalidDataException("每次复核必须对应一条完整记录。");
                string algorithm=Convert.ToString(requested["algorithm"]);
                if(algorithm!="E"&&algorithm!="E+")throw new ArgumentException("Unknown comparison algorithm.");
                var record=new Dictionary<string,object>((Dictionary<string,object>)records[0]);
                var samples=(double[])record["samples"];
                if(samples.Length<32||samples.Length>MaximumSamples)throw new InvalidDataException("样本数量超出复核范围。");
                string requestId=Guid.NewGuid().ToString("N"),recordId=Convert.ToString(record["record_id"]);
                record.Remove("samples");record["sample_count"]=samples.Length;
                var header=new Dictionary<string,object>(requested);header["records"]=new object[]{record};header["transport"]="float64-le-v1";header["request_id"]=requestId;
                byte[] requestBytes=Encoding.UTF8.GetBytes(Serializer().Serialize(header));
                if(requestBytes.Length>MaximumRequestBytes)throw new InvalidDataException("复核参数超出容量限制。");
                string root=Path.Combine(Path.GetFullPath(modules),"r71");
                trace.Point("request-prepared","samples="+samples.Length+"; headerBytes="+requestBytes.Length+"; requestId="+requestId);
                await Serial.WaitAsync(cancel).ConfigureAwait(false);
                RetainedWorker current=null;bool completed=false;
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                    deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    try{
                        cancel.ThrowIfCancellationRequested();RetirePending();
                        lock(Ownership){if(shuttingDown)throw new ObjectDisposedException("E-runtime");busy=true;current=worker;}
                        if(current!=null&&(!String.Equals(current.Root,root,StringComparison.OrdinalIgnoreCase)||!current.IsAlive)){
                            lock(Ownership){worker=null;pendingRelease=current;}
                            RetirePending();current=null;
                        }
                        if(current==null){
                            current=await Task.Run(()=>RetainedWorker.Create(root,deadline.Token)).ConfigureAwait(false);
                            lock(Ownership){worker=current;if(shuttingDown)current.RequestStop();}
                            trace.Point("runtime-integrity-verified-and-worker-started","pinnedFiles="+current.PinCount);
                        }else trace.Point("verified-worker-reused","pinnedFilesHeld="+current.PinCount);
                        // Freeze precisely this record, not a cached prior waveform.
                        var immutableSamples=(double[])samples.Clone();
                        string output=await current.Exchange(requestBytes,immutableSamples,requestId,recordId,algorithm,deadline.Token,progress).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        trace.Point("runtime-envelope-decoded","outputCharacters="+output.Length);
                        completed=true;trace.End("success");return output;
                    }catch(OperationCanceledException){
                        if(!cancel.IsCancellationRequested)throw new TimeoutException("Comparison timed out; no analysis results were changed.");
                        throw;
                    }catch(Exception error){trace.Failure(error);throw;
                    }finally{
                        bool release;
                        lock(Ownership){busy=false;release=!completed||shuttingDown;if(release&&current!=null){if(Object.ReferenceEquals(worker,current))worker=null;pendingRelease=current;}else if(current!=null)current.MarkIdle();}
                        if(release)try{RetirePending();}catch(Exception error){Debug.WriteLine(error);ReviewAnalysisDebugTrace.Event("worker-cleanup-failed",error.ToString());}
                        Serial.Release();
                    }
                }
            }
        }

        sealed class RetainedWorker
        {
            readonly List<FileStream> locks;readonly Process process;
            readonly object stopGate=new object();readonly Stopwatch idle=Stopwatch.StartNew();
            Task<string> errors;bool started,released;
            internal readonly string Root;
            RetainedWorker(string root,List<FileStream> held,Process owned){Root=root;locks=held;process=owned;}
            internal int PinCount{get{return locks.Count;}}
            internal long IdleMilliseconds{get{return idle.ElapsedMilliseconds;}}
            internal void MarkIdle(){idle.Restart();}
            internal bool IsAlive{get{lock(stopGate)return started&&!released&&!process.HasExited;}}
            internal static RetainedWorker Create(string root,CancellationToken cancel)
            {
                var held=new List<FileStream>();RetainedWorker owned=null;
                try{
                    var pins=NativePins();if(pins==null||pins.Count<1||pins.Count>4096)throw new InvalidDataException("Invalid comparison component manifest.");
                    string python=Path.Combine(root,"python.exe");RequireUnlinked(python);
                    foreach(var entry in pins){
                        cancel.ThrowIfCancellationRequested();
                        string path=Path.GetFullPath(Path.Combine(root,entry.Key.Replace('/',Path.DirectorySeparatorChar)));
                        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid component path.");
                        RequireUnlinked(path);var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);held.Add(input);
                        using(var sha=SHA256.Create())if(!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),entry.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Comparison component changed; repair the software.");
                    }
                    var start=new ProcessStartInfo{FileName=python,Arguments="-I -X utf8 -B -c \"import q73;q73.serve()\"",WorkingDirectory=root,
                        UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                        StandardOutputEncoding=new UTF8Encoding(false,true),StandardErrorEncoding=new UTF8Encoding(false,true)};
                    // Never inherit credentials or arbitrary Python search paths.
                    start.EnvironmentVariables.Clear();
                    foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","LOCALAPPDATA","USERPROFILE"}){string value=Environment.GetEnvironmentVariable(key);if(value!=null)start.EnvironmentVariables[key]=value;}
                    start.EnvironmentVariables["PATH"]=Path.GetDirectoryName(python)+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
                    owned=new RetainedWorker(root,held,new Process{StartInfo=start});
                    cancel.ThrowIfCancellationRequested();owned.process.Start();owned.started=true;
                    owned.errors=Task.Run(()=>ReadBounded(owned.process.StandardError,128*1024,CancellationToken.None));
                    owned.errors.ContinueWith(task=>{var observed=task.Exception;owned.RequestStop();},TaskContinuationOptions.OnlyOnFaulted);
                    cancel.ThrowIfCancellationRequested();return owned;
                }catch{
                    if(owned==null)foreach(var input in held)input.Dispose();
                    else if(!owned.StopAndRelease())lock(Ownership)pendingRelease=owned;
                    throw;
                }
            }
            internal void RequestStop()
            {
                lock(stopGate){if(released||!started)return;try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){ }catch(System.ComponentModel.Win32Exception error){Debug.WriteLine(error);}}
            }
            internal bool StopAndRelease()
            {
                RequestStop();lock(stopGate){
                    if(released)return true;
                    if(started&&!process.WaitForExit(5000))return false;
                    // Only a confirmed process exit permits releasing pinned bytes.
                    try{process.Dispose();}finally{foreach(var input in locks)input.Dispose();released=true;}
                    return true;
                }
            }
            static byte[] Exact(Stream input,int count)
            {
                var data=new byte[count];int offset=0;
                while(offset<count){int read=input.Read(data,offset,count-offset);if(read==0)throw new EndOfStreamException("Incomplete comparison response; no partial result used.");offset+=read;}
                return data;
            }
            string ReadFrame()
            {
                var size=Exact(process.StandardOutput.BaseStream,4);if(!BitConverter.IsLittleEndian)Array.Reverse(size);
                uint count=BitConverter.ToUInt32(size,0);
                if(count<2||count>MaximumFrameBytes)throw new InvalidDataException("Comparison response exceeded its byte limit.");
                return new UTF8Encoding(false,true).GetString(Exact(process.StandardOutput.BaseStream,(int)count));
            }
            string WriteFrame(byte[] header,double[] samples,CancellationToken cancel)
            {
                var output=process.StandardInput.BaseStream;var size=BitConverter.GetBytes(header.Length);if(!BitConverter.IsLittleEndian)Array.Reverse(size);
                output.Write(size,0,size.Length);output.Write(header,0,header.Length);
                using(var sha=SHA256.Create()){
                    var buffer=new byte[65536];
                    for(int offset=0;offset<samples.Length;){
                        cancel.ThrowIfCancellationRequested();int count=Math.Min(buffer.Length/8,samples.Length-offset),bytes=count*8;
                        Buffer.BlockCopy(samples,offset*8,buffer,0,bytes);
                        if(!BitConverter.IsLittleEndian)for(int i=0;i<count;i++)Array.Reverse(buffer,i*8,8);
                        sha.TransformBlock(buffer,0,bytes,buffer,0);output.Write(buffer,0,bytes);offset+=count;
                    }
                    sha.TransformFinalBlock(new byte[0],0,0);output.Flush();return BitConverter.ToString(sha.Hash).Replace("-","");
                }
            }
            internal async Task<string> Exchange(byte[] header,double[] samples,string requestId,string recordId,string algorithm,CancellationToken cancel,Action<string> progress)
            {
                using(cancel.Register(RequestStop)){
                    Task<string> response=Task.Run(()=>ReadFrame()),input=Task.Run(()=>WriteFrame(header,samples,cancel));
                    Task both=Task.WhenAll(response,input);var watch=Stopwatch.StartNew();
                    try{
                        while(!both.IsCompleted){
                            cancel.ThrowIfCancellationRequested();
                            // A failed reader must not wait for a blocked writer
                            // (and vice versa) until the entire request deadline.
                            if(response.IsFaulted)await response.ConfigureAwait(false);
                            if(input.IsFaulted)await input.ConfigureAwait(false);
                            if(errors.IsFaulted)throw new IOException("Comparison diagnostic stream exceeded its limit.",errors.Exception);
                            if(progress!=null)progress(watch.Elapsed.TotalSeconds.ToString("F0")+" s");
                            await Task.WhenAny(both,Task.Delay(25,cancel)).ConfigureAwait(false);
                        }
                        await both.ConfigureAwait(false);cancel.ThrowIfCancellationRequested();
                        var envelope=new JavaScriptSerializer{MaxJsonLength=MaximumEnvelopeChars,RecursionLimit=128}.DeserializeObject(response.Result) as Dictionary<string,object>;
                        if(envelope==null||!Object.Equals(envelope["schema"],"HVX.LocationE.Worker/1")||!Object.Equals(envelope["request_id"],requestId))throw new InvalidDataException("Unexpected comparison response identity.");
                        if(!Object.Equals(envelope["status"],"ok"))throw new InvalidDataException("定位复核未完成；"+(envelope.ContainsKey("message")?Convert.ToString(envelope["message"]):"Invalid worker result."));
                        var result=envelope["result"] as Dictionary<string,object>;
                        if(result==null||!Object.Equals(result["status"],"ok")||!Object.Equals(result["algorithm"],algorithm)||!Object.Equals(result["version"],ReviewEAnalysis.VersionForAlgorithm(algorithm))||!Object.Equals(result["record_id"],recordId))throw new InvalidDataException("Unexpected comparison record or version.");
                        if(!String.Equals(Convert.ToString(result["waveform_sha256"]),input.Result,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected comparison waveform identity.");
                        object identity;
                        if(algorithm=="E+"&&(!result.TryGetValue("candidate_source_identity",out identity)||!Object.Equals(identity,ReviewEAnalysis.EPlusSourceIdentity)))throw new InvalidDataException("Unexpected E+ source identity.");
                        cancel.ThrowIfCancellationRequested();
                        string serialized=Serializer().Serialize(result);
                        cancel.ThrowIfCancellationRequested();return serialized;
                    }catch{
                        RequestStop();await Task.WhenAny(both,Task.Delay(5000)).ConfigureAwait(false);
                        if(both.IsFaulted){var observed=both.Exception;}else if(!both.IsCompleted)both.ContinueWith(task=>{var observed=task.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                        cancel.ThrowIfCancellationRequested();
                        throw;
                    }
                }
            }
        }

    }
}
