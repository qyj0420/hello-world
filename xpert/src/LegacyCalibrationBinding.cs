using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace HVX.CalibrationStorage
{
    public sealed class LegacyCalibrationCandidate
    {
        public string SourceRelativePath, SourceSha256, ObservedProjectHash, SourcePhase, Level;
        public string HistoricalDateText, HistoricalTimeText, HistoricalFilterText, HistoricalAttenuationText;
        public string SourceFormat="LegacyText", HistoricalDeviceSerial="", HistoricalTimingMode="Unspecified";
        public double CoefficientPcPerV, ChargePc, HistoricalLengthM, HistoricalVelocityMPerUs,
            SampleRateHz, RecordedSampleRateHz, HistoricalIncidentSeconds, HistoricalReflectionSeconds;
        public int WaveformSampleCount;
    }

    // An explicit present-day applicability decision, not a new calibration
    // acquisition. The original CAL and its unknown historical device remain
    // untouched. No simulated/raw-per-pulse evidence is fabricated here.
    public sealed class LegacyCalibrationBinding
    {
        public int Version=1;
        public string SourceRelativePath, SourceSha256, ProjectHash, SourcePhase, TargetPhase, Level;
        public string PicoSerial, ArtSerial, ConfirmedUtc, Acknowledgement;
        public string HistoricalDeviceIdentity="Unknown", QualityStatus="NotReassessed";
        // Original declarations remain provenance only, not a verified current binding.
        public string SourceFormat="LegacyText", HistoricalDeviceSerial="", HistoricalTimingMode="Unspecified";
        public string HistoricalDateText, HistoricalTimeText, HistoricalFilterText, HistoricalAttenuationText;
        public double CoefficientPcPerV, ChargePc, HistoricalLengthM, CurrentLengthM, HistoricalVelocityMPerUs,
            SourceSampleRateHz, HistoricalIncidentSeconds, HistoricalReflectionSeconds;
        public int SourceWaveformSampleCount;
        public bool FirstArrival, OperatorConfirmed;
        [XmlIgnore] public string BindingPath;
        // The sidecar keeps the recorded source rate for audit and existing
        // bindings. Consumers use the exact historical correction separately.
        [XmlIgnore] public double EffectiveSourceSampleRateHz { get { return NormalizeLegacySampleRateHz(SourceSampleRateHz); } }
        const int MaxCalBytes=64*1024*1024, MaxSamples=2000000, MaxBindingBytes=64*1024;
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        static readonly string[] Phases={"ALL","A","B","C"};
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
        static bool Positive(double v){return Finite(v)&&v>0;}
        static string Hash(byte[] bytes){using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(bytes));}
        static string NormalizeKey(string value)
        {
            // Historical CAL files use both ASCII and full-width unit brackets.
            // Normalize spelling only; never infer a different unit or identity.
            string key=(value??"").Replace('（','(').Replace('）',')').Replace('［','[').Replace('］',']');
            return Regex.Replace(key.Trim().TrimEnd(':','：'),@"\s+","").ToUpperInvariant();
        }
        // The audited legacy selector accepts SR as Hz, nanoseconds/sample,
        // or seconds/sample. Keep this interpretation shared with browsing;
        // explicit units win and an invalid present SR never falls back to Delta_x.
        static Dictionary<string,string> CanonicalHeader(Dictionary<string,string> header)
        {
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var entry in header){string key=NormalizeKey(entry.Key);
                if(result.ContainsKey(key))throw new InvalidDataException("旧校准头含重复字段，不能确认其含义。");
                result.Add(key,entry.Value);
            }
            return result;
        }
        // Only this known historical save error is corrected. No rounding,
        // estimated rate, sample resampling or source-file rewrite is involved.
        public static double NormalizeLegacySampleRateHz(double rate)
        { return rate==125000004d?125000000d:rate; }
        public static double LegacySampleRate(Dictionary<string,string> header)
        { return NormalizeLegacySampleRateHz(RecordedLegacySampleRate(header)); }
        public static double RecordedLegacySampleRate(Dictionary<string,string> header)
        {
            var h=CanonicalHeader(header);
            string[] keys=h.Keys.Where(k=>k=="SR"||k.StartsWith("SR(",StringComparison.Ordinal)||k.StartsWith("SR[",StringComparison.Ordinal)).ToArray();
            if(keys.Length>1)throw new InvalidDataException("旧校准采样率字段重复，请核对原文件。");
            if(keys.Length==0){
                if(h.ContainsKey("DELTA_X")&&h.ContainsKey("NI_CALIB_FACTOR")&&h.ContainsKey("VELOCITY(M/USEC)"))return Number(h,"DELTA_X");
                return Double.NaN;
            }
            string key=keys[0];double v=Number(h,key);if(!Positive(v))return Double.NaN;
            if(key=="SR(HZ)"||key=="SR[HZ]")return v;
            if(key=="SR(NS)"||key=="SR[NS]")return 1e9/v;
            if(key=="SR(S)"||key=="SR[S]")return 1/v;
            if(key!="SR"&&key!="SR(N)")throw new InvalidDataException("旧校准采样率单位无法确认。");
            return v<.001?1/v:v<1000?1e9/v:v;
        }
        public static double LegacyTimeSeconds(Dictionary<string,string> header,string name)
        {
            var h=CanonicalHeader(header);string prefix=NormalizeKey(name);
            string[] keys=h.Keys.Where(k=>k==prefix||k.StartsWith(prefix+"(",StringComparison.Ordinal)||k.StartsWith(prefix+"[",StringComparison.Ordinal)).ToArray();
            if(keys.Length>1)throw new InvalidDataException("旧校准时间字段重复，请核对原文件。");
            if(keys.Length==0)return Double.NaN;
            string key=keys[0];double v=Number(h,key);
            if(key==prefix)return v>1e-3?v*1e-6:v; // Original unlabelled legacy convention.
            string unit=key.Substring(prefix.Length).Trim('(',')','[',']').Replace("µ","Μ");
            if(unit=="S")return v;
            if(unit=="US"||unit=="USEC"||unit=="ΜS")return v*1e-6;
            throw new InvalidDataException("旧校准时间单位无法确认。");
        }
        static string Value(Dictionary<string,string> header,params string[] keys)
        {foreach(string key in keys){string value;if(header.TryGetValue(NormalizeKey(key),out value))return value;}return "";}
        static double Number(Dictionary<string,string> header,params string[] keys)
        {double value;return Double.TryParse(Value(header,keys),NumberStyles.Float,Inv,out value)?value:Double.NaN;}
        static bool SerialValid(string value)
        {return !String.IsNullOrWhiteSpace(value)&&value==value.Trim()&&value.Length<=128&&!value.Any(Char.IsControl)&&!value.Contains("|");}
        static string LocalPath(string root,string path)
        {
            string folder=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(folder,path));
            if(!full.StartsWith(folder,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请选择当前项目内的校准文件。");
            if(Directory.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("校准目录不能是链接目录。");
            return LiveCalibrationRecord.ResolveRaw(root,full.Substring(folder.Length));
        }
        static byte[] ReadBounded(string path,int maximum)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length<1||stream.Length>maximum)throw new InvalidDataException("校准文件大小无效。");
                var result=new byte[(int)stream.Length];int offset=0;
                while(offset<result.Length){int read=stream.Read(result,offset,result.Length-offset);if(read==0)throw new IOException("校准文件读取不完整。");offset+=read;}
                if(stream.ReadByte()!=-1)throw new IOException("校准文件读取期间发生变化。");return result;
            }
        }
        public static double CurrentLengthForProject(string project,string phase)
        {
            if(!Phases.Contains(phase)||HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(project))
                throw new InvalidDataException("旧电缆校准只能确认用于当前电缆项目的明确相别。");
            var blocks=HVX.ProjectCables.CableJointStore.ReadGeometry(project).Blocks;
            double[] lengths=blocks.Where(b=>phase=="ALL"||b.Phase=="ALL"||b.Phase==phase).Select(b=>b.Length).ToArray();
            if(lengths.Length==0||lengths.Any(v=>!Positive(v))||lengths.Max()-lengths.Min()>1e-6)
                throw new InvalidDataException("当前相别长度不明确；三相长度不同时请分别确认各相校准。");
            return lengths[0];
        }
        public static LegacyCalibrationCandidate ReadCandidate(string project,string path)
        {
            string full=LocalPath(project,path);
            if(!String.Equals(Path.GetExtension(full),".cal",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请选择旧版 CAL 校准文件。");
            LocalPath(project,"ITEM.in");LocalPath(project,"CABLE.in");
            string identity=LiveCalibrationRecord.Identity(project);
            byte[] bytes=ReadBounded(full,MaxCalBytes);string text;
            if(LiveCalibrationRecord.IsArchiveFile(full)) {
                // A .cal extension no longer implies the historical text format.
                // Validate the actual formal envelope and project before offering
                // explicit parameter reuse; never reinterpret failed binary input
                // as text or silently bypass current device/timing applicability.
                var record=LiveCalibrationRecord.Load(project,full,true);
                if(record.IsTdr)throw new InvalidDataException("此文件是测距记录，不含放电量校准系数。");
                if(record.IsCapacitor)throw new InvalidDataException("电容校准不能作为旧电缆校准导入。");
                if(identity!=LiveCalibrationRecord.Identity(project)||Hash(bytes)!=LiveCalibrationRecord.HashFile(full))
                    throw new IOException("校准或项目资料已变化，请重新选择校准。");
                return new LegacyCalibrationCandidate {SourceRelativePath=full.Substring(Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar).Length+1),
                    SourceSha256=Hash(bytes),ObservedProjectHash=identity,SourcePhase=record.Phase,Level=record.Level,
                    CoefficientPcPerV=record.CoefficientPcPerV,ChargePc=record.ChargePc,HistoricalLengthM=record.LengthM,
                    HistoricalVelocityMPerUs=record.VelocityMPerUs,SampleRateHz=record.SampleRateHz,RecordedSampleRateHz=record.SampleRateHz,
                    WaveformSampleCount=record.Average.Length,HistoricalIncidentSeconds=record.IncidentSeconds,
                    HistoricalReflectionSeconds=record.ReflectionSeconds,HistoricalDateText=record.CreatedUtc,HistoricalTimeText="",
                    HistoricalFilterText=record.FilterId,HistoricalAttenuationText=record.AttenuationDb.ToString("R",Inv),
                    SourceFormat="FormalBinary",HistoricalDeviceSerial=record.DeviceSerial,HistoricalTimingMode=record.FirstArrival?"FirstArrival":"Peak"};
            }
            try{text=(bytes.Length>=2&&bytes[0]==255&&bytes[1]==254?new UnicodeEncoding(false,true,true).GetString(bytes):
                bytes.Length>=2&&bytes[0]==254&&bytes[1]==255?new UnicodeEncoding(true,true,true).GetString(bytes):
                new UTF8Encoding(false,true).GetString(bytes)).TrimStart('\uFEFF');}
            catch(DecoderFallbackException){text=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}
            var header=new Dictionary<string,string>(StringComparer.Ordinal);
            int count=0;double min=Double.PositiveInfinity,max=Double.NegativeInfinity;
            using(var reader=new StringReader(text)) {
                for(int i=0;i<13;i++) {
                    string headerLine=reader.ReadLine();if(headerLine==null||headerLine.Length>8192)throw new InvalidDataException("旧校准头信息不完整。");
                    int tab=headerLine.IndexOf('\t');if(tab<0)continue;
                    string key=NormalizeKey(headerLine.Substring(0,tab));if(header.ContainsKey(key))throw new InvalidDataException("旧校准头含重复字段，不能确认其含义。");
                    header[key]=headerLine.Substring(tab+1).Trim();
                }
                bool trailingBlank=false;string line;while((line=reader.ReadLine())!=null) {
                    if(String.IsNullOrWhiteSpace(line)){trailingBlank=true;continue;}
                    if(trailingBlank)throw new InvalidDataException("旧校准波形中间缺少采样值，未移动后续数据的时间位置。");
                    double sample;if(line.Length>128||!Double.TryParse(line.Trim(),NumberStyles.Float,Inv,out sample)||!Finite(sample))
                        throw new InvalidDataException("旧校准波形含无效采样值。");
                    if(++count>MaxSamples)throw new InvalidDataException("旧校准波形过长。");min=Math.Min(min,sample);max=Math.Max(max,sample);
                }
            }
            var name=Regex.Match(Path.GetFileNameWithoutExtension(full),@"^(ALL|A|B|C)_(\d+(?:\.\d+)?)(pC|nC)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(!name.Success)throw new InvalidDataException("旧校准文件名需包含明确相别和校准档位，例如 ALL_5nC.cal。");
            double levelCharge=Double.Parse(name.Groups[2].Value,Inv)*(name.Groups[3].Value.Equals("nC",StringComparison.OrdinalIgnoreCase)?1000:1);
            double charge=Number(header,"Cal.value (pC)","Cal.value(pC)","Cal.value");
            double coefficient=Number(header,"NI_Calib_factor"),length=Number(header,"Cable length (m)","CableLength(m)","Cable length");
            double velocity=Number(header,"V (m/usec)","Velocity (m/usec)","Velocity (m/us)");
            double sr=LegacySampleRate(header);
            string phase=name.Groups[1].Value.ToUpperInvariant(),phaseHeader=Value(header,"Phase");
            if(!Positive(charge)||!Positive(levelCharge)||Math.Abs(charge-levelCharge)>Math.Max(1e-9,levelCharge*1e-9)||
                !Positive(coefficient)||!Positive(length)||length>1e6||!Positive(velocity)||velocity<50||velocity>250||
                !Positive(sr)||sr<1e5||sr>1e9||count<16||!(max>min)||
                phaseHeader.Length>0&&!phaseHeader.Equals(phase,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("旧校准的系数、档位、波形或必要参数无效，不能导入。");
            if(identity!=LiveCalibrationRecord.Identity(project))throw new IOException("项目资料已变化，请重新选择旧校准。");
            return new LegacyCalibrationCandidate {SourceRelativePath=full.Substring(Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar).Length+1),SourceSha256=Hash(bytes),ObservedProjectHash=identity,
                SourcePhase=phase,Level=name.Groups[2].Value+(name.Groups[3].Value.Equals("nC",StringComparison.OrdinalIgnoreCase)?"nC":"pC"),CoefficientPcPerV=coefficient,ChargePc=charge,
                HistoricalLengthM=length,HistoricalVelocityMPerUs=velocity,SampleRateHz=sr,RecordedSampleRateHz=RecordedLegacySampleRate(header),WaveformSampleCount=count,
                HistoricalIncidentSeconds=LegacyTimeSeconds(header,"Start_point"),HistoricalReflectionSeconds=LegacyTimeSeconds(header,"Final_point"),
                HistoricalDateText=Value(header,"Date"),HistoricalTimeText=Value(header,"Time"),HistoricalFilterText=Value(header,"Filterstate","Filter state"),HistoricalAttenuationText=Value(header,"Calib_Attenuation")};
        }
        // Select the earlier parameters to offer for one explicit present-day
        // applicability decision. A readable candidate is not an approval for
        // the current devices, and this method never creates a binding.
        public static LegacyCalibrationCandidate FindCandidateForProject(string project,string phase)
        {
            if(!Phases.Contains(phase))return null;
            string identity=LiveCalibrationRecord.Identity(project);
            bool separate=LiveCalibrationRecord.RequiresSeparatePhases(project);
            if(separate&&phase=="ALL")return null;
            CurrentLengthForProject(project,phase);
            var files=new List<string>();
            foreach(string path in Directory.EnumerateFiles(Path.GetFullPath(project),"*.cal",SearchOption.TopDirectoryOnly)) {
                if(files.Count>=256)throw new InvalidDataException("项目内旧校准过多，请先整理文件。");
                files.Add(path);
            }
            LegacyCalibrationCandidate best=null;int bestRank=-1;
            foreach(string path in files.OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)) {
                try {
                    var item=ReadCandidate(project,path);
                    if(separate&&item.SourcePhase!=phase)continue;
                    int rank=item.SourcePhase==phase?2:item.SourcePhase=="ALL"?1:0;
                    if(best==null||rank>bestRank||rank==bestRank&&item.ChargePc>best.ChargePc) {
                        best=item;bestRank=rank;
                    }
                }catch(Exception error)when(error is IOException||error is InvalidDataException||error is InvalidOperationException||error is ArgumentException) { }
            }
            if(identity!=LiveCalibrationRecord.Identity(project))throw new IOException("项目资料已变化，请重新读取旧校准。");
            return best;
        }
        public static LegacyCalibrationBinding Confirm(string project,LegacyCalibrationCandidate candidate,string targetPhase,string picoSerial,string artSerial,bool firstArrival,bool operatorConfirmed,string acknowledgement)
        {
            if(!operatorConfirmed||String.IsNullOrWhiteSpace(acknowledgement)||acknowledgement.Length>4096||!SerialValid(picoSerial)||!SerialValid(artSerial)||candidate==null)
                throw new InvalidDataException("需要确认旧校准适用于当前设备和测量回路，且当前两台设备身份完整。");
            var current=ReadCandidate(project,candidate.SourceRelativePath);
            if(current.SourceSha256!=candidate.SourceSha256||current.ObservedProjectHash!=candidate.ObservedProjectHash||
                current.CoefficientPcPerV!=candidate.CoefficientPcPerV||current.SourcePhase!=candidate.SourcePhase||current.Level!=candidate.Level||
                current.SourceFormat!=candidate.SourceFormat||current.HistoricalDeviceSerial!=candidate.HistoricalDeviceSerial||current.HistoricalTimingMode!=candidate.HistoricalTimingMode||
                current.HistoricalLengthM!=candidate.HistoricalLengthM||current.HistoricalVelocityMPerUs!=candidate.HistoricalVelocityMPerUs)
                throw new IOException("旧校准或项目内容已变化，请重新查看并确认。");
            double length=CurrentLengthForProject(project,targetPhase);
            var binding=new LegacyCalibrationBinding {SourceRelativePath=current.SourceRelativePath,SourceSha256=current.SourceSha256,ProjectHash=current.ObservedProjectHash,
                SourcePhase=current.SourcePhase,TargetPhase=targetPhase,Level=current.Level,PicoSerial=picoSerial,ArtSerial=artSerial,FirstArrival=firstArrival,
                OperatorConfirmed=true,Acknowledgement=acknowledgement,ConfirmedUtc=DateTimeOffset.UtcNow.ToString("o",Inv),
                CoefficientPcPerV=current.CoefficientPcPerV,ChargePc=current.ChargePc,HistoricalLengthM=current.HistoricalLengthM,CurrentLengthM=length,
                HistoricalVelocityMPerUs=current.HistoricalVelocityMPerUs,SourceSampleRateHz=current.RecordedSampleRateHz,SourceWaveformSampleCount=current.WaveformSampleCount,
                HistoricalIncidentSeconds=current.HistoricalIncidentSeconds,HistoricalReflectionSeconds=current.HistoricalReflectionSeconds,
                HistoricalDateText=current.HistoricalDateText,HistoricalTimeText=current.HistoricalTimeText,HistoricalFilterText=current.HistoricalFilterText,HistoricalAttenuationText=current.HistoricalAttenuationText};
            binding.SourceFormat=current.SourceFormat;binding.HistoricalDeviceSerial=current.HistoricalDeviceSerial;binding.HistoricalTimingMode=current.HistoricalTimingMode;
            string relative=Path.Combine("Calibration","LegacyBindings",targetPhase+"_"+current.Level+"_"+Guid.NewGuid().ToString("N")+".hvlegacy");
            string path=LocalPath(project,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Immutable, append-only confirmation history; a later confirmation
            // creates a new file instead of overwriting the original decision.
            string temporary=path+".tmp";
            try {
                byte[] payload;using(var buffer=new MemoryStream()){new XmlSerializer(typeof(LegacyCalibrationBinding)).Serialize(buffer,binding);payload=buffer.ToArray();}
                if(payload.Length>MaxBindingBytes-44)throw new InvalidDataException("校准确认信息过长。");
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(stream)) {
                    writer.Write(Encoding.ASCII.GetBytes("HVCLGCY1"));writer.Write(payload.Length);using(var sha=SHA256.Create())writer.Write(sha.ComputeHash(payload));writer.Write(payload);writer.Flush();stream.Flush(true);
                }
                LoadAndValidate(project,temporary,picoSerial,artSerial,firstArrival);
                File.Move(temporary,path);File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.ReadOnly);
                binding.BindingPath=path;return binding;
            }finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static LegacyCalibrationBinding LoadAndValidate(string project,string path,string picoSerial,string artSerial,bool firstArrival)
        {
            string full=LocalPath(project,path);byte[] bytes=ReadBounded(full,MaxBindingBytes);LegacyCalibrationBinding b;
            using(var reader=new BinaryReader(new MemoryStream(bytes))) {
                if(bytes.Length<44||Encoding.ASCII.GetString(reader.ReadBytes(8))!="HVCLGCY1")throw new InvalidDataException("不是有效的旧校准确认记录。");
                int length=reader.ReadInt32();if(length!=bytes.Length-44||length<=0)throw new InvalidDataException("旧校准确认记录不完整。");
                byte[] expected=reader.ReadBytes(32),payload=reader.ReadBytes(length);using(var sha=SHA256.Create())if(!expected.SequenceEqual(sha.ComputeHash(payload)))throw new InvalidDataException("旧校准确认记录校验失败。");
                using(var xml=XmlReader.Create(new MemoryStream(payload),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxBindingBytes}))
                    b=(LegacyCalibrationBinding)new XmlSerializer(typeof(LegacyCalibrationBinding)).Deserialize(xml);
            }
            DateTimeOffset confirmed;
            if(b==null||b.Version!=1||!b.OperatorConfirmed||String.IsNullOrWhiteSpace(b.Acknowledgement)||b.Acknowledgement.Length>4096||
                b.HistoricalDeviceIdentity!="Unknown"||b.QualityStatus!="NotReassessed"||!SerialValid(picoSerial)||!SerialValid(artSerial)||
                b.PicoSerial!=picoSerial||b.ArtSerial!=artSerial||b.FirstArrival!=firstArrival||!Phases.Contains(b.TargetPhase)||
                !DateTimeOffset.TryParse(b.ConfirmedUtc,Inv,DateTimeStyles.RoundtripKind,out confirmed)||confirmed==default(DateTimeOffset)||confirmed>DateTimeOffset.UtcNow)
                throw new InvalidDataException("旧校准确认不适用于当前设备或计时设置，请重新核对。");
            var current=ReadCandidate(project,b.SourceRelativePath);
            if(current.SourceSha256!=b.SourceSha256||current.ObservedProjectHash!=b.ProjectHash||current.SourcePhase!=b.SourcePhase||current.Level!=b.Level||
                current.CoefficientPcPerV!=b.CoefficientPcPerV||current.ChargePc!=b.ChargePc||current.HistoricalLengthM!=b.HistoricalLengthM||
                current.HistoricalVelocityMPerUs!=b.HistoricalVelocityMPerUs||current.RecordedSampleRateHz!=b.SourceSampleRateHz||current.WaveformSampleCount!=b.SourceWaveformSampleCount||
                !current.HistoricalIncidentSeconds.Equals(b.HistoricalIncidentSeconds)||!current.HistoricalReflectionSeconds.Equals(b.HistoricalReflectionSeconds)||
                current.HistoricalDateText!=b.HistoricalDateText||current.HistoricalTimeText!=b.HistoricalTimeText||current.HistoricalFilterText!=b.HistoricalFilterText||current.HistoricalAttenuationText!=b.HistoricalAttenuationText||
                current.SourceFormat!=b.SourceFormat||current.HistoricalDeviceSerial!=b.HistoricalDeviceSerial||current.HistoricalTimingMode!=b.HistoricalTimingMode||
                CurrentLengthForProject(project,b.TargetPhase)!=b.CurrentLengthM)
                throw new InvalidDataException("旧校准原件或项目资料已改变，本次适用确认已失效。");
            b.BindingPath=full;return b;
        }
        public static LegacyCalibrationBinding FindForProject(string project,string phase,bool firstArrival,string picoSerial,string artSerial)
        {
            if(!Phases.Contains(phase)||!SerialValid(picoSerial)||!SerialValid(artSerial))return null;
            string directory=LocalPath(project,Path.Combine("Calibration","LegacyBindings"));if(!Directory.Exists(directory))return null;
            bool separate=LiveCalibrationRecord.RequiresSeparatePhases(project);if(separate&&phase=="ALL")return null;
            LegacyCalibrationBinding best=null;int rank=-1;int count=0;
            foreach(string file in Directory.EnumerateFiles(directory,"*.hvlegacy")) {
                if(++count>2000)throw new InvalidDataException("旧校准确认记录过多，请先整理项目记录。");
                try {
                    var item=LoadAndValidate(project,file,picoSerial,artSerial,firstArrival);if(separate&&item.TargetPhase!=phase)continue;
                    int value=item.TargetPhase==phase?2:item.TargetPhase=="ALL"?1:0;
                    if(best==null||value>rank||value==rank&&(item.ChargePc>best.ChargePc||item.ChargePc==best.ChargePc&&
                        String.CompareOrdinal(item.ConfirmedUtc,best.ConfirmedUtc)>0)){best=item;rank=value;}
                }catch(Exception e)when(e is IOException||e is InvalidDataException||e is InvalidOperationException||e is ArgumentException){}
            }
            return best;
        }
    }
}
