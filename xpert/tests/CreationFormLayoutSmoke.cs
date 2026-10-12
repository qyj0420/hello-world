using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Offline rendering and a temporary QA project only: no weather requests or device calls.
internal static class CreationFormLayoutSmoke
{
    sealed class QaForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { CreateParams value=base.CreateParams;value.ExStyle|=0x08000000;return value; } }
    }
    static readonly List<string> failures = new List<string>();
    static readonly List<string> observations = new List<string>();
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static int checks;
    static string scenario;
    static Control module;
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    static Size NativeClientSize(Control control)
    {
        NativeRect rectangle;
        if (!GetClientRect(control.Handle, out rectangle)) throw new System.ComponentModel.Win32Exception();
        return new Size(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
    }
    static Size ApplyViewport(Form form, Size requested)
    {
        NativeRect window;
        if (!GetWindowRect(form.Handle, out window)) throw new System.ComponentModel.Win32Exception();
        Size before = NativeClientSize(form);
        Size frame = new Size(window.Right - window.Left - before.Width, window.Bottom - window.Top - before.Height);
        Rectangle workArea = Screen.FromControl(form).WorkingArea;
        Size track = SystemInformation.MaxWindowTrackSize;
        Size available = new Size(Math.Max(1, Math.Min(workArea.Width, track.Width) - frame.Width),
            Math.Max(1, Math.Min(workArea.Height, track.Height) - frame.Height));
        Size applied = new Size(Math.Min(requested.Width, available.Width), Math.Min(requested.Height, available.Height));
        // Windows caps top-level windows at MaxWindowTrackSize. If an oversized
        // ClientSize is requested, WinForms may retain that requested value even
        // though the native client and correctly docked child are smaller.
        // Stay within the actual desktop and assert against GetClientRect.
        form.ClientSize = applied;
        form.Location = new Point(workArea.Left + Math.Max(0, (workArea.Width - applied.Width - frame.Width) / 2),
            workArea.Top + Math.Max(0, (workArea.Height - applied.Height - frame.Height) / 2));
        Pump();
        Size native = NativeClientSize(form);
        observations.Add("Requested viewport=" + requested + "; applied viewport=" + applied +
            "; native GetClientRect=" + native + "; managed ClientSize=" + form.ClientSize +
            "; work area=" + workArea + "; MaxWindowTrackSize=" + track + "; nonclient frame=" + frame);
        Check(native == applied, "Native host viewport must match the monitor-contained applied size: " + native + " vs " + applied);
        Check(workArea.Contains(form.Bounds), "Host window must remain inside the current monitor working area: " + form.Bounds);
        return native;
    }
    static object Field(string name) { return module.GetType().GetField(name, Hidden).GetValue(module); }
    static void Set(string name, object value) { module.GetType().GetProperty(name).SetValue(module, value, null); }
    static void Check(bool ok, string message)
    {
        checks++;
        if (!ok) failures.Add(scenario + " | " + message);
    }
    static void Pump()
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(100);
        do { Application.DoEvents(); Thread.Sleep(2); } while (DateTime.UtcNow < until);
    }
    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control grandchild in Descendants(child)) yield return grandchild;
        }
    }
    static string Identity(Control control)
    {
        foreach (FieldInfo field in module.GetType().GetFields(Hidden))
            if (object.ReferenceEquals(field.GetValue(module), control)) return field.Name;
        string text = (control.Text ?? "").Replace("\r", " ").Replace("\n", " ");
        return control.GetType().Name + " " + (text.Length > 46 ? text.Substring(0,46) : text);
    }
    static void InsideAncestors(Control control)
    {
        Rectangle actual = control.RectangleToScreen(control.ClientRectangle);
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
        {
            Rectangle allowed = parent.RectangleToScreen(parent.ClientRectangle);
            // Native controls can have a one-pixel non-client edge.
            allowed.Inflate(1,1);
            Check(allowed.Contains(actual), Identity(control) + " clipped by " + Identity(parent) + ": " + actual + " outside " + allowed);
            if (object.ReferenceEquals(parent, module)) break;
        }
    }
    static void ScrollIntoView(Control control)
    {
        // A short editor deliberately uses nested vertical viewports. Verify
        // that each actionable control can be brought fully into view instead
        // of treating its initial off-screen position as permanent clipping.
        var viewports = new System.Collections.Generic.List<ScrollableControl>();
        for (Control parent = control.Parent; parent != null && parent != module; parent = parent.Parent)
        {
            var viewport = parent as ScrollableControl;
            if (viewport != null && viewport.AutoScroll) viewports.Add(viewport);
        }
        for (int index = viewports.Count - 1; index >= 0; index--)
            viewports[index].ScrollControlIntoView(control);
        Application.DoEvents();
    }
    static void Inspect()
    {
        foreach (Control control in Descendants(module).Where(c => c.Visible))
        {
            ScrollableControl scroll = control as ScrollableControl;
            if (scroll != null) Check(!scroll.HorizontalScroll.Visible, Identity(control) + " has horizontal scrolling");
            if (control is HScrollBar)
            {
                DataGridView grid = control.Parent as DataGridView;
                if (grid != null && Object.ReferenceEquals(grid, Field("cableGrid")))
                {
                    // Six material-aware columns preserve readable widths; only
                    // the joint table may scroll horizontally in a narrow pane.
                    ScrollableControl viewport = grid.Parent.Parent as ScrollableControl;
                    Point originalScroll = viewport == null ? Point.Empty : viewport.AutoScrollPosition;
                    if (viewport != null && viewport.AutoScroll) viewport.ScrollControlIntoView(grid);
                    InsideAncestors(control);
                    DataGridViewColumn last = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).Last();
                    int prior = grid.HorizontalScrollingOffset;
                    grid.HorizontalScrollingOffset = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Sum(c => c.Width);
                    Rectangle cell = grid.GetCellDisplayRectangle(last.Index, -1, false);
                    Check(cell.Left >= 0 && cell.Right <= grid.ClientSize.Width + 1, "Last insulation column fully reachable by table horizontal scroll");
                    grid.HorizontalScrollingOffset = prior;
                    if (viewport != null && viewport.AutoScroll) viewport.AutoScrollPosition = new Point(-originalScroll.X, -originalScroll.Y);
                }
                else Check(false, "Visible horizontal scroll bar " + Identity(control));
            }
            bool input = control is ComboBox || control is NumericUpDown || control is DateTimePicker ||
                (control is TextBox && !(control.Parent is NumericUpDown));
            bool button = control.GetType().Name == "ProjectCreationButton" || control.GetType().Name == "ProjectCreationTabButton";
            bool caption = control is Label && !string.IsNullOrWhiteSpace(control.Text) && control.Parent is FlowLayoutPanel && control.Parent.Parent != null && control.Parent.Parent.Name == "ProjectField";
            if (!input && !button && !caption) continue;
            ScrollIntoView(control);
            InsideAncestors(control);
            Size glyphs = TextRenderer.MeasureText("Ag中", control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            Check(control.Height >= glyphs.Height, Identity(control) + " height " + control.Height + " < glyph height " + glyphs.Height);
            if ((caption || button) && !string.IsNullOrWhiteSpace(control.Text))
            {
                Size text = caption ? control.GetPreferredSize(Size.Empty) : TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Check(control.Width >= text.Width, Identity(control) + " width " + control.Width + " < text " + text.Width);
            }
        }
        foreach (string name in new[] { "saveButton", "loadButton", "clearButton", "exitButton" })
        {
            Control button = (Control)Field(name);
            Check(button.Visible, name + " unavailable");
            InsideAncestors(button);
            Check(button.Width >= TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 24, name + " has no full-caption padding");
        }
        Label required = (Label)Field("validationSummaryLabel");
        Label maximumLength = (Label)Field("maxLengthLabel");
        Check(maximumLength.Width >= TextRenderer.MeasureText(maximumLength.Text,maximumLength.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+maximumLength.Padding.Horizontal,"Maximum length must fit one line");
        if (required.Visible)
        {
            Check(!required.AutoEllipsis, "Required-field hint must not be ellipsized");
            Check(required.Width >= TextRenderer.MeasureText(required.Text, required.Font).Width + required.Padding.Horizontal, "Required-field hint clipped");
        }
        if ((string)module.GetType().GetProperty("ExpandedOptionalSectionCode").GetValue(module, null) == "InsulationAndNotes")
        {
            int language = (int)module.GetType().GetProperty("LanguageIndex").GetValue(module,null);
            foreach (string name in new[] { "insulationA1Text", "insulationA2Text", "insulationB1Text", "insulationB2Text", "insulationC1Text", "insulationC2Text" })
            {
                Control input = (Control)Field(name);
                var field = (TableLayoutPanel)input.Parent.Parent;
                Label caption = field.Controls.OfType<FlowLayoutPanel>().First().Controls.OfType<Label>().First();
                Check(caption.Text.Contains("MΩ"), "Insulation units missing: " + name);
                Check(caption.Text.Contains(language == 0 || language == 18 ? (name.Contains("1") ? "首次" : "二次") : (name.Contains("1") ? "first" : "second")), "Independent measurement caption missing: " + name);
            }
            Label referenceCaption = (Label)Field("insulationReferenceCaption");
            Check(referenceCaption.Visible && referenceCaption.Text == (language == 0 ? "参考标准" : language == 18 ? "參考標準" : "Reference standard"), "Reference language not refreshed");
            Check(((Control)Field("insulationDetailsButton")).Visible, "Reference details action missing");
            Check(((Control)Field("insulationReferenceCombo")).Width > 250, "Reference list too narrow");
        }
        foreach (string name in new[] { "startNameText", "endNameText", "phaseVoltageCombo", "lengthANumber", "lengthBNumber", "lengthCNumber" })
        {
            Control input = (Control)Field(name);
            observations.Add(scenario + " | " + name + "=" + input.Bounds + ", font=" + input.Font.SizeInPoints + ", frame=" + input.Parent.Bounds + ", field=" + input.Parent.Parent.Bounds);
        }
    }
    [STAThread]
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CreationFormLayoutSmoke"));
        Directory.CreateDirectory(output);
        try
        {
            Application.EnableVisualStyles();
            string library = Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll");
            Assembly assembly = Assembly.LoadFrom(library);
            Type type = assembly.GetType("HVX.Controls.ProjectCreationControl",true);
            using (Form form = new QaForm { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(20,20), Text = "Offline creation layout QA" })
            {
                module = (Control)Activator.CreateInstance(type);
                Set("EmbeddedMode", true);
                Set("ShowDialogMessages", false);
                Set("AutoRefreshWeatherOnHardwareSample", false);
                Set("OnlineWeatherEnabled", false);
                Set("DeviceReady", false);
                Set("HardwareAcquisitionAvailable", false);
                Set("DataRootDirectory", output);
                Set("SettingsPath", Path.Combine(output,"qa-settings.ini"));
                module.Dock = DockStyle.Fill;
                form.Controls.Add(module);
                form.ClientSize = new Size(1256,620);
                form.Show(); Pump();
                scenario = "Environment";
                ApplyViewport(form, new Size(1256,620));
                using(Graphics graphics=module.CreateGraphics())
                {
                    Check(graphics.DpiY>=96,"Rendering DPI is supported; actual=" + graphics.DpiY);
                    observations.Add("Actual rendering DPI=" + graphics.DpiY + "; WinForms DeviceDpi=" + module.DeviceDpi);
                }
                Set("StartName", "变电站 050537 / Substation 050537");
                Set("EndName", "终端 050538 / Terminal 050538");
                Set("Organization", "雄新电力科技 / XIONGXIN Power Technology");
                Set("OperatorName", "测试员 / Test operator");
                Set("TestLocation", "北京测试线路 / Test circuit");
                ((ComboBox)Field("cableModeCombo")).SelectedIndex = 0;
                foreach (string n in new[]{"lengthANumber","lengthBNumber","lengthCNumber"}) ((NumericUpDown)Field(n)).Value = 414.125m;
                string[] sections = (string[])type.GetProperty("OptionalSectionCodes").GetValue(module,null);
                foreach (int language in new[]{0,18,1,0})
                foreach (Size size in new[]{new Size(1065,500),new Size(1256,620),new Size(1850,990)})
                {
                    Set("LanguageIndex", language);
                    scenario = "Viewport " + language + " requested " + size;
                    Size actualSize = ApplyViewport(form, size);
                    foreach (string section in sections)
                    {
                        scenario = (language == 0 ? "zh" : language == 18 ? "zh-TW" : "en") + "_" + actualSize.Width + "x" + actualSize.Height + "_" + section;
                        type.GetMethod("SetOptionalSectionExpanded").Invoke(module,new object[]{section,true}); Pump();
                        if(section == "InsulationAndNotes")
                        {
                            Set("InsulationConditionsComparable", true);
                            ((TextBox)Field("insulationA1Text")).Text = "1000";
                            ((TextBox)Field("insulationA2Text")).Text = "900";
                            Set("InsulationReferenceProfile", language == 0 ? "DLT596-2021" : "IEEE400-2023");
                            Pump();
                        }
                        Check(module.ClientSize == NativeClientSize(form), "Module must fill the native host viewport: module=" + module.ClientSize + ", native=" + NativeClientSize(form));
                        Inspect();
                        using (Bitmap bitmap = new Bitmap(module.Width,module.Height))
                        {
                            module.DrawToBitmap(bitmap,module.ClientRectangle);
                            bitmap.Save(Path.Combine(output,scenario + ".png"));
                        }
                    }
                }
                scenario = "19-language viewport";
                ApplyViewport(form, new Size(1065,500));
                for(int language=0;language<19;language++)
                {
                    scenario = "19-language footer " + language;
                    Set("LanguageIndex",language); Pump();
                    foreach(string name in new[]{"saveButton","loadButton","clearButton","exitButton"})
                    {
                        Control action=(Control)Field(name);InsideAncestors(action);
                        Check(action.Width >= TextRenderer.MeasureText(action.Text,action.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+24,"Full caption: "+name);
                        Check(action.Font.SizeInPoints>=9.5f,"Footer must not shrink font: "+name);
                    }
                }
                scenario = "Narrow footer wraps";
                ApplyViewport(form, new Size(720,620));Set("LanguageIndex",5);Pump();
                Control footer = (Control)Field("projectActionButtons");
                Check(((FlowLayoutPanel)footer).WrapContents,"Footer must reflow when space runs out");
                foreach(string name in new[]{"saveButton","loadButton","clearButton","exitButton"})InsideAncestors((Control)Field(name));
                int requiredFooterWidth=footer.Controls.Cast<Control>().Where(c=>c.Visible).Sum(c=>c.Width+c.Margin.Horizontal)+footer.Padding.Horizontal;
                if(requiredFooterWidth>footer.ClientSize.Width)
                    Check(footer.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>c.Top).Distinct().Count()>1,"Captions wrap when measured widths exceed available width");
                else Check(footer.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>c.Top).Distinct().Count()==1,"Captions may share one row when they fit at the actual DPI");
                using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(output,"footer-fr-reflow-720.png"));}
                scenario = "Save event sequencing";
                long before = (long)type.GetProperty("ProjectSavedSequence").GetValue(module,null);
                Set("StartName", "");
                bool failed = !(bool)type.GetMethod("SaveProject").Invoke(module,null);
                Check(failed, "Missing required field must fail save");
                Check(before == (long)type.GetProperty("ProjectSavedSequence").GetValue(module,null), "Failed validation must not signal successful save");
                Set("StartName", "QA start " + Guid.NewGuid().ToString("N"));
                Set("EndName", "QA end");
                Check((bool)type.GetMethod("SaveProject").Invoke(module,null), "QA-only valid project saves successfully");
                Check(before + 1 == (long)type.GetProperty("ProjectSavedSequence").GetValue(module,null), "Only successful save signals host once");
                form.Close();
            }
        }
        catch (Exception e) { failures.Add("FATAL " + e); }
        File.WriteAllLines(Path.Combine(output,"layout-observations.txt"),observations,Encoding.UTF8);
        File.WriteAllLines(Path.Combine(output,"layout-issues.txt"),failures.Distinct(),Encoding.UTF8);
        Console.WriteLine("CREATION_LAYOUT_CHECKS=" + checks);
        Console.WriteLine("CREATION_LAYOUT_ISSUES=" + failures.Distinct().Count());
        Console.WriteLine("EVIDENCE=" + output);
        return failures.Count == 0 ? 0 : 1;
    }
}
