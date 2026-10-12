using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryFailedRoundReleaseRecord
    {
        public string Schema="HVX.FailedRoundRelease/2",RequestId="",Nonce="",SettingsSHA256="",AcceptedRequestSHA256="",InputSettingsPath="";
        public string Directory="",SessionDirectory="",SessionId="",Outcome="";
        public int ParentProcessId,SessionProcessId;
        public bool RoundEndReturned,NativeDisposeReturned,LeaseReleaseReturned;
    }
    internal sealed class FactoryFailedRoundReleasedException:InvalidOperationException
    {
        public bool CaptureResourcesReleased {get{return true;}}
        public string ReleaseEvidenceCode {get{return "VerifiedOwnedFailedRoundNativeCleanup";}}
        internal FactoryFailedRoundReleasedException(Exception failure):base(failure.Message,failure){}
    }
    internal static class FactoryFailedRoundReleaseEvidence
    {
        internal const string ProofFile="failed-round-native-release.xml";
        internal static FactoryFailedRoundReleaseRecord Create(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            Guid id;
            if(request==null||!Guid.TryParseExact(request.Id,"N",out id)||id==Guid.Empty||parent<=0||worker<=0||String.IsNullOrWhiteSpace(sessionId)||
                !String.Equals(Path.GetFullPath(request.SettingsPath),Path.Combine(Path.GetFullPath(request.Directory),"factory-settings.xml"),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("失败轮次输入快照缺少本轮身份或原始配置路径。");
            string hash,snapshot=CreateInputSnapshot(request,sessionDirectory,out hash);
            return new FactoryFailedRoundReleaseRecord {RequestId=request.Id,Nonce=Guid.NewGuid().ToString("N"),SettingsSHA256=hash,InputSettingsPath=snapshot,
                Directory=Path.GetFullPath(request.Directory),SessionDirectory=Path.GetFullPath(sessionDirectory),SessionId=sessionId,
                ParentProcessId=parent,SessionProcessId=worker};
        }
        internal static string CreateInputSnapshot(FactorySessionRequest request,string sessionDirectory,out string hash)
        {
            string snapshot=InputPath(request,sessionDirectory);
            // The archive later rewrites factory-settings.xml. Preserve the exact
            // submitted input bytes separately; neither the archive nor a later
            // round may replace this CreateNew, request-owned snapshot.
            using(var source=new FileStream(request.SettingsPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                hash=Hash(source);source.Position=0;
                // Input-stop and failed-round contexts share this exact input.
                // Existing bytes may be reused only if equal, never overwritten.
                if(File.Exists(snapshot)){
                    if(Hash(snapshot)!=hash)throw new InvalidDataException("本轮输入快照已存在但内容不匹配；未覆盖。");
                }else using(var target=new FileStream(snapshot,FileMode.CreateNew,FileAccess.Write,FileShare.None))source.CopyTo(target);
            }
            return snapshot;
        }
        internal static FactorySettings LoadInputSettings(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            // Requests without this receipt retain their original loading path.
            // New requests must match their input before acceptance and consume
            // the same locked snapshot bytes whose hash was submitted.
            if(request.FailureReleaseContext==null)
                return request.InputStopContext==null?FactorySettings.Load(request.SettingsPath):FactoryInputStopEvidence.LoadInputSettings(request,sessionDirectory,sessionId,parent,worker);
            var r=request.FailureReleaseContext;
            if(!Matches(r,request,sessionDirectory,sessionId,parent,worker))
                throw new InvalidDataException("轮次原始输入快照与请求身份不匹配；未执行轮次或输出。");
            return LoadSnapshotSettings(request,r.InputSettingsPath,r.SettingsSHA256);
        }
        internal static FactorySettings LoadSnapshotSettings(FactorySessionRequest request,string snapshot,string expectedHash)
        {
            using(var original=new FileStream(request.SettingsPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var source=new FileStream(snapshot,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(Hash(original)!=expectedHash||Hash(source)!=expectedHash)
                    throw new InvalidDataException("轮次输入在接受前已改变；未执行轮次或输出。");
                source.Position=0;
                using(var reader=XmlReader.Create(source,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
                    return (FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(reader);
            }
        }
        internal static void Publish(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker,
            bool roundEndReturned,bool nativeDisposeReturned,bool leaseReleaseReturned)
        {
            var r=request==null?null:request.FailureReleaseContext;
            if(!roundEndReturned||!nativeDisposeReturned||!leaseReleaseReturned||
                !Matches(r,request,sessionDirectory,sessionId,parent,worker)||!AcceptedMatches(request,sessionDirectory)||HasCleanupError(sessionDirectory))
                throw new InvalidDataException("失败轮次原生释放证明缺少本轮身份或完整清理返回记录。");
            var proof=new FactoryFailedRoundReleaseRecord {RequestId=r.RequestId,Nonce=r.Nonce,SettingsSHA256=r.SettingsSHA256,InputSettingsPath=r.InputSettingsPath,
                AcceptedRequestSHA256=Hash(AcceptedPath(request,sessionDirectory)),Directory=r.Directory,SessionDirectory=r.SessionDirectory,
                SessionId=r.SessionId,ParentProcessId=parent,SessionProcessId=worker,
                RoundEndReturned=true,NativeDisposeReturned=true,LeaseReleaseReturned=true,Outcome="FailedAcceptedRoundNativeCleanupCompleted"};
            string path=Path.Combine(request.Directory,ProofFile),pending=path+".pending."+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(FactoryFailedRoundReleaseRecord)).Serialize(stream,proof);
            File.Move(pending,path);
        }
        internal static bool Verify(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker,bool workerExited,int resultCode)
        {return workerExited&&CanAwaitExit(request,sessionDirectory,sessionId,parent,worker,resultCode);}
        internal static bool CanAwaitExit(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker,int resultCode)
        {
            if(resultCode==0||request==null||request.FailureReleaseContext==null)return false;
            try {
                if(HasCleanupError(sessionDirectory)||!File.Exists(Path.Combine(sessionDirectory,"closed.flag"))||!AcceptedMatches(request,sessionDirectory))return false;
                FactoryFailedRoundReleaseRecord p;
                using(var reader=XmlReader.Create(Path.Combine(request.Directory,ProofFile),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16384}))
                    p=(FactoryFailedRoundReleaseRecord)new XmlSerializer(typeof(FactoryFailedRoundReleaseRecord)).Deserialize(reader);
                return Matches(p,request,sessionDirectory,sessionId,parent,worker)&&p.Nonce==request.FailureReleaseContext.Nonce&&
                    p.RoundEndReturned&&p.NativeDisposeReturned&&p.LeaseReleaseReturned&&p.Outcome=="FailedAcceptedRoundNativeCleanupCompleted"&&
                    p.AcceptedRequestSHA256==Hash(AcceptedPath(request,sessionDirectory));
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}catch(XmlException){return false;}
        }
        internal static bool AcceptedMatches(FactorySessionRequest request,string sessionDirectory)
        {
            var accepted=FactoryResidentDeviceServer.ReadRequest(AcceptedPath(request,sessionDirectory));
            var a=accepted.FailureReleaseContext;var r=request.FailureReleaseContext;
            return accepted.Id==request.Id&&accepted.Command==request.Command&&accepted.Directory==request.Directory&&
                accepted.SettingsPath==request.SettingsPath&&a!=null&&r!=null&&a.Nonce==r.Nonce&&
                Matches(a,request,sessionDirectory,r.SessionId,r.ParentProcessId,r.SessionProcessId);
        }
        internal static string AcceptedPath(FactorySessionRequest request,string sessionDirectory)
        {return Path.Combine(sessionDirectory,"request."+Guid.Parse(request.Id).ToString("N")+".accepted.xml");}
        internal static string InputPath(FactorySessionRequest request,string sessionDirectory)
        {return Path.Combine(Path.GetFullPath(sessionDirectory),"request."+Guid.ParseExact(request.Id,"N").ToString("N")+".input-settings.xml");}
        internal static bool Matches(FactoryFailedRoundReleaseRecord r,FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            Guid id,nonce;
            return r!=null&&request!=null&&r.Schema=="HVX.FailedRoundRelease/2"&&Guid.TryParseExact(r.RequestId,"N",out id)&&id!=Guid.Empty&&r.RequestId==request.Id&&
                Guid.TryParseExact(r.Nonce,"N",out nonce)&&nonce!=Guid.Empty&&!String.IsNullOrWhiteSpace(sessionId)&&r.SessionId==sessionId&&
                parent>0&&worker>0&&r.ParentProcessId==parent&&r.SessionProcessId==worker&&
                String.Equals(r.Directory,Path.GetFullPath(request.Directory),StringComparison.OrdinalIgnoreCase)&&
                String.Equals(r.SessionDirectory,Path.GetFullPath(sessionDirectory),StringComparison.OrdinalIgnoreCase)&&
                String.Equals(Path.GetFullPath(request.SettingsPath),Path.Combine(r.Directory,"factory-settings.xml"),StringComparison.OrdinalIgnoreCase)&&
                !String.IsNullOrWhiteSpace(r.InputSettingsPath)&&String.Equals(r.InputSettingsPath,InputPath(request,sessionDirectory),StringComparison.OrdinalIgnoreCase)&&
                r.SettingsSHA256==Hash(r.InputSettingsPath);
        }
        static bool HasCleanupError(string directory)
        {return File.Exists(Path.Combine(directory,"cleanup-error.txt"))||File.Exists(Path.Combine(directory,FactorySessionFaultStore.CleanupFile))||File.Exists(Path.Combine(directory,"fault-record-error.txt"));}
        internal static string Hash(string path)
        {using(var stream=File.OpenRead(path))return Hash(stream);}
        static string Hash(Stream stream)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    }
}
