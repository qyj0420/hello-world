using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        Label bottomStatus;
        Panel bottomStatusHost;
        Control statusHoverControl;
        string operationStatus="";
        [Browsable(false)] public string StatusBarText {get{return bottomStatus==null?"":bottomStatus.Text;}}
        int StatusBarHeight {get{return bottomStatusHost==null?0:bottomStatusHost.Height;}}

        int PreferredStatusBarHeight(bool compact)
        {
            if(bottomStatusHost==null)return compact?24:26;
            int textHeight=Math.Max(TextRenderer.MeasureText("Ag",bottomStatus.Font).Height,
                TextRenderer.MeasureText("Ag",reviewBuildLabel.Font).Height);
            return Math.Max(compact?24:26,textHeight+bottomStatusHost.Padding.Vertical+3);
        }

        void InitializeReviewStatusBar()
        {
            bottomStatusHost=new Panel{Name="ReviewStatusBar",Dock=DockStyle.Bottom,Height=24,BackColor=ReviewTheme.SurfaceAlt,Padding=new Padding(8,2,8,2)};
            bottomStatus=new Label{Name="ReviewStatusBarText",Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,ForeColor=ReviewTheme.SecondaryInk};
            bottomStatusHost.Controls.Add(bottomStatus);
            bottomStatusHost.Controls.Add(new Panel{Dock=DockStyle.Top,Height=1,BackColor=ReviewTheme.Border});
            reviewBuildLabel.Dock=DockStyle.Right;reviewBuildLabel.TextAlign=ContentAlignment.MiddleRight;
            reviewBuildLabel.Width=Math.Max(156,TextRenderer.MeasureText(reviewBuildLabel.Text,Font).Width+12);
            bottomStatusHost.Controls.Add(reviewBuildLabel);
            // Dock the full-width divider first, then the version, then the
            // remaining status text. Long guidance never covers the version.
            bottomStatusHost.Controls.SetChildIndex(reviewBuildLabel,1);
            workspaceCanvas.Controls.Add(bottomStatusHost);
            spectrum.UseHostStatusBar=true;
            HookStatusHelp(workspaceCanvas);
            CurrentPdMapChanged+=(s,e)=>SetReviewOperationStatus(selected<0?"":L("Selected PDmap: ","当前 PDmap：")+pdmap.CurrentPdMapId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            spectrum.RegionStatisticsChanged+=(s,e)=>SetReviewOperationStatus(spectrum.RegionStatisticsActive?spectrum.RegionStatisticsSummary:L("Regional statistics closed; showing all data under current filters.","已退出区域统计，显示当前过滤条件下的全部数据。"));
            spectrum.RegionActionCompleted+=(s,e)=>SetReviewOperationStatus(spectrum.LastInteractionStatus);
            ChartReportStatusChanged+=(s,e)=>SetReviewOperationStatus(ChartReportStatusMessage);
            AnalysisCompleted+=(s,e)=>SetReviewOperationStatus(AnalysisStatus);
            RefreshBottomStatus();
            InitializeOperationJournal();
        }
        void HookStatusHelp(Control control)
        {
            if(control==bottomStatusHost)return;
            control.MouseEnter+=(s,e)=>{statusHoverControl=(Control)s;RefreshBottomStatus();};
            control.MouseLeave+=(s,e)=>{if(statusHoverControl==s){statusHoverControl=null;RefreshBottomStatus();}};
            control.Enter+=(s,e)=>{statusHoverControl=(Control)s;RefreshBottomStatus();};
            control.Leave+=(s,e)=>{if(statusHoverControl==s){statusHoverControl=null;RefreshBottomStatus();}};
            control.ControlAdded+=(s,e)=>HookStatusHelp(e.Control);
            foreach(Control child in control.Controls)HookStatusHelp(child);
        }
        void SetReviewOperationStatus(string text)
        {operationStatus=text??"";RecordOperation("Status",operationStatus+"; "+OperationContext());RefreshBottomStatus();}
        void RefreshBottomStatus()
        {
            if(bottomStatus==null)return;
            string versionHint=L("Current control build; double-click for diagnostics.","当前控件实际版本；双击查看诊断。");
            if(reviewBuildLabel.AccessibleDescription!=versionHint)
            {
                reviewBuildLabel.AccessibleDescription=versionHint;
                if(analysisDiagnosticsTip!=null)analysisDiagnosticsTip.SetToolTip(reviewBuildLabel,reviewBuildLabel.Text+"\n"+versionHint);
            }
            string help=statusHoverControl==null||statusHoverControl.IsDisposed?"":ReviewControlHelp(statusHoverControl);
            string value=analysisOutcomeWarning.Length>0?analysisOutcomeWarning:help.Length>0?help:operationStatus;
            if(value.Length==0)value=L("Ready — point to a control for guidance.","就绪 — 将鼠标移至控件可查看操作提示。");
            string notice=AlgorithmReviewNotice;
            if(notice.Length>0)value=notice+"  |  "+value;
            if(bottomStatus.Text!=value)
            {
                bottomStatus.Text=value;
                if(analysisDiagnosticsTip!=null)analysisDiagnosticsTip.SetToolTip(bottomStatus,value);
            }
            bottomStatus.AccessibleDescription=value;
        }
        string ReviewControlHelp(Control control)
        {
            if(control==locationOverview)return L("Drag to zoom; wheel to zoom and show T1/T2; drag cursors; right-click to reset.","左键框选放大；滚轮缩放并显示 T1/T2；拖动游标；右键恢复。");
            if(control==pdmap)return L("Double-click to select a reflection; drag incident/reflection cursors; wheel to zoom; right-click to reset.","双击选取反射波；拖动入射/反射游标；滚轮缩放；右键恢复。");
            if(control==spectrum)return spectrum.RegionSelectionEnabled?L("Drag a region; right-click for regional statistics; wheel: X; Shift+wheel: Y; double-click: select pulse.","拖动选区；右键统计所选区域或退出区域统计；滚轮：横轴；Shift+滚轮：纵轴；双击：选中脉冲。"):
                spectrum.DisplayModeIndex>=5&&spectrum.DisplayModeIndex<=8?L("Use the chart-view buttons to change projection; drag in 3D to rotate; wheel to zoom.","使用视图按钮切换投影；3D 视图按住左键旋转；滚轮缩放。"):
                L("Wheel to zoom; double-click to inspect a pulse; use phase buttons to change visible data.","滚轮缩放；双击查看脉冲；使用相别按钮切换显示数据。");
            if(control==analysisTree||IsWithin(control,analysisTree))return L("Click a MAP name to inspect its waveform; the status icon changes acceptance.","点击 MAP 名称查看对应波形；点击状态图标更改接受状态。");
            if((control==automaticAnalysisButton||control==manualAnalysisButton)&&AnalysisStartBlockReason.Length>0)return AnalysisStartBlockReason;
            if(control==automaticAnalysisButton)return L("Analyse checked files. Cancel finishes the current file and keeps completed results.","分析勾选的文件；取消时完成当前文件，并保留已经完成的结果。");
            if(control==manualAnalysisButton)return L("Analyse checked files using the current analysis settings.","按当前分析设置处理勾选的文件。");
            if(control==statisticsEnabledBox||control==statisticsSlider)return L("Changes statistical visibility; does not delete original waveform data.","调整统计显示范围，不删除原始波形数据。");
            if(control==analysisSettingsButton)return L("Analysis settings; shared parameters are managed in the application settings.","分析设置；共用参数请在主程序设置中调整。");
            if(!string.IsNullOrEmpty(control.AccessibleDescription))return control.AccessibleDescription;
            if(control is ButtonBase||control is ComboBox||control is UpDownBase||control is TrackBar)
                return !string.IsNullOrEmpty(control.AccessibleName)?control.AccessibleName:control.Text;
            return "";
        }
        static bool IsWithin(Control child,Control parent)
        {if(parent==null)return false;for(Control p=child;p!=null;p=p.Parent)if(p==parent)return true;return false;}
    }
}
