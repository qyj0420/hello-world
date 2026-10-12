using System.Drawing;

namespace HVX.Controls
{
    // Shared instrument palette. Keep semantic colors here so the host, plots and dialogs agree.
    internal static class BrowserTheme
    {
        internal static readonly Color Navy = Color.FromArgb(23, 59, 86);
        internal static readonly Color Header = Color.FromArgb(234, 242, 248);
        internal static readonly Color Primary = Color.FromArgb(0, 163, 199);
        internal static readonly Color PrimaryHover = Color.FromArgb(0, 120, 156);
        internal static readonly Color Cyan = Color.FromArgb(0, 120, 156);
        internal static readonly Color Canvas = Color.FromArgb(237, 242, 246);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color SurfaceAlt = Color.FromArgb(245, 248, 251);
        internal static readonly Color Border = Color.FromArgb(216, 226, 234);
        internal static readonly Color Grid = Color.FromArgb(233, 239, 244);
        internal static readonly Color Ink = Color.FromArgb(35, 53, 72);
        internal static readonly Color Muted = Color.FromArgb(97, 116, 136);
        internal static readonly Color Selection = Color.FromArgb(0, 163, 199);
        internal static readonly Color Disabled = Color.FromArgb(227, 233, 239);
        internal static readonly Color Error = Color.FromArgb(183, 81, 75);
        // Raw PD/noise and LOC waveforms share a neutral dark green. A trace
        // colour is not a discharge classification or an alarm indication.
        internal static readonly Color Pd = Color.FromArgb(44, 133, 110);
        internal static readonly Color Voltage = Color.FromArgb(0, 120, 156);
        internal static readonly Color PhaseA = Color.FromArgb(217, 162, 37);
        internal static readonly Color PhaseB = Color.FromArgb(31, 120, 102);
        internal static readonly Color PhaseC = Color.FromArgb(175, 68, 59);
    }
}
