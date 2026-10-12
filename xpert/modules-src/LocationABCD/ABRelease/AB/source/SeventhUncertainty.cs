using System;

namespace HVX.SignalEvidence
{
    // Engineering disagreement between two anchors, NOT a statistical confidence
    // interval and NOT evidence that either estimator is the physical truth.
    // Method 6 never calls this new audited policy.
    internal sealed class TimingAnchorUncertainty
    {
        internal bool Applicable,Valid,RequiresReview;
        internal bool WindowMembershipEvaluated,PeakWithinPhysicalWindow,RequestedWithinPhysicalWindow,WindowMembershipDisagreement;
        internal double PeakDelaySamples,RequestedDelaySamples,DisagreementSamples,ToleranceSamples,RoundTripSamples;
        internal string Reason;
    }
    internal static class RequestedTimingUncertaintyMath
    {
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        internal static TimingAnchorUncertainty Evaluate(bool firstArrival,int incidentPeak,int reflectionPeak,
            int incidentTiming,int reflectionTiming,double roundTripSamples,double tolerancePercent=1)
        {
            var r=new TimingAnchorUncertainty {Applicable=firstArrival,Valid=true,Reason="peak_timing_no_anchor_switch"};
            if(!firstArrival)return r;
            r.Valid=incidentPeak>=0&&reflectionPeak>incidentPeak&&incidentTiming>=0&&reflectionTiming>incidentTiming&&
                incidentTiming<=incidentPeak&&reflectionTiming<=reflectionPeak&&Finite(roundTripSamples)&&roundTripSamples>0&&
                Finite(tolerancePercent)&&tolerancePercent>=0&&tolerancePercent<=100;
            if(!r.Valid){r.RequiresReview=true;r.Reason="first_arrival_anchor_uncertainty_unavailable";return r;}
            // Subtract as doubles to avoid overflowing integer differences.
            r.PeakDelaySamples=(double)reflectionPeak-incidentPeak;
            r.RequestedDelaySamples=(double)reflectionTiming-incidentTiming;
            r.DisagreementSamples=Math.Abs(r.RequestedDelaySamples-r.PeakDelaySamples);
            r.ToleranceSamples=Math.Max(2,roundTripSamples*(tolerancePercent/100));
            // A small anchor displacement is not evidence of agreement when it
            // crosses the exact physical window. Keep both hypotheses and
            // require review; neither anchor is assumed to be the truth. The
            // resolution tolerance must never enlarge the physical window.
            r.RoundTripSamples=roundTripSamples;
            r.WindowMembershipEvaluated=true;
            r.PeakWithinPhysicalWindow=r.PeakDelaySamples>0&&r.PeakDelaySamples<=roundTripSamples;
            r.RequestedWithinPhysicalWindow=r.RequestedDelaySamples>0&&r.RequestedDelaySamples<=roundTripSamples;
            r.WindowMembershipDisagreement=r.PeakWithinPhysicalWindow!=r.RequestedWithinPhysicalWindow;
            r.RequiresReview=r.WindowMembershipDisagreement||r.DisagreementSamples>r.ToleranceSamples;
            r.Reason=r.WindowMembershipDisagreement?"first_arrival_anchor_window_membership_disagreement_requires_review":
                r.RequiresReview?"first_arrival_asymmetric_anchor_shift_requires_review":"first_arrival_anchor_difference_within_resolution";
            return r;
        }
    }
    internal static class UnknownReflectionEvidenceMath
    {
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        // An unavailable complete-packet measurement cannot prove that a strong,
        // separate, same-polarity in-window raw event is not an echo. This is a
        // veto on uniqueness, not a new accepted reflection or a location prior.
        internal static bool IsUnresolvedCompetitor(bool completeMeasurementValid,bool distinctSupport,
            bool insidePhysicalWindow,bool sameRawPolarity,double rawAmplitude,double rawNoise)
        {
            return !completeMeasurementValid&&distinctSupport&&insidePhysicalWindow&&sameRawPolarity&&
                Finite(rawAmplitude)&&rawAmplitude>0&&Finite(rawNoise)&&rawNoise>=0&&
                (rawNoise==0||rawAmplitude/rawNoise>=6);
        }
    }
}
