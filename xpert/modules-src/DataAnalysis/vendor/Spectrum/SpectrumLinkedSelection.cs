using System;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    /// <summary>
    /// One shared row-identity selection/filter for linked dual-pane spectra.
    /// Both panes and both context menus bind to this model; coordinates are not shared.
    /// </summary>
    public sealed class SpectrumLinkedSelection
    {
        int[] rows;
        public bool Active { get { return rows != null && rows.Length > 0; } }
        public int[] Rows { get { return rows == null ? new int[0] : (int[])rows.Clone(); } }
        public event EventHandler Changed;
        public void Set(int[] next)
        {
            int[] normalized = next == null || next.Length == 0 ? null : next.Distinct().OrderBy(r => r).ToArray();
            if (rows == null && normalized == null) return;
            if (rows != null && normalized != null && rows.SequenceEqual(normalized)) return;
            rows = normalized;
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        public void Clear() { Set(null); }
    }
}
