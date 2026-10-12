using System;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        static bool IsSampleIndex(double value)
        {return IsFinite(value)&&value>=0&&value<=int.MaxValue&&value==Math.Truncate(value);}
        static bool IsLocalSampleIndex(double value)
        {return IsFinite(value)&&value>=int.MinValue&&value<=int.MaxValue&&value==Math.Truncate(value);}
        void SetUnselectedReflectionAnalysis()
        {
            currentLocationM=currentTimeDifferenceNs=correlation=confidence=compensatedCharge=double.NaN;
            confidenceStatusCode="ReflectionNotSelected";confidenceDetails="";
            samePositionPulseCount=0;
        }
        static void ClearUnselectedPairColumns(double[] row)
        {
            foreach(int column in new[]{11,12,13,16,17,21,22,33})if(column<row.Length)row[column]=double.NaN;
            if(row.Length>35)row[35]=0;
        }
    }
}
