using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;

// Pure managed source/score presentation checks: no control constructors, UI,
// preferences, real project, algorithm worker, SDK or device are invoked.
static class ReviewAcceptancePresentationOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string detail){if(!value)throw new Exception(detail);checks++;}
    static object Call(Type type,object instance,string name,params object[] args)
    {return type.GetMethods(F).Single(method=>method.Name==name&&method.GetParameters().Length==args.Length).Invoke(instance,args);}
    static void Set(object instance,string name,object value){instance.GetType().GetField(name,F).SetValue(instance,value);}
    static object Get(object instance,string name){return instance.GetType().GetProperty(name,F).GetValue(instance,null);}
    static int Main(string[] args)
    {
        try{
            var assembly=Assembly.LoadFrom(args[0]);var type=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            var spectrum=assembly.GetType("HVX.UnifiedSpectrum.UnifiedSpectrumControl",true);
            var instance=FormatterServices.GetUninitializedObject(type);
            var pdmap=FormatterServices.GetUninitializedObject(assembly.GetType("LabVIEWPdMap.PdMapControl",true));
            Set(pdmap,"pulse",new double[4096]);Set(pdmap,"reflectionSelected",true);Set(pdmap,"incident",100);Set(pdmap,"reflection",200);
            Set(pdmap,"cableLengthM",415d);Set(pdmap,"dtNs",8d);Set(pdmap,"velocityMPerUs",170d);Set(pdmap,"velocityConfigured",true);Set(pdmap,"acceptedPositionErrorPercent",1d);
            var scoreControl=FormatterServices.GetUninitializedObject(spectrum);
            Set(scoreControl,"classificationLowThresholdPercent",30d);Set(scoreControl,"classificationHighThresholdPercent",60d);
            Set(instance,"pdmap",pdmap);Set(instance,"spectrum",scoreControl);
            var matrix=new double[1,36];matrix[0,10]=100;matrix[0,11]=200;matrix[0,12]=100;matrix[0,31]=0;matrix[0,32]=4095;
            Set(instance,"working",matrix);Set(instance,"selected",0);Set(instance,"removed",new[]{false});Set(instance,"reviewed",new[]{false});
            foreach(int method in new[]{8,9})foreach(double score in new[]{.205,.461}){
                matrix[0,26]=method;matrix[0,33]=score;matrix[0,35]=1;
                Set(instance,"acceptanceConfirmed",new[]{false});Set(instance,"automaticAcceptanceRows",new[]{true});
                Check((string)Get(instance,"CurrentAcceptanceSourceCode")=="Automatic","known automatic inclusion source at score "+score);
                string explanation=(string)Call(type,instance,"CurrentDecisionExplanation");
                Check(explanation.Contains("自动纳入")&&explanation.Contains("未经人工确认"),"automatic inclusion cannot imply human/physical confirmation");
                Check((string)Call(type,null,"ScoreBandCode",score,30d,60d,false)==(score<.3?"Low":"Medium"),"20.5% low / 46.1% medium score band");
                string scoreLabel=(string)Call(type,instance,"CurrentScoreLabel",false);
                Check(scoreLabel.Contains("匹配评分")&&scoreLabel.Contains(score<.3?"低":"中")&&!scoreLabel.Contains("确定"),"actual E metric label separates low/medium matching score from certainty");
                Check((string)Call(type,instance,"CurrentAcceptanceIndicatorText")=="自动纳入","actual automatic badge does not show confirmed");
                Check((int)Call(spectrum,null,"ClassifySourceRow",matrix,0,30d,60d)==(score<.3?0:1),"real spectrum classifier agrees with score band");
                Check(matrix[0,33]==score&&matrix[0,35]==1,"presentation does not alter score or statistical inclusion");
                Set(instance,"acceptanceConfirmed",new[]{true});
                Check((string)Get(instance,"CurrentAcceptanceSourceCode")=="Manual","explicit human acceptance takes precedence over prior automatic origin");
                Check((string)Call(type,instance,"CurrentAcceptanceIndicatorText")=="人工接受","actual human badge describes acceptance source");
                Check(((string)Call(type,instance,"CurrentDecisionExplanation")).Contains("不代表物理确认"),"manual acceptance remains separate from physical confirmation");
                Set(instance,"acceptanceConfirmed",new[]{false});Set(instance,"automaticAcceptanceRows",new[]{false});
                Check((string)Get(instance,"CurrentAcceptanceSourceCode")=="Unknown","old/external accepted result has unknown source");
                Check((string)Call(type,instance,"CurrentAcceptanceIndicatorText")=="来源未知","actual historical badge does not invent automatic or manual source");
                Check(((string)Call(type,instance,"CurrentDecisionExplanation")).Contains("历史来源未知"),"unknown historical source is visible");
                matrix[0,35]=0;
                Check((string)Get(instance,"CurrentAcceptanceSourceCode")=="NotAccepted","unaccepted result never shown as accepted source");
            }
            foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,-.01,1.01})Check((string)Call(type,null,"ScoreBandCode",invalid,30d,60d,false)=="Unavailable","invalid scores do not imply a category");
            Check((string)Call(type,null,"ScoreBandCode",.95,30d,60d,true)=="Unavailable","unresolved timing remains pending despite high numeric score");
            Check((string)Call(type,null,"ScoreBandCode",.3,30d,60d,false)=="Medium"&&(string)Call(type,null,"ScoreBandCode",.6,30d,60d,false)=="High","numeric category boundaries unchanged");
            var merged=new double[4,36];for(int r=0;r<3;r++)merged[r,35]=1;
            var retained=(bool[])Call(type,null,"RetainAutomaticAcceptanceRows",new[]{true,false},new[]{1,0,-1,-1},merged,true);
            Check(retained.SequenceEqual(new[]{false,true,true,false}),"subset/reorder preserves old source and records only actually auto-included new rows");
            Check(((bool[])Call(type,null,"RetainAutomaticAcceptanceRows",new bool[0],new[]{-1,-1,-1,-1},merged,false)).All(value=>!value),"external new rows do not invent automatic provenance");
            var settings=new Dictionary<string,string>{{"AutomaticAcceptanceRows",new String(retained.Select(value=>value?'1':'0').ToArray())}};
            var saved=new JavaScriptSerializer().Serialize(settings);var loaded=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(saved);
            Check(((bool[])Call(type,null,"ParseAutomaticAcceptanceRows",loaded,4)).SequenceEqual(retained),"saved source flags reload losslessly");
            Check(((bool[])Call(type,null,"ParseAutomaticAcceptanceRows",new Dictionary<string,string>(),4)).All(value=>!value),"legacy session missing source field remains unknown");
            foreach(string invalid in new[]{"1","10000","1x00",null}){
                loaded["AutomaticAcceptanceRows"]=invalid;bool rejected=false;
                try{Call(type,null,"ParseAutomaticAcceptanceRows",loaded,4);}catch(TargetInvocationException ex){rejected=ex.InnerException is ArgumentException;}
                Check(rejected,"malformed automatic source state rejected");
            }
            Set(instance,"automaticAcceptanceRows",new[]{true});var returned=(bool[])Get(instance,"AutomaticAcceptanceRows");returned[0]=false;
            Check(((bool[])Get(instance,"AutomaticAcceptanceRows"))[0],"public source snapshot cannot mutate internal provenance");
            Console.WriteLine("REVIEW_ACCEPTANCE_PRESENTATION_OFFLINE_PASS="+checks+";scores=20.5,46.1;workerUsed=false;productConstructed=false;hardwareUsed=false");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
}
