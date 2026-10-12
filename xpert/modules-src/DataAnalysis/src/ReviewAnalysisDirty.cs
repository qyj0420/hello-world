using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool analysisDirty,analysisHadContent;
        int suppressAnalysisDirty;
        string savedAnalysisParameterStamp="";
        public event EventHandler AnalysisDirtyStateChanged;
        // Optional host-provided project caption. It is user data, never translated.
        [Browsable(false)] public string AnalysisProjectDisplayName {get;set;}="";
        // QA/automation may return a name; null means Cancel. No dialog is shown.
        [Browsable(false)] public Func<string,string> AnalysisSaveNameProvider {get;set;}
        // Return SaveNew / Overwrite / Discard / Cancel. Null is cancellation.
        [Browsable(false)] public Func<bool,string> AnalysisLeaveChoiceProvider {get;set;}
        [Browsable(false)] public bool HasUnsavedAnalysisChanges
        {
            get
            {
                if(suppressAnalysisDirty>0)return analysisDirty;
                bool content=HasAnalysisContent();
                if(!content&&!analysisHadContent)return false;
                return analysisDirty||savedAnalysisParameterStamp!=AnalysisParameterStamp();
            }
        }
        bool HasAnalysisContent()
        {return SourceRowCount>0||reportCharts.Count>0||!String.IsNullOrEmpty(LoadedAnalysisId)||(analysisTree!=null&&analysisTree.GetAnalyzedDataPaths().Length>0);}
        static bool SameAnalysisProjectPath(string first,string second)
        {
            if(String.Equals(first,second,StringComparison.OrdinalIgnoreCase))return true;
            if(String.IsNullOrWhiteSpace(first)||String.IsNullOrWhiteSpace(second))return false;
            try{return String.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
        }
        void InitializeAnalysisDirtyTracking()
        {
            ResultsChanged+=(s,e)=>MarkAnalysisDirty();
            AnalysisSettingsChanged+=(s,e)=>MarkAnalysisDirty();
            MarkAnalysisClean();
        }
        void MarkAnalysisDirty()
        {
            if(suppressAnalysisDirty>0)return;
            if(!HasAnalysisContent()&&!analysisHadContent)return;
            analysisHadContent=true;
            if(analysisDirty)return;analysisDirty=true;NotifyReviewHost(AnalysisDirtyStateChanged);
        }
        void MarkAnalysisClean()
        {
            bool was=analysisDirty;analysisDirty=false;analysisHadContent=HasAnalysisContent();savedAnalysisParameterStamp=AnalysisParameterStamp();
            if(was)NotifyReviewHost(AnalysisDirtyStateChanged);
        }
        void ResetAnalysisDirtyForProject()
        {if(suppressAnalysisDirty==0){analysisDirty=false;analysisHadContent=false;savedAnalysisParameterStamp=AnalysisParameterStamp();NotifyReviewHost(AnalysisDirtyStateChanged);}}
        string AnalysisParameterStamp()
        {
            var parts=new StringBuilder();
            foreach(object value in new object[]{EndpointPolicy,AnalysisAlgorithmIndex,AnalysisMultipleReflectionSuppressionCount,AnalysisUseFirstArrivalTiming,
                AnalysisAttenuationTolerancePercent,AnalysisCorrelationErrorPercent,AnalysisAutomaticThresholdMultiplier,AnalysisMaximumPulseCount,
                AnalysisPhaseFilterEnabled,thresholdAll,globalThreshold.Manual,globalThreshold.Value,ConnectingCableLengthM,ResultGeometryAlreadyCompensated,
                ReversePositionDisplay,pdmap.ApproximateCablePositions,spectrum.UseCompensatedCharge,WaveformFilterMode,
                locationFilterEnabled,locationFilterStrength,locationFilterAlgorithmId,locationFilterBOptions.ChargeThresholdPc,locationFilterBOptions.VoltageLimitKv,
                locationFilterBOptions.MinimumClusterSize,locationFilterBOptions.FromM,locationFilterBOptions.ToM,automaticDisplay,AHistoryAuxiliaryEnabled})
                parts.Append(Convert.ToString(value,CultureInfo.InvariantCulture)).Append('|');
            foreach(bool value in analysisQuadrants)parts.Append(value?'1':'0');
            foreach(double value in FilterParameters)parts.Append('|').Append(value.ToString("R",CultureInfo.InvariantCulture));
            foreach(var item in fileThresholds.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase))
                parts.Append('|').Append(item.Key).Append(':').Append(item.Value.Manual).Append(':').Append(item.Value.Value.ToString("R",CultureInfo.InvariantCulture));
            return parts.ToString();
        }
        [Browsable(false)] public string SuggestedAnalysisName
        {
            get
            {
                string prefix=AnalysisProjectDisplayName;
                if(String.IsNullOrWhiteSpace(prefix))
                {
                    string path=(projectPath??"").TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                    if(File.Exists(path))path=Path.GetDirectoryName(path);
                    prefix=String.IsNullOrWhiteSpace(path)?L("Project","项目"):Path.GetFileName(path);
                }
                prefix=HVX.ProjectModel.AnalysisDateText.FileSafeProjectPrefix(prefix);
                prefix=new string(prefix.Select(c=>Char.IsControl(c)||Path.GetInvalidFileNameChars().Contains(c)?'_':c).ToArray()).Trim().TrimEnd('.');
                if(prefix.Length==0)prefix=L("Project","项目");if(prefix.Length>130)prefix=prefix.Substring(0,130);
                string head=prefix+" - ";long maximum=0;
                string displayHead=HVX.ProjectModel.AnalysisDateText.DisplayAutomaticName(head+"000");displayHead=displayHead.Substring(0,displayHead.Length-3);
                foreach(var item in EnumerateSavedAnalyses())
                {
                    long value;string name=item.DisplayName;if(name.StartsWith(displayHead,StringComparison.OrdinalIgnoreCase)&&Int64.TryParse(name.Substring(displayHead.Length),NumberStyles.None,CultureInfo.InvariantCulture,out value)&&value>=0&&value<Int64.MaxValue)
                        maximum=Math.Max(maximum,value);
                }
                return head+(maximum+1).ToString("D3",CultureInfo.InvariantCulture);
            }
        }
        string ValidateNewAnalysisName(string requested)
        {
            string name=(requested??"").Trim();
            if(name.Length==0||name.Length>160||name.Any(c=>Char.IsControl(c)||Path.GetInvalidFileNameChars().Contains(c))||name.EndsWith(".",StringComparison.Ordinal))
                throw new ArgumentException(L("Use 1–160 characters without file-name symbols such as \\ / : * ? \" < > | or a trailing period.","请输入1–160个字符，不可包含 \\ / : * ? \" < > | 等文件名符号，也不可用句点结尾。"));
            string stem=name.Split('.')[0].ToUpperInvariant();
            if(new[]{"CON","PRN","AUX","NUL"}.Contains(stem)||(stem.Length==4&&(stem.StartsWith("COM")||stem.StartsWith("LPT"))&&stem[3]>='1'&&stem[3]<='9'))
                throw new ArgumentException(L("This name is reserved by Windows. Choose another name.","此名称是 Windows 保留名称，请更换。"));
            if(EnumerateSavedAnalyses().Any(item=>String.Equals(item.Name,name,StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(L("An analysis with this name already exists. Choose another name; existing data will not be overwritten.","已存在同名分析，请更换名称；不会覆盖现有分析。"));
            return name;
        }
    }
}
