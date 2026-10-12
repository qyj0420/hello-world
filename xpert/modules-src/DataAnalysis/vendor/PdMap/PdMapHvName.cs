using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // Task18 v1.3.4 PerformanceExtensions.BuildTreeClassification is the ID
        // contract: union DEM/LOC basenames, timestamp/phase/name order, IDs 1..N.
        // Its AppendPulseRows joins positioning column 1 to precisely these IDs.
        // Never derive order from PDmap IDs, matrix row order or voltage alone.
        sealed class HvProjectRecord
        {
            internal string Name,Phase,LocationPath;
            internal DateTime Stamp;
            internal double Voltage;
            internal int Id,Repeat,Total;
        }
        static readonly Regex HvFileNamePattern=new Regex("^(ALL|A|B|C)_([-+]?[0-9]+(?:\\.[0-9]+)?)kV_(\\d{4})-(\\d{2})-(\\d{2})-(\\d{2})'(\\d{2})'(\\d{2})$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        readonly Dictionary<int,HvProjectRecord> hvProjectRecords=new Dictionary<int,HvProjectRecord>();
        string hvNameInput=string.Empty,hvCatalogCode="Disabled";
        string hvSourcePathInput=string.Empty,hvSourcePathCode="Disabled",hvProjectRoot=string.Empty,hvValidatedSourcePath=string.Empty;
        HvProjectRecord hvExplicitSourceRecord;

        [Category("Appearance"),DefaultValue("")]
        [Description("Optional text at the upper-right legend row. Empty uses the current MAP's original phase/voltage and verified project acquisition sequence. Does not reload data matrices.")]
        public string HVname
        {
            get{return hvNameInput;}
            set{string next=value??string.Empty;if(next==hvNameInput)return;hvNameInput=next;Invalidate();}
        }
        [Browsable(false)] public string CurrentHVname { get{string status;return ResolveHvName(out status);} }
        [Browsable(false)] public string HVnameStatusCode { get{string status;ResolveHvName(out status);return status;} }
        [Browsable(false)] public int HVnameProjectReadCount { get;private set; }

        [Category("Data"),DefaultValue("")]
        [Description("Optional actual LOC/DEM source path for custom acquisition IDs. Must be an existing top-level file of ProjectPath and match this MAP's phase/voltage. Update when the source acquisition changes. Empty uses the Task18 tree ID mapping. Does not reload matrices or rescan the catalog.")]
        public string HVnameSourcePath
        {
            get{return hvSourcePathInput;}
            set{hvSourcePathInput=(value??string.Empty).Trim();ValidateHvSourcePath();Invalidate();}
        }
        [Browsable(false)] public string MatchedHVnameSourcePath
        {
            get{string status;HvProjectRecord record=ResolveHvSourceRecord(out status);return record==null?string.Empty:status=="ExplicitSourceFileSequence"?hvValidatedSourcePath:record.LocationPath;}
        }
        [Browsable(false)] public string HVnameStatus
        {
            get
            {
                string code=HVnameStatusCode,text;
                switch(code)
                {
                    case "Manual":text=L("Manual HVname is displayed; automatic metadata is overridden.","显示手动 HVname，自动元数据已被覆盖。");break;
                    case "ProjectFileSequence":text=L("Time and repetition count match the project's acquisition ID, phase and voltage.","测试时间与加压次数已按当前项目的采集编号、相别和电压匹配。");break;
                    case "ExplicitSourceFileSequence":text=L("Time and repetition count use the explicitly identified source file. Keep HVnameSourcePath synchronized when the source acquisition changes.","测试时间与加压次数来自显式指定的源文件；切换采集记录时请同步更新 HVnameSourcePath。");break;
                    case "NoCurrentMap":text=L("No original positioning row is loaded for the current MAP.","当前 MAP 未加载原始定位参数行。");break;
                    case "InvalidPhase":text=L("Original column 0 must be ALL=0, A=1, B=2 or C=3.","原始矩阵第0列须为 ALL=0、A=1、B=2 或 C=3。");break;
                    case "InvalidVoltage":text=L("Original column 2 does not contain a valid nonnegative voltage.","原始矩阵第2列没有有效的非负电压。");break;
                    case "Disabled":text=L("ProjectPath is empty; time and repetition cannot be resolved from the matrix alone.","ProjectPath 为空，仅凭矩阵不能获取测试时间和加压次数。");break;
                    case "InvalidProjectPath":text=L("ProjectPath does not identify an existing project directory or ITEM.in.","ProjectPath 不是存在的项目目录或 ITEM.in。");break;
                    case "InvalidAcquisitionId":text=L("The acquisition identifier is invalid. Select a matching source acquisition file.","采集编号无效，请选择对应的原始采集文件。");break;
                    case "AcquisitionNotFound":text=L("The original acquisition ID is absent from the current project. Supply the actual HVnameSourcePath; time/order are not guessed.","当前项目找不到原始采集ID；请提供实际 HVnameSourcePath，不猜测时间或次序。");break;
                    case "AcquisitionMismatch":text=L("The acquisition ID points to a different phase or voltage. The project may have changed or use another ID convention; supply the actual HVnameSourcePath.","采集ID对应文件的相别或电压不一致，项目文件可能变化或采用其他ID规则；请提供实际 HVnameSourcePath。");break;
                    case "LocationFileMissing":text=L("The acquisition has no LOC file; automatic source identity is unavailable.","该采集记录没有 LOC 文件，无法自动确认定位波形来源。");break;
                    case "SourceOutsideProject":text=L("HVnameSourcePath must be a top-level file in the current ProjectPath, not another project or a subfolder.","HVnameSourcePath 必须位于当前 ProjectPath 顶层，不能是其他项目或子目录。");break;
                    case "SourceFileMissing":text=L("HVnameSourcePath does not exist. Correct it or refresh project metadata.","HVnameSourcePath 文件不存在，请修正路径或刷新项目元数据。");break;
                    case "InvalidSourcePath":text=L("HVnameSourcePath must be an absolute LOC or DEM file path.","HVnameSourcePath 须为 LOC 或 DEM 文件的完整路径。");break;
                    case "SourceNotInCatalog":text=L("The source file is not in the cached project catalog; call ReloadProjectMetadata after project files change.","源文件不在项目缓存列表中；项目文件变化后请调用 ReloadProjectMetadata。");break;
                    case "SourcePhaseVoltageMismatch":text=L("The explicit source file does not match the current MAP's original phase/voltage. Update HVnameSourcePath for this acquisition.","显式源文件与当前 MAP 的原始相别/电压不一致，请同步更新 HVnameSourcePath。");break;
                    case "UnrecognizedProjectFileName":text=L("An acquisition filename does not follow the supported timestamp format; automatic ID/time/order inference is disabled to prevent misidentification.","存在不符合时间命名格式的采集文件；为避免身份错配，已禁用自动ID、时间和次序推断。");break;
                    case "NoAcquisitionFiles":text=L("No top-level LOC/DEM acquisition files were found.","项目顶层未找到 LOC/DEM 采集文件。");break;
                    case "ProjectCatalogTooLarge":text=L("The project filename catalog exceeds the bounded scan limit.","项目文件名列表超过限定扫描数量。");break;
                    default:text=L("Project source metadata could not be read; check ProjectPath and file access.","无法读取项目源文件信息，请检查 ProjectPath 和文件访问权限。");break;
                }
                return text+" ["+code+"]";
            }
        }

        string ResolveHvName(out string status)
        {
            if(!string.IsNullOrWhiteSpace(hvNameInput)){status="Manual";return hvNameInput;}
            if(!matrixDriven||originalParameterRow==null||originalParameterRow.Length<3){status="NoCurrentMap";return string.Empty;}
            double phase=Safe(originalParameterRow,0,double.NaN),voltage=Safe(originalParameterRow,2,double.NaN);
            string physical=HvPhaseFromCode(phase);
            bool voltageValid=IsFinite(voltage)&&voltage>=0;
            var parts=new List<string>();
            if(physical.Length>0)parts.Add(english&&physical!="ALL"?"L"+((int)phase).ToString(CultureInfo.InvariantCulture):physical);
            if(voltageValid)parts.Add(voltage.ToString("0.0##",CultureInfo.InvariantCulture)+"kV");
            status=physical.Length==0?"InvalidPhase":!voltageValid?"InvalidVoltage":hvCatalogCode;
            if(physical.Length==0||!voltageValid)return string.Join(" ",parts);
            HvProjectRecord record=ResolveHvSourceRecord(out status);
            if(record==null)return string.Join(" ",parts);
            string count=record.Repeat.ToString(CultureInfo.InvariantCulture)+"/"+record.Total.ToString(CultureInfo.InvariantCulture);
            parts.Add(String.Format(L("Energization {0}","第{0}次加压"),count));
            parts.Add(record.Stamp.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture));
            return string.Join(" ",parts);
        }

        HvProjectRecord ResolveHvSourceRecord(out string status)
        {
            if(!matrixDriven||originalParameterRow==null||originalParameterRow.Length<3){status="NoCurrentMap";return null;}
            string phase=HvPhaseFromCode(Safe(originalParameterRow,0,double.NaN));double voltage=Safe(originalParameterRow,2,double.NaN);
            if(phase.Length==0){status="InvalidPhase";return null;}
            if(!IsFinite(voltage)||voltage<0){status="InvalidVoltage";return null;}
            HvProjectRecord record;
            if(hvSourcePathInput.Length>0)
            {
                status=hvSourcePathCode;record=hvExplicitSourceRecord;if(record==null)return null;
                if(!HvRecordMatches(record,phase,voltage)){status="SourcePhaseVoltageMismatch";return null;}
                status="ExplicitSourceFileSequence";return record;
            }
            double inputId=Safe(originalParameterRow,1,double.NaN);
            if(!IsFinite(inputId)||inputId<1||inputId>int.MaxValue||inputId!=Math.Truncate(inputId)){status="InvalidAcquisitionId";return null;}
            status=hvCatalogCode;if(hvCatalogCode!="CatalogLoaded")return null;
            if(!hvProjectRecords.TryGetValue((int)inputId,out record)){status="AcquisitionNotFound";return null;}
            if(!HvRecordMatches(record,phase,voltage)){status="AcquisitionMismatch";return null;}
            if(string.IsNullOrEmpty(record.LocationPath)){status="LocationFileMissing";return null;}
            status="ProjectFileSequence";return record;
        }
        static bool HvRecordMatches(HvProjectRecord record,string phase,double voltage)
        {return record.Phase==phase&&record.Voltage.ToString("0.00",CultureInfo.InvariantCulture)==voltage.ToString("0.00",CultureInfo.InvariantCulture);}

        void ValidateHvSourcePath()
        {
            hvExplicitSourceRecord=null;hvValidatedSourcePath=string.Empty;hvSourcePathCode="Disabled";
            if(hvSourcePathInput.Length==0)return;
            if(hvCatalogCode!="CatalogLoaded"){hvSourcePathCode=hvCatalogCode;return;}
            try
            {
                if(!Path.IsPathRooted(hvSourcePathInput)){hvSourcePathCode="InvalidSourcePath";return;}
                string path=Path.GetFullPath(hvSourcePathInput),extension=Path.GetExtension(path);
                if(!extension.Equals(".loc",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".dem",StringComparison.OrdinalIgnoreCase)){hvSourcePathCode="InvalidSourcePath";return;}
                if(!string.Equals(Path.GetDirectoryName(path),hvProjectRoot,StringComparison.OrdinalIgnoreCase)){hvSourcePathCode="SourceOutsideProject";return;}
                if(!File.Exists(path)){hvSourcePathCode="SourceFileMissing";return;}
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0){hvSourcePathCode="InvalidSourcePath";return;}
                string name=Path.GetFileNameWithoutExtension(path);
                hvExplicitSourceRecord=hvProjectRecords.Values.FirstOrDefault(r=>string.Equals(r.Name,name,StringComparison.OrdinalIgnoreCase));
                if(hvExplicitSourceRecord==null){hvSourcePathCode="SourceNotInCatalog";return;}
                hvValidatedSourcePath=path;hvSourcePathCode="ExplicitSourceFileSequence";
            }
            catch(Exception ex) when(IsProjectMetadataReadException(ex)){hvSourcePathCode="InvalidSourcePath";hvExplicitSourceRecord=null;}
        }

        // Called only by the existing project-catalog refresh path. Rendering,
        // dragging, font/locale/ID changes and exporting never enumerate files.
        void ReadHvProjectCatalog()
        {
            hvProjectRecords.Clear();hvCatalogCode="Disabled";hvProjectRoot=string.Empty;hvExplicitSourceRecord=null;hvValidatedSourcePath=string.Empty;hvSourcePathCode="Disabled";
            if(string.IsNullOrWhiteSpace(projectPath))return;
            HVnameProjectReadCount++;
            try
            {
                string root=Path.GetFullPath(projectPath.Trim());
                if(File.Exists(root))
                {
                    if(!Path.GetFileName(root).Equals("ITEM.in",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Expected project directory or ITEM.in.");
                    root=Path.GetDirectoryName(root);
                }
                if(!Directory.Exists(root)){hvCatalogCode="InvalidProjectPath";return;}
                hvProjectRoot=root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                var records=new Dictionary<string,HvProjectRecord>(StringComparer.OrdinalIgnoreCase);
                int examined=0;
                foreach(string path in Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly))
                {
                    if(++examined>20000){hvCatalogCode="ProjectCatalogTooLarge";return;}
                    string extension=Path.GetExtension(path);
                    bool location=extension.Equals(".loc",StringComparison.OrdinalIgnoreCase);
                    if(!location&&!extension.Equals(".dem",StringComparison.OrdinalIgnoreCase))continue;
                    string name=Path.GetFileNameWithoutExtension(path);HvProjectRecord record;
                    if(!records.TryGetValue(name,out record))
                    {
                        if(records.Count>=10000){hvCatalogCode="ProjectCatalogTooLarge";return;}
                        record=ParseHvProjectRecord(name);
                        // Skipping one malformed name could renumber every later ID.
                        // No timestamp-from-file fallback is used because Task18 has
                        // both validated and path-only modes with different fallbacks.
                        if(record==null){hvCatalogCode="UnrecognizedProjectFileName";return;}
                        records.Add(name,record);
                    }
                    if(location)record.LocationPath=path;
                }
                var ordered=records.Values.OrderBy(r=>r.Stamp).ThenBy(r=>HvPhaseRank(r.Phase)).ThenBy(r=>r.Name,StringComparer.OrdinalIgnoreCase).ToList();
                var totals=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                foreach(var record in ordered)
                {
                    string key=record.Phase+"|"+record.Voltage.ToString("0.00",CultureInfo.InvariantCulture);
                    int n;totals.TryGetValue(key,out n);record.Repeat=n+1;totals[key]=record.Repeat;
                }
                for(int i=0;i<ordered.Count;i++)
                {
                    var record=ordered[i];record.Id=i+1;
                    record.Total=totals[record.Phase+"|"+record.Voltage.ToString("0.00",CultureInfo.InvariantCulture)];
                    hvProjectRecords.Add(record.Id,record);
                }
                hvCatalogCode=ordered.Count==0?"NoAcquisitionFiles":"CatalogLoaded";
            }
            catch(Exception ex) when(IsProjectMetadataReadException(ex))
            {hvProjectRecords.Clear();hvCatalogCode=ex is ArgumentException||ex is DirectoryNotFoundException?"InvalidProjectPath":"ProjectReadError";}
            finally{ValidateHvSourcePath();}
        }

        static HvProjectRecord ParseHvProjectRecord(string name)
        {
            Match m=HvFileNamePattern.Match(name??string.Empty);double voltage;DateTime stamp;
            if(!m.Success||!double.TryParse(m.Groups[2].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out voltage)||!IsFinite(voltage)||voltage<0)return null;
            string timestamp=string.Concat(Enumerable.Range(3,6).Select(i=>m.Groups[i].Value));
            if(!DateTime.TryParseExact(timestamp,"yyyyMMddHHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out stamp))return null;
            return new HvProjectRecord{Name=name,Phase=m.Groups[1].Value.ToUpperInvariant(),Voltage=voltage,Stamp=stamp,LocationPath=string.Empty};
        }
        static string HvPhaseFromCode(double phase)
        {return phase==0?"ALL":phase==1?"A":phase==2?"B":phase==3?"C":string.Empty;}
        static int HvPhaseRank(string phase){return phase=="A"?0:phase=="B"?1:phase=="C"?2:3;}
    }
}
