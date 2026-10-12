using System;
using System.IO;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal static string TestSessionFaultDisplay()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Session fault UI: "+why);n++;};
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-SessionFault-UI-"+Guid.NewGuid().ToString("N"));
            using(var f=new FactoryMainForm(Path.Combine(root,"labels"),Path.Combine(root,"profiles")))
            {
                f.smokeMode=true;f.mode.SelectedIndex=1;
                f.pinnedArtSerial="ART-TEST";f.pinnedPicoSerial="PICO-TEST";
                f.connectionIdentity="ART-TEST|PICO-TEST";
                f.connectionJournal=new FactoryConnectionJournal(Path.Combine(root,"journal"),"ART-TEST","PICO-TEST");
                f.connectionJournal.TaskName="连续加压 / 第 5 / 5 轮";
                f.connectionJournal.Progress="读取本轮数据 (STEP_READ_RUNNING)";
                f.connectionJournal.Record="PREVIOUS-ROUND";
                f.connectionJournal.EnterPostProcessing();
                check(f.connectionJournal.Progress.Contains("后处理") && f.connectionJournal.Record=="PREVIOUS-ROUND","round tail retains its record but not read-running phase");
                f.connectionJournal.EnterIdle();
                check(f.connectionJournal.TaskName=="操作等待" && f.connectionJournal.Record=="","completed task resets idle context and former record association");
                f.ObserveConnection("LINK_ART_LOW",FactoryConnectionWatch.Stamp("read returned zero"),false);
                f.ObserveConnection("LINK_ART_CLOSED",FactoryConnectionWatch.Stamp("released"),false);
                f.ObserveIdleSessionFailure(new FactorySessionFaultException(new FactorySessionFault {
                    SessionId="same-session",Device="ART",Phase="Idle",OccurredUtc=DateTime.UtcNow,
                    Category=FactorySessionFaultCategory.EmergencyInputLow,Detail="read returned zero"}));
                string last=f.connectionJournal.Lines[f.connectionJournal.Lines.Count-1];
                check(f.artLink=="LOW_LATCHED" && !f.start.Enabled,"LOW exit is latched, not online or ready");
                check(f.connectionJournal.Incidents==0 && last.Contains("低电平锁存") && !last.Contains("通信中断"),"LOW then exit never becomes a communication incident");
                check(last.Contains("操作等待") && !last.Contains("STEP_READ_RUNNING") && !last.Contains("PREVIOUS-ROUND"),"idle fault cannot inherit the previous round");
                check(!f.emergency.Text.Contains("按下"),"display does not assert physical button action");
                f.ObserveConnection("LINK_ART_UNKNOWN",FactoryConnectionWatch.Stamp("timeout pending re-read"),false);
                check(!f.start.Enabled && f.emergency.Text.Contains("无有效值"),"unknown is not stale high permission");
                f.ObserveConnection("P04_IDLE_RETRY_RECOVERED","new valid high; fake only",false);
                check(!f.start.Enabled,"recovery diagnostic alone never sets high");
                f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("new high"),false);
                check(f.start.Enabled && !f.busy,"new valid high only permits explicit user click, no auto-start");
                f.ObserveIdleSessionFailure(new FactorySessionFaultException(new FactorySessionFault {SessionId="s",Device="Pico",Phase="Idle",
                    OccurredUtc=DateTime.UtcNow,Category=FactorySessionFaultCategory.PicoError,Detail="ping failed"}));
                last=f.connectionJournal.Lines[f.connectionJournal.Lines.Count-1];
                check(f.picoLink=="ERROR" && f.artLink=="CLOSED" && last.Split('\t')[1]=="Pico","Pico failure is not attributed to ART");
                f.ObserveIdleSessionFailure(new Exception("unknown fixture"));
                last=f.connectionJournal.Lines[f.connectionJournal.Lines.Count-1];
                check(last.Contains("原因未分类") && last.Split('\t')[1]=="Unknown" && !f.start.Enabled,"untyped exception preserves unknown identity and blocks start");
                f.ObserveIdleSessionFailure(new FactorySessionFaultException(new FactorySessionFault {SessionId="s",Device="ART",Phase="Idle",
                    OccurredUtc=DateTime.UtcNow,Category=FactorySessionFaultCategory.ReadTimeout,Detail="two timed out reads"}));
                check(f.artLink=="TIMEOUT" && f.emergency.Text.Contains("许可未知") && !f.emergency.Text.Contains("低电平"),"timeout is not low");
                f.ObserveIdleSessionFailure(new FactorySessionFaultException(new FactorySessionFault {SessionId="s",Device="Unknown",Phase="Idle",
                    OccurredUtc=DateTime.UtcNow,Category=FactorySessionFaultCategory.ReadTimeout,Detail="unknown deadline"}));
                check(f.artLink=="SESSION_ERROR" && !f.status.Text.Contains("P0.4未取得"),"timeout without a known device source is not blamed on P0.4");
                f.ObserveIdleSessionFailure(new FactorySessionFaultException(new FactorySessionFault {SessionId="s",Device="Session",Phase="Cleanup",
                    OccurredUtc=DateTime.UtcNow,Category=FactorySessionFaultCategory.CleanupError,Detail="clear failed",PriorDetail="P0.4 low"}));
                check(f.status.Text.Contains("禁止自动重开") && !f.start.Enabled,"cleanup failure remains blocking with prior cause retained");
                check(f.emergency.Text.Contains("释放未确认") && !f.emergency.Text.Contains("已释放"),"cleanup failure must not look like confirmed release");
                f.ShowInTaskbar=false;f.Opacity=0;f.Show();System.Windows.Forms.Application.DoEvents();
                f.SaveImage(Path.Combine(root,"cleanup-fault.png"));
                f.tabs.SelectedTab=f.connectionPage;System.Windows.Forms.Application.DoEvents();
                f.SaveImage(Path.Combine(root,"fault-history.png"));f.Hide();
            }
            return "Session fault UI/context: "+n+" checks PASS; HardwareAccessed=False; "+root;
        }
    }
}
