using System;
using System.IO;
using Xiongxin.FactoryTest;
using Xiongxin.ZeroBench;

// Minimal data-only stand-ins; the production phase analyzer is compiled as-is.
namespace Xiongxin.ZeroBench {
    public sealed class BenchSettings {public bool OscillationCircuitConnected;public int ArtPreTriggerCount;}
    public sealed class BenchCapture {public BenchSettings Settings=new BenchSettings();public double[] VoltageRaw;public double ArtRateHz;public double ArtFullScaleV=10;}
}
namespace Xiongxin.FactoryTest {internal static class FactorySettings {
    internal static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
    internal static bool Positive(double v){return Finite(v)&&v>0;}
}}
internal static class FactoryRangePhaseReasonSmoke {
    static int checks;static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static int Main(string[] args){try {
        var c=new BenchCapture{ArtRateHz=80000,VoltageRaw=new double[1600]};
        for(int i=0;i<c.VoltageRaw.Length;i++)c.VoltageRaw[i]=2*Math.Cos(2*Math.PI*500*i/c.ArtRateHz)*Math.Exp(-i/2000d);
        var r=FactoryRangePhase.Analyze(c);Check(r.Valid&&r.FirstZeroMs>0&&r.PeriodMs>0,"legacy feature false does not disable measured oscillation");
        Check(!c.Settings.OscillationCircuitConnected,"analyzer never manufactures a wiring confirmation");
        var previous=r;c.Settings.OscillationCircuitConnected=true;r=FactoryRangePhase.Analyze(c);
        Check(r.Valid&&r.FirstZeroMs==previous.FirstZeroMs&&r.PeriodMs==previous.PeriodMs,"legacy feature true/false produces identical raw phase evidence");
        c.ArtRateHz=0;r=FactoryRangePhase.Analyze(c);Check(!r.Valid&&r.Detail.Contains("采样率"),"invalid clock is not mislabeled absent voltage");
        c.ArtRateHz=80000;double sample=c.VoltageRaw[10];c.VoltageRaw[10]=Double.NaN;
        Check(!FactoryRangePhase.Analyze(c).Valid,"nonfinite raw voltage remains invalid");c.VoltageRaw[10]=10;
        Check(!FactoryRangePhase.Analyze(c).Valid,"raw voltage clipping remains invalid");c.VoltageRaw[10]=sample;
        c.ArtFullScaleV=0;Check(!FactoryRangePhase.Analyze(c).Valid,"invalid voltage full scale remains invalid");c.ArtFullScaleV=10;
        Array.Clear(c.VoltageRaw,0,c.VoltageRaw.Length);r=FactoryRangePhase.Analyze(c);
        Check(!r.Valid&&r.Detail.Contains("信噪比"),"flat voltage cannot fabricate a reliable crossing");
        c.VoltageRaw=null;r=FactoryRangePhase.Analyze(c);Check(!r.Valid&&r.Detail.Contains("缺少原始电压"),"missing raw voltage is reported specifically");
        Check(!FactoryRangePhase.Analyze(null).Valid,"missing capture remains invalid");
        string evidence="FACTORY_RANGE_PHASE_REASON_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0; NativeCalls=false";
        File.WriteAllText(Path.Combine(args[0],"range-phase-reason.txt"),evidence);Console.WriteLine(evidence);return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
