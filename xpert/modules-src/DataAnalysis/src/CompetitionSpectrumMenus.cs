using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    public sealed partial class CompetitionDefectRecognitionControl
    {
        // These are view filters only. The source matrix and its acceptance flags
        // remain unchanged; both charts and recognition use the same row mask.
        readonly HashSet<int> excludedLinkedRows=new HashSet<int>();
        readonly Stack<LinkedFilterState> linkedFilterHistory=new Stack<LinkedFilterState>();
        int linkedVoltageShortcut;
        sealed class LinkedFilterState
        {
            internal int[] Selection,Excluded;
            internal int Voltage;
        }
        bool HasLinkedFilters {get{return linkedRegionRows!=null||excludedLinkedRows.Count>0||linkedVoltageShortcut!=0;}}
        bool[] LinkedVisibilityMask()
        {
            if(!HasLinkedFilters)return null;
            var selected=linkedRegionRows==null?null:new HashSet<int>(linkedRegionRows);
            var mask=new bool[SourceRowCount];double u0=spectrum.OperatingVoltageKv;
            for(int r=0;r<mask.Length;r++){
                double voltage=parameters[r,2];
                bool voltageMatch=linkedVoltageShortcut==0||u0>0&&!Double.IsNaN(voltage)&&!Double.IsInfinity(voltage)&&voltage>=0&&
                    (linkedVoltageShortcut==1?voltage<=u0:voltage>u0&&voltage<=2*u0);
                mask[r]=(selected==null||selected.Contains(r))&&!excludedLinkedRows.Contains(r)&&voltageMatch;
            }
            return mask;
        }
        void RememberLinkedFilters()
        {
            // Bound per-page undo memory; retain the newest 32 view operations.
            if(linkedFilterHistory.Count>=32){var recent=linkedFilterHistory.Take(31).Reverse().ToArray();linkedFilterHistory.Clear();foreach(var item in recent)linkedFilterHistory.Push(item);}
            linkedFilterHistory.Push(new LinkedFilterState{Selection=linkedRegionRows==null?null:(int[])linkedRegionRows.Clone(),Excluded=excludedLinkedRows.ToArray(),Voltage=linkedVoltageShortcut});
        }
        void ResetLinkedFilters()
        {linkedRegionRows=null;excludedLinkedRows.Clear();linkedVoltageShortcut=0;linkedFilterHistory.Clear();clearSelection.Enabled=false;}
        void ClearLinkedFilters()
        {
            if(!HasLinkedFilters)return;
            RememberLinkedFilters();linkedRegionRows=null;excludedLinkedRows.Clear();linkedVoltageShortcut=0;ApplyLinkedFilters();
        }
        void UndoLinkedFilters()
        {
            if(linkedFilterHistory.Count==0)return;
            var prior=linkedFilterHistory.Pop();linkedRegionRows=prior.Selection;excludedLinkedRows.Clear();
            foreach(int row in prior.Excluded)excludedLinkedRows.Add(row);
            linkedVoltageShortcut=prior.Voltage;ApplyLinkedFilters();
        }
        void ExcludeLinkedSelection(UnifiedSpectrumControl plot)
        {
            int[] rows=plot.SelectionRegionCount>0?plot.GetSelectionRegionSourceRows():linkedRegionRows;
            if(rows==null||rows.Length==0)return;
            RememberLinkedFilters();foreach(int row in rows)if(row>=0&&row<SourceRowCount)excludedLinkedRows.Add(row);
            linkedRegionRows=null;ApplyLinkedFilters();
        }
        void SetLinkedVoltageShortcut(int shortcut)
        {
            if(shortcut!=0&&(!(spectrum.OperatingVoltageKv>0)||Double.IsInfinity(spectrum.OperatingVoltageKv)))return;
            if(linkedVoltageShortcut==shortcut)return;
            RememberLinkedFilters();linkedVoltageShortcut=shortcut;ApplyLinkedFilters();
        }
        void InitializeLinkedContextMenu(UnifiedSpectrumControl plot)
        {
            var menu=new ContextMenuStrip{Name="CompetitionSpectrumContextMenu"};
            var statistics=new ToolStripMenuItem{Name="StatisticsSelectedRegion"};
            var exclude=new ToolStripMenuItem{Name="ExcludeSelectedRegion"};
            var undoFilter=new ToolStripMenuItem{Name="UndoSharedFilter"};
            var showAll=new ToolStripMenuItem{Name="ShowAllSharedPulses"};
            var rectangle=new ToolStripMenuItem{Name="RectangleSelection"};
            var ellipse=new ToolStripMenuItem{Name="EllipseSelection"};
            var low=new ToolStripMenuItem{Name="ShowVoltageBelowU0"};
            var mid=new ToolStripMenuItem{Name="ShowVoltageU0To2U0"};
            var clearVoltage=new ToolStripMenuItem{Name="ClearVoltageShortcut"};
            var resetView=new ToolStripMenuItem{Name="ResetSpectrumView"};
            var exportData=new ToolStripMenuItem{Name="ExportSpectrumData"};
            var exportImage=new ToolStripMenuItem{Name="ExportSpectrumImage"};
            menu.Items.AddRange(new ToolStripItem[]{statistics,exclude,undoFilter,showAll,new ToolStripSeparator(),rectangle,ellipse,
                new ToolStripSeparator(),low,mid,clearVoltage,new ToolStripSeparator(),resetView,exportData,exportImage});
            statistics.Click+=(s,e)=>{if(plot.RegionSelectionEnabled&&plot.SelectionRegionCount>0)plot.RegionActionCommand=0;};
            exclude.Click+=(s,e)=>ExcludeLinkedSelection(plot);undoFilter.Click+=(s,e)=>UndoLinkedFilters();showAll.Click+=(s,e)=>ClearLinkedSelection();
            rectangle.Click+=(s,e)=>plot.SelectionShapeIndex=0;ellipse.Click+=(s,e)=>plot.SelectionShapeIndex=1;
            low.Click+=(s,e)=>SetLinkedVoltageShortcut(1);mid.Click+=(s,e)=>SetLinkedVoltageShortcut(2);clearVoltage.Click+=(s,e)=>SetLinkedVoltageShortcut(0);
            resetView.Click+=(s,e)=>plot.ResetView();exportData.Click+=(s,e)=>ExportLinkedPlot(plot,false);exportImage.Click+=(s,e)=>ExportLinkedPlot(plot,true);
            menu.Opening+=(s,e)=>{
                statistics.Text=T("Statistics selected region","统计选中区域");statistics.Enabled=plot.RegionSelectionEnabled&&plot.SelectionRegionCount>0;
                exclude.Text=T("Exclude selected pulses","排除所选脉冲");exclude.Enabled=plot.SelectionRegionCount>0?plot.GetSelectionRegionSourceRows().Length>0:linkedRegionRows!=null&&linkedRegionRows.Length>0;
                undoFilter.Text=T("Undo last filter","撤销上次筛选");undoFilter.Enabled=linkedFilterHistory.Count>0;
                showAll.Text=T("Show all pulses","显示全部脉冲");showAll.Enabled=HasLinkedFilters;
                rectangle.Text=T("Rectangle selection","矩形选区");ellipse.Text=T("Ellipse selection","椭圆选区");
                rectangle.Enabled=ellipse.Enabled=plot.RegionSelectionEnabled;rectangle.Checked=plot.SelectionShapeIndex==0;ellipse.Checked=plot.SelectionShapeIndex==1;
                low.Text=T("Show only U ≤ U0","仅显示 U≤U0");mid.Text=T("Show only U0 < U ≤ 2U0","仅显示 U0＜U≤2U0");
                low.Enabled=mid.Enabled=spectrum.OperatingVoltageKv>0&&!Double.IsInfinity(spectrum.OperatingVoltageKv)&&SourceRowCount>0;
                low.Checked=linkedVoltageShortcut==1;mid.Checked=linkedVoltageShortcut==2;
                clearVoltage.Text=T("Clear voltage filter","取消电压筛选");clearVoltage.Enabled=linkedVoltageShortcut!=0;
                resetView.Text=T("Reset view","复位视图");exportData.Text=T("Export chart data…","导出谱图数据…");exportImage.Text=T("Export chart image…","导出谱图图片…");
                exportData.Enabled=plot.VisibleSourceRowCount>0;
            };
            // The host owns the shared row mask, so its menu must not invoke the
            // vendor's local delete/acceptance commands or its local-only exit.
            plot.UseInternalContextMenu=false;plot.ContextMenuStrip=menu;
            plot.Disposed+=(s,e)=>menu.Dispose();
        }
        void ExportLinkedPlot(UnifiedSpectrumControl plot,bool image)
        {
            using(var dialog=new SaveFileDialog{Filter=image?"PNG (*.png)|*.png":"CSV (*.csv)|*.csv",DefaultExt=image?"png":"csv",AddExtension=true,
                FileName=plot.DisplayModeName+(image?".png":".csv"),Title=image?T("Export chart image","导出谱图图片"):T("Export chart data","导出谱图数据")}){
                if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return;
                try{if(image)plot.SavePng(dialog.FileName,1600,900);else plot.ExportDataCsv(dialog.FileName);}
                catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is InvalidOperationException||ex is System.Runtime.InteropServices.ExternalException){Failure(ex);}
            }
        }
    }
}
