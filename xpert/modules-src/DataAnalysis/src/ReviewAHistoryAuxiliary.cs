using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using HVX.PD.LocationFiltering;

namespace HVX.PdReview
{
    // Session-only evidence. Never reconstruct provenance from a saved matrix,
    // caller acceptance edits, filenames, or distinct file bytes alone.
    internal sealed class AHistoryReceipt
    {
        internal readonly string Project,Path,Acquisition,FileHash,WaveHash,ProjectHash,Settings;
        internal readonly double[,] Parameters,Raw,Filtered,Equivalent;
        internal long Cells {get{return Parameters.LongLength+Raw.LongLength+Filtered.LongLength+Equivalent.LongLength;}}
        internal AHistoryReceipt(AHistoryIdentity identity,ReviewAnalysisResult result,int[] rows)
        {
            Project=identity.Project;Path=identity.Path;Acquisition=identity.Acquisition;
            FileHash=identity.FileHash;WaveHash=identity.WaveHash;ProjectHash=identity.ProjectHash;Settings=identity.Settings;
            Parameters=AHistoryEvidence.CopyRows(result.Parameters,rows);
            Raw=AHistoryEvidence.CopyRows(result.Raw,rows);Filtered=AHistoryEvidence.CopyRows(result.Filtered,rows);
            Equivalent=AHistoryEvidence.CopyRows(result.Equivalent,rows);
        }
    }
    internal sealed class AHistoryIdentity
    {
        internal string Project,Path,Acquisition,FileHash,WaveHash,ProjectHash,Settings;
    }
    internal sealed class AHistorySelection
    {
        internal double[,] Parameters=new double[0,36],Raw=new double[0,0],Filtered=new double[0,0],Equivalent=new double[0,0];
        internal string State="disabled",Detail="",Task28="{}";
        internal int CandidateRows,QualifiedRows;
        internal string[] Sources=new string[0],RowSources=new string[0];
        internal string Json()
        {
            return new JavaScriptSerializer().Serialize(new{policy=AHistoryEvidence.Policy,state=State,detail=Detail,
                input_rows=CandidateRows,history_rows=QualifiedRows,applied=QualifiedRows>0,
                display_filter_used=false,acceptance_modified=false,physical_truth=false,
                task28_strength=40,task28_column22="NaN",task28_diagnostics=Task28,
                source_receipts=Sources,history_row_sources=RowSources,provenance_semantics="recorded_acquisition_identity_not_signed_physical_truth"});
        }
    }
    internal sealed class AHistoryBatch
    {
        internal readonly AHistoryReceipt[] Receipts;
        internal readonly string[] TargetPaths,TargetAcquisitions;
        internal readonly string Failure;
        internal readonly double[,] ReviewSnapshot;
        internal readonly bool[] RemovedSnapshot;
        string[] targetWaves;
        internal CancellationToken Cancellation;
        internal AHistoryBatch(AHistoryReceipt[] receipts,string[] paths,string[] acquisitions,string failure,double[,] reviewSnapshot=null,bool[] removedSnapshot=null)
        {Receipts=receipts;TargetPaths=paths;TargetAcquisitions=acquisitions;Failure=failure;ReviewSnapshot=reviewSnapshot;RemovedSnapshot=removedSnapshot;}
        internal string[] TargetWaves(string project)
        {
            // All requests in a batch share this immutable exclusion set. No
            // just-computed result can become evidence for the next file.
            lock(this){if(targetWaves==null){var hashes=new List<string>();foreach(string path in TargetPaths){Cancellation.ThrowIfCancellationRequested();hashes.Add(AHistoryEvidence.WaveHash(path,project));}targetWaves=hashes.ToArray();}return targetWaves;}
        }
    }
    internal static class AHistoryEvidence
    {
        internal const string Policy="A-history-Task28-v1;strength40;interior;native-accepted>=0.70;recorded-acquisition";
        internal const long MaximumCells=8000000;
        internal const int MaximumRows=4096;
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
        internal static string Root(string path)
        {return System.IO.Path.GetFullPath(File.Exists(path)?System.IO.Path.GetDirectoryName(path):path).TrimEnd(System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar);}
        internal static string Hash(string path)
        {RejectReparse(path);using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
        static void RejectReparse(string path)
        {
            string cursor=System.IO.Path.GetFullPath(path);
            while(!String.IsNullOrEmpty(cursor)){
                if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked history source is not eligible.");
                cursor=System.IO.Path.GetDirectoryName(cursor);
            }
        }
        static void RejectDuplicateJsonKeys(string json,JavaScriptSerializer serializer)
        {
            var objects=new Stack<HashSet<string>>();
            for(int i=0;i<json.Length;i++){
                if(json[i]=='{'){objects.Push(new HashSet<string>(StringComparer.Ordinal));continue;}
                if(json[i]=='}'){objects.Pop();continue;}
                if(json[i]!='"')continue;
                int start=i++;
                for(;i<json.Length;i++){if(json[i]=='\\'){i++;continue;}if(json[i]=='"')break;}
                int next=i+1;while(next<json.Length&&Char.IsWhiteSpace(json[next]))next++;
                if(next<json.Length&&json[next]==':'&&objects.Count>0){
                    string key=serializer.Deserialize<string>(json.Substring(start,i-start+1));
                    if(!objects.Peek().Add(key))throw new InvalidDataException("Repeated acquisition metadata JSON field.");
                }
            }
        }
        internal static string MetadataAcquisition(string path)
        {
            // Only the existing pressure writer's bounded, explicit metadata is
            // recognized. An old filename/header without this field is unknown.
            RejectReparse(path);
            using(var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read),new UTF8Encoding(false,true),true)){
                string identity=null;int total=0;
                for(int i=0;i<11;i++){
                    var line=new StringBuilder();int ch;
                    int read=0;
                    while((ch=reader.Read())>=0){
                        if(++read>16384||++total>65536)throw new InvalidDataException("Acquisition header exceeds limit.");
                        if(ch=='\n')break;
                        if(ch=='\r'){if(reader.Peek()!='\n')throw new InvalidDataException("Invalid acquisition header line ending.");continue;}
                        line.Append((char)ch);
                    }
                    if(ch<0&&line.Length==0)return "";
                    if(i==0&&!line.ToString().StartsWith("HVX LOC cycle record / engineering pC x 100000",StringComparison.Ordinal))return "";
                    const string key="Acquisition context\t";string text=line.ToString();if(!text.StartsWith(key,StringComparison.Ordinal))continue;
                    if(identity!=null)throw new InvalidDataException("Repeated acquisition metadata.");
                    var serializer=new JavaScriptSerializer{MaxJsonLength=16384,RecursionLimit=8};
                    string json=text.Substring(key.Length);
                    var data=serializer.DeserializeObject(json) as Dictionary<string,object>;
                    RejectDuplicateJsonKeys(json,serializer);
                    object id,sim;Guid parsed;
                    if(data==null||!data.TryGetValue("simulation",out sim)||!(sim is bool)||(bool)sim||
                        !data.TryGetValue("locationAcquisitionId",out id)||!(id is string)||!Guid.TryParseExact((string)id,"N",out parsed))return "";
                    identity=parsed.ToString("N");
                }return identity??"";
            }
        }
        internal static string WaveHash(string path,string project)
        {
            RejectReparse(path);RejectReparse(project);
            using(var guard=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
                var data=ReviewLocationFileReader.Read(path,project);
                using(var h=SHA256.Create()){
                    byte[] bytes=new byte[65536];
                    for(int offset=0;offset<data.ChargeSamples.Length;){int count=Math.Min(bytes.Length/8,data.ChargeSamples.Length-offset);
                        Buffer.BlockCopy(data.ChargeSamples,offset*8,bytes,0,count*8);h.TransformBlock(bytes,0,count*8,null,0);offset+=count;}
                    h.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(h.Hash).Replace("-","");
                }
            }
        }
        static string ProjectFingerprint(string project)
        {
            RejectReparse(project);
            var paths=Directory.GetFiles(project).Where(p=>p.EndsWith(".cal",StringComparison.OrdinalIgnoreCase)||
                String.Equals(System.IO.Path.GetFileName(p),"ITEM.in",StringComparison.OrdinalIgnoreCase)||
                String.Equals(System.IO.Path.GetFileName(p),"CABLE.in",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase);
            string text=String.Join("\n",paths.Select(p=>System.IO.Path.GetFileName(p).ToUpperInvariant()+":"+Hash(p)))+"\nengine:"+Hash(ReviewAnalysisEngine.ExpectedAlgorithmPath);
            using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","");
        }
        internal static string Settings(ReviewAnalysisRequest r)
        {
            // Source filename/LOC ID/phase are identities, not algorithm options.
            var values=new object[]{r.EndpointPolicy,r.AlgorithmIndex,r.MaxPulses,r.MultipleReflectionSuppressionCount,
                r.ManualThresholdEnabled,r.ManualThreshold,r.AutomaticThresholdMultiplier,r.AttenuationTolerancePercent,
                r.CorrelationErrorPercent,r.ConnectingCableLengthM,r.SampleRateHz,r.OscillationFrequencyHz,
                r.NoiseSimilarityTolerancePercent,r.ApplyLinearFilter,r.UseFirstArrivalTiming,
                r.LocationSuspiciousMinimumPercent,r.LocationManualAcceptanceMinimumPercent,r.LocationAutomaticAcceptanceMinimumPercent};
            return String.Join("|",values.Select(v=>Convert.ToString(v,CultureInfo.InvariantCulture)))+"|"+
                String.Join(",",r.PhaseQuadrants.Select(v=>v?"1":"0"))+"|"+
                String.Join(",",r.LinearFilterCutoffsHz.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
        }
        internal static AHistoryIdentity Identify(ReviewAnalysisRequest r)
        {
            if(String.IsNullOrWhiteSpace(r.AHistoryAcquisition))throw new InvalidDataException("Missing recorded acquisition identity; history auxiliary not enabled.");
            if(r.AHistoryRequireHeaderIdentity&&MetadataAcquisition(r.LocationFilePath)!=r.AHistoryAcquisition)throw new InvalidDataException("Acquisition identity changed after batch capture.");
            string project=Root(r.ProjectPath),path=System.IO.Path.GetFullPath(r.LocationFilePath);
            if(!path.StartsWith(project+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("History source is outside project.");
            return new AHistoryIdentity{Project=project,Path=path,Acquisition=r.AHistoryAcquisition,FileHash=Hash(path),
                WaveHash=WaveHash(path,project),ProjectHash=ProjectFingerprint(project),Settings=Settings(r)};
        }
        internal static bool Anchor(double[,] p,int r)
        {
            double length=p[r,5],x=p[r,13],epsilon=Math.Max(1e-9,1e-12*length);
            return p[r,35]==1&&Finite(p[r,33])&&p[r,33]>=.70&&p[r,33]<=1&&p[r,0]>=1&&p[r,0]<=3&&p[r,0]==Math.Floor(p[r,0])&&
                Finite(length)&&length>0&&Finite(x)&&x>epsilon&&length-x>epsilon&&
                Finite(p[r,2])&&p[r,2]>0&&Finite(p[r,15])&&p[r,15]>0&&Finite(p[r,28])&&p[r,28]>0&&
                Finite(p[r,6])&&p[r,6]>0&&Finite(p[r,7])&&p[r,7]>0&&Finite(p[r,10])&&Finite(p[r,11])&&p[r,11]>p[r,10];
        }
        internal static AHistoryReceipt Capture(AHistoryIdentity identity,ReviewAnalysisResult result)
        {
            if(identity==null)return null;
            ReviewAnalysisEngine.ValidateOutput(result,MaximumRows);
            int[] rows=Enumerable.Range(0,result.Parameters.GetLength(0)).Where(i=>Anchor(result.Parameters,i)).ToArray();
            if(rows.Length==0)return null;
            long cells=(long)rows.Length*(36+result.Raw.GetLength(1)+result.Filtered.GetLength(1)+result.Equivalent.GetLength(1));
            if(rows.Length>MaximumRows||cells>MaximumCells)throw new InvalidDataException("History waveform budget exceeded; no partial history retained.");
            return new AHistoryReceipt(identity,result,rows);
        }
        internal static void VerifyUnchanged(AHistoryIdentity identity)
        {
            if(identity==null)return;
            if(Hash(identity.Path)!=identity.FileHash||ProjectFingerprint(identity.Project)!=identity.ProjectHash)
                throw new InvalidDataException("Source/project/calibration changed during analysis; history provenance was not retained.");
        }
        internal static double[,] CopyRows(double[,] source,int[] rows)
        {var copy=new double[rows.Length,source.GetLength(1)];for(int i=0;i<rows.Length;i++)for(int c=0;c<copy.GetLength(1);c++)copy[i,c]=source[rows[i],c];return copy;}
        static double[,] Join(List<Tuple<AHistoryReceipt,int>> rows,Func<AHistoryReceipt,double[,]> get,int minimumWidth)
        {
            int width=rows.Count==0?minimumWidth:Math.Max(minimumWidth,rows.Max(row=>get(row.Item1).GetLength(1)));
            var result=new double[rows.Count,width];
            for(int i=0;i<rows.Count;i++){var source=get(rows[i].Item1);for(int c=0;c<width;c++)result[i,c]=c<source.GetLength(1)?source[rows[i].Item2,c]:Double.NaN;}
            return result;
        }
        internal static AHistorySelection Select(AHistoryBatch batch,AHistoryIdentity current,int phase)
        {
            var result=new AHistorySelection();
            if(batch==null)return result;
            if(batch.Failure.Length>0){result.State="unavailable";result.Detail=batch.Failure;return result;}
            if(current==null||phase<1||phase>3){result.State="unavailable";result.Detail="Missing current acquisition/phase provenance.";return result;}
            string[] capturedWaves=batch.TargetWaves(current.Project);
            int targetIndex=Array.FindIndex(batch.TargetPaths,p=>String.Equals(p,current.Path,StringComparison.OrdinalIgnoreCase));
            if(targetIndex<0||capturedWaves[targetIndex]!=current.WaveHash||batch.TargetAcquisitions[targetIndex]!=current.Acquisition){result.State="unavailable";result.Detail="Target provenance changed after batch freeze.";return result;}
            var targetWaves=new HashSet<string>(capturedWaves,StringComparer.Ordinal);
            var blockedGroups=new HashSet<string>(batch.TargetAcquisitions,StringComparer.Ordinal);
            var blockedWaves=new HashSet<string>(targetWaves,StringComparer.Ordinal);
            // Propagate OR-equivalence to closure: A/groupX/waveY and B/groupZ/waveY
            // are one acquisition family; excluding A must also exclude B.
            bool changed;do{changed=false;foreach(var r in batch.Receipts)
                if(blockedGroups.Contains(r.Acquisition)||blockedWaves.Contains(r.WaveHash)||batch.TargetPaths.Contains(r.Path,StringComparer.OrdinalIgnoreCase)){
                    changed|=blockedGroups.Add(r.Acquisition);changed|=blockedWaves.Add(r.WaveHash);}}while(changed);
            var candidates=batch.Receipts.Where(r=>String.Equals(r.Project,current.Project,StringComparison.OrdinalIgnoreCase)&&
                r.ProjectHash==current.ProjectHash&&r.Settings==current.Settings&&!blockedGroups.Contains(r.Acquisition)&&!blockedWaves.Contains(r.WaveHash)).ToArray();
            if(batch.ReviewSnapshot!=null){var unchanged=UnchangedRows(batch.ReviewSnapshot,batch.RemovedSnapshot);candidates=candidates.Where(r=>Enumerable.Range(0,r.Parameters.GetLength(0)).All(i=>unchanged.Contains(RowKey(r.Parameters,i)))).ToArray();}
            // Connected families are never multiple LOC votes. Conservative
            // exclusion of every repeated family also avoids choosing a lucky alias.
            candidates=candidates.Where(r=>candidates.Count(x=>x.Acquisition==r.Acquisition)==1&&candidates.Count(x=>x.WaveHash==r.WaveHash)==1).ToArray();
            var rows=new List<Tuple<AHistoryReceipt,int>>();
            int changedSources=0;
            foreach(var r in candidates){
                batch.Cancellation.ThrowIfCancellationRequested();
                if(!File.Exists(r.Path)||Hash(r.Path)!=r.FileHash){changedSources++;continue;}
                for(int i=0;i<r.Parameters.GetLength(0);i++)if(r.Parameters[i,0]==phase&&Anchor(r.Parameters,i))rows.Add(Tuple.Create(r,i));
            }
            if(rows.Count>MaximumRows)throw new InvalidDataException("History row budget exceeded.");
            result.CandidateRows=rows.Count;
            // Do not pool different propagation geometries under Task28's wider
            // same-phase tolerance. Exact equality is intentionally conservative.
            var kept=new List<Tuple<AHistoryReceipt,int>>();var diagnostics=new List<string>();
            foreach(var group in rows.GroupBy(x=>String.Join("|",new[]{5,6,7}.Select(c=>x.Item1.Parameters[x.Item2,c].ToString("R",CultureInfo.InvariantCulture))))){
                batch.Cancellation.ThrowIfCancellationRequested();
                var items=group.ToList();var input=Join(items,r=>r.Parameters,36);
                // Replace record aliases only in the private scoring copy. Each
                // recorded, deduplicated acquisition supplies one LOC vote.
                var ids=items.Select(x=>x.Item1.Acquisition).Distinct().OrderBy(x=>x,StringComparer.Ordinal).Select((v,i)=>new{v,i}).ToDictionary(x=>x.v,x=>x.i);
                for(int i=0;i<input.GetLength(0);i++){input[i,1]=ids[items[i].Item1.Acquisition];input[i,22]=Double.NaN;}
                double[,] retained,gated,scores,summary;int[] indices;string status;
                new LocationClusterFilterEngine().FilterLocationMatrixAdvanced(input,40,-1,3,false,false,out retained,out gated,out indices,out scores,out summary,out status);
                foreach(int index in indices)kept.Add(items[index]);diagnostics.Add(status);
            }
            long totalCells=kept.Count==0?0:(long)kept.Count*(36+kept.Max(x=>x.Item1.Raw.GetLength(1))+kept.Max(x=>x.Item1.Filtered.GetLength(1))+kept.Max(x=>x.Item1.Equivalent.GetLength(1)));
            if(totalCells>MaximumCells)throw new InvalidDataException("Combined history waveform budget exceeded; auxiliary disabled.");
            result.Parameters=Join(kept,r=>r.Parameters,36);result.Raw=Join(kept,r=>r.Raw,0);
            result.Filtered=Join(kept,r=>r.Filtered,0);result.Equivalent=Join(kept,r=>r.Equivalent,0);
            result.QualifiedRows=kept.Count;result.State=kept.Count>0?"applied":"no_qualified_history";
            result.Sources=kept.Select(x=>x.Item1).Distinct().Select(r=>System.IO.Path.GetFileName(r.Path)+"|acquisition="+r.Acquisition+"|file="+r.FileHash+"|wave="+r.WaveHash+"|project="+r.ProjectHash).ToArray();
            result.RowSources=kept.Select(x=>x.Item1.FileHash+"|source_row="+x.Item2+"|LOC="+x.Item1.Parameters[x.Item2,1].ToString("R",CultureInfo.InvariantCulture)+"|PDmap="+x.Item1.Parameters[x.Item2,9].ToString("R",CultureInfo.InvariantCulture)).ToArray();
            if(changedSources>0){result.Detail="Missing/changed historical source records excluded: "+changedSources;if(kept.Count==0)result.State="unavailable";}
            result.Task28="["+String.Join(",",diagnostics)+"]";return result;
        }
        internal static string AppendStatus(string status,AHistorySelection selection)
        {
            string json=(status??"").Trim();if(!json.StartsWith("{",StringComparison.Ordinal)||!json.EndsWith("}",StringComparison.Ordinal))return status;
            return json.Substring(0,json.Length-1)+",\"host_a_history_auxiliary\":"+selection.Json()+"}";
        }
        static string RowKey(double[,] matrix,int row)
        {return String.Join("|",Enumerable.Range(0,36).Where(c=>c!=9).Select(c=>matrix[row,c].ToString("R",CultureInfo.InvariantCulture)));}
        static HashSet<string> UnchangedRows(double[,] matrix,bool[] removed)
        {var set=new HashSet<string>(StringComparer.Ordinal);for(int r=0;r<matrix.GetLength(0);r++)if(matrix[r,35]==1&&(removed==null||removed.Length!=matrix.GetLength(0)||!removed[r]))set.Add(RowKey(matrix,r));return set;}
    }

    public sealed partial class PdReviewControl
    {
        bool aHistoryEnabled;
        readonly List<AHistoryReceipt> aHistoryReceipts=new List<AHistoryReceipt>();
        double[,] aHistoryWorkspace;
        CheckBox aHistoryCheck;
        Label aHistoryStateLabel;
        string aHistoryState="disabled";
        [Category("Analysis"),DefaultValue(false),Description("Session-only A review-candidate history auxiliary. Requires recorded acquisition provenance; never grants physical acceptance.")]
        public bool AHistoryAuxiliaryEnabled
        {
            get{return aHistoryEnabled;}
            set{if(analysisBusy)throw new InvalidOperationException("Wait for analysis before changing history auxiliary.");if(aHistoryEnabled==value)return;
                aHistoryEnabled=value;aHistoryReceipts.Clear();aHistoryWorkspace=original;aHistoryState=value?"awaiting_history":"disabled";
                AHistoryAuxiliaryStatus=value?"Enabled; awaiting newly analyzed, provenance-verified A records.":"Disabled";MarkAnalysisDirty();SyncAHistoryAuxiliaryUi();}
        }
        // Host-supplied acquisition GROUP identity from original acquisition
        // metadata, not filename/row ID/hash or a user acceptance checkbox.
        [Browsable(false)] public Func<string,string> AHistoryAcquisitionIdentityProvider {get;set;}=AHistoryEvidence.MetadataAcquisition;
        [Browsable(false)] public string AHistoryAuxiliaryStatus {get;private set;}="Disabled";
        void PrepareAHistoryBatch(List<ReviewAnalysisRequest> requests)
        {
            if(capacitorProject||!aHistoryEnabled)return;
            if(!ReferenceEquals(aHistoryWorkspace,original)){aHistoryReceipts.Clear();aHistoryWorkspace=original;}
            var relevant=requests.Where(r=>r.AlgorithmIndex==5).ToArray();if(relevant.Length==0)return;
            string failure="";var identities=new List<string>();var provider=AHistoryAcquisitionIdentityProvider;
            foreach(var r in relevant){string identity="";try{identity=provider==null?"":provider(r.LocationFilePath)??"";}catch(Exception ex){if(IsFatalHostException(ex))throw;failure="Acquisition provenance provider failed.";}
                if(String.IsNullOrWhiteSpace(identity)||identity.Length>512)failure="Missing recorded acquisition provenance; history auxiliary not enabled.";
                identities.Add(identity);r.AHistoryAcquisition=identity;r.AHistoryRequireHeaderIdentity=provider==(Func<string,string>)AHistoryEvidence.MetadataAcquisition;}
            if(working.LongLength>AHistoryEvidence.MaximumCells)failure="Review snapshot memory budget exceeded; history auxiliary not enabled.";
            var receipts=aHistoryReceipts.ToArray();
            var batch=new AHistoryBatch(receipts,relevant.Select(r=>Path.GetFullPath(r.LocationFilePath)).ToArray(),identities.ToArray(),failure,
                failure.Length==0?(double[,])working.Clone():null,failure.Length==0?(bool[])removed.Clone():null);
            foreach(var r in relevant)r.AHistoryBatch=batch;
            AHistoryAuxiliaryStatus=failure.Length>0?failure:"Enabled; frozen history records="+receipts.Length;
            aHistoryState=failure.Length>0?"unavailable":"awaiting_analysis";SyncAHistoryAuxiliaryUi();
        }
        void InitializeAHistoryAuxiliaryUi()
        {
            aHistoryCheck=new CheckBox{Name="ReviewAHistoryAuxiliary",Visible=false,TabStop=false,Width=156,Height=40,Margin=new Padding(0,4,0,0)};
            aHistoryStateLabel=new Label{Name="ReviewAHistoryAuxiliaryState",Visible=false,Width=156,Height=48,ForeColor=Color.DimGray,Margin=Padding.Empty};
            side.Controls.Add(aHistoryCheck);side.Controls.Add(aHistoryStateLabel);
            aHistoryCheck.CheckedChanged+=(s,e)=>{if(!uiSync)AHistoryAuxiliaryEnabled=aHistoryCheck.Checked;};SyncAHistoryAuxiliaryUi();
        }
        void SyncAHistoryAuxiliaryUi()
        {
            if(aHistoryCheck==null)return;bool prior=uiSync;uiSync=true;
            try{aHistoryCheck.Visible=aHistoryStateLabel.Visible=false;
                aHistoryCheck.Checked=aHistoryEnabled;aHistoryCheck.Enabled=!analysisBusy&&AnalysisAlgorithmIndex==5;
                aHistoryCheck.Text=L("Use previous records","参考历史定位");
                aHistoryCheck.AccessibleDescription=L("Use eligible earlier records to help compare locations. Review decisions are not changed.","参考来源一致的历史记录辅助比较定位位置，不改变已确认的结果。");
                aHistoryStateLabel.Text=!aHistoryEnabled?L("Off","已关闭"):
                    AnalysisAlgorithmIndex!=5?L("Not available for this method","当前定位方法不支持"):
                    aHistoryState=="applied"?L("Previous records used","已参考历史定位"):
                    aHistoryState=="unavailable"?L("History unavailable; analyzing current record","历史不可用，使用当前数据"):
                    L("No usable history yet","暂无可用历史记录");}
            finally{uiSync=prior;}
        }
        void CommitAHistoryReceipts(IEnumerable<ReviewAnalysisResult> results)
        {
            if(!aHistoryEnabled)return;
            var completed=results.ToArray();
            foreach(var result in completed){var receipt=result.AHistoryReceipt;if(receipt==null)continue;
                aHistoryReceipts.RemoveAll(r=>String.Equals(r.Path,receipt.Path,StringComparison.OrdinalIgnoreCase));aHistoryReceipts.Add(receipt);}
            bool overBudget=aHistoryReceipts.Count>256||aHistoryReceipts.Sum(r=>r.Cells)>AHistoryEvidence.MaximumCells;
            if(overBudget)aHistoryReceipts.Clear();
            aHistoryWorkspace=original;
            var selections=completed.Where(r=>r.AHistorySelection!=null).Select(r=>r.AHistorySelection).ToArray();
            if(selections.Length>0){aHistoryState=selections.Any(s=>s.QualifiedRows>0)?"applied":selections.Any(s=>s.State=="unavailable")?"unavailable":"no_qualified_history";
                AHistoryAuxiliaryStatus=String.Join("\n",selections.Select(s=>s.Json()));}SyncAHistoryAuxiliaryUi();
            if(overBudget){aHistoryState="unavailable";AHistoryAuxiliaryStatus="History evidence memory budget exceeded; no partial history retained.";SyncAHistoryAuxiliaryUi();}
        }
    }
}
