using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xiongxin.OWTS.Localization;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class LanguageItem
    {
        public string Code { get; private set; }
        public string DisplayName { get; private set; }
        public LanguageItem(string code, string displayName) { Code = code; DisplayName = displayName; }
        public override string ToString() { return DisplayName; }
    }

    internal static class AppLanguages
    {
        public static readonly LanguageItem[] All = BuildItems();
        static LanguageItem[] BuildItems()
        {
            var items = new LanguageItem[UiLanguage.Codes.Length];
            for (int i = 0; i < items.Length; i++) items[i] = new LanguageItem(UiLanguage.Codes[i], UiLanguage.Names[i]);
            return items;
        }

        public static LanguageItem[] SelectionItems
        {
            get
            {
                // Preserve every existing locale and its historic module index.
                // A missing caption may fall back, never hide the language.
                return (LanguageItem[])All.Clone();
            }
        }

        public static int SelectionIndex(string code)
        {
            return IndexOf(code);
        }

        public static string ResolveCode(string code)
        {
            return UiLanguage.ResolveCode(code);
        }
        public static bool IsChinese(string code) { return UiLanguage.IsChinese(ResolveCode(code)); }
        public static string Display(string code, string builtInText) { return UiLanguage.Display(ResolveCode(code), builtInText); }

        private sealed class SelectionState { internal bool Attached, ExplicitlySelected; internal int InitialIndex; }
        private static readonly ConditionalWeakTable<ComboBox, SelectionState> languageSelections =
            new ConditionalWeakTable<ComboBox, SelectionState>();

        public static void InitializeSelection(ComboBox selector, string code)
        {
            if (selector == null) throw new ArgumentNullException("selector");
            var state = languageSelections.GetValue(selector, item => new SelectionState());
            if (!state.Attached)
            {
                selector.SelectionChangeCommitted += delegate { state.ExplicitlySelected = true; };
                state.Attached = true;
            }
            selector.SelectedIndex = SelectionIndex(code);
            state.InitialIndex = selector.SelectedIndex;
            state.ExplicitlySelected = false;
        }

        public static string CaptureSelectionCode(ComboBox selector, string originalCode)
        {
            if (selector == null) throw new ArgumentNullException("selector");
            SelectionState state;
            // Opening a beta settings dialog must not convert an existing
            // forty-language preference merely because its caption falls back.
            if (languageSelections.TryGetValue(selector, out state) && !state.ExplicitlySelected &&
                selector.SelectedIndex == state.InitialIndex) return originalCode ?? UiLanguage.SystemCode;
            var selected = selector.SelectedItem as LanguageItem;
            return selected == null ? UiLanguage.SystemCode : selected.Code;
        }

        // Settings dialogs contain program-owned captions but editable values and
        // paths must remain byte-for-byte unchanged. Do not use this on data views.
        public static void TraditionalSettingsCaptions(Control parent, string preference)
        {
            if (IndexOf(preference) == 0) return;
            Action<Control> attach = null;
            attach = delegate(Control item)
            {
                if (item is Label || item is ButtonBase || item is TabPage || item is GroupBox || item is Form)
                {
                    bool updating = false;
                    EventHandler convert = delegate
                    {
                        if (updating) return;
                        updating = true;
                        try { item.Text = UiLanguage.BuiltIn(IndexOf(preference), item.Text); }
                        finally { updating = false; }
                    };
                    convert(item, EventArgs.Empty);
                    item.TextChanged += convert;
                }
                foreach (Control child in item.Controls) attach(child);
                item.ControlAdded += delegate(object sender, ControlEventArgs args) { attach(args.Control); };
            };
            attach(parent);
        }

        private static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "AppTitle", new[] { "振荡波局放检测系统", "DAC partial discharge diagnostic system" } },
            { "Home", new[] { "主界面", "Home" } },
            { "ProjectManagement", new[] { "项目管理", "Project management" } },
            { "ProjectCreation", new[] { "创建项目", "Create project" } },
            { "DataBrowser", new[] { "数据浏览", "Data browser" } },
            { "DataAnalysis", new[] { "数据分析", "Data analysis" } },
            { "ReportGenerator", new[] { "报告生成", "Report generation" } },
            { "PressureTest", new[] { "加压测试", "Voltage test" } },
            { "CalibrationTest", new[] { "校准测试", "Calibration test" } },
            { "WithstandTest", new[] { "耐压试验", "Withstand test" } },
            { "CompetitionDefect", new[] { "AI缺陷识别", "AI defect recognition" } },
            { "CurrentProject", new[] { "当前项目", "Current project" } },
            { "DeviceConnectionStatus", new[] { "设备连接状态", "Device connection status" } },
            { "Settings", new[] { "设置", "Settings" } },
            { "Help", new[] { "帮助", "Help" } },
            { "OpenProject", new[] { "加载项目", "Load project" } },
            { "Back", new[] { "返回主界面", "Back to home" } },
            { "Offline", new[] { "设备离线", "Device offline" } },
            { "Ready", new[] { "设备就绪", "Device ready" } },
            { "NoProject", new[] { "未加载项目", "No project loaded" } },
            { "ProjectLoaded", new[] { "已加载项目", "Project loaded" } },
            { "RefreshDevice", new[] { "管理设备", "Manage devices" } },
            { "ModuleUnavailable", new[] { "模块暂不可用", "Module unavailable" } },
            { "RequiresProject", new[] { "请先加载有效项目", "Load a valid project first" } },
            { "RequiresDevice", new[] { "请先启用并连接指定设备", "Enable and connect the configured device first" } }
        };

        public static int IndexOf(string code)
        {
            code = ResolveCode(code);
            for (int i = 0; i < All.Length; i++)
                if (String.Equals(All[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        public static string Text(string key, string code)
        {
            string[] pair;
            if (!Texts.TryGetValue(key, out pair)) return key;
            return UiLanguage.Text(IndexOf(code), pair[0], pair[1]);
        }
    }
}
