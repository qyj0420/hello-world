using System;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // User-defined nominal contribution: correlation 50%, measured attenuation
    // consistency 35%, other independently measured evidence 15%.
    // Missing requested CAL is unknown; its weight is never given to correlation.
    private static double A4WeightedEvidenceScore(double correlation,double widthScore,
        double spectrumScore,double snrScore,bool widthEnabled,A4AttenuationEvidence evidence)
    {
        if(!Finite(correlation)||!Finite(spectrumScore)||!Finite(snrScore)||
            (widthEnabled&&!Finite(widthScore)))return double.NaN;
        double other=(Clamp(spectrumScore,0,1)+Clamp(snrScore,0,1)+
            (widthEnabled?Clamp(widthScore,0,1):0))/(widthEnabled?3:2);
        bool enabled=evidence!=null&&evidence.Enabled;
        double wa=enabled?.35:0;
        double wc=enabled?.50:.50/.65,wo=enabled?.15:.15/.65;
        double baseScore=(.50*Clamp(correlation,0,1)+.15*other)/.65;
        // CAL must pass independent qualification first. The requested tolerance
        // is an engineering full-contribution interval, never a probability sigma.
        double attenuation=evidence!=null&&evidence.Applied?evidence.SoftConsistencyScore:double.NaN;
        double score=HVX.SignalEvidence.SixthConfidenceMath.WeightedScore(
            new[]{Clamp(correlation,0,1),attenuation,other},new[]{.50,.35,.15},new[]{true,enabled,true});
        score=Math.Min(score,HVX.SignalEvidence.SixthConfidenceMath.AttenuationCeiling(enabled,
            evidence!=null&&evidence.Applied,evidence!=null&&evidence.BasePassed,attenuation));
        if(evidence!=null)
        {
            evidence.CorrelationWeight=wc;evidence.OtherWeight=wo;
            evidence.ConsistencyScore=attenuation;evidence.OtherScore=other;evidence.WeightedScore=score;
            evidence.PenaltyFactor=baseScore>0?Clamp(score/baseScore,0,1):1;
        }
        return score;
    }
    // Hard candidate bound, including the configured default one-percent
    // endpoint margin. No extra quantization samples become selectable echoes.
    private static int A4PhysicalDelayLimit(A4Context c)
    {
        return A4ReflectionMaximumDelay(c);
    }
    private static bool A4CalibrationConflict(A4AttenuationEvidence e)
    {
        return e!=null&&e.Enabled&&(!e.Applied||!e.Passed);
    }
    private static int A4BoundaryReviewDelayLimit(A4Context c)
    {
        return A4ReflectionMaximumDelay(c);
    }
    private static bool A4DetectionReliable(double snrScore)
    {
        // Preserve the pre-existing detection reliability needed to reach 70%
        // under multiplicative scoring; additive feature weights must not turn
        // noise packets into confident matches merely through high correlation.
        return Finite(snrScore)&&snrScore>=.70;
    }
}
