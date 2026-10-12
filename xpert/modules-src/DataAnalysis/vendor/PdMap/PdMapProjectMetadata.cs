using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // Format evidence: OWTS ProjectDataEngine.ReadMetadata and audited Task24
        // ProjectCalibrationSelector.TargetLength. ITEM is INI key=value; CABLE
        // records are 0=phase, 1=accessory endpoint, 2=segment material.
        sealed class ProjectCableBlock
        {
            internal string Phase;
            internal bool Valid = true;
            internal readonly List<double> Positions = new List<double>();
            internal readonly List<string> Names = new List<string>();
            internal readonly List<string> Records = new List<string>();
            internal readonly List<ProjectMaterialSegment> Materials = new List<ProjectMaterialSegment>();
            internal string PendingMaterial;
            internal int PendingMaterialCount, MaterialIssueCount;
        }

        sealed class ProjectMaterialSegment
        {
            internal double StartM, EndM;
            internal string OriginalName;
        }

        readonly List<ProjectCableBlock> projectCableBlocks = new List<ProjectCableBlock>();
        double projectItemLengthM = double.NaN, savedNonProjectCableLengthM = double.NaN;
        double[] projectConnectorPositions = Array.Empty<double>(), explicitConnectorPositions = Array.Empty<double>();
        double[] projectMaterialStartsM=Array.Empty<double>(),projectMaterialEndsM=Array.Empty<double>();
        string[] projectMaterialNames=Array.Empty<string>(),projectMaterialOriginalNames=Array.Empty<string>();
        double[] currentMaterialStartsM=Array.Empty<double>(),currentMaterialEndsM=Array.Empty<double>();
        string[] currentMaterialNames=Array.Empty<string>(),currentMaterialOriginalNames=Array.Empty<string>();
        string materialSegmentsSource="Unavailable";
        bool connectorPositionsExplicit, projectLengthApplied;
        string projectMetadataLoadCode = "Disabled", projectMetadataLoadDetails = "";
        string projectMetadataCode = "Disabled", projectMetadataDetails = "";
        string cableLengthSource = "ExistingValue", connectorPositionsSource = "Unavailable";

        [Browsable(false)] public double CurrentCableLengthM { get { return cableLengthM; } }
        [Browsable(false)] public double[] CurrentConnectorPositionsM { get { return (double[])connectors.Clone(); } }
        [Browsable(false)] public double[] ProjectConnectorPositionsM { get { return (double[])projectConnectorPositions.Clone(); } }
        [Browsable(false)] public string CableLengthSource { get { return cableLengthSource; } }
        [Browsable(false)] public string ConnectorPositionsSource { get { return connectorPositionsSource; } }
        [Browsable(false)] public double[] ProjectMaterialStartsM { get { return (double[])projectMaterialStartsM.Clone(); } }
        [Browsable(false)] public double[] ProjectMaterialEndsM { get { return (double[])projectMaterialEndsM.Clone(); } }
        [Browsable(false)] public string[] ProjectMaterialNames { get { return (string[])projectMaterialNames.Clone(); } }
        [Browsable(false)] public string[] ProjectMaterialOriginalNames { get { return (string[])projectMaterialOriginalNames.Clone(); } }
        [Browsable(false)] public double[] CurrentMaterialStartsM { get { return (double[])currentMaterialStartsM.Clone(); } }
        [Browsable(false)] public double[] CurrentMaterialEndsM { get { return (double[])currentMaterialEndsM.Clone(); } }
        [Browsable(false)] public string[] CurrentMaterialNames { get { return (string[])currentMaterialNames.Clone(); } }
        [Browsable(false)] public string[] CurrentMaterialOriginalNames { get { return (string[])currentMaterialOriginalNames.Clone(); } }
        [Browsable(false)] public string MaterialSegmentsSource { get { return materialSegmentsSource; } }
        [Browsable(false)] public int ProjectMetadataReadCount { get; private set; }
        [Browsable(false)] public string ProjectMetadataStatusCode { get { return projectMetadataCode; } }
        [Browsable(false)] public string ProjectMetadataDetails { get { return projectMetadataDetails; } }
        [Browsable(false)] public string ProjectMetadataStatus
        {
            get
            {
                string text;
                switch(projectMetadataCode)
                {
                    case "Disabled": text=L("Project metadata is disabled.","项目电缆信息未启用。");break;
                    case "WaitingForPositioningRow":text=L("Project metadata is cached; waiting for the current pulse phase.","项目电缆信息已缓存，等待当前脉冲的相别。");break;
                    case "SamePhaseCable":text=L("Using the current phase's cable accessories.","已读取当前相的电缆附件信息。");break;
                    case "CommonCable":text=L("Using explicitly shared cable accessories.","已读取明确标记为通用的电缆附件信息。");break;
                    case "NoPhaseCable":text=L("No cable block matches this phase; no other phase's joints are used.","没有对应相的电缆结构，不使用其他相的接头。");break;
                    case "InvalidCableGeometry":text=L("The selected cable block is invalid or its duplicate blocks disagree; project joints are unavailable.","当前相电缆结构无效或重复块不一致，项目接头不可用。");break;
                    case "InvalidTestPhase":text=L("The positioning row has an invalid phase code.","定位结果行的相别代码无效。");break;
                    case "InvalidProjectPath":text=L("The project path is invalid; previous project metadata was cleared.","项目路径无效，已清除上一个项目的电缆信息。");break;
                    case "MetadataReadError":text=L("Project metadata could not be read; see ProjectMetadataDetails.","项目电缆信息读取失败，详情见 ProjectMetadataDetails。");break;
                    default:text=L("Project metadata is unavailable.","项目电缆信息不可用。");break;
                }
                string length=L(" Cable length source: "," 电缆长度来源：");
                string joints=L("; joint source: ","；接头来源：");
                return text+length+cableLengthSource+joints+connectorPositionsSource+" ["+projectMetadataCode+"]";
            }
        }

        /// <summary>Rereads ITEM/CABLE only, then updates current phase metadata and cached CAL selection.</summary>
        public void ReloadProjectMetadata()
        {
            ReadProjectMetadataCatalog();
            SelectProjectCalibrationForMap(matrixDriven?originalParameterRow:null);
            AlignCalibration(); RecomputeFilteredCurves();
            RefreshCableStructureAnalysis();
            RememberLoadedInputs();viewReady=false;Invalidate();
        }

        /// <summary>Explicit connector input wins until null is supplied or this reset method is called.</summary>
        public void ResetProjectConnectorOverride()
        {
            connectorPositionsExplicit=false;cableStructurePositionsOverride=false;explicitConnectorPositions=Array.Empty<double>();
            ApplyProjectMetadataForMap(matrixDriven?originalParameterRow:null);
            RefreshCableStructureAnalysis();
        }

        void SetExplicitConnectorPositions(double[] values)
        {
            if(values==null){ResetProjectConnectorOverride();return;}
            // Cloning prevents a caller array from changing a cached project's markers.
            double[] next=values.Where(v=>IsFinite(v)&&v>=0).Distinct().OrderBy(v=>v).ToArray();
            if(connectorPositionsExplicit&&SameValues(explicitConnectorPositions,next))return;
            bool previousStructureOverride=cableStructurePositionsOverride;
            cableStructurePositionsOverride=false;
            explicitConnectorPositions=next;
            connectorPositionsExplicit=true;connectors=(double[])explicitConnectorPositions.Clone();
            connectorPositionsSource="ExplicitInput";
            if(cableMaterialOverride!=null||previousStructureOverride)UpdateCurrentMaterialSegments();
            RefreshCableStructureAnalysis(cableMaterialOverride==null&&!previousStructureOverride);
        }

        void ReadProjectMetadataCatalog()
        {
            ReadHvProjectCatalog();
            projectCableBlocks.Clear();projectItemLengthM=double.NaN;projectConnectorPositions=Array.Empty<double>();
            ClearProjectMaterialSegments();
            projectMetadataLoadCode="Disabled";projectMetadataLoadDetails="";
            if(!ProjectCalibrationEnabled)return;
            ProjectMetadataReadCount++;
            try
            {
                string root=Path.GetFullPath(projectPath.Trim());
                if(File.Exists(root))
                {
                    if(!Path.GetFileName(root).Equals("ITEM.in",StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Project file path must identify ITEM.in.");
                    root=Path.GetDirectoryName(root);
                }
                if(!Directory.Exists(root))throw new DirectoryNotFoundException("Project directory does not exist.");
                projectMetadataLoadCode="MetadataLoaded";
                string item=Path.Combine(root,"ITEM.in"),cable=Path.Combine(root,"CABLE.in");
                if(File.Exists(item))
                {
                    try { projectItemLengthM=ReadProjectItemLength(ReadProjectMetadataLines(item)); }
                    catch(Exception ex) when(IsProjectMetadataReadException(ex))
                    { projectMetadataLoadDetails="ITEM.in: "+ex.Message; }
                }
                if(File.Exists(cable))
                {
                    try { projectCableBlocks.AddRange(ParseProjectCableBlocks(ReadProjectMetadataLines(cable))); }
                    catch(Exception ex) when(IsProjectMetadataReadException(ex))
                    { projectMetadataLoadCode="MetadataReadError";projectMetadataLoadDetails+=(projectMetadataLoadDetails.Length==0?"":"; ")+"CABLE.in: "+ex.Message; }
                }
            }
            catch(Exception ex) when(IsProjectMetadataReadException(ex))
            {projectMetadataLoadCode=ex is ArgumentException||ex is DirectoryNotFoundException?"InvalidProjectPath":"MetadataReadError";projectMetadataLoadDetails=ex.Message;}
        }

        static bool IsProjectMetadataReadException(Exception ex)
        {return ex is IOException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is NotSupportedException||ex is System.Security.SecurityException;}

        static string[] ReadProjectMetadataLines(string path)
        {
            FileInfo info=new FileInfo(path);
            if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked metadata files are not accepted.");
            if(info.Length>2L*1024*1024)throw new IOException("Metadata file exceeds 2 MiB.");
            byte[] bytes=File.ReadAllBytes(path);string text;
            if(bytes.Length>=2&&bytes[0]==255&&bytes[1]==254)text=new UnicodeEncoding(false,true,true).GetString(bytes);
            else if(bytes.Length>=2&&bytes[0]==254&&bytes[1]==255)text=new UnicodeEncoding(true,true,true).GetString(bytes);
            else
            {
                try{text=new UTF8Encoding(false,true).GetString(bytes);}
                catch(DecoderFallbackException){text=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}
            }
            var lines=new List<string>();
            using(var reader=new StringReader(text.TrimStart('\ufeff')))
            {
                string line;while((line=reader.ReadLine())!=null)
                {if(lines.Count>=20000)throw new IOException("Metadata file exceeds 20000 lines.");lines.Add(line);}
            }
            return lines.ToArray();
        }

        static double ReadProjectItemLength(string[] lines)
        {
            double length=double.NaN;
            foreach(string raw in lines)
            {
                int equals=raw.IndexOf('=');if(equals<0)continue;
                string key=raw.Substring(0,equals).Trim().TrimEnd(':','：').Trim();
                if(HVX.ProjectModel.ProjectMetadataKeys.Canonical(key)!="CableLength")continue;
                string value=raw.Substring(equals+1).Trim().Trim('"').Trim();
                Match m=Regex.Match(value,@"^([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(?:m|米)?$",RegexOptions.IgnoreCase);
                double parsed;
                if(!m.Success||!double.TryParse(m.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed)||!IsFinite(parsed)||parsed<=0)
                    throw new IOException("Invalid ITEM.in 电缆长度 value.");
                if(IsFinite(length)&&length!=parsed)throw new IOException("Conflicting ITEM.in 电缆长度 values.");
                length=parsed;
            }
            return length;
        }

        static string MetadataPhase(string text)
        {
            switch((text??"").Trim().ToUpperInvariant())
            {
                case "A":case "A相":case "L1":return "A";
                case "B":case "B相":case "L2":return "B";
                case "C":case "C相":case "L3":return "C";
                case "ALL":case "ALL相":case "COMMON":case "全相":case "三相":case "共用":return "ALL";
                default:return "";
            }
        }

        static List<ProjectCableBlock> ParseProjectCableBlocks(string[] lines)
        {
            var blocks=new List<ProjectCableBlock>();ProjectCableBlock active=null;
            foreach(string raw in lines)
            {
                string line=raw.Trim();if(line.Length==0)continue;
                Match record=Regex.Match(line,@"^([012])\s+(.*)$");
                if(record.Success&&record.Groups[1].Value=="0")
                {
                    string phase=MetadataPhase(record.Groups[2].Value);
                    active=phase.Length==0?null:new ProjectCableBlock{Phase=phase};
                    if(active!=null)blocks.Add(active);continue;
                }
                if(active==null)continue;
                active.Records.Add(Regex.Replace(line,@"\s+"," "));
                if(record.Success&&record.Groups[1].Value=="2")
                {
                    // A body record describes ONLY the interval between the previous
                    // and next accessory records. Never extend a material over a gap.
                    if(active.Positions.Count==0){active.MaterialIssueCount++;continue;}
                    active.PendingMaterialCount++;
                    active.PendingMaterial=ReadProjectMaterialName(record.Groups[2].Value);
                    continue;
                }
                if(!record.Success||record.Groups[1].Value!="1")continue;
                string payload=record.Groups[2].Value;
                Match endpoint=Regex.Match(payload,@"([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(?:m|米)?\s*$",RegexOptions.IgnoreCase);
                double position;
                if(!endpoint.Success||!double.TryParse(endpoint.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out position)||!IsFinite(position)||position<0)
                {active.Valid=false;continue;}
                if(active.Positions.Count>=4096)throw new IOException("A cable block exceeds 4096 accessory endpoints.");
                if(active.Positions.Count>0&&position<=active.Positions[active.Positions.Count-1])active.Valid=false;
                if(active.Positions.Count>0&&active.PendingMaterialCount>0)
                {
                    double start=active.Positions[active.Positions.Count-1];
                    if(active.PendingMaterialCount==1&&!string.IsNullOrWhiteSpace(active.PendingMaterial)&&position>start)
                        active.Materials.Add(new ProjectMaterialSegment{StartM=start,EndM=position,OriginalName=active.PendingMaterial});
                    else active.MaterialIssueCount++;
                }
                active.PendingMaterialCount=0;active.PendingMaterial=null;
                string label=payload.Substring(0,endpoint.Index).Trim();
                string[] cells=Regex.Split(label,@"\s+");
                string name=cells.Length>1?string.Join(" ",cells.Take(cells.Length-1)):label;
                active.Positions.Add(position);active.Names.Add(name);
            }
            return blocks;
        }

        static string ReadProjectMaterialName(string payload)
        {
            if(string.IsNullOrWhiteSpace(payload))return null;
            // Real CABLE.in uses tab-separated [2, 本体, material, empty].
            // The whitespace-only variant retains multi-word material names.
            string value;
            if(payload.IndexOf('\t')>=0)
            {
                string[] cells=payload.Split('\t');
                if(cells.Length<2||cells.Skip(2).Any(c=>!string.IsNullOrWhiteSpace(c)))return null;
                value=cells[1].Trim();
            }
            else
            {
                Match body=Regex.Match(payload.Trim(),@"^\S+\s+(.+)$");
                if(!body.Success)return null;value=body.Groups[1].Value.Trim();
            }
            value=value.Trim('"').Trim();
            return value.Length==0||value.Length>256?null:value;
        }

        static string ProjectMaterialEnglishName(string original)
        {
            string text=(original??"").Trim();
            string key=Regex.Replace(text,@"[\s\-_‐‑–—]+","").ToUpperInvariant();
            switch(key)
            {
                case "XLPE":case "XLPE绝缘":case "交联聚乙烯":case "交联聚乙烯绝缘":case "交联聚乙烯绝缘材料":case "CROSSLINKEDPOLYETHYLENE":return "XLPE";
                case "PVC":case "PVC绝缘":case "聚氯乙烯":case "聚氯乙烯绝缘":case "POLYVINYLCHLORIDE":return "PVC";
                case "PE":case "PE绝缘":case "聚乙烯":case "聚乙烯绝缘":case "POLYETHYLENE":return "PE";
                case "EVA":case "EVA绝缘":case "乙烯醋酸乙烯":case "乙烯醋酸乙烯共聚物":case "乙烯乙酸乙烯":case "乙烯乙酸乙烯共聚物":case "ETHYLENEVINYLACETATE":return "EVA";
                case "EPR":case "EPR绝缘":case "乙丙橡胶":case "乙丙橡胶绝缘":case "ETHYLENEPROPYLENERUBBER":return "EPR";
                // Generic plastic insulation is not proof of XLPE, PVC or another resin.
                case "塑料绝缘":case "PLASTICINSULATION":return "Plastic insulation";
                default:return text;
            }
        }

        void ClearProjectMaterialSegments()
        {
            projectMaterialStartsM=projectMaterialEndsM=Array.Empty<double>();
            projectMaterialNames=projectMaterialOriginalNames=Array.Empty<string>();
            currentMaterialStartsM=currentMaterialEndsM=Array.Empty<double>();
            currentMaterialNames=currentMaterialOriginalNames=Array.Empty<string>();
            materialSegmentsSource="Unavailable";
        }

        void CacheCurrentMaterialSegments(ProjectCableBlock block,string phase)
        {
            if(block==null)return;
            projectMaterialStartsM=block.Materials.Select(m=>m.StartM).ToArray();
            projectMaterialEndsM=block.Materials.Select(m=>m.EndM).ToArray();
            projectMaterialOriginalNames=block.Materials.Select(m=>m.OriginalName).ToArray();
            projectMaterialNames=projectMaterialOriginalNames.Select(ProjectMaterialEnglishName).ToArray();
            UpdateCurrentMaterialSegments();
            if(projectMaterialStartsM.Length>0&&cableMaterialOverride==null&&!cableStructurePositionsOverride)materialSegmentsSource="CABLE.in:"+phase;
            int issues=block.MaterialIssueCount+(block.PendingMaterialCount>0?1:0);
            if(issues>0)projectMetadataDetails+=(projectMetadataDetails.Length==0?"":"; ")+"Material intervals unavailable or ambiguous: "+issues;
        }

        // Called by manual length setters/restore without reapplying project geometry.
        // This uses only the current phase's cached project intervals; no file I/O.
        void UpdateCurrentMaterialSegments()
        {
            if(ApplyManualMaterialSegments())return;
            var starts=new List<double>();var ends=new List<double>();var names=new List<string>();var originals=new List<string>();
            for(int i=0;i<projectMaterialStartsM.Length;i++)
            {
                double start=Math.Max(0,ProjectPositionToDut(projectMaterialStartsM[i])),end=Math.Min(cableLengthM,ProjectPositionToDut(projectMaterialEndsM[i]));
                if(!IsFinite(cableLengthM)||cableLengthM<=0||end<=start)continue;
                starts.Add(start);ends.Add(end);names.Add(projectMaterialNames[i]);originals.Add(projectMaterialOriginalNames[i]);
            }
            currentMaterialStartsM=starts.ToArray();currentMaterialEndsM=ends.ToArray();
            currentMaterialNames=names.ToArray();currentMaterialOriginalNames=originals.ToArray();
        }

        static bool SameProjectMaterialSegments(ProjectCableBlock first,ProjectCableBlock other)
        {
            if(first.Materials.Count!=other.Materials.Count||first.MaterialIssueCount!=other.MaterialIssueCount||first.PendingMaterialCount!=other.PendingMaterialCount)return false;
            for(int i=0;i<first.Materials.Count;i++)
                if(first.Materials[i].StartM!=other.Materials[i].StartM||first.Materials[i].EndM!=other.Materials[i].EndM||first.Materials[i].OriginalName!=other.Materials[i].OriginalName)return false;
            return true;
        }

        void ApplyProjectMetadataForMap(double[] row)
        {
            double rowLength=Safe(row,5,double.NaN),phaseCode=Safe(row,0,double.NaN);
            double selectedLength=double.NaN;string selectedPhase="",geometryCode="NoPhaseCable";
            ProjectCableBlock selectedMaterialBlock=null;
            projectConnectorPositions=Array.Empty<double>();
            ClearProjectMaterialSegments();
            projectMetadataDetails=projectMetadataLoadDetails;
            bool validPhase=IsFinite(phaseCode)&&phaseCode>=0&&phaseCode<=3&&phaseCode==Math.Round(phaseCode);
            if(ProjectCalibrationEnabled&&projectMetadataLoadCode=="MetadataLoaded"&&validPhase)
            {
                string phase=phaseCode==0?"ALL":phaseCode==1?"A":phaseCode==2?"B":"C";
                ProjectCableBlock[] selected=projectCableBlocks.Where(b=>b.Phase==phase&&b.Records.Count>0).ToArray();
                selectedPhase=phase;
                if(selected.Length==0&&phase!="ALL")
                {selected=projectCableBlocks.Where(b=>b.Phase=="ALL"&&b.Records.Count>0).ToArray();selectedPhase="ALL";}
                if(selected.Length>0)
                {
                    ProjectCableBlock first=selected[0];
                    bool valid=selected.All(b=>b.Valid&&b.Positions.Count>0&&b.Positions[b.Positions.Count-1]>0&&b.Records.SequenceEqual(first.Records));
                    if(valid)
                    {
                        selectedLength=first.Positions[first.Positions.Count-1];
                        selectedMaterialBlock=selected.All(b=>SameProjectMaterialSegments(first,b))?first:null;
                        if(selectedMaterialBlock==null)projectMetadataDetails+=(projectMetadataDetails.Length==0?"":"; ")+"Material intervals in duplicate cable blocks disagree.";
                        // Accessory position, not its free-form name, identifies
                        // an internal cable boundary. Keep the existing duplicate
                        // geometry checks and lead/end exclusion unchanged.
                        projectConnectorPositions=first.Positions.Where(p=>p>connectingCableLengthM&&p<selectedLength)
                            .Select(ProjectPositionToDut).Distinct().OrderBy(p=>p).ToArray();
                        geometryCode=selectedPhase==phase?"SamePhaseCable":"CommonCable";
                        if(selected.Length>1)projectMetadataDetails+=(projectMetadataDetails.Length==0?"":"; ")+"Identical cable blocks collapsed: "+(selected.Length-1);
                    }
                    else geometryCode="InvalidCableGeometry";
                }
            }
            if(IsFinite(rowLength)&&rowLength>0){cableLengthM=rowLength;cableLengthSource="PositioningMatrix[5]";projectLengthApplied=false;}
            else if(ProjectCalibrationEnabled&&validPhase&&IsFinite(selectedLength)&&selectedLength>0)
            {
                if(!projectLengthApplied)savedNonProjectCableLengthM=cableLengthM;
                cableLengthM=selectedLength-connectingCableLengthM;cableLengthSource="CABLE.in:"+selectedPhase;projectLengthApplied=true;
            }
            else if(ProjectCalibrationEnabled&&validPhase&&IsFinite(projectItemLengthM)&&projectItemLengthM>0)
            {
                if(!projectLengthApplied)savedNonProjectCableLengthM=cableLengthM;
                cableLengthM=projectItemLengthM-connectingCableLengthM;cableLengthSource="ITEM.in:电缆长度";projectLengthApplied=true;
            }
            else
            {
                if(projectLengthApplied&&IsFinite(savedNonProjectCableLengthM)&&savedNonProjectCableLengthM>0)cableLengthM=savedNonProjectCableLengthM;
                projectLengthApplied=false;cableLengthSource="ExistingValue";
            }
            CacheCurrentMaterialSegments(selectedMaterialBlock,selectedPhase);
            if(connectorPositionsExplicit){connectors=(double[])explicitConnectorPositions.Clone();connectorPositionsSource="ExplicitInput";}
            else
            {
                // The measured row length can be shorter than project geometry. Do not
                // clamp an out-of-range project joint onto the displayed cable boundary.
                connectors=projectConnectorPositions.Where(p=>p>0&&IsFinite(cableLengthM)&&p<cableLengthM).ToArray();
                connectorPositionsSource=geometryCode=="SamePhaseCable"||geometryCode=="CommonCable"?"CABLE.in:"+selectedPhase:"Unavailable";
            }
            projectMetadataCode=!ProjectCalibrationEnabled?"Disabled":projectMetadataLoadCode!="MetadataLoaded"?projectMetadataLoadCode:
                row==null||row.Length==0?"WaitingForPositioningRow":!validPhase?"InvalidTestPhase":geometryCode;
            UpdateCurrentMaterialSegments();
            RefreshMixedCableModel();
            if(matrixDriven)CountSamePositionPulses(currentLocationM);
        }
    }
}
