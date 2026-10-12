using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    // Expanded learning + conflict gate for defect recognition samples.
    // Does not alter location E+ mathematics or pressure safety paths.
    internal static class ReviewDefectLearningExpansion
    {
        internal const double ConflictScoreThreshold=0.80;
        internal const int DefaultVariantCount=4;
        internal static RecognitionSnapshot[] Expand(RecognitionSnapshot source,int variants,int seed)
        {
            if(source==null||source.Rows==null||source.Rows.Length==0)throw new ArgumentException("No recognition snapshot to expand.");
            variants=Math.Max(0,Math.Min(16,variants));
            var results=new List<RecognitionSnapshot>(1+variants){CloneSnapshot(source)};
            if(variants==0)return results.ToArray();
            var rng=new Random(seed);
            for(int v=0;v<variants;v++){
                var clone=CloneSnapshot(source);
                for(int i=0;i<clone.Angles.Length;i++){
                    double phase=((clone.Angles[i]+(rng.NextDouble()*6.0-3.0))%360.0+360.0)%360.0;
                    double scale=1.0+(rng.NextDouble()*0.10-0.05);
                    double charge=clone.Charges[i]*scale;
                    if(Math.Abs(charge)<1e-12)charge=clone.Charges[i]>=0?1e-9:-1e-9;
                    clone.Angles[i]=phase;clone.Charges[i]=charge;
                }
                clone.Scope=(source.Scope??"")+"|expand#"+(v+1).ToString(CultureInfo.InvariantCulture);
                clone.Context=(source.Context??"")+"-x"+(v+1).ToString(CultureInfo.InvariantCulture);
                results.Add(clone);
            }
            return results.ToArray();
        }
        static RecognitionSnapshot CloneSnapshot(RecognitionSnapshot source)
        {
            return new RecognitionSnapshot{
                Rows=(int[])source.Rows.Clone(),Phases=(int[])source.Phases.Clone(),
                Angles=(double[])source.Angles.Clone(),Charges=(double[])source.Charges.Clone(),
                Context=source.Context,Scope=source.Scope,ModelPath=source.ModelPath,Project=source.Project,English=source.English
            };
        }
        /// <summary>Highest model score among the selected label keys (0..1). Missing scores count as 0.</summary>
        internal static double OverlapScore(RecognitionSnapshot input,ReviewTask12Runtime runtime,string labelKeys)
        {
            if(runtime==null||String.IsNullOrWhiteSpace(labelKeys))return 0;
            var keys=new HashSet<string>(labelKeys.Split(new[]{'+'},StringSplitOptions.RemoveEmptyEntries),StringComparer.OrdinalIgnoreCase);
            if(keys.Count==0)return 0;
            var result=PdReviewControl.RunRecognitionCore(input,runtime,()=>true);
            double max=0;
            foreach(var score in result.ClassScores)if(keys.Contains(score.LabelKey)&&score.Score>max)max=score.Score;
            return max;
        }
        internal static bool ConflictsWithExistingModel(RecognitionSnapshot input,ReviewTask12Runtime runtime,string labelKeys,out double overlap)
        {
            overlap=OverlapScore(input,runtime,labelKeys);
            return overlap>ConflictScoreThreshold;
        }
        internal static string WriteRetrainReceipt(string learningRoot,string model,string algorithm,int originals,int variants,bool engineTrained,string detail)
        {
            string root=Path.GetFullPath(learningRoot);
            Directory.CreateDirectory(root);
            var receipt=new Dictionary<string,object>{
                {"schema","HVX.DefectLearning.Retrain/1"},{"created_utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)},
                {"model_path",model??""},{"algorithm",algorithm??""},{"original_samples",originals},{"expanded_variants",variants},
                {"engine_trained",engineTrained},{"detail",detail??""},{"production_model_modified",engineTrained}
            };
            string path=Path.Combine(root,"retrain-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",CultureInfo.InvariantCulture)+".json");
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(receipt),new UTF8Encoding(false));
            return path;
        }
    }
}
