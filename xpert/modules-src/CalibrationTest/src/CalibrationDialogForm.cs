using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.Controls
{
    // Own only the cloned resources, never the parent form's icon or font.
    internal class CalibrationDialogForm : Form
    {
        readonly Icon ownedIcon;
        readonly Font ownedFont;
        internal CalibrationDialogForm(string name, string title, Font font, Size size)
        {
            Name = name; Text = title; AutoScaleMode = AutoScaleMode.None;
            ownedFont = font == null ? new Font("Microsoft YaHei UI", 9.5f) : (Font)font.Clone();
            Font = ownedFont; ownedIcon = CalibrationBranding.LoadIcon(); if (ownedIcon != null) Icon = ownedIcon;
            BackColor = Color.White; ForeColor = Color.FromArgb(24, 51, 77);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false; Size = size; MinimumSize = new Size(340, 240);
        }
        internal void SetContent(Control content, params Button[] buttons)
        {
            Controls.Add(new CalibrationDialogShell(content, buttons) { Dock = DockStyle.Fill });
        }
        internal void FitWorkingArea(Rectangle area)
        {
            int width = Math.Max(1, area.Width - 24), height = Math.Max(1, area.Height - 24);
            MinimumSize = new Size(Math.Min(340, width), Math.Min(240, height));
            Size = new Size(Math.Min(Width, width), Math.Min(Height, height));
        }
        protected override void OnLoad(EventArgs e)
        {
            FitWorkingArea(Owner == null ? Screen.FromControl(this).WorkingArea : Screen.FromControl(Owner).WorkingArea);
            base.OnLoad(e);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if(CancelButton is Control)((Control)CancelButton).Focus();
            ResetScroll(Controls);
        }
        static void ResetScroll(Control.ControlCollection controls)
        {
            foreach(Control control in controls){var viewport=control as CalibrationChoiceContent;if(viewport!=null)viewport.AutoScrollPosition=Point.Empty;ResetScroll(control.Controls);}
        }
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) { if (ownedIcon != null) ownedIcon.Dispose(); if (ownedFont != null) ownedFont.Dispose(); } }
        }
    }

    // Arrange the footer and content in one pass. Dock-based footer resizing can
    // leave its old top position after a wrapped row changes the footer height.
    internal sealed class CalibrationDialogShell : Panel
    {
        readonly Control content;
        readonly Button[] buttons;
        readonly Panel footer;
        bool arranging;
        internal CalibrationDialogShell(Control body, Button[] actions)
        {
            content = body; buttons = actions; Padding = new Padding(12);
            footer = new Panel { Name = "CalibrationDialogActions" };
            foreach (var button in buttons) {
                button.AutoSize = false; button.Padding = new Padding(12, 6, 12, 6);
                button.FontChanged += delegate { PerformLayout(); };
                button.TextChanged += delegate { PerformLayout(); };
                footer.Controls.Add(button);
            }
            content.Dock = DockStyle.None; content.Margin = Padding.Empty;
            Controls.Add(content); Controls.Add(footer);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arranging || footer == null) return;
            arranging = true;
            try {
                int available = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                int x = available, y = 8, rowHeight = 0;
                foreach (var button in buttons) {
                    int width = Math.Min(available, Math.Max(94, TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + button.Padding.Horizontal + 24));
                    int height = Math.Max(36, TextRenderer.MeasureText(button.Text, button.Font, new Size(Math.Max(1, width - button.Padding.Horizontal - 24), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + button.Padding.Vertical + 24);
                    if (x != available && x - width < 0) { y += rowHeight + 6; x = available; rowHeight = 0; }
                    x -= width; button.SetBounds(x, y, width, height); x -= 6; rowHeight = Math.Max(rowHeight, height);
                }
                int footerHeight = y + rowHeight + 6;
                int contentHeight = Math.Max(0, ClientSize.Height - Padding.Vertical - footerHeight);
                content.SetBounds(Padding.Left, Padding.Top, available, contentHeight);
                footer.SetBounds(Padding.Left, Padding.Top + contentHeight, available, footerHeight);
            } finally { arranging = false; }
        }
    }

    internal sealed class CalibrationChoiceContent : Panel
    {
        readonly Label notice;
        readonly Control choices;
        readonly Panel content;
        bool arranging;
        internal CalibrationChoiceContent(string text, Control list)
        {
            AutoScroll = true; Name = "CalibrationChoiceViewport"; choices = list;
            content = new Panel(); notice = new Label { Name = "CalibrationChoiceNotice", Text = text, UseMnemonic = false };
            content.Controls.Add(notice); content.Controls.Add(choices); Controls.Add(content);
            SizeChanged += delegate { ArrangeContent(); }; FontChanged += delegate { ArrangeContent(); };
            notice.FontChanged += delegate { ArrangeContent(); }; choices.FontChanged += delegate { ArrangeContent(); };
        }
        void ArrangeContent()
        {
            if (arranging) return; arranging = true;
            try {
                int width = Math.Max(100, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
                int hintHeight = TextRenderer.MeasureText(notice.Text, notice.Font, new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 8;
                int listHeight = Math.Max(choices.Font.Height * 5 + 12, ClientSize.Height - hintHeight - 12);
                notice.SetBounds(0, 0, width, hintHeight); choices.SetBounds(0, hintHeight + 8, width, listHeight);
                content.SetBounds(AutoScrollPosition.X, AutoScrollPosition.Y, width, choices.Bottom);
                AutoScrollMinSize = new Size(0, content.Height);
            } finally { arranging = false; }
        }
    }
}
