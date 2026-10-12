using System;
using System.IO;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryLocationCycleManifest
    {
        public bool Valid, Simulation, Shortened;
        public int Cycles, StartSample, EndExclusive, MaximumAdc;
        public double SampleRateHz, FullScaleV, CoefficientPcPerV;
        public string ArtSerial, PicoSerial, Definition, DataFile;
    }
    internal static class FactoryLocationCycleArchive
    {
        internal static void Save(FactoryResult r,string directory)
        {
            var q=r.Metrics==null?null:r.Metrics.RangeQuality;
            if(q==null || !q.FactoryCycleWindow || r.Capture==null)return;
            var c=r.Capture;
            var m=new FactoryLocationCycleManifest {Valid=q.WindowValid,Simulation=c.Simulation,Shortened=q.WindowShortened,
                Cycles=q.EvaluationCycles,StartSample=q.WindowStartSample,EndExclusive=q.WindowEndExclusive,
                MaximumAdc=c.MaximumAdc,SampleRateHz=c.PicoRateHz,FullScaleV=c.PicoFullScaleV,
                CoefficientPcPerV=c.Settings.PdCalibrationPcPerV,ArtSerial=c.ArtSerial,PicoSerial=c.PicoSerial,
                Definition=q.WindowDefinition,DataFile=q.WindowValid?"location-ncycles.bin":""};
            if(q.WindowValid)
            {
                if(q.WindowStartSample<0 || q.WindowEndExclusive>c.PdAdc.Length || q.LocationSampleCount!=q.WindowEndExclusive-q.WindowStartSample)
                    throw new InvalidDataException("定位保存窗口与量程窗口不一致。");
                using(var file=new FileStream(Path.Combine(directory,m.DataFile),FileMode.CreateNew))
                using(var w=new BinaryWriter(file))
                {
                    w.Write("XIONGXIN-LOCATION-NCYCLES-1");w.Write(q.WindowStartSample);w.Write(q.LocationSampleCount);
                    w.Write(c.PicoRateHz);w.Write(c.MaximumAdc);w.Write(c.PicoFullScaleV);
                    for(int i=q.WindowStartSample;i<q.WindowEndExclusive;i++)w.Write(c.PdAdc[i]);
                }
            }
            FactoryDataArchive.SaveXml(m,Path.Combine(directory,"location-window.xml"));
        }
    }
}
