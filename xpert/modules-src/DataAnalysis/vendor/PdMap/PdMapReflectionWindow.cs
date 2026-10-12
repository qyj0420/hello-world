using System;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // The positioning inverse includes the current DUT/segment geometry.
        // Never use CAL total time here: its connecting lead is already removed.
        bool TryReflectionSelectionBounds(out int minimum,out int maximum)
        {
            minimum=maximum=-1;
            if(pulse.Length==0||!IsFinite(cableLengthM)||cableLengthM<=0||
                !IsFinite(dtNs)||dtNs<=0||!IsFinite(acceptedPositionErrorPercent)||acceptedPositionErrorPercent<0)return false;
            if(matrixDriven&&(originalParameterRow.Length<8||!IsFinite(originalParameterRow[7])||originalParameterRow[7]<=0||
                !IsFinite(originalParameterRow[5])||originalParameterRow[5]<=0))return false;
            if(!MixedCableCalculation&&!velocityConfigured)return false;
            double error=cableLengthM*Math.Max(acceptedPositionErrorPercent,HVX.SignalEvidence.BoundaryConfidenceMath.TolerancePercent)/100.0,first,last;
            if(!IsFinite(error)||!TrySampleDeltaForPosition(mirrorLocation?-error:cableLengthM+error,out first)||
                !TrySampleDeltaForPosition(mirrorLocation?cableLengthM+error:-error,out last)||first>last)return false;
            // Round inward: no extra sample outside the configured physical window.
            double lower=Math.Max(0,Math.Ceiling(incident+first)),upper=Math.Min(pulse.Length-1.0,Math.Floor(incident+last));
            if(!IsFinite(lower)||!IsFinite(upper)||lower>upper)return false;
            minimum=(int)lower;maximum=(int)upper;return true;
        }

        int ConstrainReflectionSelection(int target)
        {
            int first,last;
            if(!TryReflectionSelectionBounds(out first,out last))
            {
                confidence=double.NaN;confidenceStatusCode="GeometryUnavailable";
                LastError=L("Reflection window unavailable: check cable length, velocity and sampling rate.","反射波选取范围不可用，请核对电缆长度、波速和采样率。");
                return -1;
            }
            if(target<first||target>last)
            {
                LastError=L("Reflection is outside the cable window plus tolerance.","反射波超出电缆窗及允许误差范围。");
                return -1;
            }
            LastError="";return target;
        }

        // Historical or geometrically stale picks are NOT moved to an invented
        // edge peak. Retain the original row; the current selection is unresolved.
        void ValidateReflectionSelectionWindow()
        {
            if(!reflectionSelected)return;
            int first,last;
            bool available=TryReflectionSelectionBounds(out first,out last);
            if(available&&reflection>=first&&reflection<=last)return;
            reflectionSelected=false;ClearPositionApproximation();SetUnselectedReflectionAnalysis();
            confidenceStatusCode=available?"ReflectionOutsideSelectionWindow":"GeometryUnavailable";
        }
    }
}
