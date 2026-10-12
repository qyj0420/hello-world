using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        float pulseLineWidth=1f;
        [Category("Appearance"),DefaultValue(1f),Description("局放曲线线宽，像素；入射反射高亮仅改色，不额外加粗。PD trace width in pixels, including colored incident/reflection segments.")]
        public float PulseLineWidth
        {
            get{return pulseLineWidth;}
            set{if(float.IsNaN(value)||float.IsInfinity(value)||value<=0)return;pulseLineWidth=Math.Max(.5f,Math.Min(3f,value));Invalidate();}
        }
        public void SetPulseLineWidth(float pixels){PulseLineWidth=pixels;}
        sealed class SeriesGeometry
        {
            internal WeakReference<double[]> Source;
            internal Rectangle Plot, Band;
            internal double X0, X1, Lo, Hi;
            internal int Start, End;
            internal PointF[] Points;
            internal System.Drawing.Drawing2D.GraphicsPath Path;
        }
        readonly List<SeriesGeometry> seriesGeometry = new List<SeriesGeometry>();
        sealed class FiniteRuns { internal int[][] Ranges; }
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<double[],FiniteRuns> finiteSeriesRuns = new System.Runtime.CompilerServices.ConditionalWeakTable<double[],FiniteRuns>();
        static FiniteRuns MeasureFiniteRuns(double[] values)
        {
            var runs=new List<int[]>();int index=0;
            while(index<values.Length)
            {
                while(index<values.Length&&!IsFinite(values[index]))index++;
                int start=index;
                while(index<values.Length&&IsFinite(values[index]))index++;
                if(index>start)runs.Add(new[]{start,index-1});
            }
            return new FiniteRuns{Ranges=runs.ToArray()};
        }
        double[] dampingRangeSource;
        int dampingDisplayStart,dampingDisplayEnd;

        void UpdateDampingDisplayRange()
        {
            if(ReferenceEquals(dampingRangeSource,alignedAttenuation))return;
            dampingRangeSource=alignedAttenuation;dampingDisplayStart=0;dampingDisplayEnd=-1;
            for(int i=0;i<alignedAttenuation.Length;i++)
                if(IsFinite(alignedAttenuation[i])&&alignedAttenuation[i]!=0)
                {if(dampingDisplayEnd<0)dampingDisplayStart=i;dampingDisplayEnd=i;}
        }
        void DrawAuxiliaryCurves(Graphics g,Rectangle plot,Rectangle band,double lo,double hi)
        {
            // Distinct, legible calibration trace remains behind the opaque PD data.
            if(Visible(1,false)&&showCalibration&&hasAlignedCalibrationCurve)
                DrawBatchedSeries(g,plot,alignedCalibration,calibrationColor,1.35f,calibrationOpacity,band,lo,hi,0,alignedCalibration.Length-1,true);
            if(Visible(4,false)&&showCalibration&&hasAlignedCalibrationCurve)
                DrawBatchedSeries(g,plot,filteredCalibration,filteredCalibrationColor,1.35f,calibrationOpacity,band,lo,hi,0,filteredCalibration.Length-1,true);
            UpdateDampingDisplayRange();
            if(Visible(2,false)&&showAttenuation&&HasDampingCurve)
                DrawBatchedSeries(g,plot,alignedAttenuation,attenuationColor,1f,attenuationOpacity,band,lo,hi,dampingDisplayStart,dampingDisplayEnd,true,true);
            if(Visible(5,false)&&showAttenuation&&HasFilteredDampingCurve)
                DrawBatchedSeries(g,plot,filteredAttenuation,filteredAttenuationColor,1f,attenuationOpacity,band,lo,hi,filteredDampingStart,filteredDampingEnd,true,true);
        }

        // A physical propagation interval, not another cursor or an inferred
        // reflection. Use the same cached DUT geometry as positioning, including
        // mixed materials and an already-excluded lead; never CAL's total time.
        RectangleF CablePropagationRangeRectangle(Rectangle plot)
        {
            if(pulse.Length<2||plot.Width<=0||plot.Height<=0||!IsFinite(viewX0)||!IsFinite(viewX1)||viewX1<=viewX0||
                !IsFinite(cableLengthM)||cableLengthM<=0||!IsFinite(dtNs)||dtNs<=0)return RectangleF.Empty;
            if(matrixDriven&&(originalParameterRow.Length<8||!IsFinite(originalParameterRow[5])||originalParameterRow[5]<=0||
                !IsFinite(originalParameterRow[7])||originalParameterRow[7]<=0))return RectangleF.Empty;
            if(!MixedCableCalculation&&!velocityConfigured)return RectangleF.Empty;
            double roundTrip;
            if(!TrySampleDeltaForPosition(mirrorLocation?cableLengthM:0,out roundTrip)||roundTrip<=0)return RectangleF.Empty;
            // A pre-incident review-only pick must not reverse cable propagation.
            double first=Math.Max(viewX0,incident),last=Math.Min(viewX1,incident+roundTrip);
            if(!IsFinite(last)||last<=first)return RectangleF.Empty;
            float left=Math.Max(plot.Left,X(first,plot)),right=Math.Min(plot.Right,X(last,plot));
            return right>left?new RectangleF(left,plot.Top,right-left,plot.Height):RectangleF.Empty;
        }
        void DrawCablePropagationRange(Graphics g,Rectangle plot)
        {
            RectangleF area=CablePropagationRangeRectangle(plot);
            if(area.IsEmpty)return;
            var saved=g.Save();
            try
            {
                g.SetClip(plot,System.Drawing.Drawing2D.CombineMode.Intersect);
                int first,last;
                if(TryReflectionSelectionBounds(out first,out last)&&viewX1>viewX0)
                {
                    float left=Math.Max(plot.Left,X(Math.Max(viewX0,first),plot));
                    float right=Math.Min(plot.Right,X(Math.Min(viewX1,last),plot));
                    if(right>left)using(var margin=new SolidBrush(Color.FromArgb(246,248,252)))
                        g.FillRectangle(margin,left,plot.Top,right-left,plot.Height);
                }
                using(var fill=new SolidBrush(Color.FromArgb(239,247,250)))g.FillRectangle(fill,area);
            }
            finally {g.Restore(saved);}
        }

        PointF[] SeriesPoints(double[] values,Rectangle plot,Rectangle band,double lo,double hi,int start,int end,bool cache)
        {
            start=Math.Max(0,Math.Max(start,(int)Math.Floor(viewX0)));
            end=Math.Min(values.Length-1,Math.Min(end,(int)Math.Ceiling(viewX1)));
            if(end<=start||hi==lo||viewX1<=viewX0)return Array.Empty<PointF>();
            if(cache)for(int i=0;i<seriesGeometry.Count;i++)
            {
                var item=seriesGeometry[i];
                double[] source;
                if(item.Source.TryGetTarget(out source)&&ReferenceEquals(source,values)&&item.Plot==plot&&item.Band==band&&item.X0==viewX0&&item.X1==viewX1
                    &&item.Lo==lo&&item.Hi==hi&&item.Start==start&&item.End==end)
                {seriesGeometry.RemoveAt(i);seriesGeometry.Add(item);return item.Points;}
            }
            int count=end-start+1,bins=Math.Max(1,plot.Width);
            var points=new List<PointF>(Math.Min(count,bins*4));
            double xs=plot.Width/(viewX1-viewX0),ys=band.Height/(hi-lo);
            Action<int> add=i=>points.Add(new PointF((float)(plot.Left+(i-viewX0)*xs),(float)(band.Bottom-(values[i]-lo)*ys)));
            if(count<=bins*2)for(int i=start;i<=end;i++)add(i);
            else for(int b=0;b<bins;b++)
            {
                int first=start+(int)((long)b*count/bins),last=start+(int)((long)(b+1)*count/bins)-1;
                int min=first,max=first;
                for(int i=first+1;i<=last;i++){if(values[i]<values[min])min=i;if(values[i]>values[max])max=i;}
                int earlier=Math.Min(min,max),later=Math.Max(min,max);
                add(first);
                if(earlier>first)add(earlier);
                if(later>earlier&&later>first)add(later);
                if(last>later&&last>first)add(last);
            }
            var result=points.ToArray();
            if(cache)
            {
                if(seriesGeometry.Count>=12){seriesGeometry[0].Path.Dispose();seriesGeometry.RemoveAt(0);}
                var path=new System.Drawing.Drawing2D.GraphicsPath();path.AddLines(result);
                seriesGeometry.Add(new SeriesGeometry{Source=new WeakReference<double[]>(values),Plot=plot,Band=band,X0=viewX0,X1=viewX1,Lo=lo,Hi=hi,Start=start,End=end,Points=result,Path=path});
            }
            return result;
        }
        double[] rangedPulse;
        double pulseMinimum, pulseMaximum;
        void PulseRange(out double lo,out double hi)
        {
            if(!ReferenceEquals(rangedPulse,pulse))
            {
                rangedPulse=pulse;pulseMinimum=Min(pulse);pulseMaximum=Max(pulse);
                if(pulseMinimum==pulseMaximum){pulseMinimum--;pulseMaximum++;}
            }
            lo=pulseMinimum;hi=pulseMaximum;
        }

        void DrawBatchedSeries(Graphics g,Rectangle plot,double[] values,Color color,float width,double alpha,Rectangle band,double lo,double hi,int start,int end,bool cache,bool dashed=false)
        {
            var runs=finiteSeriesRuns.GetValue(values,MeasureFiniteRuns).Ranges;
            foreach(var run in runs)
            {
                int first=Math.Max(start,run[0]),last=Math.Min(end,run[1]);
                if(last>first)DrawFiniteSeries(g,plot,values,color,width,alpha,band,lo,hi,first,last,cache,dashed);
            }
        }
        void DrawFiniteSeries(Graphics g,Rectangle plot,double[] values,Color color,float width,double alpha,Rectangle band,double lo,double hi,int start,int end,bool cache,bool dashed)
        {
            PointF[] points=SeriesPoints(values,plot,band,lo,hi,start,end,cache);
            if(points.Length<2)return;
            using(var pen=new Pen(Color.FromArgb((int)(255*Clamp(alpha,0,1)),color),width))
            {
                if(dashed)pen.DashStyle=System.Drawing.Drawing2D.DashStyle.Dash;
                if(cache)g.DrawPath(pen,seriesGeometry[seriesGeometry.Count-1].Path);
                else g.DrawLines(pen,points);
            }
        }

        void DrawVisiblePulseWindows(Graphics g, Rectangle plot, Rectangle band, double lo, double hi)
        {
            bool rawVisible = Visible(0, true), filteredVisible = Visible(3, false);
            if (rawVisible)
            {
                DrawCurveWindow(g, plot, band, pulse, incident, incidentColor, pulseLineWidth, 1, lo, hi);
                if(ReflectionWithinWaveform)DrawCurveWindow(g, plot, band, pulse, reflection, reflectionColor, pulseLineWidth, 1, lo, hi);
            }
            if (filteredVisible)
            {
                DrawCurveWindow(g, plot, band, filteredPulse, incident, incidentColor, pulseLineWidth, 1, lo, hi);
                if(ReflectionWithinWaveform)DrawCurveWindow(g, plot, band, filteredPulse, reflection, reflectionColor, pulseLineWidth, 1, lo, hi);
            }
        }

        void DrawCurveWindow(Graphics g, Rectangle plot, Rectangle band, double[] values, int index, Color color, float width, double alpha, double lo, double hi)
        {
            if (values.Length < 2) return;
            // Repaint the exact cached full-curve geometry inside the source-index window. Re-sampling a
            // short window independently would follow a different polyline when the main curve is dense.
            double start = Math.Max(viewX0, Math.Max(0, index - (double)n1));
            double end = Math.Min(viewX1, Math.Min(values.Length - 1, index + (double)n2));
            if (end <= start) return;
            float left = X(start, plot), right = X(end, plot);
            var state = g.Save();
            try
            {
                g.SetClip(new RectangleF(left, plot.Top, right - left, plot.Height), System.Drawing.Drawing2D.CombineMode.Intersect);
                DrawBatchedSeries(g, plot, values, color, width, alpha, band, lo, hi, 0, values.Length - 1, true);
            }
            finally { g.Restore(state); }
        }

        void DisposeSeriesGeometry()
        {
            foreach(var item in seriesGeometry)item.Path.Dispose();
            seriesGeometry.Clear();
        }
    }
}
