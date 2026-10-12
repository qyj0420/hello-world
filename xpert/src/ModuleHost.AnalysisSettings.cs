using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal static class AnalysisSettingsPersistence
    {
        internal static AppSettings Capture(object control, AppSettings current)
        {
            if (control == null || current == null) throw new ArgumentNullException();
            var copy = current.Clone();
            var analysis = copy.TaskMinus3;
            analysis.AttenuationTolerancePercent = Read<double>(control, "AnalysisAttenuationTolerancePercent");
            analysis.CorrelationErrorPercent = Read<double>(control, "AnalysisCorrelationErrorPercent");
            analysis.AutomaticThresholdMultiplier = Read<double>(control, "AnalysisAutomaticThresholdMultiplier");
            // Plus Data is the sole UI authority for these parameters. Ordinary
            // analysis confirmation must preserve the protected snapshot, even
            // when an older loaded module exposes its former advanced editor.
            analysis.UseFirstArrivalTiming = Read<bool>(control, "AnalysisUseFirstArrivalTiming");
            analysis.PhaseFilterEnabled = Read<bool>(control, "AnalysisPhaseFilterEnabled");
            bool[] quadrants = Read<bool[]>(control, "AnalysisQuadrants");
            if (quadrants.Length != 4) throw new InvalidDataException("Expected four phase quadrants.");
            analysis.Quadrant0To90 = quadrants[0]; analysis.Quadrant90To180 = quadrants[1];
            analysis.Quadrant180To270 = quadrants[2]; analysis.Quadrant270To360 = quadrants[3];
            string error;
            if (!copy.TryValidate(out error)) throw new InvalidDataException(error);
            return copy;
        }
        static T Read<T>(object control, string name)
        {
            object value = LateBound.Read(control, name);
            if (!(value is T)) throw new InvalidDataException("Invalid analysis setting: " + name);
            return (T)value;
        }
    }

    internal sealed partial class ModuleHost
    {
        void ApplyLocationFilterDefaults(Control control,bool pressure,object source,string project)
        {
            string algorithm=LateBound.ReadString(source,"LocationFilterAlgorithmId");
            // Older callers and retired A preferences now use the sole B filter.
            if((algorithm.Length==0&&LateBound.Read(source,"LocationFilterAlgorithmId")==null)||algorithm=="A")algorithm="B";
            LocationFilterSettingsDefaults.Apply(control,pressure?"StatisticsLocationFilterAlgorithmId":"LocationFilterAlgorithmId",algorithm,project);
        }
        private void ConnectAnalysisSettings(Control control)
        {
            EventInfo compare=control.GetType().GetEvent("LocalizationComparisonRequested");
            if(compare!=null&&compare.EventHandlerType==typeof(EventHandler))
                compare.AddEventHandler(control,new EventHandler(delegate {
                    if(disposed||control.IsDisposed||LateBound.ReadBoolean(control,false,"IsAnalysisRunning"))return;
                    using(var dialog=new LocalizationCForm(AppLanguages.IndexOf(LateBound.ReadString(settings,"LanguageCode")))){
                        dialog.SetInitialRecord(LateBound.ReadString(control,"DisplayedLocationFilePath"));
                        dialog.ShowDialog(control.FindForm());
                    }
                }));
            EventInfo change = control.GetType().GetEvent("AnalysisSettingsChanged");
            if (change == null || change.EventHandlerType != typeof(EventHandler)) return;
            change.AddEventHandler(control, new EventHandler(delegate
            {
                var current = settings as AppSettings;
                if (current == null || disposed || control.IsDisposed) return;
                try
                {
                    AppSettings copy = AnalysisSettingsPersistence.Capture(control, current);
                    copy.Save();
                    current.TaskMinus3 = copy.TaskMinus3.Clone();
                    contextSignatures.Remove(DataAnalysis);
                    diagnostics.Info("AnalysisSettings", "Confirmed analysis settings saved to the unified configuration.");
                }
                catch (Exception error)
                {
                    diagnostics.Error("AnalysisSettings", "Could not persist confirmed analysis settings.", error);
                    bool zh = AppLanguages.IsChinese(current.LanguageCode);
                    MessageBox.Show(control.FindForm(), (zh
                        ? "分析设置未保存。请检查文件权限后重试。"
                        : "Analysis settings were not saved. Check file permissions and retry."),
                        current.SoftwareModel, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }));
        }
    }
}
