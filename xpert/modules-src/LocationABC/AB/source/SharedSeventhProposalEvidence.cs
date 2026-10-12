using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.SignalEvidence
{
    // Method 7 only. Engineering evidence, not a probability or a learned
    // location prior. Raw arrays are already baseline-detrended engineering pC.
    internal sealed class SeventhProposalLobe
    {
        internal bool Complete;
        internal int Peak=-1,CanonicalPeak=-1,Left=-1,Right=-1,Sign;
        internal double Amplitude=double.NaN,Noise=double.NaN,Snr=double.NaN;
        internal bool QuietLeft,QuietRight;
        internal string Reason="unmeasured_lobe";
    }
    internal sealed class SeventhProposalEvidence
    {
        internal int Peak=-1,CoreHalf,LongHalf;
        internal bool Passed,ShortContext,Ambiguous,FilterPolarityConflict;
        internal SeventhProposalLobe Incident,Reflection;
        internal double CoreCorrelation=double.NaN,LongCorrelation=double.NaN,RawCorrelation=double.NaN;
        internal double Correlation=double.NaN,Score=double.NaN,AmplitudeRatio=double.NaN;
        internal string Reason="not_measured";
    }
    internal sealed class SeventhProposalSearch
    {
        internal SeventhProposalEvidence Best,Second;
        internal int Evaluated,Qualified,IncompleteCompetitors,ObservedExtrema,MergedExtrema;
        internal bool Complete=true,Ambiguous;
        internal string Reason="no_qualified_multiscale_waveform_candidate";
        internal readonly List<SeventhProposalEvidence> Candidates=new List<SeventhProposalEvidence>();
        // A failed measurement is not evidence that a strong physical
        // competitor does not exist. Keep identities, not only a count that
        // can be lost when candidates are rebuilt as graph edges.
        internal readonly List<int> IncompleteCompetitorPeaks=new List<int>();
    }
    internal static class SeventhProposalMath
    {
        internal const string Version="2026.10.01-method7-canonical-packet-graph-v5-incomplete-competitor-identity";
        internal const int CandidateBudget=128;
        static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
        static double Median(List<double> values)
        {if(values.Count==0)return double.NaN;values.Sort();int k=values.Count/2;return values.Count%2==0?(values[k-1]+values[k])*.5:values[k];}
        static double DifferenceNoise(double[] raw,int first,int last)
        {
            var diffs=new List<double>();
            for(int k=Math.Max(1,first);k<=Math.Min(raw.Length-1,last);k++)
            {if(!Finite(raw[k])||!Finite(raw[k-1]))return double.NaN;diffs.Add(raw[k]-raw[k-1]);}
            if(diffs.Count<6)return double.NaN;
            double center=Median(new List<double>(diffs));
            return 1.4826*Median(diffs.Select(x=>Math.Abs(x-center)).ToList())/Math.Sqrt(2);
        }
        static bool Quiet(double[] raw,int first,int last,double center,double noise)
        {
            if(first<0||last>=raw.Length||last-first+1<8)return false;
            double square=0,maximum=0;var values=new List<double>();
            for(int k=first;k<=last;k++)
            {double x=raw[k]-center;if(!Finite(x))return false;values.Add(x);square+=x*x;maximum=Math.Max(maximum,Math.Abs(x));}
            double scale=Math.Max(1e-12,noise);
            return Math.Abs(Median(values))<=4*scale&&Math.Sqrt(square/values.Count)<=5*scale&&maximum<=8*scale;
        }
        internal static bool HasStoredPlateau(double[] raw,int peak)
        {
            if(raw==null||peak<0||peak>=raw.Length)return true;
            if(!Finite(raw[peak]))return true;
            double tolerance=Math.Max(1e-15,Math.Abs(raw[peak])*1e-12);
            // Only a flat run CONNECTED TO this raw peak is evidence of a
            // stored peak plateau. Three baseline or low-level shoulder samples
            // nearby are not clipping and must not reject a narrow valid pulse.
            int left=peak,right=peak;
            while(left>Math.Max(0,peak-3)&&Finite(raw[left-1])&&Math.Abs(raw[left-1]-raw[peak])<=tolerance)left--;
            while(right<Math.Min(raw.Length-1,peak+3)&&Finite(raw[right+1])&&Math.Abs(raw[right+1]-raw[peak])<=tolerance)right++;
            return right-left+1>=3;
        }
        internal static SeventhProposalLobe Measure(double[] raw,int peak,double center,double noise,double fs,double[] storedRaw=null)
        {
            var d=new SeventhProposalLobe {Peak=peak,CanonicalPeak=peak,Noise=noise};
            if(raw==null||peak<=0||peak>=raw.Length-1||!Finite(center)||!Finite(noise)||noise<0||!Finite(fs)||fs<=0)
            {d.Reason="invalid_lobe_input";return d;}
            double value=raw[peak]-center;if(!Finite(value)||Math.Abs(value)<=1e-12){d.Reason="nonfinite_or_zero_peak";return d;}
            d.Sign=Math.Sign(value);d.Amplitude=Math.Abs(value);
            if(HasStoredPlateau(storedRaw??raw,peak)){d.Reason="stored_raw_three_sample_plateau_unresolved_clipping";return d;}
            if(!Finite(raw[peak-1])||!Finite(raw[peak+1])||d.Sign*(raw[peak-1]-center)>=d.Amplitude||d.Sign*(raw[peak+1]-center)>d.Amplitude)
            {d.Reason="not_signed_raw_extremum";return d;}
            // Locate BOTH measured 20%-height crossings, not synthetic peak +/-1.
            // A larger shoulder or a record boundary makes the lobe incomplete.
            int reach=Math.Max(8,Math.Min(512,(int)Math.Ceiling(Math.Min(fs,1e10)*.5e-6)));
            double level=Math.Max(.20*d.Amplitude,2*noise);
            int left=peak,right=peak;
            while(left>0&&peak-left<reach)
            {left--;if(!Finite(raw[left])){d.Reason="nonfinite_lobe_support";return d;}if(d.Sign*(raw[left]-center)<=level)break;}
            while(right<raw.Length-1&&right-peak<reach)
            {right++;if(!Finite(raw[right])){d.Reason="nonfinite_lobe_support";return d;}if(d.Sign*(raw[right]-center)<=level)break;}
            d.Left=left;d.Right=right;
            if(left==0||right==raw.Length-1||d.Sign*(raw[left]-center)>level||d.Sign*(raw[right]-center)>level)
            {d.Reason="main_lobe_crossing_unresolved_or_record_boundary";return d;}
            if(right-left<2){d.Reason="insufficient_measured_main_lobe";return d;}
            // One measured raw main lobe has ONE peak identity. Correlation may
            // not move its clock onto a smaller ripple inside this same lobe.
            // Ties have a deterministic earliest index; a stored plateau is
            // still rejected above rather than reinterpreted as a valid peak.
            int canonical=peak;double maximum=d.Amplitude;
            for(int k=left+1;k<right;k++)
            {
                double signed=d.Sign*(raw[k]-center);
                if(signed>maximum||(signed==maximum&&k<canonical)){maximum=signed;canonical=k;}
            }
            d.CanonicalPeak=canonical;
            if(canonical!=peak)
            {d.Reason="noncanonical_smaller_extremum_in_same_raw_main_lobe";return d;}
            int guard=Math.Max(8,Math.Min(32,(int)Math.Ceiling(Math.Min(fs,1e10)*128e-9)));
            double nLeft=DifferenceNoise(raw,left-guard-2,left-3),nRight=DifferenceNoise(raw,right+3,right+guard+2);
            // A noisy flank must not be averaged down by a quiet flank; a missing
            // flank is explicitly marked, while the other can support a review.
            if(Finite(nLeft))d.Noise=Math.Max(d.Noise,nLeft);
            if(Finite(nRight))d.Noise=Math.Max(d.Noise,nRight);
            d.Snr=d.Amplitude/Math.Max(1e-12,d.Noise);
            d.QuietLeft=Quiet(raw,left-guard-2,left-3,center,d.Noise);
            d.QuietRight=Quiet(raw,right+3,right+guard+2,center,d.Noise);
            d.Complete=Finite(d.Snr)&&d.Snr>=6;
            d.Reason=d.Complete?"two_raw_main_lobe_crossings_and_6sigma":"raw_main_lobe_snr_below_six";
            return d;
        }
        internal static int CanonicalPeak(double[] raw,int peak,double center,double noise,double fs,double[] storedRaw=null)
        {
            if(raw==null||peak<=0||peak>=raw.Length-1||!Finite(center)||!Finite(noise)||noise<0||!Finite(fs)||fs<=0)return -1;
            // Larger shoulders can enlarge the measured support. Follow the
            // strictly increasing raw maxima to a stable physical lobe identity;
            // never choose an arbitrary prefix if the bounded chain cannot close.
            int current=peak;
            for(int step=0;step<32;step++)
            {
                var lobe=Measure(raw,current,center,noise,fs,storedRaw);
                if(lobe.CanonicalPeak==current)return current;
                if(lobe.CanonicalPeak<0||lobe.CanonicalPeak>=raw.Length)return -1;
                current=lobe.CanonicalPeak;
            }
            return -1;
        }
        static double Correlation(double[] values,int a,int b,int half)
        {
            if(values==null||half<2||a-half<0||b-half<0||a+half>=values.Length||b+half>=values.Length)return double.NaN;
            // Scaled centered covariance avoids overflow for finite large inputs.
            double scale=0;for(int j=-half;j<=half;j++)
            {if(!Finite(values[a+j])||!Finite(values[b+j]))return double.NaN;scale=Math.Max(scale,Math.Max(Math.Abs(values[a+j]),Math.Abs(values[b+j])));}
            if(scale<=1e-20)return double.NaN;
            double sx=0,sy=0;int n=2*half+1;
            for(int j=-half;j<=half;j++){sx+=values[a+j]/scale;sy+=values[b+j]/scale;}
            double mx=sx/n,my=sy/n,xx=0,yy=0,xy=0;
            for(int j=-half;j<=half;j++){double x=values[a+j]/scale-mx,y=values[b+j]/scale-my;xx+=x*x;yy+=y*y;xy+=x*y;}
            if(xx<=1e-20||yy<=1e-20)return double.NaN;
            return Math.Max(-1,Math.Min(1,xy/Math.Sqrt(xx*yy)));
        }
        internal static SeventhProposalEvidence Evaluate(double[] raw,double[] band,int incident,int reflection,double center,double noise,
            double fs,double correlationFloor,double expectedRatio,double[] storedRaw=null)
        {
            var e=new SeventhProposalEvidence {Peak=reflection};
            if(raw==null||band==null||raw.Length!=band.Length||reflection<=incident||!Finite(correlationFloor)||correlationFloor<0||correlationFloor>1)
            {e.Reason="invalid_pair_input_or_correlation_floor";return e;}
            e.Incident=Measure(raw,incident,center,noise,fs,storedRaw);e.Reflection=Measure(raw,reflection,center,noise,fs,storedRaw);
            var a=e.Incident;var b=e.Reflection;
            if(!a.Complete||!b.Complete){e.Reason="incomplete_or_low_snr_main_lobe:"+(!a.Complete?a.Reason:b.Reason);return e;}
            if(a.Sign!=b.Sign){e.Reason="raw_main_lobe_polarity_mismatch";return e;}
            if(a.Right>=b.Left){e.Reason="overlapping_raw_main_lobes_not_separable";return e;}
            int delay=reflection-incident;
            int mainHalf=Math.Max(Math.Max(incident-a.Left,a.Right-incident),Math.Max(reflection-b.Left,b.Right-reflection));
            int room=Math.Min(Math.Min(incident,raw.Length-1-reflection),(delay-1)/2);
            if(room<mainHalf){e.Reason="complete_lobes_do_not_fit_independent_pair_context";return e;}
            int desiredCore=Math.Max(mainHalf,Math.Max(4,Math.Min(32,(int)Math.Ceiling(Math.Min(fs,1e10)*64e-9))));
            int desiredLong=Math.Max(desiredCore+4,Math.Max(2*mainHalf,Math.Min(96,(int)Math.Ceiling(Math.Min(fs,1e10)*192e-9))));
            e.CoreHalf=Math.Min(room,desiredCore);e.LongHalf=Math.Min(room,desiredLong);
            e.ShortContext=e.LongHalf<desiredLong;
            if(e.CoreHalf<3||e.LongHalf<e.CoreHalf+2)
            {e.Reason="short_separation_insufficient_multiscale_samples_manual_waveform_only";return e;}
            // Short/overlapping BAND tails are not blanket-rejected. Distinct raw
            // main lobes plus a measured valley and bounded independent contexts
            // can form a review, but never an automatic confirmation.
            if(e.ShortContext)
            {
                double valley=double.PositiveInfinity;
                for(int k=a.Right;k<=b.Left;k++){double x=a.Sign*(raw[k]-center);if(!Finite(x)){e.Reason="nonfinite_interlobe_valley";return e;}valley=Math.Min(valley,x);}
                if(valley>.25*Math.Min(a.Amplitude,b.Amplitude)){e.Reason="short_separation_no_measured_interlobe_valley";return e;}
            }
            e.CoreCorrelation=Correlation(band,incident,reflection,e.CoreHalf);
            e.LongCorrelation=Correlation(band,incident,reflection,e.LongHalf);
            e.RawCorrelation=Correlation(raw,incident,reflection,mainHalf);
            if(!Finite(e.CoreCorrelation)||!Finite(e.LongCorrelation)||!Finite(e.RawCorrelation))
            {e.Reason="nonfinite_multiscale_correlation";return e;}
            // Both scales must independently meet the ACTUAL requested threshold.
            e.Correlation=Math.Min(e.CoreCorrelation,e.LongCorrelation);
            if(e.Correlation<=0||e.Correlation+1e-12<correlationFloor)
            {
                // This is NOT a relaxed match: the candidate still fails the
                // requested signed-band gate. But two strongly inverted band
                // contexts with a strongly SAME-shaped, same-polarity raw main
                // lobe are conflicting evidence, not evidence that this raw
                // competitor does not exist. Filtering/neighbor overlap can
                // reverse the band context. Preserve the competing raw peak so
                // another candidate cannot win uniqueness merely by this veto.
                // Fixed strong-evidence floors prevent a requested floor of
                // zero from treating numerical negative noise as this conflict.
                double strongBand=Math.Max(.70,correlationFloor),strongRaw=Math.Max(.85,correlationFloor);
                e.FilterPolarityConflict=e.RawCorrelation>=strongRaw&&
                    e.CoreCorrelation<=-strongBand&&e.LongCorrelation<=-strongBand;
                e.Reason="multiscale_signed_correlation_below_requested_floor"+
                    (e.FilterPolarityConflict?";raw_band_strong_polarity_conflict_unresolved_filter_or_overlap":"");
                return e;
            }
            if(e.RawCorrelation<=0||e.RawCorrelation<Math.Max(.25,correlationFloor-.25))
            {e.Reason="raw_main_lobes_shape_inconsistent";return e;}
            if(!(a.QuietLeft||a.QuietRight)||!(b.QuietLeft||b.QuietRight))
            {e.Reason="no_independent_quiet_raw_flank_manual_waveform_only";return e;}
            e.AmplitudeRatio=b.Amplitude/a.Amplitude;
            if(!Finite(e.AmplitudeRatio)||e.AmplitudeRatio<=0){e.Reason="nonfinite_raw_amplitude_ratio";return e;}
            double strength=Math.Min(a.Snr,b.Snr)/(Math.Min(a.Snr,b.Snr)+6);
            double width=(double)(a.Right-a.Left)/(b.Right-b.Left);
            double widthScore=Math.Exp(-Math.Abs(Math.Log(width)));
            // Reliable CAL attenuation is a <=5% auxiliary score, never a veto.
            double attenuation=Finite(expectedRatio)&&expectedRatio>0&&expectedRatio<=1?
                Math.Exp(-Math.Abs(Math.Log(e.AmplitudeRatio/expectedRatio))):.5;
            e.Score=.75*e.Correlation+.10*Math.Max(0,e.RawCorrelation)+.05*widthScore+.05*strength+.05*attenuation;
            e.Passed=Finite(e.Score)&&e.Score>0&&e.Score<=1;
            e.Reason=e.Passed?(e.ShortContext?"separable_raw_lobes_bounded_short_context_review":"full_main_lobes_multiscale_echo_review"):"nonfinite_evidence_score";
            return e;
        }
        internal static SeventhProposalSearch Find(double[] raw,double[] band,int incident,int first,int last,double center,double noise,
            double fs,double floor,double expectedRatio,Func<int,bool> excluded,double[] storedRaw=null)
        {
            var r=new SeventhProposalSearch();
            if(raw==null||band==null||raw.Length!=band.Length||incident<=0||incident>=raw.Length-1||first>last||
                first<0||last>=raw.Length||last-first>131072||!Finite(center)||!Finite(noise)||noise<0||!Finite(fs)||fs<=0||!Finite(floor)||floor<0||floor>1)
            {r.Complete=false;r.Reason="invalid_or_unbounded_physical_search";return r;}
            var a=Measure(raw,incident,center,noise,fs,storedRaw);
            if(!a.Complete){r.Reason="incident_"+a.Reason;return r;}
            // Missing samples anywhere in the searched hard window are not silently
            // removed. An incomplete catalogue must not award a prefix winner.
            for(int k=first;k<=last;k++)if(!Finite(raw[k])||!Finite(band[k]))
            {r.Complete=false;r.Reason="nonfinite_physical_candidate_window";return r;}
            var distinctPackets=new HashSet<int>();
            for(int peak=Math.Max(incident+1,Math.Max(1,first));peak<=Math.Min(last,raw.Length-2);peak++)
            {
                double value=a.Sign*(raw[peak]-center);
                if(value<=1e-12||value<6*noise||value<=a.Sign*(raw[peak-1]-center)||value<a.Sign*(raw[peak+1]-center))continue;
                r.ObservedExtrema++;
                int canonical=CanonicalPeak(raw,peak,center,noise,fs,storedRaw);
                if(canonical<0)
                {r.Complete=false;r.Best=null;r.Second=null;r.Reason="canonical_main_lobe_resolution_budget_no_prefix_winner";return r;}
                // Canonical peaks outside the physical window cannot be moved
                // inside it by using one of their smaller in-window shoulders.
                if(canonical<=incident||canonical<first||canonical>last)continue;
                if(!distinctPackets.Add(canonical)){r.MergedExtrema++;continue;}
                if(excluded!=null&&excluded(canonical))continue;
                if(r.Evaluated>=CandidateBudget){r.Complete=false;r.Best=null;r.Second=null;r.Reason="candidate_budget_no_prefix_winner";return r;}
                var e=Evaluate(raw,band,incident,canonical,center,noise,fs,floor,expectedRatio,storedRaw);r.Evaluated++;r.Candidates.Add(e);
                if(!e.Passed)
                {
                    if(e.Reflection!=null&&e.Reflection.Amplitude>=6*noise&&(!e.Reflection.Complete||e.Reason.Contains("unresolved")))
                    {r.IncompleteCompetitors++;r.IncompleteCompetitorPeaks.Add(e.Peak);}
                    continue;
                }
                r.Qualified++;
                if(r.Best==null||e.Score>r.Best.Score+1e-12){r.Second=r.Best;r.Best=e;}
                else if(r.Second==null||e.Score>r.Second.Score+1e-12)r.Second=e;
            }
            if(r.Best!=null)
            {
                r.Ambiguous=r.IncompleteCompetitors>0||(r.Second!=null&&r.Best.Score-r.Second.Score<=.03&&Math.Abs(r.Best.Correlation-r.Second.Correlation)<=.025);
                r.Best.Ambiguous=r.Ambiguous;
                r.Reason=r.Ambiguous?"multiple_or_unmeasurable_competing_echoes_requires_manual_choice":r.Best.Reason;
            }
            return r;
        }
        internal static bool SharesMainLobe(double[] raw,int a,int b,double center,double noise,double fs)
        {
            if(a==b)return true;var x=Measure(raw,a,center,noise,fs);var y=Measure(raw,b,center,noise,fs);
            return x.Left>=0&&x.Right>=0&&y.Left>=0&&y.Right>=0&&Math.Max(x.Left,y.Left)<=Math.Min(x.Right,y.Right);
        }
    }
}
