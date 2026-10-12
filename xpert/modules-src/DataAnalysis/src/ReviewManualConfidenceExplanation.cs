using System;
namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        string ManualConfidenceExplanation()
        {
            string details=pdmap.ConfidenceDetails??"";
            if(details.Contains("reflectionMeasurement=selected_sample_below_raw_noise_gate"))return L("Selected point is below the raw pulse threshold; choose an independent reflection peak.","所选点低于原始脉冲门限；请选取独立反射波峰。");
            if(details.Contains("quiet_guards_unavailable"))return L("Independent quiet background is missing; confidence cannot be measured.","缺少独立干净背景区，无法测量确信度。");
            if(details.Contains("guard_pulse_contamination")||details.Contains("guard_baseline_unstable"))return L("Background is contaminated or unstable; confidence cannot be measured.","背景区残留脉冲或基线不稳，无法测量确信度。");
            if(details.Contains("fixed_band_shape_below_five_sigma"))return L("Fixed-band pulse is below the noise gate; confidence cannot be measured.","固定带宽脉冲低于噪声门限，无法测量确信度。");
            if(details.Contains("fixed_band_opposite_lobe_dominates"))return L("Opposite pulse lobe dominates; independent reflection shape cannot be scored.","反向波瓣幅值占优，无法按独立反射波形评分。");
            if(details.Contains("fixed_band_shape_unavailable"))return L("Fixed-band shape evidence is insufficient; confidence cannot be measured.","固定带宽波形证据不足，无法测量确信度。");
            return L("Incomplete pulse/calibration evidence; open diagnostics for details.","脉冲或校准证据不完整；打开诊断可查看原因。");
        }
    }
}
