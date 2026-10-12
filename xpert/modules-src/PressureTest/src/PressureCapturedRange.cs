using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // The selectable range may change while analysis runs. Export only the
        // settings owned by the installed capture for this acquisition identity.
        [Browsable(false)] public int CapturedPicoRangeIndex
        {
            get
            {
                var result=lastPressureCapture;
                if(result==null||result.Capture==null||result.Capture.Settings==null||
                    String.IsNullOrEmpty(lastPressureAcquisitionId)||
                    !String.Equals(lastPressureAcquisitionId,LocationAcquisitionId,StringComparison.Ordinal))return -1;
                int index=result.Capture.Settings.RangeIndex;
                return index>=0&&index<=10?index:-1;
            }
        }
    }
}
