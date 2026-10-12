using System;

namespace Xiongxin.Calibration
{
    // A setup recommendation only; a true result is never a hardware receipt.
    internal static class AutomaticShortCablePolicy
    {
        internal static bool ShouldEnable(double lengthM,double velocityMPerUs,
            double incidentPeakV,double reflectionPeakV,bool capacitiveLoad,
            out double roundTripUs,out double reflectionRatio)
        {
            roundTripUs=reflectionRatio=Double.NaN;
            if(capacitiveLoad||!Positive(lengthM)||!Positive(velocityMPerUs)||
                !Positive(incidentPeakV)||!Positive(reflectionPeakV))return false;
            roundTripUs=2*lengthM/velocityMPerUs;
            reflectionRatio=reflectionPeakV/incidentPeakV;
            return Positive(roundTripUs)&&roundTripUs<=2.0&&Positive(reflectionRatio)&&reflectionRatio>.9;
        }
        static bool Positive(double value){return value>0&&!Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
