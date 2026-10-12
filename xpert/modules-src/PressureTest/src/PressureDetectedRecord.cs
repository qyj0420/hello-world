using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HVX.Controls
{
    // Legacy DEM: 16 header lines, then engineering pC / kV columns.
    // Same half-open 80 kS/s bins as Task39 DetectedWaveforms, consuming
    // already-calibrated/high-passed charge so the filter is not applied twice.
    internal static class PressureDetectedRecord
    {
        internal const double RateHz = 80000;
        internal static double[][] Detect(double[] voltage, double voltageRate, double[] charge, double chargeRate, CancellationToken token)
        {
            if(voltage==null||charge==null||voltage.Length<2||charge.Length<2||!Positive(voltageRate)||!Positive(chargeRate)||chargeRate<RateHz)
                throw new InvalidDataException("检波数据或采样率无效，未保存测试记录。");
            int count=Math.Min(Boundary(Math.Min(voltage.Length/voltageRate,charge.Length/chargeRate)*RateHz,false),
                Boundary((voltage.Length-1)*RateHz/voltageRate,false)+1);
            if(count<16||count>4800)throw new InvalidDataException("检波数据长度不在有效的60 ms单段范围内。");
            var pd=new double[count];var volts=new double[count];int begin=0;
            for(int bin=0;bin<count;bin++){
                token.ThrowIfCancellationRequested();
                int end=Boundary((bin+1)*chargeRate/RateHz,true);
                if(end<=begin||end>charge.Length)throw new InvalidDataException("检波窗口无完整原始数据。");
                double peak=0;
                for(int sample=begin;sample<end;sample++){
                    if(!Finite(charge[sample]))throw new InvalidDataException("局放数据包含非有限值。");
                    peak=Math.Max(peak,Math.Abs(charge[sample]));
                }
                pd[bin]=peak;begin=end;
                double position=bin*voltageRate/RateHz,nearest=Math.Round(position);
                if(Math.Abs(position-nearest)<1e-8)position=nearest;
                int left=(int)Math.Floor(position);double fraction=position-left;
                if(left<0||left>=voltage.Length||fraction>0&&left+1>=voltage.Length)
                    throw new InvalidDataException("电压采样范围不足，未外推补齐。");
                volts[bin]=voltage[left];
                if(fraction>0)volts[bin]+=(voltage[left+1]-voltage[left])*fraction;
                if(!Finite(volts[bin]))throw new InvalidDataException("电压数据包含非有限值。");
            }
            return new[]{pd,volts};
        }
        internal static void Write(string path,double[][] data,string[] headers,CancellationToken token)
        {
            if(headers==null||headers.Length!=16||data==null||data.Length!=2||data[0].Length!=data[1].Length)
                throw new InvalidDataException("测试记录表头或列长度错误。");
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(stream,new UTF8Encoding(true))){
                foreach(string header in headers){if(header==null||header.IndexOfAny(new[]{'\r','\n'})>=0)throw new InvalidDataException("测试记录表头含非法换行。");writer.WriteLine(header);}
                for(int i=0;i<data[0].Length;i++){
                    if((i&1023)==0)token.ThrowIfCancellationRequested();
                    writer.WriteLine(data[0][i].ToString("R",CultureInfo.InvariantCulture)+"\t"+data[1][i].ToString("R",CultureInfo.InvariantCulture));
                }
            }
        }
        static int Boundary(double value,bool ceiling)
        {
            double nearest=Math.Round(value);if(Math.Abs(value-nearest)<1e-8)value=nearest;
            if(!Finite(value)||value<0||value>=Int32.MaxValue)throw new InvalidDataException("检波样点边界越界。");
            return (int)(ceiling?Math.Ceiling(value):Math.Floor(value));
        }
        static bool Positive(double v){return Finite(v)&&v>0;}
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
    }
}
