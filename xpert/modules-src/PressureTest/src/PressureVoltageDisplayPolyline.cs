using System;
using System.Drawing;

namespace HVX.Controls
{
    internal sealed partial class PressureWaveformView
    {
        PointF[] cachedVoltageDrawingPoints = new PointF[0];
        double[] cachedVoltageDrawingSource;
        Rectangle cachedVoltageDrawingPlot;
        double cachedVoltageDrawingDuration,cachedVoltageDrawingMaximum,cachedVoltageDrawingStart,cachedVoltageDrawingRate;
        long voltageDrawingBuildCount;

        void ResetVoltageDrawingCache()
        { cachedVoltageDrawingSource=null;cachedVoltageDrawingPoints=new PointF[0]; }

        // Dense voltage display is a pixel-average line, NOT an extrema zigzag.
        // This is explicitly display-only: original samples, measured peak,
        // timing, protections, saved values and all algorithm inputs remain as
        // retained. Zooming exposes the actual three-tap display sample values.
        // Cache only geometry; it never sends parameter/device notifications.
        internal PointF[] GetVoltageDrawingPoints(Rectangle plot,double duration,double maximum)
        {
            double rate=cachedTimeKnown?voltageRateHz:1,start=DisplayStart;
            if(ReferenceEquals(cachedVoltageDrawingSource,displayVoltage)&&cachedVoltageDrawingPlot==plot&&
                cachedVoltageDrawingDuration==duration&&cachedVoltageDrawingMaximum==maximum&&
                cachedVoltageDrawingStart==start&&cachedVoltageDrawingRate==rate)return cachedVoltageDrawingPoints;
            var points=new PointF[0];
            if(displayVoltage.Length>=2&&plot.Width>0&&plot.Height>0&&IsFinitePositive(duration)&&IsFinitePositive(maximum))
            {
                int first=Math.Min(displayVoltage.Length-1,(int)Math.Ceiling(start*rate-1e-9));
                int last=VisibleLastIndex(start+duration,rate,displayVoltage.Length);
                if(start>0)last=Math.Min(last,(int)Math.Ceiling((start+duration)*rate-1e-9)-1);
                int count=Math.Max(0,last-first+1);
                if(count<=Math.Max(2,plot.Width*2))
                {
                    points=new PointF[count];
                    for(int i=0;i<count;i++)points[i]=MapVoltagePoint(first+i,plot,duration,maximum);
                }
                else
                {
                    int buckets=Math.Max(1,plot.Width);
                    points=new PointF[buckets+2];
                    points[0]=MapVoltagePoint(first,plot,duration,maximum);
                    for(int bucket=0;bucket<buckets;bucket++)
                    {
                        int begin=first+(int)((long)bucket*count/buckets);
                        int end=first+(int)((long)(bucket+1)*count/buckets);
                        double mean=displayVoltage[begin];
                        for(int i=begin+1;i<end;i++)
                        {
                            // A scaled convex mean also stays finite for valid
                            // opposite-sign Double.MaxValue drawing fixtures.
                            double value=displayVoltage[i],scale=Math.Max(Math.Abs(mean),Math.Abs(value));
                            if(scale==0)continue;
                            double weight=1d/(i-begin+1);
                            double unit=(mean/scale)*(1-weight)+(value/scale)*weight;
                            mean=Math.Max(-1,Math.Min(1,unit))*scale;
                        }
                        double index=((long)begin+end-1)/2d;
                        float x=plot.Left+(float)((index/rate-start)/duration*plot.Width);
                        float y=plot.Top+(float)(VoltageDisplayYRatio(mean,maximum)*plot.Height);
                        points[bucket+1]=new PointF(x,y);
                    }
                    points[points.Length-1]=MapVoltagePoint(last,plot,duration,maximum);
                }
            }
            voltageDrawingBuildCount++;
            cachedVoltageDrawingSource=displayVoltage;cachedVoltageDrawingPlot=plot;
            cachedVoltageDrawingDuration=duration;cachedVoltageDrawingMaximum=maximum;
            cachedVoltageDrawingStart=start;cachedVoltageDrawingRate=rate;
            return cachedVoltageDrawingPoints=points;
        }
    }
}
