using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        Panel unifiedTitleBar;
        Label unifiedModuleTitle;
        Font unifiedTitleFont,unifiedSectionFont;
        bool embeddedMode;

        [Category("Appearance"),DefaultValue(false)]
        public bool EmbeddedMode
        {
            get { return embeddedMode; }
            set
            {
                if(InvokeRequired){Invoke(new Action<bool>(v=>EmbeddedMode=v),value);return;}
                if(embeddedMode==value)return;
                embeddedMode=value;
                if(unifiedTitleBar!=null)
                {
                    unifiedTitleBar.Visible=!value;
                    unifiedTitleBar.Height=value?0:32;
                }
                if(layoutReady){UpdateMinimum();ApplySplitLayout();}
            }
        }
        void InitializeUnifiedAppearance()
        {
            unifiedTitleFont=new Font(Font.FontFamily,12,FontStyle.Bold);
            unifiedSectionFont=new Font(Font.FontFamily,10.5f,FontStyle.Bold);
            Disposed+=(s,e)=>{unifiedTitleFont.Dispose();unifiedSectionFont.Dispose();};
            unifiedTitleBar=new Panel{Name="ReviewUnifiedTitleBar",Dock=DockStyle.Top,Height=32,BackColor=ReviewTheme.TopBar,Padding=new Padding(12,0,12,0)};
            var module=new Label{Name="ReviewModuleTitle",Dock=DockStyle.Fill,Text=L("Partial discharge analysis","局部放电分析"),Font=unifiedTitleFont,ForeColor=Color.White,TextAlign=ContentAlignment.MiddleLeft};
            unifiedModuleTitle=module;
            var version=new Label{Name="ReviewTitleVersion",Dock=DockStyle.Right,Width=250,Text=ControlBuildLabel,ForeColor=Color.White,TextAlign=ContentAlignment.MiddleRight};
            unifiedTitleBar.Controls.Add(module);unifiedTitleBar.Controls.Add(version);workspaceCanvas.Controls.Add(unifiedTitleBar);
            workspaceHeader.Height=36;workspaceHeader.BackColor=ReviewTheme.SurfaceAlt;workspaceHeader.Padding=new Padding(12,4,12,4);
            workspaceCanvas.BackColor=ReviewTheme.Workspace;
            analysisTree.Font=Font;analysisTree.BackColor=ReviewTheme.Surface;analysisTree.ForeColor=ReviewTheme.Ink;
            workspaceTreePanel.Paint+=(s,e)=>ControlPaint.DrawBorder(e.Graphics,workspaceTreePanel.ClientRectangle,ReviewTheme.Border,ButtonBorderStyle.Solid);
            outer.SplitterWidth=8;charts.SplitterWidth=8;
            side.BackColor=actions.BackColor=archiveActions.BackColor=ReviewTheme.Surface;
            side.Padding=new Padding(12);actions.Padding=new Padding(12,8,12,4);archiveActions.Padding=new Padding(12,0,12,12);
            // The learning controls add legitimate settings content. Keep the
            // fixed action area stable and make the settings region locally
            // scrollable when compact or high-DPI hosts cannot show it all.
            side.AutoScroll=true;actions.AutoScroll=false;
            foreach(Control child in side.Controls)child.Width=208;
            analysisSettingsButton.Width=204;analysisSettingsButton.Height=28;analysisSettingsButton.Font=unifiedSectionFont;
            ReviewTheme.ButtonRole(analysisSettingsButton,false);analysisSettingsButton.BackColor=ReviewTheme.SurfaceAlt;
            filterChoices[0].Parent.Width=208;
            filterParametersHost.Width=bandParameters.Width=waveletParameters.Width=208;
            foreach(var page in new[]{bandParameters,waveletParameters})foreach(Control row in page.Controls){row.Width=208;row.Height=28;row.Margin=Padding.Empty;}
            foreach(var slider in new[]{highPassSlider,lowPassSlider,levelsSlider,ignoreSlider})
            {
                var row=slider.Parent;slider.SetBounds(158,2,50,24);
                foreach(Control input in row.Controls)if(input is NumericUpDown)input.SetBounds(90,2,64,input.Height);
            }
            wavelet.SetBounds(90,2,118,wavelet.Height);
            wavelet.Parent.Height=Math.Max(28,wavelet.Height+2);
            statisticsSlider.Width=208;statisticsSlider.Height=22;statisticsEnabledBox.Margin=Padding.Empty;strengthLabel.Margin=Padding.Empty;
            foreach(Control child in actions.Controls)child.Width=208;
            for(int i=0;i<metricLabels.Length;i++){metricLabels[i].Width=100;metricValues[i].Left=106;metricValues[i].Width=68;metricUnits[i].Left=180;metricUnits[i].Width=28;}
            foreach(var badge in classIndicators){badge.Width=66;badge.Margin=new Padding(1);}
            previousButton.Width=nextButton.Width=100;
            compactDecisionRow=new FlowLayoutPanel{Name="ReviewCompactDecisionRow",Width=208,Height=28,Margin=Padding.Empty,WrapContents=false};
            compactDecisionRow.Controls.Add(acceptButton);compactDecisionRow.Controls.Add(deleteButton);compactDecisionRow.Controls.Add(uncertainButton);compactDecisionRow.Controls.Add(rangeFilterButton);
            acceptButton.Width=deleteButton.Width=uncertainButton.Width=rangeFilterButton.Width=65;
            actions.Controls.Add(compactDecisionRow);actions.Controls.SetChildIndex(compactDecisionRow,3);
            // Three archive operations no longer fit in one 208 px row. Keep
            // load/save paired and place the generated conclusion on a full
            // second row so every language remains inside the fixed sidebar.
            archiveActions.FlowDirection=FlowDirection.LeftToRight;archiveActions.WrapContents=true;archiveActions.Height=70;
            evaluateButton.Width=reportButton.Width=100;
            side.Paint+=(s,e)=>ControlPaint.DrawBorder(e.Graphics,side.ClientRectangle,ReviewTheme.Border,ButtonBorderStyle.Solid);
            actions.Parent.Paint+=(s,e)=>ControlPaint.DrawBorder(e.Graphics,actions.Parent.ClientRectangle,ReviewTheme.Border,ButtonBorderStyle.Solid);
            UpdateWorkspaceActionHeight();
            InitializeCompactTabs();
        }
    }
}
