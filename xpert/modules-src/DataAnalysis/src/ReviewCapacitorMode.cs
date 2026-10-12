using System;
using System.ComponentModel;
using System.IO;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool capacitorProject;
        double noiseSimilarityTolerancePercent=1;
        [Browsable(false)] public bool CapacitorStatisticsMode { get { return capacitorProject; } }
        [Browsable(false)] public double NoiseSimilarityTolerancePercent {
            get { return noiseSimilarityTolerancePercent; }
            set { if(!Finite(value)||value<0||value>10)throw new ArgumentOutOfRangeException("NoiseSimilarityTolerancePercent");noiseSimilarityTolerancePercent=value; }
        }
        bool ReadCapacitorProject(string path)
        {return !String.IsNullOrWhiteSpace(path)&&HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(File.Exists(path)?Path.GetDirectoryName(path):path);}
        bool IsCapacitorRow(int row)
        {return capacitorProject&&row>=0&&row<SourceRowCount&&working[row,26]==-2;}
        void ApplyCapacitorReviewUi()
        {
            spectrum.CapacitorStatisticsMode=capacitorProject;
            pdmap.CapacitorStatisticsMode=capacitorProject;
            overviewSnap.Enabled=overviewReverse.Enabled=overviewCompensated.Enabled=!capacitorProject;
            statisticsEnabledBox.Enabled=statisticsSlider.Enabled=!capacitorProject;
            if(!capacitorProject)return;
            // Location-only actions do not have a meaning for a lumped capacitor.
            overviewSnap.Enabled=overviewReverse.Enabled=overviewCompensated.Enabled=false;
            statisticsEnabledBox.Enabled=statisticsSlider.Enabled=false;
            if(selected>=0&&IsCapacitorRow(selected)) {
                metricValues[0].Text=L("N/A","不适用");metricUnits[0].Visible=false;
                metricValues[1].Text=L("No localization","无定位");metricUnits[1].Visible=false;
                acceptButton.AccessibleDescription=CurrentAcquisitionReferenceOnly?AcquisitionRiskText():L("Accept this capacitor charge pulse for statistics.","将本电容放电脉冲纳入统计。");
            }
        }
    }
}
