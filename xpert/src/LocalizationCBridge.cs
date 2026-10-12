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

namespace Xiongxin.OWTS.Desktop
{
    internal static class LocalizationCBridge
    {
        internal const int MaximumRequestBytes=16*1024*1024,MaximumOutputChars=32*1024*1024,MaximumSamples=16000000;
        internal const string Candidate="AB_3.0.22_CD_0.3.0_0.1.1";
        static Dictionary<string,string> NativePins()
        {
            using(var input=typeof(LocalizationCBridge).Assembly.GetManifestResourceStream("HVX.Research.Pins")){
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
        internal static Dictionary<string,object> BuildRequest(string modules,string loc,double length,double velocity,bool confirmedReference,string phase,CancellationToken cancel,out string sourceHash,string algorithm="C")
        {
            if(!File.Exists(loc)||new FileInfo(loc).Length>512L*1024*1024)throw new InvalidDataException("定位记录不存在或超过 512 MiB；未截断原记录。");
            RequireUnlinked(loc);cancel.ThrowIfCancellationRequested();
            // Keep the input write-locked while the existing validated reader
            // restores stored charge /100000; no guessed header or pC scaling.
            using(var guard=new FileStream(loc,FileMode.Open,FileAccess.Read,FileShare.Read)){
                sourceHash=Hash(loc);
                var assembly=Assembly.LoadFrom(Path.Combine(modules,"DataAnalysis","HVX.PdReview.V1.0.dll"));
                var reader=assembly.GetType("HVX.PdReview.ReviewLocationFileReader",true);
                object data=reader.GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{loc,Path.GetDirectoryName(Path.GetFullPath(loc))});
                var samples=(double[])data.GetType().GetField("ChargeSamples",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(data);
                var rate=(double)data.GetType().GetField("SampleRateHz",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(data);
                var voltage=(double)data.GetType().GetField("VoltageKv",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(data);
                cancel.ThrowIfCancellationRequested();
                if(samples.Length<32||samples.Length>MaximumSamples)throw new InvalidDataException("定位复核支持 32～1600 万个样本；未截断原记录。");
                if(length<=0||Double.IsNaN(length)||Double.IsInfinity(length)||velocity<50||velocity>299.792458||Double.IsNaN(velocity))throw new ArgumentException("Confirm cable length and full propagation speed.");
                if(algorithm!="C"&&algorithm!="D")throw new ArgumentException("请选择 C 或 D。");
                var request=new Dictionary<string,object>{
                    {"algorithm",algorithm},{"config",new Dictionary<string,object>{{"sample_rate_hz",rate},{"cable_length_m",length},{"velocity_m_per_us",velocity},
                        {"reference_plane",confirmedReference?"cable_input":"unconfirmed"},{"settings",new Dictionary<string,object>{{"maximum_pulses",2048},{"maximum_pairs",100000}}}}},
                    {"records",new object[]{new Dictionary<string,object>{{"record_id",Path.GetFileName(loc)},{"independence_key",sourceHash},{"phase",phase=="ALL"?"unknown":phase},{"voltage_kv",Double.IsNaN(voltage)?null:(object)voltage},{"samples",samples}}}}};
                if(algorithm=="D"){
                    string project=Path.GetDirectoryName(Path.GetFullPath(loc));
                    while(project!=null&&(!File.Exists(Path.Combine(project,"ITEM.in"))||!File.Exists(Path.Combine(project,"CABLE.in"))))project=Path.GetDirectoryName(project);
                    if(project==null)throw new InvalidDataException("D 需要完整项目及适用校准，不能仅凭孤立 LOC 伪造 A/B 证据。");
                    cancel.ThrowIfCancellationRequested();
                    var api=assembly.GetType("HVX.PdReview.LocalizationComparisonApi",true);
                    string evidence=(string)api.GetMethod("BuildEvidenceJson").Invoke(null,new object[]{project,loc,phase,length,velocity});
                    request["ab_evidence"]=Serializer().DeserializeObject(evidence);
                    cancel.ThrowIfCancellationRequested();
                    if(Hash(loc)!=sourceHash)throw new InvalidDataException("对照期间原记录改变，结果未提交。");
                }
                return request;
            }
        }
        static string ReadBounded(StreamReader reader,int maximum,CancellationToken cancel){
            var result=new StringBuilder();var buffer=new char[4096];int count;
            while((count=reader.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();if((long)result.Length+count>maximum)throw new InvalidDataException("Comparison output exceeded its limit.");result.Append(buffer,0,count);}
            return result.ToString();
        }
        internal static async Task<string> RunAsync(string modules,string python,Dictionary<string,object> requested,CancellationToken cancel,Action<string> progress=null,int timeoutSeconds=180)
        {
            if(timeoutSeconds<1||timeoutSeconds>600)throw new ArgumentOutOfRangeException("timeoutSeconds");
            var records=(object[])requested["records"];
            if(records.Length!=1)throw new InvalidDataException("每次复核必须对应一条完整记录。");
            var record=new Dictionary<string,object>((Dictionary<string,object>)records[0]);
            var samples=(double[])record["samples"];
            if(samples.Length<32||samples.Length>MaximumSamples)throw new InvalidDataException("样本数量超出复核范围。");
            record.Remove("samples");record["sample_count"]=samples.Length;
            var header=new Dictionary<string,object>(requested);header["records"]=new object[]{record};header["transport"]="float64-le-v1";
            byte[] requestBytes=Encoding.UTF8.GetBytes(Serializer().Serialize(header));
            if(requestBytes.Length>MaximumRequestBytes)throw new InvalidDataException("复核参数超出容量限制。");
            string root=Path.Combine(Path.GetFullPath(modules),"r71");
            python=Path.Combine(root,"python.exe");RequireUnlinked(python);
            string algorithm=Convert.ToString(requested["algorithm"]);
            if(algorithm!="C"&&algorithm!="D")throw new ArgumentException("Unknown comparison algorithm.");
            // Hold each pinned module against modification while Python imports.
            var locks=new List<FileStream>();
            try {
                foreach(var entry in NativePins()){string path=Path.GetFullPath(Path.Combine(root,entry.Key.Replace('/',Path.DirectorySeparatorChar)));
                    if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid component path.");RequireUnlinked(path);
                    locks.Add(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read));
                    if(Hash(path)!=entry.Value)throw new InvalidDataException("Comparison component changed; repair the software.");}
                var start=new ProcessStartInfo{FileName=python,Arguments="-I -X utf8 -B -c \"import q73;q73.main()\"",WorkingDirectory=root,
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                    StandardOutputEncoding=new UTF8Encoding(false,true),StandardErrorEncoding=new UTF8Encoding(false,true)};
                // Do not propagate credentials or Python search-path overrides.
                start.EnvironmentVariables.Clear();
                foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","LOCALAPPDATA","USERPROFILE"}){string value=Environment.GetEnvironmentVariable(key);if(value!=null)start.EnvironmentVariables[key]=value;}
                start.EnvironmentVariables["PATH"]=Path.GetDirectoryName(python)+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
                using(var process=new Process{StartInfo=start}){
                    cancel.ThrowIfCancellationRequested();process.Start();
                    Task<string> stdout=Task.Run(()=>ReadBounded(process.StandardOutput,MaximumOutputChars,cancel));
                    Task<string> stderr=Task.Run(()=>ReadBounded(process.StandardError,128*1024,cancel));
                    Task input=Task.Run(()=>{
                        // Binary float64 preserves every engineering sample and
                        // avoids millions of decimal strings on long Pico captures.
                        var stream=process.StandardInput.BaseStream;
                        byte[] size=BitConverter.GetBytes(requestBytes.Length);
                        if(!BitConverter.IsLittleEndian)Array.Reverse(size);
                        stream.Write(size,0,size.Length);stream.Write(requestBytes,0,requestBytes.Length);
                        var buffer=new byte[65536];
                        for(int offset=0;offset<samples.Length;){
                            cancel.ThrowIfCancellationRequested();int count=Math.Min(buffer.Length/8,samples.Length-offset);
                            Buffer.BlockCopy(samples,offset*8,buffer,0,count*8);
                            if(!BitConverter.IsLittleEndian)for(int i=0;i<count;i++)Array.Reverse(buffer,i*8,8);
                            stream.Write(buffer,0,count*8);offset+=count;
                        }
                        process.StandardInput.Close();
                    });
                    var watch=Stopwatch.StartNew();
                    try{
                        while(!process.HasExited){
                            cancel.ThrowIfCancellationRequested();
                            if(watch.Elapsed.TotalSeconds>timeoutSeconds)throw new TimeoutException("Comparison timed out; no analysis results were changed.");
                            if(stdout.IsFaulted||stderr.IsFaulted||input.IsFaulted)throw new IOException("Comparison input/output failed.");
                            if(progress!=null)progress(watch.Elapsed.TotalSeconds.ToString("F0")+" s");
                            await Task.Delay(150,cancel).ConfigureAwait(false);
                        }
                        await input.ConfigureAwait(false);
                        string output=await stdout.ConfigureAwait(false),errors=await stderr.ConfigureAwait(false);
                        var result=Serializer().DeserializeObject(output) as Dictionary<string,object>;
                        if(process.ExitCode!=0||result==null||!result.ContainsKey("status")||!Object.Equals(result["status"],"ok"))
                            throw new InvalidDataException("定位复核未完成；请核对输入记录、相别、适用校准与参数。"+(result!=null&&result.ContainsKey("message")?Convert.ToString(result["message"]):(errors.Length>600?errors.Substring(0,600):errors)));
                        if(!Object.Equals(result["algorithm"],algorithm)||!Object.Equals(result["version"],algorithm=="C"?"C-0.3.0":"D-0.1.1"))throw new InvalidDataException("Unexpected comparison version.");
                        return output;
                    } finally {
                        if(!process.HasExited){process.Kill();process.WaitForExit(5000);}
                        // Observe pipe task exceptions even on timeout/cancel.
                        try{await Task.WhenAll(stdout,stderr,input).ConfigureAwait(false);}catch(Exception){ }
                    }
                }
            } finally {foreach(var input in locks)input.Dispose();}
        }
    }
}
