using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    public sealed class ProjectInsulationStatus
    {
        public string StateCode { get; internal set; }
        public int SeverityCode { get; internal set; }
        public string StatusText { get; internal set; }
        public string AnalysisId { get; internal set; }
        public string AnalysisName { get; internal set; }
        public DateTime AnalysisSavedUtc { get; internal set; }
        public string Detail { get; internal set; }
        public string ApiVersion { get; internal set; }
        public bool UsesEvidenceV3 { get { return (ApiVersion ?? String.Empty).StartsWith("hvx-conclusion-dotnet-3.", StringComparison.Ordinal); } }
    }

    // The project list reads saved metadata only, never waveform payloads or report prose.
    public static class ProjectInsulationStatusReader
    {
        const int JsonLimit=4*1024*1024, CandidateLimit=4096;
        static readonly Regex LegacyName=new Regex(@"^Analysis_\d{4}-\d{2}-\d{2}-\d{2}'\d{2}'\d{2}\.\d{2}\.(data|bin)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        sealed class Candidate { internal string Path,Id,Name,Error; internal DateTime Saved; internal Dictionary<string,object> Manifest; }
        static ProjectInsulationStatus Result(string state,Candidate c,string detail,int grade=-1)
        {
            string api=c==null?String.Empty:Text(Value(c.Manifest,"Conclusion") as Dictionary<string,object>,"Api")??String.Empty;
            bool v3=api.StartsWith("hvx-conclusion-dotnet-3.",StringComparison.Ordinal);
            string[] labels=v3?new[]{"证据不足","计划跟踪","重点关注","优先核查"}:new[]{"记录","观察","重点关注","及时处理"};
            return new ProjectInsulationStatus{StateCode=state,SeverityCode=grade,StatusText=state=="Unanalyzed"?"未分析":state=="NoConclusion"?"未生成结论":state=="Invalid"?"结论不可用":labels[grade],AnalysisId=c==null?"":c.Id??"",AnalysisName=c==null?"":c.Name??"",AnalysisSavedUtc=c==null?DateTime.MinValue:c.Saved,Detail=detail,ApiVersion=api};
        }
        static bool Fatal(Exception e) { return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException||e is System.Threading.ThreadAbortException; }
        static void NoLink(string path)
        { for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("不读取链接或重解析点中的分析。"); }
        static Dictionary<string,object> Json(string path)
        { NoLink(path);var f=new FileInfo(path);if(f.Length>JsonLimit)throw new InvalidDataException("分析元数据超出读取上限。");return new JavaScriptSerializer{MaxJsonLength=JsonLimit,RecursionLimit=20}.DeserializeObject(new UTF8Encoding(false,true).GetString(File.ReadAllBytes(path))) as Dictionary<string,object>; }
        static object Value(Dictionary<string,object> d,string key) { object value;return d!=null&&d.TryGetValue(key,out value)?value:null; }
        static string Text(Dictionary<string,object> d,string key) { return Value(d,key) as string; }
        static DateTime Utc(string value)
        { DateTime parsed;if(String.IsNullOrWhiteSpace(value)||!DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed))throw new InvalidDataException("分析保存时间无效。");return parsed.ToUniversalTime(); }
        static string Member(string directory,string relative)
        {
            if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.IndexOf(':')>=0||relative.Split('\\','/').Any(p=>p==".."||p=="."||p.Length==0))throw new InvalidDataException("分析成员路径无效。");
            string root=Path.GetFullPath(directory).TrimEnd('\\','/')+Path.DirectorySeparatorChar,path=Path.GetFullPath(Path.Combine(root,relative));
            if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("分析成员越界。");NoLink(path);return path;
        }
        static bool Hash(string value) { return value!=null&&Regex.IsMatch(value,"^[0-9a-fA-F]{64}$"); }
        static void Add(List<Candidate> items,string file,bool named)
        {
            if(items.Count>=CandidateLimit)throw new InvalidDataException("分析目录数量超出安全读取上限。");
            var c=new Candidate{Path=file,Id=Path.GetFileName(Path.GetDirectoryName(file)),Name=Path.GetFileNameWithoutExtension(file),Saved=File.GetLastWriteTimeUtc(file)};items.Add(c);
            try
            {
                NoLink(file);
                if(!named)
                {
                    if(String.Equals(Path.GetFileName(file),"session.json",StringComparison.OrdinalIgnoreCase)&&Json(file)==null)throw new InvalidDataException("分析会话格式无效。");
                    return;
                }
                c.Manifest=Json(file);if(c.Manifest==null)throw new InvalidDataException("分析清单不是有效对象。");
                c.Id=Text(c.Manifest,"Id")??c.Id;c.Name=Text(c.Manifest,"Name")??c.Name;
                string saved=Text(c.Manifest,"SavedUtc");if(saved!=null)c.Saved=Utc(saved);
                else {string created=Text(c.Manifest,"CreatedUtc");if(created!=null&&c.Saved==DateTime.MinValue)c.Saved=Utc(created);}
                if(Text(c.Manifest,"Schema")!="HVX.NamedAnalysis"||Convert.ToInt32(Value(c.Manifest,"Version"),CultureInfo.InvariantCulture)!=1)throw new InvalidDataException("分析清单格式不受支持。");
                string parent=Path.GetDirectoryName(file),container=Path.GetDirectoryName(parent);
                bool namedChild=String.Equals(Path.GetFileName(container),"Analysis report",StringComparison.OrdinalIgnoreCase);
                if(String.IsNullOrWhiteSpace(c.Id)||c.Id!=Path.GetFileName(c.Id)||c.Id.IndexOfAny(Path.GetInvalidFileNameChars())>=0||c.Id.StartsWith(".",StringComparison.Ordinal)||(namedChild&&!String.Equals(c.Id,Path.GetFileName(parent),StringComparison.Ordinal)))throw new InvalidDataException("分析身份与目录不一致。");
            }
            catch(Exception ex){if(Fatal(ex))throw;c.Error=ex.Message;}
        }
        static void Scan(List<Candidate> items,string directory,bool subdirectories)
        {
            if(!Directory.Exists(directory))return;NoLink(directory);
            foreach(string file in Directory.EnumerateFiles(directory))
            {
                string name=Path.GetFileName(file);
                if(String.Equals(name,"analysis.json",StringComparison.OrdinalIgnoreCase))Add(items,file,true);
                else if(String.Equals(name,"session.json",StringComparison.OrdinalIgnoreCase)&&!File.Exists(Path.Combine(directory,"analysis.json")))Add(items,file,false);
                else if(LegacyName.IsMatch(name))Add(items,file,false);
            }
            int scanned=0;
            if(subdirectories)foreach(string dir in Directory.EnumerateDirectories(directory))
            {
                if(++scanned>CandidateLimit)throw new InvalidDataException("分析目录数量超出安全读取上限。");
                if(Path.GetFileName(dir).StartsWith(".",StringComparison.Ordinal))continue;
                string manifest=Path.Combine(dir,"analysis.json");if(File.Exists(manifest))Add(items,manifest,true);
                else if(File.Exists(Path.Combine(dir,"session.json")))Add(items,Path.Combine(dir,"session.json"),false);
            }
        }
        public static ProjectInsulationStatus Read(string projectRoot)
        {
            Candidate latest=null;
            try
            {
                string root=Path.GetFullPath(projectRoot);NoLink(root);var candidates=new List<Candidate>();Scan(candidates,root,false);Scan(candidates,Path.Combine(root,"Analysis report"),true);
                latest=candidates.OrderByDescending(c=>c.Saved).ThenByDescending(c=>c.Path,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if(latest==null)return Result("Unanalyzed",null,"尚无已保存分析。");
                if(latest.Error!=null)return Result("Invalid",latest,latest.Path+"："+latest.Error);
                var m=latest.Manifest;if(m==null)return Result("NoConclusion",latest,"已有旧版分析/会话，但没有可读取的结构化结论；不回退其他分析。\n"+latest.Path);
                var conclusion=Value(m,"Conclusion") as Dictionary<string,object>;
                if(conclusion==null){if(Value(m,"Conclusion")!=null)throw new InvalidDataException("结论快照格式无效。");return Result("NoConclusion",latest,"最新保存分析尚无有效结构化结论；不依据建议文字猜测等级。\n"+latest.Path);}
                if(Text(conclusion,"Schema")!="HVX.AnalysisConclusion.1"||Text(conclusion,"AnalysisId")!=latest.Id||String.IsNullOrWhiteSpace(Text(conclusion,"SummaryText")))throw new InvalidDataException("结论身份或格式无效。");
                DateTime generated=Utc(Text(conclusion,"GeneratedUtc"));
                if(Text(m,"SavedUtc")!=null&&generated>latest.Saved)throw new InvalidDataException("结论时间晚于所属分析的保存时间。");
                object[] files=Value(m,"Files") as object[];if(files==null||files.Length>256)throw new InvalidDataException("分析文件清单无效。");
                var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(object item in files)
                {
                    var f=item as Dictionary<string,object>;string relative=Text(f,"Path"),path=Member(Path.GetDirectoryName(latest.Path),relative),hash=Text(f,"Sha256");
                    long length=Convert.ToInt64(Value(f,"Bytes"),CultureInfo.InvariantCulture);
                    if(Value(f,"Bytes")==null||hashes.ContainsKey(relative)||!Hash(hash)||length<0||!File.Exists(path)||new FileInfo(path).Length!=length)throw new InvalidDataException("分析文件清单缺失或已变化。");hashes.Add(relative,hash);
                }
                foreach(string key in new[]{"parameters.bin","session.json"})
                {string h;if(!hashes.TryGetValue(key,out h)||!String.Equals(h,Text(conclusion,key=="parameters.bin"?"ParametersSha256":"SessionSha256"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("结论与分析快照不匹配。");}
                object[] regions=Value(conclusion,"Regions") as object[];if(regions==null||regions.Length==0||regions.Length>100000)throw new InvalidDataException("结论区域数据无效。");
                int maximum=0;
                foreach(object item in regions)
                {
                    var row=item as Dictionary<string,object>;double phase=Convert.ToDouble(Value(row,"PhaseMask"),CultureInfo.InvariantCulture),code=Convert.ToDouble(Value(row,"ActionCode"),CultureInfo.InvariantCulture),position=Convert.ToDouble(Value(row,"PositionM"),CultureInfo.InvariantCulture);
                    if(row==null||Value(row,"PhaseMask")==null||Value(row,"ActionCode")==null||Value(row,"PositionM")==null||phase<1||phase>7||phase!=Math.Truncate(phase)||code<0||code>3||code!=Math.Truncate(code)||Double.IsNaN(position)||Double.IsInfinity(position)||position<0)throw new InvalidDataException("结论相别、位置或等级无效。");maximum=Math.Max(maximum,(int)code);
                }
                return Result("Ready",latest,"最新已保存分析的结构化结论快照；取全部相/区域的最高处置等级。记录不等于正常。完整数据校验在加载分析时执行。\n"+latest.Path,maximum);
            }
            catch(Exception ex){if(Fatal(ex))throw;return Result("Invalid",latest,ex.Message);}
        }
    }
}
