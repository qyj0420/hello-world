using System;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        // A worker exit is not itself evidence that ART was disconnected. Keep
        // typed evidence from the matching worker, not text-based guesses.
        private void ObserveIdleSessionFailure(Exception error)
        {
            var typed=FindSessionFault(error);
            var fault=typed==null?FactorySessionFault.Unknown("","Idle",error):typed.Fault;
            DateTime at=fault.OccurredUtc==DateTime.MinValue?DateTime.UtcNow:fault.OccurredUtc.ToUniversalTime();
            string detail="会话="+fault.SessionId+"；阶段="+fault.Phase+"；"+fault.Detail;
            if(!String.IsNullOrEmpty(fault.PriorDetail))detail+="；原始原因："+fault.PriorDetail;
            string kind;
            switch(fault.Category)
            {
                case FactorySessionFaultCategory.EmergencyInputLow:
                    artLink="LOW_LATCHED";picoLink="CLOSED";
                    kind="P0.4低电平锁存后退出";
                    status.Text="有效读取曾返回P0.4低电平；会话已退出，不能据此断言物理按钮已按下。";
                    break;
                case FactorySessionFaultCategory.ReadTimeout:
                    artLink=fault.Device=="ART"?"TIMEOUT":"SESSION_ERROR";picoLink="CLOSED";
                    kind=fault.Device=="ART"?"P0.4读取超时，许可未知":"会话等待超时，设备原因未确定";
                    status.Text=fault.Device=="ART"?"P0.4未取得有效读数；不是低电平证据。原会话退出后只读重新验证。":"会话等待超时，未获得具体设备来源，不能归因于ART或P0.4。";
                    break;
                case FactorySessionFaultCategory.DriverError:
                    artLink="ERROR";picoLink="CLOSED";
                    kind="ART读取/驱动异常";
                    status.Text="ART读取/驱动异常；是否物理断线尚未确认。";
                    break;
                case FactorySessionFaultCategory.PicoError:
                    artLink="CLOSED";picoLink="ERROR";
                    kind="Pico读取/驱动异常";
                    status.Text="Pico读取/驱动异常；不记为ART断线。";
                    break;
                case FactorySessionFaultCategory.CleanupError:
                    artLink=picoLink="CLEANUP_FAILED";
                    kind="设备任务清理失败";
                    status.Text="设备任务清理失败；旧资源未确认释放，禁止自动重开。";
                    break;
                default:
                    artLink=picoLink="SESSION_ERROR";
                    kind="设备会话异常，原因未分类";
                    status.Text="设备会话异常，原因未分类；不推断ART断线或P0.4低电平。";
                    break;
            }
            artSeen=picoSeen=at;
            try
            {
                if(connectionJournal!=null)
                {
                    if(fault.Category==FactorySessionFaultCategory.ReadTimeout || fault.Category==FactorySessionFaultCategory.DriverError || fault.Category==FactorySessionFaultCategory.PicoError)
                    {connectionInterrupted=true;connectionJournal.Fault(fault.Device,at,detail,kind);}
                    else connectionJournal.Event(fault.Device,at,kind,detail);
                }
                diagnostics.AppendText(kind+"："+detail+Environment.NewLine);
                RefreshConnectionHistory();
            }
            catch(Exception logError){connectionLogFailed=true;status.Text="故障分类日志写入失败："+logError.Message;}
            // Even between rounds the failed session may not auto-continue.
            if(busy && sequenceProgress!=null && sequenceProgress.Running)
            {
                sequenceProgress.Observe(fault.Category==FactorySessionFaultCategory.EmergencyInputLow?"EMERGENCY_ACTIVE":"CONNECTION_LOST");
                if(cancellation!=null)cancellation.Cancel();
            }
            RenderConnection();
        }
        private static FactorySessionFaultException FindSessionFault(Exception error)
        {
            if(error==null)return null;
            var typed=error as FactorySessionFaultException;if(typed!=null)return typed;
            var aggregate=error as AggregateException;
            if(aggregate!=null)
            {
                FactorySessionFaultException found=null;
                foreach(var inner in aggregate.InnerExceptions)
                {var candidate=FindSessionFault(inner);if(candidate!=null && (found==null || candidate.Fault.Category==FactorySessionFaultCategory.CleanupError))found=candidate;}
                if(found!=null)return found;
            }
            return FindSessionFault(error.InnerException);
        }
    }
}
