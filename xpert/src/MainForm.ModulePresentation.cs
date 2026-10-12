using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private Panel moduleLoadingCover;
        private int modulePresentationDepth;

        // UI-thread-only staging. No transparency, message pumping, sleeps or hardware changes.
        // Nested navigation uses one cover, and exceptions always release it.
        private IDisposable BeginModulePresentation()
        {
            modulePresentationDepth++;
            if (modulePresentationDepth == 1)
            {
                moduleLoadingCover = new PresentationPanel { Dock = DockStyle.Fill, BackColor = AppTheme.Canvas };
                moduleLoadingCover.Controls.Add(new Label {
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = AppTheme.Accent, BackColor = AppTheme.Canvas,
                    Text = IsChinese ? Localize("正在准备页面，请稍候…") : "Preparing workspace…"
                });
                contentPanel.Controls.Add(moduleLoadingCover);
                moduleLoadingCover.BringToFront();
                // Paint only this control; unlike DoEvents this cannot re-enter navigation.
                moduleLoadingCover.Update();
            }
            return new PresentationLease(this);
        }

        private void EndModulePresentation()
        {
            if (--modulePresentationDepth != 0) return;
            Panel cover = moduleLoadingCover;
            moduleLoadingCover = null;
            if (cover != null) { contentPanel.Controls.Remove(cover); cover.Dispose(); }
            if (!contentPanel.IsDisposed) contentPanel.Invalidate(true);
        }

        private sealed class PresentationLease : IDisposable
        {
            private MainForm owner;
            internal PresentationLease(MainForm form) { owner = form; }
            public void Dispose() { MainForm form = owner; owner = null; if (form != null) form.EndModulePresentation(); }
        }

        private sealed class PresentationPanel : Panel
        {
            internal PresentationPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}
