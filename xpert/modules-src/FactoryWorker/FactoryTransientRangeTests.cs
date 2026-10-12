using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryTransientRangeTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R27 transient: "+why);++n;};
            Func<BenchCapture> make=()=>
            {
                var c=new BenchCapture { Settings=new BenchSettings { RangeIndex=8, TransientExclusionMs=.5,
                    CalibrationPulsePc=100, PdCalibrationPcPerV=2100 }, MaximumAdc=32767,
                    PicoRateHz=100000, PicoFullScaleV=5, Overflow=true, PdAdc=new short[1000] };
                for(int i=0;i<c.PdAdc.Length;i++)c.PdAdc[i]=10000;
                c.PdAdc[2]=32767;c.PdAdc[3]=Int16.MinValue;return c;
            };
            Func<BenchCapture,bool,LocationRangeResult> assess=(c,pressure)=>
            {
                var q=LocationRangeAdvisor.Evaluate(c.PdAdc,100,500,c.MaximumAdc,c.PicoFullScaleV,
                    c.Settings.RangeIndex,100,2100,c.Overflow);
                FactoryTransientRangePolicy.Apply(c,q,pressure);return q;
            };
            var source=make();var before=(short[])source.PdAdc.Clone();var quality=assess(source,true);
            check(quality.SwitchingTransientExcluded && quality.SwitchingTransientClippedSamples==2 && quality.SwitchingTransientLastClippedSample==3,"prefix evidence including negative saturation");
            check(quality.DriverOverflow && quality.DriverOverflowUncertain && !quality.LocationQualityPassed && quality.WindowRangeQualityPassed,"global flags retained; window decision separate");
            check(FactoryPressureSequence.NextRange(8,quality)==8,"known switching overshoot does not stop next range");
            check(quality.WindowStartSample==100 && quality.WindowEndExclusive==600 && quality.FullClippedSamples==2,"window and clipping untouched");
            bool rawUnchanged=source.PdAdc.Length==before.Length;
            for(int i=0;i<before.Length && rawUnchanged;i++)rawUnchanged=source.PdAdc[i]==before[i];
            check(source.Overflow && rawUnchanged,"all raw samples and original overflow retained");
            var clone=RangeDecisionPolicy.CopyQuality(quality);
            check(clone.SwitchingTransientExcluded && clone.SwitchingTransientClippedSamples==2 && clone.DriverOverflow,"XML decision snapshot carries new evidence");
            source=make();for(int i=100;i<600;i++)source.PdAdc[i]=1000;quality=assess(source,true);
            check(FactoryPressureSequence.NextRange(8,quality)<8,"window-only downrange allowed despite excluded prefix");
            source=make();source.PdAdc[100]=32767;quality=assess(source,true);
            check(!quality.SwitchingTransientExcluded && quality.LocationClippedSamples==1,"first-zero sample belongs to evaluation window");
            source=make();source.PdAdc[600]=32767;quality=assess(source,true);
            check(!quality.SwitchingTransientExcluded && FactoryTransientRangePolicy.BlocksForOverflow(quality,8),"clipping at prefix end not excluded");
            source=make();source.PdAdc[99]=32767;quality=assess(source,true);
            check(quality.SwitchingTransientExcluded && quality.SwitchingTransientEndSample==100,"entire pre-first-zero prefix excluded, not a fixed time");
            source=make();source.PdAdc[700]=32767;quality=assess(source,true);
            check(!quality.SwitchingTransientExcluded && FactoryTransientRangePolicy.BlocksForOverflow(quality,8),"late out-of-window clip not misclassified as switching");
            source=make();source.PdAdc[150]=32767;quality=assess(source,true);
            check(!quality.SwitchingTransientExcluded && quality.LocationClippedSamples==1 && FactoryPressureSequence.NextRange(8,quality)>8,"window clipping remains conservative uprange");
            source=make();source.PdAdc[2]=source.PdAdc[3]=10000;quality=assess(source,true);
            check(FactoryTransientRangePolicy.BlocksForOverflow(quality,8),"unlocated driver overflow stays blocking");
            source=make();quality=assess(source,false);
            check(!quality.SwitchingTransientExcluded,"zero/calibration modes unchanged");
            source.Settings.TransientExclusionMs=0;quality=assess(source,true);
            check(quality.SwitchingTransientExcluded,"pulse-analysis exclusion does not replace first-zero range boundary");
            quality.WindowValid=false;quality.SwitchingTransientExcluded=false;FactoryTransientRangePolicy.Apply(source,quality,true);
            check(!quality.SwitchingTransientExcluded,"invalid first-zero window remains invalid");
            source=make();source.Settings.RangeIndex=10;source.PicoFullScaleV=20;quality=assess(source,true);
            check(FactoryTransientRangePolicy.BlocksForOverflow(quality,10),"maximum-range input risk stays blocking");
            source=make();source.Overflow=false;quality=assess(source,true);
            check(quality.SwitchingTransientExcluded && !quality.DriverOverflow,"raw prefix clipping works without inventing overflow");
            var notice=new FactoryEmergencyNotice();notice.Observe("EMERGENCY_ACTIVE",false);
            check(!notice.Active,"read-only display not an active pressure emergency");
            notice.Observe("EMERGENCY_ACTIVE",true);notice.Observe("EMERGENCY_RELEASED",true);notice.Observe("STEP_COMPLETE_ERROR",true);
            check(notice.Active,"emergency survives release and generic fault");
            notice.Reset();check(!notice.Active,"explicit new display resets notice only");
            return "R27 transient range/emergency notice: "+n+" checks PASS; HardwareAccessed=False";
        }

        // Archived waveforms only; the 'run' delegate never starts a worker/backend.
        internal static async Task Replay(string output, string records)
        {
            Directory.CreateDirectory(output);var report=new StringBuilder();
            string[] names={"HW_20261007_052253_499_9b747a","HW_20261007_052311_548_af3dad","HW_20261007_052330_303_90d66d"};
            foreach(string name in names)
            {
                var r=Load(Path.Combine(records,name));var q=FactoryRangeReview.EvaluateCapturedData(r);
                int next=FactoryPressureSequence.NextRange(r.Capture.Settings.RangeIndex,q);
                if(!q.WindowValid || next!=8)throw new Exception("Actual range replay must recommend +/-5 V");
                if(name!=names[0] && (!q.SwitchingTransientExcluded || !q.DriverOverflow || !q.DriverOverflowUncertain || !q.WindowRangeQualityPassed || q.LocationQualityPassed))
                    throw new Exception("Actual global risk was cleared or switching exception missing");
                report.AppendLine(name+": current="+r.Capture.PicoFullScaleV+" V; next="+BenchSettings.Ranges[next]+" V; prefixClipped="+q.SwitchingTransientClippedSamples+
                    "; prefixEnd="+q.SwitchingTransientEndSample+"; lastClip="+q.SwitchingTransientLastClippedSample+"; windowStart="+q.WindowStartSample+"; globalOverflow="+q.DriverOverflow);
            }
            int calls=0, confirmations=0;var first=Load(Path.Combine(records,names[0]));
            int done=await FactoryPressureSequence.Run(first.Settings,5,s=>
            {
                var r=Load(Path.Combine(records,names[calls==0?0:1]));++calls;
                if(r.Settings.Input.RangeIndex!=s.Input.RangeIndex)throw new Exception("Unexpected archived range");
                return Task.FromResult(r);
            },(i,t,s)=>{++confirmations;return Task.FromResult(true);},null,CancellationToken.None,
                (r,q)=>Task.FromResult(true));
            if(done!=5 || calls!=5 || confirmations!=4)throw new Exception("Five-step archived queue regression failed");
            report.AppendLine("ARCHIVED_QUEUE_STEPS=5; SYNTHETIC_CONFIRMATIONS=4; PHYSICAL_ROUNDS=0; HARDWARE_ACCESSED=False; RECORD_WRITES=0");
            File.WriteAllText(Path.Combine(output,"transient-replay.txt"),report.ToString());
        }
        private static FactoryResult Load(string directory)
        {
            FactoryResult result;
            using(var stream=File.OpenRead(Path.Combine(directory,"result.xml")))
                result=(FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(stream);
            result.Capture=BenchCapture.Load(directory);result.DirectoryPath=directory;return result;
        }
    }
}
