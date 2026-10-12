using System;

namespace HVX.SignalEvidence
{
    // Endpoint projection uses the scoring boundary tolerance, without changing
    // sample timing or the independent accessory snapping radius.
    internal static class BoundaryConfidenceMath
    {
        internal const double TolerancePercent=2;
        internal static bool GeometryAvailable(double position,double length)
        {return ConfidenceMath.Finite(position)&&ConfidenceMath.Finite(length)&&length>0;}
        internal static bool WithinTolerance(double position,double length)
        {
            if(!GeometryAvailable(position,length))return false;
            double margin=length*(TolerancePercent/100);
            // Compare with interval endpoints so an exactly constructed boundary
            // is not rejected by cancellation in (position-length)/length.
            return position>=-margin&&position<=length+margin;
        }
        internal static double ExcessPercent(double position,double length)
        {
            if(!GeometryAvailable(position,length))return double.NaN;
            if(position>=0&&position<=length)return 0;
            return position<0?(-position/length)*100:((position-length)/length)*100;
        }
        internal static double ProjectEndpointPosition(double position,double length)
        {
            if(!WithinTolerance(position,length))return position;
            return position<0?0:position>length?length:position;
        }
    }
}
