using System;
using System.IO;
using System.Linq;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryCalibrationDurationTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Calibration duration mode: "+why);checks++;};
            foreach(double duration in new[]{1.0,59,60})
            {
                var input=new BenchSettings {RequestedPicoRateHz=1000000,CaptureDurationMs=duration};
                input.ValidateForNewRun();check(input.CaptureDurationMs==duration,"bounded new duration is accepted without clamping");
            }
            foreach(double duration in new[]{0.0,60.0001,61,100,1000,Double.NaN,Double.PositiveInfinity})
            {
                var input=new BenchSettings {RequestedPicoRateHz=1000000,CaptureDurationMs=duration};
                check(Fails(input.ValidateForNewRun),"new duration rejected: "+duration);
            }
            foreach(FactoryRunKind kind in new[]{FactoryRunKind.Pressure,FactoryRunKind.Calibration})
            {
                var fake=new FactoryTests.Fixture();fake.Settings.Input.CaptureDurationMs=61;
                check(Fails(()=>fake.Run(null,kind)) && fake.Backend.InitializeCount==0,
                    "61 ms "+kind+" fails before any fake initialization");
            }
            foreach(double duration in new[]{20.0,60})
            {
                var fake=new FactoryTests.Fixture();fake.Settings.Input.CaptureDurationMs=duration;fake.Settings.CalibrationPulseCount=100;
                var round=fake.Run(null,FactoryRunKind.Calibration);
                check(round.Capture.Settings.CaptureDurationMs==duration && round.Settings.CalibrationPulseCount==100 &&
                    fake.Settings.Input.CaptureDurationMs==duration,"legacy target count neither expands nor rewrites the calibration request");
                check(fake.Backend.FreeRun && fake.Backend.InitializeCount==1 && fake.Backend.HvHighCount==0 &&
                    fake.Backend.OpticalHighCount==0 && fake.Backend.PositiveAoWrites==0,
                    "bounded calibration is one input-only fake acquisition, without target-count retry");
            }
            foreach(int legacyTarget in new[]{0,-1})
            {
                var fake=new FactoryTests.Fixture();fake.Settings.CalibrationPulseCount=legacyTarget;
                var round=fake.Run(null,FactoryRunKind.Calibration);
                check(round.Settings.CalibrationPulseCount==legacyTarget && fake.Backend.InitializeCount==1 &&
                    round.Capture.Settings.CaptureDurationMs==60,
                    "unused legacy target metadata does not reject or alter a new time-segment calibration");
            }

            var capture=Segment(60);var original=(short[])capture.PdAdc.Clone();double coefficient=Double.NaN;
            foreach(int legacyTarget in new[]{3,8,20,100,0,-1})
            {
                var result=FactoryCalibrationAdapter.Calibrate(capture,100,100,legacyTarget);
                check(result.Valid && result.PulseCount==6 && result.CompleteWindowCount==6 &&
                    FactorySettings.Positive(result.CoefficientPcPerV),"all six valid pulses are used independently of legacy target "+legacyTarget);
                if(Double.IsNaN(coefficient))coefficient=result.CoefficientPcPerV;
                check(Math.Abs(result.CoefficientPcPerV-coefficient)<1e-12,"changing the unused target cannot change the coefficient");
            }
            check(original.SequenceEqual(capture.PdAdc) && capture.Settings.CaptureDurationMs==60,
                "time-segment calibration preserves all original ADC values and duration");
            var varied=Segment(60);
            for(int i=54998;i<=55002;i++)
            {
                int background=i%2==0?2:-2;
                varied.PdAdc[i]=(short)Math.Round(background+(varied.PdAdc[i]-background)*1.4);
            }
            var variedResult=FactoryCalibrationAdapter.Calibrate(varied,100,100,3);
            check(variedResult.Valid && variedResult.CompleteWindowCount==6 && variedResult.CoefficientPcPerV<coefficient*.95,
                "a valid sixth pulse beyond the old first-three cutoff contributes to the aligned average");
            var empty=Segment(60);Array.Clear(empty.PdAdc,0,empty.PdAdc.Length);
            var noPulses=FactoryCalibrationAdapter.Calibrate(empty,100,100,8);
            check(!noPulses.Valid && !FactorySettings.Positive(noPulses.CoefficientPcPerV),"zero valid pulses cannot manufacture a coefficient");
            var two=FactoryCalibrationAdapter.Calibrate(Segment(20),100,100,100);
            check(!two.Valid && two.Detail.Contains("周期") && !two.Detail.Contains("要求 100"),
                "independent periodicity quality remains explicit; failure is not an unmet legacy target");
            var clipped=Segment(60);clipped.PdAdc[5000]=32767;
            check(!FactoryCalibrationAdapter.Calibrate(clipped,100,100,3).Valid,"raw clipping still prevents calibration application");
            var overflow=Segment(60);overflow.Overflow=true;
            check(!FactoryCalibrationAdapter.Calibrate(overflow,100,100,3).Valid,"raw overflow quality cannot be cleared by time-segment averaging");
            check(!FactoryCalibrationAdapter.Calibrate(capture,Double.NaN,100,3).Valid,
                "nonfinite known charge cannot yield an applicable coefficient");
            check(!FactoryCalibrationAdapter.Calibrate(capture,Double.MaxValue,100,3).Valid,
                "a finite known charge that overflows the computed coefficient is not applicable");

            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-CalibrationDuration-"+Guid.NewGuid().ToString("N"));
            var legacy=FactoryCompatibility100Tests.CalibrationCapture();legacy.Save(root);
            byte[] settingsBefore=File.ReadAllBytes(Path.Combine(root,"settings.xml"));
            byte[] rawBefore=File.ReadAllBytes(Path.Combine(root,"capture.bin"));
            var restored=BenchCapture.Load(root);restored.ValidateCapture();
            check(restored.Settings.CaptureDurationMs==100 && restored.PdAdc.SequenceEqual(legacy.PdAdc),
                "old 100 ms capture remains readable with its complete original samples");
            check(Fails(restored.Settings.ValidateForNewRun),"an old long capture cannot authorize a new long acquisition");
            check(settingsBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"settings.xml"))) &&
                rawBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"capture.bin"))),
                "historical validation and rejected new-run use leave both saved files unchanged");
            return "60 ms / time-segment calibration: "+checks+" checks PASS; all valid pulses, no expansion, history preserved; HardwareAccessed=False; "+root;
        }

        private static BenchCapture Segment(double duration)
        {
            var capture=FactoryCompatibility100Tests.CalibrationCapture();
            capture.Settings.CaptureDurationMs=duration;
            var samples=new short[capture.Settings.RequestedPicoCount];Array.Copy(capture.PdAdc,samples,samples.Length);
            capture.PdAdc=samples;capture.VoltageRaw=new double[capture.Settings.ExpectedArtCount];return capture;
        }
        private static bool Fails(Action action)
        {try{action();return false;}catch(ArgumentException){return true;}}
    }
}
