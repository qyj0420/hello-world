using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal static class PressurePreferencesPersistence
    {
        internal static void Save(object control, AppSettings current, Action<AppSettings> save)
        {
            if(control==null||current==null||save==null)throw new ArgumentNullException();
            var copy=current.Clone();
            object frequency=LateBound.Read(control,"FrequencyReminderEnabled");
            object countdown=LateBound.Read(control,"AutomaticPressureCountdownEnabled");
            object compensation=LateBound.Read(control,"UseCompensationCapacitance");
            object rangeReminder=LateBound.Read(control,"AutomaticRangeReminderEnabled");
            object analyzeZero=LateBound.Read(control,"AnalyzeZeroVoltageData");
            if(!(frequency is bool)||!(countdown is bool)||!(compensation is bool)||!(rangeReminder is bool)||!(analyzeZero is bool))throw new InvalidDataException("Invalid pressure reminder preference.");
            copy.FrequencyReminderEnabled=(bool)frequency;
            copy.AutomaticPressureCountdownEnabled=(bool)countdown;
            copy.UseCompensationCapacitance=(bool)compensation;
            copy.AutomaticRangeReminderEnabled=(bool)rangeReminder;
            copy.AnalyzeZeroVoltageData=(bool)analyzeZero;
            save(copy);
            // Never claim persistence until the atomic settings save succeeded.
            current.FrequencyReminderEnabled=copy.FrequencyReminderEnabled;
            current.AutomaticPressureCountdownEnabled=copy.AutomaticPressureCountdownEnabled;
            current.UseCompensationCapacitance=copy.UseCompensationCapacitance;
            current.AutomaticRangeReminderEnabled=copy.AutomaticRangeReminderEnabled;
            current.AnalyzeZeroVoltageData=copy.AnalyzeZeroVoltageData;
        }
    }
    internal sealed partial class ModuleHost
    {
        void ApplyPressurePreferences(Control control)
        {
            var value=settings as AppSettings;if(value==null)return;
            ApplyLocationFilterDefaults(control,true,value,state.ProjectPath);
            TrySet(control,"FrequencyReminderMinHz",value.FrequencyReminderMinHz);
            TrySet(control,"FrequencyReminderMaxHz",value.FrequencyReminderMaxHz);
            TrySet(control,"FrequencyDangerThresholdHz",value.FrequencyDangerThresholdHz);
            TrySet(control,"CapacitanceNormalMinUf",value.CapacitanceNormalMinUf);
            TrySet(control,"CapacitanceNormalMaxUf",value.CapacitanceNormalMaxUf);
            TrySet(control,"CompensationCapacitanceUf",value.CompensationCapacitanceUf);
            TrySet(control,"UseCompensationCapacitance",value.UseCompensationCapacitance);
            TrySet(control,"FrequencyReminderEnabled",value.FrequencyReminderEnabled);
            TrySet(control,"AutomaticPressureCountdownSeconds",value.AutomaticPressureCountdownSeconds);
            TrySet(control,"AutomaticPressureCountdownEnabled",value.AutomaticPressureCountdownEnabled);
            TrySet(control,"AutomaticRangeReminderEnabled",value.AutomaticRangeReminderEnabled);
            TrySet(control,"AnalyzeZeroVoltageData",value.AnalyzeZeroVoltageData);
            // Consumed by the host's LOC saving pipeline, not by the HV scheduler.
            TrySet(control,"LocationSaveCycleCount",value.LocationSaveCycleCount);
        }
        void ConnectPressurePreferences(Control control)
        {
            EventInfo changed=control.GetType().GetEvent("PressurePreferencesChanged");
            if(changed==null||changed.EventHandlerType!=typeof(EventHandler))return;
            changed.AddEventHandler(control,new EventHandler(delegate
            {
                var current=settings as AppSettings;if(current==null||disposed||control.IsDisposed)return;
                try
                {
                    PressurePreferencesPersistence.Save(control,current,copy=>copy.Save());
                    contextSignatures.Remove(PressureTest);
                    diagnostics.Info("PressurePreferences","Pressure reminder preference saved to unified settings.");
                }
                catch(Exception error)
                {
                    ApplyPressurePreferences(control);
                    diagnostics.Error("PressurePreferences","Could not persist the reminder preference; restored saved values.",error);
                    MessageBox.Show(control.FindForm(),(AppLanguages.IsChinese(current.LanguageCode)?
                        "提醒设置未保存，已恢复原设置。请检查文件权限后重试。":
                        "The reminder preference was not saved. Previous settings were restored. Check file permissions and retry."),
                        current.SoftwareModel,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }
            }));
        }
    }
}
