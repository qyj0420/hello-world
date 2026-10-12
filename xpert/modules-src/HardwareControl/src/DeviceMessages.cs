using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HVX.OWTS.Task38
{
    // Presentation only: original exceptions/status codes remain unchanged in the control layer.
    public static class DeviceMessages
    {
        public static string Describe(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return String.Empty;
            var errors = new List<string>();
            foreach (Match m in Regex.Matches(raw, @"PICO_STATUS\s+(0x[0-9A-Fa-f]+)|ART-DAQ\s+(-?\d+)", RegexOptions.IgnoreCase))
            {
                string text;
                if (m.Groups[1].Success)
                {
                    uint code = Convert.ToUInt32(m.Groups[1].Value.Substring(2), 16);
                    string explanation;
                    // Values verified against the installed SDK's PicoStatus.h.
                    switch (code)
                    {
                        case 2: explanation = "采集缓存内存不足，请缩短单段时长或释放电脑内存。"; break;
                        case 3: explanation = "未找到设备，请检查模块供电、通信线和设备标识。"; break;
                        case 5: case 0x27: case 0x43: explanation = "设备正忙，请等待当前操作结束，并关闭其他占用设备的程序。"; break;
                        case 7: case 0xC: case 0x144: explanation = "连接失效或设备无响应，请先确认放电，再检查通信连接。"; break;
                        case 0xD: explanation = "采集参数无效，请核对采样、触发和单段长度设置。"; break;
                        case 0xE: explanation = "不支持当前采样时基，请检查采样率与分辨率配置。"; break;
                        case 0xF: explanation = "不支持当前输入量程，请重新选择局放量程。"; break;
                        case 0x11C: explanation = "供电电压不足，请检查模块电源。"; break;
                        default: explanation = "操作失败，具体原因需结合诊断日志排查；请先确认放电，再检查模块连接与设置。"; break;
                    }
                    text = "局放采集模块：" + explanation + "（诊断码 " + m.Groups[1].Value + "）";
                }
                else text = HighVoltageFunction(raw) + "操作失败。请先确认放电，再检查通信、联锁接线、通道配置及设备占用情况。（诊断码 " + m.Groups[2].Value + "）";
                if (!errors.Contains(text)) errors.Add(text);
            }
            if (errors.Count > 0) return String.Join("\n", errors.ToArray());
            string[,] parameters = {
                { "TargetVoltageV", "目标电压" }, { "VoltageGainVPerAoVolt", "加压比" }, { "DividerRatioVPerV", "直流分压器比" },
                { "SourceVoltageFeedbackFullScaleV", "直流源电压反馈满量程" }, { "SourceCurrentFeedbackFullScaleA", "直流源电流反馈满量程" },
                { "CurrentLimitA", "限流设定" }, { "HardOverCurrentA", "过流保护限值" }, { "RampStepAoVolts", "升压步长" },
                { "RampStepDelayMilliseconds", "步进延时" }, { "MaximumRampSeconds", "升压超时" }, { "ResidualVoltageSafeThresholdV", "安全残压阈值" },
                { "CaptureDurationSeconds", "单段时长" }, { "PicoExternalTriggerThresholdV", "局放同步触发阈值" }
            };
            for (int i = 0; i < parameters.GetLength(0); i++)
                if (raw.StartsWith(parameters[i, 0], StringComparison.Ordinal))
                    return "设备设置中的“" + parameters[i, 1] + "”无效或超出允许范围，请核对现场标定值与安全限值。";
            string lower = raw.ToLowerInvariant();
            string module = lower.Contains("pico") || lower.Contains("ps5000a") ? "局放采集模块" :
                lower.Contains("art") || lower.Contains("usb3135") || lower.Contains("daq") ? HighVoltageFunction(raw).TrimEnd('：') : "设备";
            if (lower.Contains(".dll") || lower.Contains("failed to load") || lower.Contains("badimageformat"))
                return module + "驱动或运行组件未正确加载，请检查程序文件、驱动安装路径和 64 位运行环境。";
            if (lower.Contains("timed out") || lower.Contains("timeout") && !Regex.IsMatch(raw, @"[\u4e00-\u9fff]"))
                return module + "等待触发或数据返回超时。请先确认放电，再核对同步触发线、电平及采集超时设置。";
            if (lower.Contains("serial") || lower.Contains("expected a non-mso"))
                return module + "设备身份或型号不匹配，请核对设备标识；禁止接管其他设备。";
            if (lower.Contains("memory is too small") || lower.Contains("timebase"))
                return module + "不支持当前采样率、分辨率或单段长度，请核对采集配置。";
            if (lower.Contains("not initialized") || lower.Contains("not armed") || lower.Contains("already initialized"))
                return module + "尚未就绪或当前状态不允许该操作，请等待连接完成，必要时退出测试后重新连接。";
            string translated = raw.Replace("PicoScope", "局放采集模块").Replace("PICO", "局放采集模块").Replace("Pico", "局放采集模块")
                .Replace("ART-DAQ", "高压控制模块").Replace("USB3135", "高压控制模块").Replace("ART", "高压控制模块")
                .Replace("ResetFaultLatch", "复位故障").Replace("AI0/AI2/AI4", "电压及电流反馈");
            translated = Regex.Replace(translated, @"索引\s+(\d+)", m =>
            {
                string[] ranges = { "±10 mV", "±20 mV", "±50 mV", "±100 mV", "±200 mV", "±500 mV", "±1 V", "±2 V", "±5 V", "±10 V", "±20 V" };
                int index; return Int32.TryParse(m.Groups[1].Value, out index) && index >= 0 && index < ranges.Length ? ranges[index] : "待确认量程";
            });
            if (!Regex.IsMatch(translated, @"[\u4e00-\u9fff]") || Regex.IsMatch(translated, @"\b(failed|cannot|must|invalid|returned|could not|was cancelled)\b", RegexOptions.IgnoreCase))
                return module + "参数或操作异常，请核对校准值、输入范围与设备状态；原始原因已记录在诊断日志中。";
            return translated;
        }
        private static string HighVoltageFunction(string raw)
        {
            string lower = raw.ToLowerInvariant();
            if (lower.Contains("safety") || lower.Contains("p0.4")) return "安全联锁输入：";
            if (lower.Contains("finite") || lower.Contains("ai4")) return "振荡电压采集模块：";
            if (lower.Contains("ao0") || lower.Contains("source off")) return "直流源控制输出：";
            return "高压控制模块：";
        }
        public static string State(Task38ControllerState state)
        {
            string[] names = { "未初始化", "安全待机", "启动前检查", "释放接地", "高压使能延时", "步进升压", "关闭高压源", "布置同步采集", "接地前延时", "等待同步数据", "残压检查", "本轮结束", "故障锁定", "连接已释放" };
            int i = (int)state; return i >= 0 && i < names.Length ? names[i] : "未知阶段";
        }
        public static string ExitReason(Task38ExitReason reason)
        {
            string[] names = { "未完成", "达到目标电压", "急停或安全联锁断开", "操作者停止", "限流无增长", "过流保护", "过电压保护", "升压超时", "设备异常", "同步采集异常" };
            int i = (int)reason; return i >= 0 && i < names.Length ? names[i] : "未知原因";
        }
    }
}
