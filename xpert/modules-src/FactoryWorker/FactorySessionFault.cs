using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public enum FactorySessionFaultCategory
    {
        Unknown, EmergencyInputLow, ReadTimeout, DriverError, PicoError, CleanupError
    }

    // Diagnostic identity only: none of these fields grants output permission.
    public sealed class FactorySessionFault
    {
        public string SessionId;
        public string Device;
        public FactorySessionFaultCategory Category;
        public string Phase;
        public DateTime OccurredUtc;
        public string Detail;
        public string PriorDetail;

        internal FactorySessionFault Copy()
        { return (FactorySessionFault)MemberwiseClone(); }

        internal static FactorySessionFault Unknown(string sessionId,string phase,Exception error)
        {
            return new FactorySessionFault {SessionId=sessionId??"",Device="Unknown",Category=FactorySessionFaultCategory.Unknown,
                Phase=phase??"Unknown",OccurredUtc=DateTime.UtcNow,Detail=error==null?"会话退出原因未知。":error.ToString()};
        }
        internal static bool ContainsTimeout(Exception error)
        {
            if(error==null)return false;
            if(error is TimeoutException)return true;
            var aggregate=error as AggregateException;
            if(aggregate!=null)foreach(var inner in aggregate.InnerExceptions)if(ContainsTimeout(inner))return true;
            return ContainsTimeout(error.InnerException);
        }
    }

    internal sealed class FactorySessionFaultException : InvalidOperationException
    {
        internal FactorySessionFault Fault {get;private set;}
        internal FactorySessionFaultException(FactorySessionFault fault)
            :base(fault==null?"设备会话退出，原因未知。":fault.Detail)
        { Fault=fault??FactorySessionFault.Unknown("","Unknown",null); }
    }

    // LINK_* events originate from the current backend. Freeze only error/LOW
    // observations; later HIGH/ONLINE/CLOSED must not erase the original cause.
    internal sealed class FactorySessionFaultTracker
    {
        private readonly object gate=new object();
        private FactorySessionFault observed;
        internal string SessionId="",Phase="Opening";
        internal void Observe(string key,string detail)
        {
            FactorySessionFaultCategory kind;string device;
            if(key=="LINK_ART_LOW"||key=="LINK_ART_LATCHED_LOW"){kind=FactorySessionFaultCategory.EmergencyInputLow;device="ART";}
            else if(key=="P04_IDLE_RETRY_FAILED" || key=="P04_IDLE_TERMINAL_TIMEOUT"){kind=FactorySessionFaultCategory.ReadTimeout;device="ART";}
            else if(key=="LINK_ART_ERROR"){kind=FactorySessionFaultCategory.DriverError;device="ART";}
            else if(key=="LINK_PICO_ERROR"){kind=FactorySessionFaultCategory.PicoError;device="Pico";}
            else return;
            DateTime at=DateTime.UtcNow,parsed;int separator=(detail??"").IndexOf('|');
            if(separator>0 && DateTime.TryParse(detail.Substring(0,separator),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed))
            {at=parsed.ToUniversalTime();detail=detail.Substring(separator+1);}
            lock(gate)if(observed==null)observed=new FactorySessionFault {SessionId=SessionId,Device=device,Category=kind,
                Phase=Phase,OccurredUtc=at,Detail=detail??""};
        }
        internal FactorySessionFault Freeze(Exception error,FactorySessionFault native=null)
        {
            FactorySessionFault result;
            lock(gate)result=observed==null?null:observed.Copy();
            if(native!=null)result=native.Copy();
            if(result==null)result=FactorySessionFault.Unknown(SessionId,Phase,error);
            if(result.Category!=FactorySessionFaultCategory.EmergencyInputLow &&
                result.Category!=FactorySessionFaultCategory.PicoError && FactorySessionFault.ContainsTimeout(error))
                result.Category=FactorySessionFaultCategory.ReadTimeout;
            result.SessionId=SessionId;
            if(String.IsNullOrEmpty(result.Phase))result.Phase=Phase;
            if(error!=null)result.Detail=(String.IsNullOrEmpty(result.Detail)?"":result.Detail+Environment.NewLine)+error;
            return result;
        }
    }

    internal static class FactorySessionFaultStore
    {
        internal const string FaultFile="session-fault.xml",CleanupFile="cleanup-fault.xml";
        internal static void Write(string directory,FactorySessionFault fault,bool cleanup)
        {
            string path=Path.Combine(directory,cleanup?CleanupFile:FaultFile);
            string pending=path+".pending."+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=XmlWriter.Create(stream,new XmlWriterSettings {Encoding=new UTF8Encoding(false),Indent=true,NewLineHandling=NewLineHandling.Entitize}))
                new XmlSerializer(typeof(FactorySessionFault)).Serialize(writer,fault);
            File.Move(pending,path);
        }
        internal static FactorySessionFault Read(string directory,string expectedSessionId=null)
        {
            // Cleanup uncertainty always takes precedence, even if its richer
            // metadata could not be written or an older LOW record exists.
            string cleanupText=Path.Combine(directory,"cleanup-error.txt");
            bool hasCleanupMetadata=File.Exists(Path.Combine(directory,CleanupFile));
            string path=Path.Combine(directory,hasCleanupMetadata?CleanupFile:FaultFile);
            FactorySessionFault fault=null;
            if(File.Exists(path))
            {
                try
                {
                    if(new FileInfo(path).Length>262144)throw new InvalidDataException("会话故障记录过长。");
                    var options=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=262144};
                    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    using(var reader=XmlReader.Create(stream,options))fault=(FactorySessionFault)new XmlSerializer(typeof(FactorySessionFault)).Deserialize(reader);
                    if(fault==null || !Enum.IsDefined(typeof(FactorySessionFaultCategory),fault.Category) ||
                        !String.IsNullOrEmpty(expectedSessionId) && fault.SessionId!=expectedSessionId)
                        throw new InvalidDataException("会话故障身份或类别无效。");
                }
                catch(Exception ex) {fault=FactorySessionFault.Unknown(expectedSessionId,"Unknown",ex);}
            }
            if(File.Exists(cleanupText) || hasCleanupMetadata)
            {
                string detail;
                try{detail=File.Exists(cleanupText)?File.ReadAllText(cleanupText):fault==null?"清理错误详情缺失。":fault.Detail;}
                catch(Exception ex){detail="清理错误详情不可读："+ex;}
                return new FactorySessionFault {SessionId=expectedSessionId??(fault==null?"":fault.SessionId),Device="Session",
                    Category=FactorySessionFaultCategory.CleanupError,Phase="Cleanup",OccurredUtc=fault!=null && fault.Category==FactorySessionFaultCategory.CleanupError?fault.OccurredUtc:DateTime.UtcNow,
                    Detail="旧设备会话清理失败，禁止自动重开："+detail,
                    PriorDetail=fault==null?null:fault.Category==FactorySessionFaultCategory.CleanupError?fault.PriorDetail:fault.Detail};
            }
            if(fault!=null)return fault;
            string error=Path.Combine(directory,"error.txt");
            try{return FactorySessionFault.Unknown(expectedSessionId,"Unknown",new InvalidOperationException(File.Exists(error)?File.ReadAllText(error):"常驻设备会话已退出；原会话不再复用，不自动恢复输出。"));}
            catch(Exception ex){return FactorySessionFault.Unknown(expectedSessionId,"Unknown",ex);}
        }
    }
}
