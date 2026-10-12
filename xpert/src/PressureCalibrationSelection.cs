using System;
using System.Globalization;
using System.IO;
using System.Threading;
using HVX.CalibrationStorage;

namespace Xiongxin.OWTS.Desktop
{
    // A confirmed legacy binding is a different source type, never a fabricated
    // live acquisition. Keep that distinction at every capture boundary.
    internal sealed class PressureCalibrationSelection
    {
        internal string Source, SourceHash;
        internal double CoefficientPcPerV, VelocityMPerUs, ChargePc;
        internal DateTimeOffset SourceUtc;
        internal int? RecordedRangeIndex;
        internal bool IsLegacy;
        string project, pico, art;
        bool firstArrival;

        internal static PressureCalibrationSelection Find(string project, string phase,
            bool firstArrival, string pico, string art)
        {
            // No path may treat an unknown current device pair as verified.
            // Formal records retain their original identity; legacy reuse still
            // requires the existing explicit applicability decision below.
            if(String.IsNullOrWhiteSpace(pico)||String.IsNullOrWhiteSpace(art))return null;
            var live=LiveCalibrationRecord.BestForProject(project,phase,firstArrival,pico);
            var selected=new PressureCalibrationSelection {project=project,pico=pico,art=art,firstArrival=firstArrival};
            if(live!=null)
            {
                selected.Source=live.SourcePath;
                selected.CoefficientPcPerV=live.CoefficientPcPerV;
                selected.ChargePc=live.ChargePc;
                selected.VelocityMPerUs=live.VelocityMPerUs;
                selected.RecordedRangeIndex=live.RangeIndex;
                selected.SourceUtc=DateTimeOffset.Parse(live.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
            }
            else
            {
                // No identity fallback: the operator's binding must name this
                // actual pair. Historical device identity remains unknown.
                var legacy=LegacyCalibrationBinding.FindForProject(project,phase,firstArrival,pico,art);
                if(legacy==null)return null;
                selected.IsLegacy=true;selected.Source=legacy.BindingPath;
                selected.CoefficientPcPerV=legacy.CoefficientPcPerV;
                selected.ChargePc=legacy.ChargePc;
                selected.VelocityMPerUs=legacy.HistoricalVelocityMPerUs;
                selected.SourceUtc=DateTimeOffset.Parse(legacy.ConfirmedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
            }
            selected.SourceHash=LiveCalibrationRecord.HashFile(selected.Source);
            selected.Revalidate();
            return selected;
        }

        internal void Revalidate()
        {
            if(LiveCalibrationRecord.HashFile(Source)!=SourceHash)
                throw new InvalidDataException("校准记录已改变，请重新载入。");
            if(IsLegacy)
            {
                var binding=LegacyCalibrationBinding.LoadAndValidate(project,Source,pico,art,firstArrival);
                if(binding.CoefficientPcPerV!=CoefficientPcPerV||binding.ChargePc!=ChargePc||binding.HistoricalVelocityMPerUs!=VelocityMPerUs)
                    throw new InvalidDataException("旧校准确认记录已改变，请重新核对。");
            }
            else
            {
                var live=LiveCalibrationRecord.Load(project,Source,true);
                if(live.CoefficientPcPerV!=CoefficientPcPerV||live.ChargePc!=ChargePc||live.DeviceSerial!=pico||live.FirstArrival!=firstArrival)
                    throw new InvalidDataException("当前校准与设备或计时设置不匹配。");
            }
            if(LiveCalibrationRecord.HashFile(Source)!=SourceHash)
                throw new InvalidDataException("校准记录在核对期间已改变。");
        }

        internal void RevalidateForCapture(Action validateContext,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(validateContext==null)throw new ArgumentNullException("validateContext");
            validateContext();
            Revalidate();
            token.ThrowIfCancellationRequested();
        }
    }
}
