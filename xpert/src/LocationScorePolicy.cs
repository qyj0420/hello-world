using System;

namespace HVX.LocationScoring
{
    // Review policy only. A score is neither a probability nor physical confirmation.
    internal sealed class LocationScorePolicy
    {
        internal const double DefaultSuspiciousMinimumPercent=30;
        internal const double DefaultManualAcceptanceMinimumPercent=45;
        internal const double DefaultAutomaticAcceptanceMinimumPercent=50;
        internal readonly double SuspiciousMinimumPercent,ManualAcceptanceMinimumPercent,AutomaticAcceptanceMinimumPercent;
        internal LocationScorePolicy(double suspiciousPercent,double manualPercent,double automaticPercent)
        {
            if(!IsValid(suspiciousPercent,manualPercent,automaticPercent))
                throw new ArgumentException("Location score limits must be finite and satisfy 0 <= suspicious <= manual <= automatic <= 100.");
            SuspiciousMinimumPercent=suspiciousPercent;ManualAcceptanceMinimumPercent=manualPercent;AutomaticAcceptanceMinimumPercent=automaticPercent;
        }
        internal static bool IsValid(double suspicious,double manual,double automatic)
        {return Finite(suspicious)&&Finite(manual)&&Finite(automatic)&&0<=suspicious&&suspicious<=manual&&manual<=automatic&&automatic<=100;}
        internal static LocationScorePolicy Defaults()
        {return new LocationScorePolicy(DefaultSuspiciousMinimumPercent,DefaultManualAcceptanceMinimumPercent,DefaultAutomaticAcceptanceMinimumPercent);}
        internal bool CanManuallyAccept(double score){return Meets(score,ManualAcceptanceMinimumPercent);}
        internal bool CanAutomaticallyAccept(double score){return Meets(score,AutomaticAcceptanceMinimumPercent);}
        internal int Classify(double score)
        {return !ValidScore(score)?-1:!Meets(score,SuspiciousMinimumPercent)?0:!Meets(score,AutomaticAcceptanceMinimumPercent)?1:2;}
        internal static bool Meets(double score,double percent)
        {return ValidScore(score)&&Finite(percent)&&percent>=0&&percent<=100&&(score==0?percent==0:score>=percent/100);}
        static bool ValidScore(double score){return Finite(score)&&score>=0&&score<=1;}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
