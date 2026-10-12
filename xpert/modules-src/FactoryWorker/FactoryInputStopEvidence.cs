using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal interface IFactoryInputStopSessionEvidence {bool InputStopHealthy {get;}}
    // This is a round-owned input-stop receipt, never a discharge or HV receipt.
    public sealed class FactoryInputStopRecord
    {
        public string Schema="HVX.InputStop/2", RequestId="", Nonce="", SettingsSHA256="", InputSettingsPath="", AcceptedRequestSHA256="", Directory="", SessionDirectory="", SessionId="", Outcome="";
        public int ParentProcessId,SessionProcessId;
    }
    internal sealed class FactoryInputStopCompletedException:OperationCanceledException
    {
        internal readonly FactoryResult PartialResult;
        internal FactoryInputStopCompletedException(FactoryResult result):base("0 kV输入采集已取消，AI/Pico本轮停止及缓冲释放已完成；不代表设备放电。") {PartialResult=result;}
    }
    internal sealed class FactoryInputStoppedException:OperationCanceledException
    {
        public bool InputResourcesReleased {get{return true;}}
        public string StopEvidenceCode {get{return "VerifiedOwnedZeroInputStop";}}
        internal FactoryInputStoppedException():base("本轮0 kV输入已停止并释放采集资源；会话保持只读，不自动继续，不代表设备放电。"){}
    }
    internal static class FactoryInputStopEvidence
    {
        internal const int ResultCode=3;
        internal const string ProofFile="input-stop-completed.xml",RequestStopFile="input-stop-request-id.txt";
        internal static FactoryInputStopRecord Create(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            string hash,snapshot=FactoryFailedRoundReleaseEvidence.CreateInputSnapshot(request,sessionDirectory,out hash);
            return new FactoryInputStopRecord {RequestId=request.Id,Nonce=Guid.NewGuid().ToString("N"),SettingsSHA256=hash,InputSettingsPath=snapshot,
                Directory=Path.GetFullPath(request.Directory),SessionDirectory=Path.GetFullPath(sessionDirectory),SessionId=sessionId,
                ParentProcessId=parent,SessionProcessId=worker};
        }
        internal static FactorySettings LoadInputSettings(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            var r=request.InputStopContext;
            if(!Matches(r,request,sessionDirectory,sessionId,parent,worker))
                throw new InvalidDataException("输入停止原始配置快照与请求身份不匹配；未执行轮次或输出。");
            return FactoryFailedRoundReleaseEvidence.LoadSnapshotSettings(request,r.InputSettingsPath,r.SettingsSHA256);
        }
        internal static bool CanPublish(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            try {
                var r=request.InputStopContext;
                return Matches(r,request,sessionDirectory,sessionId,parent,worker)&&AcceptedMatches(request,sessionDirectory)&&r.Outcome==""&&
                    File.ReadAllText(Path.Combine(request.Directory,RequestStopFile))==request.Id;
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}catch(XmlException){return false;}
        }
        internal static void Publish(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            if(!CanPublish(request,sessionDirectory,sessionId,parent,worker))throw new InvalidDataException("停止证明与本轮会话请求不匹配。");
            var r=request.InputStopContext;
            var proof=new FactoryInputStopRecord {RequestId=r.RequestId,Nonce=r.Nonce,SettingsSHA256=r.SettingsSHA256,InputSettingsPath=r.InputSettingsPath,
                AcceptedRequestSHA256=Hash(AcceptedPath(request,sessionDirectory)),Directory=r.Directory,
                SessionDirectory=r.SessionDirectory,SessionId=r.SessionId,ParentProcessId=r.ParentProcessId,SessionProcessId=r.SessionProcessId,
                Outcome="StoppedInputOnly"};
            using(var stream=new FileStream(Path.Combine(request.Directory,ProofFile),FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(FactoryInputStopRecord)).Serialize(stream,proof);
        }
        internal static bool Verify(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker,bool ownedSessionAlive,int resultCode)
        {
            if(!ownedSessionAlive||resultCode!=ResultCode||request==null||request.InputStopContext==null)return false;
            try {
                FactoryInputStopRecord proof;
                using(var reader=XmlReader.Create(Path.Combine(request.Directory,ProofFile),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16384}))
                    proof=(FactoryInputStopRecord)new XmlSerializer(typeof(FactoryInputStopRecord)).Deserialize(reader);
                return Matches(proof,request,sessionDirectory,sessionId,parent,worker)&&AcceptedMatches(request,sessionDirectory)&&proof.Nonce==request.InputStopContext.Nonce&&
                    proof.SettingsSHA256==request.InputStopContext.SettingsSHA256&&proof.Outcome=="StoppedInputOnly"&&
                    proof.AcceptedRequestSHA256==Hash(AcceptedPath(request,sessionDirectory))&&
                    File.ReadAllText(Path.Combine(request.Directory,RequestStopFile))==request.Id;
            }catch(IOException){return false;}catch(InvalidOperationException){return false;}catch(XmlException){return false;}
            catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
        }
        static bool Matches(FactoryInputStopRecord r,FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            Guid id,nonce;
            return r!=null&&request!=null&&r.Schema=="HVX.InputStop/2"&&Guid.TryParseExact(r.RequestId,"N",out id)&&id!=Guid.Empty&&r.RequestId==request.Id&&
                Guid.TryParseExact(r.Nonce,"N",out nonce)&&nonce!=Guid.Empty&&!String.IsNullOrWhiteSpace(sessionId)&&r.SessionId==sessionId&&
                parent>0&&worker>0&&r.ParentProcessId==parent&&r.SessionProcessId==worker&&
                String.Equals(r.SessionDirectory,Path.GetFullPath(sessionDirectory),StringComparison.OrdinalIgnoreCase)&&
                String.Equals(r.Directory,Path.GetFullPath(request.Directory),StringComparison.OrdinalIgnoreCase)&&
                String.Equals(Path.GetFullPath(request.SettingsPath),Path.Combine(r.Directory,"factory-settings.xml"),StringComparison.OrdinalIgnoreCase)&&
                !String.IsNullOrWhiteSpace(r.InputSettingsPath)&&String.Equals(r.InputSettingsPath,FactoryFailedRoundReleaseEvidence.InputPath(request,sessionDirectory),StringComparison.OrdinalIgnoreCase)&&
                r.SettingsSHA256==Hash(r.InputSettingsPath);
        }
        static bool AcceptedMatches(FactorySessionRequest request,string sessionDirectory)
        {
            var accepted=FactoryResidentDeviceServer.ReadRequest(AcceptedPath(request,sessionDirectory));
            var a=accepted.InputStopContext;var r=request.InputStopContext;
            return accepted.Id==request.Id&&accepted.Command==request.Command&&accepted.Directory==request.Directory&&
                accepted.SettingsPath==request.SettingsPath&&a!=null&&r!=null&&a.Nonce==r.Nonce&&
                Matches(a,request,sessionDirectory,r.SessionId,r.ParentProcessId,r.SessionProcessId);
        }
        static string AcceptedPath(FactorySessionRequest request,string sessionDirectory)
        {return Path.Combine(sessionDirectory,"request."+Guid.ParseExact(request.Id,"N").ToString("N")+".accepted.xml");}
        static string Hash(string path)
        {using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    }
}
