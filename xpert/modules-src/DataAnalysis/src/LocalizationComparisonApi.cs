using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    // Stable offline boundary; does not commit review or physical acceptance.
    public static class LocalizationComparisonApi
    {
        public static string BuildEvidenceJson(string project,string loc,string phase,double length,double velocity)
        {
            int phaseCode=phase=="A"?1:phase=="B"?2:phase=="C"?3:0;
            if(phaseCode==0)throw new ArgumentException("融合复核需要记录的实际 A、B 或 C 相别。");
            using(var guard=new FileStream(loc,FileMode.Open,FileAccess.Read,FileShare.Read)){
                var data=ReviewLocationFileReader.Read(loc,project);
                byte[] bytes=new byte[checked(data.ChargeSamples.Length*8)];Buffer.BlockCopy(data.ChargeSamples,0,bytes,0,bytes.Length);
                string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                var bundles=new List<object>();
                foreach(int method in new[]{5,7}){
                    var result=ReviewAnalysisEngine.Analyze(new ReviewAnalysisRequest{ProjectPath=project,LocationFilePath=loc,
                        AlgorithmIndex=method,MaxPulses=100,SampleRateHz=data.SampleRateHz,UseFirstArrivalTiming=false,AutomaticAcceptance=true});
                    if(!result.Success)throw new InvalidDataException((method==5?"A":"B")+" 未完成："+result.Status);
                    var matrix=result.Parameters;var pairs=new List<object>();var seen=new HashSet<string>();
                    var roles=new Dictionary<int,Dictionary<string,object>>();
                    try{
                        var diagnostic=new JavaScriptSerializer{MaxJsonLength=8*1024*1024,RecursionLimit=128}.DeserializeObject(result.Status) as Dictionary<string,object>;
                        var current=diagnostic["current_analysis"] as Dictionary<string,object>;
                        var output=current["output_pulse_roles"] as Dictionary<string,object>;
                        foreach(Dictionary<string,object> row in (object[])output["rows"])roles[Convert.ToInt32(row["row"])]=row;
                    }catch(KeyNotFoundException){}catch(ArgumentException){}catch(NullReferenceException){}
                    double actualLength=length,actualVelocity=velocity;
                    for(int r=0;r<matrix.GetLength(0);r++){
                        if(matrix[r,0]!=phaseCode)throw new InvalidDataException("记录相别与所选相别不一致。");
                        if(matrix[r,7]!=data.SampleRateHz)throw new InvalidDataException("定位输出采样率与原记录不一致。");
                        if(r==0){actualLength=matrix[r,5];actualVelocity=matrix[r,6];}
                        double i=matrix[r,10],j=matrix[r,11];
                        if(Double.IsNaN(i)||Double.IsNaN(j)||j<=i)continue;
                        if(!seen.Add(i.ToString("R",CultureInfo.InvariantCulture)+":"+j.ToString("R",CultureInfo.InvariantCulture)))continue;
                        Dictionary<string,object> role;roles.TryGetValue(r,out role);
                        var reasons=new List<string>();if(matrix[r,35]!=1)reasons.Add("algorithm_pair_requires_review");
                        object value;bool supported=role!=null&&role.TryGetValue("source_identity_supported",out value)&&Object.Equals(value,true);
                        if(method==7&&role!=null&&role.TryGetValue("source_identity_requires_review",out value)&&Object.Equals(value,true))
                            reasons.Add(role.ContainsKey("source_identity_reason")?Convert.ToString(role["source_identity_reason"]):"identity_unresolved");
                        pairs.Add(new Dictionary<string,object>{{"direct_index",(int)i},{"reflection_index",(int)j},
                            {"engineering_score",Double.IsNaN(matrix[r,33])?null:(object)matrix[r,33]},
                            {"reasons",reasons},{"source_identity_supported",supported}});
                    }
                    bundles.Add(new Dictionary<string,object>{{"algorithm",method==5?"A":"B"},{"version","3.0.22"},
                        {"record_id",Path.GetFileName(loc)},{"phase","L"+phaseCode},{"waveform_sha256",hash},
                        {"sample_rate_hz",data.SampleRateHz},{"segments",new[]{new Dictionary<string,object>{{"length_m",actualLength},{"velocity_m_per_us",actualVelocity}}}},
                        {"reference_plane","unconfirmed"},{"timing_basis","peak"},{"complete",false},{"pairs",pairs},
                        {"diagnostic",result.Status},{"settings_scope","Peak; default engine options; 100 output pulses; not a complete competition graph"}});
                }
                return new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=128}.Serialize(bundles);
            }
        }
    }
}
