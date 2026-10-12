using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool fittingReadableWorkspace;
        static int ReadableTextWidth(Control c, int allowance)
        { return TextRenderer.MeasureText(c.Text ?? "",c.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+allowance; }

        // Measure the current translated font at its actual DPI. The old fixed
        // 65/90/100 px slots truncated even English at 150% scaling.
        void ApplyReadableWorkspaceLayout()
        {
            if(fittingReadableWorkspace||compactDecisionRow==null||metricLabels==null)return;
            fittingReadableWorkspace=true;
            side.SuspendLayout();actions.SuspendLayout();archiveActions.SuspendLayout();
            try
            {
                ApplyCompactSidebarVisibility();
                int gap=6, line=Math.Max(24,Font.Height+(compactWorkspace?0:8)), pad=10;
                int inputWidth=Math.Max(76,TextRenderer.MeasureText("3000.000",Font).Width+SystemInformation.VerticalScrollBarWidth+12);
                int labelWidth=new[]{highLabel,lowLabel,waveLabel,levelsLabel,ignoreLabel}.Max(c=>ReadableTextWidth(c,8));
                int metricLabelWidth=metricLabels.Max(c=>ReadableTextWidth(c,10));
                int valueWidth=Math.Max(74,TextRenderer.MeasureText("123456.7",Font).Width+8);
                int unitWidth=TextRenderer.MeasureText("kV",Font).Width+8;
                int decisionWidth=new[]{acceptButton,deleteButton,uncertainButton,rangeFilterButton}.Max(c=>ReadableTextWidth(c,24)+4);
                int archiveWidth=Math.Max(ReadableTextWidth(evaluateButton,24),ReadableTextWidth(reportButton,24))*2+8;
                int choicesWidth=filterChoices.Sum(c=>ReadableTextWidth(c,24)+2);
                int content=Math.Max(248,Math.Max(labelWidth+inputWidth+gap,Math.Max(metricLabelWidth+valueWidth+unitWidth+gap,Math.Max(decisionWidth,Math.Max(archiveWidth,choicesWidth)))));
                // Avoid making a long translated action dictate an unusably
                // wide canvas. Full labels can wrap onto two button rows.
                content=Math.Min(content,Math.Max(280,ClientSize.Width/3));
                // These top-level buttons stay on a single line. Include their
                // own fonts (settings uses a bold section font) rather than only
                // the inherited workspace font in both width and height fitting.
                Control[] topSettings={analysisSettingsButton,aiLearningModeBox,localAiSuggestionButton};
                content=Math.Max(content,topSettings.Max(c=>ReadableTextWidth(c,24)));
                side.Padding=new Padding(pad,8,pad,6);actions.Padding=new Padding(pad,6,pad,4);archiveActions.Padding=new Padding(pad,0,pad,6);
                foreach(Control c in side.Controls)c.Width=content;
                foreach(Control c in topSettings)
                {
                    c.Width=content;
                    int textHeight=TextRenderer.MeasureText(c.Text??"",c.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Height;
                    c.Height=Math.Max(30,textHeight+10);
                }
                filterChoices[0].Parent.Width=content;filterChoices[0].Parent.Height=line+4;
                int remaining=content-filterChoices.Sum(c=>2);
                foreach(var choice in filterChoices){choice.Width=Math.Min(remaining,ReadableTextWidth(choice,24));choice.Height=line;remaining-=choice.Width;}
                filterParametersHost.Width=bandParameters.Width=waveletParameters.Width=content;
                foreach(var page in new[]{bandParameters,waveletParameters})foreach(Control row in page.Controls)
                {
                    row.Width=content;row.Margin=Padding.Empty;
                    var label=row.Controls.OfType<Label>().FirstOrDefault();var input=row.Controls.Cast<Control>().FirstOrDefault(c=>c is NumericUpDown||c is ComboBox);
                    var slider=row.Controls.OfType<TrackBar>().FirstOrDefault();
                    if(label==null||input==null)continue;
                    int edit=Math.Min(inputWidth,Math.Max(70,content/2));
                    int h=Math.Max(line,input.PreferredSize.Height+4);
                    label.AutoEllipsis=false;label.SetBounds(0,0,content-edit-gap,h);
                    input.SetBounds(content-edit,2,edit,input.PreferredSize.Height);
                    row.Height=h+(slider==null?2:22);
                    if(slider!=null)slider.SetBounds(0,h,content,22);
                }
                bandParameters.Height=FlowContentHeight(bandParameters);waveletParameters.Height=FlowContentHeight(waveletParameters);
                filterParametersHost.Height=SelectedFilterMode==1?bandParameters.Height:SelectedFilterMode==2?waveletParameters.Height:0;
                statisticsEnabledBox.Height=line;strengthLabel.Height=line;statisticsSlider.Height=24;
                foreach(Control c in actions.Controls)c.Width=content;
                // Compact notebook hosts keep four decision actions available.
                // Metrics need readable font-height rows, not button-height rows;
                // the saved space prevents clipping the archive actions below.
                int metricLine=compactWorkspace?Math.Min(line,Math.Max(17,Font.Height)):line;
                var metricHost=metricLabels[0].Parent;metricHost.Height=metricLine*metricLabels.Length;
                int labelCol=Math.Min(metricLabelWidth,content-valueWidth-unitWidth-gap);
                for(int i=0;i<metricLabels.Length;i++)
                {
                    metricLabels[i].AutoEllipsis=false;metricLabels[i].SetBounds(0,i*metricLine,labelCol,metricLine);
                    metricValues[i].SetBounds(labelCol+gap,i*metricLine,content-labelCol-unitWidth-gap,metricLine);
                    metricUnits[i].AutoEllipsis=false;metricUnits[i].SetBounds(content-unitWidth,i*metricLine,unitWidth,metricLine);
                }
                foreach(var badge in classIndicators){badge.Width=(content-6)/3;badge.Height=line;}
                classIndicators[0].Parent.Height=line+3;
                previousButton.Width=nextButton.Width=(content-8)/2;previousButton.Height=nextButton.Height=line;previousButton.Parent.Height=line+4;
                compactDecisionRow.FlowDirection=FlowDirection.TopDown;
                compactDecisionRow.WrapContents=false;
                int decisionHeight=0;
                foreach(var b in new[]{acceptButton,deleteButton,uncertainButton,rangeFilterButton})
                {
                    b.Width=content-b.Margin.Horizontal;b.Height=line;
                    decisionHeight+=b.Height+b.Margin.Vertical;
                }
                compactDecisionRow.Height=decisionHeight+compactDecisionRow.Padding.Vertical;
                bool archiveWrap=ReadableTextWidth(evaluateButton,24)+ReadableTextWidth(reportButton,24)+8>content;
                archiveActions.Height=(archiveWrap?2:1)*(line+4)+6;
                evaluateButton.Width=reportButton.Width=archiveWrap?content-4:(content-8)/2;evaluateButton.Height=reportButton.Height=line;
                sideWidth=content+pad*2;
                int treeWidth=Math.Max(218,Math.Min(340,(int)(ClientSize.Width*.23)));
                workspaceTreePanel.Width=treeWidth;workspaceTreePanel.Padding=new Padding(6);
                if(compactWorkspace)
                {
                    pdmap.MinimumSize=new Size(360,180);
                }
                spectrum.CompactHostLayout=compactWorkspace;
                if(compactSideTabs!=null)
                {compactFilterButton.Width=compactActionButton.Width=compactSetupButton.Width=(sideWidth-12)/3;compactActionButton.Left=compactFilterButton.Right+3;compactSetupButton.Left=compactActionButton.Right+3;}
                UpdateWorkspaceActionHeight();
            }
            finally{archiveActions.ResumeLayout();actions.ResumeLayout();side.ResumeLayout();fittingReadableWorkspace=false;}
        }
    }
}
