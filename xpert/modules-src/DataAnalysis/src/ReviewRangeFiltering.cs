using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool rangeCountEnabled,rangeChargeEnabled,rangeVoltageEnabled,filteredExportBusy;
        int minimumRangeCount,voltageShortcutMode;
        double rangeChargeMin,rangeChargeMax,rangeVoltageMin,rangeVoltageMax;
        double[,] exportedParameters=new double[0,36],exportedMaps=new double[0,0];
        int[] exportedRows=new int[0];
        bool lastExportFilteredCommand;
        Button rangeFilterButton;
        ContextMenuStrip reviewRangeMenu;
        [Browsable(false)] public bool RangeCountFilterEnabled { get { return rangeCountEnabled; } }
        [Browsable(false)] public bool RangeChargeFilterEnabled { get { return rangeChargeEnabled; } }
        [Browsable(false)] public bool RangeVoltageFilterEnabled { get { return rangeVoltageEnabled; } }
        [Browsable(false)] public int MinimumRangeDischargeCount { get { return minimumRangeCount; } }
        [Browsable(false)] public double[] RangeChargeBoundsPc { get { return new[]{rangeChargeMin,rangeChargeMax}; } }
        [Browsable(false)] public double[] RangeVoltageBoundsKv { get { return new[]{rangeVoltageMin,rangeVoltageMax}; } }
        [Category("Filtering"),Description("Explicit rated phase voltage U0 in kV. Zero disables U0 shortcuts; never inferred from observed test voltage.")]
        public double FilterOperatingVoltageKv
        {
            get { return spectrum.OperatingVoltageKv; }
            set { spectrum.OperatingVoltageKv=value; }
        }
        [Category("Filtering"),DefaultValue(0),Description("0 no shortcut, 1 U<=U0, 2 U0<U<=2U0; independent of ABC display.")]
        public int VoltageShortcutMode
        {
            get { return voltageShortcutMode; }
            set {
                if(value<0||value>2)throw new ArgumentOutOfRangeException("VoltageShortcutMode");
                if(value!=0&&(!Finite(FilterOperatingVoltageKv)||FilterOperatingVoltageKv<=0))
                    throw new InvalidOperationException(L("Set rated U0 first.","请先设置额定相电压 U0。"));
                if(voltageShortcutMode==value)return;voltageShortcutMode=value;RefreshStatistics();
            }
        }
        [Browsable(false)] public bool CanUndoRegionDeletion { get { return spectrum.CanUndoRegionDeletion; } }
        public bool UndoLastRegionDeletion() { return spectrum.UndoLastRegionDeletion(); }

        public void SetReviewRangeFilters(bool countEnabled,int minimumCount,bool chargeEnabled,double minimumChargePc,
            double maximumChargePc,bool voltageEnabled,double minimumVoltageKv,double maximumVoltageKv)
        {
            if(minimumCount<0)throw new ArgumentOutOfRangeException("minimumCount");
            ValidateRangeBounds(minimumChargePc,maximumChargePc,"charge");
            ValidateRangeBounds(minimumVoltageKv,maximumVoltageKv,"voltage");
            if(rangeCountEnabled==countEnabled&&minimumRangeCount==minimumCount&&rangeChargeEnabled==chargeEnabled&&
                rangeChargeMin==minimumChargePc&&rangeChargeMax==maximumChargePc&&rangeVoltageEnabled==voltageEnabled&&
                rangeVoltageMin==minimumVoltageKv&&rangeVoltageMax==maximumVoltageKv)return;
            rangeCountEnabled=countEnabled;minimumRangeCount=minimumCount;rangeChargeEnabled=chargeEnabled;
            rangeChargeMin=minimumChargePc;rangeChargeMax=maximumChargePc;rangeVoltageEnabled=voltageEnabled;
            rangeVoltageMin=minimumVoltageKv;rangeVoltageMax=maximumVoltageKv;RefreshStatistics();
        }
        static void ValidateRangeBounds(double lo,double hi,string name)
        {if(!Finite(lo)||!Finite(hi)||lo<0||hi<lo)throw new ArgumentException("Finite nonnegative min <= max required.",name);}
        public void ResetReviewRangeFilters()
        {
            double q0,q1,u0,u1;GetObservedBounds(out q0,out q1,out u0,out u1);
            voltageShortcutMode=0;
            SetReviewRangeFilters(false,0,false,q0,q1,false,u0,u1);RefreshStatistics();
        }
        void CancelReviewRangeFilters()
        {
            // Preserve configured bounds and every independent filter. Unlike
            // ResetReviewRangeFilters, this does not clear the U0 shortcut.
            SetReviewRangeFilters(false,minimumRangeCount,false,rangeChargeMin,rangeChargeMax,
                false,rangeVoltageMin,rangeVoltageMax);
        }
        void GetObservedBounds(out double q0,out double q1,out double u0,out double u1)
        {
            var q=Enumerable.Range(0,SourceRowCount).Select(r=>Math.Abs(working[r,15])).Where(Finite).ToArray();
            var u=Enumerable.Range(0,SourceRowCount).Select(r=>working[r,2]).Where(v=>Finite(v)&&v>=0).ToArray();
            q0=q.Length==0?0:q.Min();q1=q.Length==0?0:q.Max();u0=u.Length==0?0:u.Min();u1=u.Length==0?0:u.Max();
        }
        void ResetRangeFiltersForNewData()
        {
            spectrum.ClearRegionDeletionHistory();rangeCountEnabled=rangeChargeEnabled=rangeVoltageEnabled=false;
            minimumRangeCount=0;voltageShortcutMode=0;GetObservedBounds(out rangeChargeMin,out rangeChargeMax,out rangeVoltageMin,out rangeVoltageMax);
            ClearFilteredExport();
        }
        // Neighbourhood radius follows the PDmap position tolerance, per physical phase/cable.
        string RangeCountKey(int row)
        {
            double x=working[row,13],length=working[row,5],phase=working[row,0];
            if(!Finite(x)||!Finite(length)||length<=0||x<0||x>length||!Finite(phase)||phase<0||phase>3||phase!=Math.Truncate(phase))return null;
            return phase.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+length.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        }
        int[] RangeNeighbourCounts(bool[] mask)
        {
            var counts=new int[mask.Length];
            var groups=Enumerable.Range(0,mask.Length).Where(r=>mask[r]).Select(r=>new{Row=r,Key=RangeCountKey(r)})
                .Where(x=>x.Key!=null).GroupBy(x=>x.Key);
            foreach(var group in groups){
                var rows=group.Select(x=>x.Row).OrderBy(r=>working[r,13]).ToArray();
                double tolerance=working[rows[0],5]*(pdmap.PositionTolerancePercent/100.0)+1e-9;
                int lo=0,hi=0;
                for(int i=0;i<rows.Length;i++){
                    double x=working[rows[i],13];
                    while(lo<rows.Length&&working[rows[lo],13]<x&&x-working[rows[lo],13]>tolerance)lo++;
                    while(hi<rows.Length&&(working[rows[hi],13]<=x||working[rows[hi],13]-x<=tolerance))hi++;
                    counts[rows[i]]=hi-lo;
                }
            }return counts;
        }
        void ApplyReviewRangeMask(bool[] mask)
        {
            double u0=FilterOperatingVoltageKv;
            for(int r=0;r<mask.Length;r++)if(mask[r]){
                double q=Math.Abs(working[r,15]),u=working[r,2];
                bool voltageShortcut=voltageShortcutMode==0||(Finite(u0)&&u0>0&&Finite(u)&&u>=0&&
                    (voltageShortcutMode==1?u<=u0:u>u0&&u/2<=u0));
                if(rangeChargeEnabled&&(!Finite(q)||q<rangeChargeMin||q>rangeChargeMax)||
                    rangeVoltageEnabled&&(!Finite(u)||u<rangeVoltageMin||u>rangeVoltageMax)||!voltageShortcut)mask[r]=false;
            }
            if(rangeCountEnabled&&minimumRangeCount>0){
                var counts=RangeNeighbourCounts(mask);
                for(int r=0;r<mask.Length;r++)if(mask[r]&&counts[r]<minimumRangeCount)mask[r]=false;
            }
        }
        void InitializeReviewRangeActions()
        {
            spectrum.RegionDeletionRestored+=OnRegionDeletionRestored;
            spectrum.ReviewRangeContextChanged+=(s,e)=>{if(voltageShortcutMode!=0||rangeCountEnabled)RefreshStatistics();};
            pdmap.ApproximationDisplayChanged+=(s,e)=>{if(rangeCountEnabled&&!loadingData&&!synchronizing)RefreshStatistics();};
            spectrum.VoltageRangeShortcutRequested+=(s,e)=>{VoltageShortcutMode=spectrum.RequestedVoltageRangeShortcut;};
            spectrum.ReviewRangeFiltersClearRequested+=(s,e)=>CancelReviewRangeFilters();
            reviewRangeMenu=new ContextMenuStrip();
            var undo=reviewRangeMenu.Items.Add("");undo.Click+=(s,e)=>UndoLastRegionDeletion();
            var low=reviewRangeMenu.Items.Add("");low.Click+=(s,e)=>VoltageShortcutMode=1;
            var high=reviewRangeMenu.Items.Add("");high.Click+=(s,e)=>VoltageShortcutMode=2;
            var reset=reviewRangeMenu.Items.Add("");reset.Click+=(s,e)=>VoltageShortcutMode=0;
            reviewRangeMenu.Opening+=(s,e)=>{
                undo.Text=L("Undo region deletion","恢复删除");undo.Enabled=CanUndoRegionDeletion;
                low.Text=L("Show only U ≤ U0","仅显示 U≤U0");high.Text=L("Show only U0 < U ≤ 2U0","仅显示 U0＜U≤2U0");
                low.Enabled=high.Enabled=FilterOperatingVoltageKv>0;reset.Text=L("Clear voltage shortcut","取消电压快捷过滤");
            };
            side.ContextMenuStrip=actions.ContextMenuStrip=reviewRangeMenu;
            Disposed+=(s,e)=>{if(activeRangeDialog!=null)activeRangeDialog.Dispose();reviewRangeMenu.Dispose();};
        }
        void OnRegionDeletionRestored(object sender,EventArgs e)
        {
            var rows=spectrum.LastRestoredRegionSourceRows;var flags=spectrum.LastRestoredAcceptance;
            for(int i=0;i<rows.Length;i++){
                int row=rows[i];if(row<0||row>=SourceRowCount)continue;
                removed[row]=false;
                working[row,35]=IsAcquisitionReferenceOnly(row)?0:flags[i];pdmap.CommitReviewRow(row,Row(working,row),true);spectrum.UpdateReviewRow(row,Row(working,row),true);
            }
            pdmap.FlushReviewPreview();spectrum.FlushReviewPreview();RefreshStatisticsAfterEdit();
            if(rows.Length>0){SyncReviewedTree();MarkAnalysisDirty();ResultChangeSequence++;NotifyReviewHost(ResultsChanged);}
        }
        bool rangeDialogBusy,lastRangeDialogCommand;
        ReviewRangeFilterDialog activeRangeDialog;
        [Browsable(false)] public bool IsRangeFilterDialogOpen {get{return rangeDialogBusy;}}
        [Browsable(false)] public int RangeFilterDialogOpenSequence {get;private set;}
        [Category("LabVIEW Commands"),DefaultValue(false),Description("Open only on false-to-true transition. Repeated true writes do not reopen the dialog.")]
        public bool ShowRangeFilterCommand {get{return lastRangeDialogCommand;}set{bool rising=value&&!lastRangeDialogCommand;lastRangeDialogCommand=value;if(rising)ShowRangeFilterDialog();}}
        public bool ShowRangeFilterDialog()
        {
            if(IsDisposed||Disposing||SourceRowCount==0||rangeDialogBusy)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(ShowRangeFilterDialog));
            rangeDialogBusy=true;
            try { using(var dialog=new ReviewRangeFilterDialog(this,english)){
                activeRangeDialog=dialog;RangeFilterDialogOpenSequence++;
                if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return false;
                if(IsDisposed||Disposing)return false;
                SetReviewRangeFilters(dialog.CountEnabled,dialog.MinimumCount,dialog.ChargeEnabled,dialog.MinimumCharge,dialog.MaximumCharge,
                    dialog.VoltageEnabled,dialog.MinimumVoltage,dialog.MaximumVoltage);return true;
            }}catch(Exception ex){if(IsFatalHostException(ex))throw;LastError=ex.Message;if(!IsDisposed)RefreshReviewUi();return false;}
            finally{activeRangeDialog=null;rangeDialogBusy=false;}
        }
        internal double[] ObservedRangeDefaults()
        {double a,b,c,d;GetObservedBounds(out a,out b,out c,out d);int maximum=RangeNeighbourCounts(Enumerable.Repeat(true,SourceRowCount).ToArray()).DefaultIfEmpty(0).Max();return new[]{a,b,c,d,(double)maximum};}

        [Browsable(false)] public double[,] FilteredResultParameterMatrix { get { return (double[,])exportedParameters.Clone(); } }
        [Browsable(false)] public double[,] FilteredPdMapMatrix { get { return (double[,])exportedMaps.Clone(); } }
        [Browsable(false)] public int[] FilteredExportSourceRowIndices { get { return (int[])exportedRows.Clone(); } }
        [Browsable(false)] public bool FilteredExportSucceeded { get; private set; }
        [Browsable(false)] public int FilteredExportSequence { get; private set; }
        int exportResultRevision,exportFilterRevision;
        [Browsable(false)] public bool FilteredExportIsCurrent { get { return FilteredExportSucceeded&&exportResultRevision==ResultChangeSequence&&exportFilterRevision==FilterRefreshSequence; } }
        [Category("Data"),Description("Write true to snapshot filtered parameters and matching PDmap matrices. False does nothing. Acceptance flags are never changed.")]
        public bool ExportFilteredDataCommand
        {get{return lastExportFilteredCommand;}set{lastExportFilteredCommand=value;if(value){double[,] p,w;ExportFilteredData(true,out p,out w);}}}
        void ClearFilteredExport()
        {exportedParameters=new double[0,working.GetLength(1)];exportedMaps=new double[0,matching.GetLength(1)];exportedRows=new int[0];FilteredExportSucceeded=false;}
        public bool ExportFilteredData(bool exportRequested,out double[,] parameters,out double[,] pdmapWaveforms)
        {
            parameters=new double[0,working.GetLength(1)];pdmapWaveforms=new double[0,matching.GetLength(1)];
            if(!exportRequested||filteredExportBusy)return false;
            filteredExportBusy=true;ClearFilteredExport();
            try{
                pdmap.CompleteReviewPreview();RefreshStatistics(true);int[] rows=(int[])filteredRows.Clone();
                int n=SourceRowCount,blocks=matching.GetLength(0)==3L*n&&n>0?3:1;
                var p=new double[rows.Length,working.GetLength(1)];var w=new double[rows.Length*blocks,matching.GetLength(1)];
                for(int i=0;i<rows.Length;i++){
                    int row=rows[i];for(int c=0;c<p.GetLength(1);c++)p[i,c]=working[row,c];
                    int mapRow=matching.GetLength(0)==n||blocks==3?row:(int)original[row,9];
                    for(int block=0;block<blocks;block++)for(int k=0;k<w.GetLength(1);k++)w[i+block*rows.Length,k]=matching[mapRow+block*n,k];
                }
                exportedParameters=p;exportedMaps=w;exportedRows=rows;FilteredExportSucceeded=true;FilteredExportSequence++;
                exportResultRevision=ResultChangeSequence;exportFilterRevision=FilterRefreshSequence;
                parameters=(double[,])p.Clone();pdmapWaveforms=(double[,])w.Clone();LastError="";return true;
            }
            catch(Exception ex){if(IsFatalHostException(ex))throw;ClearFilteredExport();LastError=ex.Message;return false;}
            finally{filteredExportBusy=false;RefreshReviewUi();}
        }
    }
}
