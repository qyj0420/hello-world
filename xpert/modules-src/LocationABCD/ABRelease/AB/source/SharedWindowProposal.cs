using System;

namespace HVX.SignalEvidence
{
    // A bounded review proposal is a measured local extremum, never an accepted
    // discharge. It deliberately does not reuse strict packet eligibility.
    internal sealed class WindowProposal
    {
        internal int Peak=-1, Candidates;
        internal double Correlation, Score, AmplitudeRatio;
    }
    internal static class WindowProposalMath
    {
        internal static WindowProposal Find(double[] raw,double[] band,int incident,int first,int last,
            int halfWidth,double baseline,double noise,double expectedRatio,Func<int,bool> excluded)
        {
            var result=new WindowProposal();
            if(raw==null||band==null||raw.Length!=band.Length||incident<0||incident>=raw.Length||
                !Finite(baseline)||!Finite(noise)||noise<0)return result;
            int half=Math.Max(4,Math.Min(128,halfWidth));
            if(incident-half<0||incident+half>=raw.Length)return result;
            double amplitude=Math.Abs(raw[incident]-baseline);
            if(!Finite(amplitude)||amplitude<=1e-12)return result;
            int sign=Math.Sign(raw[incident]-baseline);
            first=Math.Max(first,incident+2*half+1);
            first=Math.Max(first,half);last=Math.Min(last,raw.Length-half-1);
            for(int peak=first;peak<=last;peak++)
            {
                if(excluded!=null&&excluded(peak))continue;
                double value=sign*(raw[peak]-baseline);
                if(!Finite(value)||value<=1e-12||value<=sign*(raw[peak-1]-baseline)||value<sign*(raw[peak+1]-baseline))continue;
                double rho=Correlation(band,incident,peak,half);
                if(!Finite(rho))continue;
                double ratio=value/amplitude;
                double strength=value/(value+Math.Max(1e-12,3*noise));
                double attenuation=Finite(expectedRatio)&&expectedRatio>0?Math.Exp(-Math.Abs(Math.Log(Math.Max(1e-12,ratio/expectedRatio)))):Math.Min(1,ratio);
                // Correlation is signed: an inverted packet cannot gain a high
                // score from the absolute value of its correlation.
                double score=(.70*Math.Max(0,rho)+.15*strength+.15*attenuation)*strength;
                result.Candidates++;
                if(result.Peak<0||score>result.Score+1e-12)
                {result.Peak=peak;result.Correlation=rho;result.Score=score;result.AmplitudeRatio=ratio;}
            }
            return result;
        }
        static double Correlation(double[] values,int a,int b,int half)
        {
            double x=0,y=0,xx=0,yy=0,xy=0;int count=2*half+1;
            for(int j=-half;j<=half;j++)
            {double u=values[a+j],v=values[b+j];if(!Finite(u)||!Finite(v))return double.NaN;x+=u;y+=v;xx+=u*u;yy+=v*v;xy+=u*v;}
            double vx=xx-x*x/count,vy=yy-y*y/count;
            if(vx<=1e-20||vy<=1e-20)return double.NaN;
            return Math.Max(-1,Math.Min(1,(xy-x*y/count)/Math.Sqrt(vx*vy)));
        }
        static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
    }
}
