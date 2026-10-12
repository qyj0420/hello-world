using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        // A complete project can exceed the old 8 MiB encoded quota even when
        // every record and decoded ledger is individually valid. Keep a finite
        // project-sized quota with headroom in the shared 64 MiB session limit.
        const int CandidateEvidenceLimit=ReviewAnalysisMetadataBudget.EncodedDiagnosticsBytes;
        const int CandidateStatusCharacterLimit=1024*1024;
        const int CandidateDecodedBytesLimit=64*1024*1024;
        // A finite cumulative validation-work budget, NOT a resident decoded
        // collection. ValidateLedgerStatus expands one <=16 MiB ledger at a
        // time; only its compressed status and scalar byte count survive.
        const int CandidateLedgerValidationBytesLimit=512*1024*1024;
        const string CompressedEvidencePrefix="HVX.Diagnostics.Gzip.v1:";
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
        static JavaScriptSerializer CandidateSerializer(){return new JavaScriptSerializer{MaxJsonLength=CandidateEvidenceLimit,RecursionLimit=96};}
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
            return SerializeAlgorithmDiagnosticsCore(values,null,out ignored);
        }
        string SerializeCurrentAlgorithmDiagnostics(Dictionary<string,string> values)
        {
            string project=projectPath??"";
            var prior=String.Equals(encodedCandidateProject,project,StringComparison.OrdinalIgnoreCase)?encodedCandidateStatuses:null;
            Dictionary<string,EncodedCandidateStatus> next;
            string json=SerializeAlgorithmDiagnosticsCore(values,prior,out next);
            // Publish only a completely validated, bounded snapshot. Removed
            // records are not retained, and a failed operation leaves it intact.
            encodedCandidateStatuses=next;encodedCandidateProject=project;
            return json;
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
            out Dictionary<string,EncodedCandidateStatus> next)
        {
            // Full per-file evidence is retained in memory and restored losslessly.
            // Only the session representation is compressed; never discard rows or
            // source-identity warnings to satisfy the metadata quota.
            var stored=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var validated=new Dictionary<string,EncodedCandidateStatus>(StringComparer.OrdinalIgnoreCase);
            // Repeated immutable status text within this operation is proved
            // once, while every occurrence still charges all aggregate budgets.
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
            string json=CandidateSerializer().Serialize(stored);
            if(EvidenceUtf8.GetByteCount(json)>CandidateEvidenceLimit)throw new InvalidOperationException("Algorithm diagnostics exceed the bounded 32 MiB session budget; split the analysis batch.");
            next=validated;return json;
        }
        static Dictionary<string,string> ParseAlgorithmDiagnostics(string json,string root,string[] retainedPaths)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(String.IsNullOrEmpty(json))return result;
            if(json.Length>CandidateEvidenceLimit||EvidenceUtf8.GetByteCount(json)>CandidateEvidenceLimit)throw new ArgumentException("Algorithm diagnostics exceed the bounded 32 MiB session budget.");
            var values=CandidateSerializer().DeserializeObject(json) as Dictionary<string,object>;
            if(values==null||values.Count>100000)throw new ArgumentException("Invalid algorithm diagnostics.");
            var keep=new HashSet<string>(retainedPaths,StringComparer.OrdinalIgnoreCase);
            int decodedBytes=0,eLedgerBytes=0;
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
            var next=new Dictionary<string,string>(algorithmDiagnostics,StringComparer.OrdinalIgnoreCase);
            string root=SessionRoot(projectPath);
            foreach(var file in completed){
                string status=file.Result.Status??"";
                if(status.Length>1024*1024)throw new InvalidOperationException("Algorithm status is too large; results were not merged.");
                next[SessionRelative(root,file.Request.LocationFilePath)]=status;
            }
            SerializeCurrentAlgorithmDiagnostics(next);return next;
        }
    }
}
