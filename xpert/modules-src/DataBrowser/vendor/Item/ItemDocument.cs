using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace LabVIEWItemInformation
{
 internal sealed class ItemDocument
 {
  internal Dictionary<string,string> Values { get; private set; }
  internal string SourcePath { get; private set; }
  internal string SourceEncodingName { get; private set; }
  internal string Warnings { get; private set; }
  internal string[] CablePhases { get { return phases.Length == 0 ? new string[]{"ALL"} : (string[])phases.Clone(); } }
  internal double[][] CablePositions { get {
   if (positions.Length != 0) { double[][] result=new double[positions.Length][]; for(int i=0;i<result.Length;i++) result[i]=(double[])positions[i].Clone(); return result; }
   double length; return Number(Values["CableLength"],out length)&&length>0 ? new double[][] {new double[]{0,length}} : new double[0][];
  } }
  private static readonly string[] Keys={"TestingOrganization","TestPersonnel","TestLocation","StartName","EndName","StartType","EndType","StartDispatch","EndDispatch","LineName","InstallationDate","CableModel","CableLength","CoreCount","PhaseVoltage","TestMode","Remarks","A1","A2","B1","B2","C1","C2"};
  private static readonly string[] Names={"所属单位","测试人员","测试地点","始端地点","终端地点","始端开关类型","终端开关类型","调度号1","调度号2","线路名称","铺设时间","电缆型号","电缆长度","电缆芯数","相电压","加压模式","备注","A相测试前绝缘电阻","A相测试后绝缘电阻","B相测试前绝缘电阻","B相测试后绝缘电阻","C相测试前绝缘电阻","C相测试后绝缘电阻"};
  private static readonly Dictionary<string,string> Aliases=MakeAliases();
  private readonly Dictionary<string,string> original=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string,int> fieldLines=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
  private readonly List<Line> lines=new List<Line>();
  private Encoding encoding;
  private byte[] preamble;
  private string newline="\r\n";
  private int insertionLine;
  private string[] phases=new string[0];
  private double[][] positions=new double[0][];
  private sealed class Line { internal string Raw,Ending,Name,Value,Prefix,Suffix; internal bool Quoted; }
  private ItemDocument() {
   Values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); Warnings=string.Empty;
   foreach(string key in Keys) Values[key]=original[key]=string.Empty;
  }
  internal void SetCableProjection(string[] projectedPhases,double[][] projectedPositions) {
   phases=(string[])projectedPhases.Clone(); positions=projectedPositions.Select(row=>(double[])row.Clone()).ToArray();
  }
  internal static ItemDocument Load(string fileOrProjectPath) {
   if(string.IsNullOrWhiteSpace(fileOrProjectPath)) throw new ArgumentException("Specify an ITEM file or project folder.");
   string path=Path.GetFullPath(fileOrProjectPath);
   if(Directory.Exists(path)) path=Path.Combine(path,"ITEM.in");
   if(!File.Exists(path)) throw new FileNotFoundException("ITEM file was not found.",path);
   ItemDocument doc=new ItemDocument(); doc.SourcePath=path;
   string text=ReadText(path,out doc.encoding,out doc.preamble);
   doc.SourceEncodingName=doc.encoding.CodePage==936 ? "GBK (CP936)" : doc.encoding.WebName+(doc.preamble.Length>0?" (BOM)":"");
   Match end=Regex.Match(text,"\r\n|\r|\n"); if(end.Success) doc.newline=end.Value;
   bool item=true; int recognized=0;
   foreach(Match m in Regex.Matches(text,"([^\r\n]*)(\r\n|\r|\n|$)")) {
    if(m.Length==0) continue;
    Line line=Parse(m.Groups[1].Value,m.Groups[2].Value); string trim=line.Raw.Trim();
    if(trim.StartsWith("[")&&trim.EndsWith("]")) item=string.Equals(trim,"[ITEM]",StringComparison.OrdinalIgnoreCase);
    if(item) {
     doc.insertionLine=doc.lines.Count+1;
     string key;
     if(line.Name!=null&&Aliases.TryGetValue(line.Name,out key)) { recognized++; doc.Values[key]=doc.original[key]=line.Value; doc.fieldLines[key]=doc.lines.Count; }
    }
    doc.lines.Add(line);
   }
   if(recognized==0) throw new InvalidDataException("The file contains no recognized ITEM information fields.");
   doc.ReadCable(Path.Combine(Path.GetDirectoryName(path),"CABLE.in")); return doc;
  }
  internal void SaveCopy(string path) {
   if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Specify a new ITEM output path.");
   string full=Path.GetFullPath(path); if(Directory.Exists(full)) full=Path.Combine(full,"ITEM.in");
   if(string.Equals(full,SourcePath,StringComparison.OrdinalIgnoreCase)||File.Exists(full)) throw new IOException("Output already exists. Choose a new file; source ITEM files are never overwritten.");
   // This is a newly created copy, not an in-place legacy rewrite. Known ITEM
   // names use the fixed English protocol and Unicode values remain intact.
   string[] output=new string[lines.Count]; bool item=true;
   var lastKnown=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
   for(int i=0;i<lines.Count;i++) {
    Line line=lines[i]; string trim=line.Raw.Trim();
    if(trim.StartsWith("[")&&trim.EndsWith("]")) item=string.Equals(trim,"[ITEM]",StringComparison.OrdinalIgnoreCase);
    output[i]=line.Raw+line.Ending;
    if(!item||line.Name==null||!HVX.ProjectModel.ProjectMetadataKeys.IsKnown(line.Name)) continue;
    string key=HVX.ProjectModel.ProjectMetadataKeys.Canonical(line.Name); int duplicate;
    if(lastKnown.TryGetValue(key,out duplicate)) output[duplicate]=string.Empty;
    lastKnown[key]=i;
    output[i]=key+": = "+line.Raw.Substring(line.Prefix.Length)+line.Ending;
   }
   StringBuilder added=new StringBuilder();
   for(int i=0;i<Keys.Length;i++) {
    string value; if(!Values.TryGetValue(Keys[i],out value)) value=string.Empty; value=value??string.Empty;
    if(value==original[Keys[i]]) continue;
    int index;
    if(fieldLines.TryGetValue(Keys[i],out index)) {
     Line line=lines[index]; bool quote=line.Quoted||value.IndexOfAny(new char[]{'\r','\n','\t','"'})>=0||value!=value.Trim();
     output[index]=Keys[i]+": = "+(quote?"\""+Escape(value)+"\"":value)+line.Suffix+line.Ending;
    } else added.Append(Keys[i]).Append(": = \"").Append(Escape(value)).Append('"').Append(newline);
   }
   StringBuilder final=new StringBuilder();
   for(int i=0;i<=output.Length;i++) {
    if(i==insertionLine&&added.Length>0) { if(final.Length>0&&final[final.Length-1]!='\r'&&final[final.Length-1]!='\n') final.Append(newline); final.Append(added); }
    if(i<output.Length) final.Append(output[i]);
   }
   var targetEncoding=new UTF8Encoding(true,true); byte[] targetPreamble=targetEncoding.GetPreamble();
   string finalText=HVX.ProjectCables.BoundedProjectMetadata.ValidateText(final.ToString());
   if(targetEncoding.GetByteCount(finalText)>HVX.ProjectCables.BoundedProjectMetadata.MaximumBytes-targetPreamble.Length) throw new HVX.ProjectCables.ProjectMetadataLimitException();
   byte[] data=targetEncoding.GetBytes(finalText);
   string directory=Path.GetDirectoryName(full); if(!Directory.Exists(directory)) Directory.CreateDirectory(directory);
   using(FileStream stream=new FileStream(full,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(targetPreamble,0,targetPreamble.Length); stream.Write(data,0,data.Length); }
  }
  private void ReadCable(string path) {
   if(!File.Exists(path)) { Warn("CABLE.in is missing; the diagram uses the current ITEM cable length."); return; }
   List<string> p=new List<string>(); List<double[]> x=new List<double[]>();
   try {
    Encoding enc; byte[] bom; string text=ReadText(path,out enc,out bom); string phase="ALL"; List<double> current=new List<double>();
    foreach(string raw in Regex.Split(text,"\r\n|\r|\n")) {
     string[] parts=raw.Split('\t'); if(parts.Length<2) parts=Regex.Split(raw.Trim(),"\\s+"); if(parts.Length<2) continue;
     if(parts[0].Trim()=="0") { AddBlock(p,x,phase,current); current=new List<double>(); phase=Phase(parts[1]); }
     else if(parts[0].Trim()=="1"&&parts.Length>=4) { double value; if(Number(parts[3],out value)&&value>=0) current.Add(value); else Warn("An invalid CABLE position was ignored."); }
    }
    AddBlock(p,x,phase,current); phases=p.ToArray(); positions=x.ToArray();
    if(positions.Length==0) Warn("CABLE.in has no valid positions; the diagram uses the current ITEM cable length.");
   } catch(HVX.ProjectCables.ProjectMetadataLimitException) { throw; }
     catch(IOException ex) { Warn("CABLE.in could not be read: "+ex.Message); }
     catch(UnauthorizedAccessException ex) { Warn("CABLE.in could not be read: "+ex.Message); }
     catch(DecoderFallbackException ex) { Warn("CABLE.in encoding is invalid: "+ex.Message); }
  }
  private void AddBlock(List<string> p,List<double[]> x,string phase,List<double> values) {
   if(values.Count<2) return; values.Sort(); List<double> unique=new List<double>();
   if(values[0]>0) { unique.Add(0); Warn("CABLE start position was missing; zero was added for the diagram."); }
   foreach(double value in values) if(unique.Count==0||Math.Abs(value-unique[unique.Count-1])>1e-9) unique.Add(value);
   if(unique.Count<2||unique[unique.Count-1]<=0) return;
   for(int i=0;i<p.Count;i++) {
    if(p[i]!=phase||x[i].Length!=unique.Count) continue; bool same=true;
    for(int j=0;j<unique.Count;j++) if(Math.Abs(x[i][j]-unique[j])>1e-9) { same=false; break; }
    if(same) { Warn("Identical repeated CABLE phase blocks were collapsed."); return; }
   }
   p.Add(phase); x.Add(unique.ToArray());
  }
  private static string Phase(string value) {
   value=value.Trim().ToUpperInvariant().Replace("相",string.Empty).Replace("PHASE",string.Empty).Trim();
   if(value=="L1") return "A"; if(value=="L2") return "B"; if(value=="L3") return "C";
   return value=="A"||value=="B"||value=="C" ? value : "ALL";
  }
  private static bool Number(string text,out double value) {
   Match match=Regex.Match(text??string.Empty,"^[ \\t]*([-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?)[ \\t]*(?:m|米)?[ \\t]*$",RegexOptions.IgnoreCase);
   value=double.NaN;
   return match.Success&&double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&!double.IsNaN(value)&&!double.IsInfinity(value);
  }
  private void Warn(string text) { if(Warnings.IndexOf(text,StringComparison.Ordinal)<0) Warnings+=(Warnings.Length>0?"\r\n":"")+text; }
  private static Encoding Strict(int page) { return Encoding.GetEncoding(page,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback); }
  private static string ReadText(string path,out Encoding enc,out byte[] bom) {
   byte[] data=HVX.ProjectCables.BoundedProjectMetadata.ReadBytes(path); int offset=0;
   if(data.Length>=3&&data[0]==239&&data[1]==187&&data[2]==191) { enc=Strict(65001); offset=3; }
   else if(data.Length>=2&&data[0]==255&&data[1]==254) { enc=Strict(1200); offset=2; }
   else if(data.Length>=2&&data[0]==254&&data[1]==255) { enc=Strict(1201); offset=2; }
   else if(data.Length>=4&&data[1]==0&&data[3]==0) enc=Strict(1200);
   else if(data.Length>=4&&data[0]==0&&data[2]==0) enc=Strict(1201);
   else { enc=Strict(65001); try { enc.GetString(data); } catch(DecoderFallbackException) { enc=Strict(936); } }
   bom=new byte[offset]; if(offset>0) Array.Copy(data,bom,offset); return HVX.ProjectCables.BoundedProjectMetadata.ValidateText(enc.GetString(data,offset,data.Length-offset));
  }
  private static Line Parse(string raw,string ending) {
   Line line=new Line{Raw=raw,Ending=ending}; string trim=raw.TrimStart();
   if(trim.Length==0||trim[0]==';'||trim[0]=='#'||trim[0]=='[') return line;
   int keyStart = raw.Length - trim.Length;
   int eq = raw.IndexOf('=', keyStart), tab = raw.IndexOf('\t', keyStart);
   bool alignedEquals = eq >= 0 && tab >= 0 && tab < eq && raw.Substring(tab, eq - tab).Trim().Length == 0;
   int separator = eq >= 0 && (tab < 0 || eq < tab || alignedEquals) ? eq : tab;
   if(separator<0) return line; string name=Normalize(raw.Substring(0,separator)); if(name.Length==0) return line;
   int begin=separator+1; while(begin<raw.Length&&char.IsWhiteSpace(raw[begin])) begin++;
   line.Name=name; line.Prefix=raw.Substring(0,begin); line.Suffix=string.Empty;
   if(begin<raw.Length&&raw[begin]=='"') {
    int end=begin+1;
    while(end<raw.Length) { if(raw[end]=='\\'&&end+1<raw.Length) { end+=2; continue; } if(raw[end]=='"') break; end++; }
    if(end<raw.Length) { line.Quoted=true; line.Value=Unescape(raw.Substring(begin+1,end-begin-1)); line.Suffix=raw.Substring(end+1); return line; }
   }
   string tail=raw.Substring(begin); line.Value=tail.TrimEnd(); line.Suffix=tail.Substring(line.Value.Length); return line;
  }
  private static string Escape(string value) { return value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t"); }
  private static string Unescape(string value) {
   StringBuilder output=new StringBuilder();
   for(int i=0;i<value.Length;i++) {
    if(value[i]=='\\'&&i+1<value.Length) {
     char c=value[i+1]; if(c=='\\'||c=='"') { output.Append(c); i++; continue; }
     if(c=='r'||c=='n'||c=='t') { output.Append(c=='r'?'\r':c=='n'?'\n':'\t'); i++; continue; }
    }
    output.Append(value[i]);
   }
   return output.ToString();
  }
  private static string Normalize(string text) { return Regex.Replace((text??string.Empty).Trim().Trim('\uFEFF').TrimEnd(':','：').Trim(),"\\s+",string.Empty); }
  private static Dictionary<string,string> MakeAliases() {
   Dictionary<string,string> map=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   for(int i=0;i<Keys.Length;i++) { map[Normalize(Keys[i])]=Keys[i]; map[Normalize(Names[i])]=Keys[i]; }
   Add(map,"TestingOrganization","检测单位","试验单位","测试单位");
   Add(map,"TestPersonnel","试验人员","检测人员"); Add(map,"TestLocation","试验地点","检测地点");
   Add(map,"StartName","始端名称","始端","起点地点"); Add(map,"EndName","终端名称","终端","终点地点","末端名称");
   Add(map,"StartType","始端类型"); Add(map,"EndType","终端类型","末端类型");
   Add(map,"StartDispatch","调度号始","始端调度号"); Add(map,"EndDispatch","调度号末","终端调度号","末端调度号");
   Add(map,"InstallationDate","敷设时间","敷设日期","铺设日期"); Add(map,"CoreCount","芯数");
   Add(map,"PhaseVoltage","相电压(kV)","相电压[kV]"); Add(map,"CableLength","电缆长度(m)","电缆长度[m]");
   foreach(string phase in new string[]{"A","B","C"}) {
    Add(map,phase+"1",phase+"相绝缘1",phase+"相绝缘电阻1",phase+"相试验前绝缘电阻",phase+"相绝缘[MΩ]1",phase+"相绝缘电阻[MΩ]1");
    Add(map,phase+"2",phase+"相绝缘2",phase+"相绝缘电阻2",phase+"相试验后绝缘电阻",phase+"相绝缘[MΩ]2",phase+"相绝缘电阻[MΩ]2");
   }
   return map;
  }
  private static void Add(Dictionary<string,string> map,string key,params string[] aliases) { foreach(string alias in aliases) map[Normalize(alias)]=key; }
 }
}
