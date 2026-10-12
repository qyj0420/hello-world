using System.ComponentModel;
namespace HVX.UnifiedSpectrum
{
    public sealed partial class UnifiedSpectrumControl
    {
        bool useCompensatedCharge;
        [Category("Appearance"),DefaultValue(false)] public bool UseCompensatedCharge
        {get{return useCompensatedCharge;}set{if(useCompensatedCharge==value)return;useCompensatedCharge=value;InvalidateSummaryStatistics();InvalidateAutomaticReviewRanges();ClearDataViews();Rebuild(false);}}
        double PresentedCharge(int row){return useCompensatedCharge&&Finite(source[row,16])?source[row,16]:source[row,15];}
    }
}
