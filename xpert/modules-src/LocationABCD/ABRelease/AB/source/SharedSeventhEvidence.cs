using System;
using System.Collections.Generic;

// .NET Framework / C# 7.3. Compiled unchanged into Task17 and PDmap.
// Amplitudes are guarded original CAL/LOC measurements; shape and width remain
// in the shared fixed band. No input is measured from display-filter samples.
namespace HVX.SignalEvidence
{
    internal sealed class A7EvidenceInput
    {
        internal bool GuardsValid, TimingValid, CalibrationReliable, RoleEvidenceAvailable;
        internal bool DefiniteSecondaryRole, RoleAmbiguous, WidthAvailable;
        internal bool UseAttenuation=true, UseWidth=true;
        internal double Correlation=double.NaN, CorrelationReference=1, CorrelationFloor=.7;
        internal double IncidentAmplitude=double.NaN, ReflectionAmplitude=double.NaN;
        internal double CalibrationRetention=double.NaN, DelaySeconds=double.NaN, CalibrationTravelSeconds=double.NaN;
        internal double IncidentWidthNs=double.NaN, ReflectionWidthNs=double.NaN;
        internal double CalibrationWidthRatio=double.NaN;
        internal double PrimaryRoleSimilarity=double.NaN, SecondaryRoleSimilarity=double.NaN;
        internal double AttenuationTolerancePercent=15, WidthTolerancePercent=35;
        internal double[] Weights=new[]{.40,.35,.15,.10};
    }
    internal sealed class A7EvidenceResult
    {
        internal bool Available, Eligible;
        internal double Score=double.NaN, WidthReference=double.NaN, RoleScore=double.NaN;
        internal double CorrelationScore=double.NaN;
        internal AttenuationEvidence Attenuation;
        internal ToleranceEvidence Width;
        internal string[] Reasons=new string[0];
    }
    internal static class A7EvidenceKernel
    {
        internal const string Version="2026.09.22-seventh-shared-raw-tolerance2";
        internal const double MinimumAcceptedScore=.70;
        internal static A7EvidenceResult Evaluate(A7EvidenceInput x)
        {
            if(x==null)throw new ArgumentNullException("x");
            var r=new A7EvidenceResult();var reasons=new List<string>();
            if(!x.GuardsValid)reasons.Add("same_band_guarded_measurement_unavailable");
            bool timingValid=x.TimingValid&&ConfidenceMath.Finite(x.DelaySeconds)&&x.DelaySeconds>0&&
                ConfidenceMath.Finite(x.CalibrationTravelSeconds)&&x.CalibrationTravelSeconds>0;
            if(!timingValid)reasons.Add("physical_timing_invalid");
            if(!x.CalibrationReliable)reasons.Add("same_band_calibration_unreliable");
            if(!x.RoleEvidenceAvailable)reasons.Add("calibration_echo_role_unavailable");
            if(!ConfidenceMath.Finite(x.Correlation)||x.Correlation< -1||x.Correlation>1||
                !ConfidenceMath.Finite(x.CorrelationReference)||x.CorrelationReference<=0||x.CorrelationReference>1||
                !ConfidenceMath.Finite(x.CorrelationFloor)||x.CorrelationFloor<0||x.CorrelationFloor>1)
                reasons.Add("correlation_measurement_unavailable");
            else
            {
                r.CorrelationScore=ConfidenceMath.Unit(x.Correlation/x.CorrelationReference);
                if(x.Correlation<=0||x.Correlation+1e-12<x.CorrelationFloor)reasons.Add("correlation_below_reference_floor");
            }
            r.Attenuation=SixthConfidenceMath.Attenuation(x.IncidentAmplitude,x.ReflectionAmplitude,x.CalibrationRetention,
                x.DelaySeconds,x.CalibrationTravelSeconds,x.AttenuationTolerancePercent,x.CalibrationReliable);
            if(x.UseAttenuation)
            {
                if(!r.Attenuation.Available)reasons.Add("attenuation_measurement_unavailable");
                else if(!r.Attenuation.Passed)reasons.Add("attenuation_conflict");
            }
            double fraction=ConfidenceMath.Finite(x.DelaySeconds)&&ConfidenceMath.Finite(x.CalibrationTravelSeconds)&&x.CalibrationTravelSeconds>0?
                ConfidenceMath.Unit(x.DelaySeconds/x.CalibrationTravelSeconds):double.NaN;
            if(x.WidthAvailable&&ConfidenceMath.Finite(x.CalibrationWidthRatio)&&x.CalibrationWidthRatio>0&&ConfidenceMath.Finite(fraction))
                r.WidthReference=1+(x.CalibrationWidthRatio-1)*fraction;
            r.Width=ConfidenceMath.RelativeError(x.WidthAvailable&&x.IncidentWidthNs>0?x.ReflectionWidthNs/x.IncidentWidthNs:double.NaN,
                r.WidthReference,x.WidthTolerancePercent);
            if(x.UseWidth)
            {
                if(!r.Width.Available)reasons.Add("calibrated_width_measurement_unavailable");
                else if(!r.Width.Passed)reasons.Add("calibrated_width_trend_conflict");
            }
            if(x.RoleEvidenceAvailable&&ConfidenceMath.Finite(x.PrimaryRoleSimilarity)&&x.PrimaryRoleSimilarity>=-1&&x.PrimaryRoleSimilarity<=1)
            {
                r.RoleScore=ConfidenceMath.Unit(x.PrimaryRoleSimilarity);
                if(x.PrimaryRoleSimilarity<.55)reasons.Add("primary_calibration_echo_shape_conflict");
            }
            else reasons.Add("primary_echo_role_similarity_unavailable");
            bool secondaryValid=double.IsNaN(x.SecondaryRoleSimilarity)||
                ConfidenceMath.Finite(x.SecondaryRoleSimilarity)&&x.SecondaryRoleSimilarity>=-1&&x.SecondaryRoleSimilarity<=1;
            if(!secondaryValid)reasons.Add("secondary_echo_similarity_invalid");
            if(x.DefiniteSecondaryRole)reasons.Add("candidate_has_supported_secondary_echo_role");
            if(x.RoleAmbiguous)reasons.Add("primary_secondary_echo_roles_ambiguous");
            // Missing essential evidence is unknown, not a deceptively precise zero or high score.
            r.Available=x.GuardsValid&&x.CalibrationReliable&&x.RoleEvidenceAvailable&&secondaryValid&&x.UseAttenuation&&x.UseWidth&&
                ConfidenceMath.Finite(r.CorrelationScore)&&ConfidenceMath.Finite(r.RoleScore)&&
                r.Attenuation.Available&&r.Width.Available;
            if(r.Available)
            {
                r.Score=ConfidenceMath.WeightedScore(new[]{r.CorrelationScore,r.Attenuation.Score,r.Width.Score,r.RoleScore},
                    x.Weights,new[]{true,x.UseAttenuation,x.UseWidth,true});
                r.Score=Math.Min(r.Score,SixthConfidenceMath.AttenuationCeiling(x.UseAttenuation,r.Attenuation.Available,r.Attenuation.BasePassed,r.Attenuation.Score));
                if(x.UseWidth&&!r.Width.Passed)r.Score=Math.Min(r.Score,.49*ConfidenceMath.Unit(r.Width.Score));
                if(x.Correlation<=0||x.Correlation+1e-12<x.CorrelationFloor)r.Score=Math.Min(r.Score,.49*r.CorrelationScore);
                if(!timingValid||x.DefiniteSecondaryRole)r.Score=0;
                if(x.RoleAmbiguous||x.PrimaryRoleSimilarity<.55)r.Score=Math.Min(r.Score,.49*r.RoleScore);
                if(r.Score<MinimumAcceptedScore)reasons.Add("engineering_evidence_below_70_percent");
            }
            // Method 7 is the JOINT calibrated method; explicitly disabled factors remain
            // review-only, never quietly reducing the acceptance contract to correlation.
            if(!x.UseAttenuation||!x.UseWidth)reasons.Add("joint_evidence_factor_disabled_review_only");
            r.Eligible=r.Available&&reasons.Count==0;
            r.Reasons=reasons.ToArray();return r;
        }
    }
}
