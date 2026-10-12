using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // This records a known automatic inclusion by this application. Missing
        // provenance in old/external matrices remains unknown, never inferred
        // from the algorithm number, a score or a checked acceptance flag.
        bool[] automaticAcceptanceRows=new bool[0];
        [Browsable(false)] public bool[] AutomaticAcceptanceRows { get { return (bool[])automaticAcceptanceRows.Clone(); } }
        [Browsable(false)] public string CurrentAcceptanceSourceCode
        {get{return AcceptanceSourceCode(CurrentAccepted,selected>=0&&acceptanceConfirmed[selected],selected>=0&&selected<automaticAcceptanceRows.Length&&automaticAcceptanceRows[selected]);}}
        static string AcceptanceSourceCode(bool accepted,bool manual,bool automatic)
        {return !accepted?"NotAccepted":manual?"Manual":automatic?"Automatic":"Unknown";}
        static string ScoreBandCode(double score,double lowPercent,double highPercent,bool pending)
        {
            if(pending||!Finite(score)||score<0||score>1)return "Unavailable";
            return score<lowPercent/100?"Low":score<highPercent/100?"Medium":"High";
        }
        bool CurrentIsEMatchingScore {get{return selected>=0&&ReviewEAnalysis.IsMethod((int)working[selected,26]);}}
        string CurrentAcceptanceIndicatorText()
        {
            if(CurrentAcquisitionReferenceOnly)return L("Reference only","风险参考");
            switch(CurrentAcceptanceSourceCode){
                case "Manual":return L("Manual","人工接受");
                case "Automatic":return L("Automatic","自动纳入");
                case "Unknown":return L("Unknown","来源未知");
                default:return L("Included","已纳入");
            }
        }
        string CurrentScoreLabel(bool pending)
        {
            if(!CurrentIsEMatchingScore)return L("Confidence","确信度");
            string band=ScoreBandCode(CurrentConfidence,spectrum.ClassificationLowThresholdPercent,spectrum.ClassificationHighThresholdPercent,pending);
            string level=band=="Low"?L("Low","低"):band=="Medium"?L("Medium","中"):band=="High"?L("High","高"):L("Unavailable","未知");
            return L("Match score","匹配评分")+" ("+level+")";
        }
        string CurrentDecisionExplanation()
        {
            if(selected<0)return "";
            if(CurrentAcquisitionReferenceOnly)return AcquisitionRiskText();
            switch(CurrentAcceptanceSourceCode){
                case "Manual":return L("Manually accepted; not a physical confirmation.","人工接受；不代表物理确认。");
                case "Automatic":return L("Automatically included; not manually confirmed.","自动纳入；未经人工确认。");
                case "Unknown":return L("Included; historical source unknown.","已纳入；历史来源未知。");
                default:return removed[selected]?L("Excluded from statistics.","已从统计中排除。"):CurrentUncertain?L("Uncertain; review required.","不确定；需继续复核。"):L("Pending review.","待复核。");
            }
        }
        string CurrentScoreExplanation(bool pending)
        {
            string band=ScoreBandCode(CurrentConfidence,spectrum.ClassificationLowThresholdPercent,spectrum.ClassificationHighThresholdPercent,pending);
            string level=band=="Low"?L("Low","低"):band=="Medium"?L("Medium","中"):band=="High"?L("High","高"):L("Unavailable","未知");
            string value=band=="Unavailable"?"":(" ("+(CurrentConfidence*100).ToString("0.#",CultureInfo.InvariantCulture)+"%)");
            return (CurrentIsEMatchingScore?L("Match score: ","匹配评分："):L("Score: ","评分："))+level+value;
        }
        static bool[] RetainAutomaticAcceptanceRows(bool[] existing,int[] sourceRows,double[,] parameters,bool automatic)
        {
            return sourceRows.Select((row,index)=>row>=0?(row<existing.Length&&existing[row]):automatic&&parameters[index,35]==1).ToArray();
        }
        static bool[] ParseAutomaticAcceptanceRows(System.Collections.Generic.Dictionary<string,string> settings,int count)
        {
            string saved;
            return settings.TryGetValue("AutomaticAcceptanceRows",out saved)?ParseReviewedRows(saved,count):new bool[count];
        }
    }
}
