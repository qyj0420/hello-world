using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // File-relative keys retain the engine's CURRENT-call row indices. They
        // are never interpreted as global merged matrix indices.
        Dictionary<string,string> algorithmDiagnostics=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        // Per-segment / legacy flat encoded quota (also the spill trigger).
        const int CandidateEvidenceLimit=ReviewAnalysisMetadataBudget.EncodedDiagnosticsBytes;
        // Old single-file archives may still carry a flat payload up to the
        // session.json ceiling; keep that readable without forcing a rewrite.
        const int CandidateLegacyFlatBytes=ReviewAnalysisMetadataBudget.SessionMetadataBytes;
        const int CandidateStatusCharacterLimit=1024*1024;
        // Decoded working-set ceiling across all segments (matches ledger work budget).
        const int CandidateDecodedBytesLimit=ReviewAnalysisMetadataBudget.SegmentedDiagnosticsTotalBytes;
        // A finite cumulative validation-work budget, NOT a resident decoded
        // collection. ValidateLedgerStatus expands one <=16 MiB ledger at a
        // time; only its compressed status and scalar byte count survive.
        const int CandidateLedgerValidationBytesLimit=ReviewAnalysisMetadataBudget.SegmentedDiagnosticsTotalBytes;
        const string CompressedEvidencePrefix="HVX.Diagnostics.Gzip.v1:";
        const string SegmentedDiagnosticsSchema="HVX.AlgorithmDiagnostics.Segmented/1";
        static readonly Encoding EvidenceUtf8=new UTF8Encoding(false,true);
        // Content-only cache for this control's current metadata snapshot. A
        // status is immutable and must match in full before its ledger proof or
        // lossless encoding is reused. This never caches source-file validation.
        sealed class EncodedCandidateStatus
        {
            internal readonly string Source,Encoded;
            internal readonly int DecodedBytes,LedgerBytes;
            internal EncodedCandidateStatus(string source,string encoded,int decodedBytes,int ledgerBytes)
            {Source=source;Encoded=encoded;DecodedBytes=decodedBytes;LedgerBytes=ledgerBytes;}
        }
        Dictionary<string,EncodedCandidateStatus> encodedCandidateStatuses;
        string encodedCandidateProject;
        // Archive spill payloads produced by the latest instance serialize when
        // AlgorithmDiagnosticsJson is a file-referenced segmented envelope.
        List<KeyValuePair<string,byte[]>> pendingDiagnosticsArchiveParts=new List<KeyValuePair<string,byte[]>>();
        static JavaScriptSerializer CandidateSerializer(int maxJsonLength)
        {return new JavaScriptSerializer{MaxJsonLength=maxJsonLength,RecursionLimit=96};}
        static JavaScriptSerializer CandidateSerializer(){return CandidateSerializer(CandidateLegacyFlatBytes);}
        [Browsable(false)] public string AlgorithmDiagnosticsJson { get { return SerializeCurrentAlgorithmDiagnostics(algorithmDiagnostics); } }
        [Browsable(false)] public string AlgorithmReviewNotice
        {
            get {
                if(selected<0||selected>=SourceRowCount)return "";
                int method=Finite(working[selected,26])?(int)working[selected,26]:-1;
                if(method==9)return L("E+ raw-peak research proposals; matching score is not positioning accuracy. Excluded candidates remain available for review.","E+ 原始峰值研究建议；匹配评分不是定位准确率。被排除候选保留待复核。");
                if(method==8)return L("E raw-peak research proposals; quality is a transformed matching ratio, not accuracy. Excluded candidates remain in the ledger for review.","E 原始峰值研究建议；质量是匹配比值变换，不是准确率。被排除候选保留在账本中待复核。");
                if(method==7)return L("Verify the reflection against the waveform. Statistical inclusion and quality scores do not confirm a physical location.",
                    "请结合波形复核反射来源；纳入统计及质量分数不代表位置已确认。");
                if(method==5)return confidenceSources[selected]!=0?
                    L("Manual timing score is a local estimate; it does not repeat the complete automatic waveform checks.","人工计时分数为局部估计，未重新完成整条波形的自动检查。"): 
                    L("Review the original waveforms. A quality score is not positioning accuracy.","请结合原始波形复核；质量分数不代表定位准确率。");
                return "";
            }
        }
        static string SerializeAlgorithmDiagnostics(Dictionary<string,string> values)
        {
            Dictionary<string,EncodedCandidateStatus> ignored;
            List<KeyValuePair<string,byte[]>> ignoredParts;
            return SerializeAlgorithmDiagnosticsCore(values,null,true,out ignored,out ignoredParts);
        }
        string SerializeCurrentAlgorithmDiagnostics(Dictionary<string,string> values)
        {
            string project=projectPath??"";
            var prior=String.Equals(encodedCandidateProject,project,StringComparison.OrdinalIgnoreCase)?encodedCandidateStatuses:null;
            Dictionary<string,EncodedCandidateStatus> next;
            List<KeyValuePair<string,byte[]>> parts;
            string json=SerializeAlgorithmDiagnosticsCore(values,prior,false,out next,out parts);
            // Publish only a completely validated, bounded snapshot. Removed
            // records are not retained, and a failed operation leaves it intact.
            encodedCandidateStatuses=next;encodedCandidateProject=project;
            pendingDiagnosticsArchiveParts=parts??new List<KeyValuePair<string,byte[]>>();
            return json;
        }
        internal List<KeyValuePair<string,byte[]>> TakePendingDiagnosticsArchiveParts()
        {
            var parts=pendingDiagnosticsArchiveParts??new List<KeyValuePair<string,byte[]>>();
            pendingDiagnosticsArchiveParts=new List<KeyValuePair<string,byte[]>>();
            return parts;
        }
        internal static bool IsSegmentedDiagnosticsEnvelope(string json)
        {
            if(String.IsNullOrEmpty(json))return false;
            string trim=json.TrimStart();
            if(trim.Length==0||trim[0]!='{')return false;
            return trim.IndexOf(SegmentedDiagnosticsSchema,StringComparison.Ordinal)>=0;
        }
        internal static string DiagnosticsPartFileName(int oneBasedIndex)
        {
            if(oneBasedIndex<1||oneBasedIndex>ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments)
                throw new ArgumentOutOfRangeException("oneBasedIndex");
            return "diagnostics-part-"+oneBasedIndex.ToString("D3",CultureInfo.InvariantCulture)+".json";
        }
        internal static bool IsDiagnosticsPartFileName(string relative)
        {
            if(String.IsNullOrEmpty(relative)||relative.IndexOf('/')>=0||relative.IndexOf('\\')>=0)return false;
            if(!relative.StartsWith("diagnostics-part-",StringComparison.OrdinalIgnoreCase)||
                !relative.EndsWith(".json",StringComparison.OrdinalIgnoreCase))return false;
            string middle=relative.Substring("diagnostics-part-".Length,relative.Length-"diagnostics-part-".Length-".json".Length);
            int index;return middle.Length==3&&Int32.TryParse(middle,NumberStyles.None,CultureInfo.InvariantCulture,out index)&&
                index>=1&&index<=ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments;
        }
        static EncodedCandidateStatus EncodeCandidateStatus(string status)
        {
            int ledgerBytes=ReviewEAnalysis.ValidateLedgerStatus(status);
            byte[] bytes=EvidenceUtf8.GetBytes(status);string encoded=status;
            if(bytes.Length>4096||status.StartsWith(CompressedEvidencePrefix,StringComparison.Ordinal)){
                using(var output=new MemoryStream()){
                    using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))gzip.Write(bytes,0,bytes.Length);
                    string compressed=CompressedEvidencePrefix+Convert.ToBase64String(output.ToArray());
                    if(compressed.Length<status.Length||status.StartsWith(CompressedEvidencePrefix,StringComparison.Ordinal))encoded=compressed;
                }
            }
            return new EncodedCandidateStatus(status,encoded,bytes.Length,ledgerBytes);
        }
        static string SerializeAlgorithmDiagnosticsCore(Dictionary<string,string> values,Dictionary<string,EncodedCandidateStatus> prior,
            bool embedInlineParts,out Dictionary<string,EncodedCandidateStatus> next,out List<KeyValuePair<string,byte[]>> archiveParts)
        {
            // Full per-file evidence is retained in memory and restored losslessly.
            // Only the session representation is compressed; never discard rows or
            // source-identity warnings to satisfy the metadata quota. When the
            // encoded map crosses ~32 MiB, spill into ordered segment parts.
            var stored=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var validated=new Dictionary<string,EncodedCandidateStatus>(StringComparer.OrdinalIgnoreCase);
            var byContent=new Dictionary<string,EncodedCandidateStatus>(StringComparer.Ordinal);
            int decodedBytes=0,eLedgerBytes=0;
            foreach(var entry in values){
                string status=entry.Value;
                if(status==null||status.Length>CandidateStatusCharacterLimit)throw new InvalidOperationException("Invalid algorithm status size.");
                EncodedCandidateStatus evidence;
                if(prior==null||!prior.TryGetValue(entry.Key,out evidence)||!String.Equals(evidence.Source,status,StringComparison.Ordinal)){
                    if(!byContent.TryGetValue(status,out evidence))evidence=EncodeCandidateStatus(status);
                }
                byContent[status]=evidence;validated.Add(entry.Key,evidence);
                eLedgerBytes=AddCandidateLedgerBytes(eLedgerBytes,evidence.LedgerBytes);
                decodedBytes=checked(decodedBytes+evidence.DecodedBytes);
                if(decodedBytes>CandidateDecodedBytesLimit)throw new InvalidOperationException("Algorithm diagnostics exceed the decoded session budget; split the analysis batch.");
                stored.Add(entry.Key,evidence.Encoded);
            }
            var segments=PackDiagnosticsSegments(stored);
            if(segments.Count==1){
                string json=segments[0];
                if(EvidenceUtf8.GetByteCount(json)>CandidateEvidenceLimit)throw new InvalidOperationException("Algorithm diagnostics exceed the bounded 32 MiB segment budget; split the analysis batch.");
                next=validated;archiveParts=new List<KeyValuePair<string,byte[]>>();return json;
            }
            if(segments.Count>ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments)
                throw new InvalidOperationException("Algorithm diagnostics exceed the maximum number of 32 MiB segments; split the analysis batch.");
            long totalEncoded=0;var parts=new List<KeyValuePair<string,byte[]>>(segments.Count);
            var partNames=new List<string>(segments.Count);
            var inline=embedInlineParts?new List<string>(segments.Count):null;
            for(int i=0;i<segments.Count;i++){
                string body=segments[i];
                int bytes=EvidenceUtf8.GetByteCount(body);
                if(bytes>ReviewAnalysisMetadataBudget.SegmentTriggerBytes)
                    throw new InvalidOperationException("Algorithm diagnostics segment exceeds the 32 MiB spill threshold.");
                totalEncoded+=bytes;
                if(totalEncoded>ReviewAnalysisMetadataBudget.SegmentedDiagnosticsTotalBytes)
                    throw new InvalidOperationException("Algorithm diagnostics exceed the segmented 512 MiB archive budget; split the analysis batch.");
                string name=DiagnosticsPartFileName(i+1);partNames.Add(name);
                parts.Add(new KeyValuePair<string,byte[]>(name,EvidenceUtf8.GetBytes(body)));
                if(inline!=null)inline.Add(body);
            }
            var envelope=new Dictionary<string,object>{
                {"schema",SegmentedDiagnosticsSchema},{"version",1},
                {"parts",partNames.ToArray()},{"recordCount",stored.Count},{"encodedBytes",totalEncoded}
            };
            if(inline!=null)envelope["inlineParts"]=inline.ToArray();
            string envelopeJson=CandidateSerializer(256*1024).Serialize(envelope);
            next=validated;archiveParts=embedInlineParts?new List<KeyValuePair<string,byte[]>>():parts;return envelopeJson;
        }
        static List<string> PackDiagnosticsSegments(Dictionary<string,string> stored)
        {
            // Estimate-then-flush packing: never reserialize the growing map on
            // every key (O(n²) over ~30 MiB payloads freezes the UI on 462-LOC
            // location batches such as project 11-12).
            var segments=new List<string>();
            var current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var serializer=CandidateSerializer(CandidateLegacyFlatBytes);
            const long softTrigger=(long)(ReviewAnalysisMetadataBudget.SegmentTriggerBytes*0.92);
            long estimated=2;
            foreach(var entry in stored){
                long entryBytes=8L+EvidenceUtf8.GetByteCount(entry.Key)+EvidenceUtf8.GetByteCount(entry.Value);
                if(current.Count>0&&estimated+entryBytes>softTrigger){
                    string flushed=serializer.Serialize(current);
                    if(EvidenceUtf8.GetByteCount(flushed)>ReviewAnalysisMetadataBudget.SegmentTriggerBytes)
                        throw new InvalidOperationException("Algorithm diagnostics segment exceeds the 32 MiB spill threshold.");
                    segments.Add(flushed);
                    current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    estimated=2;
                }
                current.Add(entry.Key,entry.Value);
                estimated+=entryBytes;
                if(current.Count==1){
                    string alone=serializer.Serialize(current);
                    if(EvidenceUtf8.GetByteCount(alone)>ReviewAnalysisMetadataBudget.SegmentTriggerBytes)
                        throw new InvalidOperationException("A single algorithm diagnostics record exceeds the 32 MiB segment budget.");
                    estimated=EvidenceUtf8.GetByteCount(alone);
                }
            }
            if(current.Count>0){
                string last=serializer.Serialize(current);
                if(EvidenceUtf8.GetByteCount(last)>ReviewAnalysisMetadataBudget.SegmentTriggerBytes)
                    throw new InvalidOperationException("Algorithm diagnostics segment exceeds the 32 MiB spill threshold.");
                segments.Add(last);
            }
            if(segments.Count==0)segments.Add("{}");
            return segments;
        }
        static Dictionary<string,string> ParseAlgorithmDiagnostics(string json,string root,string[] retainedPaths)
        {return ParseAlgorithmDiagnostics(json,root,retainedPaths,null,null);}
        static Dictionary<string,string> ParseAlgorithmDiagnostics(string json,string root,string[] retainedPaths,string archiveDirectory,Action<int,int,string> progress)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(String.IsNullOrEmpty(json))return result;
            if(IsSegmentedDiagnosticsEnvelope(json))
                return ParseSegmentedAlgorithmDiagnostics(json,root,retainedPaths,archiveDirectory,progress);
            // Legacy flat map: allow up to the historical session.json ceiling.
            if(json.Length>CandidateLegacyFlatBytes||EvidenceUtf8.GetByteCount(json)>CandidateLegacyFlatBytes)
                throw new ArgumentException("Algorithm diagnostics exceed the bounded 64 MiB legacy session budget.");
            int decodedBytes=0,eLedgerBytes=0;
            return ParseFlatAlgorithmDiagnostics(json,root,retainedPaths,CandidateLegacyFlatBytes,ref decodedBytes,ref eLedgerBytes);
        }
        static Dictionary<string,string> ParseSegmentedAlgorithmDiagnostics(string json,string root,string[] retainedPaths,
            string archiveDirectory,Action<int,int,string> progress)
        {
            if(json.Length>256*1024||EvidenceUtf8.GetByteCount(json)>256*1024)
                throw new ArgumentException("Segmented algorithm diagnostics envelope exceeds its size limit.");
            var envelope=CandidateSerializer(256*1024).DeserializeObject(json) as Dictionary<string,object>;
            if(envelope==null)throw new ArgumentException("Invalid segmented algorithm diagnostics.");
            object schema;if(!envelope.TryGetValue("schema",out schema)||!String.Equals(schema as string,SegmentedDiagnosticsSchema,StringComparison.Ordinal))
                throw new ArgumentException("Unsupported segmented algorithm diagnostics schema.");
            object version;int versionNumber;
            if(!envelope.TryGetValue("version",out version)||
                !Int32.TryParse(Convert.ToString(version,CultureInfo.InvariantCulture),NumberStyles.Integer,CultureInfo.InvariantCulture,out versionNumber)||
                versionNumber!=1)
                throw new ArgumentException("Unsupported segmented algorithm diagnostics version.");
            var inline=envelope.ContainsKey("inlineParts")?envelope["inlineParts"] as object[]:null;
            var parts=envelope.ContainsKey("parts")?envelope["parts"] as object[]:null;
            int segmentCount=inline!=null?inline.Length:(parts!=null?parts.Length:0);
            if(segmentCount<1||segmentCount>ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments)
                throw new ArgumentException("Invalid segmented algorithm diagnostics part count.");
            if(inline!=null&&parts!=null&&inline.Length!=parts.Length)
                throw new ArgumentException("Segmented algorithm diagnostics part lists disagree.");
            if(inline==null&&(String.IsNullOrEmpty(archiveDirectory)||parts==null))
                throw new ArgumentException("Segmented algorithm diagnostics require archive part files or inline payloads.");
            var merged=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            int decodedBytes=0,eLedgerBytes=0;long encodedTotal=0;
            for(int i=0;i<segmentCount;i++){
                string partName=parts!=null?(parts[i] as string):DiagnosticsPartFileName(i+1);
                if(partName==null||!IsDiagnosticsPartFileName(partName)||
                    !String.Equals(partName,DiagnosticsPartFileName(i+1),StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid diagnostics segment file name.");
                if(progress!=null)progress(i,segmentCount,partName);
                string body;
                if(inline!=null){
                    body=inline[i] as string;
                    if(body==null)throw new ArgumentException("Invalid inline diagnostics segment.");
                }else{
                    string path=ArchiveChild(archiveDirectory,partName);
                    byte[] bytes=ReadLimited(path,ReviewAnalysisMetadataBudget.SegmentTriggerBytes);
                    encodedTotal+=bytes.Length;body=EvidenceUtf8.GetString(bytes);
                }
                if(EvidenceUtf8.GetByteCount(body)>ReviewAnalysisMetadataBudget.SegmentTriggerBytes)
                    throw new ArgumentException("Diagnostics segment exceeds the 32 MiB spill threshold.");
                if(inline!=null)encodedTotal+=EvidenceUtf8.GetByteCount(body);
                if(encodedTotal>ReviewAnalysisMetadataBudget.SegmentedDiagnosticsTotalBytes)
                    throw new ArgumentException("Segmented algorithm diagnostics exceed the 512 MiB archive budget.");
                var slice=ParseFlatAlgorithmDiagnostics(body,root,retainedPaths,ReviewAnalysisMetadataBudget.SegmentTriggerBytes,ref decodedBytes,ref eLedgerBytes);
                foreach(var entry in slice){
                    if(merged.ContainsKey(entry.Key))throw new ArgumentException("Duplicate algorithm diagnostics key across segments.");
                    merged.Add(entry.Key,entry.Value);
                }
            }
            if(progress!=null)progress(segmentCount,segmentCount,"");
            return merged;
        }
        static Dictionary<string,string> ParseFlatAlgorithmDiagnostics(string json,string root,string[] retainedPaths,int encodedLimit,
            ref int decodedBytes,ref int eLedgerBytes)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(String.IsNullOrEmpty(json))return result;
            if(json.Length>encodedLimit||EvidenceUtf8.GetByteCount(json)>encodedLimit)
                throw new ArgumentException("Algorithm diagnostics exceed the bounded segment budget.");
            var values=CandidateSerializer(Math.Max(encodedLimit,CandidateLegacyFlatBytes)).DeserializeObject(json) as Dictionary<string,object>;
            if(values==null||values.Count>100000)throw new ArgumentException("Invalid algorithm diagnostics.");
            var keep=new HashSet<string>(retainedPaths,StringComparer.OrdinalIgnoreCase);
            foreach(var entry in values){
                string path=SessionResolve(root,entry.Key),status=entry.Value as string;
                if(status==null)throw new ArgumentException("Invalid algorithm status.");
                status=DecodeAlgorithmStatus(status,ref decodedBytes);
                eLedgerBytes=AddCandidateLedgerBytes(eLedgerBytes,ReviewEAnalysis.ValidateLedgerStatus(status));
                if(keep.Contains(path))result.Add(SessionRelative(root,path),status);
            }
            return result;
        }
        static int AddCandidateLedgerBytes(int total,int current)
        {
            if(total<0||current<0||current>ReviewEAnalysis.MaximumLedgerBytes||
                total>CandidateLedgerValidationBytesLimit-current)
                throw new InvalidDataException("Combined E ledger validation exceeds the bounded 512 MiB session work budget; split the analysis batch.");
            return checked(total+current);
        }
        static string DecodeAlgorithmStatus(string stored,ref int decodedBytes)
        {
            string status=stored;
            if(stored.StartsWith(CompressedEvidencePrefix,StringComparison.Ordinal)){
                byte[] compressed=Convert.FromBase64String(stored.Substring(CompressedEvidencePrefix.Length));
                using(var input=new MemoryStream(compressed,false))
                using(var gzip=new GZipStream(input,CompressionMode.Decompress))
                using(var output=new MemoryStream()){
                    var buffer=new byte[8192];int read;
                    while((read=gzip.Read(buffer,0,buffer.Length))>0){
                        if(output.Length+read>4L*CandidateStatusCharacterLimit||decodedBytes+output.Length+read>CandidateDecodedBytesLimit)
                            throw new InvalidDataException("Expanded algorithm diagnostics exceed the session budget.");
                        output.Write(buffer,0,read);
                    }
                    status=EvidenceUtf8.GetString(output.ToArray());
                }
            }
            if(status.Length>CandidateStatusCharacterLimit)throw new ArgumentException("Invalid algorithm status size.");
            decodedBytes=checked(decodedBytes+EvidenceUtf8.GetByteCount(status));
            if(decodedBytes>CandidateDecodedBytesLimit)throw new InvalidDataException("Expanded algorithm diagnostics exceed the session budget.");
            return status;
        }
        Dictionary<string,string> WithCompletedAlgorithmDiagnostics(IEnumerable<CompletedFile> completed)
        {
            var next=MergeCompletedAlgorithmDiagnostics(algorithmDiagnostics,projectPath,completed);
            // Location matrices must publish even when evidence serialization
            // hits a hard ceiling. Segmented spill usually succeeds; if not,
            // keep the in-memory map so 462-LOC batches still show results.
            try{SerializeCurrentAlgorithmDiagnostics(next);}
            catch(Exception ex){
                if(IsFatalHostException(ex))throw;
                pendingDiagnosticsArchiveParts=new List<KeyValuePair<string,byte[]>>();
            }
            return next;
        }
        static Dictionary<string,string> MergeCompletedAlgorithmDiagnostics(Dictionary<string,string> prior,string project,IEnumerable<CompletedFile> completed)
        {
            var next=new Dictionary<string,string>(prior??new Dictionary<string,string>(),StringComparer.OrdinalIgnoreCase);
            string root=SessionRoot(project);
            foreach(var file in completed){
                string status=file.Result.Status??"";
                if(status.Length>1024*1024)throw new InvalidOperationException("Algorithm status is too large; results were not merged.");
                next[SessionRelative(root,file.Request.LocationFilePath)]=status;
            }
            return next;
        }
        string TrySerializeAlgorithmDiagnosticsForCommit(Dictionary<string,string> values,out string warning)
        {
            warning=null;
            try{return SerializeCurrentAlgorithmDiagnostics(values);}
            catch(Exception ex){
                if(IsFatalHostException(ex))throw;
                pendingDiagnosticsArchiveParts=new List<KeyValuePair<string,byte[]>>();
                warning=ex.Message;return null;
            }
        }
        void AdoptSerializedAlgorithmDiagnostics(Dictionary<string,EncodedCandidateStatus> validated,List<KeyValuePair<string,byte[]>> parts)
        {
            encodedCandidateStatuses=validated;
            encodedCandidateProject=projectPath??"";
            pendingDiagnosticsArchiveParts=parts??new List<KeyValuePair<string,byte[]>>();
        }
    }
}
