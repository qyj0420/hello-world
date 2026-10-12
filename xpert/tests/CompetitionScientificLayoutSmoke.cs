using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Real, isolated competition host + real viewer; no Program, device, inference or learning is invoked.
// Arguments: staged application directory, QA output parent. Font cases simulate text scaling, not OS DPI.
internal static class CompetitionScientificLayoutSmoke
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly List<string> failures = new List<string>(), notes = new List<string>();
    static readonly int[] modes = { 0, 1, 2, 3, 4, 5, 6, 9, 10, 11, 12 };
    static int checks; static long revision; static Assembly hostAssembly;
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    static void Check(bool condition, string text) { checks++; if (!condition) failures.Add(text); }
    static object Get(object o, string name) { return o.GetType().GetProperty(name, All).GetValue(o, null); }
    static void Set(object o, string name, object value) { o.GetType().GetProperty(name, All).SetValue(o, value, null); }
    static object Field(object o, string name) { return o.GetType().GetField(name, All).GetValue(o); }
    static object Call(object o, string name, params object[] values) { return o.GetType().GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(o, values); }
    static IEnumerable<Control> Descendants(Control root) { yield return root; foreach (Control child in root.Controls) foreach (Control item in Descendants(child)) yield return item; }
    static Control Named(Control root, string name) { return Descendants(root).FirstOrDefault(c => c.Name == name); }
    static void Settle(Control root) { for (int i = 0; i < 4; i++) { root.PerformLayout(); Application.DoEvents(); Thread.Sleep(5); } }
    static bool Complete(object result)
    {
        var task = (Task<bool>)result; var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < 20000) { Application.DoEvents(); Thread.Sleep(3); }
        if (!task.IsCompleted) throw new TimeoutException("Synthetic saved-analysis load did not settle.");
        return task.Result;
    }
    static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""); }
    static void Matrix(string path, double[,] matrix)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        { writer.Write(Encoding.ASCII.GetBytes("HVXMAT01")); writer.Write(matrix.GetLength(0)); writer.Write(matrix.GetLength(1)); foreach (double value in matrix) writer.Write(value); }
    }
    static string Fixture(string qa)
    {
        string project = Path.Combine(qa, "UI布局验证_模拟数据"), archive = Path.Combine(project, "Analysis report", "scientific-layout"); Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(project, "ITEM.in"), "电缆芯数: = \"单相\"\r\n电缆长度: = \"414\"\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(project, "CABLE.in"), "0\tA\r\n0\t始端\t塑料绝缘\r\n80\t接头 1\t塑料绝缘\r\n264.7\t接头 2\t塑料绝缘\r\n414\t终端\t塑料绝缘\r\n", new UTF8Encoding(false));
        var matrix = new double[360, 36];
        for (int r = 0; r < matrix.GetLength(0); r++)
        {
            matrix[r, 0] = 1 + r % 3; matrix[r, 1] = 1; matrix[r, 2] = 8.7 + (r % 4) * 2.1;
            matrix[r, 3] = (r * 17) % 360; matrix[r, 5] = 414; matrix[r, 13] = 80 + r % 3 * 90 + Math.Sin(r) * 12;
            matrix[r, 15] = Math.Sin(r * .27) * 160 + Math.Cos(r * .1) * 40;
            matrix[r, 16] = matrix[r, 15]; matrix[r, 35] = r % 9 == 0 ? 0 : 1;
        }
        Matrix(Path.Combine(archive, "parameters.bin"), matrix); Matrix(Path.Combine(archive, "pdmap.bin"), new double[0, 36]); File.WriteAllText(Path.Combine(archive, "session.json"), "{}");
        var files = new[] { "parameters.bin", "pdmap.bin", "session.json" }.Select(n => new { Path = n, Bytes = new FileInfo(Path.Combine(archive, n)).Length, Sha256 = Hash(Path.Combine(archive, n)) }).ToArray();
        File.WriteAllText(Path.Combine(archive, "analysis.json"), new JavaScriptSerializer().Serialize(new {
            Schema = "HVX.NamedAnalysis", Version = 1, Id = "scientific-layout", Name = "2026-09-29 16-31-17 - 001",
            CreatedUtc = "2026-09-29T08:31:17.0000000Z", SavedUtc = "2026-09-29T08:31:17.0000000Z", SourceRowCount = matrix.GetLength(0),
            Files = files, Charts = new object[0], ExpandedNodes = new string[0], SelectedIdentity = new double[0], DisplayMode = 0, ViewPlane = 0 }), new UTF8Encoding(false));
        return project;
    }
    static object Context(string project, int language)
    {
        object value = Activator.CreateInstance(hostAssembly.GetType("Xiongxin.OWTS.Desktop.CompetitionDefectContext", true), true);
        Set(value, "Protocol", 2); Set(value, "Revision", ++revision); Set(value, "Activation", 1L); Set(value, "ProjectPath", project); return value;
    }
    static object Preferences(int language)
    {
        object value = Activator.CreateInstance(hostAssembly.GetType("Xiongxin.OWTS.Desktop.CompetitionDefectPreferences", true), true);
        Set(value, "LanguageCode", language == 18 ? "zh-TW" : language == 1 ? "en" : "zh-CN"); Set(value, "EnableAiFeatures", true); Set(value, "ModelLibraryPath", ""); return value;
    }
    static Rectangle Relative(Control c, Control ancestor) { return ancestor.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)); }
    static Size TextSize(Control c, bool wrap)
    {
        return TextRenderer.MeasureText(c.Text, c.Font, wrap ? new Size(Math.Max(1, c.ClientSize.Width - c.Padding.Horizontal), Int32.MaxValue) : Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));
    }
    static void CheckText(Control c, string scenario)
    {
        string id = scenario + "/" + c.Name;
        if (c is Button)
        {
            Size text = TextSize(c, false);
            Check(c.Height >= Math.Max(32, text.Height + 10), id + " button comfortable height");
            Check(c.Width >= text.Width + c.Padding.Horizontal + 6, id + " entire single-line button label");
        }
        else if (c is CheckBox)
        {
            var check = (CheckBox)c; Size text = TextSize(c, false);
            int glyph; using (var graphics = c.CreateGraphics()) glyph = CheckBoxRenderer.GetGlyphSize(graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
            if (check.Name == "CompetitionAiEnabled" && !check.AutoSize)
            {
                Size wrapped = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(1, c.Width - c.Padding.Horizontal - glyph - 8), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Check(c.Width >= glyph + 60 && c.Height >= wrapped.Height + c.Padding.Vertical + 4, id + " complete multiline checkbox text"); return;
            }
            Check(c.Height >= text.Height + 4, id + " checkbox label height");
            Check(c.Width >= c.GetPreferredSize(Size.Empty).Width && c.Width >= text.Width + c.Padding.Horizontal + (check.Appearance == Appearance.Button ? 6 : glyph + 3), id + " checkbox label + glyph width (actual " + c.Width + ", text " + text.Width + ", padding " + c.Padding.Horizontal + ", glyph " + glyph + ")");
        }
        else if (c is Label)
        {
            var label = (Label)c;
            // The host explicitly uses a single-line project path with a complete accessible value and tooltip.
            if (label.Name == "CompetitionProjectStatus")
            { Check(label.AccessibleDescription == label.Text && label.AutoEllipsis, id + " long path has full accessible text"); Check(label.Height >= TextSize(c, false).Height, id + " path height"); }
            else
            { Size text = TextSize(c, true); Check(c.Height + 2 >= text.Height + c.Padding.Vertical, id + " complete wrapped label height (actual " + c.Height + ", required " + text.Height + ")"); }
        }
    }
    static void Validate(Form form, Control viewer, string scenario)
    {
        var content = (Panel)Named(viewer, "CompetitionContent"); var viewport = (Panel)Named(viewer, "CompetitionViewport");
        Check(content != null && viewport != null && viewport.AutoScroll, scenario + " scientific workspace scroll fallback"); if (content == null || viewport == null) return;
        foreach (string name in new[] { "CompetitionLoad", "CompetitionRecognize", "CompetitionLearn", "CompetitionUndo", "CompetitionClear", "CompetitionAcceptedOnly", "CompetitionSavedAnalysis", "CompetitionArchiveCaption", "CompetitionStatus", "CompetitionHelp", "CompetitionStatusCaption" })
        {
            var c = Named(viewer, name); Check(c != null && c.Visible, scenario + "/" + name + " exists and visible"); if (c == null) continue;
            Check(c.Width > 0 && c.Height > 0, scenario + "/" + name + " has positive area");
            Rectangle local = c.Bounds;
            Check(local.Left >= -1 && local.Top >= -1 && local.Right <= c.Parent.ClientSize.Width + 1 && local.Bottom <= c.Parent.ClientSize.Height + 1,
                scenario + "/" + name + " fits parent " + local + " within " + c.Parent.ClientSize);
            CheckText(c, scenario);
        }
        var summary=Named(viewer,"CompetitionSummary");Check(summary!=null&&!summary.Visible&&String.IsNullOrEmpty(summary.Text),scenario+" internal work rules are not displayed");
        var combo = (ComboBox)Named(viewer, "CompetitionSavedAnalysis");
        Check(combo.Width >= 120, scenario + " analysis selector retains a usable click/read area");
        var sourceChildren = combo.Parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
        for (int i = 0; i < sourceChildren.Length; i++) for (int j = i + 1; j < sourceChildren.Length; j++) Check(!sourceChildren[i].Bounds.IntersectsWith(sourceChildren[j].Bounds), scenario + " no source-row overlap " + sourceChildren[i].Name + "/" + sourceChildren[j].Name);
        int required = TextSize(combo, false).Width + SystemInformation.VerticalScrollBarWidth + 10;
        Check(combo.Width >= required || (!String.IsNullOrWhiteSpace(combo.AccessibleDescription) && combo.AccessibleDescription.Contains(combo.Text)), scenario + " selected analysis name fits or has complete accessible value (" + combo.Width + "/" + required + ")");
        Control[] cards = content.Controls.Cast<Control>().Where(c => c.Visible).OrderBy(c => c.Top).ToArray();
        for (int i = 1; i < cards.Length; i++) Check(cards[i - 1].Bottom <= cards[i].Top, scenario + " separate non-overlapping workspace rows");
        Check(content.Height <= viewport.ClientSize.Height || viewport.AutoScrollMinSize.Height >= content.Height, scenario + " overflowing content reachable by scroll");
        foreach (string name in new[] { "CompetitionHeadingTitle", "CompetitionHeadingSubtitle", "CompetitionProjectCaption", "CompetitionProjectStatus" }) { var c = Named(form, name); Check(c != null && c.Visible, scenario + "/" + name + " visible"); if (c != null) CheckText(c, scenario); }
        var heading = Named(form, "CompetitionHeading"); var project = Named(form, "CompetitionProjectHeader"); var workspace = Named(form, "CompetitionWorkspace");
        Check(Relative(heading, form).Bottom <= Relative(project, form).Top && Relative(project, form).Bottom <= Relative(workspace, form).Top, scenario + " host branding / project / workspace do not overlap");
        Check(viewer.Width <= workspace.ClientSize.Width && (viewer.Height <= workspace.ClientSize.Height || ((ScrollableControl)workspace).AutoScroll), scenario + " viewer minimum size does not clip below host workspace");
        var settings = Named(form, "CompetitionModuleSettings"); Check(settings != null && settings.Visible, scenario + " independent preferences entry visible");
        if (settings != null)
        {
            CheckText(settings, scenario);
            Check(settings.Left >= 0 && settings.Top >= 0 && settings.Right <= settings.Parent.ClientSize.Width && settings.Bottom <= settings.Parent.ClientSize.Height, scenario + " settings button within heading");
            foreach (string name in new[] { "CompetitionBrand", "CompetitionHeadingTitle", "CompetitionHeadingSubtitle" }) Check(!Relative(settings, form).IntersectsWith(Relative(Named(form, name), form)), scenario + " settings does not overlap " + name);
        }
        var buttonRects = Descendants(viewer).Where(c => c.Visible && (c is Button || c is CheckBox) && c.Name.StartsWith("Competition", StringComparison.Ordinal)).Select(c => new { Control = c, Bounds = Relative(c, viewer) }).ToArray();
        for (int i = 0; i < buttonRects.Length; i++) for (int j = i + 1; j < buttonRects.Length; j++) Check(!buttonRects[i].Bounds.IntersectsWith(buttonRects[j].Bounds), scenario + " no action overlap " + buttonRects[i].Control.Name + "/" + buttonRects[j].Control.Name);
        object spectrum = Field(viewer, "spectrum");
        var tabs = Descendants((Control)spectrum).Where(c => c is Button && c.Name.StartsWith("SpectrumMode", StringComparison.Ordinal)).ToArray();
        Check(tabs.Length == 11, scenario + " eleven analysis mode choices retained");
        var compact=(ComboBox)Field(spectrum,"compactModeChoice");
        if(compact.Visible){Check(compact.Items.Count==11&&compact.SelectedIndex>=0&&modes[compact.SelectedIndex]==(int)Get(spectrum,"DisplayModeIndex"),scenario+" compact selector exposes complete profile and selected mode");Check(compact.Left>=0&&compact.Right<=compact.Parent.ClientSize.Width&&compact.Bottom<=compact.Parent.ClientSize.Height,scenario+" compact mode selector fits toolbar");}
        foreach (Control tab in tabs)
        {
            if(compact.Visible){Check(!tab.Visible,scenario+" compact mode selector replaces hidden native strip");continue;}
            Check(tab.Visible && tab.Left >= 0 && tab.Right <= tab.Parent.ClientSize.Width && tab.Bottom <= tab.Parent.ClientSize.Height, scenario + "/" + tab.Name + " fits mode toolbar");
            CheckText(tab, scenario);
        }
        if(!compact.Visible)Check(tabs.Count(t => !String.IsNullOrEmpty(t.AccessibleDescription)) == 1, scenario + " exactly one selected mode exposed accessibly");
    }
    static void Capture(Form form, string path) { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path); } }
    static void CameraLayout(Control viewer, string scenario)
    {
        object spectrum = Field(viewer, "spectrum"); var plot = (Control)spectrum;
        foreach (int mode in new[] { 5, 6, 12 })
        {
            Set(spectrum, "DisplayModeIndex", mode); Settle(viewer); int[] source = (int[])Get(viewer, "VisibleSourceRowIndices");
            var camera = (Control)Field(spectrum, "cameraToolbar");
            Check(camera.Visible && (bool)Get(spectrum, "ViewPlaneButtonsVisible"), scenario + " camera present in mode " + mode);
            Check(camera.Controls.Cast<Button>().Select(b => b.Text).SequenceEqual(new[] { "XY", "XZ", "ZY", "3D" }), scenario + " all camera plane labels " + mode);
            foreach (int plane in new[] { 1, 2, 3, 0 })
            {
                Call(spectrum, "PerformViewPlaneButtonClick", plane); Settle(viewer);
                Check((int)Get(spectrum, "ViewPlaneIndex") == plane, scenario + " camera switch " + mode + "/" + plane);
                Check(((int[])Get(viewer, "VisibleSourceRowIndices")).SequenceEqual(source), scenario + " view-only camera preserves source identities " + mode + "/" + plane);
                Check(camera.Controls.Cast<Button>().Count(b => b.AccessibleDescription.Length > 0) == 1, scenario + " exactly one selected camera " + mode + "/" + plane);
                foreach (Button button in camera.Controls)
                {
                    Size text = TextSize(button, false);
                    Check(button.Visible && button.Width >= text.Width + 8 && button.Height >= text.Height + 4, scenario + " camera full text " + mode + "/" + button.Text);
                    Check(button.Left >= 0 && button.Right <= camera.Width && button.Bottom <= camera.Height && camera.Right <= plot.Width && camera.Bottom <= plot.Height, scenario + " camera bounds " + mode + "/" + button.Text);
                }
                if ((bool)Get(spectrum, "RegionSelectionEnabled"))
                    foreach (string name in new[] { "SelectionShapeButtonBounds", "SelectionClearButtonBounds" }) Check(!camera.Bounds.IntersectsWith((Rectangle)Get(spectrum, name)), scenario + " ROI button does not overlap camera " + name);
            }
        }
    }
    static void LearningDialog(Control viewer, string scenario, string qa)
    {
        using (var dialog = (Form)Call(viewer, "CreateLearningDialog"))
        {
            Check(dialog.StartPosition == FormStartPosition.CenterParent, scenario + " learning dialog initially centered on owner");
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000); dialog.ShowInTaskbar = false; dialog.ClientSize = new Size(760, 900); dialog.Show(); Settle(dialog);
            foreach (string name in new[] { "CompetitionSampleTitle", "CompetitionSampleNotice", "CompetitionSampleExpand", "CompetitionSampleCustomCaption", "CompetitionSampleOperatorCaption", "CompetitionSampleConfirm", "CompetitionSampleCancel" })
            {
                Control c = Named(dialog, name); Check(c != null && c.Visible, scenario + "/" + name + " visible"); if (c == null) continue;
                CheckText(c, scenario); Check(c.Left >= 0 && c.Right <= c.Parent.ClientSize.Width && c.Bottom <= c.Parent.ClientSize.Height, scenario + "/" + name + " contained by layout row");
            }
            var expand = (CheckBox)Named(dialog, "CompetitionSampleExpand");
            Check(expand != null && expand.Checked, scenario + " expanded learning enabled by default");
            var choices = (CheckedListBox)Named(dialog, "CompetitionSampleLabels");
            Check(choices != null && choices.Items.Count > 0, scenario + " explicit manual label choices present");
            Check(choices.CheckedItems.Count == 0, scenario + " no learning label silently pre-confirmed");
            foreach (object item in choices.Items)
            {
                int textWidth = TextRenderer.MeasureText(item.ToString(), choices.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                Check(textWidth + 30 <= choices.ClientSize.Width || choices.HorizontalScrollbar, scenario + " learning label fully readable or horizontally scrollable: " + item);
            }
            var confirm = (Button)Named(dialog, "CompetitionSampleConfirm"); var cancel = (Button)Named(dialog, "CompetitionSampleCancel");
            Check(dialog.AcceptButton == confirm && dialog.CancelButton == cancel && cancel.DialogResult == DialogResult.Cancel, scenario + " explicit confirm/cancel retained");
            Capture(dialog, Path.Combine(qa, scenario + "-learning-dialog.png"));
            var scroller = Descendants(dialog).OfType<Panel>().FirstOrDefault(p => p.AutoScroll);
            Check(scroller != null, scenario + " long learning dialog supports scrolling");
            if (scroller != null)
            {
                scroller.ScrollControlIntoView(confirm); Settle(dialog);
                var buttonRect = Relative(confirm, scroller);
                Check(buttonRect.Left >= 0 && buttonRect.Right <= scroller.ClientSize.Width && buttonRect.Top >= 0 && buttonRect.Bottom <= scroller.ClientSize.Height, scenario + " confirmation reachable without clipping after scroll");
                Capture(dialog, Path.Combine(qa, scenario + "-learning-dialog-actions.png"));
            }
            // Close without confirmation; CreateLearningDialog itself has no sample commit side effect.
            dialog.Close();
        }
    }
    static void PreferencesDialog(int language, Font font, string scenario, string qa)
    {
        using (var dialog = (Form)Activator.CreateInstance(hostAssembly.GetType("Xiongxin.OWTS.Desktop.CompetitionDefectPreferencesDialog", true), All, null, new[] { Preferences(language) }, null))
        {
            Check(dialog.StartPosition == FormStartPosition.CenterParent, scenario + " independent settings initially centered");
            dialog.Font = font; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000); dialog.ShowInTaskbar = false; dialog.ClientSize = new Size(760, 600); dialog.Show(); Settle(dialog);
            foreach (Size viewportSize in new[] { new Size(760, 600), new Size(620, 330) })
            {
            dialog.ClientSize = viewportSize; Settle(dialog); string sizeCase = scenario + "/preferences-" + viewportSize.Width + "x" + viewportSize.Height;
            foreach (Control c in Descendants(dialog).Where(c => c.Visible && (c is Label || c is Button || c is CheckBox)))
            {
                CheckText(c, sizeCase);
                Check(c.Left >= 0 && c.Top >= 0 && c.Right <= c.Parent.ClientSize.Width && c.Bottom <= c.Parent.ClientSize.Height, sizeCase + " row contains " + c.Text);
            }
            foreach (string name in new[] { "CompetitionLanguage", "CompetitionAiEnabled", "CompetitionModelPath" }) Check(Named(dialog, name) != null && Named(dialog, name).Visible, scenario + " independent field visible: " + name);
            Check(Get(dialog, "Result") == null, scenario + " preferences remain unconfirmed");
            var saveButton = (Control)dialog.AcceptButton;
            for (Control ancestor = saveButton.Parent; ancestor != null; ancestor = ancestor.Parent) { var scroll = ancestor as ScrollableControl; if (scroll != null && scroll.AutoScroll) scroll.ScrollControlIntoView(saveButton); }
            Settle(dialog); Rectangle saveArea = Relative(saveButton, dialog);
            Check(saveArea.Top >= 0 && saveArea.Bottom <= dialog.ClientSize.Height, sizeCase + " save button reachable in physical dialog");
            Capture(dialog, Path.Combine(qa, scenario + "-preferences-" + viewportSize.Width + "x" + viewportSize.Height + ".png"));
            }
            dialog.Close();
        }
    }
    [STAThread] static int Main(string[] args)
    {
        string qa = null; var fonts = new List<Font>();
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: CompetitionScientificLayoutSmoke <build-directory> <qa-parent>");
            // Match app.manifest PerMonitorV2 without changing the user's display settings.
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); string build = Path.GetFullPath(args[0]); qa = Path.Combine(Path.GetFullPath(args[1]), "competition-scientific-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            hostAssembly = Assembly.LoadFrom(Path.Combine(build, "Xiongxin.OWTS.Desktop.exe")); Assembly module = Assembly.LoadFrom(Path.Combine(build, "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
            string project = Fixture(qa); var before = Directory.GetFiles(project, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
            using (var viewer = (Control)Activator.CreateInstance(module.GetType("HVX.PdReview.CompetitionDefectRecognitionControl", true)))
            using (var form = (Form)Activator.CreateInstance(hostAssembly.GetType("Xiongxin.OWTS.Desktop.CompetitionDefectHostForm", true), All, null, new object[] { Context(project, 0), (Func<Control>)(() => viewer), Preferences(0), Path.Combine(qa, "preferences.json") }, null))
            {
                Check(form.WindowState == FormWindowState.Maximized, "host initially maximized");
                form.WindowState = FormWindowState.Normal; form.MaximumSize = new Size(4096, 4096); form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false; form.ClientSize = new Size(1280, 900); form.Show(); Settle(form);
                Check(Complete(Call(viewer, "LoadSavedAnalysisAsync", "scientific-layout")), "synthetic saved analysis loaded"); Check((int)Get(viewer, "SourceRowCount") == 360, "real 360-row synthetic matrix loaded into plot");
                object spectrum = Field(viewer, "spectrum"); Check(((int[])Call(spectrum, "GetVisibleModeIndices")).SequenceEqual(modes) && (int)Get(spectrum, "ModeButtonCount") == 11, "complete requested eleven-mode analysis profile");
                foreach (int language in new[] { 0, 1, 18 })
                {
                    Call(form, "ApplyPreferences", Preferences(language), false);
                    Check((int)Get(viewer, "SourceRowCount") == 360, "independent language change retains loaded synthetic source");
                    foreach (float size in new[] { 9.5f, 14f, 18f })
                    {
                        var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular); fonts.Add(font);
                        form.Font = font; viewer.Font = font;
                        foreach (int width in new[] { 760, 1280, 1920 })
                        {
                            form.ClientSize = new Size(width, width == 1920 ? 1080 : 900); Settle(form);
                            string scenario = "lang-" + language + "-font-" + size.ToString("0.0", CultureInfo.InvariantCulture) + "-width-" + width;
                            Rect native; GetClientRect(form.Handle, out native);
                            Check(form.ClientSize.Width == width && native.Right - native.Left == width, scenario + " requested/native viewport width");
                            notes.Add(scenario + " client=" + form.ClientSize + " native=" + (native.Right - native.Left) + "x" + (native.Bottom - native.Top) + "; font scaling simulation, not OS DPI");
                            Validate(form, viewer, scenario);
                            CameraLayout(viewer, scenario);
                            Set(spectrum, "DisplayModeIndex", width == 760 ? 11 : 12); Settle(form); Capture(form, Path.Combine(qa, scenario + ".png"));
                            if (width == 760 && size == 18f)
                            {
                                var scroll = (Panel)Named(viewer, "CompetitionViewport"); scroll.ScrollControlIntoView((Control)spectrum); Settle(form);
                                var modeRow = Named((Control)spectrum, "SpectrumToolbar"); scroll.ScrollControlIntoView(modeRow); Settle(form);
                                var rowRect = Relative(modeRow, scroll);
                                Check(rowRect.Left >= 0 && rowRect.Right <= scroll.ClientSize.Width && rowRect.Top >= 0 && rowRect.Bottom <= scroll.ClientSize.Height, scenario + " wrapped spectrum tabs reachable after scroll");
                                Capture(form, Path.Combine(qa, scenario + "-plot-scrolled.png")); scroll.AutoScrollPosition = Point.Empty; Settle(form);
                            }
                        }
                        LearningDialog(viewer, "lang-" + language + "-font-" + size.ToString("0.0", CultureInfo.InvariantCulture), qa);
                        PreferencesDialog(language, font, "lang-" + language + "-font-" + size.ToString("0.0", CultureInfo.InvariantCulture), qa);
                        form.ClientSize = new Size(760, 600); Settle(form);
                        string shortCase = "lang-" + language + "-font-" + size.ToString("0.0", CultureInfo.InvariantCulture) + "-760x600";
                        Validate(form, viewer, shortCase);
                        Control tabRow = Named((Control)spectrum, "SpectrumToolbar");
                        foreach (Control tab in Descendants(tabRow).Where(c => c.Visible && (c is Button || c is CheckBox)))
                        {
                            for (Control ancestor = tab.Parent; ancestor != null; ancestor = ancestor.Parent) { var scroll = ancestor as ScrollableControl; if (scroll != null && scroll.AutoScroll) scroll.ScrollControlIntoView(tab); }
                            Settle(form); Rectangle reachable = Relative(tab, form);
                            Check(reachable.Top >= 0 && reachable.Bottom <= form.ClientSize.Height, shortCase + " individual spectrum control reachable within physical host: " + tab.Name);
                        }
                        Capture(form, Path.Combine(qa, shortCase + "-scrolled.png"));
                        foreach (var scroll in Descendants(form).OfType<ScrollableControl>().Where(c => c.AutoScroll)) scroll.AutoScrollPosition = Point.Empty;
                    }
                    foreach (int mode in modes)
                    {
                        Set(spectrum, "DisplayModeIndex", mode); Settle(form);
                        Check((int)Get(spectrum, "DisplayModeIndex") == mode && (bool)Get(spectrum, "SelfCheckPassed"), "language " + language + " mode " + mode + " selected and valid");
                        var buttons = Descendants((Control)spectrum).Where(c => c is Button && c.Name.StartsWith("SpectrumMode", StringComparison.Ordinal)).ToArray();
                        Check(buttons.Length == 11, "language " + language + " eleven mode choices retained");
                        var compact=(ComboBox)Field(spectrum,"compactModeChoice");
                        if(compact.Visible)Check(compact.Items.Count==11&&modes[compact.SelectedIndex]==mode,"language "+language+" compact selector follows selected mode");
                        foreach (var button in buttons) { Check(compact.Visible?!button.Visible:button.Visible && button.Right <= button.Parent.ClientSize.Width + 1 && button.Bottom <= button.Parent.ClientSize.Height + 1, "language " + language + " tab " + button.Name + " in toolbar or compact selector"); }
                    }
                }
                Set(viewer, "LanguageIndex", 1); Check(Named(viewer, "CompetitionLoad").Text == "Load analysis", "English source action");
                Set(viewer, "LanguageIndex", 18); Check(Named(viewer, "CompetitionLoad").Text.Contains("載"), "Traditional Chinese source action");
                Call(form, "ApplyPreferences", Preferences(0), false); var finalFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular); fonts.Add(finalFont); form.Font = finalFont; viewer.Font = finalFont;
                form.ClientSize = new Size(1920, 1080); Settle(form);
                foreach (int mode in new[] { 11, 5, 6, 12 })
                {
                    Set(spectrum, "DisplayModeIndex", mode);
                    foreach (int plane in mode == 11 ? new[] { 0 } : new[] { 0, 1 })
                    { Set(spectrum, "ViewPlaneIndex", plane); Settle(form); Capture(form, Path.Combine(qa, "final-zh-1920-mode-" + mode + "-plane-" + plane + ".png")); }
                }
                Call(form, "MarkDisconnected"); Settle(form); Check(!form.IsDisposed && (int)Get(viewer, "SourceRowCount") == 360, "disconnected parent retains independent window and current analysis");
                foreach (int language in new[] { 0, 1, 18 })
                { Call(form, "ApplyPreferences", Preferences(language), false); form.ClientSize = new Size(760, 900); Settle(form); Validate(form, viewer, "disconnected-lang-" + language); Capture(form, Path.Combine(qa, "disconnected-lang-" + language + ".png")); }
            }
            Check(before.All(item => File.Exists(item.Key) && Hash(item.Key) == item.Value), "all synthetic input files remain byte-identical");
            Check(!File.Exists(Path.Combine(qa, "preferences.json")), "layout test never persisted preferences");
            File.WriteAllLines(Path.Combine(qa, "layout-results.txt"), new[] { "Checks=" + checks, "Failures=" + failures.Count, "Only text scaling is simulated; native viewport size is recorded below." }.Concat(notes).Concat(failures.Select(f => "FAIL " + f)), Encoding.UTF8);
            if (failures.Count > 0) { foreach (string failure in failures) Console.Error.WriteLine(failure); Console.Error.WriteLine("FAIL " + failures.Count + " / " + checks + "; QA " + qa); return 1; }
            Console.WriteLine("PASS " + checks + " scientific layout checks; QA " + qa); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); if (qa != null) Console.Error.WriteLine("QA " + qa); return 1; }
        finally { foreach (Font font in fonts) font.Dispose(); }
    }
}
