using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        internal static DischargeClassScore[] ReadRecognitionScores(Dictionary<string,object> response,string phase,bool english)
        {
            var result=new List<DischargeClassScore>();if(response==null)return result.ToArray();
            Dictionary<string,object> map=null;object raw;
            if(response.TryGetValue("scores_json",out raw)&&raw is string){
                try{map=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)raw);}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            for(int i=0;i<dischargeLabels.GetLength(0);i++){
                string key=dischargeLabels[i,0];object value;
                if(map==null||!map.TryGetValue(key,out value)){if(!response.TryGetValue(key,out value))continue;}
                // Missing/boolean/text/NaN/out-of-range outputs are not scores.
                if(!(value is double||value is float||value is decimal||value is int||value is long))continue;
                double score=Convert.ToDouble(value,CultureInfo.InvariantCulture);
                if(Double.IsNaN(score)||Double.IsInfinity(score)||score<0||score>1)continue;
                result.Add(new DischargeClassScore(phase,key,dischargeLabels[i,english?2:1],score));
            }
            return result.ToArray();
        }
    }
}
