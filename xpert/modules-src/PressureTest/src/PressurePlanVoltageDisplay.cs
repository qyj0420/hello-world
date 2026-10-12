using System;
using System.ComponentModel;
using System.Globalization;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        bool? pressurePlanDisplayPeak;
        [Browsable(false)] public string PlanVoltageDisplayMode
        { get { return (pressurePlanDisplayPeak??planVoltagesArePeak)?"PEAK":"RMS"; } }
        public void SetPlanVoltageDisplayMode(string mode)
        {
            if(mode!="RMS"&&mode!="PEAK")throw new ArgumentException("Expected RMS or PEAK.","mode");
            RunOnUiThread(delegate {
                pressurePlanDisplayPeak=mode=="PEAK";
                if(pressurePlanGrid==null)return;
                pressurePlanGrid.Columns[2].HeaderText=PlanVoltageHeaderText();
                // Only values change. Do not rebuild rows or publish a hardware
                // parameter action while an operator switches display units.
                for(int i=0;i<pressurePlanGrid.Rows.Count&&i<planVoltagesKv.Length;i++)
                    pressurePlanGrid.Rows[i].Cells[2].Value=PlanVoltageDisplayText(planVoltagesKv[i]);
            });
        }
        string PlanVoltageHeaderText()
        {return PlanVoltageDisplayMode=="PEAK"?PressureUiText("峰值/kV ▾","Peak/kV ▾"):PressureUiText("有效值/kV ▾","RMS/kV ▾");}
        string PlanVoltageDisplayText(double stored)
        {
            if(!IsFinite(stored)||stored<0)return "—";
            bool peak=PlanVoltageDisplayMode=="PEAK";
            double shown=peak==planVoltagesArePeak?stored:peak?stored*Math.Sqrt(2):stored/Math.Sqrt(2);
            return shown.ToString("0.0",CultureInfo.InvariantCulture);
        }
    }
}
