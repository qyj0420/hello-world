using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace HVX.CalibrationStorage
{
    public sealed class CalibrationRawReference { public string RelativePath; public string Sha256; }
    // V1 references capture files; V2 retains effective windows. V3 stores only
    // the latest effective average and its actual acquisition/quality metadata.
    public sealed class LiveCalibrationRecord
    {
        public int Version=1, RangeIndex, PulseCount, IncidentPeakIndex, ReflectionPeakIndex;
        public string Phase, Level, DeviceSerial, DeviceModel, FilterId, LowPassFilterId, ProjectHash, CreatedUtc, AcquisitionMode;
        // Optional additive metadata. The exact acquisition geometry hash stays
        // unchanged for provenance; length/TDR and reviewed joints are not a
        // new electrical calibration. Old files without this scope remain strict.
        public string ProjectScopeHash;
        // Missing XML member in historical records retains the cable contract.
        public string MeasurementKind = "Cable";
        // Optional provenance; historical records must not be relabelled checked.
        public string CircuitReferenceStatus = "Unspecified";
        // Historical XML has unknown preprocessing provenance, never assume AC.
        public int PicoCoupling=-1;
        public string DcRemoval="";
        // Historical coefficients retain unknown preprocessing; never relabel
        // an older low-passed coefficient as a newly measured HP-only value.
        public string CoefficientProcessing="";
        public int CoefficientIncidentPeakIndex=-1;
        // Operator-accepted low-quality records retain the warning, never masquerade as quality passed.
        public bool QualityWarningAcknowledged;
        public string QualityWarning="";
        // Omitted historical members keep the original paired-cable contract.
        // Incident-only coefficients must explicitly retain unknown reflection
        // evidence; configured velocity is not a measured propagation result.
        public string ReflectionStatus="Unspecified", VelocitySource="Unspecified";
        public string ReflectionNotice="", ReflectionSelectionMethod="Unspecified";
        // Explicit editing coordinates may exist even when the selected sample
        // has zero amplitude and cannot support attenuation/propagation evidence.
        // Keep that choice separate from measured reflection fields.
        public double ManualIncidentSeconds=Double.NaN,ManualReflectionSeconds=Double.NaN;
        // Additive provenance. Missing historical XML retains exact legacy
        // cursor semantics; it is never silently labelled peak-anchored.
        public bool PeakAnchoredSelection;
        public const string UnconfirmedReflectionNotice="反射尚未确认。本次仅保存入射信号计算的放电量校准系数；反射时间与衰减保持待确认，波速沿用当前配置，不作为本次测量结果。";
        // Older incident-only records put this informational limitation in the
        // quality-confirmation field. Only that exact, reflection-only message
        // is separated; low SNR / few pulses and all other warnings remain real.
        [XmlIgnore] public bool HasIncidentQualityWarning { get { return
            Double.IsNaN(SnrDb)||Double.IsInfinity(SnrDb)||SnrDb<20||PulseCount<3||
            QualityWarningAcknowledged&&!(ReflectionStatus=="NotDetected"&&QualityWarning==UnconfirmedReflectionNotice); } }
        [XmlIgnore] public bool IsCapacitor { get { return MeasurementKind == "Capacitor"; } }
        [XmlIgnore] public bool IsTdr { get { return Level=="TDR"; } }
        [XmlIgnore] public string SourcePath { get; private set; }
        [XmlIgnore] public bool HasMeasuredReflection { get { return !IsCapacitor && ReflectionStatus!="NotDetected"; } }
        public double LengthM, VelocityMPerUs, SampleRateHz, IncidentSeconds, ReflectionSeconds,
            CoefficientPcPerV, AttenuationDb, SnrDb, Correlation, BandwidthMHz, ChargePc, TriggerThresholdV;
        public bool ShortCable, FirstArrival;
        public CalibrationRawReference[] RawFiles;
        public double[] Average;
        [XmlIgnore] public double[][] EffectiveWindows;
        public bool ShouldSerializeAverage(){return Version==1;}
        private const int MaxArchiveBytes=96*1024*1024;
        private const int MaxStoredSamples=8*1024*1024;

        public static string RecordPath(string root,string phase,string level)
        {
            if(!new[]{"ALL","A","B","C"}.Contains(phase)||String.IsNullOrEmpty(level)||level.Length>16||level.Any(c=>!Char.IsLetterOrDigit(c)))throw new InvalidDataException("校准相别/档位无效。");
            return Path.Combine(Path.GetFullPath(root),phase+"_"+level+".cal");
        }
        public static bool IsArchiveFile(string path)
        {
            if(String.IsNullOrEmpty(path))return false;
            if(String.Equals(Path.GetExtension(path),".hvcal",StringComparison.OrdinalIgnoreCase))return true;
            if(!String.Equals(Path.GetExtension(path),".cal",StringComparison.OrdinalIgnoreCase)||!File.Exists(path))return false;
            CheckNoLinks(path);
            using(var stream=File.OpenRead(path)) {
                if(stream.Length<8)return false;
                byte[] magic=new byte[8];if(stream.Read(magic,0,magic.Length)!=8)return false;
                string value=Encoding.ASCII.GetString(magic);
                return value=="HVCLIVE1"||value=="HVCLIVE2"||value=="HVCLIVE3";
            }
        }
        public static string[] EnumerateRecordFiles(string root)
        {
            string directory=Path.GetFullPath(root);
            if(!Directory.Exists(directory))return new string[0];
            var current=Directory.GetFiles(directory,"*.cal").Where(IsArchiveFile).ToArray();
            var names=new System.Collections.Generic.HashSet<string>(current.Select(Path.GetFileNameWithoutExtension),StringComparer.OrdinalIgnoreCase);
            string legacy=Path.Combine(directory,"Calibration","Live");
            return current.Concat(Directory.Exists(legacy)?Directory.GetFiles(legacy,"*.hvcal").Where(p=>!names.Contains(Path.GetFileNameWithoutExtension(p))):new string[0])
                .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public static string HashFile(string path)
        { using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream)); }
        public static string Identity(string root)
        {
            using(var sha=SHA256.Create()) {
                string value=HashFile(Path.Combine(root,"ITEM.in"))+"|"+HashFile(Path.Combine(root,"CABLE.in"));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
            }
        }
        public static string ScopeIdentity(string root)
        {
            var value=new StringBuilder("HVX.Calibration.ProjectScope/1\n");
            string[] mutable={"电缆长度","A相电缆长度","B相电缆长度","C相电缆长度",
                "ALL相采用波速","A相采用波速","B相采用波速","C相采用波速",
                "CableLength","CableLengthA","CableLengthB","CableLengthC",
                "AdoptedVelocityALL","AdoptedVelocityA","AdoptedVelocityB","AdoptedVelocityC"};
            foreach(string raw in ReadProjectText(Path.Combine(root,"ITEM.in")).Replace("\r","").Split('\n')) {
                string line=raw.Trim();if(line.Length==0)continue;
                int eq=line.IndexOf('=');string key=eq<0?"":line.Substring(0,eq).Trim().TrimEnd(':','：').Trim();
                if(!mutable.Contains(key))value.Append(line).Append('\n');
            }
            // Phase and physical endpoint identity remain bound. Internal joint
            // candidates/material edits and terminal distance are review data.
            string first=null,last=null;
            Action finish=()=>{if(first!=null)value.Append(first).Append('\n').Append(last).Append('\n');first=last=null;};
            foreach(string line in ReadProjectText(Path.Combine(root,"CABLE.in")).Replace("\r","").Split('\n')) {
                if(String.IsNullOrWhiteSpace(line))continue;
                string[] cells=line.Split('\t');if(cells.Length!=4)throw new InvalidDataException("电缆结构格式无效。");
                if(cells[0].Trim()=="0"){finish();value.Append("phase:").Append(cells[1].Trim()).Append('\n');}
                else if(cells[0].Trim()=="1") {
                    string endpoint=String.Join("\t",cells.Take(3).Select(c=>c.Trim()));
                    if(first==null)first=endpoint;last=endpoint;
                }
            }
            finish();
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString())));
        }
        private static string ReadProjectText(string path)
        {
            CheckNoLinks(path);var info=new FileInfo(path);
            if(!info.Exists||info.Length>2*1024*1024)throw new InvalidDataException("项目元数据不可用。");
            byte[] bytes=File.ReadAllBytes(path);
            if(bytes.Length>=2&&bytes[0]==255&&bytes[1]==254)return Encoding.Unicode.GetString(bytes,2,bytes.Length-2);
            if(bytes.Length>=2&&bytes[0]==254&&bytes[1]==255)return Encoding.BigEndianUnicode.GetString(bytes,2,bytes.Length-2);
            try{return new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');}
            catch(DecoderFallbackException){return Encoding.GetEncoding(936).GetString(bytes);}
        }
        public static string ResolveRaw(string root,string relative)
        {
            if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new InvalidDataException("原始数据路径无效。");
            string folder=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(Path.Combine(folder,relative));
            if(!full.StartsWith(folder,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("原始数据路径不属于当前项目。");
            CheckNoLinks(full);return full;
        }
        private static void CheckNoLinks(string path)
        {
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("校准文件不能是链接。");
            for(var d=new DirectoryInfo(Path.GetDirectoryName(path));d!=null;d=d.Parent)
                if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("校准路径不能包含链接目录。");
        }
        public void Validate(string root,bool verifyRaw)
        {
            RecordPath(root,Phase,Level);
            if(MeasurementKind!="Cable"&&MeasurementKind!="Capacitor")throw new InvalidDataException("校准测量类型无效。");
            if(IsCapacitor!=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root))throw new InvalidDataException("校准测量类型与项目负载类型不一致。");
            bool warned=QualityWarningAcknowledged&&!String.IsNullOrWhiteSpace(QualityWarning);
            if(QualityWarningAcknowledged&&!warned)throw new InvalidDataException("质量提示确认记录缺失。");
            if(!new[]{"Unspecified","Automatic","Manual"}.Contains(ReflectionSelectionMethod)||
                !new[]{"Unspecified","Detected","NotDetected","NotApplicable"}.Contains(ReflectionStatus)||
                !new[]{"Unspecified","Measured","Configured","NotApplicable"}.Contains(VelocitySource))
                throw new InvalidDataException("校准反射或波速来源标记无效。");
            if(!Double.IsNaN(ManualIncidentSeconds)||!Double.IsNaN(ManualReflectionSeconds)) {
                if(IsCapacitor||ReflectionSelectionMethod!="Manual"||!Finite(ManualIncidentSeconds)||!Finite(ManualReflectionSeconds)||
                    ManualIncidentSeconds<0||ManualReflectionSeconds<=ManualIncidentSeconds||!Positive(SampleRateHz)||Average==null||
                    ManualReflectionSeconds*SampleRateHz>=Average.Length)
                    throw new InvalidDataException("人工游标坐标与原波形范围不一致。");
            }
            if(PeakAnchoredSelection) {
                if(IsCapacitor||!HasMeasuredReflection||ReflectionSelectionMethod!="Manual"||!Positive(SampleRateHz)||
                    !Finite(ManualIncidentSeconds)||!Finite(ManualReflectionSeconds)||
                    Math.Abs(ManualIncidentSeconds*SampleRateHz-IncidentPeakIndex)>1e-6||
                    Math.Abs(ManualReflectionSeconds*SampleRateHz-ReflectionPeakIndex)>1e-6||
                    (FirstArrival?(IncidentSeconds*SampleRateHz>=IncidentPeakIndex||ReflectionSeconds*SampleRateHz>=ReflectionPeakIndex||
                        ReflectionSeconds*SampleRateHz<=IncidentPeakIndex):
                        (Math.Abs(IncidentSeconds*SampleRateHz-IncidentPeakIndex)>1e-6||Math.Abs(ReflectionSeconds*SampleRateHz-ReflectionPeakIndex)>1e-6)))
                    throw new InvalidDataException("所选波峰与传播计时记录不一致。");
            }
            if((Version!=1&&Version!=2&&Version!=3)||String.IsNullOrWhiteSpace(DeviceSerial)||FilterId!=HVX.SignalProcessing.PdLinearHighPass.FilterId||
                !Positive(SampleRateHz)||SampleRateHz<1e5||SampleRateHz>1e9||(!IsTdr&&(!Positive(CoefficientPcPerV)||!Positive(ChargePc)))||
                !Finite(IncidentSeconds)||IncidentSeconds<0||Double.IsInfinity(SnrDb)||(!IsTdr&&!warned&&(!Finite(SnrDb)||SnrDb<20))||
                !Finite(BandwidthMHz)||BandwidthMHz<0||BandwidthMHz>60||(BandwidthMHz>0&&(BandwidthMHz<1||LowPassFilterId!=HVX.SignalProcessing.PdLinearLowPass.FilterId))||RangeIndex<0||RangeIndex>10||PulseCount<(warned||IsTdr?1:3)||PulseCount>(AcquisitionMode=="Segment"?4096:256)||
                Average==null||Average.Length<128||Average.Length>2000000||Average.Any(v=>!Finite(v))||IncidentSeconds*SampleRateHz>=Average.Length||
                IncidentPeakIndex<0||IncidentPeakIndex>=Average.Length||
                // A finite segment can contain many valid calibration pulses.
                // Self-contained V2/V3 archives retain the actual pulse count and
                // must not require three different acquisition files as a proxy.
                // V1 remains raw-reference dependent with its historical contract.
                RawFiles==null||RawFiles.Length<(Version>=2||warned||IsTdr?1:3)||RawFiles.Length>268)
                throw new InvalidDataException("实机校准的来源、质量或计算参数未通过校验。");
            if(IsTdr&&((Version!=2&&Version!=3)||IsCapacitor||!Double.IsNaN(CoefficientPcPerV)||!Double.IsNaN(ChargePc)||
                ReflectionStatus!="Detected"||VelocitySource!="Configured"))
                throw new InvalidDataException("测距记录必须保留采用波速和实测时差，不得伪造电量系数。");
            if(Version==2) {
                if(CoefficientProcessing!="HP10K_NO_LP"||EffectiveWindows==null||EffectiveWindows.Length!=PulseCount)
                    throw new InvalidDataException("独立校准档案缺少有效脉冲或处理口径。");
                long total=Average.Length;
                foreach(var window in EffectiveWindows) {
                    if(window==null||window.Length!=Average.Length||window.Any(v=>!Finite(v)))
                        throw new InvalidDataException("独立校准脉冲长度或数据无效。");
                    total+=window.Length;
                }
                if(total>MaxStoredSamples)throw new InvalidDataException("独立校准数据超过保存上限；原始采集保留。");
            }
            if(Version==3&&(CoefficientProcessing!="HP10K_NO_LP"||(EffectiveWindows!=null&&EffectiveWindows.Length!=0)))
                throw new InvalidDataException("平均校准档案仅保存最后有效平均波形，不重复保存单脉冲。");
            if(IsCapacitor) {
                if(LengthM!=0||VelocityMPerUs!=0||ReflectionSeconds!=0||ReflectionPeakIndex!=-1||AttenuationDb!=0||Correlation!=0)
                    throw new InvalidDataException("电容校准不允许伪造反射、长度、波速或衰减参数。");
                if(Average.All(v=>v==0))throw new InvalidDataException("电容校准缺少实际入射脉冲。");
            } else {
                if(!Positive(LengthM)||LengthM>1e6||!Positive(VelocityMPerUs)||VelocityMPerUs<50||VelocityMPerUs>250)
                    throw new InvalidDataException("电缆校准的长度或采用波速无效。");
                if(ReflectionStatus=="NotDetected") {
                    if(VelocitySource!="Configured"||!Double.IsNaN(ReflectionSeconds)||ReflectionPeakIndex!=-1||
                        !Double.IsNaN(AttenuationDb)||!Double.IsNaN(Correlation))
                        throw new InvalidDataException("未确认反射的校准不能写入未经确认的反射或衰减结果。");
                } else {
                if(ReflectionStatus=="NotApplicable"||(!IsTdr&&VelocitySource=="Configured")||VelocitySource=="NotApplicable"||
                    !Finite(ReflectionSeconds)||ReflectionSeconds<=IncidentSeconds||(!IsTdr&&(!Finite(AttenuationDb)||!Finite(Correlation)))||(!IsTdr&&!warned&&Math.Abs(Correlation)<.6)||Double.IsInfinity(Correlation)||Double.IsInfinity(AttenuationDb)||Math.Abs(Correlation)>1.000001||
                    ReflectionSeconds*SampleRateHz>=Average.Length||ReflectionPeakIndex<=IncidentPeakIndex||ReflectionPeakIndex>=Average.Length)
                    throw new InvalidDataException("电缆校准的反射、长度、波速或衰减参数未通过校验。");
                double velocityFromTiming=2*LengthM/((ReflectionSeconds-IncidentSeconds)*1e6);
                if(!Finite(velocityFromTiming)||Math.Abs(velocityFromTiming-VelocityMPerUs)>VelocityMPerUs*1e-6)
                    throw new InvalidDataException("校准长度、入射/反射时差与波速不一致。");
                }
            }
            if(!String.IsNullOrEmpty(ProjectScopeHash)) {
                try {if(Convert.FromBase64String(ProjectHash??"").Length!=32||Convert.FromBase64String(ProjectScopeHash).Length!=32)throw new FormatException();}
                catch(FormatException){throw new InvalidDataException("校准项目来源哈希无效。");}
            }
            if(String.IsNullOrEmpty(ProjectScopeHash)?ProjectHash!=Identity(root):ProjectScopeHash!=ScopeIdentity(root))
                throw new InvalidDataException("项目资料已变化，需重新核对校准。");
            foreach(var file in RawFiles) {
                if(file==null)throw new InvalidDataException("原始数据引用缺失。");
                string path=ResolveRaw(root,file.RelativePath);
                if(Version>=2) {
                    try {if(Convert.FromBase64String(file.Sha256??"").Length!=32)throw new FormatException();}
                    catch(FormatException){throw new InvalidDataException("原始采集来源哈希无效。");}
                }
                if(verifyRaw && Version==1 && (!File.Exists(path)||HashFile(path)!=file.Sha256))throw new InvalidDataException("原始采集文件缺失或已改变："+file.RelativePath);
            }
        }
        public string Save(string root)
        {
            Validate(root,true);
            if(String.IsNullOrEmpty(ProjectScopeHash))ProjectScopeHash=ScopeIdentity(root);
            string path=RecordPath(root,Phase,Level);CheckNoLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            bool existed=File.Exists(path);FileAttributes attributes=existed?File.GetAttributes(path):FileAttributes.Normal;
            if(existed&&!IsArchiveFile(path))throw new IOException("同名历史校准文件已存在，请保留并改名后再保存，不覆盖不同格式的原始档案。");
            try {
                using(var buffer=new MemoryStream()) {
                    if(Version==1)new XmlSerializer(typeof(LiveCalibrationRecord)).Serialize(buffer,this);
                    else using(var writer=new BinaryWriter(buffer,Encoding.UTF8,true)) {
                        using(var xml=new MemoryStream()) {
                            new XmlSerializer(typeof(LiveCalibrationRecord)).Serialize(xml,this);
                            writer.Write(checked((int)xml.Length));writer.Write(xml.ToArray());
                        }
                        WriteSamples(writer,Average);
                        if(Version==2){writer.Write(EffectiveWindows.Length);foreach(var window in EffectiveWindows)WriteSamples(writer,window);}
                        writer.Flush();
                    }
                    if(buffer.Length>MaxArchiveBytes-44)throw new InvalidDataException("校准档案超过保存上限。");
                    byte[] payload=buffer.ToArray(),hash;using(var sha=SHA256.Create())hash=sha.ComputeHash(payload);
                    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(stream)) {
                        writer.Write(Encoding.ASCII.GetBytes("HVCLIVE"+Version));writer.Write(payload.Length);writer.Write(hash);writer.Write(payload);writer.Flush();stream.Flush(true);
                    }
                }
                Load(root,temporary,true);
                if(existed&&(attributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(path,attributes&~FileAttributes.ReadOnly);
                // A verified new record atomically replaces the previous valid
                // phase/level record; do not accumulate a full backup on each Save.
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.ReadOnly);
                SourcePath=Path.GetFullPath(path);
                return path;
            }finally{if(File.Exists(path)&&existed&&(attributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.ReadOnly);if(File.Exists(temporary))File.Delete(temporary);}
        }
        // Called only after publishing a newly acquired average. Never scan or
        // remove historical project trees; only exact hash-matching inputs of
        // this verified self-contained record can be retired.
        public static void RetireAveragedCaptureCache(string root,string archive)
        {
            try {
                using(var archiveLock=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    var record=Load(root,archive,true);if(record.Version!=3)return;
                    string boundary=Path.GetFullPath(Path.Combine(root,"Calibration","Acquisition")).TrimEnd(Path.DirectorySeparatorChar);
                    foreach(string other in EnumerateRecordFiles(root))
                        if(Load(root,other,false).Version==1)return; // Legacy reference ownership is not disposable.
                    foreach(var reference in record.RawFiles) {
                        string path=ResolveRaw(root,reference.RelativePath);
                        if(!path.StartsWith(boundary+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))continue;
                        CheckNoLinks(path);
                        if(HashFile(path)!=reference.Sha256)continue;
                        File.Delete(path);
                        string directory=Path.GetDirectoryName(path);
                        while(directory.StartsWith(boundary+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                            Directory.Exists(directory)&&!Directory.EnumerateFileSystemEntries(directory).Any()) {
                            CheckNoLinks(directory);Directory.Delete(directory,false);directory=Path.GetDirectoryName(directory);
                        }
                    }
                }
            }catch(Exception error)when(error is IOException||error is UnauthorizedAccessException||error is InvalidOperationException||error is ArgumentException){
                System.Diagnostics.Trace.TraceWarning("Calibration cache retained: "+error.Message);
            }
        }
        public static LiveCalibrationRecord Load(string root,string path,bool verifyRaw=true)
        {
            CheckNoLinks(path);
            using(var reader=new BinaryReader(File.OpenRead(path))) {
                if(reader.BaseStream.Length<44||reader.BaseStream.Length>MaxArchiveBytes)throw new InvalidDataException("不是有效实机校准档案。");
                string magic=Encoding.ASCII.GetString(reader.ReadBytes(8));
                if(magic!="HVCLIVE1"&&magic!="HVCLIVE2"&&magic!="HVCLIVE3")throw new InvalidDataException("不是有效实机校准档案。");
                int length=reader.ReadInt32();if(length<1||length!=reader.BaseStream.Length-44)throw new InvalidDataException("校准档案不完整。");
                byte[] hash=reader.ReadBytes(32),payload=reader.ReadBytes(length);
                using(var sha=SHA256.Create())if(!hash.SequenceEqual(sha.ComputeHash(payload)))throw new InvalidDataException("校准校验和不匹配。");
                var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxArchiveBytes};
                using(var buffer=new MemoryStream(payload)) {
                    LiveCalibrationRecord record;
                    if(magic=="HVCLIVE1")using(var xml=XmlReader.Create(buffer,settings)) {
                        record=(LiveCalibrationRecord)new XmlSerializer(typeof(LiveCalibrationRecord)).Deserialize(xml);
                        if(record.Version!=1)throw new InvalidDataException("校准档案版本不匹配。");
                    }else using(var binary=new BinaryReader(buffer,Encoding.UTF8,true)) {
                        int xmlLength=binary.ReadInt32();
                        if(xmlLength<1||xmlLength>buffer.Length-buffer.Position)throw new InvalidDataException("校准表头长度无效。");
                        using(var metadata=new MemoryStream(binary.ReadBytes(xmlLength)))using(var xml=XmlReader.Create(metadata,settings))
                            record=(LiveCalibrationRecord)new XmlSerializer(typeof(LiveCalibrationRecord)).Deserialize(xml);
                        if(record.Version!=(magic=="HVCLIVE3"?3:2)||record.Average!=null)throw new InvalidDataException("校准档案版本或重复波形无效。");
                        int remaining=MaxStoredSamples;record.Average=ReadSamples(binary,ref remaining);
                        if(record.Version==2){
                            int count=binary.ReadInt32();
                            if(count<1||count>4096||count!=record.PulseCount)throw new InvalidDataException("校准脉冲数无效。");
                            record.EffectiveWindows=new double[count][];
                            for(int i=0;i<count;i++)record.EffectiveWindows[i]=ReadSamples(binary,ref remaining);
                        }
                        if(buffer.Position!=buffer.Length)throw new InvalidDataException("校准档案存在额外数据。");
                    }
                    record.Validate(root,verifyRaw);record.SourcePath=Path.GetFullPath(path);return record;
                }
            }
        }
        private static void WriteSamples(BinaryWriter writer,double[] samples)
        {writer.Write(samples.Length);foreach(double value in samples)writer.Write(value);}
        private static double[] ReadSamples(BinaryReader reader,ref int remaining)
        {
            int count=reader.ReadInt32();
            if(count<128||count>2000000||count>remaining||(long)count*8>reader.BaseStream.Length-reader.BaseStream.Position)
                throw new InvalidDataException("校准波形长度无效。");
            remaining-=count;var values=new double[count];for(int i=0;i<count;i++)values[i]=reader.ReadDouble();return values;
        }
        // Calibration applicability is not joint-editing scope. Equal cable
        // lengths permit one saved phase (or ALL) to calibrate the other phases;
        // keep the recorded source phase intact for traceability.
        public static bool RequiresSeparatePhases(string root)
        {
            if(HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root))return false;
            var geometry=HVX.ProjectCables.CableJointStore.ReadGeometry(root);
            double[] lengths=geometry.Blocks.Select(b=>b.Length).ToArray();
            return lengths.Max()-lengths.Min()>1e-6;
        }
        public static LiveCalibrationRecord BestForProject(string root,string phase,bool firstArrival,string deviceSerial=null)
        {
            string identity=Identity(root);
            bool separate=RequiresSeparatePhases(root);
            // ALL does not stand in for three independently required calibrations.
            var result=separate&&phase=="ALL"?null:FindBest(root,phase,firstArrival,deviceSerial,!separate,!separate);
            if(identity!=Identity(root))throw new IOException("项目资料已变化，请重新加载校准。 / Project changed; reload calibration.");
            return result;
        }
        public static LiveCalibrationRecord Best(string root,string phase,bool firstArrival,string deviceSerial=null,bool allowCommon=false)
        { return FindBest(root,phase,firstArrival,deviceSerial,allowCommon,false); }
        private static LiveCalibrationRecord FindBest(string root,string phase,bool firstArrival,string deviceSerial,bool allowCommon,bool allowOtherPhase)
        {
            if(!new[]{"ALL","A","B","C"}.Contains(phase))return null;
            LiveCalibrationRecord best=null;int bestRank=-1;
            // Display enumeration hides superseded same-name records. Selection
            // must instead validate every formal archive: an incompatible or
            // corrupt current .cal must not hide a still-applicable .hvcal.
            string historical=Path.Combine(Path.GetFullPath(root),"Calibration","Live");
            var candidates=EnumerateRecordFiles(root).Concat(Directory.Exists(historical)?Directory.GetFiles(historical,"*.hvcal"):new string[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase);
            foreach(string path in candidates)try {
                var item=Load(root,path);
                if(item.IsTdr)continue; // Ranging evidence is never a charge coefficient.
                if((item.Phase!=phase&&!(allowCommon&&item.Phase=="ALL")&&!allowOtherPhase)||item.FirstArrival!=firstArrival||
                    (!String.IsNullOrEmpty(deviceSerial)&&item.DeviceSerial!=deviceSerial))continue;
                int rank=item.Phase==phase?2:item.Phase=="ALL"?1:0;
                // An explicitly accepted unknown SNR remains unknown in its
                // record, but must not poison comparisons with every later
                // same-phase candidate (NaN > x and NaN == x are both false).
                double itemScore=Finite(item.SnrDb)?item.SnrDb:Double.NegativeInfinity;
                double bestScore=best!=null&&Finite(best.SnrDb)?best.SnrDb:Double.NegativeInfinity;
                // Same phase, then common/eligible shared phase; within that
                // group use the highest valid calibration charge as the legacy
                // project selector does. Quality/time only break equal charges.
                if(best==null || rank>bestRank || rank==bestRank&&(item.ChargePc>best.ChargePc || item.ChargePc==best.ChargePc&&
                    (itemScore>bestScore || itemScore==bestScore&&
                    (String.CompareOrdinal(item.CreatedUtc,best.CreatedUtc)>0 || item.CreatedUtc==best.CreatedUtc&&String.CompareOrdinal(path,best.SourcePath)<0)))) {best=item;bestRank=rank;}
            }catch(InvalidDataException){}catch(IOException){}catch(InvalidOperationException){}catch(ArgumentException){}
            return best;
        }
        private static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
        private static bool Positive(double v){return Finite(v)&&v>0;}
    }
}
