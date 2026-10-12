using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    public sealed partial class CompetitionDefectRecognitionControl
    {
        readonly UnifiedSpectrumControl secondarySpectrum=new UnifiedSpectrumControl(true,true){Name="CompetitionRightSpectrum",Dock=DockStyle.Fill,AcceptedOnly=true};
        readonly TableLayoutPanel spectra=new TableLayoutPanel{Name="CompetitionLinkedSpectra",ColumnCount=2,RowCount=2,BackColor=Color.White,Margin=Padding.Empty};
        readonly FlowLayoutPanel sharedPhases=new FlowLayoutPanel{Name="CompetitionSharedPhases",Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Padding=new Padding(6),Margin=Padding.Empty};
        readonly Label phaseCaption=new Label{AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,8,10,0)};
        readonly CheckBox[] sharedPhaseButtons=new CheckBox[3];
        bool syncingSharedPhases;
        readonly Button clearSelection=new HVX.Presentation.R2RoundedButton{Name="CompetitionClearSelection",AutoSize=true,Enabled=false};
        int leftMode=11,rightMode=4;
        int[] linkedRegionRows;
        IEnumerable<UnifiedSpectrumControl> LinkedSpectra {get{return new[]{spectrum,secondarySpectrum};}}
        [Browsable(false)]public int LeftSpectrumMode{get{return spectrum.DisplayModeIndex;}set{spectrum.DisplayModeIndex=value;}}
        [Browsable(false)]public int RightSpectrumMode{get{return secondarySpectrum.DisplayModeIndex;}set{secondarySpectrum.DisplayModeIndex=value;}}
        [Browsable(false)]public int[] SelectedSourceRowIndices{get{return linkedRegionRows==null?new int[0]:(int[])linkedRegionRows.Clone();}}
        [Browsable(false)]public int[] SharedSourceRowIndices
        {
            get{
                bool[] mask=LinkedVisibilityMask();
                // Recognition always consumes the selected physical PHI/Q rows.
                // A plot may legitimately omit rows without its extra coordinates
                // (e.g. unknown L); that does not fabricate L or discard their PRPD.
                return SpectrumData.Build(parameters,2,spectrum.PhaseVisibility,spectrum.PhaseFilterIndex,accepted.Checked,null,100,72,50,0,mask)
                    .Points.SelectMany(point=>point.SourceRows).Distinct().OrderBy(row=>row).ToArray();
            }
        }
        void InitializeLinkedSpectra()
        {
            spectra.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));spectra.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            spectra.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            spectra.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            spectrum.Margin=new Padding(0,0,4,0);secondarySpectrum.Margin=new Padding(4,0,0,0);
            foreach(var plot in LinkedSpectra){plot.BackColor=Color.White;plot.UseHostStatusBar=true;plot.CompactHostLayout=true;plot.PhaseSelectorVisible=false;InitializeLinkedContextMenu(plot);}
            secondarySpectrum.DisplayModeIndex=rightMode;
            spectra.Controls.Add(spectrum,0,0);spectra.Controls.Add(secondarySpectrum,1,0);
            sharedPhases.Controls.Add(phaseCaption);
            for(int i=0;i<3;i++){
                var button=new RoundedReviewToggle{Name="CompetitionPhase"+(i+1),Appearance=Appearance.Button,AutoSize=true,TextAlign=ContentAlignment.MiddleCenter,Padding=new Padding(12,5,12,5),Margin=new Padding(0,0,6,0),FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false};
                button.CheckedChanged+=(s,e)=>{if(syncingSharedPhases)return;var next=Enumerable.Range(0,3).Select(p=>sharedPhaseButtons[p].Checked).ToArray();syncing=true;try{foreach(var plot in LinkedSpectra){plot.PhaseFilterIndex=-1;plot.PhaseVisibility=next;}}finally{syncing=false;}SyncSharedPhases();ViewChanged();};
                sharedPhaseButtons[i]=button;sharedPhases.Controls.Add(button);
            }
            spectra.Controls.Add(sharedPhases,0,1);spectra.SetColumnSpan(sharedPhases,2);
            foreach(var plot in LinkedSpectra){plot.ViewChanged+=(s,e)=>LinkedSpectrumChanged((UnifiedSpectrumControl)s);plot.RegionStatisticsChanged+=(s,e)=>LinkedRegionChanged((UnifiedSpectrumControl)s);plot.RegionActionCompleted+=(s,e)=>LinkedRegionActionCompleted((UnifiedSpectrumControl)s);}
            StyleButton(clearSelection,false,false);actions.Controls.Add(clearSelection);
            clearSelection.Click+=(s,e)=>ClearLinkedSelection();
            SyncSharedPhases();
        }
        void SyncSharedPhases()
        {
            syncingSharedPhases=true;
            try{
                sharedPhases.Font=phaseCaption.Font=Font;phaseCaption.Text=T("Phases","相别");
                var visibility=spectrum.PhaseVisibility;
                for(int i=0;i<3;i++){
                    var button=sharedPhaseButtons[i];if(button==null)continue;
                    bool shown=visibility[i]&&(spectrum.PhaseFilterIndex==-1||spectrum.PhaseFilterIndex==i+1);
                    Color color=i==0?Color.FromArgb(180,138,0):i==1?Color.FromArgb(34,119,72):Color.FromArgb(190,52,44);
                    button.Font=Font;button.Checked=shown;button.Text=(shown?"✓ ":"")+T("L"+(i+1),new[]{"A相","B相","C相"}[i]);
                    button.BackColor=shown?ControlPaint.LightLight(color):Color.White;button.ForeColor=shown?color:ReviewTheme.Muted;
                    button.FlatAppearance.BorderSize=1;button.FlatAppearance.BorderColor=color;button.FlatAppearance.CheckedBackColor=button.BackColor;
                    button.AccessibleName=T("Show phase ","显示相别 ")+T("L"+(i+1),new[]{"A","B","C"}[i]);
                    button.AccessibleDescription=T("Applies to both charts","同时应用于两张谱图");
                }
            }finally{syncingSharedPhases=false;}
        }
        void LinkedSpectrumChanged(UnifiedSpectrumControl changed)
        {
            if(syncing)return;
            syncing=true;
            try{
                var other=Object.ReferenceEquals(changed,spectrum)?secondarySpectrum:spectrum;
                int prior=Object.ReferenceEquals(changed,spectrum)?leftMode:rightMode;
                // Selecting the other chart's type swaps the pair: neither chart
                // disappears and the two chart types are never equal.
                if(changed.DisplayModeIndex==other.DisplayModeIndex)other.DisplayModeIndex=prior;
                other.PhaseFilterIndex=changed.PhaseFilterIndex;other.PhaseVisibility=changed.PhaseVisibility;
                leftMode=spectrum.DisplayModeIndex;rightMode=secondarySpectrum.DisplayModeIndex;
            }finally{syncing=false;}
            SyncSharedPhases();
            ViewChanged();
        }
        void LinkedRegionChanged(UnifiedSpectrumControl changed)
        {
            if(syncing)return;
            int[] rows=changed.RegionStatisticsActive?changed.RegionStatisticsSourceRowIndices:null;
            ApplyLinkedSelection(rows);
        }
        void LinkedRegionActionCompleted(UnifiedSpectrumControl changed)
        {
            // The chart's clear-region button clears the shared region as well;
            // independent exclusions and voltage filters remain in effect.
            if(syncing||changed.RegionActionCommand!=2||linkedRegionRows==null)return;
            RememberLinkedFilters();linkedRegionRows=null;ApplyLinkedFilters();
        }
        public void ClearLinkedSelection(){ClearLinkedFilters();}
        void ApplyLinkedSelection(int[] rows)
        {
            RememberLinkedFilters();
            linkedRegionRows=rows==null?null:rows.Where(r=>r>=0&&r<SourceRowCount).Distinct().OrderBy(r=>r).ToArray();
            ApplyLinkedFilters();
        }
        void ApplyLinkedFilters()
        {
            syncing=true;
            try{
                // Shared selection uses original row identities, not coordinates
                // projected into unrelated axes; no matrix or acceptance edit.
                bool[] mask=LinkedVisibilityMask();
                foreach(var plot in LinkedSpectra)
                {
                    plot.ExitRegionStatistics();
                    plot.SetReviewVisibility(mask);
                    if(plot.RegionSelectionEnabled&&plot.SelectionRegionCount>0)plot.RegionActionCommand=2;
                    plot.SyncLinkedContextMenu();
                }
                clearSelection.Enabled=HasLinkedFilters;
            }finally{syncing=false;}
            ViewChanged();
        }
        string RecognitionSummary(DischargeRecognitionResult result)
        {
            if(result==null)return T("No result yet.","尚无识别结果。");
            if(String.IsNullOrWhiteSpace(result.DisplayLabel))return result.StatusCode=="InsufficientInput"?T("Not enough pulses. Select more data.","脉冲不足，请选择更多数据。"):T("Recognition unavailable. Please try again.","暂时无法识别，请重试。");
            string caption=T("Discharge type: ","放电类型：")+result.DisplayLabel;
            var scores=result.ClassScores;
            if(scores.Length==0)return caption+Environment.NewLine+T("Class scores not provided.","未提供各类型评分。");
            return caption+Environment.NewLine+String.Join("   ",scores.Select(x=>x.Phase+" · "+x.DisplayLabel+" "+(x.Score*100).ToString("0.0",CultureInfo.InvariantCulture)+"%"));
        }
    }
}
