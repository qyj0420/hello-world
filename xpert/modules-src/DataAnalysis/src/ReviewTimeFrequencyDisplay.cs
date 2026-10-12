using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        double[,] timeFrequencySource,timeFrequencyWaves,timeFrequencyFeatures;
        Dictionary<string,string> timeFrequencyDiagnostics;
        int timeFrequencyBuildCount;
        internal static string TimeFrequencyIdentity(double phase,double id)
        {return phase.ToString("R",CultureInfo.InvariantCulture)+":"+id.ToString("R",CultureInfo.InvariantCulture);}
        static Dictionary<string,string> TimeFrequencyStatuses(string root,string[] paths,double[,] catalog,Dictionary<string,string> diagnostics)
        {
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            if(String.IsNullOrEmpty(root)||paths==null||catalog==null||diagnostics==null)return result;
            for(int file=0;file<paths.Length&&file<catalog.GetLength(0);file++) {
                string status;
                if(diagnostics.TryGetValue(SessionRelative(root,paths[file]),out status))
                    result.Add(TimeFrequencyIdentity(catalog[file,0],catalog[file,1]),status);
            }
            return result;
        }
        bool EnsureTimeFrequencyFeatures()
        {
            if(ReferenceEquals(timeFrequencySource,working)&&ReferenceEquals(timeFrequencyWaves,matching)&&ReferenceEquals(timeFrequencyDiagnostics,algorithmDiagnostics))return false;
            var statuses=String.IsNullOrEmpty(projectPath)?new Dictionary<string,string>():
                TimeFrequencyStatuses(SessionRoot(projectPath),analysisTree.GetDataPaths(),analysisTree.GetDataCatalog(),algorithmDiagnostics);
            timeFrequencyFeatures=ReviewEAnalysis.CreateTimeFrequencyDisplay(working,matching,statuses);
            timeFrequencySource=working;timeFrequencyWaves=matching;timeFrequencyDiagnostics=algorithmDiagnostics;timeFrequencyBuildCount++;return true;
        }
        double[,] SpectrumDisplayParameters()
        {
            EnsureTimeFrequencyFeatures();var display=(double[,])working.Clone();
            for(int row=0;row<display.GetLength(0);row++)if(display[row,26]==8||display[row,26]==9) {
                // A changed incident/sampling support needs fresh evidence. Dragging
                // a reflection never re-runs FFTs or rewrites the original features.
                bool same=display[row,10].Equals(timeFrequencyFeatures[row,10])&&display[row,7].Equals(timeFrequencyFeatures[row,7])&&display[row,31].Equals(timeFrequencyFeatures[row,31]);
                display[row,23]=same?timeFrequencyFeatures[row,23]:Double.NaN;
                display[row,24]=same?timeFrequencyFeatures[row,24]:Double.NaN;
            }
            return display;
        }
        double[] SpectrumDisplayRow(int index,double[] source)
        {
            var row=(double[])source.Clone();if(row[26]!=8&&row[26]!=9)return row;
            EnsureTimeFrequencyFeatures();bool same=row[10].Equals(timeFrequencyFeatures[index,10])&&row[7].Equals(timeFrequencyFeatures[index,7])&&row[31].Equals(timeFrequencyFeatures[index,31]);
            row[23]=same?timeFrequencyFeatures[index,23]:Double.NaN;row[24]=same?timeFrequencyFeatures[index,24]:Double.NaN;return row;
        }
        void LoadSpectrumDisplayData(){spectrum.LoadDataMatrix(SpectrumDisplayParameters());}
        void RefreshTimeFrequencyDisplayIfChanged(){if(EnsureTimeFrequencyFeatures())LoadSpectrumDisplayData();}
    }
}
