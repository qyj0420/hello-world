using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace HVX.ProjectEvidence
{
    [Obfuscation(Exclude=true,ApplyToMembers=true)]
    public sealed class PressureFixtureEvidence
    {
        public int Phase,Component,SourceRow;
        public double PositionM,ChargePc;
        public string TerminalType;
    }
    [Obfuscation(Exclude=true,ApplyToMembers=true)]
    public sealed class PressureFixtureDecision
    {
        public string Schema="HVX.Pressure.FixtureDecision/1",Project,Trial,Acquisition,SourceRecord,SourceSha256,MetadataSha256,Choice;
        public DateTime DecidedUtc;
        public PressureFixtureEvidence[] Evidence;
        public bool StartsHardware=false,ProvesFixtureSafe=false,ProvesDischargeSafe=false;
    }
    internal static class PressureFixtureJournal
    {
        internal static bool IsHighVoltageSeparable(string name)
        {
            string value=(name??"").Trim();
            if(value=="高压插拔头")return true; // explicit project catalog category
            if(value.IndexOf("插拔",StringComparison.OrdinalIgnoreCase)<0&&value.IndexOf("separable",StringComparison.OrdinalIgnoreCase)<0&&
                value.IndexOf("plug-in",StringComparison.OrdinalIgnoreCase)<0)return false;
            // A generic connector label is not a rated voltage. Do not infer
            // its rating from the project's applied voltage or U0.
            var match=Regex.Match(value,@"(?<![\d.])(\d+(?:\.\d+)?)\s*kV(?![A-Za-z])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            double rating;
            return match.Success&&Double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out rating)&&rating>=6;
        }
        internal static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
        internal static void NoLinks(string path)
        {
            string full=Path.GetFullPath(path);
            if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked event file is not supported.");
            for(var directory=new DirectoryInfo(File.Exists(full)?Path.GetDirectoryName(full):full);directory!=null;directory=directory.Parent)
                if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked event path is not supported.");
        }
        internal static string MetadataHash(string project)
        {
            string item=Path.Combine(project,"ITEM.in"),cable=Path.Combine(project,"CABLE.in");NoLinks(item);NoLinks(cable);
            return Hash(item)+":"+Hash(cable);
        }
        internal static string Save(string project,PressureFixtureDecision record)
        {
            string root=Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);
            record.Project=root;record.DecidedUtc=DateTime.UtcNow;
            Validate(root,record);
            if(record.MetadataSha256!=MetadataHash(root))throw new InvalidDataException("项目电缆参数已改变，请重新分析后确认。");
            string directory=Path.Combine(root,"Logs","PressureFixture");NoLinks(directory);Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"fixture-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".xml");
            string pending=path+".pending";
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                new XmlSerializer(typeof(PressureFixtureDecision)).Serialize(stream,record);stream.Flush(true);
            }
            File.Move(pending,path);
            return path;
        }
        static void Validate(string project,PressureFixtureDecision record)
        {
            if(record==null||record.Schema!="HVX.Pressure.FixtureDecision/1"||record.Evidence==null||record.Evidence.Length==0||record.Evidence.Length>1000||
                String.IsNullOrWhiteSpace(record.Trial)||record.Trial.Length>200||String.IsNullOrWhiteSpace(record.Acquisition)||record.Acquisition.Length>200||
                record.DecidedUtc.Kind!=DateTimeKind.Utc||record.DecidedUtc>DateTime.UtcNow.AddMinutes(1)||record.DecidedUtc.Year<2020||
                String.IsNullOrEmpty(record.MetadataSha256)||!Regex.IsMatch(record.MetadataSha256,@"\A[A-F0-9]{64}:[A-F0-9]{64}\z")||
                (record.Choice!="FixtureConfirmedContinue"&&record.Choice!="StopQueue")||
                record.StartsHardware||record.ProvesFixtureSafe||record.ProvesDischargeSafe||
                !String.Equals(Path.GetFullPath(record.Project),project,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid fixture decision.");
            if(String.IsNullOrWhiteSpace(record.SourceRecord)||Path.IsPathRooted(record.SourceRecord))throw new InvalidDataException("Invalid fixture source.");
            string source=Path.GetFullPath(Path.Combine(project,record.SourceRecord));
            if(!source.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetExtension(source),".loc",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Fixture source outside project.");
            NoLinks(source);
            if(!File.Exists(source)||Hash(source)!=record.SourceSha256)throw new InvalidDataException("Fixture source changed or missing.");
            foreach(var evidence in record.Evidence)
                if(evidence==null||evidence.Phase<1||evidence.Phase>3||(evidence.Component!=2&&evidence.Component!=3)||evidence.SourceRow<0||
                    Double.IsNaN(evidence.PositionM)||Double.IsInfinity(evidence.PositionM)||Double.IsNaN(evidence.ChargePc)||Double.IsInfinity(evidence.ChargePc)||
                    evidence.ChargePc<=10000||!IsHighVoltageSeparable(evidence.TerminalType))throw new InvalidDataException("Invalid fixture endpoint evidence.");
        }
        internal static PressureFixtureEvidence[] Select(string project,double[,] endpoints)
        {
            if(endpoints==null||endpoints.GetLength(1)!=5||endpoints.GetLength(0)>100000)throw new InvalidDataException("端部分析证据格式无效。");
            if(endpoints.GetLength(0)==0)return new PressureFixtureEvidence[0];
            NoLinks(Path.Combine(project,"CABLE.in"));
            var cable=HVX.ProjectCables.CableJointStore.Read(project);
            var selected=new System.Collections.Generic.List<PressureFixtureEvidence>();
            for(int r=0;r<endpoints.GetLength(0);r++){
                double p=endpoints[r,0],c=endpoints[r,1],q=endpoints[r,3],source=endpoints[r,4];
                if(Double.IsNaN(p)||p<1||p>3||p!=Math.Truncate(p)||(c!=2&&c!=3)||Double.IsNaN(q)||Double.IsInfinity(q)||q<=10000||
                    Double.IsNaN(source)||source<0||source>Int32.MaxValue||source!=Math.Truncate(source))throw new InvalidDataException("端部分析证据无效。");
                string phase="ABC"[(int)p-1].ToString();
                var blocks=cable.Blocks.Where(b=>cable.Mode==HVX.ProjectCables.CableJointMode.SharedThreeCore||b.Phase==phase||b.Phase=="ALL").ToArray();
                if(blocks.Length==0)throw new InvalidDataException("端部证据相别与项目电缆结构不一致。");
                // Repeated/shared blocks cannot be treated as one concatenated
                // cable. Accept only an unambiguous common termination type.
                var types=blocks.Select(b=>cable.Lines[c==2?b.AccessoryLines.First():b.AccessoryLines.Last()].Split('\t')[2]).Distinct(StringComparer.Ordinal).ToArray();
                if(types.Length!=1)throw new InvalidDataException("该相端部类型不一致，请核对项目电缆结构后继续。");
                string type=types[0];
                if(IsHighVoltageSeparable(type))selected.Add(new PressureFixtureEvidence{Phase=(int)p,Component=(int)c,PositionM=endpoints[r,2],ChargePc=q,SourceRow=(int)source,TerminalType=type});
            }
            // Keep the largest event per phase/termination; every retained row
            // still points to its original location matrix source.
            return selected.GroupBy(e=>e.Phase+":"+e.Component).Select(g=>g.OrderByDescending(e=>e.ChargePc).First()).ToArray();
        }
        internal static string Summary(string project,bool chinese)
        {
            if(String.IsNullOrWhiteSpace(project))return "";
            string root=Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar),directory=Path.Combine(root,"Logs","PressureFixture");
            if(!Directory.Exists(directory))return "";NoLinks(directory);
            var files=Directory.GetFiles(directory,"fixture-*.xml",SearchOption.TopDirectoryOnly);
            if(files.Length>5000)throw new InvalidDataException("Too many fixture events; archive the project event history before reporting.");
            var text=new StringBuilder();long bytes=0;
            foreach(string file in files.OrderBy(p=>p,StringComparer.Ordinal)){
                NoLinks(file);long length=new FileInfo(file).Length;bytes+=length;
                if(length>1048576||bytes>16777216)throw new InvalidDataException("Fixture event history exceeds report limit.");
                PressureFixtureDecision record;
                using(var stream=File.OpenRead(file))using(var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576}))
                    record=(PressureFixtureDecision)new XmlSerializer(typeof(PressureFixtureDecision)).Deserialize(reader);
                Validate(root,record);
                foreach(var evidence in record.Evidence.GroupBy(e=>e.Phase+":"+e.Component).Select(g=>g.OrderByDescending(e=>e.ChargePc).First()))
                    text.AppendLine(chinese?String.Format(CultureInfo.InvariantCulture,"{0:u}，{1}相{2}端部放电 {3:0.###} nC（超过10 nC）；操作者{4}。轮次 {5}。此项为人工操作记录，不代表软件已验证工装安全或设备已放电。",
                        record.DecidedUtc,"ABC"[evidence.Phase-1],evidence.Component==2?"始端":"末端",evidence.ChargePc/1000,
                        record.Choice=="FixtureConfirmedContinue"?"确认已安装工装并选择继续":"选择停止后续试验",record.Acquisition):
                        String.Format(CultureInfo.InvariantCulture,"{0:u}: phase {1}, {2} termination, {3:0.###} nC (>10 nC). The operator {4}. Round {5}. This is an operator statement, not verification of fixture or discharge safety.",
                        record.DecidedUtc,"ABC"[evidence.Phase-1],evidence.Component==2?"start":"remote",evidence.ChargePc/1000,
                        record.Choice=="FixtureConfirmedContinue"?"confirmed the fixture was installed and chose to continue":"stopped subsequent rounds",record.Acquisition));
            }
            return text.Length==0?"":(chinese?"项目工装确认记录（不改变本次分析结论）：":"Project fixture decisions (do not alter this analysis conclusion):")+Environment.NewLine+text;
        }
    }
}
