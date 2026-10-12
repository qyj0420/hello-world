using System;
using System.Collections.Generic;

namespace Xiongxin.OWTS.Desktop
{
    internal enum WithstandMode { CosineVlf, SineVlf, DirectCurrent, Sheath }
    internal enum WithstandStage { Idle, InitialDac, Running, Completed, Stopped, Failed }

    internal sealed class WithstandInitialDacEventArgs : EventArgs
    {
        public string ProjectId { get; internal set; }
        public string Phase { get; internal set; }
        public double VoltageKv { get; internal set; }
        public double CapacitanceUf { get; internal set; }
        public bool IsSimulation { get { return true; } }
    }

    // No driver, hardware interface or persistent storage is reachable from this model.
    // Its DAC history is deliberately simulation-only and local to this page instance.
    internal sealed class WithstandSimulation
    {
        readonly Dictionary<string,double> simulatedDac = new Dictionary<string,double>(StringComparer.Ordinal);
        bool cosine, combined;
        public string ProjectId { get; private set; } = "";
        public string Phase { get; private set; } = "A";
        public double TargetKv { get; private set; }
        public double MaximumKv { get; private set; }
        public int DurationSeconds { get; private set; } = 60;
        public double? ExplicitPrecheckKv { get; set; }
        public WithstandMode Mode { get; private set; } = WithstandMode.CosineVlf;
        public WithstandStage Stage { get; private set; }
        public string ErrorCode { get; private set; } = "";
        public double StageSeconds { get; private set; }
        public double ElapsedSeconds { get; private set; }
        public double? CapacitanceUf { get; private set; }
        public bool IsRunning { get { return Stage==WithstandStage.InitialDac || Stage==WithstandStage.Running; } }
        public bool RequiresDac { get { return Mode==WithstandMode.CosineVlf || Mode==WithstandMode.SineVlf; } }
        public bool InitialDacCompleted { get { return simulatedDac.ContainsKey(Key); } }
        string Key { get { return ProjectId.Length+":"+ProjectId+":"+Phase; } }
        public event EventHandler<WithstandInitialDacEventArgs> InitialDacCompletedSimulation;
        public WithstandMode[] AvailableModes { get { return combined ? new[]{WithstandMode.SineVlf,WithstandMode.CosineVlf,WithstandMode.DirectCurrent,WithstandMode.Sheath} : cosine ? new[]{WithstandMode.CosineVlf,WithstandMode.DirectCurrent,WithstandMode.Sheath} : new WithstandMode[0]; } }
        public static double? PrecheckVoltageForLimit(double maximumKv)
        {
            if(!Finite(maximumKv)||maximumKv<=0)return null;
            double estimate=maximumKv<=30?maximumKv/30:maximumKv<60?1+(maximumKv-30)/15:maximumKv/20;
            estimate=Math.Min(maximumKv,Math.Round(estimate,2,MidpointRounding.AwayFromZero));
            return estimate>0?(double?)estimate:null;
        }
        public bool PrecheckVoltageIsEstimated { get { return !ExplicitPrecheckKv.HasValue&&MaximumKv!=30&&MaximumKv!=60&&PrecheckVoltageKv.HasValue; } }
        public double? PrecheckVoltageKv
        {
            get
            {
                if(ExplicitPrecheckKv.HasValue) return Finite(ExplicitPrecheckKv.Value)&&ExplicitPrecheckKv.Value>0&&ExplicitPrecheckKv.Value<=MaximumKv ? ExplicitPrecheckKv : null;
                return PrecheckVoltageForLimit(MaximumKv);
            }
        }
        public void ConfigureCapabilities(bool supportsCosineVlf,bool supportsCombinedVlf)
        {
            if(IsRunning)throw new InvalidOperationException("test_running");
            cosine=supportsCosineVlf;combined=supportsCombinedVlf;
            if(Array.IndexOf(AvailableModes,Mode)<0&&AvailableModes.Length>0) Mode=AvailableModes[0];
        }
        public void SetContext(string projectId,string phase,double targetKv,int durationSeconds,double maximumVoltageKv)
        {
            if(IsRunning)throw new InvalidOperationException("test_running");
            ProjectId=(projectId??"").Trim();Phase=(phase??"").Trim().ToUpperInvariant();TargetKv=targetKv;DurationSeconds=durationSeconds;MaximumKv=maximumVoltageKv;
            Stage=WithstandStage.Idle;ErrorCode="";StageSeconds=ElapsedSeconds=0;CapacitanceUf=InitialDacCompleted?(double?)simulatedDac[Key]:null;
        }
        public void SetMode(WithstandMode mode)
        {
            if(IsRunning)throw new InvalidOperationException("test_running");
            if(Array.IndexOf(AvailableModes,mode)<0)throw new ArgumentOutOfRangeException("mode");
            Mode=mode;Stage=WithstandStage.Idle;ErrorCode="";
        }
        public bool Start()
        {
            if(IsRunning)return false;
            ErrorCode=ProjectId.Length==0?"project_required":Array.IndexOf(new[]{"A","B","C","ALL"},Phase)<0?"phase_required":
                Array.IndexOf(AvailableModes,Mode)<0?"mode_unavailable":!Finite(MaximumKv)||MaximumKv<=0?"limit_required":
                !Finite(TargetKv)||TargetKv<=0||TargetKv>MaximumKv?"invalid_target":DurationSeconds<=0||DurationSeconds>86400?"invalid_duration":
                RequiresDac&&!InitialDacCompleted&&!PrecheckVoltageKv.HasValue?"precheck_voltage_required":"";
            if(ErrorCode.Length>0){Stage=WithstandStage.Failed;return false;}
            StageSeconds=ElapsedSeconds=0;
            Stage=RequiresDac&&!InitialDacCompleted?WithstandStage.InitialDac:WithstandStage.Running;
            return true;
        }
        public void Advance(double seconds)
        {
            if(!Finite(seconds)||seconds<0)throw new ArgumentOutOfRangeException("seconds");
            if(!IsRunning)return;
            StageSeconds+=seconds;
            if(Stage==WithstandStage.Running){ElapsedSeconds=Math.Min(DurationSeconds,ElapsedSeconds+seconds);if(ElapsedSeconds>=DurationSeconds)Stage=WithstandStage.Completed;}
        }
        public bool CompleteInitialDac(bool success,double? capacitanceUf)
        {
            if(Stage!=WithstandStage.InitialDac)return false;
            if(!success||!capacitanceUf.HasValue||!Finite(capacitanceUf.Value)||capacitanceUf.Value<=0)
            {Stage=WithstandStage.Failed;ErrorCode=success?"invalid_capacitance":"precheck_failed";return false;}
            CapacitanceUf=capacitanceUf;simulatedDac[Key]=capacitanceUf.Value;
            var args=new WithstandInitialDacEventArgs{ProjectId=ProjectId,Phase=Phase,VoltageKv=PrecheckVoltageKv.Value,CapacitanceUf=capacitanceUf.Value};
            Stage=WithstandStage.Running;StageSeconds=ElapsedSeconds=0;
            var handler=InitialDacCompletedSimulation;if(handler!=null)handler(this,args);
            return true;
        }
        public void Stop(){if(IsRunning){Stage=WithstandStage.Stopped;ErrorCode="";}}
        public double SimulationVoltageKv
        {
            get
            {
                if(Stage==WithstandStage.InitialDac) return PrecheckVoltageKv.GetValueOrDefault()*Math.Exp(-StageSeconds*1.2)*Math.Sin(StageSeconds*Math.PI*10);
                if(Stage!=WithstandStage.Running)return 0;
                double ramp=Math.Min(1,ElapsedSeconds/2);
                return TargetKv*ramp*(Mode==WithstandMode.SineVlf?Math.Sin(ElapsedSeconds*Math.PI*.2):Mode==WithstandMode.CosineVlf?Math.Cos(ElapsedSeconds*Math.PI*.2):1);
            }
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
