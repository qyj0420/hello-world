using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private readonly Dictionary<A4Pair,TimingAnchorUncertainty> a7TimingUncertainties=new Dictionary<A4Pair,TimingAnchorUncertainty>();
    private readonly Dictionary<A4Pair,TimingAnchorUncertainty> a7MeasurementTimingUncertainties=new Dictionary<A4Pair,TimingAnchorUncertainty>();
    private readonly Dictionary<A4Pair,Tuple<int,int,int,int,bool>> a7TimingPeakIdentities=new Dictionary<A4Pair,Tuple<int,int,int,int,bool>>();
    private readonly Dictionary<int,int[]> a7UnknownCompetitorPeaks=new Dictionary<int,int[]>();
    private readonly HashSet<A4Pair> a7ManualReviewBlocks=new HashSet<A4Pair>();

    private void A7RequireManualReview(A4Pair pair,SeventhPipelineResult result,string reason)
    {
        result.AutoAccept=false;
        if(!result.Reasons.Contains(reason))result.Reasons=result.Reasons.Concat(new[]{reason}).ToArray();
        pair.State="review";
        // Preserve the measurement score during ranking. Apply the review
        // ceiling only after selection, so it cannot manufacture a new winner.
        a7ManualReviewBlocks.Add(pair);
        if(!pair.ReviewReason.Contains(reason))pair.ReviewReason+=";"+reason;
    }
    private void A7ApplyTimingUncertainty(A4Context c,A4Pair pair,SeventhPipelineResult result,int reflectionPeak,int a,int b,int measuredIncidentPeak=-1)
    {
        // Graph identity is the canonical raw pair. Complete-packet measurement
        // may select a different maximum; never mix that reflection maximum with
        // the graph incident maximum and call it one consistent peak delay.
        bool incidentMeasurementProvided=measuredIncidentPeak>=0;
        int measurementIncident=incidentMeasurementProvided?measuredIncidentPeak:pair.OutputIncident;
        var uncertainty=RequestedTimingUncertaintyMath.Evaluate(a7FirstArrival,pair.OutputIncident,pair.OutputReflection,a,b,c.RoundTripExact);
        var measuredUncertainty=RequestedTimingUncertaintyMath.Evaluate(a7FirstArrival,measurementIncident,reflectionPeak,a,b,c.RoundTripExact);
        bool identityDisagreement=measurementIncident!=pair.OutputIncident||reflectionPeak!=pair.OutputReflection;
        a7TimingPeakIdentities[pair]=Tuple.Create(pair.OutputIncident,pair.OutputReflection,measurementIncident,reflectionPeak,incidentMeasurementProvided);
        a7MeasurementTimingUncertainties[pair]=measuredUncertainty;
        if(a7FirstArrival&&identityDisagreement)
        {
            const string identityReason="first_arrival_measurement_peak_identity_disagreement_requires_review";
            uncertainty.Reason=uncertainty.RequiresReview?uncertainty.Reason+";"+identityReason:identityReason;
            uncertainty.RequiresReview=true;
        }
        a7TimingUncertainties[pair]=uncertainty;
        if(uncertainty.RequiresReview)A7RequireManualReview(pair,result,uncertainty.Reason);
    }
    private bool A7UnresolvedReflection(A4Context c,A4Pulse incident,A4Pulse reflection,A7PipelinePulse measured)
    {
        int a=A4ChargeIndex(incident),b=A4ChargeIndex(reflection);
        if(a<0||b<0||a>=c.Raw.Length||b>=c.Raw.Length)return false;
        double av=A4RawSigned(c,a),bv=A4RawSigned(c,b);
        if(!Finite(av)||!Finite(bv))return false;
        return UnknownReflectionEvidenceMath.IsUnresolvedCompetitor(measured.Valid,reflection.Start>incident.End,
            b>a&&A4ReflectionDelayWithinWindow(c,(double)b-a)&&A4BoundaryTimingValid(c,(double)b-a),
            Math.Sign(av)==Math.Sign(bv),Math.Abs(bv),c.MeasurementSignal.RawSigma);
    }
    private string A7TimingUncertaintyJson(TimingAnchorUncertainty u)
    {
        if(u==null)return "null";
        return "{\"applicable\":"+(u.Applicable?"true":"false")+
            ",\"valid\":"+(u.Valid?"true":"false")+",\"requires_review\":"+(u.RequiresReview?"true":"false")+
            ",\"peak_delay_samples\":"+NumberJson(u.PeakDelaySamples)+",\"requested_delay_samples\":"+NumberJson(u.RequestedDelaySamples)+
            ",\"anchor_disagreement_samples\":"+NumberJson(u.DisagreementSamples)+",\"tolerance_samples\":"+NumberJson(u.ToleranceSamples)+
            ",\"window_membership_evaluated\":"+(u.WindowMembershipEvaluated?"true":"false")+
            ",\"exact_round_trip_samples\":"+NumberJson(u.RoundTripSamples)+
            ",\"peak_within_physical_window\":"+(u.PeakWithinPhysicalWindow?"true":"false")+
            ",\"requested_within_physical_window\":"+(u.RequestedWithinPhysicalWindow?"true":"false")+
            ",\"window_membership_disagreement\":"+(u.WindowMembershipDisagreement?"true":"false")+
            ",\"reason\":"+Json(u.Reason)+"}";
    }
    private string A7UncertaintyJson(A4Pair pair)
    {
        TimingAnchorUncertainty u,measured;int[] peaks;Tuple<int,int,int,int,bool> identities;
        if(!a7UnknownCompetitorPeaks.TryGetValue(pair.OutputIncident,out peaks))peaks=new int[0];
        a7TimingUncertainties.TryGetValue(pair,out u);
        a7MeasurementTimingUncertainties.TryGetValue(pair,out measured);
        string identity="null";
        if(a7TimingPeakIdentities.TryGetValue(pair,out identities))
        {
            bool differs=identities.Item1!=identities.Item3||identities.Item2!=identities.Item4;
            identity="{\"canonical_incident_peak\":"+identities.Item1+",\"canonical_reflection_peak\":"+identities.Item2+
                ",\"measurement_incident_peak\":"+identities.Item3+",\"measurement_reflection_peak\":"+identities.Item4+
                ",\"measurement_incident_explicitly_provided\":"+(identities.Item5?"true":"false")+
                ",\"identity_disagreement\":"+(differs?"true":"false")+",\"requires_review\":"+(a7FirstArrival&&differs?"true":"false")+"}";
        }
        return "{\"unresolved_competitor_peaks\":["+String.Join(",",peaks)+"],\"timing_anchor_uncertainty\":"+A7TimingUncertaintyJson(u)+
            ",\"measurement_timing_anchor_uncertainty\":"+A7TimingUncertaintyJson(measured)+",\"timing_peak_identity\":"+identity+"}";
    }
}
