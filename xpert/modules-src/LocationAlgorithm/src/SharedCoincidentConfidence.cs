using System;

namespace HVX.SignalEvidence
{
    internal sealed class CoincidentEvidence
    {
        // General localisation confidence is unavailable for a self match.
        // A separate engineering support index must never enter column 33.
        internal double Correlation=double.NaN, Score=double.NaN;
        internal bool Available=false;
        internal double FarEndSupport=double.NaN;
        internal bool FarEndSupportAvailable;
        internal string Reason="coincident_waveform_unavailable";
    }
    // A self match is not an independent echo. This deliberately review-only
    // engineering index never claims a calibrated probability or auto-acceptance.
    internal static class CoincidentConfidenceMath
    {
        internal const string Version="2026.09.22-coincident-review2";

        // This is a selection-state test, not proof of an actual reflection or
        // of a valid self correlation. Keep it independent of missing geometry.
        // At least two stored samples exclude an all-zero placeholder row.
        internal static bool IsCoincidentRow(double[,] matrix,int row)
        {
            if(matrix==null||matrix.GetLength(1)<36||row<0||row>=matrix.GetLength(0))return false;
            double a=matrix[row,10],b=matrix[row,11],start=matrix[row,31],end=matrix[row,32];
            return SampleIndex(a)&&SampleIndex(b)&&a==b&&matrix[row,12]==0&&
                SampleIndex(start)&&SampleIndex(end)&&start<end&&start<=a&&a<=end;
        }
        private static bool SampleIndex(double value)
        {return ConfidenceMath.Finite(value)&&value>=0&&value<=int.MaxValue&&value==Math.Floor(value);}
        internal static CoincidentEvidence Evaluate(bool selfValid,double nearSimilarity,double farSimilarity,
            double templateSimilarity,double widthSimilarity,int independentSameLocationCount)
        {
            var result=new CoincidentEvidence();
            if(!selfValid)return result;
            result.Correlation=1;
            if(!ConfidenceMath.Finite(nearSimilarity)||!ConfidenceMath.Finite(farSimilarity)||
                !ConfidenceMath.Finite(templateSimilarity)||!ConfidenceMath.Finite(widthSimilarity)||independentSameLocationCount<0)
            {result.Reason="coincident_far_end_evidence_unavailable";return result;}
            double contrast=ConfidenceMath.Unit(1-templateSimilarity);
            // Nearly identical CAL templates cannot discriminate the two ends.
            if(contrast<.05){result.Reason="coincident_calibration_end_templates_ambiguous";return result;}
            double preference=ConfidenceMath.Unit((farSimilarity-nearSimilarity)/contrast);
            double morphology=ConfidenceMath.Unit(farSimilarity)*preference*ConfidenceMath.Unit(widthSimilarity);
            // Repeated, independently accepted events only provide 5 points of
            // bounded support AFTER far-end morphology. They cannot manufacture it.
            double repeat=independentSameLocationCount/(independentSameLocationCount+3.0);
            result.FarEndSupport=morphology*(.44+.05*repeat);
            result.FarEndSupportAvailable=true;
            // Zero means no relative support for the far-end interpretation;
            // it does not mean zero localisation confidence or interference.
            // Even positive support cannot resolve an independent echo, so the
            // ordinary Score/Available pair intentionally stays NaN/false.
            result.Reason=result.FarEndSupport>0?"coincident_far_end_support_present_review_required":
                "coincident_far_end_support_absent_review_required";
            return result;
        }
    }
}
