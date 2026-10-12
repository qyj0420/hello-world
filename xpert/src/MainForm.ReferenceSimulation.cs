using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private const string ReferenceSimulationProjectName = "人大附中215-金茂府4#212";
        private Panel referenceSimulationStrip;
        private Label referenceSimulationCaption;
        private ComboBox referenceSimulationSource;
        private Label referenceSimulationNotice;
        private Button referenceSimulationDetails;
        private string referenceSimulationFullDescription = String.Empty;
        private ToolTip referenceSimulationTip;
        private Timer referenceSimulationStateTimer;
        private OfflineSimulationBridge referenceSimulationBoundBridge;
        private Control referenceSimulationBoundModule;
        private bool referenceSimulationChanging;
        private bool referenceSimulationLayoutUpdating;

        internal Action<string> ReferenceSimulationWarning { get; set; }

        private void BuildReferenceSimulationStrip(Panel container)
        {
            referenceSimulationStrip = new Panel
            {
                Name = "ReferenceSimulationStrip", Dock = DockStyle.Top,
                BackColor = AppTheme.Surface, Visible = false, Height = 0
            };
            referenceSimulationCaption = new Label
            {
                Name = "ReferenceSimulationCaption", AutoSize = false,
                ForeColor = AppTheme.Navy, Font = AppTheme.Font(9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            referenceSimulationSource = new ComboBox
            {
                Name = "ReferenceSimulationSource", DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = true, Font = AppTheme.Font(9.5f, FontStyle.Regular),
                ForeColor = AppTheme.Navy, BackColor = AppTheme.Surface
            };
            referenceSimulationNotice = new Label
            {
                Name = "ReferenceSimulationNotice", AutoSize = false, AutoEllipsis = false,
                ForeColor = AppTheme.Muted, Font = AppTheme.Font(9f, FontStyle.Regular),
                UseMnemonic = false
            };
            referenceSimulationDetails = AppTheme.Button(String.Empty, false);
            referenceSimulationDetails.Name = "ReferenceSimulationDetails";
            referenceSimulationDetails.Click += delegate
            {
                using (Form dialog = CreateReferenceSimulationDetailsDialog()) dialog.ShowDialog(this);
            };
            referenceSimulationTip = new ToolTip { AutoPopDelay = 20000 };
            referenceSimulationStrip.Controls.Add(referenceSimulationCaption);
            referenceSimulationStrip.Controls.Add(referenceSimulationSource);
            referenceSimulationStrip.Controls.Add(referenceSimulationNotice);
            referenceSimulationStrip.Controls.Add(referenceSimulationDetails);
            referenceSimulationSource.SelectionChangeCommitted += delegate
            {
                TryChangeReferenceSimulationSource(referenceSimulationSource.SelectedIndex == 1);
            };
            referenceSimulationStrip.SizeChanged += delegate { LayoutReferenceSimulationStrip(); };
            container.Controls.Add(referenceSimulationStrip);
            // WinForms docks back-to-front: page title, source strip, then module.
            container.Controls.SetChildIndex(referenceSimulationStrip, 1);
            container.Controls.SetChildIndex(modulePageHeader, 2);
            referenceSimulationStateTimer = new Timer { Interval = 200 };
            referenceSimulationStateTimer.Tick += delegate { UpdateReferenceSimulationAvailability(); };
            referenceSimulationStrip.Disposed += delegate
            {
                UnbindReferenceSimulationSource();
                referenceSimulationStateTimer.Dispose();
                referenceSimulationTip.Dispose();
            };
            ApplyReferenceSimulationLanguage();
        }

        private bool HasReferenceSimulationContext()
        {
            return activeTestSimulationMode && activeModule != null && !activeModule.IsDisposed &&
                LateBound.ReadBoolean(activeModule, false, "SimulationMode") &&
                (String.Equals(activeModule.Tag as string, ModuleHost.CalibrationTest, StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(activeModule.Tag as string, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase));
        }

        private void BindReferenceSimulationSource()
        {
            UnbindReferenceSimulationSource();
            if (!HasReferenceSimulationContext() || offlineSimulationBridge == null) return;
            referenceSimulationBoundBridge = offlineSimulationBridge;
            referenceSimulationBoundModule = activeModule;
            referenceSimulationBoundBridge.DescriptionChanged += ReferenceSimulationDescriptionChanged;
            referenceSimulationStrip.Visible = true;
            ApplyReferenceSimulationLanguage();
            referenceSimulationStateTimer.Start();
        }

        private void UnbindReferenceSimulationSource()
        {
            if (referenceSimulationBoundBridge != null)
                referenceSimulationBoundBridge.DescriptionChanged -= ReferenceSimulationDescriptionChanged;
            referenceSimulationBoundBridge = null;
            referenceSimulationBoundModule = null;
            if (referenceSimulationStateTimer != null) referenceSimulationStateTimer.Stop();
            if (referenceSimulationStrip != null && !referenceSimulationStrip.IsDisposed)
                referenceSimulationStrip.Visible = false;
        }

        private void ReferenceSimulationDescriptionChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !Object.ReferenceEquals(sender, referenceSimulationBoundBridge)) return;
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke(new MethodInvoker(delegate { ReferenceSimulationDescriptionChanged(sender, e); })); }
                catch (InvalidOperationException) { }
                return;
            }
            if (!HasReferenceSimulationContext() || !Object.ReferenceEquals(activeModule, referenceSimulationBoundModule)) return;
            ApplyReferenceSimulationLanguage();
        }

        private void ApplyReferenceSimulationLanguage()
        {
            if (referenceSimulationSource == null || referenceSimulationSource.IsDisposed) return;
            referenceSimulationCaption.Text = IsChinese ? Localize("仿真生成源") : "Generator";
            referenceSimulationDetails.Text = IsChinese ? Localize("查看来源") : "Details";
            referenceSimulationSource.BeginUpdate();
            try
            {
                referenceSimulationSource.Items.Clear();
                referenceSimulationSource.Items.Add(IsChinese ? Localize("合成仿真（默认）") : "Synthetic simulation (default)");
                // This is the reference record's name, not a translated UI string.
                referenceSimulationSource.Items.Add(ReferenceSimulationProjectName + " · " +
                    (IsChinese ? Localize("项目参考仿真") : "Project reference simulation"));
                referenceSimulationSource.SelectedIndex = referenceSimulationBoundBridge != null &&
                    referenceSimulationBoundBridge.ReferenceProfileEnabled ? 1 : 0;
            }
            finally { referenceSimulationSource.EndUpdate(); }
            bool reference = referenceSimulationSource.SelectedIndex == 1;
            referenceSimulationNotice.Text = IsChinese ? Localize("仅供仿真，并非当前项目实测。新采集按当前参数生成，不改写项目参数。")
                : "Simulation only, not measurements of this project. New captures use current settings; project parameters stay unchanged.";
            bool calibration = activeModule != null && String.Equals(activeModule.Tag as string, ModuleHost.CalibrationTest, StringComparison.OrdinalIgnoreCase);
            string origin = calibration ? LateBound.ReadString(activeModule, "SimulationDataOrigin") : String.Empty;
            bool loaded = calibration && LateBound.ReadBoolean(activeModule, false, "HasCalibrationData");
            if (reference)
                referenceSimulationNotice.Text += "\r\n" + (calibration
                    ? (IsChinese ? Localize("参考校准：借鉴 ALL 相形态，电荷及时间轴适配；不是当前相别实测。")
                        : "Reference calibration: ALL-phase shapes, charge/time adapted; not measured on the selected phase.")
                    : (IsChinese ? Localize("参考加压：电压适配、pC 保留；时轴可能估算，零位待核验，无核验定位。")
                        : "Reference pressure: voltage adapted, original pC; estimated time base. Zero unverified; no verified location."));
            else if (loaded)
                referenceSimulationNotice.Text += "\r\n" + (IsChinese ? Localize("已载校准来源：") : "Loaded calibration: ") +
                    DescribeLoadedSimulationOrigin(origin) + (IsChinese ? Localize("；下拉仅选择后续生成源。") : "; the selector controls future captures only.");
            string detail = referenceSimulationBoundBridge == null ? String.Empty : referenceSimulationBoundBridge.SourceDescription;
            referenceSimulationFullDescription = referenceSimulationNotice.Text;
            if (!String.IsNullOrWhiteSpace(detail)) referenceSimulationFullDescription += "\r\n\r\n" + detail.Trim();
            if (loaded && !String.IsNullOrWhiteSpace(origin) && !referenceSimulationFullDescription.Contains(origin))
                referenceSimulationFullDescription += "\r\n\r\n" + (IsChinese ? Localize("已载校准来源：") : "Loaded calibration origin: ") + origin;
            referenceSimulationSource.AccessibleName = referenceSimulationCaption.Text;
            referenceSimulationSource.AccessibleDescription = referenceSimulationFullDescription;
            referenceSimulationNotice.AccessibleName = referenceSimulationNotice.Text;
            referenceSimulationTip.SetToolTip(referenceSimulationSource, referenceSimulationSource.Text);
            referenceSimulationTip.SetToolTip(referenceSimulationNotice, referenceSimulationFullDescription);
            LayoutReferenceSimulationStrip();
            UpdateReferenceSimulationAvailability();
        }

        private void LayoutReferenceSimulationStrip()
        {
            if (referenceSimulationLayoutUpdating || referenceSimulationStrip == null ||
                referenceSimulationStrip.IsDisposed || referenceSimulationStrip.ClientSize.Width < 1) return;
            referenceSimulationLayoutUpdating = true;
            try
            {
                int inset = ScaleLogical(10), gap = ScaleLogical(10), width = Math.Max(1, referenceSimulationStrip.ClientSize.Width - 2 * inset);
                int captionWidth = TextRenderer.MeasureText(referenceSimulationCaption.Text, referenceSimulationCaption.Font,
                    Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + ScaleLogical(4);
                int rowHeight = Math.Max(referenceSimulationSource.PreferredHeight, ScaleLogical(27));
                int detailsWidth = TextRenderer.MeasureText(referenceSimulationDetails.Text, referenceSimulationDetails.Font).Width + ScaleLogical(20);
                int selectorTop = ScaleLogical(5);
                referenceSimulationCaption.SetBounds(inset, selectorTop, captionWidth, rowHeight);
                referenceSimulationSource.SetBounds(inset + captionWidth + gap, selectorTop,
                    Math.Max(1, width - captionWidth - detailsWidth - 2 * gap), rowHeight);
                referenceSimulationDetails.SetBounds(inset + width - detailsWidth, selectorTop, detailsWidth, rowHeight);
                int widestItem = 0;
                foreach (object item in referenceSimulationSource.Items)
                    widestItem = Math.Max(widestItem, TextRenderer.MeasureText(Convert.ToString(item),
                        referenceSimulationSource.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
                referenceSimulationSource.DropDownWidth = Math.Max(referenceSimulationSource.Width,
                    widestItem + SystemInformation.VerticalScrollBarWidth + ScaleLogical(20));
                int noticeTop = selectorTop + rowHeight + ScaleLogical(4);
                Size notice = TextRenderer.MeasureText(referenceSimulationNotice.Text, referenceSimulationNotice.Font,
                    new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                referenceSimulationNotice.SetBounds(inset, noticeTop, width, notice.Height + ScaleLogical(2));
                referenceSimulationStrip.Height = noticeTop + referenceSimulationNotice.Height + ScaleLogical(5);
            }
            finally { referenceSimulationLayoutUpdating = false; }
        }

        private string DescribeLoadedSimulationOrigin(string origin)
        {
            bool reference = (origin ?? String.Empty).IndexOf("REFERENCE[", StringComparison.Ordinal) >= 0;
            bool synthetic = (origin ?? String.Empty).IndexOf("SYNTHETIC[", StringComparison.Ordinal) >= 0;
            if (reference && synthetic) return IsChinese ? Localize("参考与合成混合仿真") : "mixed reference/synthetic simulation";
            if (reference) return IsChinese ? Localize("项目参考仿真") : "project-reference simulation";
            if (synthetic) return IsChinese ? Localize("合成仿真") : "synthetic simulation";
            return IsChinese ? Localize("旧档案未记录明确来源") : "legacy source not recorded";
        }

        private Form CreateReferenceSimulationDetailsDialog()
        {
            var dialog = new Form
            {
                Text = IsChinese ? Localize("仿真来源说明") : "Simulation source details",
                AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                BackColor = AppTheme.Surface, Font = AppTheme.Font(10f, FontStyle.Regular),
                ClientSize = new Size(720, 400), MinimumSize = new Size(480, 300)
            };
            AppTheme.ApplyDialogIcon(dialog);
            AppTheme.FitDialogToWorkingArea(dialog);
            var body = new TextBox
            {
                Name = "SimulationSourceDetailsText", Dock = DockStyle.Fill,
                Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                Text = referenceSimulationFullDescription, BorderStyle = BorderStyle.None,
                BackColor = AppTheme.Surface, ForeColor = AppTheme.Ink, Font = dialog.Font
            };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
            var close = AppTheme.Button(IsChinese ? Localize("关闭") : "Close", true);
            close.AutoSize = true; close.MinimumSize = new Size(110, 38); close.DialogResult = DialogResult.OK;
            footer.Controls.Add(close); dialog.Padding = new Padding(16);
            dialog.Controls.Add(body); dialog.Controls.Add(footer); dialog.AcceptButton = close; dialog.CancelButton = close;
            return dialog;
        }

        private bool ReferenceSimulationModuleIsBusy(Control module)
        {
            if (module == null || module.IsDisposed) return true;
            // Missing or failed state contracts must not permit a source switch.
            try
            {
                foreach (string name in new[] { "IsBusy", "ActionPending" })
                {
                    PropertyInfo property = module.GetType().GetProperty(name);
                    if (property == null || !property.CanRead || property.PropertyType != typeof(bool) ||
                        (bool)property.GetValue(module, null)) return true;
                }
                return false;
            }
            catch (Exception) { return true; }
        }

        private void UpdateReferenceSimulationAvailability()
        {
            if (referenceSimulationSource == null || referenceSimulationSource.IsDisposed) return;
            referenceSimulationSource.Enabled = !referenceSimulationChanging && HasReferenceSimulationContext() &&
                Object.ReferenceEquals(activeModule, referenceSimulationBoundModule) && referenceSimulationBoundBridge != null &&
                !ReferenceSimulationModuleIsBusy(activeModule);
        }

        private bool ConfirmCalibrationBeforeReferenceSwitch()
        {
            Func<DialogResult> previousPrompt = UnsavedCalibrationPrompt;
            try
            {
                if (previousPrompt == null)
                    UnsavedCalibrationPrompt = delegate
                    {
                        if (verificationMode) return DialogResult.Cancel;
                        return MessageBox.Show(this, IsChinese
                            ? Localize("当前校准有未保存的数据或修改。切换仿真来源前是否保存？\r\n\r\n是：保存成功后切换。\r\n否：放弃未保存的修改后切换。\r\n取消：保留当前来源和波形。")
                            : "Calibration data or changes have not been saved. Save before changing the simulation source?\r\n\r\nYes: save, then switch.\r\nNo: discard unsaved changes, then switch.\r\nCancel: keep the current source and waveforms.",
                            IsChinese ? Localize("切换仿真来源") : "Change simulation source", MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                    };
                return CanLeaveActiveCalibration();
            }
            finally { UnsavedCalibrationPrompt = previousPrompt; }
        }

        private bool TryChangeReferenceSimulationSource(bool enabled)
        {
            OfflineSimulationBridge bridge = referenceSimulationBoundBridge;
            Control module = referenceSimulationBoundModule;
            if (referenceSimulationChanging || !HasReferenceSimulationContext() || bridge == null ||
                !Object.ReferenceEquals(bridge, offlineSimulationBridge) || !Object.ReferenceEquals(module, activeModule)) return false;
            if (bridge.ReferenceProfileEnabled == enabled) { ApplyReferenceSimulationLanguage(); return true; }
            referenceSimulationChanging = true;
            try
            {
                UpdateReferenceSimulationAvailability();
                if (ReferenceSimulationModuleIsBusy(module))
                {
                    ShowReferenceSimulationWarning(IsChinese ? Localize("当前操作尚未完成，请等待完成后再切换仿真来源。")
                        : "An operation is still in progress. Wait for it to finish before changing the simulation source.");
                    return false;
                }
                if (!ConfirmCalibrationBeforeReferenceSwitch()) return false;
                // A modal save prompt runs a message loop. Recheck its entire context.
                if (!HasReferenceSimulationContext() || !Object.ReferenceEquals(module, activeModule) ||
                    !Object.ReferenceEquals(bridge, offlineSimulationBridge) ||
                    !Object.ReferenceEquals(bridge, referenceSimulationBoundBridge) || ReferenceSimulationModuleIsBusy(module)) return false;
                bridge.ReferenceProfileEnabled = enabled;
                return bridge.ReferenceProfileEnabled == enabled;
            }
            catch (Exception)
            {
                ShowReferenceSimulationWarning(IsChinese ? Localize("未能切换仿真来源。请确认当前操作已结束，参考数据完整可用后再试。")
                    : "The simulation source could not be changed. Check that the current operation has finished and the reference data is available, then try again.");
                return false;
            }
            finally
            {
                referenceSimulationChanging = false;
                if (!IsDisposed && !Disposing) ApplyReferenceSimulationLanguage();
            }
        }

        private void ShowReferenceSimulationWarning(string message)
        {
            if (ReferenceSimulationWarning != null) ReferenceSimulationWarning(message);
            else if (!verificationMode) MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
