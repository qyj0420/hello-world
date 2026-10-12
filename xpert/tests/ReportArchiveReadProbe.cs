using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
internal static class ReportArchiveReadProbe
{
    static int Main(string[] args)
    {
        try {
            var a=Assembly.LoadFrom(args[0]);
            var t=a.GetType("HVX.ReportGenerator.AnalysisPackageReader",true);
            var p=t.GetMethod("Read").Invoke(null,new object[]{args[1]});
            foreach(var name in new[]{"ReportInputStatus","HasReportInputSnapshot","EffectiveSelectionStatus"})
                Console.WriteLine(name+"="+p.GetType().GetProperty(name).GetValue(p,null));
            var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
            var session=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(args[1],"session.json")));
            foreach(object[] setting in (object[])session["settings"])if((string)setting[0]=="ReportInputContext") {
                var context=(Dictionary<string,object>)json.DeserializeObject((string)setting[1]);
                var rows=(object[])context["ProjectionRows"];var positions=(object[])context["RawPositionsM"];
                var matrix=(double[,])p.GetType().GetProperty("LocationMatrix").GetValue(p,null);
                int count=0;double max=0;
                for(int i=0;i<rows.Length;i++){double x=Convert.ToDouble(positions[i]),y=matrix[Convert.ToInt32(rows[i]),13];if(x!=y){count++;max=Math.Max(max,Math.Abs(x-y));}}
                Console.WriteLine("RawPositionMismatchCount="+count+"; MaxDifferenceM="+max.ToString("R"));
            }
            return (bool)p.GetType().GetProperty("HasReportInputSnapshot").GetValue(p,null)?0:2;
        } catch(Exception ex) { Console.WriteLine(ex);return 1; }
    }
}
