using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool compactWorkspace;
        FlowLayoutPanel compactDecisionRow;
        Panel compactChartTabs,compactSideTabs;
        Panel compactWaveHost,compactSpectrumHost;
        Button compactWaveButton,compactSpectrumButton,compactFilterButton,compactActionButton,compactSetupButton;
        bool compactWaveSelected=true,compactFilterSelected=true,compactSetupSelected;

        void InitializeCompactTabs()
        {
            compactChartTabs=new Panel{Name="ReviewCompactChartTabs",Dock=DockStyle.Top,Height=28,Visible=false,BackColor=ReviewTheme.SurfaceAlt};
            compactWaveButton=CompactTab("ReviewCompactWaveform",0,compactChartTabs,()=>SelectCompactChart(true));
            compactSpectrumButton=CompactTab("ReviewCompactSpectrum",108,compactChartTabs,()=>SelectCompactChart(false));
            workspaceRightPanel.Controls.Add(compactChartTabs);
            compactSideTabs=new Panel{Name="ReviewCompactSideTabs",Dock=DockStyle.Top,Height=28,Visible=false,BackColor=ReviewTheme.SurfaceAlt};
            compactFilterButton=CompactTab("ReviewCompactFilters",0,compactSideTabs,()=>SelectCompactSidebar(true));
            compactActionButton=CompactTab("ReviewCompactActions",112,compactSideTabs,()=>SelectCompactSidebar(false));
            compactSetupButton=CompactTab("ReviewCompactSetup",224,compactSideTabs,()=>SelectCompactSidebar(true,true));
            outer.Panel2.Controls.Add(compactSideTabs);
            compactWaveHost=new Panel{Name="ReviewCompactWaveHost",Dock=DockStyle.Fill,Visible=false};
            compactSpectrumHost=new Panel{Name="ReviewCompactSpectrumHost",Dock=DockStyle.Fill,Visible=false};
            outer.Panel1.Controls.Add(compactWaveHost);outer.Panel1.Controls.Add(compactSpectrumHost);
            UpdateCompactTabLanguage();UpdateCompactTabColors();
        }
        static Button CompactTab(string name,int x,Panel parent,Action click)
        {
            var button=ActionButton(104);button.Name=name;button.SetBounds(x+3,2,104,24);button.Margin=Padding.Empty;
            button.Click+=(s,e)=>click();parent.Controls.Add(button);return button;
        }
        void UpdateCompactTabLanguage()
        {
            if(compactWaveButton==null)return;
            compactWaveButton.Text=L("Waveform","波形");compactSpectrumButton.Text=L("Spectrum","谱图");
            compactFilterButton.Text=L("Filters","筛选");compactActionButton.Text=L("Actions","操作");
            compactSetupButton.Text=L("Settings","设置");
        }
        void UpdateCompactTabColors()
        {
            if(compactWaveButton==null)return;
            foreach(var pair in new[]{new{Button=compactWaveButton,Active=compactWaveSelected},new{Button=compactSpectrumButton,Active=!compactWaveSelected},
                new{Button=compactFilterButton,Active=compactFilterSelected&&!compactSetupSelected},new{Button=compactActionButton,Active=!compactFilterSelected},new{Button=compactSetupButton,Active=compactFilterSelected&&compactSetupSelected}})
            {pair.Button.BackColor=pair.Active?ReviewTheme.Active:ReviewTheme.Surface;pair.Button.ForeColor=pair.Active?Color.White:ReviewTheme.Ink;}
        }
        void SelectCompactChart(bool waveform)
        {
            compactWaveSelected=waveform;UpdateCompactTabColors();
            if(!compactWorkspace)return;
            EnsureCompactChartHosts();
            compactWaveHost.Visible=waveform;compactSpectrumHost.Visible=!waveform;
            if(waveform)compactWaveHost.BringToFront();else compactSpectrumHost.BringToFront();
            ApplySplitLayout();
        }
        void EnsureCompactChartHosts()
        {
            if(compactWaveHost==null)return;
            if(compactWorkspace)
            {
                if(pdmap.Parent!=compactWaveHost){compactWaveHost.Controls.Add(pdmap);compactWaveHost.Controls.Add(emptyWave);compactWaveHost.Controls.Add(overviewHost);compactSpectrumHost.Controls.Add(spectrum);}
                charts.Visible=false;
                compactWaveHost.Visible=compactWaveSelected;compactSpectrumHost.Visible=!compactWaveSelected;
            }
            else
            {
                if(pdmap.Parent!=charts.Panel1){charts.Panel1.Controls.Add(pdmap);charts.Panel1.Controls.Add(emptyWave);charts.Panel1.Controls.Add(overviewHost);charts.Panel2.Controls.Add(spectrum);}
                compactWaveHost.Visible=compactSpectrumHost.Visible=false;charts.Visible=true;
            }
        }
        void SelectCompactSidebar(bool filters,bool settings=false)
        {
            compactFilterSelected=filters;compactSetupSelected=settings;UpdateCompactTabColors();
            if(!compactWorkspace)return;
            var actionHost=actions.Parent;
            if(filters){actionHost.Visible=false;side.Dock=DockStyle.Fill;side.Visible=true;side.AutoScroll=false;side.AutoScrollMinSize=Size.Empty;}
            else{side.Visible=false;actionHost.Dock=DockStyle.Fill;actionHost.Visible=true;}
            ApplyCompactSidebarVisibility();
            ApplySplitLayout();
        }
        void ApplyCompactSidebarVisibility()
        {
            bool settings=!compactWorkspace||compactSetupSelected;
            analysisSettingsButton.Visible=aiLearningModeBox.Visible=localAiSuggestionButton.Visible=settings;
            bool filters=(!compactWorkspace||!compactSetupSelected)&&!locationOverviewMode;
            filterChoices[0].Parent.Visible=filters;filterParametersHost.Visible=filters&&SelectedFilterMode!=0;
            statisticsEnabledBox.Visible=strengthLabel.Visible=statisticsSlider.Visible=filters;
            autoDisplayBox.Visible=filters;
            displayRecommendationLabel.Visible=displayAdviceDetails.Visible=false;
        }
        // In the embedded 13-inch host only ~416 px remain after shell chrome.
        // Show one full-size chart and one sidebar section at a time instead of
        // shrinking two charts below their usable minima or adding scrollbars.
        void ApplyCompactWorkspace()
        {
            int availableHeight=ClientSize.Height;
            bool compact=availableHeight<680&&Font.SizeInPoints<=9.5f;
            int statusHeight=PreferredStatusBarHeight(compact);
            // DPI/font changes can happen without crossing the compact-mode
            // threshold. Keep the footer tall enough for its actual text.
            if(compact==compactWorkspace)
            {
                if(bottomStatusHost.Height!=statusHeight)bottomStatusHost.Height=statusHeight;
                return;
            }
            compactWorkspace=compact;
            side.SuspendLayout();actions.SuspendLayout();
            try
            {
                workspaceHeader.Height=compact?28:36;
                workspaceHeader.Padding=compact?new Padding(5,2,5,2):new Padding(5,3,5,3);
                workspaceBody.Padding=compact?new Padding(6,0,6,0):new Padding(12);
                workspaceTreePanel.Width=compact?216:228;
                workspaceTreePanel.Padding=compact?new Padding(8):new Padding(10);
                bottomStatusHost.Height=statusHeight;
                pdmap.MinimumSize=new Size(pdmap.MinimumSize.Width,compact?180:250);
                charts.Panel1MinSize=compact?180:250;
                statisticsSlider.Height=22;
                statisticsEnabledBox.Margin=compact?Padding.Empty:new Padding(0,4,0,0);
                strengthLabel.Margin=compact?Padding.Empty:new Padding(0,2,0,2);
                compactChartTabs.Visible=compact;compactSideTabs.Visible=compact;
                side.AutoScroll=!compact;
                if(compact)side.AutoScrollMinSize=Size.Empty;
                if(compact){SelectCompactChart(compactWaveSelected);SelectCompactSidebar(compactFilterSelected);}
                else
                {
                    EnsureCompactChartHosts();
                    charts.Panel1Collapsed=false;charts.Panel2Collapsed=false;
                    side.Visible=true;side.Dock=DockStyle.Fill;
                    actions.Parent.Visible=true;actions.Parent.Dock=DockStyle.Bottom;
                }
                UpdateFilterEnabled();UpdateWorkspaceActionHeight();
            }
            finally{actions.ResumeLayout();side.ResumeLayout();}
        }
        void UpdateWorkspaceActionHeight()
        {
            if(compactWorkspace)return;
            int content=actions.Padding.Vertical;
            foreach(Control child in actions.Controls)
                content+=child.Height+child.Margin.Vertical;
            actions.Parent.Height=locationOverviewMode?archiveActions.Height:content+archiveActions.Height+8;
        }
    }
}
