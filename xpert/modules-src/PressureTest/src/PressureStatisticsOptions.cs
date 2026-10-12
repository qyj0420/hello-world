using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        public event EventHandler DisplayRecommendationChanged;
        [Category("Statistics"),DefaultValue(false)]
        public bool StatisticsFilterEnabled
        {get{return spectrumDashboardView.StatisticsFilterEnabled;}set{RunOnUiThread(()=>spectrumDashboardView.StatisticsFilterEnabled=value);}}
        [Category("Statistics"),DefaultValue(40d)]
        public double StatisticsFilterStrength
        {get{return spectrumDashboardView.StatisticsFilterStrength;}set{if(Double.IsNaN(value)||Double.IsInfinity(value)||value<0||value>100)throw new ArgumentOutOfRangeException("value");RunOnUiThread(()=>spectrumDashboardView.StatisticsFilterStrength=value);}}
        [Browsable(false)] public int[] StatisticsFilteredSourceRows {get{return spectrumDashboardView.StatisticsFilteredSourceRows;}}
        [Category("Statistics"),DefaultValue(true)]public bool AutomaticDisplayRecommendation
        {get{return spectrumDashboardView.AutomaticDisplayRecommendation;}set{RunOnUiThread(()=>spectrumDashboardView.AutomaticDisplayRecommendation=value);}}
        [Browsable(false)]public string DisplayRecommendationJson {get{return spectrumDashboardView.DisplayRecommendationJson;}}
        [Category("Language"),DefaultValue(0)]
        public int LanguageIndex {get{return spectrumDashboardView.LanguageIndex;}set{RunOnUiThread(()=>{spectrumDashboardView.SetLanguageIndex(value);ApplyPressureBuiltInLanguage();UpdateRangeUi();UpdatePressureUi();UpdateCustomVoltageUi();});}}
    }
}
