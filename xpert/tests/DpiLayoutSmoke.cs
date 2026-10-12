using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class DpiLayoutSmoke
{
    private static int checks;
    private sealed class LayoutOnlyModule : Panel
    {
        public bool HasUnsavedCalibrationChanges { get { return false; } }
        public bool HasCalibrationData { get { return false; } }
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static Rectangle RelativeTo(Control child, Control ancestor)
    {
        Point point = child.Location;
        for (Control parent = child.Parent; parent != null && parent != ancestor; parent = parent.Parent)
            point.Offset(parent.Location);
        return new Rectangle(point, child.Size);
    }

    private static void ScrollIntoView(Control child, Control root)
    {
        var chain = new List<ScrollableControl>();
        for (Control parent = child.Parent; parent != null && parent != root; parent = parent.Parent)
        {
            var scrollable = parent as ScrollableControl;
            if (scrollable != null && scrollable.AutoScroll) chain.Add(scrollable);
        }
        for (int index = chain.Count - 1; index >= 0; index--)
            chain[index].ScrollControlIntoView(child);
        root.PerformLayout();
        Application.DoEvents();
    }

    private static void CheckButtons(Form form, string label)
    {
        foreach (Button button in Descendants(form).OfType<Button>().Where(item => item.Visible && item.Enabled && IsOnSelectedTab(item)))
        {
            ScrollIntoView(button, form);
            Check(form.ClientRectangle.Contains(RelativeTo(button, form)),
                label + " button is reachable and complete: " + button.Text + " " + RelativeTo(button, form));
        }
    }

    private static bool IsOnSelectedTab(Control control)
    {
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
        {
            var page = parent as TabPage;
            var tabs = page == null ? null : page.Parent as TabControl;
            if (tabs != null && tabs.SelectedTab != page) return false;
        }
        return true;
    }

    private static T PrivateControl<T>(object owner, string fieldName) where T : Control
    {
        FieldInfo field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(field != null, "private layout field exists: " + fieldName);
        var value = field.GetValue(owner) as T;
        Check(value != null, "private layout control exists: " + fieldName);
        return value;
    }

    private static void CheckInsideParent(Control control, string label)
    {
        Check(control.Parent != null, label + " has a parent");
        Check(control.Parent.ClientRectangle.Contains(new Rectangle(control.Location, control.Size)),
            label + " remains complete in parent: " + new Rectangle(control.Location, control.Size) +
            " parent=" + control.Parent.ClientRectangle);
    }

    private static void CheckMainChrome(MainForm form, string label)
    {
        var root = PrivateControl<TableLayoutPanel>(form, "rootLayout");
        var header = PrivateControl<Panel>(form, "headerPanel");
        var logo = PrivateControl<PictureBox>(form, "brandLogo");
        var title = PrivateControl<Panel>(form, "titleArea");
        var headerActions = PrivateControl<FlowLayoutPanel>(form, "headerActions");
        var commandActions = PrivateControl<FlowLayoutPanel>(form, "commandActions");
        var commandStatuses = PrivateControl<FlowLayoutPanel>(form, "commandStatuses");
        var projectCard = PrivateControl<Panel>(form, "projectStatusCard");
        var deviceCard = PrivateControl<Panel>(form, "deviceStatusCard");
        var projectStatus = PrivateControl<Label>(form, "projectStatus");
        var projectDetail = PrivateControl<Label>(form, "projectDetail");
        var deviceStatus = PrivateControl<Label>(form, "deviceStatus");
        var deviceDetail = PrivateControl<Label>(form, "deviceDetail");
        var welcome = PrivateControl<Panel>(form, "welcomePanel");
        var welcomeTitle = PrivateControl<Label>(form, "welcomeTitle");
        var welcomeSubtitle = PrivateControl<Label>(form, "welcomeSubtitle");
        var tilePanel = PrivateControl<FlowLayoutPanel>(form, "tilePanel");
        Console.WriteLine("LAYOUT_SUSPEND="+String.Join(",", typeof(Control).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Where(field=>field.Name.IndexOf("Suspend",StringComparison.OrdinalIgnoreCase)>=0).Select(field=>field.Name+":"+field.GetValue(root))));
        Console.WriteLine(label + " dpi=" + form.DeviceDpi + " client=" + form.ClientSize +
            " home=" + tilePanel.Parent.ClientSize + " welcome=" + welcome.Size +
            " tiles=" + tilePanel.ClientSize + " scroll=" + tilePanel.AutoScroll +
            " chromeRows=" + String.Join(",", root.GetRowHeights()) +
            " styles=" + String.Join(",", root.RowStyles.Cast<RowStyle>().Select(row => row.Height.ToString())) +
            " queued=" + typeof(MainForm).GetField("chromeLayoutQueued", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) +
            " builds=" + form.ResponsiveChromeBuildCount +
            " compact=" + typeof(MainForm).GetField("compactDisplayChrome", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) +
            " subtitleVisible=" + PrivateControl<Label>(form,"productSubtitle").Visible +
            " rows=" + String.Join(",", tilePanel.Controls.OfType<ModuleTile>().Select(item => item.Bounds.ToString())));
        if (tilePanel.Visible && form.ClientSize.Width >= 1920 && form.ClientSize.Height >= 1000)
            Check(!tilePanel.VerticalScroll.Visible,
                label + " shows all home tiles without a vertical scrollbar on a large viewport");
        foreach (Control control in new Control[] { root, header, logo, title, headerActions, commandActions, commandStatuses, projectCard, deviceCard })
            CheckInsideParent(control, label + " " + control.GetType().Name);
        Check(!logo.Bounds.IntersectsWith(headerActions.Bounds), label + " logo does not overlap header actions");
        Check(!title.Bounds.IntersectsWith(headerActions.Bounds), label + " title does not overlap header actions");
        Check(!commandActions.Bounds.IntersectsWith(commandStatuses.Bounds), label + " command actions do not overlap statuses");
        Check(!projectCard.Bounds.IntersectsWith(deviceCard.Bounds), label + " status cards do not overlap");
        Check(projectStatus.Bottom <= projectDetail.Top, label + " project status lines do not overlap");
        Check(deviceStatus.Bottom <= deviceDetail.Top, label + " device status lines do not overlap");
        Check(welcomeTitle.Bottom <= welcomeSubtitle.Top, label + " workspace heading and description do not overlap");
        Check(welcomeSubtitle.Bottom <= welcome.ClientSize.Height, label + " workspace description remains inside heading row");
        bool competitionVisible = form.StateSnapshot().Contains("CompetitionDefect:Visible=True");
        var tiles = tilePanel.Controls.OfType<ModuleTile>()
            .Where(item => item.ModuleId != "CompetitionDefect" || competitionVisible).ToArray();
        if (tilePanel.Visible)
        {
            Check(tiles.Length == (competitionVisible ? 9 : 8) && tiles.All(item => item.Visible),
                label + " keeps the configured home tiles visible");
            Check(tilePanel.Controls.OfType<ModuleTile>().Single(item => item.ModuleId == "CompetitionDefect").Visible == competitionVisible,
                label + " optional tile visibility follows its manufacturer setting");
            Check(!tilePanel.HorizontalScroll.Visible, label + " has no horizontal home scrollbar");
            CheckInsideParent(tilePanel, label + " home tile viewport");
            Check(form.ClientRectangle.Contains(RelativeTo(tilePanel, form)), label + " home tile viewport fits in the window");
        }
        for (int index = 0; index < tiles.Length; index++)
        {
            if (tilePanel.Visible)
            {
                // Deliberately undersized screens may scroll vertically. Keep
                // every tile readable and reachable instead of shrinking text.
                ScrollIntoView(tiles[index], form);
                CheckInsideParent(tiles[index], label + " reachable tile " + tiles[index].ModuleId);
            }
            Rectangle titleBounds;
            Rectangle descriptionBounds;
            Rectangle footerBounds;
            tiles[index].GetContentBounds(out titleBounds, out descriptionBounds, out footerBounds);
            Check(tiles[index].ClientRectangle.Contains(titleBounds), label + " tile title fits " + tiles[index].ModuleId);
            Check(tiles[index].ClientRectangle.Contains(footerBounds), label + " tile footer fits " + tiles[index].ModuleId);
            if (tiles[index].CompactMode)
            {
                Check(descriptionBounds.IsEmpty, label + " compact tile hides description " + tiles[index].ModuleId);
                Check(titleBounds.Bottom <= footerBounds.Top,
                    label + " compact tile title and footer remain separate " + tiles[index].ModuleId);
            }
            else
            {
                Check(tiles[index].ClientRectangle.Contains(descriptionBounds), label + " tile description fits " + tiles[index].ModuleId);
                Check(titleBounds.Bottom < descriptionBounds.Top && descriptionBounds.Bottom < footerBounds.Top,
                    label + " tile text regions remain separate " + tiles[index].ModuleId);
            }
            for (int other = index + 1; other < tiles.Length; other++)
                Check(!tiles[index].Bounds.IntersectsWith(tiles[other].Bounds),
                    label + " tiles do not overlap: " + tiles[index].ModuleId + "/" + tiles[other].ModuleId);
        }
    }

    private static void Prepare(Form form, float scale, Size logicalMinimum)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Opacity = 0;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Application.DoEvents();
        // The production manifest has already applied the monitor DPI. Scale only
        // the extra factor needed for a larger requested stress case; multiplying
        // by 1.75 on a 144-DPI monitor incorrectly exercises 262.5% geometry.
        float monitorScale = Math.Max(1f, form.DeviceDpi / 96f);
        float additionalScale = Math.Max(1f, scale / monitorScale);
        if (Math.Abs(additionalScale - 1f) > 0.001f) form.Scale(new SizeF(additionalScale, additionalScale));
        ResponsiveFormLayout.FitToBounds(form, new Rectangle(0, 0, 1366, 728),
            (int)Math.Round(96 * Math.Max(scale, monitorScale)), logicalMinimum);
        form.Location = new Point(-20000, -20000);
        form.PerformLayout();
        Application.DoEvents();
        Check(form.Width <= 1342 && form.Height <= 704,
            "window remains inside 1366x728 working area at scale " + scale + ": " + form.Size);
        Check(form.MinimumSize.Width <= form.Width && form.MinimumSize.Height <= form.Height,
            "minimum remains attainable at scale " + scale + ": min=" + form.MinimumSize + " size=" + form.Size);
    }

    private static void Save(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(path);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output = Path.GetFullPath(args.Length == 0 ? "dpi-layout-smoke" : args[0]);
            Directory.CreateDirectory(output);
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
            {
                string suffix = scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                foreach (bool competition in new[] { false, true })
                using (var main = new MainForm(new AppSettings { EnableCompetitionDefectModule = competition }, new ApplicationState(), true))
                {
                    Prepare(main, scale, new Size(760, 500));
                    CheckMainChrome(main, "main scale=" + suffix + "; competition=" + competition);
                    CheckButtons(main, "main scale=" + suffix);
                    Save(main, Path.Combine(output, "main-" + suffix + (competition ? "-competition" : "") + ".png"));
                }
                if (Math.Abs(scale - 1f) < 0.001f)
                {
                    foreach (int width in new[] { 760, 985, 1120, 1280 })
                    {
                        using (var narrow = new MainForm(new AppSettings(), new ApplicationState(), true))
                        {
                            Prepare(narrow, scale, new Size(760, 500));
                            // Exercise this intentionally narrow viewport even when the
                            // monitor-scaled production minimum is wider than the case.
                            narrow.MinimumSize = Size.Empty;
                            narrow.ClientSize = new Size(width, width == 1120 ? 710 : width >= 1200 ? 720 : 620);
                            narrow.PerformLayout();
                            Application.DoEvents();
                            Check(narrow.ClientSize.Width == width && narrow.Width < width + 64,
                                "narrow viewport uses its requested physical width: " + width);
                            Save(narrow, Path.Combine(output, "main-width-" + width + ".png"));
                            CheckMainChrome(narrow, "main width=" + width);
                            var narrowTiles = PrivateControl<FlowLayoutPanel>(narrow, "tilePanel");
                            Check(!narrowTiles.HorizontalScroll.Visible,
                                "main width=" + width + " keeps home tiles within the viewport width");
                            CheckButtons(narrow, "main width=" + width);
                            Save(narrow, Path.Combine(output, "main-width-" + width + ".png"));
                        }
                    }
                    using (var moduleHost = new MainForm(new AppSettings(), new ApplicationState(), true))
                    {
                        Prepare(moduleHost, scale, new Size(760, 500));
                        moduleHost.ClientSize = new Size(1129, 690);
                        MethodInfo showModule = typeof(MainForm).GetMethod("ShowEmbeddedModule",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Check(showModule != null, "embedded module navigation exists");
                        showModule.Invoke(moduleHost, new object[] { "DataAnalysis", new Panel(), null });
                        moduleHost.PerformLayout();
                        Application.DoEvents();
                        CheckMainChrome(moduleHost, "embedded analysis host");
                        var surface = Descendants(moduleHost).OfType<Panel>()
                            .Single(item => item.Name == "ModuleSurface");
                        Check(surface.ClientSize.Height >= 500,
                            "embedded analysis has at least 500 px height on a 13-inch host: " + surface.ClientSize);
                        Check(surface.ClientSize.Width >= 1000,
                            "embedded analysis retains sufficient width on a 13-inch host: " + surface.ClientSize);
                        Console.WriteLine("MODULE_HOST_1129=" + surface.ClientSize);
                        Save(moduleHost, Path.Combine(output, "module-host-1129.png"));
                    }
                    using (var large = new MainForm(new AppSettings(), new ApplicationState(), true))
                    {
                        large.ShowInTaskbar = false;
                        large.StartPosition = FormStartPosition.Manual;
                        large.Opacity = 0;
                        large.Location = new Point(-20000, -20000);
                        large.Show();
                        large.ClientSize = new Size(1920, 1050);
                        large.PerformLayout();
                        Application.DoEvents();
                        CheckMainChrome(large, "main 1920x1080");
                        Save(large, Path.Combine(output, "main-1920x1080.png"));
                    }
                    foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
                    using (var restoredHome = new MainForm(new AppSettings { LanguageCode = language }, new ApplicationState(), true))
                    {
                        restoredHome.ShowInTaskbar = false; restoredHome.StartPosition = FormStartPosition.Manual;
                        restoredHome.Opacity = 0; restoredHome.Location = new Point(-20000, -20000); restoredHome.Show();
                        restoredHome.MinimumSize = Size.Empty;
                        foreach (Size size in new[] { new Size(1650, 1080), new Size(1120, 720), new Size(1650, 1080) })
                        {
                            restoredHome.MinimumSize = Size.Empty;
                            restoredHome.Size = size; restoredHome.PerformLayout(); Application.DoEvents();
                            Check(restoredHome.Size == size, "resize regression uses the requested physical size: " + size);
                            CheckMainChrome(restoredHome, "home-resize-" + language + "-" + size.Width);
                            if (size.Width == 1650)
                            {
                                var panel = PrivateControl<FlowLayoutPanel>(restoredHome, "tilePanel");
                                foreach (ModuleTile tile in panel.Controls.OfType<ModuleTile>())
                                {
                                    Check(!tile.CompactMode && tile.Height >= 160, "large DPI home retains normal cards and descriptions: " + tile.ModuleId);
                                    Rectangle title, body, footer; tile.GetContentBounds(out title, out body, out footer);
                                    Check(!body.IsEmpty && body.Height >= 36, "large home reserves description region: " + tile.ModuleId);
                                }
                                Check(!panel.VerticalScroll.Visible && !panel.HorizontalScroll.Visible, "large home fits both normal card rows");
                                Save(restoredHome, Path.Combine(output, "home-1650x1080-" + language + ".png"));
                            }
                        }
                    }
                }
                if (Math.Abs(scale - 1.5f) < 0.001f)
                {
                    using (var physical = new MainForm(new AppSettings(), new ApplicationState(), true))
                    {
                        Prepare(physical, scale, new Size(760, 500));
                        physical.Size = new Size(1280, 720);
                        physical.PerformLayout();
                        Application.DoEvents();
                        Check(physical.Size == new Size(1280, 720), "13-inch test uses physical 1280x720 outer bounds");
                        CheckMainChrome(physical, "physical 1280x720");
                        CheckButtons(physical, "physical 1280x720");
                        Save(physical, Path.Combine(output, "main-1280x720-actual-dpi.png"));
                        MethodInfo showModule = typeof(MainForm).GetMethod("ShowEmbeddedModule", BindingFlags.Instance | BindingFlags.NonPublic);
                        MethodInfo showHome = typeof(MainForm).GetMethod("ShowHome", BindingFlags.Instance | BindingFlags.NonPublic);
                        foreach (string id in new[] { "PressureTest", "CalibrationTest", "DataAnalysis" })
                        {
                            // This is a layout-only placeholder, not a simulation-capable
                            // module. Suppress bridge startup without opening any hardware.
                            showModule.Invoke(physical, new object[] { id, new LayoutOnlyModule(), false });
                            physical.PerformLayout(); Application.DoEvents();
                            Check(!PrivateControl<Panel>(physical, "commandBarPanel").Visible, id + " hides the home-only status and refresh strip");
                            CheckMainChrome(physical, "physical module " + id);
                            Save(physical, Path.Combine(output, "module-1280x720-" + id + ".png"));
                            showHome.Invoke(physical, null);
                            physical.PerformLayout(); Application.DoEvents();
                            Check(PrivateControl<Panel>(physical, "commandBarPanel").Visible, id + " restores home status and refresh strip");
                        }
                    }
                    using (var moduleHost = new MainForm(new AppSettings(), new ApplicationState(), true))
                    {
                        Prepare(moduleHost, scale, new Size(760, 500));
                        MethodInfo showModule = typeof(MainForm).GetMethod("ShowEmbeddedModule",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Check(showModule != null, "high-DPI embedded module navigation exists");
                        showModule.Invoke(moduleHost, new object[] { "DataAnalysis", new Panel(), null });
                        moduleHost.PerformLayout();
                        Application.DoEvents();
                        var surface = Descendants(moduleHost).OfType<Panel>()
                            .Single(item => item.Name == "ModuleSurface");
                        // DPI-scaled readable brand/page captions need their full
                        // font height. Reserve at least 70% of the client for the
                        // module instead of enforcing an obsolete 500px constant.
                        Check(surface.ClientSize.Height >= Math.Ceiling(moduleHost.ClientSize.Height * .70),
                            "150% 1366x728 analysis retains 70% client height with unclipped captions: " + surface.ClientSize);
                        Console.WriteLine("MODULE_HOST_1366_150=" + surface.ClientSize);
                        Save(moduleHost, Path.Combine(output, "module-host-1366-150pct.png"));
                    }
                }
                using (var settings = new SettingsForm())
                {
                    Prepare(settings, scale, new Size(760, 520));
                    var tabs = Descendants(settings).OfType<TabControl>().Single();
                    foreach (TabPage page in tabs.TabPages)
                    {
                        tabs.SelectedTab = page;
                        settings.PerformLayout();
                        CheckButtons(settings, "settings scale=" + suffix + " tab=" + page.Text);
                    }
                    Save(settings, Path.Combine(output, "settings-" + suffix + ".png"));
                }
                using (var deviceSettings = new SettingsForm(new AppSettings(), SettingsSection.Device))
                {
                    Prepare(deviceSettings, scale, new Size(760, 520));
                    CheckButtons(deviceSettings, "locked device settings scale=" + suffix);
                    Save(deviceSettings, Path.Combine(output, "settings-device-locked-" + suffix + ".png"));
                }
                using (var firstRun = new FirstRunForm())
                {
                    Prepare(firstRun, scale, new Size(600, 420));
                    CheckButtons(firstRun, "first-run scale=" + suffix);
                    Save(firstRun, Path.Combine(output, "first-run-" + suffix + ".png"));
                }
                using (var help = new HelpForm(new AppSettings()))
                {
                    Prepare(help, scale, new Size(560, 420));
                    CheckButtons(help, "help scale=" + suffix);
                    Save(help, Path.Combine(output, "help-" + suffix + ".png"));
                }
            }
            Console.WriteLine("DPI_LAYOUT_SMOKE_PASS=" + checks);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
