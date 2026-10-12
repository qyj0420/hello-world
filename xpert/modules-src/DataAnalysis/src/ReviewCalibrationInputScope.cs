using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using HVX.CalibrationStorage;
using LabVIEWPdMap.ProjectCalibrationImplementation;

namespace HVX.PdReview
{
    // The frozen A/B entry points accept only a project path for CAL input.
    // This per-call projection preserves samples/times and supplies a separately
    // validated local packet identity. It does not grant quality or acceptance.
    internal sealed class ReviewCalibrationInputScope : IDisposable
    {
        readonly List<string> ownedFiles=new List<string>();
        readonly Dictionary<string,string> sources=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> outputs=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly List<Dictionary<string,object>> records=new List<Dictionary<string,object>>();
        string ownedRoot;
        internal string AlgorithmProjectPath { get; private set; }
        internal bool IsProjected { get { return ownedRoot!=null; } }
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        const long MaximumCopiedBytes=128L*1024*1024;
        long copiedBytes;

        internal static ReviewCalibrationInputScope Create(string projectPath,string locationPath)
        {
            var scope=new ReviewCalibrationInputScope {AlgorithmProjectPath=projectPath};
            try { scope.Prepare(projectPath,locationPath);return scope; }
            catch { scope.Dispose();throw; }
        }
        static string Phase(string token)
        {
            switch(token.ToUpperInvariant()) {case "A":case "L1":return "A";case "B":case "L2":return "B";case "C":case "L3":return "C";
                case "ALL":case "COMMON":return "ALL";default:return "";}
        }
        void Prepare(string projectPath,string locationPath)
        {
            string root;List<Candidate> catalog;
            try { catalog=Selector.ReadCatalog(projectPath,out root); }
            catch(SelectionFailure error) {if(error.Code=="NoCalibrationFiles")return;throw;}
            string phase=Phase(Path.GetFileNameWithoutExtension(locationPath).Split('_')[0]);
            if(phase.Length==0)throw new InvalidDataException("定位文件相别无法核验，未应用校准参考。");
            // B deliberately retains its original parser/gates, but that must
            // not mean a present broken local input contract can be ignored.
            // Include invalid catalog entries too: malformed CAL headers cannot
            // hide their sidecar by dropping out of the valid selection list.
            foreach(Candidate c in catalog.Where(c=>Phase(Path.GetFileNameWithoutExtension(c.Path).Split('_')[0])==phase||Phase(Path.GetFileNameWithoutExtension(c.Path).Split('_')[0])=="ALL")) {
                if(!CalibrationPacketIdentityContract.HasSidecar(c.Path))continue;
                sources[c.Path]=LiveCalibrationRecord.HashFile(c.Path);
                string metadata=CalibrationPacketIdentityContract.SidecarPath(c.Path);sources[metadata]=LiveCalibrationRecord.HashFile(metadata);
                CalibrationPacketIdentityContract.ReadOptionalFile(c.Path);
            }
            VerifySources();
            var eligible=catalog.Where(c=>c.Valid&&(c.Phase==phase||c.Phase=="ALL")).ToArray();
            if(!eligible.Any(c=>c.IsLiveArchive))return; // Legacy-only calls retain their original path and policy.
            string parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"HVXAnalysisCalibrationInputs"));
            CheckUnlinked(parent);Directory.CreateDirectory(parent);CheckUnlinked(parent);
            string target=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
            if(!String.Equals(Path.GetDirectoryName(target),parent,StringComparison.OrdinalIgnoreCase)||Directory.Exists(target)||File.Exists(target))
                throw new IOException("无法创建独立校准输入目录。");
            Directory.CreateDirectory(target);CheckUnlinked(target);ownedRoot=target;AlgorithmProjectPath=target;
            foreach(string name in new[]{"ITEM.in","CABLE.in"}) {
                string path=Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly).SingleOrDefault(p=>Path.GetFileName(p).Equals(name,StringComparison.OrdinalIgnoreCase));
                if(path==null)throw new FileNotFoundException("项目文件缺失。",Path.Combine(root,name));Copy(path,name);
            }
            CopyMatchedDem(root,locationPath);
            foreach(Candidate c in eligible) {
                CheckUnlinked(c.Path);string hash=LiveCalibrationRecord.HashFile(c.Path);
                var detail=new Dictionary<string,object>{{"sourcePath",c.Path},{"sourceSha256",hash},{"phase",c.Phase},{"chargePc",c.Value},
                    {"sampleRateHz",c.Rate},{"sampleCount",c.Wave.Length},{"rawUnits","V"},{"amplitudeScaled",false},{"resampled",false}};
                // The existing archive helper returns base64. Retain that old
                // provenance field for compatibility and label its encoding;
                // the new independent contract uses canonical hexadecimal SHA.
                detail["sourceHashEncoding"]="Base64_SHA256";detail["sourceFileSha256"]=CalibrationPacketIdentityContract.HashFile(c.Path);
                records.Add(detail);sources[c.Path]=hash;
                if(!c.IsLiveArchive) {
                    string legacyName=Path.GetFileNameWithoutExtension(c.Path)+".cal";Copy(c.Path,legacyName);
                    if(CalibrationPacketIdentityContract.HasSidecar(c.Path))Copy(CalibrationPacketIdentityContract.SidecarPath(c.Path),legacyName+".packets.xml");
                    detail["adaptation"]="legacy_byte_copy";continue;
                }
                LiveCalibrationRecord live=LiveCalibrationRecord.Load(root,c.Path,true);
                detail["sampleRateHz"]=live.SampleRateHz;detail["sampleCount"]=live.Average.Length;
                detail["captureRangeIndex"]=live.RangeIndex;detail["timingMode"]=live.FirstArrival?"FirstArrival":"Peak";
                detail["incidentSeconds"]=live.IncidentSeconds;detail["reflectionSeconds"]=Selector.Number(live.ReflectionSeconds);
                detail["hasMeasuredReflection"]=live.HasMeasuredReflection;detail["qualityWarning"]=live.QualityWarning??"";
                if(live.RangeIndex<0||live.RangeIndex>10)throw new InvalidDataException("校准采集档位无法核验。");
                if(!live.HasMeasuredReflection||live.HasIncidentQualityWarning) {
                    detail["adaptation"]="not_projected";detail["reason"]=!live.HasMeasuredReflection?"no_measured_reflection":"incident_quality_requires_review";continue;
                }
                if(!Selector.Finite(live.IncidentSeconds)||!Selector.Finite(live.ReflectionSeconds)||live.IncidentSeconds<0||
                    live.ReflectionSeconds<=live.IncidentSeconds||live.ReflectionSeconds*live.SampleRateHz>=live.Average.Length)
                    throw new InvalidDataException("校准计时证据无效，未生成参考反射。");
                string file=Own(Path.GetFileNameWithoutExtension(c.Path)+".cal");
                using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false))) {
                    string[] header={"Date\t"+c.Time.ToString("yyyy-MM-dd",Inv),"Time\t"+c.Time.ToString("HH:mm:ss",Inv),
                        "Cable length\t"+N(live.LengthM),"Cal.value\t"+N(live.ChargePc),"Velocity (m/us)\t"+N(live.VelocityMPerUs),
                        "NI_Calib_factor\t"+N(live.CoefficientPcPerV),"Start_point (s)\t"+N(live.IncidentSeconds),
                        "Final_point (s)\t"+N(live.ReflectionSeconds),"SR\t"+N(live.SampleRateHz),"Calib_Attenuation\t"+N(live.AttenuationDb),
                        "Phase\t"+live.Phase,"Units\tV","Data"};
                    foreach(string line in header)writer.WriteLine(line);
                    foreach(double value in live.Average)writer.WriteLine(N(value));
                }
                var identity=new CalibrationPacketIdentityContract {ProjectedCalSha256=CalibrationPacketIdentityContract.HashFile(file),SourceCalSha256=(string)detail["sourceFileSha256"],
                    SampleSha256=CalibrationPacketIdentityContract.HashSamples(live.Average),Phase=live.Phase,ChargePc=live.ChargePc,RangeIndex=live.RangeIndex,
                    SampleRateHz=live.SampleRateHz,SampleCount=live.Average.Length,CoefficientPcPerV=live.CoefficientPcPerV,
                    IncidentSeconds=live.IncidentSeconds,ReflectionSeconds=live.ReflectionSeconds,IncidentPeakIndex=live.IncidentPeakIndex,
                    ReflectionPeakIndex=live.ReflectionPeakIndex,TimingMode=live.FirstArrival?"FirstArrival":"Peak"};
                string sidecar=Own(Path.GetFileName(file)+".packets.xml");identity.WriteNew(file);
                outputs[file]=identity.ProjectedCalSha256;outputs[sidecar]=CalibrationPacketIdentityContract.HashFile(sidecar);
                detail["adaptation"]="HVCLIVE_13_header_CAL_with_local_packet_identity";detail["projectedPath"]=file;
                detail["packetIdentitySchema"]=CalibrationPacketIdentityContract.Schema;detail["packetIdentityPath"]=sidecar;
                detail["projectedCalSha256"]=identity.ProjectedCalSha256;detail["packetIdentitySha256"]=outputs[sidecar];
                detail["sampleSha256"]=identity.SampleSha256;detail["incidentPeakIndex"]=identity.IncidentPeakIndex;detail["reflectionPeakIndex"]=identity.ReflectionPeakIndex;
                detail["timingRetained"]=true;detail["packetIdentityDoesNotGrantQualityOrAcceptance"]=true;
                copiedBytes+=new FileInfo(file).Length;if(copiedBytes>MaximumCopiedBytes)throw new InvalidDataException("校准兼容输入超过受控大小上限。");
            }
            VerifySources();
            string manifest=Own("calibration-input-source.json");
            File.WriteAllText(manifest,new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Serialize(Evidence()),new UTF8Encoding(false));
            outputs[manifest]=CalibrationPacketIdentityContract.HashFile(manifest);
        }
        static string N(double value){return value.ToString("R",Inv);}
        static void CheckUnlinked(string path)
        {
            string full=Path.GetFullPath(path);
            for(string current=full;!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
                if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("不使用链接目录生成校准兼容输入。");
        }
        string Own(string name)
        {
            CheckUnlinked(ownedRoot);string path=Path.GetFullPath(Path.Combine(ownedRoot,name));
            if(Path.GetFileName(name)!=name||!String.Equals(Path.GetDirectoryName(path),ownedRoot,StringComparison.OrdinalIgnoreCase))
                throw new IOException("校准输入目标超出独立目录。");
            ownedFiles.Add(path);return path;
        }
        void Copy(string source,string name)
        {
            CheckUnlinked(source);long length=new FileInfo(source).Length;copiedBytes+=length;
            if(copiedBytes>MaximumCopiedBytes)throw new InvalidDataException("项目兼容输入超过受控大小上限。");
            string hash=LiveCalibrationRecord.HashFile(source);sources[source]=hash;
            string path=Own(name);
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
            outputs[path]=CalibrationPacketIdentityContract.HashFile(path);
        }
        void CopyMatchedDem(string root,string locationPath)
        {
            string name=Path.GetFileNameWithoutExtension(locationPath)+".dem";
            string[] files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly);
            string exact=files.FirstOrDefault(p=>Path.GetFileName(p).Equals(name,StringComparison.OrdinalIgnoreCase));
            if(exact!=null){Copy(exact,Path.GetFileName(exact));return;}
            DateTime when;if(!MeasurementTime(locationPath,out when))return;
            string[] parts=Path.GetFileNameWithoutExtension(locationPath).Split('_');double voltage;
            if(parts.Length<2||!Double.TryParse(parts[1].Replace("kV",""),NumberStyles.Float,Inv,out voltage))return;
            var matches=files.Where(p=>Path.GetExtension(p).Equals(".dem",StringComparison.OrdinalIgnoreCase)).Select(p=>{
                string[] tokens=Path.GetFileNameWithoutExtension(p).Split('_');DateTime stamp=default(DateTime);double value;
                bool valid=tokens.Length>1&&Phase(tokens[0])==Phase(parts[0])&&Double.TryParse(tokens[1].Replace("kV",""),NumberStyles.Float,Inv,out value)&&
                    Math.Abs(value-voltage)<1e-6&&MeasurementTime(p,out stamp);
                return new {Path=p,Distance=valid?Math.Abs((stamp-when).TotalSeconds):Double.PositiveInfinity};
            }).Where(p=>p.Distance<=1).OrderBy(p=>p.Distance).ThenBy(p=>p.Path,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if(matches!=null)Copy(matches.Path,Path.GetFileName(matches.Path));
        }
        static bool MeasurementTime(string path,out DateTime time)
        {
            string name=Path.GetFileNameWithoutExtension(path);Match m=Regex.Match(name,@"(20\d{12})$");
            if(m.Success&&DateTime.TryParseExact(m.Value,"yyyyMMddHHmmss",Inv,DateTimeStyles.None,out time))return true;
            m=Regex.Match(name,@"(20\d{2})[-_](\d{2})[-_](\d{2})[-_](\d{2})[':_-](\d{2})[':_-](\d{2})$");
            time=default(DateTime);if(!m.Success)return false;
            try {time=new DateTime(Int32.Parse(m.Groups[1].Value),Int32.Parse(m.Groups[2].Value),Int32.Parse(m.Groups[3].Value),Int32.Parse(m.Groups[4].Value),Int32.Parse(m.Groups[5].Value),Int32.Parse(m.Groups[6].Value));return true;}
            catch(ArgumentOutOfRangeException){return false;}
        }
        Dictionary<string,object> Evidence()
        {return new Dictionary<string,object>{{"schema","HVX.CalibrationInputProjection/1"},{"storageOnly",false},
            {"localPacketContract",CalibrationPacketIdentityContract.Schema},{"timingPolicy","saved_time_unchanged;packet_identity_for_amplitude_only"},
            {"qualityPolicy","original_packet_separation_clipping_quiet_SNR_and_correlation_gates_unchanged"},
            {"methodBPolicy","original_same_packet_rejection_unchanged"},{"originalLocRetained",true},
            {"captureRangePolicy","validated_saved_CAL_range;LOC_range_not_declared"},{"records",records}};}
        internal void VerifySources()
        {
            foreach(var source in sources) {CheckUnlinked(source.Key);if(!String.Equals(LiveCalibrationRecord.HashFile(source.Key),source.Value,StringComparison.Ordinal))throw new IOException("分析期间校准或项目信息已变化，请重新分析。");}
            foreach(var output in outputs)if(!String.Equals(CalibrationPacketIdentityContract.HashFile(output.Key),output.Value,StringComparison.Ordinal))throw new IOException("分析期间独立校准输入已变化，请重新分析。");
        }
        internal string AppendStatus(string status)
        {
            if(!IsProjected)return status;
            var json=ReviewStatusJson.DeserializeObject(status) as Dictionary<string,object>;
            if(json==null)throw new InvalidDataException("算法缺少可追溯的状态数据。");
            json["calibration_input_adapter"]=Evidence();return ReviewStatusJson.Serialize(json);
        }
        public void Dispose()
        {
            if(ownedRoot==null)return;
            try {
                CheckUnlinked(ownedRoot);
                foreach(string file in ownedFiles) {
                    if(!String.Equals(Path.GetDirectoryName(file),ownedRoot,StringComparison.OrdinalIgnoreCase))throw new IOException("拒绝清理越界目录。");
                    CheckUnlinked(file);if(File.Exists(file))File.Delete(file);
                }
                Directory.Delete(ownedRoot,false);
            }
            catch(Exception error) when(error is IOException||error is UnauthorizedAccessException||error is ArgumentException)
            {
                try {using(var journal=new ReviewSessionJournal()){journal.Append("CalibrationInputCleanupFailed","RetainedScratch="+ownedRoot+"; "+error.GetType().Name+": "+error.Message);journal.Flush();}}
                catch(Exception logError) {if(logError is OutOfMemoryException||logError is StackOverflowException)throw;}
            }
            ownedRoot=null;
        }
    }
}
