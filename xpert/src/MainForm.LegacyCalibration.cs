using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HVX.CalibrationStorage;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        internal Func<string,DialogResult> LegacyCalibrationReusePrompt { get; set; }
        private bool legacyCalibrationPromptActive;
        // Compatible saved decisions are reused silently. A readable old source
        // with unknown applicability needs one real, short operator decision.
        private bool TryLegacyCalibrationBeforeTest(string phase,out bool handled,Func<bool> phaseMatches=null)
        {
            handled=false;
            if(legacyCalibrationPromptActive){handled=true;return false;}
            string project=state.ProjectPath,identity=DeviceSetupStatus.ActualIdentity(state.Hardware);
            if(String.IsNullOrWhiteSpace(project)||!Directory.Exists(project)||identity.Length==0)return false;
            string[] pair=identity.Split('|');if(pair.Length!=3)return false;
            bool firstArrival=settings.TaskMinus3.UseFirstArrivalTiming;
            Func<bool> contextMatches=()=>!IsDisposed&&state.ProjectPath==project&&
                DeviceSetupStatus.ActualIdentity(state.Hardware)==identity&&
                settings.TaskMinus3.UseFirstArrivalTiming==firstArrival&&(phaseMatches==null||phaseMatches());
            try {
                var saved=PressureCalibrationSelection.Find(project,phase,firstArrival,pair[1],pair[2]);
                if(saved!=null&&contextMatches()){handled=true;return true;}
                if(!contextMatches())return false;
                var candidate=LegacyCalibrationBinding.FindCandidateForProject(project,phase);
                if(candidate==null||!contextMatches())return false;
                handled=true;legacyCalibrationPromptActive=true;
                string message=LegacyCalibrationChoiceText();
                DialogResult choice;
                try {choice=LegacyCalibrationReusePrompt!=null?LegacyCalibrationReusePrompt(message):
                    verificationMode?DialogResult.Cancel:ShowLegacyCalibrationReuseChoice(message);}
                finally {legacyCalibrationPromptActive=false;}
                if(!contextMatches())return false;
                if(choice==DialogResult.Retry) {
                    if(pressureStandalone)ShowRequirement(Localize(IsChinese?"请完成校准并保存后重新加载项目。":"Complete and save calibration, then reload the project."));
                    else if(CalibrationRequiredNavigation!=null)CalibrationRequiredNavigation();
                    else OpenModule(ModuleHost.CalibrationTest);
                    return false;
                }
                if(choice!=DialogResult.Yes)return false;
                LegacyCalibrationBinding.Confirm(project,candidate,phase,pair[1],pair[2],firstArrival,true,
                    "The operator selected Use existing calibration and confirmed that the saved project calibration applies to the current equipment and wiring.");
                if(!contextMatches())return false;
                saved=PressureCalibrationSelection.Find(project,phase,firstArrival,pair[1],pair[2]);
                return saved!=null&&contextMatches();
            }catch(Exception error)when(error is IOException||error is InvalidDataException||error is InvalidOperationException||error is ArgumentException||error is UnauthorizedAccessException) {
                if(!verificationMode)new Diagnostics().Error("LegacyCalibration","Legacy applicability could not be confirmed.",error);
                if(handled&&!verificationMode)ShowRequirement(Localize(IsChinese?"无法使用已有校准，请重新校准后再试。":"The saved calibration could not be used. Recalibrate, then try again."));
                return false;
            }
        }
        private string LegacyCalibrationChoiceText()
        {return Localize(IsChinese?"使用项目已有校准？请确认其适用于当前设备与接线。":"Use the saved project calibration? Confirm that it applies to the current equipment and wiring.");}
        private Form CreateLegacyCalibrationReuseDialog(string message)
        {
            var dialog=new Form {Text=Localize(IsChinese?"已有校准":"Saved calibration"),StartPosition=FormStartPosition.CenterParent,
                ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false,FormBorderStyle=FormBorderStyle.FixedDialog,
                AutoScaleMode=AutoScaleMode.Dpi,ClientSize=new Size(640,190),Font=new Font("Microsoft YaHei UI",10F)};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=2};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            dialog.Controls.Add(layout);
            layout.Controls.Add(new Label {Text=message,Dock=DockStyle.Fill,AutoSize=false,TextAlign=ContentAlignment.MiddleLeft},0,0);
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};layout.Controls.Add(buttons,0,1);
            var cancel=new HVX.Presentation.R2RoundedButton {Name="CancelLegacyCalibration",Text=Localize(IsChinese?"取消":"Cancel"),DialogResult=DialogResult.Cancel,Width=100,Height=38};
            var recalibrate=new HVX.Presentation.R2RoundedButton {Name="NewLegacyCalibration",Text=Localize(IsChinese?"重新校准":"New calibration"),DialogResult=DialogResult.Retry,Width=175,Height=38};
            var use=new HVX.Presentation.R2RoundedButton {Name="UseLegacyCalibration",Text=Localize(IsChinese?"使用已有校准":"Use existing calibration"),DialogResult=DialogResult.Yes,Width=210,Height=38,
                BackColor=Color.FromArgb(0,163,193),ForeColor=Color.White};
            buttons.Controls.Add(cancel);buttons.Controls.Add(recalibrate);buttons.Controls.Add(use);
            // Enter/Escape must not provide an accidental applicability approval.
            dialog.CancelButton=cancel;dialog.Shown+=(s,e)=>cancel.Select();
            return dialog;
        }
        private DialogResult ShowLegacyCalibrationReuseChoice(string message)
        {
            using(var icon=AppTheme.LoadIcon())using(var dialog=CreateLegacyCalibrationReuseDialog(message)) {
                if(icon!=null)dialog.Icon=icon;
                return dialog.ShowDialog(this);
            }
        }
    }
}
