using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace HVX.Task17.Location;

// Explicit optional sidecar. Never called by the append-only single-LOC API.
// No expected source positions, source-count limit, training labels or method6 votes.
public static class EighthProjectEvidence
{
    public sealed class Input
    {
        public string EngineConfigId { get; set; }
        public string TimingMode { get; set; }
        public string[] ExpectedRecordIds { get; set; }
        public Receipt[] Receipts { get; set; }
    }
    public sealed class Receipt
    {
        public string RecordId { get; set; }
        public string WaveformSha256 { get; set; }
        public string AcquisitionGroupId { get; set; }
        public string EngineConfigId { get; set; }
        public string TimingMode { get; set; }
        public string Phase { get; set; }
        public string GeometryIdentity { get; set; }
        public double LengthM { get; set; }
        public bool Valid { get; set; }
        public bool Complete { get; set; }
        public Candidate[] Candidates { get; set; }
    }
    public sealed class Candidate
    {
        public string Id { get; set; }
        public double PositionM { get; set; }
        public double TimingPositionBoundM { get; set; }
        public bool PhysicalAccepted { get; set; }
        public string Role { get; set; }
    }
    public sealed class Item
    {
        public string RecordId { get; set; }
        public string CandidateId { get; set; }
        public double? PositionM { get; set; }
        public double? TimingPositionBoundM { get; set; }
        public string NumericIssue { get; set; }
        public bool OriginalPhysicalAccepted { get; set; }
        public int OtherEvidenceGroups { get; set; }
        public string[] SupportingRecordIds { get; set; }
        public string State { get; set; }
        public bool RepeatedLocationEvidence { get; set; }
    }
    public sealed class Result
    {
        public string Schema { get; set; }
        public bool Complete { get; set; }
        public bool OriginalRowsChanged { get; set; }
        public bool CertifiesPartialDischarge { get; set; }
        public bool IndependentAcquisitionProven { get; set; }
        public string[] MissingRecordIds { get; set; }
        public string[] InvalidOrIncompleteRecordIds { get; set; }
        public int DistinctWaveformCount { get; set; }
        public Item[] Items { get; set; }
        public string Limitations { get; set; }
    }
    private sealed class Entry
    {
        internal Receipt Receipt;
        internal Candidate Candidate;
        internal bool Eligible;
        internal string Group;
        internal double Lo,Hi;
    }
    private sealed class IntervalEvent
    {
        internal double Position;
        internal string Group,Record;
        internal int Delta;
    }
    private static bool Finite(double x) { return !Double.IsNaN(x)&&!Double.IsInfinity(x); }
    private static bool Text(string x) { return !String.IsNullOrWhiteSpace(x); }
    private static bool Hash(string x)
    {
        return x!=null&&x.Length==64&&x.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'));
    }
    private static string Waveform(Receipt r) { return r.WaveformSha256.ToUpperInvariant(); }
    private static bool Compatible(Entry a,Entry b)
    {
        return a.Receipt.Phase==b.Receipt.Phase&&a.Receipt.GeometryIdentity==b.Receipt.GeometryIdentity&&
            a.Receipt.LengthM==b.Receipt.LengthM&&Waveform(a.Receipt)!=Waveform(b.Receipt)&&
            a.Group!=b.Group&&
            a.Lo<=b.Hi&&b.Lo<=a.Hi;
    }
    private static Dictionary<string,string> EvidenceGroups(Receipt[] receipts)
    {
        // OR equivalence closure: either the same bytes OR an explicitly shared
        // acquisition makes evidence dependent. Different aliases cannot split a copy.
        var parent=receipts.ToDictionary(r=>r.RecordId,r=>r.RecordId,StringComparer.Ordinal);
        Func<string,string> root=id=>{while(parent[id]!=id)id=parent[id];return id;};
        Action<string,string> union=(a,b)=>{a=root(a);b=root(b);if(a!=b){if(StringComparer.Ordinal.Compare(a,b)<0)parent[b]=a;else parent[a]=b;}};
        var firstHash=new Dictionary<string,string>(StringComparer.Ordinal);
        var firstAcquisition=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var receipt in receipts.OrderBy(r=>r.RecordId,StringComparer.Ordinal))
        {
            string first;
            if(Hash(receipt.WaveformSha256))
            {string hash=Waveform(receipt);if(firstHash.TryGetValue(hash,out first))union(receipt.RecordId,first);else firstHash[hash]=receipt.RecordId;}
            if(Text(receipt.AcquisitionGroupId))
            {if(firstAcquisition.TryGetValue(receipt.AcquisitionGroupId,out first))union(receipt.RecordId,first);else firstAcquisition[receipt.AcquisitionGroupId]=receipt.RecordId;}
        }
        return receipts.ToDictionary(r=>r.RecordId,r=>root(r.RecordId),StringComparer.Ordinal);
    }
    /// <summary>
    /// Counts leave-one-waveform common-interval support, not probability or PD truth.
    /// Different file bytes are not proof of statistically independent acquisitions.
    /// Bound must come from raw timing and geometry, never fitted to project clusters.
    /// </summary>
    public static Result Evaluate(Input input)
    {
        if(input==null||!Text(input.EngineConfigId)||
            (input.TimingMode!="peak"&&input.TimingMode!="first_arrival")||
            input.ExpectedRecordIds==null||input.Receipts==null)
            throw new ArgumentException("Explicit engine/config identity, timing mode and complete record manifest required.");
        if(input.ExpectedRecordIds.Length>100000||input.Receipts.Length>100000)
            throw new ArgumentException("Record budget exceeded; no partial concentration claim returned.");
        var expected=new HashSet<string>(input.ExpectedRecordIds,StringComparer.Ordinal);
        if(expected.Count!=input.ExpectedRecordIds.Length||expected.Any(x=>!Text(x)))
            throw new ArgumentException("Expected record identities must be nonblank and unique.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var all=new List<Entry>();
        var incomplete=new List<string>();
        foreach(var receipt in input.Receipts)
        {
            if(receipt==null||!Text(receipt.RecordId)||!seen.Add(receipt.RecordId)||!expected.Contains(receipt.RecordId))
                throw new ArgumentException("Unexpected or duplicate receipt identity.");
            if(receipt.EngineConfigId!=input.EngineConfigId||receipt.TimingMode!=input.TimingMode)
                throw new ArgumentException("Cannot pool different engine/config or timing-mode results.");
            bool valid=receipt.Valid&&receipt.Complete&&Hash(receipt.WaveformSha256)&&
                Text(receipt.Phase)&&Text(receipt.GeometryIdentity)&&Finite(receipt.LengthM)&&receipt.LengthM>0;
            if(!valid)incomplete.Add(receipt.RecordId);
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var candidate in receipt.Candidates??new Candidate[0])
            {
                if(candidate==null||!Text(candidate.Id)||!ids.Add(candidate.Id))
                    throw new ArgumentException("Candidate identities must be nonblank and unique within each receipt.");
                bool position=Finite(candidate.PositionM)&&candidate.PositionM>=0&&candidate.PositionM<=receipt.LengthM;
                bool bound=Finite(candidate.TimingPositionBoundM)&&candidate.TimingPositionBoundM>=0&&
                    Finite(candidate.PositionM-candidate.TimingPositionBoundM)&&Finite(candidate.PositionM+candidate.TimingPositionBoundM);
                // A whole-cable bound has no discriminating power: keep it, but do not anchor a source.
                bool informative=bound&&2*candidate.TimingPositionBoundM<receipt.LengthM;
                all.Add(new Entry {Receipt=receipt,Candidate=candidate,
                    Eligible=valid&&position&&informative&&candidate.PhysicalAccepted&&candidate.Role=="independent",
                    Lo=bound?candidate.PositionM-candidate.TimingPositionBoundM:Double.NaN,
                    Hi=bound?candidate.PositionM+candidate.TimingPositionBoundM:Double.NaN});
                if(all.Count>200000)throw new ArgumentException("Candidate budget exceeded; no partial result returned.");
            }
        }
        // A bounded quadratic guard is explicit: it does not silently take only the largest clusters.
        if(all.Count>12000)throw new ArgumentException("Pair-comparison budget exceeded; partition by phase/geometry explicitly.");
        var evidenceGroups=EvidenceGroups(input.Receipts);
        foreach(var entry in all)entry.Group=evidenceGroups[entry.Receipt.RecordId];
        var items=new List<Item>();
        foreach(var q in all.OrderBy(e=>e.Receipt.RecordId,StringComparer.Ordinal).ThenBy(e=>e.Candidate.Id,StringComparer.Ordinal))
        {
            var supporters=new string[0];int best=0;
            if(q.Eligible)
            {
                var neighbors=all.Where(e=>e.Eligible&&Compatible(q,e)).ToArray();
                // Maximum common intersection, not single-link chaining. One very large LOC is one vote.
                var events=new List<IntervalEvent>();
                foreach(var n in neighbors)
                {
                    events.Add(new IntervalEvent {Position=Math.Max(q.Lo,n.Lo),Group=n.Group,Record=n.Receipt.RecordId,Delta=1});
                    events.Add(new IntervalEvent {Position=Math.Min(q.Hi,n.Hi),Group=n.Group,Record=n.Receipt.RecordId,Delta=-1});
                }
                var active=new Dictionary<string,SortedDictionary<string,int>>(StringComparer.Ordinal);
                foreach(var positionEvents in events.GroupBy(e=>e.Position).OrderBy(g=>g.Key))
                {
                    foreach(var ev in positionEvents.Where(e=>e.Delta==1))
                    {
                        SortedDictionary<string,int> records;
                        if(!active.TryGetValue(ev.Group,out records))active[ev.Group]=records=new SortedDictionary<string,int>(StringComparer.Ordinal);
                        int count;records.TryGetValue(ev.Record,out count);records[ev.Record]=count+1;
                    }
                    // Closed intervals: starts are included before equal-position ends are removed.
                    if(active.Count>best) {best=active.Count;supporters=active.Values.Select(v=>v.Keys.First()).OrderBy(x=>x,StringComparer.Ordinal).ToArray();}
                    foreach(var ev in positionEvents.Where(e=>e.Delta==-1))
                    {
                        var records=active[ev.Group];
                        if(--records[ev.Record]==0)records.Remove(ev.Record);
                        if(records.Count==0)active.Remove(ev.Group);
                    }
                }
            }
            bool repeated=q.Eligible&&best>=2;
            items.Add(new Item {RecordId=q.Receipt.RecordId,CandidateId=q.Candidate.Id,
                PositionM=Finite(q.Candidate.PositionM)?(double?)q.Candidate.PositionM:null,
                TimingPositionBoundM=Finite(q.Candidate.TimingPositionBoundM)?(double?)q.Candidate.TimingPositionBoundM:null,
                NumericIssue=!Finite(q.Candidate.PositionM)?"position_nonfinite_preserved_as_null":
                    !Finite(q.Candidate.TimingPositionBoundM)?"timing_bound_nonfinite_preserved_as_null":"",
                OriginalPhysicalAccepted=q.Candidate.PhysicalAccepted,OtherEvidenceGroups=best,
                SupportingRecordIds=supporters,RepeatedLocationEvidence=repeated,
                State=repeated?"repeated_location_evidence_not_PD_confirmation":
                    q.Eligible?"sparse_physical_candidate_project_review":"ineligible_or_role_unresolved_original_preserved"});
        }
        var missing=expected.Where(x=>!seen.Contains(x)).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        return new Result {Schema="method8-project-evidence-v1",Complete=missing.Length==0&&incomplete.Count==0,
            OriginalRowsChanged=false,CertifiesPartialDischarge=false,IndependentAcquisitionProven=false,
            MissingRecordIds=missing,InvalidOrIncompleteRecordIds=incomplete.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            DistinctWaveformCount=input.Receipts.Where(r=>Hash(r.WaveformSha256)).Select(Waveform).Distinct().Count(),
            Items=items.ToArray(),Limitations="Optional sidecar; no source-count cap or changed position. At least two OTHER distinct raw waveforms at a common interval are required. Repeated interference and shared CAL bias remain possible. Bounds are not calibrated confidence intervals; shared systematic error does not shrink with count. Failed/missing records are not negative PD findings. Role-unresolved candidates cannot be promoted by concentration."};
    }
    public static string EvaluateJson(string inputJson)
    {
        if(inputJson==null||inputJson.Length>67108864)throw new ArgumentException("JSON input budget exceeded.");
        var serializer=new JavaScriptSerializer {MaxJsonLength=67108864,RecursionLimit=128};
        return serializer.Serialize(Evaluate(serializer.Deserialize<Input>(inputJson)));
    }
}
