using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using HVX.Controls;

namespace LabVIEWItemInformation
{
    [DefaultEvent("InformationChanged")]
    public sealed class ItemInformationControl : UserControl
    {
        internal static readonly string[] FieldKeys = {
            "TestingOrganization", "TestPersonnel", "TestLocation", "StartName", "EndName",
            "StartType", "EndType", "StartDispatch", "EndDispatch", "LineName", "InstallationDate",
            "CableModel", "CableLength", "CoreCount", "PhaseVoltage", "TestMode", "Remarks",
            "A1", "A2", "B1", "B2", "C1", "C2" };
        readonly Dictionary<string, Control> editors = new Dictionary<string, Control>();
        readonly Dictionary<string, Label> labels = new Dictionary<string, Label>();
        readonly Dictionary<string, string> emptyValues = new Dictionary<string, string>();
        readonly Panel layout = new BufferedPanel();
        Size logicalSize;
        int arrangedWidth = -1, remarksTop, targetViewportHeight;
        readonly Font[] responsiveFonts = { new Font("Arial", 10.5f), new Font("Arial", 11.5f), new Font("Arial", 12.5f) };
        bool layoutDirty = true;
        readonly Dictionary<string, ReferenceFieldFrame> frames = new Dictionary<string, ReferenceFieldFrame>();
        bool arranging;
        readonly CableSketch sketch = new CableSketch();
        readonly Label status = new Label();
        readonly ToolTip tips = new ToolTip();
        ItemDocument document;
        int language;
        bool refreshing, metadataReadOnly = true, insulationReadOnly;
        string lastError = "";
        long changeSequence;
        bool dirty, lastOperationSucceeded;

        public ItemInformationControl()
        {
            Font = responsiveFonts[1];
            BackColor = BrowserTheme.Surface; ForeColor = BrowserTheme.Ink;
            Size = new Size(921, 608);
            AutoScaleMode = AutoScaleMode.None;
            AutoScroll = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            layout.BackColor = BrowserTheme.Surface;
            layout.AutoScroll = true;
            Controls.Add(layout);
            foreach (string key in FieldKeys)
            {
                emptyValues[key] = "";
                Label label = MakeLabel(key);
                Control editor = MakeEditor(key);
                ReferenceFieldFrame frame = new ReferenceFieldFrame();
                frame.BackColor = key == "Remarks" ? BrowserTheme.Surface : BrowserTheme.SurfaceAlt;
                frame.Controls.Add(editor);
                frames.Add(key, frame);
                layout.Controls.Add(label); layout.Controls.Add(frame);
            }
            TextBox notes = (TextBox)editors["Remarks"];
            notes.Multiline = true; notes.AcceptsReturn = true;
            notes.TextAlign = HorizontalAlignment.Left; notes.ScrollBars = ScrollBars.None;
            // Source path and diagnostic state remain available through the .NET properties.
            status.Visible = false;
            sketch.Margin = Padding.Empty;
            layout.Controls.Add(sketch);
            RefreshEditors();
            Resize += delegate { Arrange(); };
            Arrange();
        }

        Dictionary<string, string> Values { get { return document == null ? emptyValues : document.Values; } }
        void OnUi(Action action) { if (InvokeRequired) Invoke(action); else action(); }
        T OnUi<T>(Func<T> action) { return InvokeRequired ? (T)Invoke(action) : action(); }

        [Category("Language"), DefaultValue(0)]
        public int LanguageId { get { return OnUi(delegate { return language; }); } set { SetLanguage(value); } }
        public void SetLanguage(int languageId)
        {
            if (languageId < 0 || languageId >= Xiongxin.OWTS.Localization.UiLanguage.Codes.Length) throw new ArgumentOutOfRangeException("languageId", "Expected 0..18.");
            OnUi(delegate { if(language==languageId)return; language = languageId; RefreshEditors(); });
        }
        public string[] GetLanguageNames() { return (string[])UiText.Names.Clone(); }
        [Category("Language"), DefaultValue(false)]
        public bool EnglishVersion { get { return LanguageId == 1; } set { SetLanguage(value ? 1 : 0); } }
        public void SetEnglishDisplay(bool englishVersion) { EnglishVersion = englishVersion; }
        [Category("Behavior"), DefaultValue(true)]
        public bool MetadataReadOnly { get { return metadataReadOnly; } set { OnUi(delegate { if(metadataReadOnly==value)return; metadataReadOnly = value; RefreshEditors(); }); } }
        [Category("Behavior"), DefaultValue(false)]
        public bool InsulationReadOnly { get { return insulationReadOnly; } set { OnUi(delegate { if(insulationReadOnly==value)return; insulationReadOnly = value; RefreshEditors(); }); } }
        internal Size PreferredUnscaledSize { get { return PreferredSizeForViewport(921, 608); } }
        internal Size PreferredSizeForWidth(int width) { return PreferredSizeForViewport(width, ClientSize.Height); }
        internal Size PreferredSizeForViewport(int width, int height)
        {
            return OnUi(delegate
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);
                bool previousArranging=arranging;arranging=true;
                SuspendEditorLayout();
                try {
                    targetViewportHeight = height;
                    Font wanted = width >= 1250 && height >= 720 ? responsiveFonts[2]
                        : width < 850 || height < 530 ? responsiveFonts[0] : responsiveFonts[1];
                    if (!Object.ReferenceEquals(Font, wanted)) Font = wanted;
                    if (layoutDirty || arrangedWidth != width) BuildLogicalLayout(width);
                    else FitRemarks();
                    return logicalSize;
                }
                finally { ResumeEditorLayout();arranging=previousArranging; }
            });
        }
        public bool IsLoaded { get { return OnUi(delegate { return document != null; }); } }
        public string LoadedItemPath { get { return OnUi(delegate { return document == null ? "" : document.SourcePath; }); } }
        public string SourceEncodingName { get { return OnUi(delegate { return document == null ? "" : document.SourceEncodingName; }); } }
        public string LoadWarnings { get { return OnUi(delegate { return document == null ? "" : document.Warnings; }); } }
        public string LastError { get { return OnUi(delegate { return lastError; }); } }
        public bool LastOperationSucceeded { get { return OnUi(delegate { return lastOperationSucceeded; }); } }
        public bool HasUnsavedChanges { get { return OnUi(delegate { return dirty; }); } }
        public long ChangeSequence { get { return OnUi(delegate { return changeSequence; }); } }
        public event EventHandler InformationChanged;

        internal void SetCableStructureProjection(string[] phases, double[][] positions)
        {
            if (document == null) return;
            document.SetCableProjection(phases, positions);
            UpdateSketch(); sketch.Invalidate();
        }

        public bool LoadItem(string itemFileOrProjectDirectory, int languageId)
        {
            return OnUi(delegate
            {
                try
                {
                    if (languageId < 0 || languageId >= Xiongxin.OWTS.Localization.UiLanguage.Codes.Length) throw new ArgumentOutOfRangeException("languageId", "Expected 0..18.");
                    ItemDocument next = ItemDocument.Load(itemFileOrProjectDirectory);
                    return ApplyPreparedItem(next,languageId,"");
                }
                catch (Exception ex) { Fail(ex); return false; }
            });
        }

        // Parsing is independent of WinForms. The browser prepares this document
        // on its worker, then applies it only after the load generation is checked.
        internal bool ApplyPreparedItem(ItemDocument next,int languageId,string error)
        {
            return OnUi(delegate {
                if(languageId<0||languageId>=Xiongxin.OWTS.Localization.UiLanguage.Codes.Length)throw new ArgumentOutOfRangeException("languageId");
                document=next;language=languageId;dirty=false;lastError=error??"";lastOperationSucceeded=next!=null;
                if(next==null){RefreshStatus();return false;}
                RefreshEditors();RaiseChanged(false);return true;
            });
        }

        public string GetFieldValue(string fieldKey)
        {
            return OnUi(delegate { CheckKey(fieldKey); string value; return Values.TryGetValue(fieldKey, out value) ? value : ""; });
        }
        public void SetFieldValue(string fieldKey, string value)
        {
            OnUi(delegate { CheckKey(fieldKey); Values[fieldKey] = value ?? ""; RefreshEditors(); RaiseChanged(true); });
        }
        static void CheckKey(string key)
        {
            if (key == null || Array.IndexOf(FieldKeys, key) < 0) throw new ArgumentException("Unknown field key.", "fieldKey");
        }
        public string[,] GetInformationTable()
        {
            return OnUi(delegate
            {
                string[,] table = new string[FieldKeys.Length, 3];
                for (int i = 0; i < FieldKeys.Length; i++)
                {
                    table[i, 0] = FieldKeys[i]; table[i, 1] = Caption(FieldKeys[i]); table[i, 2] = GetFieldValue(FieldKeys[i]);
                }
                return table;
            });
        }
        public string[,] GetInsulationTextMatrix()
        {
            return OnUi(delegate
            {
                string[,] result = new string[3, 2];
                for (int p = 0; p < 3; p++) for (int m = 0; m < 2; m++) result[p, m] = GetFieldValue("ABC"[p] + (m + 1).ToString(CultureInfo.InvariantCulture));
                return result;
            });
        }
        public void SetInsulationText(string a1, string a2, string b1, string b2, string c1, string c2)
        {
            OnUi(delegate
            {
                string[] texts = { a1, a2, b1, b2, c1, c2 };
                string[] keys = { "A1", "A2", "B1", "B2", "C1", "C2" };
                for (int i = 0; i < keys.Length; i++) Values[keys[i]] = texts[i] ?? "";
                RefreshEditors(); RaiseChanged(true);
            });
        }
        public double[,] GetInsulationValues()
        {
            string[,] text = GetInsulationTextMatrix();
            double[,] result = new double[3, 2];
            for (int p = 0; p < 3; p++) for (int m = 0; m < 2; m++) result[p, m] = ParseResistance(text[p, m]);
            return result;
        }
        public void SetInsulationValues(double[,] resistanceMOhm)
        {
            if (resistanceMOhm == null || resistanceMOhm.GetLength(0) != 3 || resistanceMOhm.GetLength(1) != 2)
                throw new ArgumentException("Expected a 3 x 2 matrix (A/B/C rows, measurement 1/2 columns).", "resistanceMOhm");
            string[] result = new string[6];
            for (int p = 0; p < 3; p++) for (int m = 0; m < 2; m++)
            {
                double n = resistanceMOhm[p, m];
                if (n < 0 || double.IsNegativeInfinity(n)) throw new ArgumentOutOfRangeException("resistanceMOhm", "Resistance cannot be negative.");
                result[p * 2 + m] = double.IsNaN(n) ? "" : double.IsPositiveInfinity(n) ? "∞" : n.ToString("G17", CultureInfo.InvariantCulture);
            }
            SetInsulationText(result[0], result[1], result[2], result[3], result[4], result[5]);
        }
        internal static double ParseResistance(string value)
        {
            string s = (value ?? "").Trim();
            if (s == "∞" || s.Equals("Infinity", StringComparison.OrdinalIgnoreCase) || s.Equals("+Infinity", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            double n;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && n >= 0 && !double.IsInfinity(n) ? n : double.NaN;
        }
        public bool SaveItemCopy(string newItemFilePath)
        {
            return OnUi(delegate
            {
                try
                {
                    if (document == null) throw new InvalidOperationException("Load an ITEM file before saving a copy.");
                    document.SaveCopy(newItemFilePath); dirty = false; lastError = ""; lastOperationSucceeded = true; RefreshStatus(); return true;
                }
                catch (Exception ex) { Fail(ex); return false; }
            });
        }
        public bool ExportCsv(string newCsvFilePath)
        {
            return OnUi(delegate
            {
                try
                {
                    string[,] table = GetInformationTable();
                    using (FileStream stream = new FileStream(newCsvFilePath, FileMode.CreateNew, FileAccess.Write))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(true)))
                    {
                        writer.WriteLine("FieldKey,Label,Value");
                        for (int i = 0; i < table.GetLength(0); i++) writer.WriteLine(Csv(table[i, 0]) + "," + Csv(table[i, 1]) + "," + Csv(table[i, 2]));
                    }
                    lastError = ""; lastOperationSucceeded = true; RefreshStatus(); return true;
                }
                catch (Exception ex) { Fail(ex); return false; }
            });
        }
        static string Csv(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
        void Fail(Exception ex) { lastError = ex.Message; lastOperationSucceeded = false; RefreshStatus(); }
        void RaiseChanged(bool edited)
        {
            dirty = edited || dirty; changeSequence++; RefreshStatus();
            EventHandler handler = InformationChanged; if (handler != null) handler(this, EventArgs.Empty);
        }
        static bool IsResistance(string key) { return key.Length == 2 && "ABC".IndexOf(key[0]) >= 0 && (key[1] == '1' || key[1] == '2'); }
        string Caption(string key)
        {
            if (IsResistance(key))
                return (Xiongxin.OWTS.Localization.UiLanguage.IsChinese(language) ? key[0].ToString() : "L" + ("ABC".IndexOf(key[0]) + 1).ToString()) + " " + UiText.Get("Insulation", language) + " [MΩ] " + key[1];
            return UiText.Get(key, language) + (key == "CableLength" ? " [m]" : key == "PhaseVoltage" ? " [kV]" : "");
        }
        void RefreshStatus()
        {
            status.ForeColor = lastError.Length > 0 ? BrowserTheme.Error : BrowserTheme.Muted;
            status.Text = lastError.Length > 0 ? lastError : document == null ? UiText.Get("NotLoaded", language) : (dirty ? "* " : "") + document.SourcePath;
            tips.SetToolTip(status, status.Text + (document == null || document.Warnings.Length == 0 ? "" : "\r\n" + document.Warnings));
        }
        void RefreshEditors()
        {
            refreshing = true;
            SuspendEditorLayout();
            try
            {
                foreach (string key in FieldKeys)
                {
                    string text; if (!Values.TryGetValue(key, out text)) text = "";
                    Control editor = editors[key];
                    labels[key].Text = DisplayCaption(key); tips.SetToolTip(labels[key], Caption(key) + (IsResistance(key) ? " — " + UiText.Get(key[1] == '1' ? "Measurement1" : "Measurement2", language) : ""));
                    ReferenceComboBox combo = editor as ReferenceComboBox;
                    if (combo != null)
                    {
                        combo.Items.Clear(); string[] options = Options(key);
                        combo.Items.Add("");
                        foreach (string raw in options) combo.Items.Add(DisplayValue(key, TranslateOption(raw)));
                        string display = DisplayValue(key, TranslateOption(text));
                        if (!combo.Items.Contains(display)) combo.Items.Add(display);
                        combo.SelectedItem = display; combo.ReadOnly = metadataReadOnly; combo.Visible = !metadataReadOnly; frames[key].Invalidate();
                    }
                    else
                    {
                        TextBox box = (TextBox)editor; box.Text = DisplayValue(key, text);
                        box.ReadOnly = IsResistance(key) ? insulationReadOnly : key != "Remarks" && metadataReadOnly;
                        box.BackColor = key == "Remarks" ? BrowserTheme.Surface : BrowserTheme.SurfaceAlt;
                        box.ForeColor = BrowserTheme.Ink;
                    }
                }
                UpdateSketch();
                layoutDirty = true;
                sketch.LanguageId = language; sketch.EmptyCaption = UiText.Get("NoGeometry", language);
                sketch.Invalidate();
                RefreshStatus();
            }
            finally { ResumeEditorLayout(); refreshing = false; }
            Arrange();
        }
        void SuspendEditorLayout(){layout.SuspendLayout();foreach(ReferenceFieldFrame frame in frames.Values)frame.SuspendLayout();}
        void ResumeEditorLayout(){foreach(ReferenceFieldFrame frame in frames.Values)frame.ResumeLayout(true);layout.ResumeLayout(true);}
        static string[] Options(string key)
        {
            if (key == "StartType" || key == "EndType") return new string[] { "户内冷缩", "户外冷缩", "户内热缩", "户外热缩", "高压插拔头" };
            if (key == "CoreCount") return new string[] { "单芯", "三芯", "三相单芯" };
            if (key == "TestMode") return new string[] { "一年以内", "一年以上" };
            if (key == "PhaseVoltage") return new string[] { "6", "8.7", "12", "21", "26", "64", "127" };
            return new string[0];
        }
        string TranslateOption(string raw)
        {
            if(raw=="高压插拔头")return Xiongxin.OWTS.Localization.UiLanguage.Text(language,"高压插拔头","Separable connector");
            string key = raw == "户内冷缩" ? "IndoorCold" : raw == "户外冷缩" ? "OutdoorCold" : raw == "户内热缩" ? "IndoorHeat" : raw == "户外热缩" ? "OutdoorHeat" : raw == "单芯" ? "SingleCore" : (raw == "三芯" || raw == "三相" || raw == "一体挤包") ? "ThreeCore" : raw == "三相单芯" ? "ThreeSingleCore" : raw == "一年以内" ? "WithinYear" : raw == "一年以上" ? "OverYear" : "";
            return key.Length == 0 ? raw : UiText.Get(key, language);
        }
        void EditorChanged(string key)
        {
            if (refreshing) return;
            string value = editors[key].Text;
            foreach (string raw in Options(key)) if (DisplayValue(key, TranslateOption(raw)) == value) { value = raw; break; }
            Values[key] = value;
            if (key == "CoreCount") { UpdateSketch(); layoutDirty = true; Arrange(); sketch.Invalidate(); }
            else if(key=="StartType"||key=="EndType"){UpdateSketch();sketch.Invalidate();}
            RaiseChanged(true);
        }
        string DisplayCaption(string key)
        {
            if (IsResistance(key))
            {
                if (language == 0) return key[0] + "相绝缘[MΩ] " + key[1];
                if (language == 18) return key[0] + "相絕緣[MΩ] " + key[1];
                string[] shortNames = { "绝缘", "Insul.", "Изол.", "Izol.", "Izol.", "Isol.", "Isolation", "Szig.", "Isol.", "Izol.", "Izol.", "Изол.", "Изол.", "Izol.", "Izol.", "Aisl.", "Yalıtım", "Cách điện" };
                return "L" + ("ABC".IndexOf(key[0]) + 1).ToString() + " " + (language >= 19 ? "IR" : shortNames[language]) + " [MΩ] " + key[1];
            }
            if (key == "Remarks") return UiText.Get(key, language) + (language == 0 ? "：" : ":");
            return UiText.Get(key, language);
        }
        static string DisplayValue(string key, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            double number;
            if (key == "CableLength" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return value + " m";
            if (key == "PhaseVoltage" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return value + "kV";
            return value;
        }
        Label MakeLabel(string key)
        {
            Label label = new ReferenceLabel { AutoEllipsis = false, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, UseMnemonic = false };
            label.BackColor = BrowserTheme.Surface;
            label.ForeColor = BrowserTheme.Ink;
            labels.Add(key, label); return label;
        }
        Control MakeEditor(string key)
        {
            Control editor;
            if (Options(key).Length > 0)
            {
                ReferenceComboBox combo = new ReferenceComboBox();
                combo.TextChanged += delegate { EditorChanged(key); }; editor = combo;
            }
            else
            {
                TextBox box = new TextBox { BorderStyle = BorderStyle.None, AutoSize = false, TextAlign = HorizontalAlignment.Center };
                box.TextChanged += delegate { EditorChanged(key); }; editor = box;
            }
            editor.Name = key; editor.Margin = Padding.Empty; editors.Add(key, editor); return editor;
        }
        int RowHeight { get { return Math.Max(29, Font.Height + 8); } }
        int RowPitch { get { return RowHeight + 5; } }
        void Place(string key, float labelX, int y, float labelWidth, float fieldX, float fieldWidth, float scale)
        {
            labels[key].SetBounds((int)Math.Round(labelX * scale), y, (int)Math.Round(labelWidth * scale), RowHeight);
            frames[key].SetBounds((int)Math.Round(fieldX * scale), y, (int)Math.Round(fieldWidth * scale), RowHeight);
        }
        int LabelWidth(string key)
        {
            return TextRenderer.MeasureText(DisplayCaption(key), Font, new Size(2000, 40), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 8;
        }
        int MaxLabel(params string[] keys)
        {
            int width = 0; foreach (string key in keys) width = Math.Max(width, LabelWidth(key)); return width;
        }
        int MinimumEditorWidth(string key, int minimum)
        {
            int result = minimum;
            foreach (string value in Options(key))
                result = Math.Max(result, TextRenderer.MeasureText(DisplayValue(key, TranslateOption(value)), Font, new Size(2000, 40), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 38);
            return result;
        }
        void InlineRow(string[] keys, int y, int width, int minimumFieldWidth)
        {
            int required = RequiredRowWidth(keys, minimumFieldWidth);
            float extra = Math.Max(0, width - required) / (float)keys.Length;
            float x = 50;
            foreach (string key in keys)
            {
                int labelWidth = LabelWidth(key);
                float fieldWidth = MinimumEditorWidth(key, minimumFieldWidth) + extra;
                Place(key, x, y, labelWidth, x + labelWidth, fieldWidth, 1);
                x += labelWidth + fieldWidth + 12;
            }
        }
        int RequiredRowWidth(string[] keys, int minimumFieldWidth)
        {
            int width = 85 + (keys.Length - 1) * 12;
            foreach (string key in keys) width += LabelWidth(key) + MinimumEditorWidth(key, minimumFieldWidth);
            return width;
        }
        int PlaceFlexibleRows(string[] keys, int y, int width, int minimumFieldWidth, int maximumPerRow)
        {
            for (int index = 0; index < keys.Length;)
            {
                int count = Math.Min(maximumPerRow, keys.Length - index);
                string[] row;
                while (true)
                {
                    row = new string[count];
                    Array.Copy(keys, index, row, 0, count);
                    if (count == 1 || RequiredRowWidth(row, minimumFieldWidth) <= width) break;
                    count--;
                }
                InlineRow(row, y, width, minimumFieldWidth);
                index += count;
                y += RowPitch;
            }
            return y;
        }
        // Keep controls at their readable font size. The host can use PreferredSizeForWidth
        // to provide vertical scrolling when the form changes from two columns to one.
        void Arrange()
        {
            if (arranging || frames.Count != FieldKeys.Length || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            arranging = true;
            layout.SuspendLayout();
            foreach (ReferenceFieldFrame frame in frames.Values) frame.SuspendLayout();
            try
            {
                if (layoutDirty || arrangedWidth != ClientSize.Width) BuildLogicalLayout(ClientSize.Width);
                else FitRemarks();
                layout.Bounds = ClientRectangle;
                layout.Font = Font;
                foreach (ReferenceFieldFrame frame in frames.Values) frame.UiScale = 1f;
                int visibleWidth = logicalSize.Width > ClientSize.Width
                    ? logicalSize.Width : Math.Max(0, logicalSize.Width - SystemInformation.VerticalScrollBarWidth);
                layout.AutoScrollMinSize = new Size(visibleWidth, logicalSize.Height);

                sketch.Invalidate();
            }
            finally
            {
                foreach (ReferenceFieldFrame frame in frames.Values) frame.ResumeLayout(true);
                layout.ResumeLayout(true);
                arranging = false;
            }
        }
        void UpdateSketch()
        {
            string startType,endType;Values.TryGetValue("StartType",out startType);Values.TryGetValue("EndType",out endType);
            sketch.StartType=startType;sketch.EndType=endType;
            double[][] positions = document == null ? new double[0][] : document.CablePositions;
            string[] phases = document == null ? new string[0] : document.CablePhases;
            string core; Values.TryGetValue("CoreCount", out core);
            if (core == "三相单芯")
            {
                sketch.Positions = new double[3][];
                sketch.Phases = new string[] { "A", "B", "C" };
                int common = Array.IndexOf(phases, "ALL");
                for (int p = 0; p < 3; p++)
                {
                    int index = Array.IndexOf(phases, sketch.Phases[p]);
                    if (index < 0) index = common;
                    // Unknown phase geometry stays empty; do not invent lengths or joints.
                    sketch.Positions[p] = index >= 0 && index < positions.Length ? positions[index] : new double[0];
                }
            }
            else
            {
                int index = Array.IndexOf(phases, "A");
                if (index < 0) index = 0;
                sketch.Positions = positions.Length == 0 ? positions : new double[][] { positions[index] };
                sketch.Phases = new string[] { "ALL" };
            }
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            layoutDirty = true;
            if (frames != null) Arrange();
        }
        void FitRemarks()
        {
            int available = targetViewportHeight > 0 ? targetViewportHeight : ClientSize.Height;
            int notesHeight = Math.Max(48, available - remarksTop - 8);
            frames["Remarks"].Height = notesHeight;
            logicalSize = new Size(logicalSize.Width, remarksTop + notesHeight + 8);
        }
        void BuildLogicalLayout(int requestedWidth)
        {
            layout.AutoScrollPosition = Point.Empty;
            int leftLabel = MaxLabel("TestingOrganization", "TestPersonnel", "StartName", "StartType", "StartDispatch");
            int rightLabel = MaxLabel("TestLocation", "EndName", "EndType", "EndDispatch");
            string[] info = { "LineName", "InstallationDate", "CableModel" };
            string[] parameters = { "CableLength", "CoreCount", "PhaseVoltage", "TestMode" };
            string[] before = { "A1", "B1", "C1" }, after = { "A2", "B2", "C2" };
            int twoColumnMinimum = Math.Max(720, 85 + 28 + leftLabel + rightLabel
                + 2 * Math.Max(MinimumEditorWidth("StartType", 210), MinimumEditorWidth("EndType", 210)));
            // Keep the main pairs visible at medium widths; compact the denser rows below.
            bool twoColumns = requestedWidth >= twoColumnMinimum;
            int singleLabel = MaxLabel(FieldKeys);
            int singleEditor = 180;
            foreach (string key in FieldKeys)
                singleEditor = Math.Max(singleEditor, MinimumEditorWidth(key, 180));
            int width = twoColumns ? requestedWidth : Math.Max(requestedWidth, Math.Max(500, 85 + singleLabel + singleEditor));
            int sketchWidth = width - 85;
            float sketchScale = Math.Min(1.22f, Math.Max(0.85f, sketchWidth / 900f));
            int sketchHeight = (int)Math.Ceiling((70 + Math.Max(0, sketch.Positions.Length - 1) * 62) * sketchScale);
            sketch.UiScale = sketchScale;
            int sketchTop;
            if (twoColumns)
            {
                float fieldWidth = (width - 85 - 28 - leftLabel - rightLabel) / 2f;
                float rightX = 50 + leftLabel + fieldWidth + 28;
                Place("TestingOrganization", 50, 13, leftLabel, 50 + leftLabel, fieldWidth, 1);
                string[] leftKeys = { "TestPersonnel", "StartName", "StartType", "StartDispatch" };
                string[] rightKeys = { "TestLocation", "EndName", "EndType", "EndDispatch" };
                int[] ys = { 13 + RowPitch, 13 + 2 * RowPitch, 13 + 3 * RowPitch, 13 + 4 * RowPitch };
                for (int i = 0; i < 4; i++)
                {
                    Place(leftKeys[i], 50, ys[i], leftLabel, 50 + leftLabel, fieldWidth, 1);
                    Place(rightKeys[i], rightX, ys[i], rightLabel, rightX + rightLabel, fieldWidth, 1);
                }
                int parametersTop = PlaceFlexibleRows(info, 13 + 5 * RowPitch + 2, width, 130, 3) + 3;
                sketchTop = PlaceFlexibleRows(parameters, parametersTop, width, 90, 4) + 10;
                sketch.SetBounds(50, sketchTop, sketchWidth, sketchHeight);
                int insulationTop = sketchTop + sketchHeight + 2;
                int afterTop = PlaceFlexibleRows(before, insulationTop, width, 128, 3) - 1;
                remarksTop = PlaceFlexibleRows(after, afterTop, width, 110, 3) + 7;
                int noteLabel = LabelWidth("Remarks");
                labels["Remarks"].SetBounds(50, remarksTop, noteLabel, RowHeight);
                frames["Remarks"].SetBounds(50 + noteLabel, remarksTop, width - 85 - noteLabel, 48);
            }
            else
            {
                string[] metadata = {
                    "TestingOrganization", "TestPersonnel", "TestLocation", "StartName", "EndName",
                    "StartType", "EndType", "StartDispatch", "EndDispatch", "LineName",
                    "InstallationDate", "CableModel", "CableLength", "CoreCount", "PhaseVoltage", "TestMode"
                };
                int y = 13;
                foreach (string key in metadata)
                {
                    Place(key, 50, y, singleLabel, 50 + singleLabel, width - 85 - singleLabel, 1);
                    y += RowPitch;
                }
                sketchTop = y + 6;
                sketch.SetBounds(50, sketchTop, sketchWidth, sketchHeight);
                y = sketchTop + sketchHeight + 14;
                string[] resistance = { "A1", "A2", "B1", "B2", "C1", "C2" };
                foreach (string key in resistance)
                {
                    Place(key, 50, y, singleLabel, 50 + singleLabel, width - 85 - singleLabel, 1);
                    y += RowPitch;
                }
                remarksTop = y + 7;
                labels["Remarks"].SetBounds(50, remarksTop, singleLabel, RowHeight);
                frames["Remarks"].SetBounds(50 + singleLabel, remarksTop, width - 85 - singleLabel, 48);
            }
            logicalSize = new Size(width, remarksTop + 56);
            FitRemarks();
            arrangedWidth = requestedWidth;
            layoutDirty = false;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { tips.Dispose(); foreach (Font font in responsiveFonts) font.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

namespace LabVIEWItemInformation
{
    internal sealed class BufferedPanel : Panel
    {
        internal BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }
    internal sealed class ReferenceLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (Brush fill = new SolidBrush(BackColor)) e.Graphics.FillRectangle(fill, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        }
    }
    internal sealed class ReferenceFieldFrame : Panel
    {
        internal float UiScale = 1f;
        int Pixel(float value) { return Math.Max(1, (int)Math.Round(value * UiScale)); }
        internal ReferenceFieldFrame()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Margin = Padding.Empty;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Controls.Count == 0) return;
            Control editor = Controls[0];
            TextBox box = editor as TextBox;
            bool multiline = box != null && box.Multiline;
            int pad = Pixel(4);
            int contentHeight = multiline ? Math.Max(1, Height - 2 * pad) : editor is ComboBox ? editor.PreferredSize.Height : Math.Min(Height - 2, Font.Height + 1);
            editor.SetBounds(pad, multiline ? pad : Math.Max(1, (Height - contentHeight) / 2), Math.Max(1, Width - 2 * pad), Math.Max(1, contentHeight));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (Pen border = new Pen(BrowserTheme.Border, Math.Max(1, 1.5f * UiScale)))
            {
                e.Graphics.DrawRectangle(border, 0.75f, 0.75f, Math.Max(0, Width - 1.5f), Math.Max(0, Height - 1.5f));
                ReferenceComboBox combo = Controls.Count > 0 ? Controls[0] as ReferenceComboBox : null;
                if (combo != null && combo.ReadOnly)
                {
                    Rectangle textRect = new Rectangle(Pixel(4), Pixel(2), Math.Max(1, Width - Pixel(33)), Math.Max(1, Height - Pixel(4)));
                    TextRenderer.DrawText(e.Graphics, combo.Text, Font, textRect, BrowserTheme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    Rectangle button = new Rectangle(Width - Pixel(27), Pixel(3), Pixel(23), Math.Max(1, Height - Pixel(6)));
                    using (Brush fill = new SolidBrush(BrowserTheme.Surface)) e.Graphics.FillRectangle(fill, button);
                    using (Pen edge = new Pen(BrowserTheme.Border)) e.Graphics.DrawRectangle(edge, button);
                    int center = button.Left + button.Width / 2, y = button.Top + button.Height / 2;
                    using (Pen arrow = new Pen(BrowserTheme.Muted, Math.Max(1, 1.8f * UiScale)))
                        e.Graphics.DrawLines(arrow, new Point[] { new Point(center - Pixel(5), y - Pixel(2)), new Point(center, y + Pixel(3)), new Point(center + Pixel(5), y - Pixel(2)) });
                }
            }
        }
    }
    internal sealed class ReferenceComboBox : ComboBox
    {
        internal bool ReadOnly;
        internal ReferenceComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.Normal;
            FlatStyle = FlatStyle.Flat;
            BackColor = BrowserTheme.SurfaceAlt;
            ForeColor = BrowserTheme.Ink;
            ItemHeight = Font.Height;
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e); ItemHeight = Font.Height;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            bool selected = (e.State & DrawItemState.Selected) != 0 && DroppedDown;
            Color back = selected ? BrowserTheme.Primary : BackColor;
            Color fore = selected ? BrowserTheme.Surface : ForeColor;
            using (Brush fill = new SolidBrush(back)) e.Graphics.FillRectangle(fill, e.Bounds);
            string value = e.Index >= 0 && e.Index < Items.Count ? Convert.ToString(Items[e.Index]) : Text;
            TextRenderer.DrawText(e.Graphics, value, Font, e.Bounds, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected override void WndProc(ref Message m)
        {
            if (ReadOnly && (m.Msg == 0x201 || m.Msg == 0x202 || m.Msg == 0x203 || m.Msg == 0x100 || m.Msg == 0x102 || m.Msg == 0x104 || m.Msg == 0x20A)) return;
            base.WndProc(ref m);
        }
    }
}
