using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        sealed class RegionDeletion {internal int[] Rows;internal double[] Flags;internal SelectionRegion[] Regions;internal int Mode;}
        readonly Stack<RegionDeletion> regionDeletionHistory=new Stack<RegionDeletion>();
        int[] restoredRegionRows=new int[0];double[] restoredRegionFlags=new double[0];
        ToolStripMenuItem undoRegionItem,lowVoltageItem,midVoltageItem,clearVoltageItem,clearReviewRangeItem;
        [Browsable(false)] public bool CanUndoRegionDeletion {get{return regionDeletionHistory.Count>0;}}
        [Browsable(false)] public int RegionDeletionUndoCount {get{return regionDeletionHistory.Count;}}
        [Browsable(false)] public int[] LastRestoredRegionSourceRows {get{return (int[])restoredRegionRows.Clone();}}
        [Browsable(false)] public double[] LastRestoredAcceptance {get{return (double[])restoredRegionFlags.Clone();}}
        [Browsable(false)] public int RequestedVoltageRangeShortcut {get;private set;}
        public event EventHandler RegionDeletionRestored;
        public event EventHandler VoltageRangeShortcutRequested;
        internal event EventHandler ReviewRangeContextChanged;
        // Owner-only command: do not confuse the Filter dialog with the separate
        // voltage shortcut or the spectrum's classification/display settings.
        internal event EventHandler ReviewRangeFiltersClearRequested;
        void NotifyRangeContextChanged(){NotifyHost(ReviewRangeContextChanged);}
        internal void ClearRegionDeletionHistory(){regionDeletionHistory.Clear();restoredRegionRows=new int[0];restoredRegionFlags=new double[0];}
        void RememberRegionDeletion(int[] rows)
        {
            if(rows.Length==0)return;
            regionDeletionHistory.Push(new RegionDeletion{Rows=(int[])rows.Clone(),Flags=rows.Select(r=>source[r,35]).ToArray(),Mode=mode,
                Regions=State.Regions.Select(r=>new SelectionRegion{X0=r.X0,X1=r.X1,Y0=r.Y0,Y1=r.Y1,Circle=r.Circle}).ToArray()});
        }
        public bool UndoLastRegionDeletion()
        {
            if(regionDeletionHistory.Count==0)return false;
            var undo=regionDeletionHistory.Pop();restoredRegionRows=(int[])undo.Rows.Clone();restoredRegionFlags=(double[])undo.Flags.Clone();
            for(int i=0;i<undo.Rows.Length;i++){int row=undo.Rows[i];deleted.Remove(row);source[row,35]=undo.Flags[i];}
            ViewState state;if(states.TryGetValue(undo.Mode,out state)){state.Regions.Clear();state.Regions.AddRange(undo.Regions);}
            ClearDataViews();ClearHit();Rebuild(false);NotifyHost(RegionDeletionRestored);return true;
        }
        void InitializeRangeContextItems()
        {
            regionMenu.Items.Add(new ToolStripSeparator());
            undoRegionItem=new ToolStripMenuItem();lowVoltageItem=new ToolStripMenuItem();midVoltageItem=new ToolStripMenuItem();clearVoltageItem=new ToolStripMenuItem();
            regionMenu.Items.AddRange(new ToolStripItem[]{undoRegionItem,lowVoltageItem,midVoltageItem,clearVoltageItem});
            undoRegionItem.Click+=(s,e)=>UndoLastRegionDeletion();
            lowVoltageItem.Click+=(s,e)=>RequestVoltageShortcut(1);midVoltageItem.Click+=(s,e)=>RequestVoltageShortcut(2);clearVoltageItem.Click+=(s,e)=>RequestVoltageShortcut(0);
            clearReviewRangeItem=new ToolStripMenuItem{Name="CancelReviewRangeFilters"};
            clearReviewRangeItem.Click+=(s,e)=>NotifyHost(ReviewRangeFiltersClearRequested);
            regionMenu.Items.Add(clearReviewRangeItem);
            regionMenu.Opening+=(s,e)=>UpdateRangeContextItems();
        }
        void RequestVoltageShortcut(int value){RequestedVoltageRangeShortcut=value;NotifyHost(VoltageRangeShortcutRequested);}
        void UpdateRangeContextItems()
        {
            if(undoRegionItem==null)return;
            undoRegionItem.Text=L("Undo region deletion","恢复删除");undoRegionItem.Enabled=CanUndoRegionDeletion;
            lowVoltageItem.Text=L("Show only U ≤ U0","仅显示 U≤U0");midVoltageItem.Text=L("Show only U0 < U ≤ 2U0","仅显示 U0＜U≤2U0");
            lowVoltageItem.Enabled=midVoltageItem.Enabled=OperatingVoltageKv>0&&VoltageRangeShortcutRequested!=null;
            lowVoltageItem.ToolTipText=midVoltageItem.ToolTipText=L("Uses specified rated phase voltage U0.","使用已设置的额定相电压 U0；未设置时不可用。");
            clearVoltageItem.Text=L("Clear voltage shortcut","取消电压快捷过滤");clearVoltageItem.Enabled=VoltageRangeShortcutRequested!=null;
            clearReviewRangeItem.Text=L("Cancel data filters","取消过滤");
            clearReviewRangeItem.ToolTipText=L("Disable only charge, voltage and count filters from the Filter dialog.","仅取消“过滤”窗口的放电量、电压和次数过滤。");
            clearReviewRangeItem.Visible=clearReviewRangeItem.Enabled=ReviewRangeFiltersClearRequested!=null;
        }
    }
}
