using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private System.Windows.Forms.Timer connectionTimer;
        private Task idleConnectionTask;
        private CancellationTokenSource idleConnectionCancel;
        private int idleConnectionGeneration;
        private string idleConnectionIdentity="",connectionIdentity="";
        private DateTime artSeen=DateTime.MinValue,picoSeen=DateTime.MinValue,nextIdleAttempt=DateTime.MinValue;
        private DateTime idleStarted;
        private string artLink="WAIT",picoLink="WAIT";
        private string artStateDetail="";
        private bool workerConnectionOwned,connectionTransition,connectionInterrupted,connectionLogFailed;
        private bool connectionDisplayEnabled;
        private bool activeArtLinkFault,activePicoLinkFault;
        private FactoryConnectionJournal renderedConnectionJournal;
        private int renderedConnectionLineCount=-1;
        private FactoryConnectionJournal connectionJournal;
        private readonly TabPage connectionPage=new TabPage("连接与断线记录");
        private readonly TextBox connectionHistory=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false};
        private readonly Label connectionSummary=new Label {Dock=DockStyle.Top,Height=52};
        private readonly DataGridView connectionGrid=new DataGridView {Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells,SelectionMode=DataGridViewSelectionMode.FullRowSelect,BackgroundColor=Color.White};
        private readonly TextBox connectionLogLocation=new TextBox {Dock=DockStyle.Bottom,Height=46,ReadOnly=true,Multiline=true};
        private void InitializeConnectionDisplay(bool test)
        {
            connectionDisplayEnabled=true;
            emergency.Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold);
            string[] titles={"发生时间及时区","设备","ART序列号","Pico序列号","任务及轮次","任务阶段","事件","中断秒数","处理详情","原始记录"};
            int[] widths={178,65,100,100,180,150,120,85,330,210};
            for(int i=0;i<titles.Length;i++){connectionGrid.Columns.Add("C"+i,titles[i]);connectionGrid.Columns[i].Width=widths[i];connectionGrid.Columns[i].SortMode=DataGridViewColumnSortMode.NotSortable;connectionGrid.Columns[i].DefaultCellStyle.WrapMode=i==4 || i==5 || i>=8?DataGridViewTriState.True:DataGridViewTriState.False;}
            connectionPage.Controls.Add(connectionGrid);connectionPage.Controls.Add(connectionLogLocation);connectionPage.Controls.Add(connectionSummary);tabs.TabPages.Add(connectionPage);
            emergency.Cursor=Cursors.Hand;emergency.Click+=(s,e)=>tabs.SelectedTab=connectionPage;
            if(test)return;
            connectionTimer=new System.Windows.Forms.Timer {Interval=500};
            connectionTimer.Tick+=async(s,e)=>await ConnectionTick();
            Shown+=(s,e)=>connectionTimer.Start();
            FormClosing+=async(s,e)=>
            {
                if(idleConnectionTask!=null && !idleConnectionTask.IsCompleted)
                {e.Cancel=true;closeAfterStop=true;await SuspendConnectionMonitor();if(!busy && !scanning && !exporting)Close();}
            };
            Disposed+=(s,e)=>{connectionTimer.Stop();connectionTimer.Dispose();if(idleConnectionCancel!=null)idleConnectionCancel.Cancel();};
        }
        private void BindConnectionJournal(FactorySettings settings)
        {
            if(settings.Simulation)return;
            string identity=settings.Input.ArtSerial+"|"+settings.Input.PicoSerial;
            if(connectionJournal!=null && connectionIdentity==identity)return;
            connectionIdentity=identity;artSeen=picoSeen=DateTime.MinValue;artLink=picoLink="WAIT";connectionInterrupted=false;
            RefreshStartAvailability();
            try
            {
                connectionJournal=new FactoryConnectionJournal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Xiongxin","FactoryAcceptance","ConnectionLogs"),settings.Input.ArtSerial,settings.Input.PicoSerial);
                connectionGrid.Rows.Clear();
                connectionLogFailed=false;RefreshConnectionHistory();
            }
            catch(Exception ex){connectionLogFailed=true;status.Text="连接日志不能创建，禁止开始："+ex.Message;}
        }
        private async Task SuspendConnectionMonitor()
        {
            ++idleConnectionGeneration;
            var task=idleConnectionTask;var cancel=idleConnectionCancel;
            if(cancel!=null)cancel.Cancel();
            if(task!=null)try{await task;}catch(Exception){/* watcher error already displayed, not an output owner */}
            if(ReferenceEquals(idleConnectionTask,task)){idleConnectionTask=null;idleConnectionCancel=null;idleConnectionIdentity="";}
            if(cancel!=null)cancel.Dispose();
        }
        private async Task ConnectionTick()
        {
            if(IsDisposed)return;
            if(connectionTransition){RefreshStartAvailability();return;}
            RenderConnection();
            if(closeAfterStop || workerConnectionOwned || scanning || exporting || smokeMode || mode.SelectedIndex!=1 || loadingParameters)
            {
                if(idleConnectionTask!=null || (mode.SelectedIndex!=1 && FactoryResidentDevices.HasSession))
                {
                    connectionTransition=true;RefreshStartAvailability();
                    try { await SuspendConnectionMonitor();if(!workerConnectionOwned && mode.SelectedIndex!=1)await FactoryResidentDevices.CloseAsync(); }
                    catch(Exception ex) { status.Text="设备会话清理未确认："+ex.Message; }
                    finally { connectionTransition=false;RefreshStartAvailability(); }
                }
                return;
            }
            FactorySettings settings;
            try{settings=ConnectionWatchSettings();FactoryP04Diagnostic.Validate(settings.Input);if(String.IsNullOrWhiteSpace(settings.Input.PicoSerial))return;}
            catch{return;}
            string identity=settings.Input.ArtDevice+"|"+settings.Input.ArtSerial+"|"+settings.Input.PicoSerial+"|"+settings.Input.ArtLibraryDirectory+"|"+settings.Input.PicoLibraryDirectory;
            if(idleConnectionTask!=null && identity!=idleConnectionIdentity)
            {connectionTransition=true;RefreshStartAvailability();try{await SuspendConnectionMonitor();}finally{connectionTransition=false;RefreshStartAvailability();}return;}
            if(idleConnectionTask!=null && !idleConnectionTask.IsCompleted)
            {
                // A blocked input SDK must not leave a permanently green lamp.
                if(DateTime.UtcNow-idleStarted>TimeSpan.FromSeconds(10) && DateTime.UtcNow-artSeen>TimeSpan.FromSeconds(5))
                {
                    ObserveConnection("LINK_ART_ERROR",FactoryConnectionWatch.Stamp("只读监测超过5秒无有效更新；停止旧监测进程后重试"),false);
                    connectionTransition=true;RefreshStartAvailability();try{await SuspendConnectionMonitor();}finally{connectionTransition=false;RefreshStartAvailability();}
                }
                return;
            }
            if(DateTime.UtcNow<nextIdleAttempt)return;
            BindConnectionJournal(settings);if(connectionLogFailed)return;
            if(idleConnectionCancel!=null)idleConnectionCancel.Dispose();
            idleConnectionCancel=new CancellationTokenSource();idleConnectionIdentity=identity;idleStarted=DateTime.UtcNow;int generation=++idleConnectionGeneration;
            var token=idleConnectionCancel.Token;
            idleConnectionTask=RunIdleConnection(settings,generation,token);
        }
        private FactorySettings ConnectionWatchSettings()
        {
            // NumericUpDown.Value validates a pending edit. A heartbeat must never
            // call ReadSettings(), otherwise unfinished parameter text is committed
            // every 500 ms. The read-only worker only needs identity and libraries.
            var art=artChoice.SelectedItem as ArtDeviceIdentity;
            string pico=picoChoice.SelectedItem as string;
            if(pico!=null && (devices==null || !devices.PicoSerials.Contains(pico)))pico=null;
            bool simulation=mode.SelectedIndex==0;
            string module=art!=null?art.Name:!String.IsNullOrEmpty(parameterArtName)?parameterArtName:"Dev1";
            return new FactorySettings {Simulation=simulation,Input=new BenchSettings {
                RealHardware=!simulation,
                ArtDevice=module,ArtTrigger="/"+module+"/PFI0",
                ArtSerial=art!=null?art.Serial:pinnedArtSerial,PicoSerial=pico??pinnedPicoSerial,
                ArtLibraryDirectory=artLibrary.Text.Trim(),PicoLibraryDirectory=picoLibrary.Text.Trim() }};
        }
        private async Task RunIdleConnection(FactorySettings settings,int generation,CancellationToken token)
        {
            try
            {
                await Program.WatchConnection(settings,NewDirectory("CONNECTION"),(key,text)=>
                {
                    if(IsDisposed || !IsHandleCreated)return;
                    BeginInvoke(new Action(()=>{if(generation==idleConnectionGeneration && !token.IsCancellationRequested && settings.Input.ArtSerial==pinnedArtSerial && settings.Input.PicoSerial==pinnedPicoSerial)ObserveConnection(key,text,false);}));
                },token);
            }
            catch(Exception ex)
            {
                // Earlier snapshots (including CLOSED) must drain before the
                // typed terminal cause is shown, not overwrite it afterwards.
                if(IsHandleCreated && !IsDisposed)
                {var fence=new TaskCompletionSource<bool>();BeginInvoke(new Action(()=>fence.SetResult(true)));await fence.Task;}
                if(!token.IsCancellationRequested && generation==idleConnectionGeneration && !IsDisposed)ObserveIdleSessionFailure(ex);
            }
            finally{nextIdleAttempt=DateTime.UtcNow.AddSeconds(2);}
        }
        private bool ObserveConnection(string key,string detail,bool active)
        {
            if(key.StartsWith("P04_IDLE_",StringComparison.Ordinal))
            {
                try {if(connectionJournal!=null)connectionJournal.Event("ART",DateTime.UtcNow,key,detail);RefreshConnectionHistory();}
                catch(Exception ex){connectionLogFailed=true;status.Text="P0.4诊断日志写入失败："+ex.Message;}
                return true;
            }
            if(!key.StartsWith("LINK_",StringComparison.Ordinal))return false;
            if(key=="LINK_ART_NAME")
            {
                // Only idle exact-serial discovery may update the logical module name.
                if(!active && !workerConnectionOwned && !scanning && connectionJournal!=null)
                {
                    var selected=artChoice.SelectedItem as ArtDeviceIdentity;
                    if(selected==null && !String.IsNullOrEmpty(pinnedArtSerial))
                    {
                        selected=new ArtDeviceIdentity {Name=detail,Serial=pinnedArtSerial,Model="USB3135"};
                        if(devices==null)devices=new DiscoveryResult();
                        devices.ArtDevices.Add(selected);
                        applyingDiscovery=true;try{artChoice.Items.Add(selected);artChoice.SelectedItem=selected;}finally{applyingDiscovery=false;}
                        parameterArtName=detail;SelectArt();
                    }
                    if(selected!=null && selected.Serial==pinnedArtSerial && selected.Name!=detail)
                    {selected.Name=detail;parameterArtName=detail;artChoice.Refresh();artIdentity.Text=detail+" / "+selected.Serial;ScheduleParameterSave();}
                }
                return true;
            }
            string device=key.StartsWith("LINK_PICO_",StringComparison.Ordinal)?"Pico":"ART";
            string state=key.Substring(device=="ART"?9:10);
            DateTime at=DateTime.UtcNow;int split=(detail??"").IndexOf('|');DateTime parsed;
            if(split>0 && DateTime.TryParse(detail.Substring(0,split),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed))
            {at=parsed.ToUniversalTime();detail=detail.Substring(split+1);}
            if(state!="ERROR" && DateTime.UtcNow-at>TimeSpan.FromSeconds(2))return true; // delayed IPC is not live
            string previousState=device=="ART"?artLink:picoLink;
            if(device=="ART"){artLink=state;artSeen=at;artStateDetail=detail??"";}else{picoLink=state;picoSeen=at;}
            if(!active && device=="Pico" && state=="ONLINE" && (devices==null || !devices.PicoSerials.Contains(picoChoice.SelectedItem as string)) && !String.IsNullOrEmpty(pinnedPicoSerial))
            {
                if(devices==null)devices=new DiscoveryResult();
                if(!devices.PicoSerials.Contains(pinnedPicoSerial))devices.PicoSerials.Add(pinnedPicoSerial);
                applyingDiscovery=true;
                try{if(!picoChoice.Items.Contains(pinnedPicoSerial))picoChoice.Items.Add(pinnedPicoSerial);picoChoice.SelectedItem=pinnedPicoSerial;}
                finally{applyingDiscovery=false;}
                SelectPico();
            }
            try
            {
                if(state=="ERROR" || state=="TIMEOUT")
                {
                    connectionInterrupted=true;
                    if(connectionJournal!=null)connectionJournal.Fault(device,at,detail,state=="TIMEOUT"?"P0.4读取超时，许可未知":"通信中断/读取异常");
                    if(active || busy && sequenceProgress!=null && sequenceProgress.Running)
                    {
                        if(device=="ART")activeArtLinkFault=true;else activePicoLinkFault=true;
                        if(sequenceProgress!=null)sequenceProgress.Observe("CONNECTION_LOST");
                        if(cancellation!=null)cancellation.Cancel();
                        feedbackTrend.SetLive(false);pressureTrend.SetLive(false);
                    }
                }
                else if(device=="ART" && state=="LOW" && previousState!="LOW")
                {
                    if(connectionJournal!=null)connectionJournal.Event("ART",at,"P0.4有效低电平，许可不满足",detail);
                    if(active || busy && sequenceProgress!=null && sequenceProgress.Running)
                    {if(sequenceProgress!=null)sequenceProgress.Observe("EMERGENCY_ACTIVE");if(cancellation!=null)cancellation.Cancel();}
                }
                else if(!active && connectionJournal!=null && (state=="HIGH" || state=="LOW" || state=="ONLINE"))connectionJournal.Online(device,at);
                RefreshConnectionHistory();
            }
            catch(Exception ex){connectionLogFailed=true;status.Text="连接日志写入失败："+ex.Message;if(active && cancellation!=null)cancellation.Cancel();}
            RenderConnection();return true;
        }
        private void RenderConnection()
        {
            RefreshStartAvailability();
            if(!connectionDisplayEnabled || mode.SelectedIndex!=1)return;
            bool artFresh=DateTime.UtcNow-artSeen<TimeSpan.FromSeconds(2),picoFresh=DateTime.UtcNow-picoSeen<TimeSpan.FromSeconds(3);
            string a=artFresh && artLink=="HIGH"?"在线 · P0.4 高":artFresh && artLink=="LOW"?"P0.4 低 · 许可不满足":artLink=="LOW_LATCHED"?"P0.4 低已锁存 · 会话关闭":artLink=="TIMEOUT"?"P0.4超时 · 许可未知":artLink=="UNKNOWN"?"P0.4无有效值 · 正在复核":artLink=="ERROR"?"通信中断/读取异常":artLink=="CLOSED"?"本轮连接已释放":"检测中/状态过期";
            string p=picoFresh && picoLink=="BUSY"?"Pico 忙 · 等待触发/采集":picoFresh && picoLink=="ONLINE"?(workerConnectionOwned?"Pico 在线":"Pico 已识别"):picoLink=="ERROR"?"Pico 通信异常":picoLink=="CLOSED"?"Pico 连接切换中":"Pico 待检测";
            if(artLink=="CLEANUP_FAILED")a="任务释放未确认";
            if(picoLink=="CLEANUP_FAILED")p="设备清理失败，禁止重开";
            if(artLink=="SESSION_ERROR")a="会话异常 · 原因未确定";
            bool online=artFresh && picoFresh && artLink=="HIGH" && picoLink=="ONLINE";
            bool latched=workerConnectionOwned && (activeArtLinkFault || activePicoLinkFault);
            string text="ART "+a+"\n"+p+"\n"+(artFresh?artSeen.ToLocalTime().ToString("HH:mm:ss"):"无实时值")+(latched?" 本轮中断锁定":emergencyNotice.Active?" 急停本轮锁存":connectionInterrupted && online?" 恢复后待操作":"");
            if(emergency.Text!=text)emergency.Text=text;
            pulseTip.SetToolTip(emergency,artLink=="UNKNOWN"?artStateDetail+"\n历史电平不代表当前许可。":status.Text);
            bool bad=latched || emergencyNotice.Active || artLink=="ERROR" || artLink=="TIMEOUT" || artLink=="LOW_LATCHED" || artLink=="CLEANUP_FAILED" || artLink=="SESSION_ERROR" || picoLink=="ERROR" || artFresh && artLink=="LOW";
            Color background=bad?Color.MistyRose:online?Color.FromArgb(196,232,208):Color.Gainsboro;
            Color foreground=bad?Color.DarkRed:Color.DarkSlateGray;
            if(emergency.BackColor!=background)emergency.BackColor=background;
            if(emergency.ForeColor!=foreground)emergency.ForeColor=foreground;
            if(workerConnectionOwned && stageSettings!=null && !stageSettings.Simulation && stageSettings.TargetKv>0 && artSeen!=DateTime.MinValue && !artFresh && artLink!="ERROR" && artLink!="CLOSED")
                ObserveConnection("LINK_ART_ERROR",FactoryConnectionWatch.Stamp("监测事件超过2秒未更新；控制通信状态未知，请求停止"),true);
        }
        private void RefreshConnectionHistory()
        {if(connectionJournal!=null){
            // Healthy heartbeats are not journal entries. Avoid rebuilding the
            // entire text/grid (and invalidating unrelated layout) for every one.
            if(!ReferenceEquals(renderedConnectionJournal,connectionJournal) || renderedConnectionLineCount!=connectionJournal.Lines.Count)
            {
                renderedConnectionJournal=connectionJournal;renderedConnectionLineCount=connectionJournal.Lines.Count;
                string history=connectionJournal.Describe();
                if(connectionHistory.Text!=history)connectionHistory.Text=history;
            }
            while(connectionGrid.Rows.Count<connectionJournal.Lines.Count)connectionGrid.Rows.Add(connectionJournal.Lines[connectionGrid.Rows.Count].Split('\t'));
            string location="本机日志："+connectionJournal.FilePath;
            if(connectionLogLocation.Text!=location)connectionLogLocation.Text=location;
            string summary=
            workerConnectionOwned && (activeArtLinkFault || activePicoLinkFault)
            ?"通信异常：已请求本轮/队列停止，等待原工作进程关断与退出；重连不自动加压。"
            :"通信事件 "+connectionJournal.Incidents+" 次；记录按ART/Pico序列号绑定。时间为主机时钟；通信恢复不代表已放电。";
            if(connectionSummary.Text!=summary)connectionSummary.Text=summary;}}
        private void LogConnectionResult(string detail)
        {
            if(connectionJournal==null || !connectionInterrupted)return;
            try
            {
                connectionJournal.Result(detail);RefreshConnectionHistory();
                if(!String.IsNullOrEmpty(connectionJournal.Record) && Directory.Exists(connectionJournal.Record))
                    File.Copy(connectionJournal.FilePath,Path.Combine(connectionJournal.Record,"connection-events.tsv"),true);
            }
            catch(Exception ex){connectionLogFailed=true;status.Text="连接日志未保存："+ex.Message;}
        }
    }
}
