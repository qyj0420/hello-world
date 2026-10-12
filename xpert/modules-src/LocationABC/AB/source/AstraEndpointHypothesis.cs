using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Read-only, post-match engineering hypotheses. This is NOT a location
    // estimator and NOT a calibrated probability. One-ended TDR with an RT
    // separation is not, by itself, an identifiable near/far-end experiment.
    // In particular two opposite-sign lobes are not two independent arrivals.
    // No cache, packet, accepted state, matrix, coordinate or CAL is changed.
    private const double A4EndpointShapeErrorPercent=60.0;
    private const double A4EndpointLobeSnr=4.5;
    private const int A4EndpointGuardPoints=16;

    private sealed class A4EndpointShape
    {
        internal bool Valid,MultiComponent;
        internal string Reason="fixed_band_measurement_unavailable";
        internal double WidthNs=double.NaN,ComponentWidthNs=double.NaN;
        internal double Energy=double.NaN,EquivalentWidthNs=double.NaN;
        internal double FirstEnergyFraction=double.NaN,SecondEnergyFraction=double.NaN;
        internal double MainEnergyFraction=double.NaN;
        internal double TemporalEnergyBalance=double.NaN,TemporalBalanceUncertainty=double.NaN;
        internal readonly List<int> LobePeaks=new List<int>();
        internal readonly List<int> LobeSigns=new List<int>();
        internal readonly List<double> LobeEnergies=new List<double>();
        internal readonly List<double> LobeEnergyUncertainties=new List<double>();
    }
    private sealed class A4EndpointPrecursor
    {
        internal bool Covered,QuietGuards,Significant,AbsenceSupported;
        internal string Reason="preceding_window_unavailable";
        internal long RequestedStart,RequestedEnd;
        internal int ExpectedPeak,GuardStart,GuardEnd;
        internal double Baseline=double.NaN,Noise=double.NaN,RawPeak=double.NaN,Threshold=double.NaN;
        internal readonly List<int> PacketPeaks=new List<int>();
    }
    private sealed class A4EndpointBudget
    {
        internal bool Complete=true;
        internal int Steps;
        internal bool Take(int count=1)
        {
            if(count<0||Steps>A4MaximumRoleReviewSteps-count){Complete=false;return false;}
            Steps+=count;return true;
        }
    }

    private static A4EndpointShape A4EndpointMeasureShape(A4AttenuationMeasurement m,double fs)
    {
        var s=new A4EndpointShape();
        if(!A4Index4FixedMeasurementReliable(m)||m.SignalSamples==null||!Finite(fs)||fs<=0)return s;
        int lo=m.Start-m.SignalOrigin,hi=m.End-m.SignalOrigin,peak=m.Peak-m.SignalOrigin;
        if(lo<0||hi>=m.SignalSamples.Length||hi<lo||peak<lo||peak>hi||!Finite(m.Baseline))return s;
        var w=A6MeasureWidth(null,m,fs);
        s.WidthNs=w.WidthNs;s.ComponentWidthNs=w.ComponentWidthNs;s.MultiComponent=w.MultiPeak;
        double energy=0,maximum=0;int runStart=lo,sign=0,runPeak=lo;
        double runEnergy=0,runAmplitude=0,runAbsoluteSum=0;
        for(int i=lo;i<=hi+1;i++)
        {
            double v=i<=hi?m.SignalSamples[i]-m.Baseline:0;
            if(!Finite(v)){s.Reason="nonfinite_fixed_band_support";return s;}
            int next=i>hi?0:(v>0?1:v<0?-1:0);
            if(next!=sign)
            {
                int n=i-runStart;
                double noiseEnergy=m.Noise*m.Noise*(n+A4EndpointLobeSnr*Math.Sqrt(2.0*n));
                if(sign!=0&&runAmplitude>A4EndpointLobeSnr*m.Noise&&runEnergy>noiseEnergy)
                {
                    s.LobePeaks.Add(runPeak+m.SignalOrigin);s.LobeSigns.Add(sign);s.LobeEnergies.Add(runEnergy/fs);
                    // Engineering perturbation bound, not a Gaussian confidence
                    // interval: perturb each retained sample by +/-2 noise.
                    s.LobeEnergyUncertainties.Add((4*m.Noise*runAbsoluteSum+4*n*m.Noise*m.Noise)/fs);
                }
                sign=next;runStart=i;runPeak=i;runEnergy=0;runAmplitude=0;runAbsoluteSum=0;
            }
            if(i<=hi)
            {
                energy+=v*v;maximum=Math.Max(maximum,Math.Abs(v));runEnergy+=v*v;
                runAbsoluteSum+=Math.Abs(v);
                if(Math.Abs(v)>runAmplitude){runAmplitude=Math.Abs(v);runPeak=i;}
            }
        }
        s.Energy=energy/fs;
        if(maximum>0)s.EquivalentWidthNs=energy/(maximum*maximum)/fs*1e9;
        double significant=s.LobeEnergies.Sum();
        if(significant>0)
        {
            s.FirstEnergyFraction=s.LobeEnergies[0]/significant;
            s.SecondEnergyFraction=s.LobeEnergies.Count>1?s.LobeEnergies[1]/significant:0;
            s.MainEnergyFraction=s.LobeEnergies.Max()/significant;
            if(s.LobeEnergies.Count>=2)
            {
                double e1=s.LobeEnergies[0],e2=s.LobeEnergies[1],sum=e1+e2;
                s.TemporalEnergyBalance=(e1-e2)/sum;
                s.TemporalBalanceUncertainty=Math.Min(2,2*(s.LobeEnergyUncertainties[0]+s.LobeEnergyUncertainties[1])/sum);
            }
        }
        s.Valid=w.Valid&&Finite(s.Energy)&&s.Energy>0&&Finite(s.EquivalentWidthNs)&&significant>0;
        s.Reason=s.Valid?"guarded_fixed_10k_3MHz_full_support_chronological_lobes":w.Reason;
        return s;
    }

    private static A4EndpointPrecursor A4EndpointPrecedingWindow(A4Context c,A4Pulse a,
        IList<A4Pulse> pulses,int tolerance,double fixedRawNoise,A4EndpointBudget budget)
    {
        var p=new A4EndpointPrecursor();
        long shift=(long)Math.Round(c.RoundTripExact);
        p.ExpectedPeak=(int)Math.Max(int.MinValue,Math.Min(int.MaxValue,(long)A4ChargeIndex(a)-shift));
        p.RequestedStart=(long)a.Start-shift-tolerance;
        p.RequestedEnd=(long)a.End-shift+tolerance;
        long gl=p.RequestedStart-A4EndpointGuardPoints,gr=p.RequestedEnd+A4EndpointGuardPoints;
        if(gl<0||gr>=c.Raw.Length||p.RequestedEnd>=a.Start||p.RequestedEnd<p.RequestedStart)
        {p.Reason="preceding_support_and_two_guards_not_fully_covered_or_overlap_first_packet";return p;}
        long count=gr-gl+1;
        if(count>int.MaxValue||!budget.Take((int)count))
        {p.Reason="diagnostic_raw_scan_budget_incomplete";return p;}
        p.GuardStart=(int)gl;p.GuardEnd=(int)gr;p.Covered=true;
        var guards=new List<double>();
        for(int i=(int)gl;i<=(int)gr;i++)
        {
            if(!Finite(c.Raw[i])){p.Covered=false;p.Reason="nonfinite_preceding_raw_window";return p;}
            if(i<p.RequestedStart||i>p.RequestedEnd)guards.Add(c.Raw[i]);
        }
        p.Baseline=Median(guards.ToArray());
        double local=RobustSigma(guards.ToArray(),p.Baseline);
        // Raw predecessor samples need a raw-domain noise reference. The
        // displayed/filtered packet's NoiseSigma must never be substituted.
        double reference=fixedRawNoise;
        if(!Finite(local)||!Finite(reference)||reference<=0)
        {p.Reason="preceding_raw_noise_reference_unavailable";return p;}
        p.Noise=Math.Max(reference,local);p.Threshold=A4EndpointLobeSnr*p.Noise;
        // A contaminated guard must not enlarge the threshold to manufacture
        // absence. This conservative guard test is auxiliary, never a gate on
        // discovery. Guard peaks and a >3x noise increase invalidate absence.
        p.QuietGuards=local<=3*reference&&guards.All(v=>Math.Abs(v-p.Baseline)<=A4EndpointLobeSnr*reference);
        p.RawPeak=0;
        for(int i=(int)p.RequestedStart;i<=p.RequestedEnd;i++)
            p.RawPeak=Math.Max(p.RawPeak,Math.Abs(c.Raw[i]-p.Baseline));
        foreach(var q in pulses)
        {
            if(!budget.Take()){p.Reason="diagnostic_packet_scan_budget_incomplete";return p;}
            int peak=A4ChargeIndex(q);
            if(peak>=p.RequestedStart&&peak<=p.RequestedEnd&&q.MappingValid&&Finite(q.Snr)&&q.Snr>=A4EndpointLobeSnr)
                p.PacketPeaks.Add(peak);
        }
        p.Significant=p.PacketPeaks.Count>0||p.RawPeak>p.Threshold;
        p.AbsenceSupported=p.QuietGuards&&!p.Significant;
        p.Reason=p.Significant?"significant_raw_activity_or_packet_in_expected_preceding_window":
            p.QuietGuards?"no_activity_above_reported_noise_threshold_in_fully_observed_preceding_window":
            "preceding_guards_contaminated_absence_not_established";
        return p;
    }

    private static string A4EndpointShapeJson(A4EndpointShape s)
    {
        return "{\"valid\":"+(s.Valid?"true":"false")+",\"reason\":"+Json(s.Reason)+
            ",\"main_lobe_fwhm_ns\":"+NumberJson(s.WidthNs)+",\"component_fwhm_ns\":"+NumberJson(s.ComponentWidthNs)+
            ",\"full_support_energy_pc2_seconds\":"+NumberJson(s.Energy)+",\"energy_over_peak2_width_ns\":"+NumberJson(s.EquivalentWidthNs)+
            ",\"same_polarity_half_height_multiple_components\":"+(s.MultiComponent?"true":"false")+
            ",\"chronological_significant_lobe_peaks\":["+String.Join(",",s.LobePeaks)+"],\"chronological_lobe_signs\":["+String.Join(",",s.LobeSigns)+
            "],\"chronological_lobe_energies\":["+String.Join(",",s.LobeEnergies.Select(NumberJson))+"],\"first_lobe_energy_fraction\":"+NumberJson(s.FirstEnergyFraction)+
            ",\"second_lobe_energy_fraction\":"+NumberJson(s.SecondEnergyFraction)+",\"largest_lobe_energy_fraction\":"+NumberJson(s.MainEnergyFraction)+
            ",\"first_minus_second_energy_balance\":"+NumberJson(s.TemporalEnergyBalance)+
            ",\"balance_engineering_perturbation_bound\":"+NumberJson(s.TemporalBalanceUncertainty)+"}";
    }
    private static string A4EndpointPrecursorJson(A4EndpointPrecursor p)
    {
        return "{\"expected_peak\":"+p.ExpectedPeak+",\"requested_support_start\":"+p.RequestedStart+",\"requested_support_end\":"+p.RequestedEnd+
            ",\"full_raw_and_guard_coverage\":"+(p.Covered?"true":"false")+",\"quiet_guards\":"+(p.QuietGuards?"true":"false")+
            ",\"significant_predecessor_activity\":"+(p.Significant?"true":"false")+",\"absence_above_threshold_supported\":"+(p.AbsenceSupported?"true":"false")+
            ",\"raw_baseline_pc\":"+NumberJson(p.Baseline)+",\"raw_noise_sigma_pc\":"+NumberJson(p.Noise)+",\"raw_peak_pc\":"+NumberJson(p.RawPeak)+
            ",\"activity_threshold_pc\":"+NumberJson(p.Threshold)+",\"detected_packet_raw_peaks\":["+String.Join(",",p.PacketPeaks.Distinct().OrderBy(v=>v))+
            "],\"reason\":"+Json(p.Reason)+"}";
    }
    private static double A4EndpointRelativeError(double measured,double reference)
    {return Finite(measured)&&Finite(reference)&&reference>0?100*Math.Abs(measured/reference-1):double.NaN;}
    private static bool A4EndpointSameRaw(A4Pulse a,A4Pulse b)
    {return a!=null&&b!=null&&A4ChargeIndex(a)==A4ChargeIndex(b);}
    private static string A4EndpointEdgeKey(A4Pair p)
    {return A4ChargeIndex(p.Incident)+":"+A4ChargeIndex(p.Reflection);}
    private static bool A4EndpointCommitted(A4Pair p)
    {return p!=null&&!p.Ambiguous&&p.Incident!=null&&p.Reflection!=null&&
        p.Incident.Owner>=0&&p.Incident.Owner==p.Reflection.Owner&&(p.State=="confirmed"||p.State=="accepted");}

    // Method 6 stores the already evaluated measurements/gates outside A4Pair.
    // Adapt them into diagnostic-only pair/context objects; do not attach fields
    // to original pairs and do not manufacture an A4 attenuation-deviation dB.
    private string A6EndpointHypothesisJson(A4Context c,IList<A4Pair> currentPairs,IList<A4Pulse> pulses)
    {
        try
        {
            if(c==null||currentPairs==null||pulses==null)
                return "{\"enabled\":false,\"reason\":\"method6_endpoint_context_unavailable\"}";
            if(a6Evidence.Count>A4MaximumCandidateEvaluations||currentPairs.Count>A4MaximumCandidateEvaluations)
                return "{\"enabled\":true,\"complete\":false,\"reason\":\"method6_endpoint_inventory_budget_incomplete\",\"pairs\":[]}";
            var copies=new Dictionary<A4Pair,A4Pair>();var native=new Dictionary<A4Pair,A6Evidence>();
            foreach(var original in a6Evidence.Keys.Concat(currentPairs).Distinct())
            {
                if(original==null)continue;
                var copy=new A4Pair {Incident=original.Incident,Reflection=original.Reflection,
                    Confidence=original.Confidence,Position=original.Position,EdgeId=original.EdgeId,
                    State=original.State,ReviewReason=original.ReviewReason,Ambiguous=original.Ambiguous,
                    Blind=original.Blind,Amplified=original.Amplified,ParallelAmbiguous=original.ParallelAmbiguous,
                    BoundaryAmbiguous=original.BoundaryAmbiguous,OutputIncident=original.OutputIncident,
                    OutputReflection=original.OutputReflection};
                A6Evidence e;
                if(a6Evidence.TryGetValue(original,out e)&&e!=null)
                {
                    copy.UnifiedCorrelation=new A4UnifiedCorrelationEvidence {Incident=e.A,Reflection=e.B,
                        Measured=e.Correlation,Reference=e.CorrelationReference,Floor=e.CorrelationFloor,
                        Score=e.CorrelationScore,TravelFraction=e.TravelFraction,Basis=e.CorrelationBasis,
                        Available=e.A!=null&&e.B!=null&&e.A.Valid&&e.B.Valid&&e.Correlation!=null&&e.Correlation.Valid,
                        Passed=e.Eligible,CanonicalDelay=original.Incident==null||original.Reflection==null?0:
                            A4ChargeIndex(original.Reflection)-A4ChargeIndex(original.Incident)};
                    native.Add(copy,e);
                }
                copies.Add(original,copy);
            }
            var view=A4ReviewContext(c);
            view.RoleReviewLimitExceeded=c.RoleReviewLimitExceeded||a6IncompleteComponents>0;
            double unusedReference,unusedFloor,unusedMargin;
            var cal=c.AttenuationScoringReference;var rho=a6CalibrationCorrelation;
            bool calShape=rho!=null&&rho.Valid&&rho.StructureCompatible&&!rho.OppositeTemporalPolarity&&rho.PolarityRatio>0&&
                rho.Fixed>=A6Settings.MinimumUsableCalibrationCorrelation&&
                A4UnifiedCorrelationReferenceMath(rho.Fixed,rho.Lower,1,out unusedReference,out unusedFloor,out unusedMargin);
            bool calReliable=a6ReferenceReliable&&cal!=null&&cal.Valid&&Finite(cal.TravelSeconds)&&cal.TravelSeconds>0&&
                cal.IncidentSnr>=6&&cal.ReflectionSnr>=6&&calShape;
            view.UnifiedCorrelation=new A4UnifiedCorrelationState {
                CalibrationMeasurement=c.AttenuationScoringReference,
                CalibrationCorrelation=a6CalibrationCorrelation,
                CalibrationReliable=calReliable,
                CalibrationReason=calReliable?a6ReferenceReason:"method6_CAL_shape_travel_or_guarded_quality_not_endpoint_usable"};
            var outputs=currentPairs.Where(p=>p!=null&&copies.ContainsKey(p)).Select(p=>copies[p]).ToList();
            return A4EndpointHypothesisCoreJson(view,outputs,pulses,copies.Values.ToList(),native);
        }
        catch(Exception ex)
        {
            return "{\"enabled\":false,\"complete\":false,\"diagnostic_only\":true,\"position_changed\":false,"+
                "\"acceptance_changed\":false,\"reason\":\"method6_endpoint_diagnostic_unavailable\",\"exception_type\":"+Json(ex.GetType().Name)+",\"pairs\":[]}";
        }
    }
    private static bool A4EndpointCachedEvidence(A4Context c,A4Pair p,Dictionary<A4Pair,A6Evidence> native,out string reason)
    {
        if(native==null)return A4Index4StepEvidence(c,p,out reason);
        reason="method6_cached_evidence_unavailable";
        A6Evidence e;
        if(p==null||!native.TryGetValue(p,out e)||e==null)return false;
        reason="method6_existing_enabled_gates_not_all_passed";
        if(!e.Eligible)return false;
        reason="method6_unreliable_or_overlapping_packet_support";
        if(!A4Index4PacketReliable(c,p.Incident)||!A4Index4PacketReliable(c,p.Reflection)||
            p.Incident.End>=p.Reflection.Start||p.Blind||p.Amplified||p.ParallelAmbiguous||p.BoundaryAmbiguous||
            !Finite(p.Confidence)||p.Confidence<=A4MinimumOutputConfidence)return false;
        reason="method6_signed_correlation_or_guarded_measurement_not_endpoint_quality";
        var r=e.Correlation;
        if(!A4Index4FixedMeasurementReliable(e.A)||!A4Index4FixedMeasurementReliable(e.B)||r==null||!r.Valid||
            !r.StructureCompatible||r.OppositeTemporalPolarity||!Finite(r.PolarityRatio)||r.PolarityRatio<=0||
            !Finite(r.Fixed)||!Finite(e.CorrelationFloor)||r.Fixed<e.CorrelationFloor||
            e.Pair==null||e.Pair.OutputReflection-e.Pair.OutputIncident!=A4ChargeIndex(p.Reflection)-A4ChargeIndex(p.Incident))return false;
        reason="method6_existing_signed_correlation_and_all_enabled_gates_passed";return true;
    }

    // currentPairs must be the pairs actually represented in this call's rows;
    // evaluatedPairs may include already measured primary/review alternatives.
    // Historical rows without guarded packets cannot become new evidence here.
    // Pass the complete packet inventory, not just the displayed waveform crop.
    private static string A4EndpointHypothesisJson(A4Context c,IList<A4Pair> currentPairs,
        IList<A4Pulse> pulses,IList<A4Pair> evaluatedPairs)
    {
        try {return A4EndpointHypothesisCoreJson(c,currentPairs,pulses,evaluatedPairs);}
        catch(Exception ex)
        {
            // Diagnostics must not propagate an exception into the public
            // method's failure handler and erase otherwise valid primary rows.
            return "{\"enabled\":false,\"complete\":false,\"diagnostic_only\":true,\"position_changed\":false,"+
                "\"acceptance_changed\":false,\"reason\":\"endpoint_diagnostic_unavailable\",\"exception_type\":"+Json(ex.GetType().Name)+",\"pairs\":[]}";
        }
    }
    private static string A4EndpointHypothesisCoreJson(A4Context c,IList<A4Pair> currentPairs,
        IList<A4Pulse> pulses,IList<A4Pair> evaluatedPairs,Dictionary<A4Pair,A6Evidence> native=null)
    {
        if(c==null||c.Raw==null||currentPairs==null||pulses==null||!Finite(c.RoundTripExact)||c.RoundTripExact<=0||
            !Finite(c.Fs)||c.Fs<=0||!Finite(c.Length)||c.Length<=0)
            return "{\"enabled\":false,\"reason\":\"endpoint_diagnostic_context_unavailable\"}";
        if(currentPairs.Count>A4MaximumCandidateEvaluations||pulses.Count>A4MaximumCandidateEvaluations||
            (evaluatedPairs!=null&&evaluatedPairs.Count>A4MaximumCandidateEvaluations))
            return "{\"enabled\":true,\"complete\":false,\"reason\":\"endpoint_inventory_budget_incomplete\",\"pairs\":[]}";
        var budget=new A4EndpointBudget();
        var edges=(evaluatedPairs??currentPairs).Concat(currentPairs).Where(p=>p!=null&&p.Incident!=null&&p.Reflection!=null)
            .GroupBy(A4EndpointEdgeKey).Select(g=>g.First()).OrderBy(p=>A4ChargeIndex(p.Incident)).ThenBy(p=>A4ChargeIndex(p.Reflection)).ToArray();
        var rows=new List<string>();int row=-1;
        foreach(var pair in currentPairs)
        {
            row++;
            if(!budget.Take())break;
            if(pair==null||pair.Incident==null||pair.Reflection==null)continue;
            int delay,tolerance;
            if(!A4Index4IsRoundTrip(c,pair,out delay,out tolerance))continue;
            var a=pair.Incident;var b=pair.Reflection;var u=pair.UnifiedCorrelation;
            string evidenceReason;
            bool pairReliable=A4EndpointCachedEvidence(c,pair,native,out evidenceReason);
            if(pair.Ambiguous){pairReliable=false;evidenceReason="current_pair_ambiguous_no_endpoint_support";}
            var sa=A4EndpointMeasureShape(u==null?null:u.Incident,c.Fs);
            var sb=A4EndpointMeasureShape(u==null?null:u.Reflection,c.Fs);
            var reference=c.UnifiedCorrelation==null?null:c.UnifiedCorrelation.CalibrationMeasurement;
            bool comparable=c.Calibration!=null&&c.UnifiedCorrelation!=null&&c.UnifiedCorrelation.CalibrationReliable&&
                reference!=null&&reference.Valid&&Finite(c.Calibration.SampleRate)&&c.Calibration.SampleRate>0;
            var ca=A4EndpointMeasureShape(comparable?reference.Incident:null,comparable?c.Calibration.SampleRate:0);
            var cb=A4EndpointMeasureShape(comparable?reference.Reflection:null,comparable?c.Calibration.SampleRate:0);
            comparable=comparable&&ca.Valid&&cb.Valid&&sa.Valid&&sb.Valid;
            double widthRatio=sa.Valid&&sb.Valid?sb.WidthNs/sa.WidthNs:double.NaN;
            double calWidthRatio=ca.Valid&&cb.Valid?cb.WidthNs/ca.WidthNs:double.NaN;
            double energyWidthRatio=sa.Valid&&sb.Valid?sb.EquivalentWidthNs/sa.EquivalentWidthNs:double.NaN;
            double calEnergyWidthRatio=ca.Valid&&cb.Valid?cb.EquivalentWidthNs/ca.EquivalentWidthNs:double.NaN;
            double widthError=A4EndpointRelativeError(widthRatio,calWidthRatio);
            double energyWidthError=A4EndpointRelativeError(energyWidthRatio,calEnergyWidthRatio);
            bool shapeCompatible=comparable&&widthError<=A4EndpointShapeErrorPercent&&energyWidthError<=A4EndpointShapeErrorPercent;
            bool overlapSuspected=sa.MultiComponent||(sa.LobePeaks.Count>=3&&ca.Valid&&sa.LobePeaks.Count>ca.LobePeaks.Count);
            double calBalanceContrast=ca.TemporalEnergyBalance-cb.TemporalEnergyBalance;
            double calBalanceUncertainty=ca.TemporalBalanceUncertainty+cb.TemporalBalanceUncertainty;
            bool energyDiscriminating=comparable&&Finite(calBalanceContrast)&&Finite(calBalanceUncertainty)&&
                calBalanceContrast>calBalanceUncertainty+.15;
            string morphology="energy_factor_nondiscriminating";
            double balanceBoundary=energyDiscriminating?.5*(ca.TemporalEnergyBalance+cb.TemporalEnergyBalance):double.NaN;
            double balanceMargin=energyDiscriminating?sa.TemporalBalanceUncertainty+.5*calBalanceUncertainty:double.NaN;
            if(energyDiscriminating&&Finite(sa.TemporalEnergyBalance)&&Finite(balanceMargin))
            {
                if(sa.TemporalEnergyBalance>balanceBoundary+balanceMargin)morphology="cal_incident_like";
                else if(sa.TemporalEnergyBalance<balanceBoundary-balanceMargin)morphology="cal_reflection_like_far_morphology_suggested";
                else morphology="energy_balance_inside_indeterminate_interval";
            }
            double ordinaryPosition=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length);
            double scopeMeters=c.Velocities!=null&&c.Velocities.Length>0&&Finite(c.Velocities[0])?
                tolerance/c.Fs*1e6*.5*c.Velocities[0]:double.NaN;
            int farResolutionSamples=0;
            if(comparable)
            {
                double samples=(reference.Incident.End-reference.Incident.Start+1)*c.Fs/c.Calibration.SampleRate;
                if(Finite(samples)&&samples>0&&samples<int.MaxValue)
                    farResolutionSamples=Math.Max(tolerance,(int)Math.Ceiling(samples));
            }
            // A whole guarded CAL packet defines one observable pulse-resolution
            // unit, not an exact terminal distance. If that unit approaches a
            // quarter of a round trip it cannot identify a terminal region.
            bool farResolutionUsable=farResolutionSamples>0&&farResolutionSamples<c.RoundTripExact/4;
            double farExtent=farResolutionUsable?c.Length-A4BoundaryOutputPosition(farResolutionSamples,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
            var pre=A4EndpointPrecedingWindow(c,a,pulses,tolerance,u!=null&&u.Incident!=null?u.Incident.RawNoise:double.NaN,budget);
            var reasons=new List<string>();var echoRows=new List<string>();var usedTimes=new HashSet<int>{A4ChargeIndex(b)};
            int additionalEchoes=0;bool farIndependent=false;string farProof="null";
            var farAlternatives=new HashSet<string>();
            // The selected A->B event is not reused as corroboration. An RT
            // successor or predecessor can establish a possible echo role, not
            // a cable end. Repeated aliases at the same raw time count once.
            foreach(var edge in edges)
            {
                if(!budget.Take())break;
                int ed,et;string er;
                if(!A4Index4IsRoundTrip(c,edge,out ed,out et)||!A4EndpointCachedEvidence(c,edge,native,out er))continue;
                bool follows=A4EndpointSameRaw(edge.Incident,b);
                bool precedes=A4EndpointSameRaw(edge.Reflection,a);
                if(!follows&&!precedes)continue;
                int time=follows?A4ChargeIndex(edge.Reflection):A4ChargeIndex(edge.Incident);
                if(!usedTimes.Add(time))continue;
                additionalEchoes++;
                echoRows.Add("{\"raw_incident\":"+A4ChargeIndex(edge.Incident)+",\"raw_reflection\":"+A4ChargeIndex(edge.Reflection)+
                    ",\"delay_samples\":"+ed+",\"direction\":"+Json(follows?"following":"preceding")+",\"endpoint_proof\":false}");
            }
            // A resolvable independent short A->C pair provides near-L geometry
            // under the ordinary formula. Require its unique, separate C->D RT
            // echo and a corresponding B->D short pair as well. All four raw
            // supports must be disjoint; a bipolar packet cannot manufacture C.
            // This route is intentionally unavailable for exactly merged arrivals.
            foreach(var shortPair in edges)
            {
                if(!budget.Take())break;
                string sr;
                if(!farResolutionUsable||!A4EndpointSameRaw(shortPair.Incident,a)||shortPair.Ambiguous||
                    !A4EndpointCachedEvidence(c,shortPair,native,out sr))continue;
                var other=shortPair.Reflection;int shortDelay=A4ChargeIndex(other)-A4ChargeIndex(a);
                if(shortDelay<=0||shortDelay>farResolutionSamples||a.End>=other.Start||other.End>=b.Start)continue;
                foreach(var companion in edges)
                {
                    if(!budget.Take())break;
                    int cd,ct;string cr;
                    if(companion.Ambiguous||!A4EndpointSameRaw(companion.Incident,other)||!A4Index4IsRoundTrip(c,companion,out cd,out ct)||
                        !A4EndpointCachedEvidence(c,companion,native,out cr))continue;
                    var d=companion.Reflection;
                    if(b.End>=d.Start||Math.Abs(cd-delay)>2*Math.Min(ct,tolerance)||
                        new[]{A4ChargeIndex(a),A4ChargeIndex(other),A4ChargeIndex(b),A4ChargeIndex(d)}.Distinct().Count()!=4)continue;
                    A4Pair shortEcho=null;
                    foreach(var e in edges)
                    {
                        if(!budget.Take())break;
                        string qr;
                        if(!e.Ambiguous&&A4EndpointSameRaw(e.Incident,b)&&A4EndpointSameRaw(e.Reflection,d)&&
                            A4EndpointCachedEvidence(c,e,native,out qr)&&Math.Abs((A4ChargeIndex(d)-A4ChargeIndex(b))-shortDelay)<=2*tolerance)
                        {shortEcho=e;break;}
                    }
                    if(shortEcho==null)continue;
                    // Same guarded amplitude for every reuse of an identity.
                    var am=u==null?null:u.Incident;var bm=u==null?null:u.Reflection;
                    var cm=companion.UnifiedCorrelation.Incident;var dm=companion.UnifiedCorrelation.Reflection;
                    if(am==null||bm==null||!A4EndpointSameMeasurement(am,shortPair.UnifiedCorrelation.Incident,budget)||
                        !A4EndpointSameMeasurement(cm,shortPair.UnifiedCorrelation.Reflection,budget)||
                        !A4EndpointSameMeasurement(bm,shortEcho.UnifiedCorrelation.Incident,budget)||
                        !A4EndpointSameMeasurement(dm,shortEcho.UnifiedCorrelation.Reflection,budget))continue;
                    double na=2*Math.Sqrt(am.Noise*am.Noise+bm.Noise*bm.Noise),nc=2*Math.Sqrt(cm.Noise*cm.Noise+dm.Noise*dm.Noise);
                    if(am.Amplitude-bm.Amplitude<=na||cm.Amplitude-dm.Amplitude<=nc)continue;
                    double ratioError=Math.Abs(20*Math.Log10((bm.Amplitude/am.Amplitude)/(dm.Amplitude/cm.Amplitude)));
                    double ratioAllowance=3+20*Math.Log10(1+na/bm.Amplitude+nc/dm.Amplitude);
                    if(!Finite(ratioError)||ratioError>ratioAllowance)continue;
                    string alternative=A4ChargeIndex(other)+":"+A4ChargeIndex(d);
                    if(!farAlternatives.Add(alternative))continue;
                    farProof="{\"raw_packet_indices\":["+A4ChargeIndex(a)+","+A4ChargeIndex(other)+","+A4ChargeIndex(b)+","+A4ChargeIndex(d)+
                        "],\"independent_short_delay_samples\":"+shortDelay+",\"independent_short_pair_position_m\":"+
                        NumberJson(A4BoundaryOutputPosition(shortDelay,c.Fs,c.Joints,c.Velocities,c.Length))+",\"additional_echo_raw_time\":"+A4ChargeIndex(d)+
                        ",\"two_echo_ratio_difference_db\":"+NumberJson(ratioError)+",\"ratio_allowance_db\":"+NumberJson(ratioAllowance)+"}";
                    if(usedTimes.Add(A4ChargeIndex(d)))
                    {
                        additionalEchoes++;
                        echoRows.Add("{\"raw_incident\":"+A4ChargeIndex(other)+",\"raw_reflection\":"+A4ChargeIndex(d)+
                            ",\"delay_samples\":"+cd+",\"direction\":\"independent_companion\",\"endpoint_proof\":false}");
                    }
                }
            }
            farIndependent=farAlternatives.Count==1;
            if(!farIndependent)farProof="null";
            bool recordComplete=!A4RecordReviewIncomplete(c)&&budget.Complete;
            string state="endpoint_unresolved";
            if(!recordComplete)reasons.Add("candidate_or_diagnostic_evidence_inventory_incomplete");
            if(!Finite(ordinaryPosition))reasons.Add("piecewise_cable_geometry_unavailable");
            if(!pairReliable)reasons.Add(evidenceReason);
            if(!pre.Covered||!pre.QuietGuards)reasons.Add("preceding_window_absence_not_identifiable");
            if(pre.Significant)reasons.Add("significant_preceding_activity_possible_later_echo_or_superposition");
            if(!comparable)reasons.Add("same_band_guarded_CAL_width_or_lobe_comparison_unavailable");
            else if(!shapeCompatible)reasons.Add("width_or_energy_width_ratio_outside_reported_CAL_consistency_interval");
            if(overlapSuspected)reasons.Add("multiple_lobes_or_components_are_shape_evidence_not_resolved_independent_arrivals");
            if(!energyDiscriminating)reasons.Add("CAL_lobe_energy_contrast_not_noise_resolved_no_energy_based_endpoint_assignment");
            else reasons.Add(morphology);
            if(!farResolutionUsable)reasons.Add("CAL_full_packet_resolution_unit_unavailable_or_too_broad_for_terminal_interpretation");
            if(farAlternatives.Count>1)reasons.Add("multiple_independent_endpoint_topologies_no_unique_far_hypothesis");
            if(recordComplete&&Finite(ordinaryPosition)&&pairReliable&&pre.AbsenceSupported&&shapeCompatible)
            {
                if(farIndependent){state="far_end_supported";reasons.Add("resolved_near_L_short_pair_and_distinct_corresponding_echo_support_far_hypothesis_only");}
                else if(farAlternatives.Count==0&&!overlapSuspected&&A4EndpointCommitted(pair)&&morphology=="cal_incident_like")
                {state="near_end_supported";reasons.Add("ordinary_near_zero_geometry_with_CAL_compatible_isolated_packet_supports_near_hypothesis_only");}
            }
            if(state=="endpoint_unresolved")reasons.Add("RT_and_shape_alone_do_not_identify_near_vs_far_end_exactly_overlapped_arrivals_remain_unresolved");
            rows.Add("{\"diagnostic_pair_ordinal\":"+row+",\"raw_incident\":"+A4ChargeIndex(a)+",\"raw_reflection\":"+A4ChargeIndex(b)+
                ",\"hypothesis\":"+Json(state)+",\"label_zh\":"+Json(state=="near_end_supported"?"近端倾向":state=="far_end_supported"?"对端倾向":"端部待定")+
                ",\"canonical_delay_samples\":"+delay+",\"RT_tolerance_samples\":"+tolerance+",\"near_zero_scope_half_width_m\":"+NumberJson(scopeMeters)+
                ",\"ordinary_geometry_position_m\":"+NumberJson(ordinaryPosition)+",\"pair_fixed_band_evidence_passed\":"+(pairReliable?"true":"false")+
                ",\"same_band_CAL_comparable\":"+(comparable?"true":"false")+",\"width_ratio\":"+NumberJson(widthRatio)+",\"CAL_width_ratio\":"+NumberJson(calWidthRatio)+
                ",\"width_ratio_error_percent\":"+NumberJson(widthError)+",\"energy_width_ratio\":"+NumberJson(energyWidthRatio)+",\"CAL_energy_width_ratio\":"+NumberJson(calEnergyWidthRatio)+
                ",\"energy_width_ratio_error_percent\":"+NumberJson(energyWidthError)+",\"first_packet_overlap_shape_suspected\":"+(overlapSuspected?"true":"false")+
                ",\"lobe_energy_morphology\":"+Json(morphology)+",\"CAL_balance_contrast\":"+NumberJson(calBalanceContrast)+
                ",\"CAL_balance_contrast_uncertainty\":"+NumberJson(calBalanceUncertainty)+",\"balance_midpoint\":"+NumberJson(balanceBoundary)+
                ",\"balance_indeterminate_half_width\":"+NumberJson(balanceMargin)+",\"far_resolution_samples\":"+farResolutionSamples+
                ",\"far_end_resolution_extent_m\":"+NumberJson(farExtent)+",\"far_resolution_usable\":"+(farResolutionUsable?"true":"false")+
                ",\"incident_shape\":"+A4EndpointShapeJson(sa)+",\"reflection_shape\":"+A4EndpointShapeJson(sb)+",\"CAL_incident_shape\":"+A4EndpointShapeJson(ca)+
                ",\"CAL_reflection_shape\":"+A4EndpointShapeJson(cb)+",\"preceding_window\":"+A4EndpointPrecursorJson(pre)+
                ",\"additional_distinct_echo_events\":"+additionalEchoes+",\"echo_events\":["+String.Join(",",echoRows)+"],\"far_independent_support\":"+farProof+
                ",\"far_independent_alternative_count\":"+farAlternatives.Count+",\"far_alternative_raw_identity_keys\":["+String.Join(",",farAlternatives.OrderBy(v=>v).Select(Json))+"]"+
                ",\"reasons\":["+String.Join(",",reasons.Distinct().Select(Json))+"]}");
        }
        if(!budget.Complete)
            return "{\"enabled\":true,\"diagnostic_only\":true,\"complete\":false,\"reason\":\"endpoint_diagnostic_budget_incomplete_no_hypothesis_published\",\"steps\":"+
                budget.Steps+",\"position_changed\":false,\"acceptance_changed\":false,\"pairs\":[]}";
        return "{\"enabled\":true,\"diagnostic_only\":true,\"complete\":"+(!A4RecordReviewIncomplete(c)?"true":"false")+",\"steps\":"+budget.Steps+
            ",\"position_changed\":false,\"acceptance_changed\":false,\"probability_score\":false,\"endpoint_confirmed\":false,\"history_used\":false,"+
            "\"pair_association\":\"join_raw_incident_raw_reflection_to_output_pulse_roles_canonical_raw_peak_canonical_raw_reflection; diagnostic_pair_ordinal_is_not_LabVIEW_matrix_row\",\"evidence_source\":"+
            Json(native==null?"existing_index4_unified_evidence":"existing_method6_cached_measurements_and_native_enabled_gates")+","+
            "\"band_hz\":[10000,3000000],\"shape_ratio_error_percent\":"+NumberJson(A4EndpointShapeErrorPercent)+
            ",\"shape_ratio_interval_role\":\"diagnostic_engineering_consistency_only_not_a_fitted_endpoint_classifier\",\"lobe_peak_noise_sigma\":"+NumberJson(A4EndpointLobeSnr)+
            ",\"lobe_energy_threshold\":\"noise_sigma_squared*(N+4.5*sqrt(2*N))\",\"precursor_guard_points_per_side\":"+A4EndpointGuardPoints+
            ",\"CAL_energy_contrast_minimum_beyond_uncertainty\":0.15,\"energy_balance_perturbation\":\"each_sample_plus_or_minus_2_fixed_band_noise; engineering_bound_not_probability\","+
            "\"energy_morphology_interpretation\":\"CAL_incident_like_vs_CAL_reflection_like_only; reflection_CAL_is_not_an_invented_half_distance_or_unique_far_end_template\","+
            "\"far_resolution_rule\":\"one_CAL_incident_full_support_duration_mapped_to_LOC_samples; must_be_less_than_RT/4; report_piecewise_terminal_extent_not_exact_L\","+
            "\"RT_scope_rule\":\"min(existing_EchoTolerance(A),existing_EchoTolerance(B)); each=max(2,min(10,period/3))\","+
            "\"identifiability_limit\":\"single_ended_RT_timing_and_pulse_shape_do_not_uniquely_identify_cable_end; merged_arrivals_are_not_resolved_by_lobe_count; no_coordinate_reversal\","+
            "\"echo_deduplication\":\"raw_packet_identity_and_event_time; selected_reflection_time_excluded_from_additional_echo_count\",\"pairs\":["+String.Join(",",rows)+"]}";
    }
    private static bool A4EndpointSameMeasurement(A4AttenuationMeasurement a,A4AttenuationMeasurement b,A4EndpointBudget budget)
    {
        if(a==null||b==null||!a.Valid||!b.Valid||a.Start!=b.Start||a.End!=b.End||a.Peak!=b.Peak||
            a.Amplitude!=b.Amplitude||a.Noise!=b.Noise||a.Baseline!=b.Baseline||a.SignalOrigin!=b.SignalOrigin||
            a.SignalSamples==null||b.SignalSamples==null||a.SignalSamples.Length!=b.SignalSamples.Length)return false;
        if(ReferenceEquals(a.SignalSamples,b.SignalSamples))return true;
        return budget.Take(a.SignalSamples.Length)&&a.SignalSamples.SequenceEqual(b.SignalSamples);
    }
}
