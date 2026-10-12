using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    // Edits an isolated working copy. The caller owns persistence after OK.
    internal sealed class JointEditorDialog : Form
    {
        readonly int languageIndex;
        static readonly string[] AttachmentChinese = { "户内冷缩", "户外冷缩", "中间接头", "其他" };
        static readonly string[] AttachmentEnglish = { "Indoor cold shrink", "Outdoor cold shrink", "Intermediate joint", "Other" };
        static readonly string[] BodyChinese = { "塑料绝缘", "交联聚乙烯", "油纸", "其他" };
        static readonly string[] BodyEnglish = { "Plastic insulation", "XLPE", "Oil-impregnated paper", "Other" };
        readonly List<CableJoint> working = new List<CableJoint>();
        readonly ListBox list = new ListBox();
        readonly NumericUpDown distance = new NumericUpDown();
        readonly ComboBox attachment = new ComboBox();
        readonly ComboBox body = new ComboBox();
        readonly Label message = new Label();
        readonly Panel content = new Panel();
        readonly Label listLabel, positionLabel, attachmentLabel, bodyLabel;
        readonly Button add, edit, delete, save, cancel;
        bool layoutReady, arranging;
        CableJoint[] result;

        public CableJoint[] ResultJoints { get { return result == null ? null : Clone(result); } }

        internal JointEditorDialog(CableJoint[] joints, double proposedDistance, bool english)
            : this(joints, proposedDistance, english ? 1 : 0) { }

        internal JointEditorDialog(CableJoint[] joints, double proposedDistance, int languageIndex)
        {
            BrowserLanguages.Validate(languageIndex);
            this.languageIndex = languageIndex;
            if (joints != null) working.AddRange(Clone(joints).Where(j => j != null));
            Text = T("编辑电缆附件", "Edit cable attachments");
            Font = new Font("Arial", 10f);
            ForeColor = BrowserTheme.Ink;
            BackColor = BrowserTheme.Canvas;
            ClientSize = new Size(620, 440);
            MinimumSize = new Size(400, 300);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            content.Dock = DockStyle.Fill;
            content.AutoScroll = true;
            content.BackColor = BrowserTheme.Canvas;
            Controls.Add(content);

            listLabel = LabelAt(T("附件列表", "Attachments"), 22, 18, 235, 25);
            content.Controls.Add(listLabel);
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = BrowserTheme.Surface;
            list.ForeColor = BrowserTheme.Ink;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Math.Max(28, Font.Height + 10);
            list.DrawItem += DrawListItem;
            list.SelectedIndexChanged += delegate { SelectJoint(); };
            content.Controls.Add(list);

            positionLabel = LabelAt(T("位置  m", "Position  m"), 310, 51, 278, 24);
            content.Controls.Add(positionLabel);
            distance.AutoSize = false; distance.DecimalPlaces = 1; distance.Increment = .1m;
            distance.Minimum = 0; distance.Maximum = 1000000000m;
            distance.BackColor = BrowserTheme.Surface;
            distance.ForeColor = BrowserTheme.Ink;
            content.Controls.Add(distance);
            attachmentLabel = LabelAt(T("附件类型", "Attachment type"), 310, 125, 278, 24);
            content.Controls.Add(attachmentLabel);
            attachment.DropDownStyle = ComboBoxStyle.DropDown;
            foreach (string option in AttachmentChinese) attachment.Items.Add(LocalizeChoice(option, AttachmentChinese, AttachmentEnglish));
            attachment.BackColor = BrowserTheme.Surface;
            attachment.ForeColor = BrowserTheme.Ink;
            content.Controls.Add(attachment);
            bodyLabel = LabelAt(T("本体类型", "Cable body type"), 310, 200, 278, 24);
            content.Controls.Add(bodyLabel);
            body.DropDownStyle = ComboBoxStyle.DropDown;
            foreach (string option in BodyChinese) body.Items.Add(LocalizeChoice(option, BodyChinese, BodyEnglish));
            body.BackColor = BrowserTheme.Surface;
            body.ForeColor = BrowserTheme.Ink;
            content.Controls.Add(body);

            add = MakeButton(T("新增", "Add"), 310, 289, 84, false);
            edit = MakeButton(T("修改", "Edit"), 407, 289, 84, false);
            delete = MakeButton(T("删除", "Delete"), 504, 289, 84, false);
            add.Click += delegate { CommitEditor(false); };
            edit.Click += delegate { CommitEditor(true); };
            delete.Click += delegate { DeleteSelected(); };
            message.ForeColor = BrowserTheme.Muted;
            message.AutoEllipsis = true;
            content.Controls.Add(message);
            save = MakeButton(T("保存", "Save"), 394, 391, 94, true);
            cancel = MakeButton(T("取消", "Cancel"), 502, 391, 94, false);
            save.Click += delegate { SaveResult(); };
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            AcceptButton = save;
            FormClosing += delegate { if (DialogResult != DialogResult.OK) result = null; };
            RefreshList(null);
            double end = EndPosition(working);
            double initial = proposedDistance;
            if (!Finite(initial) || initial <= 0 || initial >= end) initial = end > 0 ? end / 2 : 0;
            SetDistance(initial);
            attachment.Text = LocalizeChoice("户内冷缩", AttachmentChinese, AttachmentEnglish);
            body.Text = LocalizeChoice("塑料绝缘", BodyChinese, BodyEnglish);
            SetHint();
            layoutReady = true;
            content.Resize += delegate { LayoutContent(); };
            FontChanged += delegate { list.ItemHeight = Math.Max(28, Font.Height + 10); LayoutContent(); };
            LayoutContent();
        }

        string T(string chinese, string en) { return BrowserLanguages.Text(languageIndex, chinese, en); }
        string LocalizeChoice(string canonical, string[] source, string[] english)
        {
            int index = Array.IndexOf(source, canonical);
            return index < 0 ? canonical : T(source[index], english[index]);
        }
        string CanonicalChoice(string display, string[] source, string[] english)
        {
            for (int i = 0; i < source.Length; i++)
                if (String.Equals(display, T(source[i], english[i]), StringComparison.Ordinal)) return source[i];
            return display;
        }
        Label LabelAt(string text, int x, int y, int width, int height)
        { return new Label { Text = text, ForeColor = BrowserTheme.Ink, Location = new Point(x, y), Size = new Size(width, height), TextAlign = ContentAlignment.MiddleLeft }; }
        Button MakeButton(string text, int x, int y, int width, bool primary)
        {
            var b = new RoundedEditorButton { Text = text, Location = new Point(x, y), Size = new Size(width, 32), Primary = primary };
            content.Controls.Add(b); return b;
        }
        void LayoutContent()
        {
            if (!layoutReady || arranging || content.ClientSize.Width < 1 || content.ClientSize.Height < 1) return;
            arranging = true;
            content.SuspendLayout();
            try
            {
                const int padding = 18, gap = 12;
                content.AutoScrollPosition = Point.Empty;
                int commandWidth = Math.Max(84, Math.Max(
                    TextRenderer.MeasureText(add.Text, Font).Width,
                    Math.Max(TextRenderer.MeasureText(edit.Text, Font).Width,
                             TextRenderer.MeasureText(delete.Text, Font).Width)) + 28);
                int saveWidth = Math.Max(94, TextRenderer.MeasureText(save.Text, Font).Width + 28);
                int cancelWidth = Math.Max(94, TextRenderer.MeasureText(cancel.Text, Font).Width + 28);
                int minimumContentWidth = Math.Max(340, commandWidth * 3 + gap * 2);
                int width = Math.Max(minimumContentWidth,
                    content.ClientSize.Width - padding * 2 - SystemInformation.VerticalScrollBarWidth);
                int labelHeight = Math.Max(24, Font.Height + 6);
                int inputHeight = Math.Max(29, Font.Height + 10);
                int buttonHeight = Math.Max(32, Font.Height + 12);
                bool sideBySide = width >= Math.Max(560, 240 + gap + commandWidth * 3 + gap * 2);
                int listWidth = sideBySide ? Math.Max(240, (int)(width * .43)) : width;
                int listTop = padding + labelHeight + 6;
                int listHeight = sideBySide
                    ? Math.Max(180, content.ClientSize.Height - listTop - 105)
                    : Math.Max(170, list.ItemHeight * 5);
                listLabel.SetBounds(padding, padding, listWidth, labelHeight);
                list.SetBounds(padding, listTop, listWidth, listHeight);

                int editorX = sideBySide ? padding + listWidth + gap : padding;
                int editorWidth = sideBySide ? width - listWidth - gap : width;
                int y = sideBySide ? listTop : list.Bottom + 14;
                positionLabel.SetBounds(editorX, y, editorWidth, labelHeight);
                y += labelHeight + 4;
                distance.SetBounds(editorX, y, editorWidth, inputHeight);
                y += inputHeight + 12;
                attachmentLabel.SetBounds(editorX, y, editorWidth, labelHeight);
                y += labelHeight + 4;
                attachment.SetBounds(editorX, y, editorWidth, inputHeight);
                y += inputHeight + 12;
                bodyLabel.SetBounds(editorX, y, editorWidth, labelHeight);
                y += labelHeight + 4;
                body.SetBounds(editorX, y, editorWidth, inputHeight);
                y += inputHeight + 20;
                add.SetBounds(editorX, y, commandWidth, buttonHeight);
                edit.SetBounds(editorX + commandWidth + gap, y, commandWidth, buttonHeight);
                delete.SetBounds(editorX + (commandWidth + gap) * 2, y, commandWidth, buttonHeight);

                int footerTop = Math.Max(list.Bottom, add.Bottom) + 8;
                int messageHeight = Math.Max(36, Font.Height * 2 + 4);
                message.SetBounds(padding, footerTop, width, messageHeight);
                int footerButtonTop = message.Bottom + 8;
                cancel.SetBounds(padding + width - cancelWidth, footerButtonTop, cancelWidth, buttonHeight);
                save.SetBounds(cancel.Left - gap - saveWidth, footerButtonTop, saveWidth, buttonHeight);
                content.AutoScrollMinSize = new Size(width + padding * 2, cancel.Bottom + padding);
            }
            finally
            {
                content.ResumeLayout(false);
                arranging = false;
            }
        }
        void SetHint()
        { message.ForeColor = BrowserTheme.Muted; message.Text = T("端点为灰色，不可修改或删除。新增和修改后点击保存。", "Grey endpoints cannot be edited or deleted. Save after adding or editing."); }
        void SetDistance(double value)
        {
            if (!Finite(value)) value = 0;
            value = Math.Max((double)distance.Minimum, Math.Min((double)distance.Maximum, value));
            distance.Value = (decimal)value;
        }
        void RefreshList(CableJoint selected)
        {
            working.Sort(delegate(CableJoint a, CableJoint b) { return a.PositionMetres.CompareTo(b.PositionMetres); });
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var joint in working) list.Items.Add(joint);
                if (selected != null) list.SelectedItem = selected;
            }
            finally { list.EndUpdate(); }
            edit.Enabled = delete.Enabled = list.SelectedItem != null && !((CableJoint)list.SelectedItem).IsEndpoint;
        }
        void SelectJoint()
        {
            var joint = list.SelectedItem as CableJoint;
            edit.Enabled = delete.Enabled = joint != null && !joint.IsEndpoint;
            if (joint == null) return;
            SetDistance(joint.PositionMetres);
            attachment.Text = LocalizeChoice(joint.AttachmentType ?? "", AttachmentChinese, AttachmentEnglish);
            body.Text = LocalizeChoice(joint.BodyType ?? "", BodyChinese, BodyEnglish);
            SetHint();
        }
        void DrawListItem(object sender, DrawItemEventArgs e)
        {
            using (var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? BrowserTheme.Selection : BrowserTheme.Surface)) e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var joint = (CableJoint)list.Items[e.Index];
            Color color = joint.IsEndpoint ? BrowserTheme.Muted : BrowserTheme.Ink;
            string title = joint.PositionMetres.ToString("0.0", CultureInfo.InvariantCulture) + " m  " +
                (joint.IsEndpoint ? T(joint.PositionMetres == 0 ? "起点" : "终点", joint.PositionMetres == 0 ? "Start" : "End") :
                LocalizeChoice(joint.AttachmentType ?? "", AttachmentChinese, AttachmentEnglish));
            if (joint.IsEndpoint) title += T("  [端点]", "  [Endpoint]");
            TextRenderer.DrawText(e.Graphics, title, Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }
        void CommitEditor(bool replace)
        {
            var selected = list.SelectedItem as CableJoint;
            if (replace && (selected == null || selected.IsEndpoint)) return;
            var candidate = new CableJoint
            {
                PositionMetres = Math.Round((double)distance.Value, 1, MidpointRounding.AwayFromZero),
                AttachmentType = CanonicalChoice(attachment.Text.Trim(), AttachmentChinese, AttachmentEnglish),
                BodyType = CanonicalChoice(body.Text.Trim(), BodyChinese, BodyEnglish), IsEndpoint = false
            };
            var trial = new List<CableJoint>(working);
            if (replace) trial.Remove(selected);
            trial.Add(candidate);
            string error;
            if (!ValidateJoints(trial.ToArray(), out error)) { ShowError(error); return; }
            if (replace) working.Remove(selected);
            working.Add(candidate);
            RefreshList(candidate); SetHint();
        }
        void DeleteSelected()
        {
            var selected = list.SelectedItem as CableJoint;
            if (selected == null || selected.IsEndpoint) return;
            working.Remove(selected); RefreshList(null); SetHint();
        }
        void SaveResult()
        {
            string error;
            if (!ValidateJoints(working.ToArray(), out error)) { ShowError(error); return; }
            result = Clone(working.OrderBy(j => j.PositionMetres).ToArray());
            DialogResult = DialogResult.OK; Close();
        }
        void ShowError(string code)
        {
            string text;
            switch (code)
            {
                case "endpoints": text = T("需要有效的起点 0 m 和终点。", "Valid start at 0 m and end are required."); break;
                case "distance": text = T("附件位置必须在起点和终点之间。", "An attachment must be strictly between the start and end."); break;
                case "duplicate": text = T("该位置已存在附件，位置按一位小数比较。", "An attachment already exists at that position to one decimal place."); break;
                case "type": text = T("请填写附件类型和本体类型。", "Enter both attachment type and cable body type."); break;
                default: text = T("附件数据无效。", "Invalid attachment data."); break;
            }
            message.ForeColor = BrowserTheme.Error; message.Text = text;
        }

        internal static bool ValidateJoints(CableJoint[] joints, out string error)
        {
            error = "";
            if (joints == null || joints.Any(j => j == null || !Finite(j.PositionMetres))) { error = "invalid"; return false; }
            var endpoints = joints.Where(j => j.IsEndpoint).OrderBy(j => j.PositionMetres).ToArray();
            if (endpoints.Length != 2 || endpoints[0].PositionMetres != 0 || endpoints[1].PositionMetres <= 0)
            { error = "endpoints"; return false; }
            double end = endpoints[1].PositionMetres;
            var occupied = new HashSet<double>();
            foreach (var joint in joints)
            {
                if (!joint.IsEndpoint && (joint.PositionMetres <= 0 || joint.PositionMetres >= end)) { error = "distance"; return false; }
                if (!occupied.Add(Math.Round(joint.PositionMetres, 1, MidpointRounding.AwayFromZero))) { error = "duplicate"; return false; }
                if (!joint.IsEndpoint && (String.IsNullOrWhiteSpace(joint.AttachmentType) || String.IsNullOrWhiteSpace(joint.BodyType))) { error = "type"; return false; }
            }
            return true;
        }
        static double EndPosition(IEnumerable<CableJoint> joints)
        { return joints.Where(j => j != null && j.IsEndpoint && Finite(j.PositionMetres)).Select(j => j.PositionMetres).DefaultIfEmpty(0).Max(); }
        static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        static CableJoint[] Clone(IEnumerable<CableJoint> joints)
        { return joints.Select(j => j == null ? null : new CableJoint { PositionMetres = j.PositionMetres, AttachmentType = j.AttachmentType, BodyType = j.BodyType, IsEndpoint = j.IsEndpoint }).ToArray(); }

        sealed class RoundedEditorButton : Button
        {
            internal bool Primary;
            internal RoundedEditorButton()
            { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; UseVisualStyleBackColor = false; }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = !Enabled ? BrowserTheme.Canvas : Primary ? BrowserTheme.Primary : BrowserTheme.SurfaceAlt;
                if (Enabled && ClientRectangle.Contains(PointToClient(MousePosition))) fill = Primary ? BrowserTheme.PrimaryHover : BrowserTheme.Selection;
                var r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (var path = new GraphicsPath())
                {
                    float d = Math.Min(12f * Math.Max(.5f, e.Graphics.DpiX / 96f), Math.Min(r.Width, r.Height));
                    path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
                    using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                    using (var border = new Pen(Primary && Enabled ? BrowserTheme.Primary : BrowserTheme.Border)) e.Graphics.DrawPath(border, path);
                }
                Color text = !Enabled ? BrowserTheme.Muted : Primary ? Color.White : BrowserTheme.Ink;
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), text, fill);
            }
        }
    }
}


