using System;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// ART + Pico 双设备身份校验（对照 Factory SameDeviceSession）。
    /// 期望串为空则跳过该侧；两侧均配置时须同时匹配才算通过。
    /// </summary>
    public static class HardwareDualIdentity
    {
        public const string AssetMatchOk = "DualMatch";
        public const string AssetMatchSkip = "NotRequired";
        public const string AssetMatchFail = "DualMismatch";
        public const string AssetMatchPartial = "PartialSkip";

        /// <summary>
        /// 校验期望身份与探测到的 ART（Controller）/ Pico（Acquisition）序列号。
        /// </summary>
        public static bool TryValidate(
            string expectedArtIdentity,
            string expectedPicoSerial,
            string actualArtIdentity,
            string actualPicoSerial,
            out string message,
            out string assetMatchStatus)
        {
            string expArt = Normalize(expectedArtIdentity);
            string expPico = Normalize(expectedPicoSerial);
            string actArt = Normalize(actualArtIdentity);
            string actPico = Normalize(actualPicoSerial);

            bool checkArt = !string.IsNullOrEmpty(expArt);
            bool checkPico = !string.IsNullOrEmpty(expPico);

            if (!checkArt && !checkPico)
            {
                message = StatusCopy.DualIdentityNotConfigured;
                assetMatchStatus = AssetMatchSkip;
                return true; // 未配置期望串：不阻断连接，仅提示
            }

            bool artOk = !checkArt || string.Equals(expArt, actArt, StringComparison.OrdinalIgnoreCase);
            bool picoOk = !checkPico || PicoSerialEquals(expPico, actPico);

            if (artOk && picoOk)
            {
                message = StatusCopy.DualIdentityOk
                    + " ART=" + (checkArt ? actArt : "(skip)")
                    + " Pico=" + (checkPico ? DisplayPico(actPico) : "(skip)");
                assetMatchStatus = (checkArt && checkPico) ? AssetMatchOk : AssetMatchPartial;
                return true;
            }

            var parts = new System.Text.StringBuilder();
            parts.Append(StatusCopy.DualIdentityMismatch);
            if (checkArt && !artOk)
                parts.Append(" ART期望=").Append(expArt).Append(" 实际=").Append(string.IsNullOrEmpty(actArt) ? "(空)" : actArt).Append(';');
            if (checkPico && !picoOk)
                parts.Append(" Pico期望=").Append(expPico).Append(" 实际=").Append(DisplayPico(actPico)).Append(';');
            message = parts.ToString();
            assetMatchStatus = AssetMatchFail;
            return false;
        }

        /// <summary>对 Probe 结果就地写入 IdentityValid / AssetMatchStatus / Message（失败时 IdentityValid=false）。</summary>
        public static HardwareIdentityProbeResult ApplyToProbe(
            HardwareIdentityProbeResult probe,
            string expectedArtIdentity,
            string expectedPicoSerial)
        {
            if (probe == null) probe = new HardwareIdentityProbeResult();
            string msg, status;
            bool ok = TryValidate(
                expectedArtIdentity,
                expectedPicoSerial,
                probe.ControllerSerial,
                probe.AcquisitionSerial,
                out msg,
                out status);
            probe.AssetMatchStatus = status;
            if (!ok)
            {
                probe.IdentityValid = false;
                probe.Message = msg;
                probe.StatusCode = "IdentityMismatch";
            }
            else if (status == AssetMatchOk || status == AssetMatchPartial)
            {
                if (string.IsNullOrEmpty(probe.Message) || probe.Message.IndexOf("身份", StringComparison.Ordinal) < 0)
                    probe.Message = string.IsNullOrEmpty(probe.Message) ? msg : (probe.Message + "；" + msg);
            }
            else if (status == AssetMatchSkip && string.IsNullOrEmpty(probe.Diagnostic))
            {
                probe.Diagnostic = msg;
            }
            return probe;
        }

        static string Normalize(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
        }

        static bool PicoSerialEquals(string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return true;
            // OpenUnit 空串 = 第一台；配置写 "(first unit)" 时与空实际串视为同义
            if (IsFirstUnitToken(expected) && (string.IsNullOrEmpty(actual) || IsFirstUnitToken(actual)))
                return true;
            return false;
        }

        static bool IsFirstUnitToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            return string.Equals(s, "(first unit)", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "first", StringComparison.OrdinalIgnoreCase);
        }

        static string DisplayPico(string s)
        {
            return string.IsNullOrEmpty(s) ? "(first unit)" : s;
        }
    }
}
