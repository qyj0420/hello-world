using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // Capture once, before any project, measurement or user input is loaded.
        // Never run script conversion over live arbitrary control text.
        readonly Dictionary<Control,string> pressureBuiltInText=new Dictionary<Control,string>();
        readonly Dictionary<Control,string> pressureBuiltInAccessibleName=new Dictionary<Control,string>();
        static readonly Dictionary<string,string> sidebarEnglish=new Dictionary<string,string>{
            {"OWTS  加压测试","OWTS  Voltage test"},
            {"2026-06-13 21:36   ·   测试-测试   ·   电缆长度 175.4 m","2026-06-13 21:36 · Test-Test · Cable length 175.4 m"},
            {"局放与电压波形","PD and voltage waveform"},{"谱图分析","Spectrum analysis"},
            {"∿  局放与电压波形","∿  PD and voltage waveform"},{"▦  谱图分析","▦  Spectrum analysis"},{"详情","Details"},
            {"最长时间","Longest duration"},{"水平分隔条","Horizontal divider"},{"垂直分隔条","Vertical divider"},
            {"局放最大值 / pC","Max PD / pC"},{"等效电容 / μF","Capacitance / μF"},{"振荡频率 / Hz","Frequency / Hz"},{"介损 / %","Tan delta / %"},
            {"实时指标","Metrics"},{"显示设置","Display"},{"显示周期","Display cycles"},{"超低频加压极性","VLF polarity"},{"正极性","Positive"},{"负极性","Negative"},
            {"使用补偿电容","Use compensation"},{"更多指标","More metrics"},
            {"自定义","Custom"},{"计划","Plan"},{"模式","Mode"},{"量程","Range"},
            {"加压设置","Voltage"},{"模式与量程","Mode / range"},{"计划加压","Plan"},{"自定义加压","Custom"},{"电压设置","Voltage setting"},{"有效值","RMS"},{"峰值","Peak"},
            {"加压模式","Voltage mode"},{"自动加压","Automatic"},{"手动加压","Manual"},{"量程选择","Range mode"},{"自动量程","Auto range"},{"手动量程","Manual range"},{"局放量程","PD range"},
            {"删除","Delete"},{"自动保存","Auto save"},{"A相","A"},{"B相","B"},{"C相","C"}};
        void CapturePressureBuiltInText(Control parent)
        {
            foreach(Control child in parent.Controls)
            {
                // This subtree translates its own dynamic captions/tooltips.
                // Captured Chinese defaults must not overwrite that translation.
                if(Object.ReferenceEquals(child,spectrumDashboardView))continue;
                if((child is Label||child is ButtonBase)&&ContainsChinese(child.Text))pressureBuiltInText[child]=child.Text;
                if(ContainsChinese(child.AccessibleName))pressureBuiltInAccessibleName[child]=child.AccessibleName;
                CapturePressureBuiltInText(child);
            }
        }
        static bool ContainsChinese(string text)
        {foreach(char c in text??"")if(c>='\u4e00'&&c<='\u9fff')return true;return false;}
        void ApplyPressureBuiltInLanguage()
        {
            foreach(var entry in pressureBuiltInText)
                if(!entry.Key.IsDisposed){string english;entry.Key.Text=LanguageIndex==18?Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(entry.Value):LanguageIndex==0?entry.Value:sidebarEnglish.TryGetValue(entry.Value,out english)?Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex,entry.Value,english):Xiongxin.OWTS.Localization.UiLanguage.BuiltIn(LanguageIndex,entry.Value);}
            foreach(var entry in pressureBuiltInAccessibleName)
                if(!entry.Key.IsDisposed){string english;entry.Key.AccessibleName=LanguageIndex==18?Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(entry.Value):LanguageIndex==0?entry.Value:sidebarEnglish.TryGetValue(entry.Value,out english)?Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex,entry.Value,english):Xiongxin.OWTS.Localization.UiLanguage.BuiltIn(LanguageIndex,entry.Value);}
            UpdateMetricStatusVisuals();
            RefreshPlanGrid();
            RefreshPressureSidebarLayout();
            if(waveformView!=null){waveformView.LanguageIndex=LanguageIndex;waveformView.Invalidate();}
            ApplyPressureScrollbarLanguage(this);
        }

        void ApplyPressureScrollbarLanguage(Control parent)
        {
            // Native grid scrollbars are created after the initial caption capture.
            // Own their display labels explicitly; never convert arbitrary input text.
            foreach(Control child in parent.Controls)
            {
                if(child is HScrollBar)child.AccessibleName=PressureUiText("水平滚动条","Horizontal scrollbar");
                else if(child is VScrollBar)child.AccessibleName=PressureUiText("垂直滚动条","Vertical scrollbar");
                ApplyPressureScrollbarLanguage(child);
            }
        }
    }
}
