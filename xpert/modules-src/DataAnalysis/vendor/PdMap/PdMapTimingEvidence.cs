using System;

namespace LabVIEWPdMap
{
    // Control-only input semantics. Do not alter the frozen Task17 evidence ABI.
    internal static class PdMapTimingEvidence
    {
        internal static bool IsNegativeDelayRow(double[,] matrix,int row)
        {
            if(matrix==null||matrix.GetLength(1)<36||row<0||row>=matrix.GetLength(0))return false;
            double incident=matrix[row,10],reflection=matrix[row,11],delay=matrix[row,12];
            return Index(incident)&&Index(reflection)&&reflection<incident&&delay==reflection-incident;
        }
        static bool Index(double value)
        {return !double.IsNaN(value)&&!double.IsInfinity(value)&&value>=0&&value<=int.MaxValue&&value==Math.Floor(value);}
    }
}
