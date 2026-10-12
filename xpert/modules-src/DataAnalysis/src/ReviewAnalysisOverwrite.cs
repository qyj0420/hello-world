using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        string loadedAnalysisFingerprint="";
        [Browsable(false)] public string LastAnalysisBackupPath {get;private set;}="";
        [Browsable(false)] public bool CanOverwriteLoadedAnalysis
        {get{return !String.IsNullOrEmpty(LoadedAnalysisId)&&!String.IsNullOrEmpty(loadedAnalysisFingerprint)&&SameAnalysisProjectPath(loadedArchiveRoot,projectPath);}}
        // Invoked only by offline fault-injection tests. Never configured in production.
        internal Action<string> BeforeAnalysisReplacementPublishForTests;

        static string AnalysisDirectoryFingerprint(string directory)
        {
            string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Directory.Exists(directory)||(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Analysis directory is missing or is a link.");
            var queue=new Queue<string>();queue.Enqueue(directory);var rows=new List<string>();long bytes=0;int entries=0;
            while(queue.Count>0) {
                string current=queue.Dequeue();
                foreach(string child in Directory.GetFileSystemEntries(current)) {
                    if(++entries>4096)throw new InvalidDataException("Analysis directory contains too many entries.");
                    string full=Path.GetFullPath(child);if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Analysis path escaped its archive.");
                    FileAttributes attributes=File.GetAttributes(full);
                    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Analysis archive links are not supported.");
                    string relative=full.Substring(root.Length).Replace('\\','/');
                    if((attributes&FileAttributes.Directory)!=0){rows.Add("D|"+relative);queue.Enqueue(full);continue;}
                    long length=new FileInfo(full).Length;bytes+=length;
                    if(length<0||bytes>512L*1024*1024)throw new InvalidDataException("Analysis directory exceeds its 512 MiB budget.");
                    rows.Add("F|"+relative+"|"+length+"|"+SessionHash(full));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n",rows))));
        }
        ArchiveManifest VerifyLoadedAnalysisForOverwrite(string root)
        {
            if(!CanOverwriteLoadedAnalysis)throw new InvalidOperationException(L("Load a saved analysis before overwriting it.","请先加载需要覆盖的已保存分析。"));
            string destination=ArchiveChild(root,LoadedAnalysisId);
            var manifest=ReadArchiveManifest(destination,LoadedAnalysisId,true);
            if(AnalysisDirectoryFingerprint(destination)!=loadedAnalysisFingerprint)
                throw new IOException(L("This analysis was changed by another operation since it was loaded. Reload it or save under a new name.","该分析在加载后已被其他操作更改；为避免覆盖新内容，请重新加载或另存为新分析。"));
            return manifest;
        }
        void EnsureAnalysisPathFits(string path,bool directory)
        {
            string full=Path.GetFullPath(path);
            if(full.Length>=(directory?248:260)||(!directory&&Path.GetDirectoryName(full).Length>=248))
                throw new PathTooLongException(L("The project path is too long to safely save the analysis and its recovery copy. Move a copy of the project to a shorter path and retry. No archive has been changed. Path: ","项目路径过长，无法安全保存分析及恢复备份；请将项目副本移至较短路径后重试，分析存档未改写。路径：")+full);
        }
        void EnsureAnalysisRecoveryPathsFit(string destination,string backup)
        {
            EnsureAnalysisPathFits(backup,true);
            string source=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var pending=new Queue<string>();pending.Enqueue(destination);
            while(pending.Count>0) {
                foreach(string entry in Directory.GetFileSystemEntries(pending.Dequeue())) {
                    string full=Path.GetFullPath(entry);
                    if(!full.StartsWith(source,StringComparison.OrdinalIgnoreCase))throw new IOException("Analysis recovery path escaped its archive.");
                    var attributes=File.GetAttributes(full);
                    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Analysis archive links are not supported.");
                    bool directory=(attributes&FileAttributes.Directory)!=0;
                    EnsureAnalysisPathFits(ArchiveChild(backup,full.Substring(source.Length)),directory);
                    if(directory)pending.Enqueue(full);
                }
            }
        }
        void EnsureAnalysisArchivePathsFit(string root,string stage,string destination,string backup,bool overwrite)
        {EnsureAnalysisArchivePathsFit(root,stage,destination,backup,overwrite,null);}
        void EnsureAnalysisArchivePathsFit(string root,string stage,string destination,string backup,bool overwrite,IEnumerable<string> diagnosticsParts)
        {
            foreach(string directory in new[]{root,stage,destination,backup})EnsureAnalysisPathFits(directory,true);
            EnsureAnalysisPathFits(ArchiveChild(root,".save.lock"),false);
            var names=new List<string>{"parameters.bin","pdmap.bin","session.json","analysis.json","selection.json"};
            if(diagnosticsParts!=null)foreach(string part in diagnosticsParts){
                if(!IsDiagnosticsPartFileName(part))throw new InvalidDataException("Invalid diagnostics segment file name.");
                names.Add(part);
            }
            foreach(var chart in reportCharts) {
                string prefix="chart_"+chart.Sequence.ToString("D6",System.Globalization.CultureInfo.InvariantCulture);
                names.AddRange(new[]{prefix+".png",prefix+".csv",prefix+".bin"});
            }
            // Include the future recovery copy when saving a new analysis too.
            foreach(string directory in new[]{stage,destination,backup})foreach(string name in names)
                EnsureAnalysisPathFits(ArchiveChild(directory,name),false);
            if(overwrite)EnsureAnalysisRecoveryPathsFit(destination,backup);
        }
        void PublishAnalysisReplacement(string root,string stage,string destination,string id,string backup)
        {
            // Recheck immediately before the directory exchange while holding the
            // project save lock, including unlisted attachments and selection.json.
            VerifyLoadedAnalysisForOverwrite(root);
            EnsureAnalysisRecoveryPathsFit(destination,backup);
            Directory.Move(destination,backup);LastAnalysisBackupPath=backup;
            bool published=false;
            try {
                if(AnalysisDirectoryFingerprint(backup)!=loadedAnalysisFingerprint)
                    throw new IOException("The analysis changed during replacement. Its complete contents have been retained.");
                var hook=BeforeAnalysisReplacementPublishForTests;if(hook!=null)hook(stage);
                Directory.Move(stage,destination);published=true;
            } catch(Exception failure) {
                // Never delete or overwrite a competing destination. Restore the
                // original only into an absent path; otherwise keep the backup.
                if(!published&&!Directory.Exists(destination)&&!File.Exists(destination)) {
                    try{Directory.Move(backup,destination);LastAnalysisBackupPath="";}
                    catch(Exception restore){throw new IOException("Replacement failed; restore the complete original from "+backup+". "+restore.Message,failure);}
                }
                throw;
            }
        }
    }
}
