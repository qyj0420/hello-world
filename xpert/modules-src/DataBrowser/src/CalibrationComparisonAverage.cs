using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        sealed class AverageComparisonSource
        {
            internal BrowserRecord Record;
            internal string Hash;
            internal double Origin, Baseline, Scale;
            internal CalibrationComparisonWaveExtent Extent;
            internal CalibrationTraceGeometry Geometry;
        }

        // Two passes retain only one full source waveform at a time. Never average
        // the peak-preserving display previews, whose sample positions differ.
        ComparisonPrepared PrepareAverageComparison(BrowserRecord[] records,bool normalize,bool align,CancellationToken token)
        {
            var result=new ComparisonPrepared();
            var sources=new List<AverageComparisonSource>();
            foreach(var record in records)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string hash=ComparisonSourceHash(record.Path,token);
                    var wave=BrowserReader.LoadWaveform(record,token);
                    if(wave.X==null||wave.Y==null||wave.Y.Length<16||wave.X.Length!=wave.Y.Length)
                        throw new InvalidDataException("波形数据不完整。");
                    double origin=align?wave.IncidentSeconds:0;
                    if(align&&(!BrowserPlot.Finite(origin)||origin<wave.X[0]||origin>wave.X[wave.X.Length-1]))
                        throw new InvalidDataException("缺少有效入射游标；可取消入射对齐。");
                    var extent=CalibrationComparisonMath.Inspect(wave.X,wave.Y,origin,token);
                    bool validIncident=BrowserPlot.Finite(wave.IncidentSeconds)&&wave.IncidentSeconds>=wave.X[0]&&wave.IncidentSeconds<=wave.X[wave.X.Length-1];
                    int count=BrowserPlot.Finite(wave.IncidentSampleIndex)&&wave.IncidentSampleIndex>=16
                        ?Math.Min(200,(int)Math.Min(wave.Y.Length,wave.IncidentSampleIndex)/2)
                        :Math.Max(8,Math.Min(200,wave.Y.Length/20));
                    double baseline=normalize?wave.Y.Take(count).Average():0,scale=1;
                    if(normalize)
                    {
                        scale=0;
                        for(int i=0;i<wave.Y.Length;i++)
                        {if((i&65535)==0)token.ThrowIfCancellationRequested();scale=Math.Max(scale,Math.Abs(wave.Y[i]-baseline));}
                        if(!BrowserPlot.Finite(scale)||scale<=1e-15)throw new InvalidDataException("零幅值或无效幅值不能归一化。");
                    }
                    if(hash!=ComparisonSourceHash(record.Path,token))throw new IOException("文件在读取中发生变化，请重新加载。");
                    sources.Add(new AverageComparisonSource{Record=record,Hash=hash,Extent=extent,Origin=origin,Baseline=baseline,Scale=scale,
                        Geometry=new CalibrationTraceGeometry{Name=record.Name,Phase=record.Phase,Kind=wave.MeasurementKind,IncidentSeconds=validIncident?wave.IncidentSeconds:Double.NaN,ReflectionSeconds=wave.ReflectionSeconds,
                            LengthM=wave.CableLengthMetres,VelocityMPerUs=wave.VelocityMPerUs,SampleIntervalSeconds=extent.MinimumIntervalSeconds}});
                }
                catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is InvalidOperationException||ex is FormatException||ex is OverflowException)
                {result.Errors.Add(record.Name+"："+ex.Message);}
            }
            if(sources.Count==0)return result;
            // Display averaging uses the measured time coordinates, not cable
            // geometry. Incompatible/unknown geometry disables only location
            // projection and joint operations; never invent a common distance.
            try { result.Geometry=CalibrationTraceGeometry.Compatible(sources.Select(s=>s.Geometry).ToArray(),align); }
            catch(InvalidDataException ex) { result.GeometryWarning=ex.Message; }
            var plan=CalibrationComparisonMath.PlanAverage(sources.Select(s=>s.Extent).ToArray(),4000000,token);
            var accumulator=new CalibrationComparisonAverageAccumulator(plan);
            foreach(var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if(source.Hash!=ComparisonSourceHash(source.Record.Path,token))throw new IOException(source.Record.Name+"：文件在平均计算中发生变化，请重新加载。");
                var wave=BrowserReader.LoadWaveform(source.Record,token);
                accumulator.Add(wave.X,wave.Y,source.Origin,source.Baseline,source.Scale,token);
                // A change after the validation pass invalidates the complete result,
                // rather than silently changing the number of contributors per point.
                if(source.Hash!=ComparisonSourceHash(source.Record.Path,token))throw new IOException(source.Record.Name+"：文件在平均计算中发生变化，请重新加载。");
            }
            var mean=accumulator.Complete(token);
            foreach(var source in sources)result.SourceHashes[source.Record.Path]=source.Hash;
            result.MeanX=mean.XSeconds;result.MeanY=mean.Y;
            if(result.Geometry!=null)result.Geometry.SampleIntervalSeconds=plan.IntervalSeconds;
            var preview=ComparisonPreview(new BrowserWaveform{X=mean.XSeconds,Y=mean.Y},0,"平均",0,1,0,8192,token);
            preview.Name="平均（"+mean.TraceCount+" 条校准）";preview.Dashed=false;
            result.Series.Add(preview);result.SourceCount=mean.TraceCount;
            result.Details="按当前归一化/对齐设置，对完整波形等权平均，再抽样显示；不对平均结果二次归一化。共同时间范围："+
                (plan.StartSeconds*1e6).ToString("G7",CultureInfo.CurrentCulture)+" ～ "+(plan.EndSeconds*1e6).ToString("G7",CultureInfo.CurrentCulture)+" μs；线性插值，不补零或外推。\r\n";
            if(result.Geometry==null)result.Details+="已显示时间轴平均。所选记录的定位参数不能共用，平均图的距离及接头功能不可用。\r\n"+result.GeometryWarning+"\r\n";
            else if(result.Geometry.Kind!="Capacitor")result.Details+="传播阴影：入射至入射 + 2L/v；距离按当前平均时基计算。接头查找仅给人工复核候选，不改原波形或系数。\r\n";
            return result;
        }
    }
}
