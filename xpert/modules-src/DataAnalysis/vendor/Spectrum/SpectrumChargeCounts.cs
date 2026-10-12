using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    internal sealed class ChargeCountCell
    {
        internal int Phase, Bin, Count;
        internal double Voltage, Lower, Upper, HalfVoltageWidth;
    }

    internal static class ChargeCountBinning
    {
        // Bin the complete filtered source view once, not pixels or the current
        // zoom window. Upper bound belongs to the final bin; empty bins are absent.
        internal static List<ChargeCountCell> Build(SpectrumView view,int bins)
        {
            var result=new List<ChargeCountCell>();
            if(view.Points.Count==0)return result;
            double maximum=view.Points.Max(p=>p.Y);if(maximum<=0)maximum=1;
            var voltages=view.Points.Select(p=>p.Z).Distinct().OrderBy(v=>v).ToArray();
            double totalSpan=voltages[voltages.Length-1]-voltages[0];
            var widths=new Dictionary<double,double>();
            for(int i=0;i<voltages.Length;i++)
            {
                double gap=double.PositiveInfinity;
                if(i>0)gap=Math.Min(gap,voltages[i]-voltages[i-1]);
                if(i+1<voltages.Length)gap=Math.Min(gap,voltages[i+1]-voltages[i]);
                widths[voltages[i]]=double.IsInfinity(gap)?Math.Max(.02,Math.Abs(voltages[i])*.03):Math.Min(gap*.35,totalSpan*.025);
            }
            var cells=new Dictionary<Tuple<int,double,int>,ChargeCountCell>();
            foreach(var point in view.Points)
            {
                int bin=SpectrumDensity.RangeIndex(point.Y,0,maximum,bins);
                var key=Tuple.Create(point.Phase,point.Z,bin);ChargeCountCell cell;
                if(!cells.TryGetValue(key,out cell))
                {
                    cell=new ChargeCountCell{Phase=point.Phase,Voltage=point.Z,Bin=bin,
                        Lower=maximum*((double)bin/bins),Upper=maximum*((double)(bin+1)/bins),HalfVoltageWidth=widths[point.Z]};
                    cells.Add(key,cell);
                }
                cell.Count=checked(cell.Count+point.SourceRows.Length);
            }
            return cells.Values.OrderBy(c=>c.Phase).ThenBy(c=>c.Voltage).ThenBy(c=>c.Bin).ToList();
        }
    }

    public partial class UnifiedSpectrumControl
    {
        bool showChargeCountOverlay=true;
        [Category("Appearance"),DefaultValue(true)]
        public bool ShowChargeCountOverlay
        {
            get{return showChargeCountOverlay;}
            set{if(showChargeCountOverlay==value)return;showChargeCountOverlay=value;DropDrawingCaches();SyncCameraToolbar();Invalidate();}
        }
        [Browsable(false)]public bool ChargeCountOverlayAvailable {get{return mode==5&&Camera.PlaneIndex==3;}}
        [Browsable(false)]public int ChargeCountMaximum
        {get{return mode==5&&view!=null&&view.ChargeCounts.Count>0?view.ChargeCounts.Max(c=>c.Count):0;}}
        /// <summary>Fresh rows: phase, voltage kV, lower |Q| pC, upper |Q| pC, pulse count.
        /// Includes the current acceptance, phase and statistics filters, not viewport clipping.</summary>
        public double[,] GetChargeCountBins()
        {
            if(mode!=5||view==null)return new double[0,5];
            var data=new double[view.ChargeCounts.Count,5];
            for(int i=0;i<view.ChargeCounts.Count;i++)
            {var c=view.ChargeCounts[i];data[i,0]=c.Phase;data[i,1]=c.Voltage;data[i,2]=c.Lower;data[i,3]=c.Upper;data[i,4]=c.Count;}
            return data;
        }
        void DrawChargeCountOverlay(Graphics g,Rectangle area)
        {
            if(!ShowChargeCountOverlay||!ChargeCountOverlayAvailable||view.ChargeCounts.Count==0)return;
            int maximum=ChargeCountMaximum;
            var phases=view.ChargeCounts.Select(c=>c.Phase).Distinct().OrderBy(p=>p).ToArray();
            foreach(var cell in view.ChargeCounts)
            {
                // Separate visible phases within each voltage column. The cells
                // remain counts of real pulses, never resampled scatter points.
                int phaseIndex=Array.IndexOf(phases,cell.Phase);
                double start=Math.Max(view.ZMin,cell.Voltage-cell.HalfVoltageWidth);
                double end=Math.Min(view.ZMax,cell.Voltage+cell.HalfVoltageWidth);
                double width=(end-start)/phases.Length;
                double left=start+phaseIndex*width;
                var p1=Project(new SpectrumPoint{X=0,Y=cell.Lower,Z=left},area);
                var p2=Project(new SpectrumPoint{X=0,Y=cell.Upper,Z=left+width},area);
                var rect=RectangleF.FromLTRB(Math.Min(p1.X,p2.X),Math.Min(p1.Y,p2.Y),Math.Max(p1.X,p2.X),Math.Max(p1.Y,p2.Y));
                if(rect.Width<=0||rect.Height<=0||float.IsInfinity(rect.Width)||float.IsInfinity(rect.Height))continue;
                int alpha=12+(int)(42*Math.Sqrt((double)cell.Count/maximum));
                using(var brush=new SolidBrush(Color.FromArgb(alpha,PhaseColors[cell.Phase])))g.FillRectangle(brush,rect);
            }
        }
    }
}
