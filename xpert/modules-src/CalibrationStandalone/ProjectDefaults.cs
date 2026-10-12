using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Xiongxin.Calibration
{
    internal sealed class ProjectDefaults
    {
        internal double LengthM, VelocityMPerUs;
        internal bool CapacitiveLoad;
        internal string Material = "", VelocitySource = "", Notice = "";
        internal static ProjectDefaults Load(string root, string phase = "ALL")
        {
            string item = Path.Combine(root, "ITEM.in"), cable = Path.Combine(root, "CABLE.in");
            if (!File.Exists(item) || !File.Exists(cable)) throw new IOException("项目顶层需要 ITEM.in 和 CABLE.in。");
            var result = new ProjectDefaults();
            result.CapacitiveLoad=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root);
            if(result.CapacitiveLoad) { result.Notice="电容测试：只校准入射脉冲，不测距、不使用电缆长度和波速。";return result; }
            var velocities = new List<double>(); var rowLengths = new List<double>();
            double phaseLength=0,adoptedVelocity=0;
            foreach (string raw in ReadLines(item))
            {
                int eq=raw.IndexOf('='); if(eq<0)continue;
                string key=HVX.ProjectModel.ProjectMetadataKeys.Canonical(raw.Substring(0,eq));
                string value=raw.Substring(eq+1).Trim().Trim('"');
                if (key == "CableLength") result.LengthM = Number(value);
                if (key == "CableLength"+phase) phaseLength=Number(value);
                if (key == "AdoptedVelocity"+phase) adoptedVelocity=Number(value);
                int row;
                if (!Int32.TryParse(key,out row)) continue;
                string[] cells=value.Split(','); if(cells.Length<4 || !String.Equals(cells[0].Trim(),phase,StringComparison.OrdinalIgnoreCase))continue;
                double length=Number(cells[2]), velocity=Number(cells[3]);
                if (length>0) rowLengths.Add(length);
                if (velocity>=50 && velocity<=250) velocities.Add(velocity);
            }
            var materials=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ends=new List<double>();
            string cablePhase="";
            foreach(string raw in ReadLines(cable))
            {
                var cells=raw.Split('\t'); if(cells.Length<4)continue;
                if(cells[0].Trim()=="0")cablePhase=cells[1].Trim().Replace("相",String.Empty);
                if(phase!="ALL" && cablePhase!=phase && cablePhase!="ALL")continue;
                if(cells[0].Trim()=="2" && !String.IsNullOrWhiteSpace(cells[2])) materials.Add(cells[2].Trim());
                if(cells[0].Trim()=="1" && (cells[1].Contains("终端") || cells[1].Trim().Equals("End",StringComparison.OrdinalIgnoreCase))) {double end=Number(cells[3]);if(end>0)ends.Add(end);}
            }
            if(phaseLength>0)result.LengthM=phaseLength;
            else if(phase!="ALL" && ends.Count>0)result.LengthM=ends.Max();
            if(result.LengthM<=0 && ends.Count>0) result.LengthM=ends.Max();
            if(result.LengthM<=0 && rowLengths.Count>0) result.LengthM=rowLengths.Max();
            if(result.LengthM<=0) throw new IOException("项目没有可用的电缆长度。请在创建项目中补全后再加载。");
            result.Material=materials.Count==1?materials.First():materials.Count>1?"多种材料":"未记录";
            if(adoptedVelocity>=50&&adoptedVelocity<=250) {result.VelocityMPerUs=adoptedVelocity;result.VelocitySource="本相已确认采用波速";}
            else if(velocities.Count>0)
            {
                double min=velocities.Min(),max=velocities.Max();
                if(max/min<=1.01)
                { result.VelocityMPerUs=velocities.OrderBy(v=>v).ElementAt(velocities.Count/2);result.VelocitySource="已有校准波速"; }
                else result.Notice="项目内多档波速相差超过 1%，请核对后填写采用值。";
            }
            if(result.VelocityMPerUs<=0 && materials.Count==1)
            {
                var materialVelocity=FindMaterialVelocity(result.Material);
                if(materialVelocity>0){result.VelocityMPerUs=materialVelocity;result.VelocitySource="材料建议值（需核对）";}
            }
            if(result.VelocityMPerUs<=0 && result.Notice.Length==0)result.Notice="材料缺失或没有对应的波速，请人工填写后采集。";
            if(ends.Count>0 && ends.Any(e=>Math.Abs(e-result.LengthM)>Math.Max(.1,result.LengthM*.01)))
                result.Notice += " ITEM 长度与 CABLE 终端位置不一致，请核对。";
            return result;
        }
        static double FindMaterialVelocity(string material)
        {
            var catalogue=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase)
            { {"塑料绝缘",170}, {"橡胶绝缘",140}, {"油纸绝缘",160} };
            string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HVX","ProjectCreation","project-creation.ini");
            if(File.Exists(path))
            {
                bool inMaterials=false;
                foreach(string raw in File.ReadAllLines(path,Encoding.UTF8))
                {
                    string line=raw.Trim();
                    if(line.StartsWith("[")){inMaterials=String.Equals(line,"[Materials]",StringComparison.OrdinalIgnoreCase);continue;}
                    if(!inMaterials || !line.StartsWith("item=",StringComparison.OrdinalIgnoreCase))continue;
                    string[] cells=line.Substring(5).Split('|');
                    double v;if(cells.Length==2 && Double.TryParse(cells[1],NumberStyles.Float,CultureInfo.InvariantCulture,out v) && v>=50 && v<=250)catalogue[cells[0].Trim()]=v;
                }
            }
            double velocity; return catalogue.TryGetValue(material,out velocity)?velocity:0;
        }
        static string[] ReadLines(string path)
        {
            var info=new FileInfo(path);if(info.Length>2*1024*1024)throw new IOException("项目元数据文件超过 2 MB。");
            byte[] bytes=File.ReadAllBytes(path);string text;
            if(bytes.Length>=2 && bytes[0]==0xFF && bytes[1]==0xFE)text=Encoding.Unicode.GetString(bytes,2,bytes.Length-2);
            else if(bytes.Length>=2 && bytes[0]==0xFE && bytes[1]==0xFF)text=Encoding.BigEndianUnicode.GetString(bytes,2,bytes.Length-2);
            else if(bytes.Length>=3 && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF)text=Encoding.UTF8.GetString(bytes,3,bytes.Length-3);
            else
            {
                try{text=new UTF8Encoding(false,true).GetString(bytes);}
                catch(DecoderFallbackException){text=Encoding.GetEncoding(936).GetString(bytes);}
            }
            return text.Replace("\r","").Split('\n');
        }
        static double Number(string text)
        {
            string s=(text??"").Trim().Trim('"');int i=0;
            while(i<s.Length && (Char.IsDigit(s[i])||s[i]=='.'||s[i]=='+'||s[i]=='-'))i++;
            double value;return Double.TryParse(s.Substring(0,i),NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&!Double.IsNaN(value)&&!Double.IsInfinity(value)?value:0;
        }
    }
}
