namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // Standalone charts retain their help; the unified host provides one
        // context-sensitive status bar instead of duplicating help in each plot.
        internal bool UseHostStatusBar {get;set;}
    }
}
