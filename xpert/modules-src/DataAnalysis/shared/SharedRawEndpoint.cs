using System;
namespace HVX.SignalEvidence
{
    // Immutable, request-local manufacturer policy. Operators are evaluated
    // left to right: (split OP1 width) OP2 ratio. No process-global settings.
    internal sealed class RawEndpointPolicy
    {
        internal readonly double WidthMultiple,RatioDivisor;
        internal readonly bool FirstAnd,SecondAnd;
        internal readonly string Serialized;
        private RawEndpointPolicy(double width,double ratio,bool first,bool second)
        {
            WidthMultiple=width;RatioDivisor=ratio;FirstAnd=first;SecondAnd=second;
            Serialized=width.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+";"+
                ratio.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+";"+(first?"AND":"OR")+";"+(second?"AND":"OR");
        }
        internal static RawEndpointPolicy Parse(string value)
        {
            if(String.IsNullOrEmpty(value))return null; // Historical standalone callers.
            string[] fields=value.Split(';');double width,ratio;
            if(fields.Length!=4||
                !double.TryParse(fields[0],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out width)||
                !double.TryParse(fields[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out ratio)||
                !ConfidenceMath.Finite(width)||width<1||width>100||!ConfidenceMath.Finite(ratio)||ratio<1||ratio>100||
                (fields[2]!="AND"&&fields[2]!="OR")||(fields[3]!="AND"&&fields[3]!="OR"))
                throw new ArgumentException("Invalid manufacturer endpoint policy.","value");
            return new RawEndpointPolicy(width,ratio,fields[2]=="AND",fields[3]=="AND");
        }
    }
    internal sealed class RawEndpointShape
    {
        internal bool Valid,Split;
        internal double Main=double.NaN,Opposite=double.NaN,FwhmNs=double.NaN;
        internal double Ratio { get { return Opposite>0?Main/Opposite:double.NaN; } }
    }
    internal sealed class RawEndpointDecision
    {
        internal string Direction="Indeterminate",Reason="raw_endpoint_evidence_missing";
        internal double FarScore=double.NaN;
    }
    // Unfiltered baseline residual only. Never pass filtered samples here.
    internal static class RawEndpointMath
    {
        internal const string Version="2026.09.22-raw-endpoint-ratio-div1p5-width2-split1";
        static bool P(double x){return ConfidenceMath.Finite(x)&&x>0;}
        internal static RawEndpointShape Measure(double[] y,double fs,double noise)
        {
            var r=new RawEndpointShape();
            if(y==null||y.Length<5||y.Length>65536||!P(fs)||!ConfidenceMath.Finite(noise)||noise<0)return r;
            int k=0;double maximum=0;
            for(int i=0;i<y.Length;i++){if(!ConfidenceMath.Finite(y[i]))return r;if(Math.Abs(y[i])>maximum){maximum=Math.Abs(y[i]);k=i;}}
            // Ratio ONLY keeps temporal identity. Width/split continue to use
            // the original dominant peak k, never the ratio's earlier lobe.
            // Ignore small precursor noise (<50% packet maximum or <6 sigma).
            double principalFloor=Math.Max(.5*maximum,6*noise);int ratioPeak=k;
            for(int i=0;i<y.Length;i++)
            {
                if(Math.Abs(y[i])<principalFloor)continue;
                int polarity=y[i]>0?1:-1,j=i,best=i;
                while(j+1<y.Length&&polarity*y[j+1]>0){j++;if(polarity*y[j]>polarity*y[best])best=j;}
                ratioPeak=best;break;
            }
            if(k==0||k==y.Length-1||maximum<=0||maximum<6*noise)return r;
            int sign=y[k]>0?1:-1,ratioSign=y[ratioPeak]>0?1:-1;r.Main=Math.Abs(y[ratioPeak]);r.Opposite=0;
            for(int i=ratioPeak+1;i<y.Length;i++)r.Opposite=Math.Max(r.Opposite,-ratioSign*y[i]);
            if(r.Opposite<4.5*noise)r.Opposite=double.NaN;
            double half=maximum/2;int left=k,right=k;
            while(left>0&&sign*y[left-1]>=half)left--;
            while(right<y.Length-1&&sign*y[right+1]>=half)right++;
            if(left>0&&right<y.Length-1)
            {
                double l=left-1+(half-sign*y[left-1])/(sign*(y[left]-y[left-1]));
                double h=right+(sign*y[right]-half)/(sign*(y[right]-y[right+1]));
                if(h>l)r.FwhmNs=(h-l)/fs*1e9;
            }
            // Only split peaks in the connected principal lobe, not later
            // opposite lobes/ringing or independent pulses elsewhere in a crop.
            int a=k,b=k;while(a>0&&sign*y[a-1]>0)a--;while(b<y.Length-1&&sign*y[b+1]>0)b++;
            double depth=Math.Max(.20*maximum,6*noise);
            for(int i=a+1;i<b;i++)
            {
                if(Math.Abs(i-k)<2||sign*y[i]<half||sign*y[i]<sign*y[i-1]||sign*y[i]<=sign*y[i+1])continue;
                double valley=double.PositiveInfinity;
                for(int j=Math.Min(i,k);j<=Math.Max(i,k);j++)valley=Math.Min(valley,sign*y[j]);
                if(Math.Min(maximum,sign*y[i])-valley>=depth){r.Split=true;break;}
            }
            r.Valid=true;return r;
        }
        internal static RawEndpointDecision Decide(RawEndpointShape incident,RawEndpointShape reflection,RawEndpointShape calibration)
        {return Decide(incident,reflection,calibration,null);}
        internal static RawEndpointDecision Decide(RawEndpointShape incident,RawEndpointShape reflection,RawEndpointShape calibration,RawEndpointPolicy policy)
        {
            var r=new RawEndpointDecision();
            if(policy!=null)
            {
                r.Reason="manufacturer_endpoint_policy:"+policy.Serialized+";evidence_missing";
                if(incident==null||reflection==null||calibration==null||!incident.Valid||!reflection.Valid||!calibration.Valid||
                    !P(incident.FwhmNs)||!P(calibration.FwhmNs)||!P(incident.Ratio)||!P(calibration.Ratio))return r;
                bool width=incident.FwhmNs/calibration.FwhmNs>=policy.WidthMultiple;
                bool ratio=incident.Ratio<=calibration.Ratio/policy.RatioDivisor;
                bool combined=policy.FirstAnd?(reflection.Split&&width):(reflection.Split||width);
                combined=policy.SecondAnd?(combined&&ratio):(combined||ratio);
                double widthScore=ConfidenceMath.Unit(incident.FwhmNs/calibration.FwhmNs-1);
                double ratioScore=ConfidenceMath.Unit((1-incident.Ratio/calibration.Ratio)*3);
                r.FarScore=combined?1:.5*widthScore+.5*ratioScore;
                // Failing a strong condition is NOT evidence for the other end.
                // Retain the independent near-evidence check, never the old
                // score >= .70 far fallback that would bypass configured AND.
                r.Direction=combined?"Far":!reflection.Split&&r.FarScore<=.30?"Near":"Indeterminate";
                r.Reason="manufacturer_endpoint_policy:"+policy.Serialized+";"+(combined?"matched":r.Direction=="Near"?"near_shape_evidence":"conditions_not_met");
                return r;
            }
            if(reflection!=null&&reflection.Valid&&reflection.Split)
            {r.Direction="Far";r.FarScore=1;r.Reason="raw_reflection_principal_lobe_split";return r;}
            if(incident==null||calibration==null||!incident.Valid||!calibration.Valid)return r;
            bool widths=P(incident.FwhmNs)&&P(calibration.FwhmNs),ratios=P(incident.Ratio)&&P(calibration.Ratio);
            if(widths&&incident.FwhmNs>=2*calibration.FwhmNs)
            {r.Direction="Far";r.FarScore=1;r.Reason="raw_incident_fwhm_at_least_twice_calibration";return r;}
            if(ratios&&incident.Ratio<=calibration.Ratio/1.5)
            {r.Direction="Far";r.FarScore=1;r.Reason="raw_incident_ratio_at_most_calibration_divided_by_1p5";return r;}
            if(!widths||!ratios||reflection==null||!reflection.Valid)return r;
            // Engineering score, not probability. Neutral evidence stays pending.
            double w=ConfidenceMath.Unit(incident.FwhmNs/calibration.FwhmNs-1);
            double q=ConfidenceMath.Unit((1-incident.Ratio/calibration.Ratio)*3);
            r.FarScore=.5*w+.5*q;
            r.Direction=r.FarScore>=.70?"Far":r.FarScore<=.30?"Near":"Indeterminate";
            r.Reason="raw_width_ratio_equal_weight_score_30_70_engineering_policy";return r;
        }
    }
}
