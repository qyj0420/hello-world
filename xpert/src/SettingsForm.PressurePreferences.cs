using System;
using System.Drawing;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    internal partial class SettingsForm
    {
        NumericUpDown frequencyReminderMin, frequencyReminderMax, frequencyDangerThreshold;
        NumericUpDown capacitanceNormalMin, capacitanceNormalMax, pressureCountdownSeconds, locationSaveCycles;
        NumericUpDown compensationCapacitance;
        NumericUpDown locationSuspiciousMinimum,locationManualMinimum,locationAutomaticMinimum;
        bool linkingLocationScoreLimits;
        CheckBox useCompensationCapacitance;
        CheckBox frequencyReminderCheck, pressureCountdownCheck;
        CheckBox projectCreationGuideCheck;
        CheckBox analyzeZeroVoltageData, automaticRangeReminder;

        void AddGeneralPressureRows(TableLayoutPanel grid)
        {
            Func<string,string,string> L=(zh,en)=>zh;
            frequencyReminderMin = GeneralNumber("FrequencyReminderMinHz", .001m, 1000000, 3, 1);
            frequencyReminderMax = GeneralNumber("FrequencyReminderMaxHz", .001m, 1000000, 3, 1);
            frequencyDangerThreshold = GeneralNumber("FrequencyDangerThresholdHz", .001m, 1000000, 3, 1);
            capacitanceNormalMin = GeneralNumber("CapacitanceNormalMinUf", .000001m, 1000000, 6, .01m);
            capacitanceNormalMax = GeneralNumber("CapacitanceNormalMaxUf", .000001m, 1000000, 6, .01m);
            pressureCountdownSeconds = GeneralNumber("AutomaticPressureCountdownSeconds", 1, 10, 0, 1);
            locationSaveCycles = GeneralNumber("LocationSaveCycleCount", 1, 5, 0, 1);
            compensationCapacitance = GeneralNumber("CompensationCapacitanceUf", 0, 1000000, 6, .01m);
            useCompensationCapacitance = new CheckBox { Name="UseCompensationCapacitance", Text=L("使用补偿电容（显示电容 = 原始电容 − 补偿电容）","Use compensation capacitance (display = raw − compensation)"), AutoSize=false };
            frequencyReminderCheck = new CheckBox { Name="FrequencyReminderEnabled", Text=L("显示振荡频率异常提醒（重新勾选可恢复）","Show abnormal frequency reminder (check to restore)"), AutoSize=false };
            pressureCountdownCheck = new CheckBox { Name="AutomaticPressureCountdownEnabled", Text=L("显示自动加压倒计时（重新勾选可恢复）","Show automatic pressure countdown (check to restore)"), AutoSize=false };
            AddRow(grid, L("振荡频率正常下限 / Hz","Frequency normal minimum / Hz"), frequencyReminderMin);
            AddRow(grid, L("振荡频率正常上限 / Hz","Frequency normal maximum / Hz"), frequencyReminderMax);
            AddRow(grid, L("振荡频率红色警告阈值 / Hz","Frequency danger threshold / Hz"), frequencyDangerThreshold);
            AddRow(grid, L("电容正常下限 / μF","Capacitance normal minimum / μF"), capacitanceNormalMin);
            AddRow(grid, L("电容正常上限 / μF","Capacitance normal maximum / μF"), capacitanceNormalMax);
            AddRow(grid, L("补偿电容 / μF（200 nF = 0.2）","Compensation / μF (200 nF = 0.2)"), compensationCapacitance);
            AddWideRow(grid, useCompensationCapacitance);
            AddWideRow(grid, frequencyReminderCheck);
            AddRow(grid, L("自动加压倒计时 / 秒（1–10）","Automatic pressure countdown / s (1–10)"), pressureCountdownSeconds);
            AddWideRow(grid, pressureCountdownCheck);
            AddRow(grid, L("定位保存周期数（整数 1–5，默认 1）","Location save cycles (integer 1–5, default 1)"), locationSaveCycles);
            locationSuspiciousMinimum=GeneralNumber("LocationSuspiciousMinimumPercent",0,100,2,1);
            locationManualMinimum=GeneralNumber("LocationManualAcceptanceMinimumPercent",0,100,2,1);
            locationAutomaticMinimum=GeneralNumber("LocationAutomaticAcceptanceMinimumPercent",0,100,2,1);
            AddRow(grid,"定位评分：可疑下限 / %",locationSuspiciousMinimum);
            AddRow(grid,"定位评分：手动接受下限 / %",locationManualMinimum);
            AddRow(grid,"定位评分：自动接受下限 / %",locationAutomaticMinimum);
            locationSuspiciousMinimum.ValueChanged+=delegate{LinkLocationScoreLimits();};
            locationManualMinimum.ValueChanged+=delegate{LinkLocationScoreLimits();};
            locationAutomaticMinimum.ValueChanged+=delegate{LinkLocationScoreLimits();};
            Label cycleNote=CreateNote("建议保留 1 个周期。较多周期会增加内存占用和处理时间；最多 5 个，数据不足时仅保留实际可用数据。");
            cycleNote.Name="LocationCycleCountNote";AddWideRow(grid,cycleNote);
            analyzeZeroVoltageData=new CheckBox{Name="AnalyzeZeroVoltageData",Text="分析 0 kV 数据（关闭时仅保存为噪声参考）",AutoSize=false};
            automaticRangeReminder=new CheckBox{Name="AutomaticRangeReminderEnabled",Text="显示普通自动量程建议（重新勾选可恢复）",AutoSize=false};
            AddWideRow(grid,analyzeZeroVoltageData);
            AddWideRow(grid,automaticRangeReminder);
            Label rangeNote=CreateNote("关闭提醒后，仅在已开始的本轮内按可用量程自动调整；不能调整或无法判断时保留数据并停止后续轮次。重新勾选可恢复普通提醒；修改本设置不启动采集或加压。");
            rangeNote.Name="AutomaticRangeReminderNote";AddWideRow(grid,rangeNote);
            projectCreationGuideCheck=new CheckBox{Name="ShowProjectCreationGuide",Text=L("进入新建项目时显示三页操作引导（重新勾选可恢复）","Show the three-page new project guide (check to restore)"),AutoSize=false};
            AddWideRow(grid,projectCreationGuideCheck);
            bool arranging=false;
            EventHandler fit=delegate
            {
                if(arranging)return;arranging=true;
                try
                {
                    rangeNote.MaximumSize=new Size(Math.Max(160,grid.ClientSize.Width-20),0);
                    cycleNote.MaximumSize=new Size(Math.Max(160,grid.ClientSize.Width-20),0);
                    foreach(Control child in grid.Controls)
                    {
                        var label=child as Label;
                        if(label!=null&&grid.GetColumnSpan(label)==1)label.MaximumSize=new Size(Math.Max(100,(int)grid.ColumnStyles[0].Width-label.Margin.Horizontal-grid.Padding.Horizontal),0);
                    }
                    foreach(var check in new[]{useCompensationCapacitance,frequencyReminderCheck,pressureCountdownCheck,projectCreationGuideCheck,analyzeZeroVoltageData,automaticRangeReminder})
                        check.Height=Math.Max(check.Font.Height+8,TextRenderer.MeasureText(check.Text,check.Font,new Size(Math.Max(100,grid.ClientSize.Width-48),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+8);
                }
                finally{arranging=false;}
            };
            grid.SizeChanged+=fit;grid.FontChanged+=fit;
            foreach(var check in new[]{useCompensationCapacitance,frequencyReminderCheck,pressureCountdownCheck,projectCreationGuideCheck,analyzeZeroVoltageData,automaticRangeReminder}) { check.FontChanged+=fit; check.TextChanged+=fit; }
            fit(grid,EventArgs.Empty);
        }
        static NumericUpDown GeneralNumber(string name,decimal min,decimal max,int digits,decimal increment)
        { var result=CreateNumeric(min,max,digits,increment); result.Name=name;return result; }
        void LoadGeneralPressureValues(AppSettings value)
        {
            SetNumeric(frequencyReminderMin,value.FrequencyReminderMinHz);SetNumeric(frequencyReminderMax,value.FrequencyReminderMaxHz);
            SetNumeric(frequencyDangerThreshold,value.FrequencyDangerThresholdHz);
            SetNumeric(capacitanceNormalMin,value.CapacitanceNormalMinUf);SetNumeric(capacitanceNormalMax,value.CapacitanceNormalMaxUf);
            SetNumeric(compensationCapacitance,value.CompensationCapacitanceUf);useCompensationCapacitance.Checked=value.UseCompensationCapacitance;
            SetNumeric(pressureCountdownSeconds,value.AutomaticPressureCountdownSeconds);SetNumeric(locationSaveCycles,value.LocationSaveCycleCount);
            frequencyReminderCheck.Checked=value.FrequencyReminderEnabled;pressureCountdownCheck.Checked=value.AutomaticPressureCountdownEnabled;
            projectCreationGuideCheck.Checked=value.ShowProjectCreationGuide;
            analyzeZeroVoltageData.Checked=value.AnalyzeZeroVoltageData;
            automaticRangeReminder.Checked=value.AutomaticRangeReminderEnabled;
            linkingLocationScoreLimits=true;
            try{
                foreach(var numeric in new[]{locationSuspiciousMinimum,locationManualMinimum,locationAutomaticMinimum}){numeric.Minimum=0;numeric.Maximum=100;}
                SetNumeric(locationSuspiciousMinimum,value.LocationSuspiciousMinimumPercent);
                SetNumeric(locationManualMinimum,value.LocationManualAcceptanceMinimumPercent);
                SetNumeric(locationAutomaticMinimum,value.LocationAutomaticAcceptanceMinimumPercent);
            }finally{linkingLocationScoreLimits=false;}
            LinkLocationScoreLimits();
        }
        void LinkLocationScoreLimits()
        {
            if(linkingLocationScoreLimits)return;
            linkingLocationScoreLimits=true;
            try{
                locationSuspiciousMinimum.Maximum=locationManualMinimum.Value;
                locationManualMinimum.Minimum=locationSuspiciousMinimum.Value;
                locationManualMinimum.Maximum=locationAutomaticMinimum.Value;
                locationAutomaticMinimum.Minimum=locationManualMinimum.Value;
            }finally{linkingLocationScoreLimits=false;}
        }
        void CaptureGeneralPressureValues(AppSettings value)
        {
            value.FrequencyReminderMinHz=ReadNumber(frequencyReminderMin);value.FrequencyReminderMaxHz=ReadNumber(frequencyReminderMax);
            value.FrequencyDangerThresholdHz=ReadNumber(frequencyDangerThreshold);
            value.CapacitanceNormalMinUf=ReadNumber(capacitanceNormalMin);value.CapacitanceNormalMaxUf=ReadNumber(capacitanceNormalMax);
            value.CompensationCapacitanceUf=ReadNumber(compensationCapacitance);value.UseCompensationCapacitance=useCompensationCapacitance.Checked;
            value.AutomaticPressureCountdownSeconds=(int)pressureCountdownSeconds.Value;value.LocationSaveCycleCount=(int)locationSaveCycles.Value;
            value.FrequencyReminderEnabled=frequencyReminderCheck.Checked;value.AutomaticPressureCountdownEnabled=pressureCountdownCheck.Checked;
            value.ShowProjectCreationGuide=projectCreationGuideCheck.Checked;
            // Retained XML compatibility field, not an operator confirmation.
            value.PressureOscillationCircuitConfirmed=true;
            // Retain the legacy XML field for compatibility, but there is only
            // one operator setting for both saving and range evaluation.
            value.PressureRangeEvaluationCycleCount=value.LocationSaveCycleCount;
            value.AnalyzeZeroVoltageData=analyzeZeroVoltageData.Checked;
            value.AutomaticRangeReminderEnabled=automaticRangeReminder.Checked;
            value.LocationSuspiciousMinimumPercent=ReadNumber(locationSuspiciousMinimum);
            value.LocationManualAcceptanceMinimumPercent=ReadNumber(locationManualMinimum);
            value.LocationAutomaticAcceptanceMinimumPercent=ReadNumber(locationAutomaticMinimum);
        }
    }
}
