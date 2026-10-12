using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryTriggerThresholdDecision
    {
        public DateTime DecisionUtc;
        public string ArtSerial, PicoSerial, SequenceId, RecordDirectory, Detail;
        public bool Simulation, Changed;
        public int Round, Count, Attempt, ConsecutiveFailures;
        public double OldVolts, NewVolts;
    }
    // Session-local streak, separate for each exact card pair and mode. A
    // manual threshold change or successful synchronous round starts a new streak.
    internal sealed class FactoryTriggerThresholdPolicy
    {
        internal static readonly FactoryTriggerThresholdPolicy Shared=new FactoryTriggerThresholdPolicy();
        private sealed class Streak {internal int Count;internal double Volts;}
        private readonly Dictionary<string,Streak> streaks=new Dictionary<string,Streak>();
        private readonly HashSet<string> processed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate=new object();
        private static string Key(FactorySettings s)
        { return (s.Simulation?"SIM":"HW")+":"+s.Input.ArtSerial.Length+":"+s.Input.ArtSerial+":"+s.Input.PicoSerial; }
        internal void Success(FactorySettings s)
        {lock(gate)streaks.Remove(Key(s));}
        internal FactoryTriggerThresholdDecision Failure(FactoryResult r)
        {
            if(r==null || r.Settings==null || !r.Settings.UseOpticalTimeoutRetry || r.Kind!=FactoryRunKind.Pressure ||
                r.TriggerRecovery==null || !r.TriggerRecovery.InitialTimeout || !r.TriggerRecovery.EligibleForThresholdAdjustment ||
                r.Completed || r.AcquisitionCompleted || r.ResidualSafe)
                throw new InvalidOperationException("不是可计数的纯联动触发失败；不改变阈值。");
            var s=r.Settings;double old=s.Input.ExternalTriggerVolts;
            if(!FactorySettings.Positive(old) || old>=5 || String.IsNullOrWhiteSpace(r.DirectoryPath) || !Directory.Exists(r.DirectoryPath))
                throw new InvalidOperationException("阈值或失败档案路径无效，未应用调整。");
            lock(gate)
            {
                string path=Path.GetFullPath(r.DirectoryPath),key=Key(s);
                if(processed.Contains(path))throw new InvalidOperationException("该轮已处理，不能重复计数或降阈值。");
                Streak previous;int n=streaks.TryGetValue(key,out previous) && previous.Volts==old ? previous.Count+1 : 1;
                double next=n>=3 && old>.5 ? Math.Max(.5,Math.Round(old*.75,6,MidpointRounding.AwayFromZero)) : old;
                var d=new FactoryTriggerThresholdDecision {DecisionUtc=DateTime.UtcNow,ArtSerial=s.Input.ArtSerial,PicoSerial=s.Input.PicoSerial,
                    Simulation=s.Simulation,SequenceId=s.SequenceId,Round=s.SequenceRound,Count=s.SequenceCount,Attempt=s.SequenceAttempt,
                    RecordDirectory=path,OldVolts=old,NewVolts=next,ConsecutiveFailures=n,Changed=next!=old};
                d.Detail="本次会话连续联动失败 "+n+" / 3；EXT "+old.ToString("0.######",CultureInfo.InvariantCulture)+" → "+
                    next.ToString("0.######",CultureInfo.InvariantCulture)+" V。"+(d.Changed?"连续3次：原值×75%，下限0.5 V（设置保留6位小数），仅下一次用户启动生效。":
                    n>=3?"已到或低于0.5 V，不再降低。":"保持当前阈值。")+"未完成轮次未扣除；没有自动重加压。";
                // Required decision evidence is durable BEFORE changing the next threshold.
                using(var stream=new FileStream(Path.Combine(path,"trigger-threshold-decision.xml"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                {new XmlSerializer(typeof(FactoryTriggerThresholdDecision)).Serialize(stream,d);stream.Flush(true);}
                streaks[key]=new Streak {Count=n>=3?0:n,Volts=next};processed.Add(path);
                return d;
            }
        }
    }
}
