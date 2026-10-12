using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HVX.ProjectCables;

namespace HVX.Controls
{
    // Only metadata is changed. Encoding, phase labels and all raw captures are preserved.
    internal static class CalibrationProjectStore
    {
        internal sealed class TextFile
        {
            internal string Path, Newline; internal byte[] Bytes, Preamble; internal Encoding Encoding; internal List<string> Lines;
            internal static TextFile Read(string path)
            {
                var f = new FileInfo(path);
                if (!f.Exists || f.Length > 2*1024*1024 || (f.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("项目元数据不可用。" );
                var r = new TextFile { Path=path, Bytes=File.ReadAllBytes(path), Encoding=new UTF8Encoding(false,true) }; int skip=0;
                if(r.Bytes.Length>=3 && r.Bytes[0]==239 && r.Bytes[1]==187 && r.Bytes[2]==191) skip=3;
                else if(r.Bytes.Length>=2 && r.Bytes[0]==255 && r.Bytes[1]==254) {r.Encoding=new UnicodeEncoding(false,false,true);skip=2;}
                else if(r.Bytes.Length>=2 && r.Bytes[0]==254 && r.Bytes[1]==255) {r.Encoding=new UnicodeEncoding(true,false,true);skip=2;}
                else {try {r.Encoding.GetString(r.Bytes);} catch(DecoderFallbackException) {r.Encoding=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);}}
                r.Preamble=r.Bytes.Take(skip).ToArray();string text=r.Encoding.GetString(r.Bytes,skip,r.Bytes.Length-skip);
                r.Newline=text.Contains("\r\n")?"\r\n":"\n";r.Lines=text.Replace("\r\n","\n").Split('\n').ToList();return r;
            }
            internal byte[] Output() {return Preamble.Concat(Encoding.GetBytes(string.Join(Newline,Lines))).ToArray();}
        }
        internal static string[] Phases(string root)
        { return CableJointStore.Read(root).PhaseChoices; }
        private static string[] ResolveJointPhases(string root,string[] phases,IEnumerable<double> distances)
        {
            var snapshot=CableJointStore.Read(root);
            if(phases==null||phases.Length==0||phases.Any(p=>!snapshot.PhaseChoices.Contains(p)))
                throw new InvalidOperationException("请选择项目实际存在的相别；ALL 不代表独立三相的全选。");
            var blocks=snapshot.Blocks.Where(b=>snapshot.Mode==CableJointMode.SharedThreeCore||phases.Contains(b.Phase)).ToArray();
            double[] positions=distances.ToArray();
            if(positions.Any(n=>!CableJointStore.Finite(n)||n<=0||blocks.Any(b=>n>=b.Length)))
                throw new InvalidOperationException("接头必须位于每个所选相别的两端之间，未改写任何相。");
            return blocks.Select(b=>b.Phase).Distinct().ToArray();
        }
        internal static string Value(string line, out string key)
        {int eq=line.IndexOf('='); key=eq<0?"":HVX.ProjectModel.ProjectMetadataKeys.Canonical(line.Substring(0,eq));return eq<0?"":line.Substring(eq+1).Trim().Trim('"');}
        internal static void Set(List<string> lines,string key,string value)
        {
            key=HVX.ProjectModel.ProjectMetadataKeys.Canonical(key);
            int first=-1;
            for(int i=0;i<lines.Count;i++) {string k;Value(lines[i],out k);if(k!=key)continue;if(first<0)first=i;else{lines.RemoveAt(i);i--;}}
            string entry=key+": = \""+value+"\"";if(first<0)lines.Add(entry);else lines[first]=entry;
        }
        internal static void SaveLength(string root,string phase,double length,double velocity)
        {
            if(length<=0||length>1000000||double.IsNaN(length)||double.IsInfinity(length))throw new ArgumentException("测距长度无效。");
            if(!new[]{"ALL","A","B","C"}.Contains(phase)||double.IsNaN(velocity)||velocity<50||velocity>250)throw new ArgumentException("相别/波速无效。");
            var item=TextFile.Read(Path.Combine(root,"ITEM.in"));var cable=TextFile.Read(Path.Combine(root,"CABLE.in"));
            ValidateStructure(cable);
            string current="";int end=-1;double previous=0;bool changed=false;
            Action finish=()=>{if(end<0)return;if(length<=previous)throw new InvalidOperationException("测得终端不得落在已有接头之前，请先核对接头。");var c=cable.Lines[end].Split('\t');c[3]=length.ToString("0.###",CultureInfo.InvariantCulture)+"m";cable.Lines[end]=string.Join("\t",c);changed=true;end=-1;previous=0;};
            foreach(int i in Enumerable.Range(0,cable.Lines.Count))
            {
                var c=cable.Lines[i].Split('\t'); if(c.Length!=4)continue;
                if(c[0].Trim()=="0"){finish();current=CableJointStore.PhaseName(c[1]);}
                if((phase=="ALL"||current==phase||current=="ALL")&&c[0].Trim()=="1") {if(end>=0)previous=ParsePosition(cable.Lines[end].Split('\t')[3]);end=i;}
            }
            finish();if(!changed)throw new InvalidOperationException("未找到所选相别的电缆结构，未改写项目。");
            string number=length.ToString("0.###",CultureInfo.InvariantCulture);
            if(phase=="ALL"||Phases(root).SequenceEqual(new[]{"ALL"}))foreach(string p in new[]{"A","B","C"})Set(item.Lines,p+"相电缆长度",number);
            else Set(item.Lines,phase+"相电缆长度",number);
            double maximum=length;foreach(string line in item.Lines){string k;string v=Value(line,out k);double n;if(new[]{"CableLengthA","CableLengthB","CableLengthC"}.Contains(k)&&double.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out n))maximum=Math.Max(maximum,n);}
            Set(item.Lines,"电缆长度",maximum.ToString("0.###",CultureInfo.InvariantCulture));
            Set(item.Lines,phase+"相采用波速",velocity.ToString("R",CultureInfo.InvariantCulture));
            Commit(root,new[]{item,cable});
        }
        internal static double ParsePosition(string value)
        {double n;if(!double.TryParse(value.Trim().TrimEnd('m','米',' '),NumberStyles.Float,CultureInfo.InvariantCulture,out n)||double.IsNaN(n)||double.IsInfinity(n))throw new InvalidDataException("无法识别电缆附件位置。");return n;}
        internal static void AddJoints(string root,string[] phases,IEnumerable<double> distances)
        {
            var positions=distances.ToArray();phases=ResolveJointPhases(root,phases,positions);
            var cable=TextFile.Read(Path.Combine(root,"CABLE.in"));
            if(AddJoints(cable,phases,positions))Commit(root,new[]{cable});
        }
        private static bool AddJoints(TextFile cable,string[] phases,IEnumerable<double> distances)
        {
            var additions=distances.Distinct().OrderByDescending(n=>n).ToArray();bool changed=false;
            ValidateStructure(cable);
            if(phases==null||phases.Length==0||phases.Any(p=>!new[]{"ALL","A","B","C"}.Contains(p)))throw new ArgumentException("请选择接头适用相别。");
            if(additions.Any(n=>double.IsNaN(n)||double.IsInfinity(n)||n<=0))throw new ArgumentException("接头位置无效。");
            foreach(double distance in additions)
            {
                string current="";int previous=-1;
                for(int i=0;i<cable.Lines.Count;i++)
                {
                    var c=cable.Lines[i].Split('\t');if(c.Length!=4)continue;
                    if(c[0].Trim()=="0"){current=CableJointStore.PhaseName(c[1]);previous=-1;continue;}
                    if(!phases.Contains(current))continue;
                    if(c[0].Trim()!="1")continue;
                    double position=ParsePosition(c[3]);
                    if(previous>=0&&distance>ParsePosition(cable.Lines[previous].Split('\t')[3])+.001&&distance<position-.001)
                    {
                        string body=cable.Lines.Skip(previous+1).Take(i-previous-1).FirstOrDefault(l=>l.StartsWith("2\t"));
                        if(body==null)throw new InvalidDataException("接头所在区段缺少材料记录。");
                        cable.Lines.InsertRange(i,new[]{"1\t"+CableJointStore.AutomaticJointName(1)+"\t校准识别\t"+distance.ToString("0.###",CultureInfo.InvariantCulture)+"m",body});
                        i+=2;changed=true;
                    }
                    previous=i;
                }
            }
            if(changed)RenumberAutomaticJoints(cable);
            return changed;
        }
        internal static void RemoveGeneratedJoints(string root,string[] phases,IEnumerable<double> distances)
        {
            var positions=distances.ToArray();phases=ResolveJointPhases(root,phases,positions);
            var cable=TextFile.Read(Path.Combine(root,"CABLE.in"));ValidateStructure(cable);
            if(RemoveGeneratedJoints(cable,phases,positions))Commit(root,new[]{cable});
        }
        internal static void ReviewJoints(string root,string[] phases,IEnumerable<double> confirmed,IEnumerable<double> excluded)
        { ReviewJoints(root,phases,confirmed,excluded,null); }
        internal static void ReviewJoints(string root,string[] phases,IEnumerable<double> confirmed,IEnumerable<double> excluded,string expectedCableHash)
        { ReviewJoints(root,phases,confirmed,excluded,expectedCableHash,null); }
        internal static void ReviewJoints(string root,string[] phases,IEnumerable<double> confirmed,IEnumerable<double> excluded,string expectedCableHash,string expectedItemHash)
        {
            // Hold the mode-defining metadata against write/delete for the entire
            // review transaction. UI callers additionally bind its opening hash.
            string itemPath=Path.Combine(root,"ITEM.in");
            if((File.GetAttributes(itemPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked project metadata is not supported.");
            using(var itemLease=new FileStream(itemPath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if(expectedItemHash!=null&&CableJointStore.Hash(JointMaterialCatalog.ReadBounded(itemPath))!=expectedItemHash)
                throw new IOException("项目负载或参数已在确认期间修改，请重新打开接头窗口；本次未写入。 / Project metadata changed during review; reopen the dialog.");
            double[] included=confirmed.ToArray(),removedPositions=excluded.ToArray();
            phases=ResolveJointPhases(root,phases,included.Concat(removedPositions));
            var cable=TextFile.Read(Path.Combine(root,"CABLE.in"));
            // Compare the exact bytes this review will edit, not a separate UI
            // read. Commit also checks these bytes again before replacement.
            if(expectedCableHash!=null&&CableJointStore.Hash(cable.Bytes)!=expectedCableHash)
                throw new IOException("项目接头已在确认期间修改，请重新打开接头窗口；本次未写入。 / Project joints changed during review. Reopen the joint dialog; nothing was saved.");
            ValidateStructure(cable);
            bool removed=RemoveGeneratedJoints(cable,phases,removedPositions);
            bool added=AddJoints(cable,phases,included);
            if(removed||added)Commit(root,new[]{cable});
            }
        }
        private static bool RemoveGeneratedJoints(TextFile cable,string[] phases,IEnumerable<double> distances)
        {
            if(phases==null||phases.Length==0||phases.Any(p=>!new[]{"ALL","A","B","C"}.Contains(p)))throw new ArgumentException("请选择接头适用相别。");
            var positions=distances.ToArray();string phase="";bool changed=false;
            for(int i=0;i<cable.Lines.Count;i++) {
                var c=cable.Lines[i].Split('\t');if(c.Length!=4)continue;
                if(c[0].Trim()=="0")phase=CableJointStore.PhaseName(c[1]);
                if(c[0].Trim()!="1"||c[2]!="校准识别"||!phases.Contains(phase)||!positions.Any(d=>Math.Abs(d-ParsePosition(c[3]))<.001))continue;
                if(i+1>=cable.Lines.Count||!cable.Lines[i+1].StartsWith("2\t"))throw new IOException("接头后区段已变化，不能自动撤销。");
                string preceding=cable.Lines.Take(i).LastOrDefault(l=>l.StartsWith("2\t"));
                if(preceding!=cable.Lines[i+1])throw new IOException("接头两侧材料已变化，请在项目结构中人工核对，未自动删除。");
                cable.Lines.RemoveRange(i,2);i--;changed=true;
            }
            if(changed)RenumberAutomaticJoints(cable);
            return changed;
        }
        private static void RenumberAutomaticJoints(TextFile cable)
        {
            var nodes=new List<int>();
            Action finish=()=>{for(int ordinal=1;ordinal<nodes.Count-1;ordinal++){int line=nodes[ordinal];var c=cable.Lines[line].Split('\t');if(CableJointStore.IsAutomaticJointName(c[1])){c[1]=CableJointStore.AutomaticJointName(ordinal);cable.Lines[line]=String.Join("\t",c);}}nodes.Clear();};
            for(int i=0;i<cable.Lines.Count;i++){var c=cable.Lines[i].Split('\t');if(c.Length!=4)continue;if(c[0].Trim()=="0")finish();else if(c[0].Trim()=="1")nodes.Add(i);}
            finish();
        }
        private static void ValidateStructure(TextFile cable)
        {
            bool phase=false;double last=-1;
            foreach(string line in cable.Lines) {
                if(String.IsNullOrWhiteSpace(line))continue;
                string[] c=line.Split('\t');if(c.Length!=4)throw new InvalidDataException("电缆结构不是四列格式，未改写。");
                string kind=c[0].Trim();
                if(kind=="0") {CableJointStore.PhaseName(c[1]);phase=true;last=-1;}
                else if(!phase)throw new InvalidDataException("电缆结构缺少相别头。");
                else if(kind=="1"){double position=ParsePosition(c[3]);if(position<0||position<=last)throw new InvalidDataException("附件位置未递增，未改写项目。");last=position;}
                else if(kind!="2")throw new InvalidDataException("未知电缆结构行，未改写。");
            }
        }
        // Offline fault injection only; never assigned by production callers.
        internal static Action<int,string> BeforePublishForTests;
        sealed class MetadataWrite
        {
            internal string Path,Temporary,Backup,RestoreTemporary,RestoreBackup;
            internal byte[] Original,Updated;
            internal FileAttributes Attributes;
            internal bool AttributesChanged;
        }
        static bool MatchesMetadata(string path,byte[] expected)
        {
            return File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0&&
                new FileInfo(path).Length==expected.Length&&File.ReadAllBytes(path).SequenceEqual(expected);
        }
        static void WriteMetadataTemporary(string path,byte[] bytes)
        {
            // A random name must still never truncate a pre-existing file.
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
        }
        internal static void Commit(string root,TextFile[] files)
        {
            root=new DirectoryInfo(Path.GetFullPath(root)).FullName.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(root.Length==2&&root[1]==':')root+=Path.DirectorySeparatorChar;
            for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("不支持链接目录中的项目写入。");
            var writes=new List<MetadataWrite>();var targets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(files==null||files.Length==0)throw new ArgumentException("缺少项目写入资料。");
            string tag=Guid.NewGuid().ToString("N");
            foreach(var f in files) {
                string path=Path.GetFullPath(f.Path);
                if(!String.Equals(Path.GetDirectoryName(path),root,StringComparison.OrdinalIgnoreCase)||!targets.Add(path))throw new IOException("项目写入目标越界或重复。");
                writes.Add(new MetadataWrite{Path=path,Original=(byte[])f.Bytes.Clone(),Updated=f.Output(),
                    Temporary=path+".c-"+tag+".tmp",Backup=path+".c-"+tag+".bak",RestoreTemporary=path+".r-"+tag+".tmp",RestoreBackup=path+".r-"+tag+".bak"});
            }
            string lockPath=Path.Combine(root,".DataBrowser.write.lock");
            if(File.Exists(lockPath)&&(File.GetAttributes(lockPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("项目锁文件不能为链接。");
            // Keep a complete unique token without repeating a long timestamp and
            // operation caption in every sibling name. Otherwise a valid project
            // path could fail only when File.Replace tries to create its backup.
            foreach(var write in writes)foreach(string path in new[]{write.Path,write.Temporary,write.Backup,write.RestoreTemporary,write.RestoreBackup,lockPath})
                if(path.Length>=260)throw new IOException("项目目录过深，无法安全创建校准备份；请将项目复制到较短路径后重试，原文件未修改。");
            using(var gate=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                var done=new List<MetadataWrite>();
                try {
                    foreach(var write in writes)if(!MatchesMetadata(write.Path,write.Original))throw new IOException("项目资料已被其它操作修改，请重新加载。");
                    for(int i=0;i<writes.Count;i++) {
                        var write=writes[i];bool created=false;
                        try {
                            WriteMetadataTemporary(write.Temporary,write.Updated);created=true;
                            var hook=BeforePublishForTests;if(hook!=null)hook(i,write.Path);
                            if(!MatchesMetadata(write.Path,write.Original))throw new IOException("项目资料已被其它操作修改，请重新加载。");
                            if(File.Exists(write.Backup)||Directory.Exists(write.Backup))throw new IOException("校准备份路径已存在，未覆盖。");
                            write.Attributes=File.GetAttributes(write.Path);
                            if((write.Attributes&FileAttributes.ReadOnly)!=0) {
                                File.SetAttributes(write.Path,write.Attributes&~FileAttributes.ReadOnly);
                                write.AttributesChanged=true;
                            }
                            File.Replace(write.Temporary,write.Path,write.Backup);done.Add(write);
                        } finally {if(created&&File.Exists(write.Temporary))File.Delete(write.Temporary);}
                    }
                } catch(Exception failure) {
                    var recoveryErrors=new List<string>();
                    foreach(var write in done.AsEnumerable().Reverse()) {
                        bool created=false;
                        try {
                            if(!MatchesMetadata(write.Path,write.Updated))throw new IOException("检测到外部更改，未覆盖当前内容。");
                            WriteMetadataTemporary(write.RestoreTemporary,write.Original);created=true;
                            if(!MatchesMetadata(write.Path,write.Updated))throw new IOException("恢复前检测到外部更改，未覆盖当前内容。");
                            if(File.Exists(write.RestoreBackup)||Directory.Exists(write.RestoreBackup))throw new IOException("恢复备份路径已存在，未覆盖。");
                            File.Replace(write.RestoreTemporary,write.Path,write.RestoreBackup);
                        } catch(Exception recovery) {recoveryErrors.Add(write.Path+"："+recovery.Message+" 原始备份："+write.Backup);}
                        finally {
                            if(created&&File.Exists(write.RestoreTemporary))try{File.Delete(write.RestoreTemporary);}
                            catch(Exception cleanup){recoveryErrors.Add("临时恢复文件未清理："+write.RestoreTemporary+"；"+cleanup.Message);}
                        }
                    }
                    if(recoveryErrors.Count>0)throw new IOException("项目保存失败；部分资料未自动恢复，请核对当前内容和保留的原始备份。\r\n"+String.Join("\r\n",recoveryErrors),failure);
                    throw;
                } finally {
                    // Unlock only validated metadata targets, never directories or
                    // access-control lists. Rollback completes before relocking.
                    var errors=new List<string>();
                    foreach(var write in writes.Where(w=>w.AttributesChanged)) {
                        try {
                            if(!File.Exists(write.Path)||(File.GetAttributes(write.Path)&FileAttributes.ReparsePoint)!=0)
                                throw new IOException("目标文件已变化，不能恢复只读属性。");
                            File.SetAttributes(write.Path,File.GetAttributes(write.Path)|FileAttributes.ReadOnly);
                        } catch(Exception ex) {errors.Add(write.Path+"："+ex.Message+"；原始备份："+write.Backup);}
                    }
                    if(errors.Count>0)throw new IOException("文件写入或恢复已结束，但未能重新开启只读保护。请核对以下文件及备份：\r\n"+String.Join("\r\n",errors));
                }
            }
        }
    }
}
