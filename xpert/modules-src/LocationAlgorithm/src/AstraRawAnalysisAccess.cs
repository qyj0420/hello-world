namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Raw output remains untouched. Timing, charge and discovery read the same
    // high-bandwidth baseline-corrected signal, not the display-filtered view.
    private static double[] A4RawAnalysis(A4Context c)
    {return c.MeasurementSignal==null?c.Raw:c.MeasurementSignal.DetectionRaw;}

    private static double A4RawBaselineAt(A4Context c,int index)
    {return c.MeasurementSignal==null?c.RawCenter:c.MeasurementSignal.Trend[index]+c.RawCenter;}
}
