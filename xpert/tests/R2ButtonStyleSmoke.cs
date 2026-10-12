using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class R2ButtonStyleSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Color Canvas = Color.FromArgb(49, 58, 67), Accent = Color.FromArgb(0, 163, 199);
    static int checks;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new InvalidOperationException(text); }
    static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    static Button New(Assembly assembly, string type) { return (Button)Activator.CreateInstance(assembly.GetType(type, true), true); }
    static void Set(object target, string name, object value)
    {
        var t = target.GetType(); var p = t.GetProperty(name, All);
        if (p != null) p.SetValue(target, value, null); else t.GetField(name, All).SetValue(target, value);
    }
    static void Event(Button button, string name, object args) { button.GetType().GetMethod(name, All).Invoke(button, new[] { args }); }
    static Bitmap Paint(Button button, float dpi)
    {
        var image = new Bitmap(button.Width, button.Height); image.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Canvas);
            button.GetType().GetMethod("OnPaint", All).Invoke(button, new object[] { new PaintEventArgs(g, button.ClientRectangle) });
        }
        return image;
    }
    static void Corners(Bitmap image, string name)
    {
        foreach (var p in new[] { new Point(0, 0), new Point(image.Width - 1, 0), new Point(0, image.Height - 1), new Point(image.Width - 1, image.Height - 1) })
            Check(image.GetPixel(p.X, p.Y).ToArgb() == Canvas.ToArgb(), name + " outside corner " + p);
    }
    static void Radius(Bitmap image, float dpi, string name)
    {
        int first = -1;
        int row = name.EndsWith("PressureTestButton", StringComparison.Ordinal) ? 2 : 1;
        for (int x = 0; x < image.Width / 2; x++) if (image.GetPixel(x, row).ToArgb() != Canvas.ToArgb()) { first = x; break; }
        // Check the scaled six-DIP outline envelope. The existing painters use
        // different half-pixel/integer origins and edge antialiasers; do not
        // rewrite those original rectangles to make a single-pixel golden pass.
        int min = dpi <= 96 ? 2 : dpi <= 144 ? 4 : 6;
        int max = dpi <= 96 ? 4 : dpi <= 144 ? 7 : 11;
        Console.WriteLine("RADIUS " + name + " dpi=" + dpi + " first-row-edge=" + first);
        Check(first >= min && first <= max, name + " six-DIP scaled outline, first=" + first);
    }
    static void Configure(Button button)
    {
        button.SetBounds(17, 23, 144, 40); button.Text = "&Apply"; button.TabIndex = 7;
        button.Margin = new Padding(3, 4, 5, 6); button.Padding = new Padding(2, 1, 3, 2);
        button.AutoSize = false; button.TextAlign = ContentAlignment.MiddleLeft;
        button.ImageAlign = ContentAlignment.MiddleRight; button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.UseMnemonic = true; button.UseCompatibleTextRendering = false; button.AutoEllipsis = true;
        button.RightToLeft = RightToLeft.No; button.DialogResult = DialogResult.Cancel;
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(202, 216, 229);
        button.BackColor = Color.White; button.ForeColor = Color.FromArgb(24, 51, 77);
        button.UseVisualStyleBackColor = false;
    }
    static Rectangle InkBox(Bitmap image, Func<Color, bool> match)
    {
        int l = image.Width, t = image.Height, r = -1, b = -1;
        for (int y = 8; y < image.Height - 8; y++) for (int x = 10; x < image.Width - 10; x++) if (match(image.GetPixel(x, y))) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
        return r < l ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, b + 1);
    }
    static void NativeContract(Assembly assembly, string qa, string type = "HVX.Presentation.R2RoundedButton")
    {
        using (var panel = new Panel { BackColor = Canvas })
        using (var native = new Button()) using (var r2 = New(assembly, type))
        using (var icon = new Bitmap(14, 12)) using (var font = new Font("Microsoft YaHei UI", 9.5f))
        {
            using (var g = Graphics.FromImage(icon)) g.Clear(Color.Magenta);
            panel.Controls.Add(native); panel.Controls.Add(r2); Configure(native); Configure(r2); native.Font = r2.Font = font;
            var bounds = r2.Bounds; var margin = r2.Margin; var padding = r2.Padding;
            foreach (FlatStyle flat in new[] { FlatStyle.Standard, FlatStyle.Flat, FlatStyle.Popup })
            foreach (bool auto in new[] { false, true })
            foreach (TextImageRelation relation in new[] { TextImageRelation.Overlay, TextImageRelation.ImageBeforeText, TextImageRelation.TextBeforeImage, TextImageRelation.ImageAboveText, TextImageRelation.TextAboveImage })
            {
                native.FlatStyle = r2.FlatStyle = flat; native.AutoSize = r2.AutoSize = auto;
                native.Image = r2.Image = icon; native.TextImageRelation = r2.TextImageRelation = relation;
                Check(native.GetPreferredSize(Size.Empty) == r2.GetPreferredSize(Size.Empty), "native preferred size retained " + flat + "/" + auto + "/" + relation + " original=" + native.GetPreferredSize(Size.Empty) + " actual=" + r2.GetPreferredSize(Size.Empty));
            }
            native.AutoSize = r2.AutoSize = false; native.SetBounds(17, 23, 144, 40); r2.SetBounds(17, 23, 144, 40);
            foreach (FlatStyle flat in new[] { FlatStyle.Standard, FlatStyle.Flat, FlatStyle.Popup })
            foreach (TextImageRelation relation in new[] { TextImageRelation.Overlay, TextImageRelation.ImageBeforeText, TextImageRelation.TextBeforeImage, TextImageRelation.ImageAboveText, TextImageRelation.TextAboveImage })
            foreach (RightToLeft rtl in new[] { RightToLeft.No, RightToLeft.Yes })
            {
                native.FlatStyle = r2.FlatStyle = flat;
                native.TextImageRelation = r2.TextImageRelation = relation; native.RightToLeft = r2.RightToLeft = rtl;
                using (var original = Paint(native, 96)) using (var actual = Paint(r2, 96))
                {
                    var oldIcon = InkBox(original, c => c.R > 240 && c.G < 20 && c.B > 240);
                    Check(!oldIcon.IsEmpty && oldIcon == InkBox(actual, c => c.R > 240 && c.G < 20 && c.B > 240), "native image rectangle retained " + flat + "/" + relation + "/" + rtl);
                    Check(InkBox(original, c => c.R < 80 && c.G < 90 && c.B < 100) == InkBox(actual, c => c.R < 80 && c.G < 90 && c.B < 100), "native text rectangle retained " + flat + "/" + relation + "/" + rtl);
                }
            }
            r2.Image = null; r2.Text = ""; r2.RightToLeft = RightToLeft.No; r2.FlatStyle = FlatStyle.Flat;
            foreach (float dpi in new[] { 96f, 144f, 192f }) using (var image = Paint(r2, dpi))
            {
                Corners(image, "native adapter " + dpi); Radius(image, dpi, "native adapter"); Check(image.GetPixel(30, 10).ToArgb() == Color.White.ToArgb(), "neutral white fill");
                image.Save(Path.Combine(qa, "shared-neutral-" + dpi + ".png"));
            }
            foreach (var color in new[] { Accent, Color.FromArgb(196, 71, 71), Color.FromArgb(209, 154, 18), Color.FromArgb(43, 154, 104), Color.FromArgb(207, 77, 81) })
            {
                r2.BackColor = color; r2.FlatAppearance.BorderColor = color;
                using (var image = Paint(r2, 96)) Check(image.GetPixel(30, 10).ToArgb() == color.ToArgb(), "configured primary/danger/phase face retained " + color);
            }
            r2.BackColor = Color.White; r2.ForeColor = Color.FromArgb(196, 71, 71); r2.FlatAppearance.BorderColor = r2.ForeColor;
            using (var image = Paint(r2, 96)) Check(image.GetPixel(r2.Width / 2, 0).R > image.GetPixel(r2.Width / 2, 0).B, "danger border stays red");
            r2.Text = "&Apply"; int clicks = 0; r2.Click += (s, e) => clicks++;
            r2.PerformClick(); Check(clicks == 1, "inherited click");
            var mnemonic = typeof(Button).GetMethod("ProcessMnemonic", All);
            Check((bool)mnemonic.Invoke(r2, new object[] { 'A' }) && clicks == 2, "inherited mnemonic");
            r2.Enabled = false; r2.PerformClick(); Check(clicks == 2, "disabled does not activate");
            Check(!(bool)mnemonic.Invoke(r2, new object[] { 'A' }) && clicks == 2, "disabled mnemonic does not activate");
            using (var image = Paint(r2, 96)) Corners(image, "disabled adapter");
            r2.Enabled = true; r2.BackColor = Color.White; r2.ForeColor = Color.FromArgb(24, 51, 77);
            panel.PerformLayout(); int layouts = 0; panel.Layout += (s, e) => layouts++;
            for (int i = 0; i < 100; i++)
            {
                Event(r2, "OnMouseEnter", EventArgs.Empty); Event(r2, "OnMouseLeave", EventArgs.Empty);
                Event(r2, "OnKeyDown", new KeyEventArgs(Keys.Space)); Event(r2, "OnKeyUp", new KeyEventArgs(Keys.Space));
                using (var image = Paint(r2, i % 2 == 0 ? 96 : 144)) { }
            }
            Check(layouts == 0, "paint/hover/key state cause zero parent layouts");
            Check(bounds == r2.Bounds && margin == r2.Margin && padding == r2.Padding && r2.TabIndex == 7 && r2.Font == font && r2.DialogResult == DialogResult.Cancel, "bounds/text layout/focus order/dialog contract retained");
            Check(panel.Controls.Count == 2 && r2.Region == null, "no wrappers/regions inserted");
            r2.Size = new Size(3, 3); using (var tiny = Paint(r2, 384)) Check(tiny.Width == 3, "tiny high-DPI paint stays bounded");
        }
        using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Opacity = 0, ClientSize = new Size(240, 100) }) using (var button = New(assembly, type))
        {
            Configure(button); button.DialogResult = DialogResult.OK; form.Controls.Add(button); form.AcceptButton = button; form.CancelButton = button;
            form.Show(); button.Select(); form.ActiveControl = button;
            Check(form.ActiveControl == button && button.CanSelect, "inherited focus selection");
            button.PerformClick(); Check(form.DialogResult == DialogResult.OK, "inherited AcceptButton/DialogResult");
            form.DialogResult = DialogResult.None;
            var dialogKey = typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
            Check((bool)dialogKey.Invoke(form, new object[] { Keys.Enter }) && form.DialogResult == DialogResult.OK, "inherited Enter activates AcceptButton");
            form.DialogResult = DialogResult.None; button.DialogResult = DialogResult.Cancel;
            Check((bool)dialogKey.Invoke(form, new object[] { Keys.Escape }) && form.DialogResult == DialogResult.Cancel, "inherited Escape activates CancelButton");
            form.DialogResult = DialogResult.None; button.Enabled = false; button.PerformClick(); Check(form.DialogResult == DialogResult.None, "disabled does not complete dialog");
        }
    }
    static void OwnerButton(Assembly assembly, string type, string qa)
    {
        using (var panel = new Panel { BackColor = Canvas }) using (var button = New(assembly, type))
        {
            panel.Controls.Add(button); button.Size = new Size(144, 40); button.Text = "";
            button.FlatAppearance.BorderColor = Color.FromArgb(202, 216, 229);
            var bounds = button.Bounds; var font = button.Font; var margin = button.Margin; var padding = button.Padding;
            foreach (float dpi in new[] { 96f, 144f, 192f }) using (var image = Paint(button, dpi))
            {
                Corners(image, type + "/" + dpi);
                // PressureTestButton already had six-DIP corners and a separate
                // inset shadow before this change. Preserve that geometry;
                // the generic flat outer-edge envelope cannot measure its face.
                if (!type.EndsWith("PressureTestButton", StringComparison.Ordinal)) Radius(image, dpi, type);
                image.Save(Path.Combine(qa, type.Split('.').Last().Replace('+', '-') + "-" + dpi + ".png"));
            }
            button.Text = "&Apply"; int clicks = 0; button.Click += (s, e) => clicks++;
            button.PerformClick(); Check(clicks == 1, type + " inherited click");
            button.Enabled = false; button.PerformClick(); Check(clicks == 1, type + " disabled click blocked");
            using (var disabled = Paint(button, 96)) Corners(disabled, type + " disabled");
            button.Enabled = true; panel.PerformLayout(); int layouts = 0; panel.Layout += (s, e) => layouts++;
            for (int i = 0; i < 30; i++) { Event(button, "OnMouseEnter", EventArgs.Empty); Event(button, "OnMouseLeave", EventArgs.Empty); using (var image = Paint(button, 96)) { } }
            Check(layouts == 0 && bounds == button.Bounds && font == button.Font && margin == button.Margin && padding == button.Padding, type + " redraw does not change geometry or cause parent layout");
        }
    }
    static Assembly LoadFinal(string root, string relative)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Check(File.Exists(path), "actual module exists " + relative);
        Console.WriteLine("PRODUCT_SHA256 " + relative + " " + Hash(path)); return Assembly.LoadFrom(path);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Actual build directory (or --source) and QA directory required.");
            Directory.CreateDirectory(args[1]); bool source = args[0] == "--source";
            if (source)
            {
                var a = Assembly.GetExecutingAssembly(); NativeContract(a, args[1]); NativeContract(a, args[1], "Xiongxin.OWTS.Desktop.AppThemeButton");
                foreach (var t in new[] { "Xiongxin.OWTS.Desktop.AppThemeButton", "HVX.Controls.BrowserButton", "HVX.Controls.InstrumentButton", "HVX.ReportGenerator.ReportButton", "HVX.PdReview.RoundedReviewButton", "HVX.Controls.PressureTestButton" }) OwnerButton(a, t, args[1]);
                Console.WriteLine("SOURCE_ONLY_SCOPE: exact shared/host/browser/report/management/review/pressure painter files; ProjectCreation/JointEditor and module constructor propagation require final-byte test, not silently counted.");
            }
            else
            {
                var modules = new Dictionary<string, Assembly>();
                foreach (var pair in new[] { "Host|Xiongxin.OWTS.Desktop.exe", "ProjectCreation|modules/ProjectCreation/Xiongxin.OWTS.ProjectCreation.dll", "ProjectManagement|modules/ProjectManagement/Xiongxin.OWTS.ProjectManagement.dll", "DataBrowser|modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll", "DataAnalysis|modules/DataAnalysis/HVX.PdReview.V1.0.dll", "ReportGenerator|modules/ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll", "CalibrationTest|modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll", "PressureTest|modules/PressureTest/Xiongxin.OWTS.PressureTest.dll" })
                { var p = pair.Split('|'); modules.Add(p[0], LoadFinal(args[0], p[1])); }
                foreach (var p in modules) { Check(p.Value.GetType("HVX.Presentation.R2RoundedButton", false) != null, "actual shared renderer compiled into " + p.Key); NativeContract(p.Value, args[1]); }
                NativeContract(modules["Host"], args[1], "Xiongxin.OWTS.Desktop.AppThemeButton");
                foreach (var pair in new[] { "Host|Xiongxin.OWTS.Desktop.AppThemeButton", "ProjectCreation|HVX.Controls.ProjectCreationButton", "ProjectManagement|HVX.Controls.InstrumentButton", "DataBrowser|HVX.Controls.BrowserButton", "DataBrowser|HVX.Controls.JointEditorDialog+RoundedEditorButton", "DataAnalysis|HVX.PdReview.RoundedReviewButton", "ReportGenerator|HVX.ReportGenerator.ReportButton", "PressureTest|HVX.Controls.PressureTestButton" })
                { var p = pair.Split('|'); OwnerButton(modules[p[0]], p[1], args[1]); }
                Console.WriteLine("EXACT_FINAL_BYTES=1; REFLECTION_FALLBACK=0; PRODUCT_MAIN_EXECUTED=0");
            }
            Console.WriteLine("PASS " + checks + " checks; hardware=0; SDK=0; user-writes=0; offscreen controls only; no physical DPI/performance claim."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
