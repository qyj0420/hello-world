using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // Standalone presentation and demonstration workflow. Never dispatches live HV commands.
    internal sealed class WithstandTestControl : UserControl
    {
        readonly WithstandSimulation model=new WithstandSimulation();
        readonly Timer timer=new Timer{Interval=100};
        readonly Stopwatch elapsed=new Stopwatch();
        readonly Label simulationNotice,statusLabel,remainingLabel,precheckLabel,modeCaption,phaseCaption,targetCaption,durationCaption;
        readonly ComboBox modeBox,phaseBox;
        readonly NumericUpDown targetBox,durationBox;
        readonly Button startButton,stopButton,waveTab,statisticsTab;
        readonly TableLayoutPanel chartTabs;
        readonly Panel optionPanel;
        readonly TableLayoutPanel root,body,charts;
        readonly TabControl statistics;
        readonly WithstandPlot voltagePlot,detailPlot;
        readonly Font uiFont,titleFont;
        readonly List<double> times=new List<double>(),volts=new List<double>(),leakage=new List<double>();
        bool syncing,disposed;
        bool showStatistics;
        int language;
        double lastElapsed;
        public event EventHandler StateChanged;
        public event EventHandler StartRequested;
        public event EventHandler StopRequested;
        public event EventHandler<WithstandInitialDacEventArgs> SimulationInitialDacCompleted;
        public bool SimulationOnly { get { return true; } }
        public bool IsRunning { get { return model.IsRunning; } }
        public string StateCode { get { return model.Stage.ToString(); } }
        public string LastErrorCode { get { return model.ErrorCode; } }
        public int LanguageIndex { get { return language; } set {if(value<0||value>=Xiongxin.OWTS.Localization.UiLanguage.Codes.Length)throw new ArgumentOutOfRangeException("value");language=value;ApplyLanguage();} }
        public double? InitialDacVoltageKv {get{return model.PrecheckVoltageKv;}}
        public bool InitialDacVoltageIsEstimated {get{return model.PrecheckVoltageIsEstimated;}}
        public bool SimulationInitialDacSucceeded {get{return model.InitialDacCompleted;}}
        public WithstandTestControl()
        {
            Name="WithstandTestControl";AutoScaleMode=AutoScaleMode.None;BackColor=AppTheme.Canvas;ForeColor=AppTheme.Ink;
            uiFont=AppTheme.Font(10,FontStyle.Regular);titleFont=AppTheme.Font(13,FontStyle.Bold);Font=uiFont;
            root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(8)};
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,52));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            simulationNotice=new Label{Name="WithstandSimulationNotice",Dock=DockStyle.Fill,Padding=new Padding(8,2,8,2),BackColor=Color.FromArgb(226,246,250),ForeColor=AppTheme.Ink,TextAlign=ContentAlignment.MiddleLeft};
            statusLabel=new Label{Name="WithstandStatus",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=AppTheme.Muted};
            body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,300));
            charts=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};charts.RowStyles.Add(new RowStyle(SizeType.Absolute,0));charts.RowStyles.Add(new RowStyle(SizeType.Percent,56));charts.RowStyles.Add(new RowStyle(SizeType.Percent,44));
            chartTabs=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};chartTabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));chartTabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));chartTabs.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            waveTab=AppTheme.Button("",false);waveTab.Name="WithstandWaveTab";waveTab.Dock=DockStyle.Fill;waveTab.Margin=Padding.Empty;
            statisticsTab=AppTheme.Button("",false);statisticsTab.Name="WithstandTrendTab";statisticsTab.Dock=DockStyle.Fill;statisticsTab.Margin=Padding.Empty;
            chartTabs.Controls.Add(waveTab,0,0);chartTabs.Controls.Add(statisticsTab,1,0);waveTab.Click+=(s,e)=>{showStatistics=false;ArrangePage();};statisticsTab.Click+=(s,e)=>{showStatistics=true;ArrangePage();};
            voltagePlot=new WithstandPlot{Dock=DockStyle.Fill,Font=uiFont};detailPlot=new WithstandPlot{Dock=DockStyle.Fill,Font=uiFont,IsDetail=true};
            statistics=new TabControl{Name="WithstandStatisticsTabs",Dock=DockStyle.Fill,Font=uiFont};
            foreach(string name in new[]{"Dissipation","Insulation","PartialDischarge","Faults"})statistics.TabPages.Add(new TabPage{Name=name,BackColor=AppTheme.Surface});
            statistics.SelectedIndexChanged+=(s,e)=>{statistics.SelectedTab.Controls.Add(detailPlot);RefreshPlots();};statistics.TabPages[0].Controls.Add(detailPlot);
            charts.Controls.Add(chartTabs,0,0);charts.Controls.Add(voltagePlot,0,1);charts.Controls.Add(statistics,0,2);
            optionPanel=new Panel{Name="WithstandOptions",Dock=DockStyle.Fill,BackColor=AppTheme.Surface,Margin=new Padding(8,0,0,0)};
            remainingLabel=new Label{Name="WithstandRemaining",Font=titleFont,TextAlign=ContentAlignment.MiddleCenter,BackColor=AppTheme.Canvas};
            precheckLabel=new Label{Name="WithstandPrecheck",ForeColor=AppTheme.Muted,TextAlign=ContentAlignment.MiddleLeft};
            modeCaption=Caption();phaseCaption=Caption();targetCaption=Caption();durationCaption=Caption();
            modeBox=new ComboBox{Name="WithstandMode",DropDownStyle=ComboBoxStyle.DropDownList,Font=uiFont};phaseBox=new ComboBox{Name="WithstandPhase",DropDownStyle=ComboBoxStyle.DropDownList,Font=uiFont};phaseBox.Items.AddRange(new object[]{"A","B","C","ALL"});phaseBox.SelectedIndex=0;
            targetBox=new NumericUpDown{Name="WithstandTargetKv",DecimalPlaces=1,Minimum=0,Maximum=1000,Increment=.1m,Font=uiFont};durationBox=new NumericUpDown{Name="WithstandDuration",Minimum=1,Maximum=86400,Value=60,Font=uiFont};
            startButton=AppTheme.Button("",true);startButton.Name="WithstandStart";stopButton=AppTheme.Button("",false);stopButton.Name="WithstandStop";stopButton.ForeColor=AppTheme.Danger;
            optionPanel.Controls.AddRange(new Control[]{remainingLabel,precheckLabel,modeCaption,phaseCaption,targetCaption,durationCaption,modeBox,phaseBox,targetBox,durationBox,startButton,stopButton});
            modeBox.SelectedIndexChanged+=(s,e)=>{if(!syncing&&modeBox.SelectedItem is ModeChoice){model.SetMode(((ModeChoice)modeBox.SelectedItem).Mode);RefreshState();}};
            phaseBox.SelectedIndexChanged+=(s,e)=>SyncInputs();targetBox.ValueChanged+=(s,e)=>SyncInputs();durationBox.ValueChanged+=(s,e)=>SyncInputs();
            startButton.Click+=(s,e)=>RequestStart();stopButton.Click+=(s,e)=>RequestStop();
            root.Controls.Add(simulationNotice,0,0);body.Controls.Add(charts,0,0);body.Controls.Add(optionPanel,1,0);root.Controls.Add(body,0,1);root.Controls.Add(statusLabel,0,2);Controls.Add(root);
            optionPanel.SizeChanged+=(s,e)=>ArrangeOptions();SizeChanged+=(s,e)=>ArrangePage();
            model.InitialDacCompletedSimulation+=(s,e)=>{var handler=SimulationInitialDacCompleted;if(handler!=null)handler(this,e);};
            timer.Tick+=(s,e)=>
            {
                double now=elapsed.Elapsed.TotalSeconds,step=now-lastElapsed;lastElapsed=now;
                model.Advance(step);
                // Synthetic positive capacitance is deliberately not measured or persisted.
                if(model.Stage==WithstandStage.InitialDac&&model.StageSeconds>=3)model.CompleteInitialDac(true,.2);
                times.Add(now);volts.Add(model.SimulationVoltageKv);leakage.Add(Math.Abs(model.SimulationVoltageKv)*.15);
                if(times.Count>1800){times.RemoveAt(0);volts.RemoveAt(0);leakage.RemoveAt(0);}
                RefreshState();RefreshPlots();if(!model.IsRunning){timer.Stop();elapsed.Stop();}
            };
            ApplyLanguage();ArrangePage();RefreshState();
        }
        Label Caption(){return new Label{Font=uiFont,TextAlign=ContentAlignment.MiddleLeft};}
        public void ConfigureCapabilities(bool supportsCosineVlf,bool supportsCombinedVlf)
        {model.ConfigureCapabilities(supportsCosineVlf,supportsCombinedVlf);RefreshModes();RefreshState();}
        public void SetContext(string projectId,string phase,double targetKv,int durationSeconds,double maximumVoltageKv)
        {
            model.SetContext(projectId,phase,targetKv,durationSeconds,maximumVoltageKv);syncing=true;
            try{phaseBox.SelectedItem=model.Phase;targetBox.Value=(decimal)Math.Max(0,Math.Min(1000,Double.IsNaN(targetKv)||Double.IsInfinity(targetKv)?0:targetKv));durationBox.Value=Math.Max(1,Math.Min(86400,durationSeconds));}
            finally{syncing=false;}RefreshState();
        }
        // Service-configured precheck for unlisted voltage ratings; not exposed to customers.
        public void SetInitialDacVoltage(double? voltageKv){if(IsRunning)throw new InvalidOperationException("test_running");model.ExplicitPrecheckKv=voltageKv;RefreshState();}
        void SyncInputs(){if(syncing||IsRunning)return;model.SetContext(model.ProjectId,Convert.ToString(phaseBox.SelectedItem), (double)targetBox.Value,(int)durationBox.Value,model.MaximumKv);RefreshState();}
        public bool RequestStart()
        {
            if(!model.Start()){RefreshState();return false;}
            times.Clear();volts.Clear();leakage.Clear();lastElapsed=0;elapsed.Restart();timer.Start();RefreshState();RefreshPlots();
            var requested=StartRequested;if(requested!=null)requested(this,EventArgs.Empty);return true;
        }
        public void RequestStop(){bool active=model.IsRunning;model.Stop();timer.Stop();elapsed.Stop();RefreshState();RefreshPlots();if(active){var requested=StopRequested;if(requested!=null)requested(this,EventArgs.Empty);}}
        void RefreshModes()
        {
            syncing=true;try{modeBox.Items.Clear();foreach(var mode in model.AvailableModes)modeBox.Items.Add(new ModeChoice{Mode=mode,Name=ModeName(mode)});for(int i=0;i<modeBox.Items.Count;i++)if(((ModeChoice)modeBox.Items[i]).Mode==model.Mode)modeBox.SelectedIndex=i;}finally{syncing=false;}
        }
        string L(string zh,string en){return Xiongxin.OWTS.Localization.UiLanguage.Text(language,zh,en);}
        string ModeName(WithstandMode mode){switch(mode){case WithstandMode.CosineVlf:return L("余弦超低频","Cosine VLF");case WithstandMode.SineVlf:return L("正弦超低频","Sine VLF");case WithstandMode.DirectCurrent:return L("直流耐压","DC withstand");default:return L("护套试验","Sheath test");}}
        void ApplyLanguage()
        {
            if(simulationNotice==null)return;
            simulationNotice.Text=L("仿真演示 · 不连接设备、不输出高压；演示数据不能作为试验或诊断结果。","SIMULATION ONLY · No device connection or HV output. Demonstration data are not test or diagnostic results.");
            modeCaption.Text=L("试验模式","Test mode");phaseCaption.Text=L("试验相别","Phase");targetCaption.Text=L("目标电压 / kV","Target voltage / kV");durationCaption.Text=L("耐压时间 / 秒","Duration / seconds");
            startButton.Text=L("开始仿真","Start simulation");stopButton.Text=L("停止","Stop");
            waveTab.Text=L("电压与漏电流","Voltage and leakage");statisticsTab.Text=L("统计趋势","Statistical trends");
            string[] labels={L("介损","Dissipation"),L("绝缘","Insulation"),L("局放","PD"),L("故障","Faults")};for(int i=0;i<labels.Length;i++)statistics.TabPages[i].Text=labels[i];
            RefreshModes();ArrangePage();RefreshState();RefreshPlots();
        }
        void ArrangePage()
        {
            if(root==null)return;
            root.RowStyles[0].Height=Math.Max(Font.Height*2+8,48);root.RowStyles[2].Height=Math.Max(Font.Height+10,34);
            int stopWidth=Math.Max(72,TextRenderer.MeasureText(stopButton.Text,stopButton.Font).Width+18);
            int buttonRowWidth=TextRenderer.MeasureText(startButton.Text,startButton.Font).Width+10+stopWidth+8+40;
            body.ColumnStyles[1].Width=Math.Max(260,Math.Min(ClientSize.Width-480,
                Math.Max(ClientSize.Width*.29f,buttonRowWidth)));
            bool compact=ClientSize.Height<580;chartTabs.Visible=compact;charts.SuspendLayout();
            charts.RowStyles[0].Height=compact?Math.Max(40,Font.Height+14):0;
            charts.RowStyles[1].SizeType=compact&&showStatistics?SizeType.Absolute:SizeType.Percent;charts.RowStyles[1].Height=compact?(showStatistics?0:100):56;
            charts.RowStyles[2].SizeType=compact&&!showStatistics?SizeType.Absolute:SizeType.Percent;charts.RowStyles[2].Height=compact?(showStatistics?100:0):44;
            voltagePlot.Visible=!compact||!showStatistics;statistics.Visible=!compact||showStatistics;
            waveTab.BackColor=showStatistics?AppTheme.Surface:AppTheme.Accent;waveTab.ForeColor=showStatistics?AppTheme.Ink:Color.White;
            statisticsTab.BackColor=showStatistics?AppTheme.Accent:AppTheme.Surface;statisticsTab.ForeColor=showStatistics?Color.White:AppTheme.Ink;charts.ResumeLayout(true);
            ArrangeOptions();
        }
        void ArrangeOptions()
        {
            if(optionPanel==null)return;int width=optionPanel.ClientSize.Width-20,x=10,y=6,line=Math.Max(27,Font.Height+4),field=Math.Max(32,Font.Height+8);
            bool compact=optionPanel.ClientSize.Height<520;
            remainingLabel.SetBounds(x,y,width,Math.Max(34,titleFont.Height+8));y=remainingLabel.Bottom+4;
            if(compact)
            {
                int label=Math.Max(105,Math.Min(width/2,170));
                modeCaption.Text=L("模式","Mode");phaseCaption.Text=L("相别","Phase");targetCaption.Text=L("目标 / kV","Target / kV");durationCaption.Text=L("时间 / 秒","Time / s");
                foreach(var pair in new[]{Tuple.Create(modeCaption,(Control)modeBox),Tuple.Create(phaseCaption,(Control)phaseBox),Tuple.Create(targetCaption,(Control)targetBox),Tuple.Create(durationCaption,(Control)durationBox)})
                {pair.Item1.SetBounds(x,y,label,line);pair.Item2.SetBounds(x+label,y,width-label,field);y+=field+3;}
            }
            else foreach(var pair in new[]{Tuple.Create(modeCaption,(Control)modeBox),Tuple.Create(phaseCaption,(Control)phaseBox),Tuple.Create(targetCaption,(Control)targetBox),Tuple.Create(durationCaption,(Control)durationBox)})
            {pair.Item1.SetBounds(x,y,width,line);y+=line;pair.Item2.SetBounds(x,y,width,field);y+=field+8;}
            precheckLabel.SetBounds(x,y,width,Math.Max(line*2,optionPanel.ClientSize.Height-y-field-20));
            int top=Math.Max(y+line*2+8,optionPanel.ClientSize.Height-field-10),gap=8,stop=Math.Max(72,TextRenderer.MeasureText(stopButton.Text,stopButton.Font).Width+18);
            startButton.SetBounds(x,top,width-stop-gap,field);stopButton.SetBounds(x+width-stop,top,stop,field);
        }
        void RefreshState()
        {
            if(statusLabel==null)return;
            remainingLabel.Text=L("剩余 ","Remaining ")+TimeSpan.FromSeconds(Math.Max(0,model.DurationSeconds-model.ElapsedSeconds)).ToString(@"hh\:mm\:ss");
            string precheck=model.RequiresDac?(model.InitialDacCompleted?L("本项目本相：模拟预检已完成","Simulation precheck completed for this project / phase"):
                model.PrecheckVoltageKv.HasValue?L("首次低压预检：","Initial low-voltage precheck: ")+model.PrecheckVoltageKv.Value.ToString("0.0",CultureInfo.InvariantCulture)+" kV":L("未配置适用的低压预检电压","Configure an applicable precheck voltage")):L("当前为直流 / 护套仿真模式","DC / sheath demonstration mode");
            if(model.PrecheckVoltageIsEstimated&&!model.InitialDacCompleted&&model.RequiresDac)precheck=L("预检测估算电压（非标准值）：","Estimated precheck (not a standard value): ")+model.PrecheckVoltageKv.Value.ToString("0.00",CultureInfo.InvariantCulture)+" kV";
            precheckLabel.Text=precheck+(model.CapacitanceUf.HasValue?Environment.NewLine+L("模拟电容量：","Synthetic capacitance: ")+model.CapacitanceUf.Value.ToString("0.000",CultureInfo.InvariantCulture)+" μF":"");
            string[] zh={"就绪","低压振荡波预检（仿真）","耐压运行（仿真）","仿真完成","已停止仿真","无法开始仿真"};string[] en={"Ready","Initial DAC precheck (simulation)","Withstand running (simulation)","Simulation completed","Simulation stopped","Simulation cannot start"};
            statusLabel.Text=L(zh[(int)model.Stage],en[(int)model.Stage])+(model.ErrorCode.Length>0?" · "+ErrorText(model.ErrorCode):"");
            startButton.Enabled=!IsRunning&&model.AvailableModes.Length>0;stopButton.Enabled=IsRunning;foreach(Control c in new Control[]{modeBox,phaseBox,targetBox,durationBox})c.Enabled=!IsRunning;
            var handler=StateChanged;if(handler!=null)handler(this,EventArgs.Empty);
        }
        string ErrorText(string code)
        {
            switch(code){case "project_required":return L("请先加载项目","Load a project first");case "phase_required":return L("请选择相别","Select a phase");case "mode_unavailable":return L("设备未配置耐压能力","Withstand capability is not configured");case "limit_required":return L("请先核对设备限压","Verify the device voltage limit");case "invalid_target":return L("目标电压必须大于零且不超过设备限压","Target must be positive and within the device voltage limit");case "invalid_duration":return L("试验时间必须为 1–86400 秒","Duration must be 1–86400 seconds");case "precheck_voltage_required":return L("此限压未配置低压预检电压","No low-voltage precheck is configured for this voltage rating");case "invalid_capacitance":return L("预检未获得有效电容量，不继续耐压","No valid precheck capacitance; withstand will not continue");default:return L("低压预检失败，不继续耐压","Initial DAC failed; withstand will not continue");}
        }
        void RefreshPlots()
        {
            if(voltagePlot==null)return;
            voltagePlot.Caption=L("电压与漏电流 · 仿真","Voltage and leakage current · Simulation");voltagePlot.TimeLabel=L("时间 / 秒","Time / s");voltagePlot.LeftLabel=L("电压 / kV","Voltage / kV");voltagePlot.RightLabel=L("漏电流 / mA","Leakage / mA");voltagePlot.SetData(times,volts,leakage);
            int tab=Math.Max(0,statistics.SelectedIndex);detailPlot.Caption=statistics.TabPages[tab].Text+L(" · 演示数据，不作诊断"," · Demonstration only, not diagnostic");detailPlot.TimeLabel=voltagePlot.TimeLabel;detailPlot.LeftLabel=tab==0?"%":tab==1?"MΩ":tab==2?"pC":"";
            detailPlot.EmptyMessage=tab==3?L("未接入实机故障监测；不能据此判断无故障。","Live fault monitoring is not connected; no fault-free conclusion can be drawn."):L("开始仿真后显示演示趋势","Start simulation to view a synthetic trend");
            detailPlot.SetData(times,tab==3?new List<double>():times.Select(t=>tab==0?.2+.02*Math.Sin(t*.6):tab==1?1500+30*Math.Sin(t*.2):Math.Max(0,Math.Sin(t*.7))*10).ToList(),null);
        }
        protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;timer.Stop();timer.Dispose();elapsed.Stop();}base.Dispose(disposing);if(disposing){uiFont.Dispose();titleFont.Dispose();}}
        sealed class ModeChoice{internal WithstandMode Mode;internal string Name;public override string ToString(){return Name;}}
    }

    internal sealed class WithstandPlot : Control
    {
        double[] times=new double[0],left=new double[0],right=new double[0];
        public string Caption="",TimeLabel="",LeftLabel="",RightLabel="",EmptyMessage="";
        public bool IsDetail;
        public WithstandPlot(){DoubleBuffered=true;BackColor=AppTheme.Surface;Margin=new Padding(0,0,0,6);}
        public void SetData(IList<double> t,IList<double> a,IList<double> b){times=t.ToArray();left=a.ToArray();right=b==null?new double[0]:b.ToArray();Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            int h=Font.Height;TextRenderer.DrawText(g,Caption,Font,new Rectangle(10,6,Math.Max(1,Width-20),h+4),AppTheme.Ink,TextFormatFlags.EndEllipsis);
            if(times.Length<2||left.Length<2){TextRenderer.DrawText(g,EmptyMessage.Length>0?EmptyMessage:"—",Font,new Rectangle(12,h+20,Math.Max(1,Width-24),Math.Max(1,Height-h-30)),AppTheme.Muted,TextFormatFlags.WordBreak|TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter);return;}
            var plot=new Rectangle(70,h*2+12,Math.Max(1,Width-(right.Length>0?148:92)),Math.Max(1,Height-h*4-28));
            int ticks=Math.Max(1,Math.Min(4,plot.Height/(h+3)));
            using(var pen=new Pen(AppTheme.Border)){for(int i=0;i<=ticks;i++){int y=plot.Top+plot.Height*i/ticks;g.DrawLine(pen,plot.Left,y,plot.Right,y);}g.DrawRectangle(pen,plot);}
            double maxTime=Math.Max(.1,times[times.Length-1]-times[0]),lMax=Math.Max(.1,left.Select(Math.Abs).Max()),rMax=right.Length>0?Math.Max(.1,right.Max()):1;
            for(int i=0;i<=ticks;i++){double value=lMax*(1-i*2d/ticks);TextRenderer.DrawText(g,value.ToString("0.##",CultureInfo.InvariantCulture),Font,new Rectangle(1,plot.Top+plot.Height*i/ticks-h/2,65,h),AppTheme.Muted,TextFormatFlags.Right|TextFormatFlags.NoPadding);}
            TextRenderer.DrawText(g,LeftLabel,Font,new Rectangle(8,h+8,Width/2,h),AppTheme.Success,TextFormatFlags.NoPadding);
            if(right.Length>0){TextRenderer.DrawText(g,RightLabel,Font,new Rectangle(Width/2,h+8,Width/2-8,h),AppTheme.Accent,TextFormatFlags.Right|TextFormatFlags.NoPadding);TextRenderer.DrawText(g,rMax.ToString("0.#",CultureInfo.InvariantCulture),Font,new Rectangle(plot.Right+5,plot.Top-h/2,65,h),AppTheme.Muted);}
            Action<double[],double,Color> curve=(values,max,color)=>{int count=Math.Min(times.Length,values.Length);if(count<2)return;var points=new PointF[count];for(int i=0;i<count;i++)points[i]=new PointF(plot.Left+(float)((times[i]-times[0])/maxTime*plot.Width),plot.Top+plot.Height/2-(float)(values[i]/max*plot.Height/2));using(var pen=new Pen(color,1.6f))g.DrawLines(pen,points);};
            curve(left,lMax,AppTheme.Success);if(right.Length>0)curve(right,rMax,AppTheme.Accent);
            TextRenderer.DrawText(g,times[0].ToString("0.#",CultureInfo.InvariantCulture),Font,new Rectangle(plot.Left,plot.Bottom+3,80,h),AppTheme.Muted,TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g,times[times.Length-1].ToString("0.#",CultureInfo.InvariantCulture),Font,new Rectangle(plot.Right-80,plot.Bottom+3,80,h),AppTheme.Muted,TextFormatFlags.Right|TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g,TimeLabel,Font,new Rectangle(plot.Left,Height-h-8,plot.Width,h),AppTheme.Ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
        }
    }
}
