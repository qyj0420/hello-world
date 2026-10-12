using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using System.Web.Script.Serialization;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    // One pressure page is one trial. Reconfiguring its phase/backend preserves
    // the trial; leaving the page ends it. Saved project files are never deleted.
    internal sealed class PressureAnalysisTrial
    {
        readonly List<string> rounds = new List<string>();
        readonly HashSet<string> committed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> acquisitions = new Dictionary<string,string>(StringComparer.Ordinal);
        readonly Dictionary<string,ThresholdChoice> thresholds=new Dictionary<string,ThresholdChoice>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> sourceHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> companionHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal sealed class ThresholdChoice {internal readonly bool Manual;internal readonly double Pc;internal ThresholdChoice(bool manual,double pc){Manual=manual;Pc=pc;}}
        internal readonly string Project;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal PressureBackgroundAnalysisQueue AnalysisQueue { get; private set; } = new PressureBackgroundAnalysisQueue();
        internal bool Ended { get; private set; }

        internal PressureAnalysisTrial(string project)
        {
            Project = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal bool IsForProject(string project)
        {
            return !Ended && String.Equals(Project,
                Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        internal bool HasCommitted(string acquisitionId)
        {
            string path;
            if(Ended)return false;
            if(!acquisitions.TryGetValue(acquisitionId??"",out path))return false;
            if(!File.Exists(path))throw new InvalidDataException("本轮已保存的测试记录已丢失，请核对项目文件。");
            HVX.Storage.PressureRecordPublication.RequireReadable(path);
            return true;
        }

        internal string CommittedPath(string acquisitionId)
        {
            if(!HasCommitted(acquisitionId))throw new InvalidDataException("本轮尚未保存到项目，不能进行后续轮次确认。");
            return acquisitions[acquisitionId];
        }

        internal bool Commit(string acquisitionId,string path)
        {
            if (Ended) throw new InvalidOperationException("本次试验已结束，不能再加入累计分析。");
            return CommitPrepared(PrepareCommit(acquisitionId,path));
        }

        // Only immutable project/path inputs enter the file worker. Trial
        // dictionaries and lifecycle state remain on their original UI owner.
        internal Task<PreparedCommit> PrepareCommitAsync(string acquisitionId,string path)
        {
            if(Ended)throw new InvalidOperationException("本次试验已结束，不能再加入累计分析。");
            return Task.Run(()=>PrepareCommit(acquisitionId,path));
        }
        internal sealed class PreparedCommit
        {
            internal readonly PressureAnalysisTrial Owner;
            internal readonly string AcquisitionId,Path,SourceHash,Companion,CompanionHash,SourceStamp,CompanionStamp;
            internal PreparedCommit(PressureAnalysisTrial owner,string id,string path,string hash,string companion,string companionHash,string sourceStamp,string companionStamp)
            {Owner=owner;AcquisitionId=id;Path=path;SourceHash=hash;Companion=companion;CompanionHash=companionHash;SourceStamp=sourceStamp;CompanionStamp=companionStamp;}
        }
        PreparedCommit PrepareCommit(string acquisitionId,string path)
        {
            if(String.IsNullOrWhiteSpace(acquisitionId))throw new InvalidDataException("本轮采集标识缺失。");
            string full = Path.GetFullPath(path);
            bool capacitor=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(Project);
            if (!full.StartsWith(Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetExtension(full), capacitor?".dem":".loc", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidDataException("仅当前项目已完整保存的适用测试记录可以加入本次试验。");
            HVX.Storage.PressureRecordPublication.RequireReadable(full);
            if(capacitor)RequireCapacitorDetectedRecord(full,acquisitionId);
            string sourceHash,sourceStamp=RecordStamp(full);
            using(var stream=File.OpenRead(full))using(var sha=SHA256.Create())sourceHash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
            string companion=capacitor?null:Path.ChangeExtension(full,".dem"),companionHash=null,companionStamp=null;
            if(companion!=null&&File.Exists(companion)){
                companionStamp=RecordStamp(companion);
                RequireRoundFileIdentity(companion,acquisitionId,12,"Acquisition context:\t");
                companionHash=CurrentRecordHash(companion);
            }
            if(RecordStamp(full)!=sourceStamp||companionHash!=null&&RecordStamp(companion)!=companionStamp)throw new InvalidDataException("保存校验期间记录已改变，未加入累计分析。");
            return new PreparedCommit(this,acquisitionId,full,sourceHash,companion,companionHash,sourceStamp,companionStamp);
        }
        internal bool CommitPrepared(PreparedCommit prepared)
        {
            if(Ended)throw new InvalidOperationException("本次试验已结束，不能再加入累计分析。");
            if(prepared==null||!Object.ReferenceEquals(prepared.Owner,this))throw new InvalidOperationException("本轮保存凭据不属于当前试验。");
            string acquisitionId=prepared.AcquisitionId,full=prepared.Path;
            if(!File.Exists(full)||prepared.CompanionHash!=null&&!File.Exists(prepared.Companion))throw new InvalidDataException("本轮已保存的测试记录已丢失，请核对项目文件。");
            if(RecordStamp(full)!=prepared.SourceStamp||prepared.CompanionHash!=null&&RecordStamp(prepared.Companion)!=prepared.CompanionStamp)throw new InvalidDataException("保存校验后记录已改变，未加入累计分析。");
            HVX.Storage.PressureRecordPublication.RequireReadable(full);
            if (HasCommitted(acquisitionId) || committed.Contains(full)) return false;
            // Hash first: an I/O failure must not leave a partially committed
            // identity that a later callback could treat as a successful save.
            committed.Add(full);
            acquisitions.Add(acquisitionId,full);
            rounds.Add(full);
            sourceHashes.Add(full,prepared.SourceHash);
            if(prepared.CompanionHash!=null)companionHashes.Add(prepared.Companion,prepared.CompanionHash);
            return true;
        }

        static string RecordStamp(string path)
        {
            var info=new FileInfo(path);
            if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("测试记录路径无法核验。");
            return info.Length.ToString(CultureInfo.InvariantCulture)+":"+info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        static string CurrentRecordHash(string path)
        {
            var info=new FileInfo(path);
            if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("测试记录路径无法核验。");
            using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
        }
        static void RequireRoundFileIdentity(string path,string acquisitionId,int contextLine,string prefix)
        {
            using(var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))){
                string line=null;for(int i=0;i<=contextLine;i++){line=reader.ReadLine();if(line==null||line.Length>65536)throw new InvalidDataException("本轮记录不完整。");}
                if(!line.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("本轮记录来源无法核验。");
                var context=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(line.Substring(prefix.Length));
                object id;if(context==null||!context.TryGetValue("locationAcquisitionId",out id)||!Object.Equals(id,acquisitionId))throw new InvalidDataException("测试记录不属于当前轮次。");
            }
        }
        // Exact current-round recovery only. No recursive deletion, no CAL/raw
        // movement, and no historical record inferred from a similar filename.
        internal string RecoverCommittedRound(string acquisitionId,Func<bool> current)
        {
            if(current==null||!current())throw new InvalidOperationException("当前轮次已改变，未删除记录。");
            string path=CommittedPath(acquisitionId);
            if(!String.Equals(Path.GetDirectoryName(path),Project,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("仅可删除当前项目的本轮记录。");
            bool capacitor=String.Equals(Path.GetExtension(path),".dem",StringComparison.OrdinalIgnoreCase);
            RequireRoundFileIdentity(path,acquisitionId,capacitor?12:9,capacitor?"Acquisition context:\t":"Acquisition context\t");
            var targets=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{path,sourceHashes[path]}};
            string companion=capacitor?null:Path.ChangeExtension(path,".dem"),hash;
            if(companion!=null){
                if(!companionHashes.TryGetValue(companion,out hash))throw new InvalidDataException("本轮配对记录未核验，未删除。");
                RequireRoundFileIdentity(companion,acquisitionId,12,"Acquisition context:\t");targets.Add(companion,hash);
            }
            foreach(var target in targets)if(CurrentRecordHash(target.Key)!=target.Value)throw new InvalidDataException("本轮文件已改变，未删除记录。");
            HVX.Storage.PressureRecordPublication.RequireReadable(path);
            string parent=Path.Combine(Project,".pressure-deleted");
            if((new DirectoryInfo(Project).Attributes&FileAttributes.ReparsePoint)!=0||Directory.Exists(parent)&&(new DirectoryInfo(parent).Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("恢复目录路径无法核验。");
            if(!current())throw new InvalidOperationException("当前轮次已改变，未删除记录。");
            Directory.CreateDirectory(parent);string recovery=Path.Combine(parent,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery);
            var moved=new List<string>();
            try{
                foreach(var target in targets){
                    if(!current())throw new InvalidOperationException("当前轮次已改变，未删除记录。");
                    if(CurrentRecordHash(target.Key)!=target.Value)throw new InvalidDataException("本轮文件已改变，未删除记录。");
                    File.Move(target.Key,Path.Combine(recovery,Path.GetFileName(target.Key)));moved.Add(target.Key);
                }
                if(!current())throw new InvalidOperationException("当前轮次已改变，未删除记录。");
                File.WriteAllText(Path.Combine(recovery,"recovery.json"),new JavaScriptSerializer().Serialize(new Dictionary<string,object>{
                    {"schema","HVX.Pressure.CurrentRoundRecovery/1"},{"acquisitionId",acquisitionId},{"originalFiles",targets},{"movedUtc",DateTime.UtcNow.ToString("O")}}),new System.Text.UTF8Encoding(false));
            }catch{
                // Best-effort transaction rollback; a failed rollback remains
                // recoverable and never reports a successful deletion ACK.
                for(int i=moved.Count-1;i>=0;i--){string source=Path.Combine(recovery,Path.GetFileName(moved[i]));if(File.Exists(source)&&!File.Exists(moved[i]))File.Move(source,moved[i]);}
                throw;
            }
            acquisitions.Remove(acquisitionId);committed.Remove(path);rounds.Remove(path);sourceHashes.Remove(path);thresholds.Remove(path);
            if(companion!=null)companionHashes.Remove(companion);
            return recovery;
        }

        static void RequireCapacitorDetectedRecord(string path,string acquisitionId)
        {
            // A capacitor ACK binds the actual formal DEM transaction, not a
            // made-up location file or merely an existing filename.
            using(var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))){
                var headers=new string[16];
                for(int i=0;i<headers.Length;i++){
                    headers[i]=reader.ReadLine();
                    if(headers[i]==null||headers[i].Length>65536)throw new InvalidDataException("电容测试记录表头不完整。");
                }
                int count;double step;
                if(!headers[7].StartsWith("Number of Samples:\t",StringComparison.Ordinal)||
                    !Int32.TryParse(headers[7].Substring(19),NumberStyles.Integer,CultureInfo.InvariantCulture,out count)||count<16||count>4800||
                    !headers[8].StartsWith("Delta-x [s]:\t",StringComparison.Ordinal)||
                    !Double.TryParse(headers[8].Substring(13),NumberStyles.Float,CultureInfo.InvariantCulture,out step)||step!=1d/80000||
                    headers[15]!="PD (pC):\tVoltage (kV):"||!headers[12].StartsWith("Acquisition context:\t",StringComparison.Ordinal))
                    throw new InvalidDataException("电容测试记录采样表头无效。");
                var context=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=8}
                    .Deserialize<Dictionary<string,object>>(headers[12].Substring(21));
                object kind,id;
                if(context==null||!context.TryGetValue("measurementKind",out kind)||!Object.Equals(kind,"Capacitor")||
                    !context.TryGetValue("locationAcquisitionId",out id)||!Object.Equals(id,acquisitionId))
                    throw new InvalidDataException("电容测试记录与本轮采集身份不符。");
                for(int i=0;i<count;i++){
                    string line=reader.ReadLine();double pd,v;var cells=line==null?new string[0]:line.Split('\t');
                    if(cells.Length!=2||!Double.TryParse(cells[0],NumberStyles.Float,CultureInfo.InvariantCulture,out pd)||
                        !Double.TryParse(cells[1],NumberStyles.Float,CultureInfo.InvariantCulture,out v)||pd<0||
                        Double.IsNaN(pd)||Double.IsInfinity(pd)||Double.IsNaN(v)||Double.IsInfinity(v))
                        throw new InvalidDataException("电容测试记录数据不完整或无效。");
                }
                if(reader.ReadLine()!=null)throw new InvalidDataException("电容测试记录样本数不符。");
            }
        }

        internal string[] Snapshot()
        {
            if (Ended) throw new InvalidOperationException("本次试验已结束。");
            return rounds.ToArray();
        }

        internal Dictionary<string,ThresholdChoice> SnapshotThresholds(string current,bool manual,double pc,bool all)
        {
            if(Ended)throw new InvalidOperationException("本次试验已结束。");
            if(Double.IsNaN(pc)||Double.IsInfinity(pc)||pc<0||pc>1e9)throw new ArgumentOutOfRangeException("pc");
            string full=Path.GetFullPath(current);
            if(!committed.Contains(full))throw new InvalidOperationException("只能设置本次试验已保存轮次的分析阈值。");
            foreach(string path in rounds)
                if(all||String.Equals(path,full,StringComparison.OrdinalIgnoreCase))thresholds[path]=new ThresholdChoice(manual,pc);
                else if(!thresholds.ContainsKey(path))thresholds[path]=new ThresholdChoice(false,100);
            return new Dictionary<string,ThresholdChoice>(thresholds,StringComparer.OrdinalIgnoreCase);
        }

        internal Dictionary<string,string> SnapshotHashes()
        {
            if(Ended)throw new InvalidOperationException("本次试验已结束。");
            return new Dictionary<string,string>(sourceHashes,StringComparer.OrdinalIgnoreCase);
        }

        // Cancel only managed analysis. Committed identities, input hashes and
        // operator thresholds survive so an explicit later round can recompute
        // the complete saved trial. Old jobs retain their own revoked queue.
        internal void CancelAnalysis()
        {
            if(Ended)return;
            var old=AnalysisQueue;AnalysisQueue=new PressureBackgroundAnalysisQueue();old.End();
        }
        internal void End() { Ended = true; AnalysisQueue.End(); }
    }
}
