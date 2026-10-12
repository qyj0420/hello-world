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
        // are never interpreted as global merged matrix indices. A value is a
        // small inline status or a ReviewDiagnosticSpill reference; complete
        // statuses are restored one file at a time, never held for a session.
        Dictionary<string,string> algorithmDiagnostics=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        // Flat self-contained JSON quota (also the per-segment quota).
        const int CandidateEvidenceLimit=ReviewAnalysisMetadataBudget.EncodedDiagnosticsBytes;
        // Old single-file archives may still carry a flat payload up to the
        // session.json ceiling; keep that readable without forcing a rewrite.
        const int CandidateLegacyFlatBytes=ReviewAnalysisMetadataBudget.SessionMetadataBytes;
        const int CandidateStatusCharacterLimit=1024*1024;
        const long CandidateDecodedBytesLimit=ReviewAnalysisMetadataBudget.DecodedDiagnosticsBytes;
        const long CandidateLedgerValidationBytesLimit=ReviewAnalysisMetadataBudget.LedgerValidationWorkBytes;
        const string CompressedEvidencePrefix="HVX.Diagnostics.Gzip.v1:";
        const string SegmentedDiagnosticsSchema="HVX.AlgorithmDiagnostics.Segmented/1";
        static readonly Encoding EvidenceUtf8=new UTF8Encoding(false,true);
        static JavaScriptSerializer CandidateSerializer(int maxJsonLength)
        {return new JavaScriptSerializer{MaxJsonLength=maxJsonLength,RecursionLimit=96};}
        [Browsable(false)] public string AlgorithmDiagnosticsJson { get { return SerializeAlgorithmDiagnostics(algorithmDiagnostics); } }
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
        internal static string ResolveAlgorithmStatus(string stored){return ReviewDiagnosticSpill.Resolve(stored);}
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
        static string EncodeCandidateStatus(string status)
        {
            byte[] bytes=EvidenceUtf8.GetBytes(status);
            bool reserved=status.StartsWith(CompressedEvidencePrefix,StringComparison.Ordinal)||status.StartsWith(ReviewDiagnosticSpill.ReferencePrefix,StringComparison.Ordinal);
            if(bytes.Length<=4096&&!reserved)return status;
            using(var output=new MemoryStream()){
                using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))gzip.Write(bytes,0,bytes.Length);
                string compressed=CompressedEvidencePrefix+Convert.ToBase64String(output.ToArray());
                return compressed.Length<status.Length||reserved?compressed:status;
            }
        }
        // Self-contained form for hosts that persist the session themselves.
        // Statuses are restored and encoded one at a time; only the encoded
        // map is built. Above 32 MiB it becomes an inline segmented envelope,
        // capped at the 64 MiB session-metadata ceiling that hosts can reload.
        static string SerializeAlgorithmDiagnostics(Dictionary<string,string> values)
        {
            CheckAlgorithmDiagnosticsBudget(values);
            var stored=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var byContent=new Dictionary<string,string>(StringComparer.Ordinal);
            long encodedBytes=0;
            foreach(var entry in values){
                string encoded;
                if(!byContent.TryGetValue(entry.Value,out encoded)){
                    string status=ResolveAlgorithmStatus(entry.Value);
                    if(status.Length>CandidateStatusCharacterLimit)throw new InvalidOperationException("Invalid algorithm status size.");
                    encoded=EncodeCandidateStatus(status);byContent.Add(entry.Value,encoded);
                }
                encodedBytes+=EvidenceUtf8.GetByteCount(encoded)+EvidenceUtf8.GetByteCount(entry.Key)+8;
                if(encodedBytes>CandidateLegacyFlatBytes)
                    throw new InvalidOperationException("Algorithm diagnostics exceed the 64 MiB self-contained session budget; save a named analysis instead.");
                stored.Add(entry.Key,encoded);
            }
            byContent=null;
            var segments=PackDiagnosticsSegments(stored);
            if(segments.Count==1)return segments[0];
            if(segments.Count>ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments)
                throw new InvalidOperationException("Algorithm diagnostics exceed the maximum number of 32 MiB segments; save a named analysis instead.");
            long totalEncoded=segments.Sum(segment=>(long)EvidenceUtf8.GetByteCount(segment));
            var envelope=new Dictionary<string,object>{
                {"schema",SegmentedDiagnosticsSchema},{"version",1},
                {"parts",Enumerable.Range(1,segments.Count).Select(DiagnosticsPartFileName).ToArray()},
                {"recordCount",stored.Count},{"encodedBytes",totalEncoded},{"inlineParts",segments.ToArray()}
            };
            return CandidateSerializer(2*CandidateLegacyFlatBytes).Serialize(envelope);
        }
        static List<string> PackDiagnosticsSegments(Dictionary<string,string> stored)
        {
            // Estimate-then-flush packing; never reserialize the growing map.
            var segments=new List<string>();
            var current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var serializer=CandidateSerializer(CandidateLegacyFlatBytes);
            const long softTrigger=(long)(ReviewAnalysisMetadataBudget.SegmentTriggerBytes*0.92);
            long estimated=2;
            foreach(var entry in stored){
                long entryBytes=8L+EvidenceUtf8.GetByteCount(entry.Key)+EvidenceUtf8.GetByteCount(entry.Value);
                if(current.Count>0&&estimated+entryBytes>softTrigger){
                    segments.Add(CheckedSegment(serializer.Serialize(current)));
                    current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    estimated=2;
                }
                current.Add(entry.Key,entry.Value);
                estimated+=entryBytes;
            }
            if(current.Count>0)segments.Add(CheckedSegment(serializer.Serialize(current)));
            if(segments.Count==0)segments.Add("{}");
            return segments;
        }
        static string CheckedSegment(string segment)
        {
            if(EvidenceUtf8.GetByteCount(segment)>ReviewAnalysisMetadataBudget.SegmentTriggerBytes)
                throw new InvalidOperationException("Algorithm diagnostics segment exceeds the 32 MiB segment budget.");
            return segment;
        }
        // Archive/rollback form: spill references stay references and the
        // statuses they name are written to diagnostics.bin separately.
        static string SerializeAlgorithmDiagnosticReferences(Dictionary<string,string> values)
        {
            CheckAlgorithmDiagnosticsBudget(values);
            var stored=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in values)stored.Add(entry.Key,ReviewDiagnosticSpill.IsReference(entry.Value)?entry.Value:EncodeCandidateStatus(entry.Value));
            string json=CandidateSerializer(CandidateEvidenceLimit).Serialize(stored);
            if(EvidenceUtf8.GetByteCount(json)>CandidateEvidenceLimit)throw new InvalidOperationException("Algorithm diagnostics index exceeds its 32 MiB budget; split the analysis batch.");
            return json;
        }
        static IEnumerable<string> AlgorithmDiagnosticReferences(Dictionary<string,string> values)
        {return values.Values.Where(ReviewDiagnosticSpill.IsReference);}
        // Finite ceiling on what one session may reference. Reference metadata
        // is used, so nothing is decoded to enforce it.
        static void CheckAlgorithmDiagnosticsBudget(Dictionary<string,string> values)
        {
            long decodedBytes=0,ledgerBytes=0;
            foreach(var value in values.Values){
                string sha;int decoded,ledger;
                if(value==null)throw new InvalidOperationException("Invalid algorithm status.");
                if(!ReviewDiagnosticSpill.TryParseReference(value,out sha,out decoded,out ledger)){
                    if(value.Length>CandidateStatusCharacterLimit)throw new InvalidOperationException("Invalid algorithm status size.");
                    decoded=EvidenceUtf8.GetByteCount(value);ledger=ReviewEAnalysis.ValidateLedgerStatus(value);
                }
                decodedBytes+=decoded;
                if(decodedBytes>CandidateDecodedBytesLimit)throw new InvalidOperationException("Algorithm diagnostics exceed the bounded 2 GiB referenced-evidence budget; split the analysis batch.");
                ledgerBytes=AddCandidateLedgerBytes(ledgerBytes,ledger);
            }
        }
        // Worker-thread step: validate one completed file and spill it so the
        // batch never accumulates complete statuses in memory.
        internal static string RetainCompletedStatus(string status)
        {
            status=status??"";
            if(status.Length>CandidateStatusCharacterLimit)throw new InvalidOperationException("Algorithm status is too large; this file was not merged.");
            return ReviewDiagnosticSpill.Retain(status,ReviewEAnalysis.ValidateLedgerStatus(status));
        }
        sealed class DiagnosticsParse
        {
            internal string Root,ArchiveDirectory;
            internal HashSet<string> Keep;
            internal bool ProcessReferences;
            internal ReviewDiagnosticArchivePack Pack;
            internal long DecodedBytes,LedgerBytes;
            internal int Seen;
            internal Action<int,int,string> Progress;
            internal Dictionary<string,string> Result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        }
        static Dictionary<string,string> ParseAlgorithmDiagnostics(string json,string root,string[] retainedPaths)
        {return ParseAlgorithmDiagnostics(json,root,retainedPaths,null,null,false);}
        static Dictionary<string,string> ParseAlgorithmDiagnostics(string json,string root,string[] retainedPaths,string archiveDirectory,Action<int,int,string> progress)
        {return ParseAlgorithmDiagnostics(json,root,retainedPaths,archiveDirectory,progress,false);}
        // Accepts a flat map, a segmented envelope (inline or archive part
        // files) and a reference index. Spill references are honoured only from
        // a named archive (verified against its diagnostics.bin) or, for an
        // in-process rollback, from the live store. Every entry is decoded,
        // verified and spilled on its own; nothing accumulates decoded.
        static Dictionary<string,string> ParseAlgorithmDiagnostics(string json,string root,string[] retainedPaths,string archiveDirectory,
            Action<int,int,string> progress,bool processReferences)
        {
            var parse=new DiagnosticsParse{Root=root,ArchiveDirectory=archiveDirectory,ProcessReferences=processReferences,Progress=progress,
                Keep=new HashSet<string>(retainedPaths,StringComparer.OrdinalIgnoreCase)};
            if(String.IsNullOrEmpty(json))return parse.Result;
            try{
                if(IsSegmentedDiagnosticsEnvelope(json))ParseSegmentedAlgorithmDiagnostics(json,parse);
                else ParseFlatAlgorithmDiagnostics(json,CandidateLegacyFlatBytes,parse);
            }finally{if(parse.Pack!=null)parse.Pack.Dispose();}
            return parse.Result;
        }
        static void ParseSegmentedAlgorithmDiagnostics(string json,DiagnosticsParse parse)
        {
            if(json.Length>2*CandidateLegacyFlatBytes)throw new ArgumentException("Segmented algorithm diagnostics envelope exceeds its size limit.");
            var envelope=CandidateSerializer(2*CandidateLegacyFlatBytes).DeserializeObject(json) as Dictionary<string,object>;
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
            if(inline==null&&EvidenceUtf8.GetByteCount(json)>256*1024)
                throw new ArgumentException("Segmented algorithm diagnostics envelope exceeds its size limit.");
            int segmentCount=inline!=null?inline.Length:(parts!=null?parts.Length:0);
            if(segmentCount<1||segmentCount>ReviewAnalysisMetadataBudget.MaxDiagnosticsSegments)
                throw new ArgumentException("Invalid segmented algorithm diagnostics part count.");
            if(inline!=null&&parts!=null&&inline.Length!=parts.Length)
                throw new ArgumentException("Segmented algorithm diagnostics part lists disagree.");
            if(inline==null&&(String.IsNullOrEmpty(parse.ArchiveDirectory)||parts==null))
                throw new ArgumentException("Segmented algorithm diagnostics require archive part files or inline payloads.");
            long encodedTotal=0;
            for(int i=0;i<segmentCount;i++){
                string partName=parts!=null?(parts[i] as string):DiagnosticsPartFileName(i+1);
                if(partName==null||!IsDiagnosticsPartFileName(partName)||
                    !String.Equals(partName,DiagnosticsPartFileName(i+1),StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid diagnostics segment file name.");
                if(parse.Progress!=null)parse.Progress(i,segmentCount,partName);
                string body;
                if(inline!=null){
                    body=inline[i] as string;
                    if(body==null)throw new ArgumentException("Invalid inline diagnostics segment.");
                }else body=EvidenceUtf8.GetString(ReadLimited(ArchiveChild(parse.ArchiveDirectory,partName),ReviewAnalysisMetadataBudget.SegmentTriggerBytes));
                encodedTotal+=EvidenceUtf8.GetByteCount(body);
                if(encodedTotal>ReviewAnalysisMetadataBudget.SegmentedDiagnosticsTotalBytes)
                    throw new ArgumentException("Segmented algorithm diagnostics exceed the 512 MiB archive budget.");
                ParseFlatAlgorithmDiagnostics(body,ReviewAnalysisMetadataBudget.SegmentTriggerBytes,parse);
                if(inline!=null)inline[i]=null;
            }
            if(parse.Progress!=null)parse.Progress(segmentCount,segmentCount,"");
        }
        static void ParseFlatAlgorithmDiagnostics(string json,int encodedLimit,DiagnosticsParse parse)
        {
            if(String.IsNullOrEmpty(json))return;
            if(json.Length>encodedLimit||EvidenceUtf8.GetByteCount(json)>encodedLimit)
                throw new ArgumentException("Algorithm diagnostics exceed the bounded session budget.");
            var values=CandidateSerializer(Math.Max(encodedLimit,CandidateLegacyFlatBytes)).DeserializeObject(json) as Dictionary<string,object>;
            if(values==null||values.Count>100000||parse.Seen+values.Count>100000)throw new ArgumentException("Invalid algorithm diagnostics.");
            foreach(var entry in values){
                string path=SessionResolve(parse.Root,entry.Key),stored=entry.Value as string;
                if(stored==null)throw new ArgumentException("Invalid algorithm status.");
                string key=SessionRelative(parse.Root,path);
                bool retained=parse.Keep.Contains(path);
                if(retained&&parse.Result.ContainsKey(key))throw new ArgumentException("Duplicate algorithm diagnostics key.");
                if(++parse.Seen%64==0&&parse.Progress!=null)parse.Progress(parse.Seen,values.Count,"AlgorithmDiagnostics");
                string sha;int decoded,ledger;
                if(ReviewDiagnosticSpill.TryParseReference(stored,out sha,out decoded,out ledger)){
                    if(parse.ArchiveDirectory==null&&!parse.ProcessReferences)throw new ArgumentException("Spilled algorithm diagnostics require their analysis archive.");
                    if(!retained)continue;
                    string reference;
                    if(parse.ArchiveDirectory!=null){
                        if(parse.Pack==null)parse.Pack=ReviewDiagnosticArchivePack.Open(ArchiveChild(parse.ArchiveDirectory,ReviewDiagnosticArchivePack.MemberName));
                        reference=parse.Pack.Import(sha,decoded,ledger);
                    }
                    else reference=ReviewDiagnosticSpill.Adopt(stored);
                    if(reference==null)throw new InvalidDataException("Spilled algorithm diagnostics are no longer available.");
                    parse.DecodedBytes+=decoded;
                    if(parse.DecodedBytes>CandidateDecodedBytesLimit)throw new InvalidDataException("Expanded algorithm diagnostics exceed the session budget.");
                    parse.LedgerBytes=AddCandidateLedgerBytes(parse.LedgerBytes,ledger);
                    parse.Result.Add(key,reference);
                    continue;
                }
                string status=DecodeAlgorithmStatus(stored,ref parse.DecodedBytes);
                int ledgerBytes=ReviewEAnalysis.ValidateLedgerStatus(status);
                parse.LedgerBytes=AddCandidateLedgerBytes(parse.LedgerBytes,ledgerBytes);
                if(retained)parse.Result.Add(key,ReviewDiagnosticSpill.Retain(status,ledgerBytes));
            }
        }
        static long AddCandidateLedgerBytes(long total,int current)
        {
            if(total<0||current<0||current>ReviewEAnalysis.MaximumLedgerBytes||
                total>CandidateLedgerValidationBytesLimit-current)
                throw new InvalidDataException("Combined E ledger validation exceeds the bounded 8 GiB session work budget; split the analysis batch.");
            return total+current;
        }
        static string DecodeAlgorithmStatus(string stored,ref long decodedBytes)
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
            decodedBytes+=EvidenceUtf8.GetByteCount(status);
            if(decodedBytes>CandidateDecodedBytesLimit)throw new InvalidDataException("Expanded algorithm diagnostics exceed the session budget.");
            return status;
        }
        Dictionary<string,string> WithCompletedAlgorithmDiagnostics(IEnumerable<CompletedFile> completed)
        {
            var next=MergeCompletedAlgorithmDiagnostics(algorithmDiagnostics,projectPath,completed);
            CheckAlgorithmDiagnosticsBudget(next);return next;
        }
        // Completed files arrive already spilled by the worker; only the
        // reference map is built, so no status is decoded here.
        static Dictionary<string,string> MergeCompletedAlgorithmDiagnostics(Dictionary<string,string> prior,string project,IEnumerable<CompletedFile> completed)
        {
            var next=new Dictionary<string,string>(prior??new Dictionary<string,string>(),StringComparer.OrdinalIgnoreCase);
            string root=SessionRoot(project);
            foreach(var file in completed)
                next[SessionRelative(root,file.Request.LocationFilePath)]=file.Diagnostics??RetainCompletedStatus(file.Result.Status);
            return next;
        }
    }
}
