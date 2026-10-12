using System;
using System.Globalization;

namespace HVX.Task17.Location
{
    // Advisory descriptors; the tested hard gate over-rejected field signals.
    // Never silently erase candidates. This does not solve indistinguishability.
    internal sealed class EighthPacketSpectrum
    {
        internal bool Valid;
        internal double RmsWidthSeconds, F50Hz, F90Hz, HighBandFraction, BinHz;
        internal string Reason="not_measured";
    }
    internal static class EighthSourceIdentity
    {
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        internal static EighthPacketSpectrum Measure(EighthPulse p)
        {
            var r=new EighthPacketSpectrum();
            if(p==null||!p.Valid||p.EdgeClipped||p.FlatTop||p.Samples==null||p.Samples.Length<4||p.Samples.Length>65536||!Finite(p.SampleRateHz)||p.SampleRateHz<=0||!Finite(p.AbsAmplitude)||p.AbsAmplitude<=0)
            {r.Reason="invalid_or_incomplete_full_packet";return r;}
            int n=1,lg=0;while(n<p.Samples.Length){n*=2;lg++;}
            EighthKernelResources.Work(30L*n*lg+30L*n,"source_identity_full_packet_fft");
            if(EighthKernelResources.Current!=null)EighthKernelResources.Current.Bytes(64+16L*n,"source_identity_fft_buffers");
            var re=new double[n];var im=new double[n];double energy=0,mean=0;
            for(int i=0;i<p.Samples.Length;i++)
            {
                double y=p.Samples[i]/p.AbsAmplitude;
                if(!Finite(y)){r.Reason="nonfinite_packet";return r;}
                re[i]=y;double e=y*y;energy+=e;mean+=i*e;
            }
            if(!Finite(energy)||energy<=0){r.Reason="zero_or_nonfinite_energy";return r;}
            mean/=energy;double variance=0;
            for(int i=0;i<p.Samples.Length;i++){double d=i-mean;variance+=d*d*re[i]*re[i];}
            r.RmsWidthSeconds=Math.Sqrt(variance/energy)/p.SampleRateHz;
            // Complete support, zero padding; no stretching/resampling. DC is
            // retained and one-sided interior bins carry twice the energy.
            for(int i=1,j=0;i<n;i++){int bit=n>>1;for(; (j&bit)!=0;bit>>=1)j^=bit;j^=bit;if(i<j){double t=re[i];re[i]=re[j];re[j]=t;}}
            for(int len=2;len<=n;len*=2)
            {
                double angle=-2*Math.PI/len,cr=Math.Cos(angle),ci=Math.Sin(angle);
                for(int start=0;start<n;start+=len)
                {
                    double wr=1,wi=0;
                    for(int j=0;j<len/2;j++)
                    {
                        int a=start+j,b=a+len/2;double vr=re[b]*wr-im[b]*wi,vi=re[b]*wi+im[b]*wr;
                        re[b]=re[a]-vr;im[b]=im[a]-vi;re[a]+=vr;im[a]+=vi;
                        double next=wr*cr-wi*ci;wi=wr*ci+wi*cr;wr=next;
                    }
                }
            }
            double total=0,high=0;
            for(int k=0;k<=n/2;k++){double e=(re[k]*re[k]+im[k]*im[k])*(k==0||k==n/2?1:2);re[k]=e;total+=e;if(k>n/8)high+=e;}
            if(!Finite(total)||total<=0){r.Reason="invalid_spectral_energy";return r;}
            double sum=0;int q50=-1,q90=-1;
            for(int k=0;k<=n/2;k++){sum+=re[k];if(q50<0&&sum>=.50*total)q50=k;if(q90<0&&sum>=.90*total)q90=k;}
            r.BinHz=p.SampleRateHz/n;r.F50Hz=Math.Max(0,q50)*r.BinHz;r.F90Hz=Math.Max(0,q90)*r.BinHz;
            r.HighBandFraction=high/total;r.Valid=true;r.Reason="full_support_energy_rms_and_one_sided_spectrum_not_transfer_proof";
            return r;
        }
        static EighthPacketSpectrum Features(EighthPulse p)
        {if(p==null)return new EighthPacketSpectrum();if(p.Spectrum==null)p.Spectrum=Measure(p);return p.Spectrum;}
        internal static void Assess(EighthPair pair,EighthCalibration cal,bool observedTwoBranchFamily)
        {
            if(pair==null)return;
            var a=Features(pair.Incident);var b=Features(pair.Reflection);
            pair.SourceIdentityReason="single_pair_independent_discharge_competition_unresolved";
            pair.SourceIdentitySupported=false;
            if(!a.Valid||!b.Valid)pair.SourceIdentityReason="source_identity_packet_features_unavailable";
            else if(observedTwoBranchFamily)
            {
                pair.SourceIdentitySupported=true;
                pair.SourceIdentityReason="observed_two_branch_family_conditional_support_not_physical_proof";
            }
            else if(cal!=null&&cal.TemplateReliable)
            {
                var ca=Features(cal.Incident);var cb=Features(cal.Reflection);
                if(ca.Valid&&cb.Valid)
                {
                    double ai=Math.Abs(EighthPulseMath.ShapeCorrelation(pair.Incident,cal.Incident));
                    double ar=Math.Abs(EighthPulseMath.ShapeCorrelation(pair.Incident,cal.Reflection));
                    double bi=Math.Abs(EighthPulseMath.ShapeCorrelation(pair.Reflection,cal.Incident));
                    double br=Math.Abs(EighthPulseMath.ShapeCorrelation(pair.Reflection,cal.Reflection));
                    // Independent CAL must distinguish roles. Unchanged width
                    // alone must NOT veto a witnessed nondispersive echo chain.
                    bool roleContrast=ai>=.85&&br>=.85&&ai-ar>.10&&br-bi>.10;
                    double dw=b.RmsWidthSeconds-a.RmsWidthSeconds,dcw=cb.RmsWidthSeconds-ca.RmsWidthSeconds;
                    bool widthDistinct=Math.Abs(dw)>2/Math.Min(pair.Incident.SampleRateHz,pair.Reflection.SampleRateHz)&&Math.Abs(dcw)>2/cal.SampleRateHz&&Math.Sign(dw)==Math.Sign(dcw);
                    double df=b.F90Hz-a.F90Hz,dcf=cb.F90Hz-ca.F90Hz;
                    bool bandDistinct=Math.Abs(df)>2*Math.Max(a.BinHz,b.BinHz)&&Math.Abs(dcf)>2*Math.Max(ca.BinHz,cb.BinHz)&&Math.Sign(df)==Math.Sign(dcf);
                    if(roleContrast&&(widthDistinct||bandDistinct))
                    {pair.SourceIdentitySupported=true;pair.SourceIdentityReason="distinct_CAL_roles_and_measured_transfer_direction_conditional_support";}
                }
            }
            // Accepted remains the legacy engineering-candidate selection, NOT
            // independent confirmation of the physical source identity.
            if(pair.Accepted&&!pair.SourceIdentitySupported)
                pair.Reason+=(pair.Reason.Length==0?"":";")+"source_identity_review_advisory:"+pair.SourceIdentityReason;
        }
        static string N(double v){return Finite(v)?v.ToString("R",CultureInfo.InvariantCulture):"null";}
        internal static string Json(EighthPulse p)
        {
            var f=p==null?null:p.Spectrum;if(f==null)return "null";
            return "{\"valid\":"+(f.Valid?"true":"false")+",\"energy_rms_width_s\":"+N(f.RmsWidthSeconds)+",\"f50_hz\":"+N(f.F50Hz)+",\"f90_hz\":"+N(f.F90Hz)+",\"high_band_above_fs_over_8_fraction\":"+N(f.HighBandFraction)+",\"frequency_bin_hz\":"+N(f.BinHz)+",\"reason\":\""+f.Reason+"\"}";
        }
    }
}
