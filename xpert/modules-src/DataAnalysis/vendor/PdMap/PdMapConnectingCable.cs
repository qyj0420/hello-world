using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        double connectingCableLengthM;
        bool reversePositionDisplay, inputGeometryAlreadyCompensated;
        CheckBox reversePositionBox;
        Label connectingCableLabel;
        NumericUpDown connectingCableInput;
        string connectingCableUiError="";
        internal string ConnectingCableUiError { get { return connectingCableUiError; } }
        internal bool connectingGeometryReload;
        internal event EventHandler ConnectingCableSettingsChanged;
        internal event EventHandler ConnectingCableUiFeedback;

        [Category("Cable"), DefaultValue(0.0)]
        [Description("连接电缆长度/m，仅设备端注入校准时设置；被测电缆始端注入请保持0。Connecting lead length in metres; set only for device-end calibration injection.")]
        public double ConnectingCableLengthM
        {
            get { return connectingCableLengthM; }
            set
            {
                if(!IsFinite(value)||value<0||value>1000000)throw new ArgumentOutOfRangeException("value");
                if(connectingCableLengthM.Equals(value))return;
                if(value>0&&ConnectingCableSettingsChanged==null)throw new ArgumentException("请在统一复核控件中设置连接线长度。 / Configure the lead on the unified review control.");
                // Finish a pending drag against the OLD geometry before notifying
                // the owner. A property write can arrive while mouse capture is active.
                CompleteReviewPreview();FlushReviewPreview();
                double old=connectingCableLengthM;connectingCableLengthM=value;
                try { var h=ConnectingCableSettingsChanged;if(h!=null)h(this,EventArgs.Empty); }
                catch { connectingCableLengthM=old;UpdateDisplayOptions();throw; }
                UpdateDisplayOptions();Invalidate();
            }
        }
        [Category("Cable"), DefaultValue(false)]
        [Description("输入矩阵的长度、波速、往返时间和位置已经剔除连接线时设为true，防止再次补偿。True only when incoming geometry is already lead-compensated.")]
        public bool InputGeometryAlreadyCompensated
        { get { return inputGeometryAlreadyCompensated; } set { inputGeometryAlreadyCompensated=value; } }

        [Category("Appearance"), DefaultValue(false)]
        [Description("仅反转位置显示为L-x，不改变传播方向、游标或原始结果。Display position as L-x; timing and propagation direction stay unchanged.")]
        public bool ReversePositionDisplay
        {
            get { return reversePositionDisplay; }
            set { if(reversePositionDisplay==value)return;reversePositionDisplay=value;UpdateDisplayOptions();NotifyApproximationDisplayChanged();Invalidate(); }
        }
        [Browsable(false)] public double PresentedLocationM { get { return PresentPosition(DisplayedLocationM); } }
        internal double PresentPosition(double physical)
        { return reversePositionDisplay&&IsFinite(physical)&&IsFinite(cableLengthM)?cableLengthM-physical:physical; }
        internal void BeginConnectingGeometryReload()
        { connectingGeometryReload=true;forceReloadNextInput=true; }
        internal void EndConnectingGeometryReload() { connectingGeometryReload=false; }
        internal void RestoreConnectingLengthWithoutNotification(double value) { connectingCableLengthM=value;UpdateDisplayOptions(); }
        internal double[] ReevaluateConnectingGeometryConfidence()
        {
            // Geometry has already been rebuilt from the retained cursor delay.
            // Re-measure confidence only: no snapping, charge compensation or edit event.
            UpdateConfidenceEvidence();reviewConfidenceSourceCode=1;
            lastReviewNotification=GetReviewParameterRow();lastReviewNotificationWasPreview=false;
            return new[]{correlation/100.0,confidence/100.0};
        }
        void RefreshConnectingPositionOnly()
        {
            ValidateReflectionSelectionWindow();
            if(!reflectionSelected){SetUnselectedReflectionAnalysis();return;}
            currentTimeDifferenceNs=Math.Abs(reflection-(double)incident)*dtNs;currentLocationM=Safe(originalParameterRow,13,double.NaN);
            // Configuration changes are not a new waveform analysis or amplitude measurement.
            compensatedCharge=Safe(originalParameterRow,16,compensatedCharge);
            correlation=IsFinite(Safe(originalParameterRow,21,double.NaN))?ToPercent(originalParameterRow[21]):double.NaN;
            confidence=IsFinite(Safe(originalParameterRow,33,double.NaN))?ToPercent(originalParameterRow[33]):double.NaN;
            NormalizeLoadedCoincidentConfidence();
            CountSamePositionPulses(currentLocationM);
        }

        void InitializeConnectingCableOptions()
        {
            reversePositionBox=CreateDisplayOption("ReversePositionDisplayCheckBox");
            // This control has its own handler; it must not enter curve-visibility logic.
            reversePositionBox.CheckedChanged-=DisplayOptionChanged;
            reversePositionBox.CheckedChanged+=(s,e)=>{if(!updatingDisplayOptions)ReversePositionDisplay=reversePositionBox.Checked;};
            connectingCableLabel=new Label { Name="ConnectingCableLengthLabel",Font=displayOptionsFont,BackColor=Color.Transparent,
                ForeColor=HVX.PdReview.ReviewTheme.Ink,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=false };
            connectingCableInput=new NumericUpDown { Name="ConnectingCableLengthInput",Font=displayOptionsFont,Minimum=0,Maximum=1000000,
                DecimalPlaces=2,Increment=.1M,ThousandsSeparator=false,Width=64,TabStop=true };
            connectingCableInput.ValueChanged+=(s,e)=>
            {
                if(updatingDisplayOptions)return;
                try
                {
                    ConnectingCableLengthM=(double)connectingCableInput.Value;
                    ReportConnectingCableUiFeedback("");
                }
                catch(Exception ex)
                {
                    if(ex is OutOfMemoryException||ex is StackOverflowException||ex is AccessViolationException||ex is System.Threading.ThreadAbortException)throw;
                    UpdateDisplayOptions();
                    ReportConnectingCableUiFeedback(ex.Message);
                    Invalidate();
                }
            };
            connectingCableInput.MouseLeave+=(s,e)=>displayOptionsToolTip.Hide(connectingCableInput);
            connectingCableInput.Leave+=(s,e)=>displayOptionsToolTip.Hide(connectingCableInput);
            Controls.Add(reversePositionBox);Controls.Add(connectingCableLabel);Controls.Add(connectingCableInput);
        }
        void SyncConnectingCableOptions()
        {
            if(reversePositionBox==null)return;
            reversePositionBox.Checked=reversePositionDisplay;
            // A positive lead forces mixed propagation, not a read-only length.
            connectingCableInput.Enabled=true;
            connectingCableInput.ReadOnly=false;
            connectingCableInput.Value=(decimal)connectingCableLengthM;
        }
        void ReportConnectingCableUiFeedback(string message)
        {
            connectingCableUiError=message??"";
            LastError=connectingCableUiError;
            var handler=ConnectingCableUiFeedback;
            if(handler!=null)handler(this,EventArgs.Empty);
            if(LastError.Length==0)
            {
                displayOptionsToolTip.Hide(connectingCableInput);
                return;
            }
            // A rejected edit restores the old value, with a visible explanation.
            // No modal message box: the operator can immediately correct the setting.
            ShowConnectingCableHint(connectingCableInput,LastError);
        }
        internal void ClearConnectingCableUiFeedback()
        {
            if(connectingCableInput!=null)displayOptionsToolTip.Hide(connectingCableInput);
            if(connectingCableUiError.Length==0)return;
            if(LastError==connectingCableUiError)LastError="";
            connectingCableUiError="";
            var handler=ConnectingCableUiFeedback;
            if(handler!=null)handler(this,EventArgs.Empty);
        }
        void ShowConnectingCableHint(Control target,string message)
        {
            if(target==null||target.IsDisposed||!target.IsHandleCreated||!target.Visible)return;
            displayOptionsToolTip.Show(message,target,new Point(0,-8),10000);
        }
        string AutomaticMixedCableHint()
        {
            return L("Mixed-cable calculation is automatic while Lead is greater than 0 m. Change the lead length below; set it to 0 m to restore manual mixed-cable selection.","连接线长度大于0 m时自动使用混合电缆计算。可直接修改下方连接线长度；设为0 m后可手动选择混合电缆。");
        }
        int ConnectingOptionWidth(int index)
        {
            Font f=displayOptionsFont??Font;
            if(index==5)return TextRenderer.MeasureText(L("Reverse","定位反转"),f,Size.Empty,TextFormatFlags.NoPadding).Width+Math.Max(30,f.Height+16);
            return TextRenderer.MeasureText(L("Lead / m","连接线 / m"),f,Size.Empty,TextFormatFlags.NoPadding).Width+ConnectingInputWidth+8;
        }
        int ConnectingInputWidth {get{return Math.Max(80,TextRenderer.MeasureText("1000.00",displayOptionsFont??Font).Width+SystemInformation.VerticalScrollBarWidth+8);}}
        void LayoutConnectingCableOptions(Rectangle[] rectangles,int top,ref Rectangle contentBounds)
        {
            if(reversePositionBox==null)return;
            reversePositionBox.Text=L("Reverse","定位反转");
            reversePositionBox.AccessibleName=L("Reverse displayed positions","定位显示反转");
            reversePositionBox.AccessibleDescription=L("Display L minus position. Does not change propagation direction or raw results.","按电缆长度减去位置进行显示，不改变传播方向和原始结果。");
            displayOptionsToolTip.SetToolTip(reversePositionBox,reversePositionBox.AccessibleDescription);
            Rectangle reverse=rectangles[5];reverse.Offset(0,top);reversePositionBox.Bounds=reverse;
            Rectangle lead=rectangles[6];lead.Offset(0,top);
            connectingCableLabel.Text=L("Lead / m","连接线 / m");
            int inputWidth=ConnectingInputWidth;
            connectingCableLabel.Bounds=new Rectangle(lead.X,lead.Y,Math.Max(1,lead.Width-inputWidth-8),lead.Height);
            connectingCableInput.Bounds=new Rectangle(lead.Right-inputWidth,lead.Y+1,inputWidth,connectingCableInput.PreferredSize.Height);
            string tip=L("Set this only when calibration is injected at the device end. For injection at the tested cable start, leave 0 m to avoid duplicate compensation.","当校准位置在设备端时方需设置该参数。若从被测电缆始端注入，请保持0 m，避免重复补偿。");
            connectingCableInput.AccessibleName=L("Connecting cable length in metres","连接电缆长度，单位米");
            connectingCableInput.AccessibleDescription=connectingCableLabel.AccessibleDescription=tip;
            displayOptionsToolTip.SetToolTip(connectingCableInput,tip);displayOptionsToolTip.SetToolTip(connectingCableLabel,tip);
            contentBounds=Rectangle.Union(Rectangle.Union(contentBounds,reverse),lead);
            reversePositionBox.BringToFront();connectingCableLabel.BringToFront();connectingCableInput.BringToFront();
        }
    }
}
