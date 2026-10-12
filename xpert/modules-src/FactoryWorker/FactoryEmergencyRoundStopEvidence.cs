using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Raised only after the sole existing DI reader actually returned LOW.
    internal sealed class FactoryEmergencyStopException : OperationCanceledException
    {
        internal FactoryEmergencyStopException():base("急停输入LOW已锁存；停止本轮输出，不自动恢复或续轮。"){}
    }
    internal interface IFactoryEmergencyLatchedSessionEvidence : IFactoryHealthyRoundStopSessionEvidence
    {
        bool EmergencyOutputLatched {get;}
        bool CanRetainEmergencyDisplaySession {get;}
    }
    public sealed class FactoryEmergencyRoundStopRecord
    {
        public string Schema="HVX.EmergencyRoundStop/1",AcceptedRequestSHA256="",Outcome="";
        public FactoryFailedRoundReleaseRecord RequestContext;
        public bool OutputShutdownReturned,ArtRoundResourcesReleased,PicoStopCaptureReturned,
            PicoBufferReleaseReturned,RoundEndReturned,OutputLatched,DisplaySessionRetained;
    }
    internal sealed class FactoryEmergencyRoundStoppedException : OperationCanceledException
    {
        public bool CaptureResourcesReleased {get{return true;}}
        public bool OutputLatched {get{return true;}}
        public string StopEvidenceCode {get{return "VerifiedEmergencyRoundStopRetained";}}
        internal FactoryEmergencyRoundStoppedException():base("急停已停止本轮；HV OFF、停采及缓冲释放返回已核验。保留只读DI，输出仍锁存，不自动恢复或续轮；不代表已放电。"){}
    }
    internal static class FactoryEmergencyRoundStopEvidence
    {
        internal const int ResultCode=5;
        internal const string ProofFile="emergency-round-stop.xml";
        // Context wrappers may not hide a second cleanup/driver error. A user
        // cancel racing with physical LOW is still a stop, never a success.
        internal static bool IsOnlyStopFailure(Exception error)
        {
            if(error is OperationCanceledException)return true;
            var aggregate=error as AggregateException;
            if(aggregate==null||aggregate.InnerExceptions.Count==0)return false;
            foreach(var inner in aggregate.InnerExceptions)if(!IsOnlyStopFailure(inner))return false;
            return true;
        }
        internal static bool CanPublish(FactorySessionRequest request,string directory,string session,int parent,int worker)
        {
            try{return request!=null&&request.Command=="Pressure"&&request.FailureReleaseContext!=null&&
                FactoryFailedRoundReleaseEvidence.Matches(request.FailureReleaseContext,request,directory,session,parent,worker)&&
                FactoryFailedRoundReleaseEvidence.AcceptedMatches(request,directory)&&
                File.ReadAllText(Path.Combine(directory,"ready.flag"))==session&&
                !File.Exists(Path.Combine(directory,"closed.flag"))&&!File.Exists(Path.Combine(directory,"cleanup-error.txt"))&&
                !File.Exists(Path.Combine(directory,FactorySessionFaultStore.FaultFile))&&!File.Exists(Path.Combine(directory,FactorySessionFaultStore.CleanupFile))&&
                !File.Exists(Path.Combine(directory,"fault-record-error.txt"));}
            catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}catch(XmlException){return false;}
        }
        internal static void Publish(FactorySessionRequest request,string directory,string session,int parent,int worker,
            FactoryHealthyRoundStopFacts facts,bool retained)
        {
            if(facts==null||!facts.Complete||!retained||!CanPublish(request,directory,session,parent,worker))
                throw new InvalidDataException("急停停止回执缺少输入绑定、完整关断/停采返回或健康只读DI；不恢复就绪。");
            var proof=new FactoryEmergencyRoundStopRecord {RequestContext=request.FailureReleaseContext,
                AcceptedRequestSHA256=FactoryFailedRoundReleaseEvidence.Hash(FactoryFailedRoundReleaseEvidence.AcceptedPath(request,directory)),
                OutputShutdownReturned=facts.OutputShutdownReturned,ArtRoundResourcesReleased=facts.ArtRoundResourcesReleased,
                PicoStopCaptureReturned=facts.PicoStopCaptureReturned,PicoBufferReleaseReturned=facts.PicoBufferReleaseReturned,
                RoundEndReturned=facts.RoundEndReturned,OutputLatched=true,DisplaySessionRetained=true,Outcome="EmergencyStoppedDisplayOnly"};
            string path=Path.Combine(request.Directory,ProofFile),pending=path+".pending."+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(FactoryEmergencyRoundStopRecord)).Serialize(stream,proof);
            File.Move(pending,path);
        }
        internal static bool Verify(FactorySessionRequest request,string directory,string session,int parent,int worker,bool alive,int code)
        {
            if(!alive||code!=ResultCode||!CanPublish(request,directory,session,parent,worker))return false;
            try{
                FactoryEmergencyRoundStopRecord p;
                using(var reader=XmlReader.Create(Path.Combine(request.Directory,ProofFile),new XmlReaderSettings {
                    DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16384}))
                    p=(FactoryEmergencyRoundStopRecord)new XmlSerializer(typeof(FactoryEmergencyRoundStopRecord)).Deserialize(reader);
                return p!=null&&p.Schema=="HVX.EmergencyRoundStop/1"&&p.Outcome=="EmergencyStoppedDisplayOnly"&&p.RequestContext!=null&&
                    p.RequestContext.Nonce==request.FailureReleaseContext.Nonce&&
                    FactoryFailedRoundReleaseEvidence.Matches(p.RequestContext,request,directory,session,parent,worker)&&
                    p.AcceptedRequestSHA256==FactoryFailedRoundReleaseEvidence.Hash(FactoryFailedRoundReleaseEvidence.AcceptedPath(request,directory))&&
                    p.OutputShutdownReturned&&p.ArtRoundResourcesReleased&&p.PicoStopCaptureReturned&&p.PicoBufferReleaseReturned&&
                    p.RoundEndReturned&&p.OutputLatched&&p.DisplaySessionRetained;
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}catch(XmlException){return false;}
        }
    }
}
