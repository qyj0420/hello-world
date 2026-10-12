using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ReferenceSimulationSourceUiSmoke
{
    private static int checks;
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, Hidden).Invoke(owner, args); }
    private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Hidden).GetValue(owner); }
    private static bool Change(MainForm form, bool enabled) { return (bool)Call(form, "TryChangeReferenceSimulationSource", enabled); }

    private sealed class SimulationPage : UserControl
    {
        public bool SimulationMode { get; set; }
        public int LanguageIndex { get; set; }
        public bool IsBusy { get; set; }
        public bool ActionPending { get; set; }
        public bool ActionHandshakeEnabled { get; set; }
        public bool HasUnsavedCalibrationChanges { get; set; }
        public bool HasCalibrationData { get; set; }
        public bool IsCalibrationSavedValid { get; set; }
        public bool SaveResult = true, KeepDirty, FailClear;
        public int SaveCalls, DiscardCalls, ClearCalls;
        public double CableLength = 321.5, Velocity = 168;
        public string ProjectPath = "QA project only";
        public bool ReadPendingAction(out long sequence, out string code, out string json)
        { sequence = 0; code = json = ""; return false; }
        public bool CompleteAction(long sequence, bool success, string error) { return true; }
        public bool SaveCalibrationFile()
        { SaveCalls++; if (SaveResult) { HasUnsavedCalibrationChanges = KeepDirty; IsCalibrationSavedValid = true; } return SaveResult; }
        public void DiscardCalibrationChanges() { DiscardCalls++; HasUnsavedCalibrationChanges = false; }
        public bool ClearSimulationDisplay()
        {
            if (!SimulationMode || IsBusy || ActionPending || HasUnsavedCalibrationChanges || FailClear) return false;
            ClearCalls++; HasCalibrationData = false; IsCalibrationSavedValid = false; return true;
        }
        public void ClearDisplayData() { ClearCalls++; }
    }

    [STAThread] private static int Main(string[] args)
    {
        try
        {
            string release = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            string asset = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "reference-simulation", "rdfz-jmf-v1.json");
            // The test executable and destination are generated inside this QA directory.
            string qaRoot = Path.GetDirectoryName(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(asset).StartsWith(qaRoot, StringComparison.OrdinalIgnoreCase), "asset copy stays in QA output");
            Directory.CreateDirectory(Path.GetDirectoryName(asset));
            File.Copy(Path.Combine(release, "assets", "reference-simulation", "rdfz-jmf-v1.json"), asset, true);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" }) Exercise(language, output);
            RenderRealModules(release, output);
            Console.WriteLine("REFERENCE_SIMULATION_SOURCE_UI_PASS=" + checks + "; HARDWARE_COMMANDS=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Exercise(string language, string output)
    {
        var settings = new AppSettings { LanguageCode = language }; settings.Hardware.Enabled = false;
        using (var form = new MainForm(settings, new ApplicationState(), true))
        {
            form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
            int warnings = 0, prompts = 0;
            DialogResult choice = DialogResult.Cancel;
            form.ReferenceSimulationWarning = text => { warnings++; Check(!String.IsNullOrWhiteSpace(text), "clear switch warning"); };
            form.CalibrationNavigationError = text => Check(!String.IsNullOrWhiteSpace(text), "save failure stays explained");
            form.UnsavedCalibrationPrompt = () => { prompts++; return choice; };
            var page = new SimulationPage { LanguageIndex = language == "en" ? 1 : language == "zh-TW" ? 18 : 0 };
            Call(form, "ShowEmbeddedModule", ModuleHost.CalibrationTest, page, (bool?)true);
            var strip = Field<Panel>(form, "referenceSimulationStrip");
            var source = Field<ComboBox>(form, "referenceSimulationSource");
            var notice = Field<Label>(form, "referenceSimulationNotice");
            var bridge = Field<OfflineSimulationBridge>(form, "offlineSimulationBridge");
            Check(strip.Visible && source.Enabled && source.SelectedIndex == 0 && !bridge.ReferenceProfileEnabled, "calibration SIM defaults to synthetic");
            page.SimulationMode = false;
            Check(!Change(form, true) && !bridge.ReferenceProfileEnabled && page.ClearCalls == 0, "module mode change cannot be bypassed by stale host simulation state");
            page.SimulationMode = true;
            Check(source.Items[1].ToString().StartsWith("人大附中215-金茂府4#212 · "), "source project name preserved verbatim");
            page.IsBusy = true; Call(form, "UpdateReferenceSimulationAvailability");
            Check(!source.Enabled && !Change(form, true) && !bridge.ReferenceProfileEnabled, "busy source change rejected");
            page.IsBusy = false; page.ActionPending = true;
            Check(!Change(form, true) && !bridge.ReferenceProfileEnabled, "pending source change rejected");
            page.ActionPending = false; page.HasCalibrationData = page.HasUnsavedCalibrationChanges = true;
            Check(!Change(form, true) && page.ClearCalls == 0 && page.HasCalibrationData && source.SelectedIndex == 0, "cancel preserves source and waveform");
            choice = DialogResult.Yes; page.SaveResult = false;
            Check(!Change(form, true) && page.ClearCalls == 0 && page.SaveCalls == 1, "failed save cannot switch");
            page.SaveResult = true; page.KeepDirty = true;
            Check(!Change(form, true) && page.ClearCalls == 0, "false clean claim cannot switch");
            page.KeepDirty = false;
            Check(Change(form, true) && bridge.ReferenceProfileEnabled && source.SelectedIndex == 1 && page.ClearCalls == 1, "verified save switches and clears old waveform");
            Check(page.CableLength == 321.5 && page.Velocity == 168 && page.ProjectPath == "QA project only" && page.SimulationMode, "switch keeps geometry project and SIM mode");
            Check(notice.Text.Contains(language == "en" ? "not measurements" : language == "zh-TW" ? "實測" : "实测"), "visible non-measured warning");
            int clearBefore = page.ClearCalls;
            Check(Change(form, true) && page.ClearCalls == clearBefore, "same source does not clear again");
            page.HasCalibrationData = page.HasUnsavedCalibrationChanges = true; choice = DialogResult.No;
            Check(Change(form, false) && page.DiscardCalls == 1 && !bridge.ReferenceProfileEnabled, "explicit discard then synthetic source");
            page.FailClear = true;
            Check(!Change(form, true) && !bridge.ReferenceProfileEnabled && source.SelectedIndex == 0, "clear refusal keeps source selection truthful");
            page.FailClear = false;
            page.HasCalibrationData = page.HasUnsavedCalibrationChanges = true;
            form.UnsavedCalibrationPrompt = () => { page.ActionPending = true; return DialogResult.No; };
            Check(!Change(form, true) && !bridge.ReferenceProfileEnabled, "busy state is rechecked after modal prompt");
            page.ActionPending = false; form.UnsavedCalibrationPrompt = () => choice;
            Check(Change(form, true), "reference source selected for screenshots");
            foreach (Size size in new[] { new Size(1120, 720), new Size(1280, 720), new Size(1650, 1080) })
            {
                form.MinimumSize = Size.Empty; form.Size = size; Application.DoEvents();
                Call(form, "LayoutReferenceSimulationStrip"); Application.DoEvents();
                Check(strip.ClientRectangle.Contains(source.Bounds) && strip.ClientRectangle.Contains(notice.Bounds), "source controls contained " + size);
                int needed = TextRenderer.MeasureText(source.Text, source.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                Check(source.Width >= needed + SystemInformation.VerticalScrollBarWidth + 8, "selected source text uncut " + language + " " + size);
                int measuredHeight = TextRenderer.MeasureText(notice.Text, notice.Font, new Size(notice.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                Check(notice.Height >= measuredHeight && !notice.AutoEllipsis, "all explanation lines visible");
                Panel surface = (Panel)Field<Panel>(form, "modulePanel").Controls["ModuleSurface"];
                Check(surface.Top >= strip.Bottom && strip.Top >= Field<Panel>(form, "modulePageHeader").Bottom, "source strip never overlays module or title");
                form.RenderToPng(Path.Combine(output, "source-" + language + "-" + size.Width + "x" + size.Height + ".png"));
            }
            Exception backgroundError = null;
            var worker = new Thread(() =>
            {
                try { typeof(OfflineSimulationBridge).GetMethod("SetSourceDescription", Hidden).Invoke(bridge, new object[] { "QA async description" }); }
                catch (Exception error) { backgroundError = error; }
            });
            worker.Start(); Check(worker.Join(5000), "description worker completed"); Application.DoEvents();
            Check(backgroundError == null && Field<string>(form, "referenceSimulationFullDescription").Contains("QA async description"), "description event safely marshalled to current UI");
            using (Form details = (Form)Call(form, "CreateReferenceSimulationDetailsDialog"))
            {
                Check(details.Icon != null, "details dialog uses branded icon");
                var text = (TextBox)details.Controls["SimulationSourceDetailsText"];
                Check(text.ReadOnly && text.Multiline && text.Text.Contains("QA async description"), "complete details read-only and retained");
                details.Opacity = 0; details.Show(); Application.DoEvents();
                using (var image = new Bitmap(details.Width, details.Height))
                { details.DrawToBitmap(image, new Rectangle(Point.Empty, details.Size)); image.Save(Path.Combine(output, "details-" + language + ".png")); }
                details.Close();
            }
            Call(form, "ShowHome");
            Check(!strip.Visible && Field<OfflineSimulationBridge>(form, "referenceSimulationBoundBridge") == null, "home unbinds source event and hides strip");
            Call(form, "ReferenceSimulationDescriptionChanged", bridge, EventArgs.Empty);
            Check(!strip.Visible, "late event cannot revive detached strip");
            var pressure = new SimulationPage { LanguageIndex = page.LanguageIndex };
            Call(form, "ShowEmbeddedModule", ModuleHost.PressureTest, pressure, (bool?)true);
            Check(strip.Visible && source.SelectedIndex == 0, "pressure SIM starts with synthetic source independently");
            int previousPrompts = prompts;
            Check(Change(form, true) && pressure.ClearCalls == 1 && prompts == previousPrompts, "pressure source clears its display without calibration prompt");
            Check(notice.Text.Contains(language == "en" ? "Zero unverified" : language == "zh-TW" ? "零位待核驗" : "零位待核验"), "pending zero status not hidden in details");
            Call(form, "ShowHome");
            using (var live = new SimulationPage())
            {
                Call(form, "ShowEmbeddedModule", ModuleHost.PressureTest, live, (bool?)false);
                Check(!strip.Visible && !Change(form, true) && !live.SimulationMode, "live page never receives reference selection");
                Call(form, "ShowHome");
            }
            using (var browser = new Panel())
            {
                Call(form, "ShowEmbeddedModule", ModuleHost.DataBrowser, browser, null);
                Check(!strip.Visible && !Change(form, true), "non-test page has no source strip"); Call(form, "ShowHome");
            }
            Check(warnings >= 3, "busy and failed clear have feedback");
        }
    }

    private static void Set(object target, string property, object value) { target.GetType().GetProperty(property).SetValue(target, value, null); }
    private static object Get(object target, string property) { return target.GetType().GetProperty(property).GetValue(target, null); }
    private static object PublicCall(object target, string method, params object[] args)
    { return target.GetType().GetMethods().Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(target, args); }
    private static void RenderRealModules(string release, string output)
    {
        Assembly.LoadFrom(Path.Combine(release, "modules", "PressureTest", "HVX.PdReview.V1.0.dll"));
        foreach (string id in new[] { ModuleHost.CalibrationTest, ModuleHost.PressureTest })
        foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
        {
            string project = (string)typeof(Xiongxin.OWTS.Desktop.Program).GetMethod("CreateFixtureProject", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { Path.Combine(output, "real-" + id + "-" + language) });
            var state = new ApplicationState(); string error; Check(state.TryLoadProject(project, out error), "real UI QA project loads");
            var settings = new AppSettings { LanguageCode = language }; settings.Hardware.Enabled = false;
            string typeName = id == ModuleHost.CalibrationTest ? "HVX.Controls.CalibrationProgramControl" : "HVX.Controls.PressureTestControl";
            Assembly assembly = Assembly.LoadFrom(Path.Combine(release, "modules", id, "Xiongxin.OWTS." + id + ".dll"));
            using (var page = (Control)Activator.CreateInstance(assembly.GetType(typeName, true)))
            using (var form = new MainForm(settings, state, true))
            {
                Set(page, "ProjectPath", project); Set(page, "LanguageIndex", language == "en" ? 1 : language == "zh-TW" ? 18 : 0); Set(page, "EmbeddedMode", true);
                if (id == ModuleHost.CalibrationTest)
                {
                    Set(page, "SelectedPhaseIndex", -1); Set(page, "SelectedLevelCode", "5nC");
                    PublicCall(page, "SetPhaseParameters", -1, 400d, 170d, false, false);
                }
                else
                {
                    Set(page, "SelectedPhaseCode", "A"); Set(page, "CableLengthMeters", 400d);
                    Set(page, "AdvancedMode", true); Set(page, "PressureProgramModeCode", "CUSTOM");
                    Set(page, "CustomVoltageModeCode", "PEAK"); Set(page, "CustomVoltageValue", 12d);
                }
                form.UnsavedCalibrationPrompt = () => DialogResult.No; form.ReferenceSimulationWarning = message => { throw new InvalidOperationException(message); };
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                Call(form, "ShowEmbeddedModule", id, page, (bool?)true); Check(Change(form, true), "real page source selected");
                if (id == ModuleHost.PressureTest)
                {
                    Set(page, "MaximumPeakVoltageKv", 24d); // Isolated 12 kV fixture keeps the production hard-limit gate.
                    Set(page, "VlfPolarityAvailable", true); // Exercise polarity layout only when the capability is present.
                }
                PublicCall(page, id == ModuleHost.CalibrationTest ? "RequestStart" : "RequestPressure");
                DateTime until = DateTime.UtcNow.AddSeconds(5);
                while ((bool)Get(page, "IsBusy") && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                Check(!(bool)Get(page, "IsBusy") && (bool)Get(page, "LastActionSucceeded"), "actual mailbox request completed: " + Get(page, "LastActionError") +
                    "; action=" + (id == ModuleHost.PressureTest ? Get(page, "LastActionCode") : "calibration") + "; target=" +
                    (id == ModuleHost.PressureTest ? Convert.ToString(Get(page, "CalculatedTargetPeakVoltageKv")) : "calibration"));
                if (id == ModuleHost.CalibrationTest) Check((bool)Get(page, "HasCalibrationData"), "actual reference calibration installed");
                else
                {
                    Check((bool)Get(page, "CurrentAcquisitionIsReferencePreview"), "actual reference pressure installed");
                    Check((double)Get(page, "RequestedTargetPeakVoltageKv") == 12d, "actual nonzero reference request uses 12 kV");
                }
                foreach (Size size in new[] { new Size(1280, 720), new Size(1650, 1080) })
                {
                    form.MinimumSize = Size.Empty; form.Size = size; Application.DoEvents();
                    Panel strip = Field<Panel>(form, "referenceSimulationStrip"); Label notice = Field<Label>(form, "referenceSimulationNotice");
                    int oneLine = TextRenderer.MeasureText("Ag中", notice.Font, Size.Empty, TextFormatFlags.SingleLine).Height;
                    Check(notice.Height <= oneLine * 2 + 5, "real description no more than two lines " + language + " " + size + " height " + notice.Height);
                    form.RenderToPng(Path.Combine(output, "real-" + id + "-" + language + "-" + size.Width + "x" + size.Height + ".png"));
                    Check(strip.Height < 125 && page.Height >= 340, "real waveform area not squeezed by metadata: strip=" + strip.Height + ", page=" + page.Height);
                    if (id == ModuleHost.PressureTest)
                    {
                        var viewport = (Panel)page.Controls.Find("RealtimeSidebarScroll", true).Single();
                        var negative = Field<RadioButton>(page, "negativeRadio");
                        var positive = Field<RadioButton>(page, "positiveRadio");
                        var displayTab = (Button)page.Controls.Find("RealtimeDisplayTab", true).Single();
                        if (displayTab.Visible) { displayTab.PerformClick(); Application.DoEvents(); }
                        foreach (RadioButton radio in new[] { positive, negative })
                        {
                            viewport.ScrollControlIntoView(radio); Application.DoEvents();
                            Rectangle visible = viewport.RectangleToClient(radio.RectangleToScreen(radio.ClientRectangle));
                            Check(viewport.ClientRectangle.Contains(visible), "whole polarity option reachable in sidebar: " + visible);
                            Check(radio.Height >= radio.Font.Height, "polarity text not reduced or vertically clipped");
                        }
                        Check(!viewport.HorizontalScroll.Visible, "sidebar never requires horizontal scroll");
                        form.RenderToPng(Path.Combine(output, "real-PressureTest-sidebar-" + language + "-" + size.Width + "x" + size.Height + ".png"));
                        viewport.AutoScrollPosition = Point.Empty;
                        foreach (string name in new[] { "MetricPdCaption", "MetricCapacitanceCaption", "MetricFrequencyCaption", "MetricTanDeltaCaption" })
                        {
                            var caption = (Label)page.Controls.Find(name, true).Single();
                            int textHeight = TextRenderer.MeasureText(caption.Text, caption.Font,
                                new Size(caption.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                            Check(!caption.AutoEllipsis && caption.Height >= textHeight,
                                "metric name and unit remain complete: " + caption.Text);
                        }
                        var settingsViewport = (Panel)page.Controls.Find("PressureSettingsScroll", true).Single();
                        var numeric = page.Controls.Find("CustomVoltageValueEditor", true).Single();
                        var hint = (Label)page.Controls.Find("CustomVoltageHintLabel", true).Single();
                        foreach (Control item in new Control[] { numeric, hint })
                        {
                            settingsViewport.ScrollControlIntoView(item); Application.DoEvents();
                            Rectangle visible = settingsViewport.RectangleToClient(item.RectangleToScreen(item.ClientRectangle));
                            Check(settingsViewport.ClientRectangle.Contains(visible), "custom voltage field fully reachable: " + item.Name + " " + visible +
                                " viewport=" + settingsViewport.ClientRectangle);
                            Size textSize = TextRenderer.MeasureText(item.Text, item.Font, new Size(item.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                            Check(item.Height >= textSize.Height, "custom voltage text never vertically clipped: " + item.Name);
                        }
                        Check(!settingsViewport.HorizontalScroll.Visible, "pressure settings never need horizontal scroll");
                        form.RenderToPng(Path.Combine(output, "real-PressureTest-voltage-" + language + "-" + size.Width + "x" + size.Height + ".png"));
                        settingsViewport.AutoScrollPosition = Point.Empty;
                    }
                    else
                    {
                        object geometry = page.GetType().GetProperty("CompactLayout", Hidden).GetValue(page, null);
                        RectangleF button = (RectangleF)geometry.GetType().GetField("VelocityReview", Hidden).GetValue(geometry);
                        using (Graphics graphics = page.CreateGraphics())
                        using (var font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular))
                        {
                            string caption = language == "en" ? "Review phase\nvelocity" : language == "zh-TW" ? "核對本相波速" : "核对本相波速";
                            int fitted, lines;
                            graphics.MeasureString(caption, font, new SizeF(button.Width - 8, button.Height - 4), StringFormat.GenericDefault, out fitted, out lines);
                            Check(fitted == caption.Length && lines <= 2 && page.ClientRectangle.Contains(Rectangle.Ceiling(button)), "complete phase velocity review caption");
                        }
                    }
                }
                Check(!Field<string>(form, "referenceSimulationFullDescription").Contains(":\\"), "details contain no source absolute path");
                Call(form, "ShowHome");
            }
        }
    }
}
