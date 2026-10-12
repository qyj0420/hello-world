using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Xiongxin.ZeroBench
{
    internal enum StageState { NotStarted, Running, Passed, Failed, Skipped, Canceled, Unverified }

    internal sealed class StageDiagnosticRecord
    {
        internal readonly string Key, Caption;
        internal StageState State { get; set; }
        internal DateTime? FirstStarted { get; set; }
        internal DateTime? LastUpdated { get; set; }
        internal readonly List<string> History = new List<string>();
        internal StageDiagnosticRecord(string key, string caption) { Key = key; Caption = caption; }
    }

    // UI-independent evidence model. Entering another stage is never evidence that a prior
    // stage passed, and successful cleanup is not evidence that the acquisition passed.
    internal sealed class StageDiagnostics
    {
        internal static readonly string[] Keys = { "INIT", "AO_ZERO", "HV_ON", "HV_DELAY", "HV_UP", "HV_OFF", "ARM", "ARM_DELAY", "WAIT_TRIGGER", "TRG_OUTPUT", "READ", "RELEASE", "COMPLETE" };
        internal static readonly string[] Captions = { "初始化两卡", "AO = 0 V", "HV ON", "HV ON 延时", "HV UP / 0 V 结束", "HV OFF", "两卡采集布置", "布置后延时", "等待同一触发", "P0.7 TRG 输出", "读取本轮数据", "释放设备", "本轮完成" };
        private readonly Dictionary<string, StageDiagnosticRecord> records = new Dictionary<string, StageDiagnosticRecord>(StringComparer.Ordinal);
        private string observationStage;

        internal StageDiagnostics() { Reset(); }
        internal StageDiagnosticRecord this[string key] { get { return records[key]; } }
        internal void Reset()
        {
            records.Clear(); observationStage = null;
            for (int i = 0; i < Keys.Length; i++) records.Add(Keys[i], new StageDiagnosticRecord(Keys[i], Captions[i]));
        }

        internal bool TryApply(string eventKey, string message) { return TryApply(eventKey, message, DateTime.Now); }
        internal bool TryApply(string eventKey, string message, DateTime timestamp)
        {
            if (String.IsNullOrEmpty(eventKey) || !eventKey.StartsWith("STEP_", StringComparison.Ordinal)) return false;
            int separator = eventKey.LastIndexOf('_');
            if (separator <= 5) return false;
            string key = eventKey.Substring(5, separator - 5), result = eventKey.Substring(separator + 1);
            StageState state;
            switch (result)
            {
                case "RUNNING": state = StageState.Running; break;
                case "PASSED": state = StageState.Passed; break;
                case "FAILED": state = StageState.Failed; break;
                case "SKIPPED": state = StageState.Skipped; break;
                case "CANCELED": state = StageState.Canceled; break;
                case "UNVERIFIED": state = StageState.Unverified; break;
                default: return false;
            }
            if (!records.ContainsKey(key)) return false;
            observationStage = key;
            if (key == "COMPLETE" && state == StageState.Passed)
            {
                var missing = new List<string>(); var failures = new List<string>(); bool canceled = false;
                foreach (string otherKey in Keys)
                {
                    if (otherKey == "COMPLETE") continue;
                    StageState other = records[otherKey].State;
                    if (other == StageState.Failed) failures.Add(records[otherKey].Caption);
                    if (other == StageState.Canceled) canceled = true;
                    if (other == StageState.NotStarted || other == StageState.Running) missing.Add(records[otherKey].Caption);
                }
                if (failures.Count > 0) { state = StageState.Failed; message += "\r\n不能标为通过：仍有失败步骤：" + String.Join("、", failures); }
                else if (canceled) { state = StageState.Canceled; message += "\r\n不能标为通过：本轮存在已停止步骤。"; }
                else if (missing.Count > 0) { state = StageState.Unverified; message += "\r\n不能标为通过：缺少明确结果：" + String.Join("、", missing); }
            }
            Set(records[key], state, eventKey, message, timestamp);
            return true;
        }

        private static void Set(StageDiagnosticRecord record, StageState state, string source, string message, DateTime timestamp)
        {
            bool sticky = record.State == StageState.Failed || record.State == StageState.Canceled;
            // A cleanup exception may upgrade cancellation to failure, never the reverse.
            bool canReplace = !sticky || state == StageState.Failed;
            if (state == StageState.Running && !record.FirstStarted.HasValue) record.FirstStarted = timestamp;
            record.LastUpdated = timestamp;
            record.History.Add(timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + source + "\r\n" + (message ?? "")
                + (!canReplace && record.State != state ? "\r\n[保留原失败/停止状态，不被后续事件覆盖]" : ""));
            if (canReplace) record.State = state;
        }

        internal void Observe(string key, string message)
        {
            if (records.ContainsKey(key)) observationStage = key;
            else if (key == "ACQ_STATE") observationStage = "WAIT_TRIGGER";
            if (observationStage == null) return;
            StageDiagnosticRecord record = records[observationStage];
            record.History.Add(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + key + "\r\n" + (message ?? ""));
            // Legacy phase messages may indicate work started, but can never grant a pass.
            if (records.ContainsKey(key) && record.State == StageState.NotStarted)
            {
                record.State = StageState.Running; record.FirstStarted = record.LastUpdated = DateTime.Now;
            }
        }

        internal void Abort(string message, bool canceled)
        {
            StageState finalState = canceled ? StageState.Canceled : StageState.Failed;
            foreach (string key in Keys)
                if (records[key].State == StageState.Running)
                    Set(records[key], finalState, canceled ? "RUN_CANCELED" : "RUN_FAILED", message, DateTime.Now);
            Set(records["COMPLETE"], finalState, canceled ? "RUN_CANCELED" : "RUN_FAILED", message, DateTime.Now);
        }

        internal static string StateText(StageState state)
        {
            switch (state)
            {
                case StageState.Running: return "执行中";
                case StageState.Passed: return "通过";
                case StageState.Failed: return "错误";
                case StageState.Skipped: return "跳过";
                case StageState.Canceled: return "已停止";
                case StageState.Unverified: return "待验证";
                default: return "未执行";
            }
        }

        internal string Details(string key)
        {
            StageDiagnosticRecord record = records[key];
            var text = new StringBuilder();
            text.AppendLine("步骤：" + record.Caption + "  [" + record.Key + "]");
            text.AppendLine("状态：" + StateText(record.State));
            text.AppendLine("开始时间：" + FormatTime(record.FirstStarted));
            text.AppendLine("最近状态时间：" + FormatTime(record.LastUpdated));
            text.AppendLine("绿色=明确通过；浅红=错误；黄色=跳过；蓝色=执行中；灰色=未执行；淡紫=待验证。");
            text.AppendLine("驱动调用成功不等于端口物理电平已验证；未独立回读的输出保持待验证。仿真状态不代表硬件。");
            text.AppendLine(); text.AppendLine("本步骤完整记录（保留原始错误码/异常）：");
            if (record.History.Count == 0) text.AppendLine("尚无本轮事件；没有错误记录不代表已经执行或通过。");
            else foreach (string entry in record.History) { text.AppendLine(entry); text.AppendLine(); }
            return text.ToString();
        }
        private static string FormatTime(DateTime? value) { return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : "--"; }
    }
}
