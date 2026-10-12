using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

// Exact managed methods, synthetic state only. No control constructors,
// message boxes, local learning files, algorithm worker, SDK or hardware.
static class LocalAiSuggestionFeedbackOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;}
    static void Set(object value,string field,object data){value.GetType().GetField(field,F).SetValue(value,data);}
    static object Get(object value,string name){return value.GetType().GetProperty(name,F).GetValue(value,null);}
    static object Call(object value,string name,params object[] args)
    {return value.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static object Suggestion(Type type,bool available,double threshold,int reflection,string status)
    {return Activator.CreateInstance(type,F,null,new object[]{available,3,.5,threshold,reflection,status},null);}
    static int Main(string[] args)
    {
        try{
            var assembly=Assembly.LoadFrom(args[0]);var reviewType=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            var mapType=assembly.GetType("LabVIEWPdMap.PdMapControl",true);var suggestionType=assembly.GetType("HVX.PdReview.LocalAiAnalysisSuggestion",true);
            var outcomeType=assembly.GetType("LabVIEWPdMap.ReflectionMoveResult",true);
            var review=FormatterServices.GetUninitializedObject(reviewType);var map=FormatterServices.GetUninitializedObject(mapType);
            Set(map,"pulse",new double[4096]);Set(map,"reflectionSelected",true);Set(map,"reflection",200);Set(map,"incident",100);
            Set(map,"cableLengthM",415d);Set(map,"dtNs",8d);Set(map,"velocityMPerUs",170d);Set(map,"velocityConfigured",true);Set(map,"acceptedPositionErrorPercent",1d);
            Set(map,"curveVisibility",new[]{true,false,false,false,false,false,true,true});Set(map,"originalParameterRow",new double[0]);
            Set(review,"pdmap",map);Set(review,"selected",-1);Set(review,"working",new double[1,36]);Set(review,"overviewPath","");
            Set(review,"acceptanceConfirmed",new[]{false});Set(review,"automaticAcceptanceRows",new[]{false});
            Set(review,"enableAiFeatures",true);
            // A closed inert journal guarantees the test cannot create log files.
            var journal=FormatterServices.GetUninitializedObject(assembly.GetType("HVX.PdReview.ReviewSessionJournal",true));
            Set(journal,"gate",new object());Set(journal,"closed",true);Set(review,"operationJournal",journal);
            var tree=FormatterServices.GetUninitializedObject(assembly.GetType("LabVIEWLocationTree.LocationTreeControl",true));Set(review,"analysisTree",tree);
            var thresholdsField=reviewType.GetField("fileThresholds",F);var thresholds=Activator.CreateInstance(thresholdsField.FieldType);thresholdsField.SetValue(review,thresholds);
            var accepted=reviewType.GetField("working",F).GetValue(review) as double[,];accepted[0,35]=1;accepted[0,33]=.205;

            Set(map,"capacitorStatisticsMode",true);
            Check(Call(map,"MoveReflectionToIndexWithResult",200).ToString()=="UnsupportedMode","unsupported capacitor mode is explicit");
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,100d,200,"available")),"unsupported suggestion is not reported applied");
            Check(((string)Get(review,"AiLearningStatus")).Contains("不支持"),"unsupported reason reaches visible operation status");
            Check(((IDictionary)thresholds).Count==0,"failed reflection does not partially update thresholds");
            Set(map,"capacitorStatisticsMode",false);Set(map,"pulse",new double[0]);
            Check(Call(map,"MoveReflectionToIndexWithResult",200).ToString()=="NoWaveform","missing waveform is explicit");
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,100d,200,"available")),"no waveform is not reported applied");
            Check(((string)Get(review,"AiLearningStatus")).Contains("尚未加载波形"),"no-waveform reason reaches visible status");
            Set(map,"pulse",new double[4096]);
            Check(Call(map,"MoveReflectionToIndexWithResult",200).ToString()=="AlreadyAtTarget","existing suggestion point is a distinct no-op");
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,Double.NaN,200,"available")),"already at target without threshold change returns false");
            Check(((string)Get(review,"AiLearningStatus")).Contains("已位于建议点")&&((string)Get(review,"AiLearningStatus")).Contains("未更改"),"no-op is visible and not labelled applied");
            Check((string)reviewType.GetField("operationStatus",F).GetValue(review)==(string)Get(review,"AiLearningStatus"),"button result published to ordinary status text");
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,false,Double.NaN,-1,"synthetic unavailable")),"unavailable sample set returns false");
            Check((string)Get(review,"AiLearningStatus")=="synthetic unavailable","unavailable sample reason survives");
            Set(review,"aiLearningMode",true);
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,100d,200,"available")),"learning mode does not apply suggestions");
            Check(((string)Get(review,"AiLearningStatus")).Contains("不应用"),"learning-mode block is visible");Set(review,"aiLearningMode",false);
            Set(review,"analysisBusy",true);
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,100d,200,"available")),"running analysis blocks suggestion mutation");
            Check(((string)Get(review,"AiLearningStatus")).Contains("等待"),"running analysis reason visible");Set(review,"analysisBusy",false);
            Set(review,"enableAiFeatures",false);
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestion"),"disabled AI public action returns false");
            Check(((string)Get(review,"AiLearningStatus")).Contains("关闭"),"disabled reason visible");Set(review,"enableAiFeatures",true);

            // Already-at-target may still apply a genuinely different threshold;
            // a repeated same-value choice preserves human-confirmed provenance.
            Check((bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,100d,200,"available")),"threshold-only change is reported separately");
            Check(((string)Get(review,"AiLearningStatus")).Contains("仅更新"),"threshold-only result does not claim cursor movement");
            var choice=((IDictionary)thresholds)[""];Set(choice,"HumanConfirmed",true);
            Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestionCore",Suggestion(suggestionType,true,100d,200,"available")),"identical repeat is no change");
            Check((bool)choice.GetType().GetField("HumanConfirmed",F).GetValue(choice),"unchanged threshold retains human-confirmation provenance");
            foreach(string code in new[]{"UnsupportedMode","NoWaveform","SelectionUnavailable","AlreadyAtTarget","Moved"}){
                string message=(string)Call(review,"LocalAiMoveMessage",Enum.Parse(outcomeType,code),false);
                Check(message.Length>0,"every move outcome has user-visible feedback: "+code);
                Check(code=="Moved"?message.Contains("已移动"):!message.Contains("已移动"),"no-op/failure never claims cursor movement: "+code);
            }
            Check(accepted[0,35]==1&&accepted[0,33]==.205,"feedback does not change acceptance or algorithm score");
            Check(!((bool[])reviewType.GetField("acceptanceConfirmed",F).GetValue(review))[0],"AI suggestion never confirms human acceptance");
            Check((int)mapType.GetField("reflection",F).GetValue(map)==200,"all no-op branches retain cursor");
            Console.WriteLine("LOCAL_AI_SUGGESTION_FEEDBACK_OFFLINE_PASS="+checks+";workerUsed=false;productConstructed=false;hardwareUsed=false;storeRead=false");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
}
