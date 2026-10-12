using System;
using System.Threading;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 出厂加压 UI 线性高通入口（默认 10 kHz）。
    /// 算法权威：D:\PD_AI_LabVIEW2026_Project\deliverables\HVX_PD_BandpassFilter_算法移交包_20261012\PdBandpassFilter.cs
    /// （零相位 Butterworth 带通；低截止=界面截止，高截止≈0.45·Fs）。禁止自造 FIR/FFT/移动平均滤波核。
    /// 勾选后：左窗检波 + 右窗定位原始局放（均滤波后）；仅量程调节参考用未滤波 Pico。
    /// </summary>
    public static class FactoryPdHighPass
    {
        public const string FilterId = "PD-HP-via-HVX-Bandpass-ZP-Butterworth4-v1";
        public const double DefaultCutoffHz = 10000;
        static readonly PdBandpassFilter Shared = new PdBandpassFilter();

        /// <summary>高截止取 min(0.45·Fs, Fs/2 - 1)，满足移交包 high &lt; Nyquist。</summary>
        public static double ResolveHighCutoffHz(double sampleRateHz, double lowCutoffHz)
        {
            if (!(sampleRateHz > 0) || double.IsNaN(sampleRateHz) || double.IsInfinity(sampleRateHz))
                throw new ArgumentOutOfRangeException("sampleRateHz");
            double nyquist = sampleRateHz * 0.5;
            double high = sampleRateHz * 0.45;
            if (high <= lowCutoffHz)
                high = Math.Min(nyquist * 0.99, lowCutoffHz * 2.0);
            if (high >= nyquist)
                high = nyquist * 0.99;
            if (!(high > lowCutoffHz))
                throw new InvalidOperationException("无法为高通推导有效高截止（Fs 过低或低截止过高）。");
            return high;
        }

        public static int WindowLength(double sampleRateHz, double cutoffHz)
        {
            // 兼容旧调用：用移交包延拓尺度作近似窗长提示（非 FIR 窗）
            double cut = cutoffHz > 0 ? cutoffHz : DefaultCutoffHz;
            if (double.IsNaN(sampleRateHz) || double.IsInfinity(sampleRateHz) || sampleRateHz < 20000 || sampleRateHz > 1e9)
                throw new ArgumentOutOfRangeException("sampleRateHz", "高通需要 20 kS/s 至 1 GS/s 的采样率（含预览有效Fs）。");
            if (cut < 100 || cut > sampleRateHz * 0.45)
                throw new ArgumentOutOfRangeException("cutoffHz", "低截止须在 100 Hz～0.45·Fs 内。");
            int pad = Math.Max(48, (int)Math.Ceiling(3.0 * sampleRateHz / cut));
            return Math.Min(pad * 2 + 1, Math.Max(3, (int)sampleRateHz));
        }

        public static double[] Apply(double[] raw, double sampleRateHz, double cutoffHz = DefaultCutoffHz)
        {
            return ApplyCancellable(raw, sampleRateHz, cutoffHz, CancellationToken.None);
        }

        public static double[] ApplyCancellable(double[] raw, double sampleRateHz, double cutoffHz, CancellationToken cancellation)
        {
            if (raw == null) throw new ArgumentNullException("raw");
            cancellation.ThrowIfCancellationRequested();
            double cut = cutoffHz > 0 ? cutoffHz : DefaultCutoffHz;
            if (double.IsNaN(sampleRateHz) || double.IsInfinity(sampleRateHz) || sampleRateHz < 20000 || sampleRateHz > 1e9)
                throw new ArgumentOutOfRangeException("sampleRateHz", "高通需要 20 kS/s 至 1 GS/s 的采样率（含预览有效Fs）。");
            if (cut < 100 || cut >= sampleRateHz * 0.5)
                throw new ArgumentOutOfRangeException("cutoffHz", "低截止须在 100 Hz～Nyquist 内。");
            double high = ResolveHighCutoffHz(sampleRateHz, cut);
            cancellation.ThrowIfCancellationRequested();
            // 移交包为离线整段滤波；长波形时仍一次完成（与原 FIR 路径一致）
            double[] filtered = Shared.Filter(raw, sampleRateHz, cut, high);
            cancellation.ThrowIfCancellationRequested();
            return filtered;
        }

        /// <summary>检波：高通后取绝对值（包络显示用）。</summary>
        public static double[] DemodulateAbs(double[] filtered)
        {
            if (filtered == null) return null;
            var o = new double[filtered.Length];
            for (int i = 0; i < filtered.Length; i++)
                o[i] = Math.Abs(filtered[i]);
            return o;
        }
    }
}
