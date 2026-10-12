using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    /// <summary>
    /// Keeps application-owned windows inside the current monitor while preserving
    /// their normal desktop size. Content panels remain responsible for scrolling.
    /// </summary>
    internal static class ResponsiveFormLayout
    {
        private const int WorkingAreaInset = 12;

        internal static void Attach(Form form, Size logicalMinimumSize)
        {
            if (form == null) throw new ArgumentNullException("form");
            EventHandler fit = delegate { FitToWorkingArea(form, logicalMinimumSize); };
            form.Load += fit;
            form.Shown += fit;
            form.DpiChanged += delegate
            {
                if (!form.IsDisposed && form.IsHandleCreated)
                    form.BeginInvoke(new MethodInvoker(delegate { FitToWorkingArea(form, logicalMinimumSize); }));
            };
        }

        internal static void FitToWorkingArea(Form form, Size logicalMinimumSize)
        {
            Rectangle workingArea = Screen.FromControl(form).WorkingArea;
            FitToBounds(form, workingArea, form.DeviceDpi, logicalMinimumSize);
        }

        // Internal overload used by the deterministic DPI/layout smoke test.
        internal static void FitToBounds(Form form, Rectangle workingArea, int dpi, Size logicalMinimumSize)
        {
            if (form == null) throw new ArgumentNullException("form");
            if (workingArea.Width <= 0 || workingArea.Height <= 0) return;

            int availableWidth = Math.Max(320, workingArea.Width - WorkingAreaInset * 2);
            int availableHeight = Math.Max(240, workingArea.Height - WorkingAreaInset * 2);
            int effectiveDpi = Math.Max(96, dpi);
            int requestedMinimumWidth = Scale(logicalMinimumSize.Width, effectiveDpi);
            int requestedMinimumHeight = Scale(logicalMinimumSize.Height, effectiveDpi);
            form.MinimumSize = new Size(
                Math.Min(availableWidth, Math.Max(320, requestedMinimumWidth)),
                Math.Min(availableHeight, Math.Max(240, requestedMinimumHeight)));

            Size fitted = new Size(
                Math.Min(availableWidth, Math.Max(form.MinimumSize.Width, form.Width)),
                Math.Min(availableHeight, Math.Max(form.MinimumSize.Height, form.Height)));
            if (form.Size != fitted) form.Size = fitted;

            int left = Math.Max(workingArea.Left + WorkingAreaInset,
                Math.Min(form.Left, workingArea.Right - WorkingAreaInset - form.Width));
            int top = Math.Max(workingArea.Top + WorkingAreaInset,
                Math.Min(form.Top, workingArea.Bottom - WorkingAreaInset - form.Height));
            if (form.Location != new Point(left, top)) form.Location = new Point(left, top);
        }

        internal static void MaintainVerticalCanvas(ScrollableControl viewport, params Control[] content)
        {
            if (viewport == null || content == null) return;
            int requiredHeight = viewport.Padding.Vertical;
            foreach (Control control in content)
                if (control != null && control.Visible)
                    requiredHeight += control.Height + control.Margin.Vertical;
            Size minimum = new Size(0, Math.Max(0, requiredHeight));
            if (viewport.AutoScrollMinSize != minimum) viewport.AutoScrollMinSize = minimum;
        }

        private static int Scale(int logicalPixels, int dpi)
        {
            return (int)Math.Ceiling(logicalPixels * dpi / 96.0);
        }
    }
}
