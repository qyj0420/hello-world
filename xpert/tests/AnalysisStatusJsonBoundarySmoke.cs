using System;
using System.Collections.Generic;
using System.IO;
using HVX.PdReview;

internal static class AnalysisStatusJsonBoundarySmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static bool Reject(Action action)
    {try{action();return false;}catch(InvalidDataException){return true;}catch(ArgumentException){return true;}catch(InvalidOperationException){return true;}}
    static int Main()
    {
        try {
            Check(ReviewStatusJson.MaximumCharacters==16*1024*1024&&ReviewStatusJson.MaximumDepth==24,"existing archive budgets reused");
            var parsed=(Dictionary<string,object>)ReviewStatusJson.DeserializeObject("{\"current_analysis\":{\"output_threshold\":45},\"note\":\"校准 A相\"}");
            Check(parsed.ContainsKey("current_analysis")&&parsed["note"].ToString()=="校准 A相","valid status and Unicode unchanged");
            string special=ReviewStatusJson.Serialize(new Dictionary<string,object>{{"text","[[[[{{{{\\\"escaped\"}}}}]]]]"}});
            Check(((Dictionary<string,object>)ReviewStatusJson.DeserializeObject(special))["text"].ToString().Contains("escaped"),"quoted and escaped braces are not nesting");
            string limit="\""+new string('a',ReviewStatusJson.MaximumCharacters-2)+"\"";
            Check(((string)ReviewStatusJson.DeserializeObject(limit)).Length==ReviewStatusJson.MaximumCharacters-2,"exact character budget accepted");
            Check(Reject(()=>ReviewStatusJson.DeserializeObject(limit+" ")),"one character over limit rejected before parse");
            string deep=new string('[',ReviewStatusJson.MaximumDepth+1)+"0"+new string(']',ReviewStatusJson.MaximumDepth+1);
            Check(Reject(()=>ReviewStatusJson.DeserializeObject(deep)),"excessive nesting rejected before recursive parse");
            Check(Reject(()=>ReviewStatusJson.DeserializeObject(new string('[',256)+"0"+new string(']',256))),"old 256-level recursion no longer accepted");
            Check(Reject(()=>ReviewStatusJson.DeserializeObject("[}")),"mismatched malformed JSON rejected");
            bool syntaxFailedRecord=false;
            try{ReviewStatusJson.DeserializeObject("{\"current_analysis\":}");}catch(InvalidDataException){syntaxFailedRecord=true;}
            Check(syntaxFailedRecord,"parser syntax failures have record-failure semantics");
            Check(Reject(()=>ReviewStatusJson.DeserializeObject("{\"truncated\": [")),"truncated status rejected");
            Check(Reject(()=>ReviewStatusJson.DeserializeObject(null)),"missing status rejected");
            Check(Reject(()=>ReviewStatusJson.Serialize(new Dictionary<string,object>{{"text",new string('x',ReviewStatusJson.MaximumCharacters)}})),"oversized augmented status not serialized or truncated");
            Console.WriteLine("ANALYSIS_STATUS_JSON_BOUNDARY_PASS="+checks+";HardwareAccessed=false;ProductEntryStarted=false;SDKCalled=false;RecordsTruncated=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
