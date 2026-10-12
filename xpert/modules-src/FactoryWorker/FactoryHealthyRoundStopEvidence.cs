using System;
using System.IO;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // The accepted input binding is shared with failure cleanup, but the
    // outcomes are deliberately distinct: this receipt retains a live session
    // and never asserts Close, physical discharge, or a successful capture.
    public sealed class FactoryHealthyRoundStopRecord
    {
        public string Schema="HVX.HealthyRoundStop/1",AcceptedRequestSHA256="",Outcome="";
        public FactoryFailedRoundReleaseRecord RequestContext;
        public bool OutputShutdownReturned,ArtRoundResourcesReleased,PicoStopCaptureReturned,
            PicoBufferReleaseReturned,RoundEndReturned,SessionReusable;
    }
    internal sealed class FactoryHealthyRoundStoppedException:OperationCanceledException
    {
        public bool CaptureResourcesReleased {get{return true;}}
        public string StopEvidenceCode {get{return "VerifiedOwnedHealthyRoundStop";}}
        internal FactoryHealthyRoundStoppedException():base("操作人员已停止本轮，关断调用、停采和缓冲释放已核验；保持连接，不保存或自动续轮。停止不代表已放电。"){}
    }
    internal sealed class FactoryHealthyRoundStopFacts
    {
        internal readonly bool OutputShutdownReturned,ArtRoundResourcesReleased,PicoStopCaptureReturned,PicoBufferReleaseReturned,RoundEndReturned;
        internal FactoryHealthyRoundStopFacts(bool output,bool art,bool picoStop,bool buffer,bool end)
        {OutputShutdownReturned=output;ArtRoundResourcesReleased=art;PicoStopCaptureReturned=picoStop;PicoBufferReleaseReturned=buffer;RoundEndReturned=end;}
        internal bool Complete {get{return OutputShutdownReturned&&ArtRoundResourcesReleased&&PicoStopCaptureReturned&&PicoBufferReleaseReturned&&RoundEndReturned;}}
    }
    internal interface IFactoryHealthyRoundStopSessionEvidence
    {
        FactoryHealthyRoundStopFacts HealthyRoundStopFacts {get;}
        void ResetHealthyRoundStopEvidence();
    }
    internal static class FactoryHealthyRoundStopEvidence
    {
        internal const int ResultCode=4;
        internal const string ProofFile="healthy-round-stop.xml";
        internal static bool IsOnlyCanceledFailure(Exception error)
        {
            var run=error as FactoryRunException;
            return run!=null&&run.InnerExceptions.Count==1&&run.InnerExceptions[0] is OperationCanceledException;
        }
        internal static void ThrowIfCanceledAfterRound(FactoryResult result,CancellationToken cancellation)
        {
            if(cancellation.IsCancellationRequested)
                throw new FactoryRunException(result,new Exception[]{new OperationCanceledException(cancellation)});
        }
        internal static bool CanPublish(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker)
        {
            try{return request!=null&&request.Command=="Pressure"&&BoundInput(request,sessionDirectory,sessionId,parent,worker)&&StopRequested(request,sessionDirectory)&&
                !HasTerminalEvidence(sessionDirectory);}
            catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}catch(XmlException){return false;}
        }
        internal static void Publish(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker,
            FactoryHealthyRoundStopFacts facts,bool reusable)
        {
            if(facts==null||!facts.Complete||!reusable||!CanPublish(request,sessionDirectory,sessionId,parent,worker))
                throw new InvalidDataException("主动停止证明缺少本轮输入绑定、停止请求或完整资源返回记录；未恢复就绪。");
            var proof=new FactoryHealthyRoundStopRecord {RequestContext=request.FailureReleaseContext,
                AcceptedRequestSHA256=FactoryFailedRoundReleaseEvidence.Hash(FactoryFailedRoundReleaseEvidence.AcceptedPath(request,sessionDirectory)),
                OutputShutdownReturned=facts.OutputShutdownReturned,ArtRoundResourcesReleased=facts.ArtRoundResourcesReleased,
                PicoStopCaptureReturned=facts.PicoStopCaptureReturned,PicoBufferReleaseReturned=facts.PicoBufferReleaseReturned,
                RoundEndReturned=facts.RoundEndReturned,SessionReusable=true,Outcome="OwnedRoundStoppedSessionRetained"};
            string path=Path.Combine(request.Directory,ProofFile),pending=path+".pending."+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(FactoryHealthyRoundStopRecord)).Serialize(stream,proof);
            File.Move(pending,path);
        }
        internal static bool Verify(FactorySessionRequest request,string sessionDirectory,string sessionId,int parent,int worker,bool alive,int code)
        {
            if(!alive||code!=ResultCode||!CanPublish(request,sessionDirectory,sessionId,parent,worker))return false;
            try{
                FactoryHealthyRoundStopRecord p;
                using(var reader=XmlReader.Create(Path.Combine(request.Directory,ProofFile),new XmlReaderSettings {
                    DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16384}))
                    p=(FactoryHealthyRoundStopRecord)new XmlSerializer(typeof(FactoryHealthyRoundStopRecord)).Deserialize(reader);
                return p!=null&&p.Schema=="HVX.HealthyRoundStop/1"&&p.Outcome=="OwnedRoundStoppedSessionRetained"&&
                    p.RequestContext!=null&&p.RequestContext.Nonce==request.FailureReleaseContext.Nonce&&
                    FactoryFailedRoundReleaseEvidence.Matches(p.RequestContext,request,sessionDirectory,sessionId,parent,worker)&&
                    p.AcceptedRequestSHA256==FactoryFailedRoundReleaseEvidence.Hash(FactoryFailedRoundReleaseEvidence.AcceptedPath(request,sessionDirectory))&&
                    p.OutputShutdownReturned&&p.ArtRoundResourcesReleased&&p.PicoStopCaptureReturned&&p.PicoBufferReleaseReturned&&
                    p.RoundEndReturned&&p.SessionReusable;
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}catch(XmlException){return false;}
        }
        static bool BoundInput(FactorySessionRequest request,string directory,string sessionId,int parent,int worker)
        {
            return request!=null&&request.FailureReleaseContext!=null&&
                FactoryFailedRoundReleaseEvidence.Matches(request.FailureReleaseContext,request,directory,sessionId,parent,worker)&&
                FactoryFailedRoundReleaseEvidence.AcceptedMatches(request,directory)&&
                File.ReadAllText(Path.Combine(directory,"ready.flag"))==sessionId;
        }
        static bool StopRequested(FactorySessionRequest request,string directory)
        {return File.Exists(Path.Combine(directory,"batch.cancel"))&&File.Exists(Path.Combine(request.Directory,"cancel.flag"))&&
            File.ReadAllText(Path.Combine(request.Directory,FactoryInputStopEvidence.RequestStopFile)).Trim()==request.Id;}
        static bool HasTerminalEvidence(string directory)
        {return File.Exists(Path.Combine(directory,"closed.flag"))||File.Exists(Path.Combine(directory,"cleanup-error.txt"))||
            File.Exists(Path.Combine(directory,FactorySessionFaultStore.FaultFile))||File.Exists(Path.Combine(directory,FactorySessionFaultStore.CleanupFile))||
            File.Exists(Path.Combine(directory,"fault-record-error.txt"));}
    }
}
