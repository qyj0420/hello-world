using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Launch protocol only: never calls the SDK or treats process exit as native
    // cleanup. This proof is emitted solely at the wrapper's pre-native guard.
    public sealed class UnifiedWorkerLaunchRecord
    {
        public string Schema="HVX.Worker.PreNative/1", Nonce="", Directory="", SettingsSHA256="", Reason="";
        public int ParentProcessId, WorkerProcessId;
    }
    internal sealed class FactoryNativeNotStartedException:OperationCanceledException
    {
        public bool NativeNeverEnteredConfirmed { get { return true; } }
        public string LaunchEvidenceCode { get; private set; }
        internal FactoryNativeNotStartedException(string code):base("本轮已在设备调用前取消；未进入原生设备接口。") { LaunchEvidenceCode=code; }
    }
    internal static class UnifiedWorkerLaunchEvidence
    {
        internal const string RequestFile="worker-launch-request.xml", ProofFile="worker-not-started.xml";
        internal const string CancelReason="CancellationRequestedBeforeNative";
        internal static UnifiedWorkerLaunchRecord Create(string directory,string settings,int parent)
        {
            var record=new UnifiedWorkerLaunchRecord {Nonce=Guid.NewGuid().ToString("N"),Directory=Path.GetFullPath(directory),
                SettingsSHA256=Hash(settings),ParentProcessId=parent};
            WriteNew(Path.Combine(record.Directory,RequestFile),record);return record;
        }
        internal static void WriteCanceledBeforeNative(string directory,string settings,int parent,int worker)
        {
            var request=Read(Path.Combine(directory,RequestFile));
            if(!MatchesRequest(request,directory,settings,parent)||worker<=0||worker==parent)
                throw new InvalidDataException("启动取消证明与当前请求不符。");
            request.WorkerProcessId=worker;request.Reason=CancelReason;
            WriteNew(Path.Combine(directory,ProofFile),request);
        }
        internal static bool VerifyCanceledBeforeNative(UnifiedWorkerLaunchRecord request,string settings,int worker,bool exited,int exitCode)
        {
            if(!exited||exitCode!=2||worker<=0||request==null)return false;
            try {
                var proof=Read(Path.Combine(request.Directory,ProofFile));
                return MatchesRequest(proof,request.Directory,settings,request.ParentProcessId)&&
                    proof.Nonce==request.Nonce&&proof.SettingsSHA256==request.SettingsSHA256&&
                    proof.WorkerProcessId==worker&&proof.Reason==CancelReason;
            }catch(IOException){return false;}catch(InvalidOperationException){return false;}catch(XmlException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}
        }
        static bool MatchesRequest(UnifiedWorkerLaunchRecord record,string directory,string settings,int parent)
        {
            Guid nonce;
            return record!=null&&record.Schema=="HVX.Worker.PreNative/1"&&Guid.TryParseExact(record.Nonce,"N",out nonce)&&
                record.ParentProcessId==parent&&parent>0&&String.Equals(record.Directory,Path.GetFullPath(directory),StringComparison.OrdinalIgnoreCase)&&
                String.Equals(Path.GetFullPath(settings),Path.Combine(record.Directory,"factory-settings.xml"),StringComparison.OrdinalIgnoreCase)&&
                record.SettingsSHA256==Hash(settings);
        }
        static string Hash(string path)
        {using(var algorithm=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-","");}
        static UnifiedWorkerLaunchRecord Read(string path)
        {
            using(var reader=XmlReader.Create(path,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16384}))
                return (UnifiedWorkerLaunchRecord)new XmlSerializer(typeof(UnifiedWorkerLaunchRecord)).Deserialize(reader);
        }
        static void WriteNew(string path,UnifiedWorkerLaunchRecord value)
        {
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(UnifiedWorkerLaunchRecord)).Serialize(stream,value);
        }
    }

    // Separate from a round or a native-session release receipt. The child can
    // issue this record only before crossing the resident-server entry boundary.
    // It proves that this launch did not create native resources, not that a
    // previous session released them, nor that hardware is electrically safe.
    public sealed class UnifiedResidentLaunchRecord
    {
        public string Schema="HVX.Resident.PreNative/1", Nonce="", Directory="", SettingsSHA256="", WorkerSHA256="", Reason="", FailureType="";
        public int ParentProcessId, WorkerProcessId;
    }
    internal static class UnifiedResidentLaunchEvidence
    {
        internal const string RequestFile="resident-launch-request.xml", ProofFile="resident-not-started.xml", EnteredFile="resident-server-entered.xml";
        internal const string Canceled="CancellationRequestedBeforeNative", ParentUnavailable="SupervisorUnavailableBeforeNative",
            HeartbeatInvalid="InvalidHeartbeatBeforeNative", GuardFailed="LaunchGuardFailedBeforeNative";
        internal static UnifiedResidentLaunchRecord Create(string directory,string settings,int parent,string workerPath)
        {
            var record=new UnifiedResidentLaunchRecord {Nonce=Guid.NewGuid().ToString("N"),Directory=Path.GetFullPath(directory),
                SettingsSHA256=Hash(settings),WorkerSHA256=Hash(workerPath),ParentProcessId=parent};
            if(!MatchesRequest(record,directory,settings,parent,record.WorkerSHA256))throw new InvalidDataException("常驻启动请求身份无效。");
            WriteNew(Path.Combine(record.Directory,RequestFile),record);return record;
        }
        internal static void WriteRejectedBeforeNative(string directory,string settings,int parent,int worker,string workerPath,string reason,Exception failure=null)
        {
            if(!ValidReason(reason))throw new InvalidDataException("常驻启动拒绝原因无效。");
            var request=Read(Path.Combine(directory,RequestFile));
            if(!MatchesRequest(request,directory,settings,parent,Hash(workerPath))||worker<=0||worker==parent||
                File.Exists(Path.Combine(directory,EnteredFile)))throw new InvalidDataException("常驻启动拒绝证明与当前请求不符或已进入设备会话。");
            request.WorkerProcessId=worker;request.Reason=reason;
            request.FailureType=failure==null?"":failure.GetType().FullName;
            WriteNew(Path.Combine(directory,ProofFile),request);
        }
        internal static void MarkServerEntry(string directory,string settings,int parent,int worker,string workerPath)
        {
            var request=Read(Path.Combine(directory,RequestFile));
            if(!MatchesRequest(request,directory,settings,parent,Hash(workerPath))||worker<=0||worker==parent||
                File.Exists(Path.Combine(directory,ProofFile)))throw new InvalidDataException("常驻启动请求身份失配；未进入设备会话。");
            request.WorkerProcessId=worker;request.Reason="ResidentServerEntryStarted";
            WriteNew(Path.Combine(directory,EnteredFile),request);
        }
        internal static UnifiedResidentLaunchRecord VerifyNeverEntered(UnifiedResidentLaunchRecord request,string settings,
            string expectedWorkerHash,int worker,bool exited,int exitCode)
        {
            if(!exited||exitCode!=2||worker<=0||request==null)return null;
            try {
                // Absence alone is never sufficient: require the exact child's
                // bound proof as well. Contradictory/native/cleanup evidence wins.
                foreach(var name in new[]{EnteredFile,"ready.flag","progress.log","session-fault.xml","cleanup-fault.xml","cleanup-error.txt","closed.flag","error.txt"})
                    if(File.Exists(Path.Combine(request.Directory,name)))return null;
                var proof=Read(Path.Combine(request.Directory,ProofFile));
                return MatchesRequest(proof,request.Directory,settings,request.ParentProcessId,expectedWorkerHash)&&
                    proof.Nonce==request.Nonce&&proof.SettingsSHA256==request.SettingsSHA256&&proof.WorkerSHA256==request.WorkerSHA256&&
                    proof.WorkerProcessId==worker&&ValidReason(proof.Reason)?proof:null;
            }catch(IOException){return null;}catch(InvalidDataException){return null;}catch(InvalidOperationException){return null;}catch(XmlException){return null;}
            catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
        }
        static bool MatchesRequest(UnifiedResidentLaunchRecord record,string directory,string settings,int parent,string expectedWorkerHash)
        {
            Guid nonce;
            return record!=null&&record.Schema=="HVX.Resident.PreNative/1"&&Guid.TryParseExact(record.Nonce,"N",out nonce)&&nonce!=Guid.Empty&&
                record.ParentProcessId==parent&&parent>0&&String.Equals(record.Directory,Path.GetFullPath(directory),StringComparison.OrdinalIgnoreCase)&&
                String.Equals(Path.GetFullPath(settings),Path.Combine(record.Directory,"session-settings.xml"),StringComparison.OrdinalIgnoreCase)&&
                record.SettingsSHA256==Hash(settings)&&record.WorkerSHA256==expectedWorkerHash&&IsHash(expectedWorkerHash);
        }
        static bool ValidReason(string reason){return reason==Canceled||reason==ParentUnavailable||reason==HeartbeatInvalid||reason==GuardFailed;}
        static bool IsHash(string value)
        {if(value==null||value.Length!=64)return false;foreach(char c in value)if(!((c>='0'&&c<='9')||(c>='A'&&c<='F')))return false;return true;}
        static string Hash(string path)
        {using(var algorithm=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-","");}
        static UnifiedResidentLaunchRecord Read(string path)
        {
            if(new FileInfo(path).Length>16384)throw new InvalidDataException("常驻启动证明过长。");
            using(var reader=XmlReader.Create(path,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16384}))
                return (UnifiedResidentLaunchRecord)new XmlSerializer(typeof(UnifiedResidentLaunchRecord)).Deserialize(reader);
        }
        static void WriteNew(string path,UnifiedResidentLaunchRecord value)
        {using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))new XmlSerializer(typeof(UnifiedResidentLaunchRecord)).Serialize(stream,value);}
    }
    internal static class FactoryResidentLaunchLifecycle
    {
        // Injectable actions exercise the production ordering without a process,
        // native backend, product entry or real device lock in offline tests.
        internal static async Task StartAsync(Action start,Func<Task> waitReady,Func<Task> close,CancellationToken token)
        {
            try {token.ThrowIfCancellationRequested();start();await waitReady().ConfigureAwait(false);}
            catch(Exception original) {
                try {await close().ConfigureAwait(false);}
                catch(Exception cleanup) {throw new AggregateException("设备会话启动失败，且释放未确认；保留原始启动原因及清理原因，不自动重开。",original,cleanup);}
                throw;
            }
        }
    }
}
