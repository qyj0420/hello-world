using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // This is checked provenance against caller-supplied matrices, NOT a digital
    // signature or a promise that a caller cannot deliberately fabricate history.
    private const string A7PriorVersion="seventh-local-independent-v2-calibration-quality";
    private string a7PreviousPriorJson="",a7PriorContext="",a7RecordKey="",a7PriorReason="not_supplied";
    private double[,] a7PreviousPriorParameters=new double[0,36],a7PreviousPriorRaw=new double[0,0];
    private readonly List<SeventhPriorSeed> a7ExternalSeeds=new List<SeventhPriorSeed>();
    private readonly List<A7PriorEntry> a7VerifiedPriorEntries=new List<A7PriorEntry>();
    private readonly HashSet<string> a7ExcludedPriorEvents=new HashSet<string>(StringComparer.Ordinal);
    private bool a7PriorQuarantined;
    private readonly List<A4Pair> a7IndependentSeedPairs=new List<A4Pair>();
    private readonly List<string> a7RankingTrace=new List<string>();
    private string a7NextPriorJson="null";
    [DataContract(Name="SeventhPriorDocument",Namespace="urn:hvx:seventh:prior")] private sealed class A7PriorDocument
    {
        [DataMember(Name="version",IsRequired=true)] internal string version;
        [DataMember(Name="context",IsRequired=true)] internal string context;
        [DataMember(Name="seeds",IsRequired=true)] internal A7PriorEntry[] seeds;
        [DataMember(Name="excluded_event_keys",EmitDefaultValue=false)] internal string[] excluded_event_keys;
        [DataMember(Name="disabled",EmitDefaultValue=false)] internal bool disabled;
    }
    [DataContract(Name="SeventhPriorEntry",Namespace="urn:hvx:seventh:prior")] private sealed class A7PriorEntry
    {
        [DataMember(Name="provenance",IsRequired=true)] internal string provenance;
        [DataMember(Name="event_key",IsRequired=true)] internal string event_key;
        [DataMember(Name="record_key",IsRequired=true)] internal string record_key;
        [DataMember(Name="parameter_sha256",IsRequired=true)] internal string parameter_sha256;
        [DataMember(Name="raw_crop_sha256",IsRequired=true)] internal string raw_crop_sha256;
        [DataMember(Name="canonical_peak",IsRequired=true)] internal int canonical_peak;
        [DataMember(Name="support_start",IsRequired=true)] internal int support_start;
        [DataMember(Name="support_end",IsRequired=true)] internal int support_end;
        [DataMember(Name="position_m",IsRequired=true)] internal double position_m;
        [DataMember(Name="local_score",IsRequired=true)] internal double local_score;
        [DataMember(Name="history_used",IsRequired=true)] internal bool history_used;
    }
    internal void A7SetPreviousPrior(string json,double[,] parameters,double[,] raw)
    {
        a7PreviousPriorJson=json??"";
        a7PreviousPriorParameters=AstraExplicitHistory.Copy(parameters,36);
        a7PreviousPriorRaw=AstraExplicitHistory.Copy(raw,0);
    }
    private static string A7HashBytes(byte[] bytes)
    {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
    private static string A7HashSamples(int count,Func<int,double> sample)
    {
        using(var sha=SHA256.Create())
        {
            var buffer=new byte[65536];int used=0;
            for(int i=0;i<count;i++)
            {
                // NaN payloads are not evidence. Canonicalize them, but preserve
                // finite bits (including signed zero) exactly.
                double v=sample(i);byte[] b=BitConverter.GetBytes(double.IsNaN(v)?double.NaN:v);
                Buffer.BlockCopy(b,0,buffer,used,8);used+=8;
                if(used==buffer.Length){sha.TransformBlock(buffer,0,used,buffer,0);used=0;}
            }
            sha.TransformFinalBlock(buffer,0,used);return BitConverter.ToString(sha.Hash).Replace("-","");
        }
    }
    private static string A7ParameterHash(double[,] p,int row)
    {return A7HashSamples(35,i=>p[row,i<9?i:i+1]);}
    private static bool A7Hex(string value)
    {return value!=null&&value.Length==64&&value.All(c=>(c>='0'&&c<='9')||(c>='A'&&c<='F'));}
    private void A7PreparePrior(A4Context c,string root,string cablePath,string calPath,int phase)
    {
        a7ExternalSeeds.Clear();a7VerifiedPriorEntries.Clear();a7IndependentSeedPairs.Clear();a7RankingTrace.Clear();a7NextPriorJson="null";
        a7ExcludedPriorEvents.Clear();a7PriorQuarantined=false;
        string cableHash=A7HashBytes(File.ReadAllBytes(cablePath));
        string calHash=String.IsNullOrEmpty(calPath)||!File.Exists(calPath)?"unavailable":A7HashBytes(File.ReadAllBytes(calPath));
        a7PriorContext=A7HashBytes(Encoding.UTF8.GetBytes(A7PriorVersion+"|"+Path.GetFullPath(root).TrimEnd('\\','/').ToUpperInvariant()+"|"+
            cableHash+"|"+calHash+"|"+phase+"|"+NumberJson(c.Fs)+"|"+NumberJson(c.Length)+"|"+NumberJson(c.ConnectingCableLengthM)+"|"+
            NumberArrayJson(c.Joints)+"|"+NumberArrayJson(c.Velocities)+"|"+NumberJson(c.CalibrationToleranceAdaptationLimitPercent)+"|"+
            (ManufacturerEndpointPolicy==null?"legacy":ManufacturerEndpointPolicy.Serialized)));
        a7RecordKey=A7HashSamples(c.Raw.Length,i=>c.Raw[i]);
        a7PriorReason="not_supplied_current_batch_only";
        if(String.IsNullOrWhiteSpace(a7PreviousPriorJson))return;
        try
        {
            if(a7PreviousPriorJson.Length>2*1024*1024)throw new InvalidDataException("sidecar_size_limit");
            A7PriorDocument doc;
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(a7PreviousPriorJson)))
                doc=(A7PriorDocument)new DataContractJsonSerializer(typeof(A7PriorDocument)).ReadObject(stream);
            if(doc==null||doc.version!=A7PriorVersion||doc.context!=a7PriorContext||doc.seeds==null||doc.seeds.Length>4096)
                throw new InvalidDataException("sidecar_version_context_or_count_mismatch");
            if(doc.disabled){a7PriorQuarantined=true;a7PriorReason="external_prior_disabled:prior_receipt_budget_quarantine";return;}
            foreach(string key in doc.excluded_event_keys??new string[0])
            {
                int peak;
                if(key==null||key.Length<66||key[64]!=':'||!A7Hex(key.Substring(0,64))||
                    !int.TryParse(key.Substring(65),out peak)||peak<0||key.Substring(65)!=peak.ToString())
                    throw new InvalidDataException("excluded_event_identity_invalid");
                a7ExcludedPriorEvents.Add(key);
            }
            int rows=a7PreviousPriorParameters.GetLength(0);
            if(rows>100000||a7PreviousPriorParameters.GetLength(1)!=36||a7PreviousPriorRaw.GetLength(0)!=rows)
                throw new InvalidDataException("previous_matrix_schema_or_budget");
            var hashes=new Dictionary<string,List<int>>(StringComparer.Ordinal);
            for(int row=0;row<rows;row++)
            {
                if(a7PreviousPriorParameters[row,26]!=6||a7PreviousPriorParameters[row,35]!=1||
                    !Finite(a7PreviousPriorParameters[row,33])||a7PreviousPriorParameters[row,33]<.7||
                    !Finite(a7PreviousPriorParameters[row,13]))continue;
                string key=A7ParameterHash(a7PreviousPriorParameters,row);List<int> found;
                if(!hashes.TryGetValue(key,out found)){found=new List<int>();hashes.Add(key,found);}found.Add(row);
            }
            foreach(var entry in doc.seeds)
            {
                if(entry==null||entry.provenance!="local_unique_all_hard_gates_autoaccepted"||entry.history_used||
                    !A7Hex(entry.record_key)||!A7Hex(entry.parameter_sha256)||!A7Hex(entry.raw_crop_sha256)||
                    entry.event_key!=entry.record_key+":"+entry.canonical_peak||entry.support_start<0||entry.support_end<entry.support_start||
                    entry.canonical_peak<entry.support_start||entry.canonical_peak>entry.support_end||!Finite(entry.position_m)||
                    !Finite(entry.local_score)||entry.local_score<.7||entry.local_score>1)
                    throw new InvalidDataException("sidecar_seed_provenance_invalid");
                List<int> matching;bool verified=false;
                if(hashes.TryGetValue(entry.parameter_sha256,out matching))foreach(int row in matching)
                {
                    var p=a7PreviousPriorParameters;
                    if(p[row,0]!=phase||p[row,5]!=c.Length||p[row,7]!=c.Fs||p[row,13]!=entry.position_m||p[row,33]!=entry.local_score||
                        p[row,31]>entry.support_start||p[row,32]<entry.support_end)continue;
                    int count=checked((int)(p[row,32]-p[row,31]+1));
                    if(count>0&&count<=a7PreviousPriorRaw.GetLength(1)&&A7HashSamples(count,i=>a7PreviousPriorRaw[row,i])==entry.raw_crop_sha256)
                    {verified=true;break;}
                }
                if(!verified)throw new InvalidDataException("sidecar_seed_not_present_in_previous_matrices");
                a7VerifiedPriorEntries.Add(entry);
                a7ExternalSeeds.Add(new SeventhPriorSeed {EventKey=entry.event_key,RecordKey=entry.record_key,Peak=entry.canonical_peak,
                    SupportStart=entry.support_start,SupportEnd=entry.support_end,Position=entry.position_m});
            }
            foreach(var group in a7VerifiedPriorEntries.GroupBy(e=>e.event_key,StringComparer.Ordinal))
            {
                if(group.Select(A7SeedVersion).Distinct(StringComparer.Ordinal).Count()>1)a7ExcludedPriorEvents.Add(group.Key);
            }
            a7VerifiedPriorEntries.RemoveAll(e=>a7ExcludedPriorEvents.Contains(e.event_key));
            a7ExternalSeeds.RemoveAll(e=>a7ExcludedPriorEvents.Contains(e.EventKey));
            a7PriorReason=a7ExcludedPriorEvents.Count==0?"validated_against_explicit_previous_matrices_not_a_signature":
                "validated_with_conflicting_event_identities_excluded_not_a_signature";
        }
        catch(Exception ex)
        {
            // Bad optional history cannot fail an otherwise valid LOC analysis.
            a7ExternalSeeds.Clear();a7VerifiedPriorEntries.Clear();
            a7PriorReason="external_prior_disabled:"+ex.GetType().Name+":"+ex.Message;
        }
    }
    private static string A7SeedVersion(A7PriorEntry e)
    {return e.parameter_sha256+":"+e.raw_crop_sha256+":"+e.support_start+":"+e.support_end+":"+NumberJson(e.position_m)+":"+NumberJson(e.local_score);}
    private SeventhPriorSeed A7CurrentSeed(A4Pair pair)
    {return new SeventhPriorSeed {EventKey=a7RecordKey+":"+pair.OutputIncident,RecordKey=a7RecordKey,Peak=pair.OutputIncident,
        SupportStart=pair.Incident.Start,SupportEnd=pair.Incident.End,Position=pair.Position};}
    private SeventhRankResult A7Rank(A4Context c,IEnumerable<A4Pair> candidates,IEnumerable<SeventhPriorSeed> seeds,A4Pulse incident)
    {return SeventhCandidateRanking.Select(candidates.Select(p=>new SeventhRankCandidate {Id=p.EdgeId,HardPassed=a7PipelineResults[p].CandidatePassed||a7PipelineResults[p].ReviewHypothesis,
        Correlation=p.Correlation,BaseScore=p.Confidence,Position=p.Position}),seeds,a7RecordKey,A4ChargeIndex(incident),incident.Start,incident.End,c.Length,true);}
    private void A7RecordRank(A4Pulse incident,SeventhRankResult rank,string stage)
    {a7RankingTrace.Add("{\"incident_peak\":"+A4ChargeIndex(incident)+",\"stage\":"+Json(stage)+",\"selected_candidate_id\":"+rank.SelectedId+
        ",\"history_used\":"+(rank.HistoryUsed?"true":"false")+",\"ambiguous\":"+(rank.Ambiguous?"true":"false")+",\"reason\":"+Json(rank.Reason)+
        ",\"near_candidate_ids\":["+String.Join(",",rank.NearCandidateIds)+"],\"support_counts\":["+String.Join(",",rank.SupportCounts)+
        "],\"supporting_seed_ids\":"+StringArrayJson(rank.SupportingSeedIds)+"}");}
    internal void A7CompletePrior(double[,] parameters,double[,] raw)
    {
        var entries=new List<A7PriorEntry>(a7VerifiedPriorEntries);
        foreach(var pair in a7IndependentSeedPairs)
        {
            Tuple<int,int> timing;if(!a7OutputTimings.TryGetValue(pair,out timing))continue;
            for(int row=0;row<parameters.GetLength(0);row++)
            {
                if(parameters[row,10]!=timing.Item1||parameters[row,11]!=timing.Item2||parameters[row,35]!=1||
                    parameters[row,33]!=pair.Confidence||parameters[row,13]!=pair.Position||
                    parameters[row,31]>pair.Incident.Start||parameters[row,32]<pair.Incident.End)continue;
                int count=(int)(parameters[row,32]-parameters[row,31]+1);
                entries.Add(new A7PriorEntry {provenance="local_unique_all_hard_gates_autoaccepted",history_used=false,
                    event_key=a7RecordKey+":"+pair.OutputIncident,record_key=a7RecordKey,canonical_peak=pair.OutputIncident,
                    support_start=pair.Incident.Start,support_end=pair.Incident.End,position_m=pair.Position,local_score=pair.Confidence,
                    parameter_sha256=A7ParameterHash(parameters,row),raw_crop_sha256=A7HashSamples(count,i=>raw[row,i])});break;
            }
        }
        foreach(var group in entries.GroupBy(e=>e.event_key,StringComparer.Ordinal))
            if(group.Select(A7SeedVersion).Distinct(StringComparer.Ordinal).Count()>1)a7ExcludedPriorEvents.Add(group.Key);
        entries.RemoveAll(e=>a7ExcludedPriorEvents.Contains(e.event_key));
        // Preserve all distinct verified versions of each exported event. Never
        // split a conflicting event group at the bounded receipt budget.
        var exported=new List<A7PriorEntry>();
        foreach(var group in entries.GroupBy(e=>e.event_key,StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            var versions=group.GroupBy(e=>e.parameter_sha256+":"+e.raw_crop_sha256,StringComparer.Ordinal)
                .Select(g=>g.First()).OrderBy(e=>e.parameter_sha256,StringComparer.Ordinal).ToArray();
            if(exported.Count+versions.Length>1024)continue;
            exported.AddRange(versions);
        }
        var doc=new A7PriorDocument {version=A7PriorVersion,context=a7PriorContext,seeds=a7PriorQuarantined?new A7PriorEntry[0]:exported.ToArray(),
            excluded_event_keys=a7ExcludedPriorEvents.OrderBy(s=>s,StringComparer.Ordinal).ToArray(),disabled=a7PriorQuarantined};
        using(var stream=new MemoryStream())
        {new DataContractJsonSerializer(typeof(A7PriorDocument)).WriteObject(stream,doc);a7NextPriorJson=Encoding.UTF8.GetString(stream.ToArray());}
        if(a7NextPriorJson.Length>2*1024*1024)
        {
            // Never trim conflict tombstones and accidentally revive an identity.
            doc.seeds=new A7PriorEntry[0];doc.excluded_event_keys=new string[0];doc.disabled=true;
            using(var stream=new MemoryStream())
            {new DataContractJsonSerializer(typeof(A7PriorDocument)).WriteObject(stream,doc);a7NextPriorJson=Encoding.UTF8.GetString(stream.ToArray());}
        }
        // Called after equivalent waveform extraction finalizes feature columns.
        // The receipt fingerprints the actual returned rows, not an earlier copy.
        if(StatusJson.EndsWith("}",StringComparison.Ordinal))StatusJson=StatusJson.Substring(0,StatusJson.Length-1)+",\"next_prior\":"+a7NextPriorJson+"}";
    }
    private string A7PriorDiagnosticJson()
    {return "{\"external_prior_reason\":"+Json(a7PriorReason)+",\"context_key\":"+Json(a7PriorContext)+
        ",\"external_seed_count\":"+a7ExternalSeeds.Count+",\"current_independent_seed_count\":"+a7IndependentSeedPairs.Count+
        ",\"excluded_event_identity_count\":"+a7ExcludedPriorEvents.Count+
        ",\"rho_gap\":0.02,\"base_score_gap\":0.05,\"radius_fraction_net_length\":0.01,\"minimum_independent_events\":3,"+
        "\"engineering_defaults_not_field_confirmed\":true,\"history_selected_never_becomes_seed\":true,\"base_scores_unchanged\":true,"+
        "\"decisions\":["+String.Join(",",a7RankingTrace)+"]}";}
}
