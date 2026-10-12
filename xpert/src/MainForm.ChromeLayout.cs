using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        int chromeLayoutDepth;
        bool chromeLayoutRequested;
        bool chromeLayoutQueued;
        bool chromeRestorePending;
        string lastChromeMetricsKey;
        internal int ResponsiveChromeBuildCount { get; private set; }

        string ChromeMetricsKey()
        {
            var key=new StringBuilder();
            key.Append(ClientSize).Append('|').Append(DeviceDpi).Append('|').Append(activeModule!=null).Append('|')
                .Append(compactDisplayChrome).Append('|').Append(settings.LanguageCode).Append('|')
                .Append(headerPanel.Width).Append(':').Append(headerPanel.Height).Append('|')
                .Append(commandBarPanel.Width).Append(':').Append(commandBarPanel.Height).Append('|').Append(modulePanel.Width);
            // WinForms DPI scaling can mutate absolute row heights after a
            // resize callback. Do not reuse a pre-scale shell cache for them.
            if(rootLayout!=null)foreach(RowStyle row in rootLayout.RowStyles)
                if(row.SizeType!=SizeType.Percent)key.Append('|').Append(row.SizeType).Append(':').Append(row.Height);
            if(rootLayout!=null)foreach(int height in rootLayout.GetRowHeights())
                key.Append('|').Append(height);
            foreach(Control c in new Control[]{productTitle,productSubtitle,settingsButton,helpButton,backButton,openProjectButton,
                refreshDeviceButton,projectStatus,deviceStatus,projectDetail,deviceDetail,moduleTitle,moduleContext,releaseProjectButton,disconnectDeviceButton})
                if(c!=null)key.Append('|').Append(c.Text).Append('|').Append(c.Visible).Append('|')
                    .Append(c.Font.Name).Append(':').Append(c.Font.SizeInPoints).Append(':').Append(c.Font.Style);
            foreach(var tile in tiles)key.Append('|').Append(tile.Key).Append(':').Append(tile.Value.Visible);
            return key.ToString();
        }

        private void RequestResponsiveChrome()
        {
            if (chromeLayoutDepth > 0) { chromeLayoutRequested = true; return; }
            ApplyResponsiveChrome();
        }

        private bool ChromeAbsoluteRowsUnsettled()
        {
            if (rootLayout == null) return false;
            int[] heights = rootLayout.GetRowHeights();
            for (int row = 0; row < heights.Length && row < rootLayout.RowStyles.Count; row++)
                if (rootLayout.RowStyles[row].SizeType == SizeType.Absolute &&
                    Math.Abs(heights[row] - rootLayout.RowStyles[row].Height) > 1) return true;
            return false;
        }

        private void QueueResponsiveChrome()
        {
            if (IsDisposed || Disposing || !IsHandleCreated || chromeLayoutQueued) return;
            // A TableLayoutPanel Layout notification is raised from inside its
            // layout engine. Measure after that pass, never recursively mutate
            // its row styles while the engine still holds cached cell heights.
            chromeLayoutQueued = true;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    chromeLayoutQueued = false;
                    if (!IsDisposed && !Disposing && IsHandleCreated) RequestResponsiveChrome();
                }));
            }
            catch (InvalidOperationException) { chromeLayoutQueued = false; }
        }

        private IDisposable BeginChromeLayout()
        {
            chromeLayoutDepth++;
            if (chromeLayoutDepth > 1) return new ChromeLayoutLease(this, null);
            // Parent-first resume is intentional: each child receives its final
            // bounds while still suspended, then performs just its final layout.
            // Keep module content outside this list; its own layout still runs.
            var controls = new List<Control>();
            foreach (Control control in new Control[] { this, rootLayout, headerPanel,
                titleArea, headerActions, commandBarPanel, commandActions, commandStatuses,
                contentPanel, modulePanel, modulePageHeader, homePanel, welcomePanel, tilePanel })
                if (control != null && !control.IsDisposed) { control.SuspendLayout(); controls.Add(control); }
            return new ChromeLayoutLease(this, controls);
        }

        private void EndChromeLayout(List<Control> controls)
        {
            Exception failure = null;
            try
            {
                if (controls != null)
                    foreach (Control control in controls)
                        if (!control.IsDisposed)
                            try { control.ResumeLayout(true); }
                            catch (Exception error) { if (failure == null) failure = error; }
            }
            finally { chromeLayoutDepth--; }
            // A failed Layout callback must not suspend the remaining shell.
            if (failure != null) throw new InvalidOperationException("Workspace layout failed.", failure);
            if (chromeLayoutDepth == 0 && chromeLayoutRequested)
            {
                // A nested resize during the current pass still needs its final
                // viewport measured. The outer pass drains it after its guard clears.
                if (!responsiveLayoutUpdating && !IsDisposed)
                { chromeLayoutRequested = false; ApplyResponsiveChrome(); }
            }
        }

        private sealed class ChromeLayoutLease : IDisposable
        {
            MainForm owner;
            readonly List<Control> controls;
            internal ChromeLayoutLease(MainForm form, List<Control> suspended) { owner = form; controls = suspended; }
            public void Dispose() { var form = owner; owner = null; if (form != null) form.EndChromeLayout(controls); }
        }
    }
}
