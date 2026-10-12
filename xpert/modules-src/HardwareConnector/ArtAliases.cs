using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;

namespace Xiongxin.HvDacXpert
{
    // ART DMC 1.8.80: DMCSCSL.h. These are acquisition aliases, not PnP display names.
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi, Pack=4)]
    struct ArtRecord
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=16)] public string Type;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Instance;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=16)] public string Category;
        public uint Class,Bus,Serial,Slot,Status;
        public bool Supported {get{return Class==1 && Bus==1 && Type=="USB3135";}}
        public bool Started {get{return (Status&8)!=0;}}
        public string Identity {get{return Type+"/"+Serial;}}
    }
    sealed class ArtPlan
    {
        public List<ArtRecord> Online=new List<ArtRecord>(),Offline=new List<ArtRecord>();
        public bool NeedsChange {get{return Offline.Count>0 || Online.Where((r,i)=>r.Name!="Dev"+(i+1)).Any();}}
        public string Describe(){return string.Join(Environment.NewLine,Offline.Select(r=>"REMOVE offline: "+r.Name+" ["+r.Serial+"]").Concat(Online.Select((r,i)=>r.Name+" -> Dev"+(i+1)+" ["+r.Serial+"]")));}
    }
    static class ArtAliases
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DMCSCSL_64.dll",CallingConvention=CallingConvention.StdCall)] static extern int DMCSCSL_GetDeviceList(uint cls,IntPtr buffer,int size,ref int count);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DMCSCSL_64.dll",CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Ansi)] static extern int DMCSCSL_RemoveDevice(string name);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("DMCSCSL_64.dll",CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Ansi)] static extern int DMCSCSL_RenameDevice(string oldName,string newName);
        static void Check(int code,string operation){if(code!=0)throw new InvalidOperationException("ART Dev: "+operation+"; code="+code);}
        public static List<ArtRecord> Read()
        {
            int size=Marshal.SizeOf(typeof(ArtRecord));if(size!=244)throw new InvalidOperationException("ART ABI size mismatch");
            for(int attempt=0;attempt<3;attempt++)
            {
                int count=0;Check(DMCSCSL_GetDeviceList(uint.MaxValue,IntPtr.Zero,0,ref count),"enumerate count");
                if(count<0 || count>1024)throw new InvalidOperationException("ART invalid device count");
                if(count==0)return new List<ArtRecord>();
                int capacity=count;IntPtr memory=Marshal.AllocHGlobal(size*capacity);
                try
                {
                    int code=DMCSCSL_GetDeviceList(uint.MaxValue,memory,size*capacity,ref count);
                    if(code==-200229)continue;Check(code,"enumerate");
                    if(count<0 || count>capacity)continue;
                    var result=new List<ArtRecord>();
                    for(int i=0;i<count;i++)result.Add((ArtRecord)Marshal.PtrToStructure(IntPtr.Add(memory,i*size),typeof(ArtRecord)));
                    return result;
                }
                finally{Marshal.FreeHGlobal(memory);}
            }
            throw new InvalidOperationException("ART topology changed; refresh and retry");
        }
        public static ArtPlan Plan(List<ArtRecord> all,HashSet<string> present)
        {
            var result=new ArtPlan();var supported=all.Where(r=>r.Supported).ToList();
            if(all.GroupBy(r=>r.Name,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1) || supported.GroupBy(r=>r.Identity).Any(g=>g.Count()>1))throw new InvalidOperationException("ART duplicate identity/name; no changes made");
            foreach(var r in supported)
            {
                bool exists=present.Contains(r.Instance??"") || present.Any(id=>id.EndsWith("\\"+r.Serial,StringComparison.OrdinalIgnoreCase));
                if(r.Started && exists && !string.IsNullOrEmpty(r.Instance))result.Online.Add(r);
                // A nonempty stale instance or a present but stopped device is ambiguous: fail closed.
                else if(!r.Started && !exists && string.IsNullOrEmpty(r.Instance) && r.Serial!=0)result.Offline.Add(r);
                else throw new InvalidOperationException("ART device pending/ambiguous: "+r.Name+"; no cleanup allowed");
            }
            if(result.Online.Count==0)throw new InvalidOperationException("ART: no verified online USB3135; no changes made");
            // Present devices missing from DMC may still be initializing; do not renumber a partial set.
            if(present.Any(id=>!result.Online.Any(r=>string.Equals(r.Instance,id,StringComparison.OrdinalIgnoreCase))))throw new InvalidOperationException("ART: DMC/PnP enumeration mismatch; wait and retry");
            result.Online=result.Online.OrderBy(r=>r.Serial).ThenBy(r=>r.Instance,StringComparer.OrdinalIgnoreCase).ToList();
            var preserved=all.Where(r=>!r.Supported).Select(r=>r.Name).ToList();
            for(int i=1;i<=result.Online.Count;i++)if(preserved.Contains("Dev"+i,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("ART: Dev"+i+" belongs to another device; no changes made");
            return result;
        }
        static string Fingerprint(List<ArtRecord> all){return string.Join("|",all.OrderBy(r=>r.Name).Select(r=>r.Name+":"+r.Identity+":"+r.Instance+":"+r.Started));}
        public static string Preview(){return Plan(Read(),DeviceNames.PresentArtInstances()).Describe();}
        public static bool Ready(){try{return !Plan(Read(),DeviceNames.PresentArtInstances()).NeedsChange;}catch{return false;}}
        public static void Normalize(Action<string> report)
        {
            using(var mutex=new Mutex(false,@"Global\XIONGXIN.HvDacXpert.ArtAliases"))
            {
                bool owned=false;
                try
                {
                    try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
                    if(!owned)throw new InvalidOperationException("ART Dev operation already running");
                    var expected=Read();var plan=Plan(expected,DeviceNames.PresentArtInstances());
                    Thread.Sleep(700);
                    if(Fingerprint(expected)!=Fingerprint(Read()))throw new InvalidOperationException("ART devices changed; refresh and retry");
                    Plan(expected,DeviceNames.PresentArtInstances());
                    if(!plan.NeedsChange){report("ART Dev1…DevN: "+L10n.T("pass"));return;}
                    string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"XIONGXIN","HvDacXpert","backups","art-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
                    Directory.CreateDirectory(dir);
                    new XDocument(new XElement("ArtDevices",expected.Select(r=>new XElement("Device",new XAttribute("name",r.Name),new XAttribute("type",r.Type),new XAttribute("serial",r.Serial),new XAttribute("instance",r.Instance??""),new XAttribute("status",r.Status),new XAttribute("slot",r.Slot),new XAttribute("bus",r.Bus),new XAttribute("class",r.Class),new XAttribute("category",r.Category))))).Save(Path.Combine(dir,"before.xml"));
                    string vendor=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Art Technology","ArtDAQ","Data","DeviceList.json");
                    if(File.Exists(vendor))File.Copy(vendor,Path.Combine(dir,"DeviceList.json.backup"));
                    File.WriteAllText(Path.Combine(dir,"plan.txt"),plan.Describe());
                    Action guard=delegate{
                        if(Fingerprint(expected)!=Fingerprint(Read()))throw new InvalidOperationException("ART topology/name changed during operation; stopped");
                        Plan(expected,DeviceNames.PresentArtInstances());
                    };
                    Action<string> journal=s=>{File.AppendAllText(Path.Combine(dir,"operations.txt"),DateTime.Now.ToString("O")+" "+s+Environment.NewLine);report(s);};
                    journal("ART Dev backup: "+dir);
                    // No driver uninstall, device reset, service restart or direct configuration edits.
                    foreach(var r in plan.Offline)
                    {
                        guard();Check(DMCSCSL_RemoveDevice(r.Name),"remove offline "+r.Name);
                        expected.RemoveAll(x=>x.Name==r.Name);journal("ART Dev: REMOVE offline "+r.Name+" ["+r.Serial+"]");
                    }
                    var changed=plan.Online.Where((r,i)=>r.Name!="Dev"+(i+1)).ToList();
                    var moves=new List<Tuple<string,string>>();
                    Action<string,string> rename=(from,to)=>{
                        guard();Check(DMCSCSL_RenameDevice(from,to),from+" -> "+to);
                        int at=expected.FindIndex(x=>x.Name==from);var r=expected[at];r.Name=to;expected[at]=r;
                        moves.Add(Tuple.Create(from,to));journal("ART Dev: "+from+" -> "+to);
                    };
                    try
                    {
                        string prefix="HVX"+Guid.NewGuid().ToString("N");var temps=new Dictionary<string,string>();
                        foreach(var r in changed){string temp=prefix+temps.Count;temps.Add(r.Identity,temp);rename(r.Name,temp);}
                        for(int i=0;i<plan.Online.Count;i++){var r=plan.Online[i];if(temps.ContainsKey(r.Identity))rename(temps[r.Identity],"Dev"+(i+1));}
                        guard();if(Plan(Read(),DeviceNames.PresentArtInstances()).NeedsChange)throw new InvalidOperationException("ART alias readback failed");
                    }
                    catch
                    {
                        // Only undo completed renames when the original hardware still owns the name.
                        foreach(var move in moves.AsEnumerable().Reverse())
                        {
                            try
                            {
                                var live=Read();var before=expected.First(r=>r.Name==move.Item2);
                                if(live.Any(r=>r.Name==move.Item1) || !live.Any(r=>r.Name==move.Item2 && r.Identity==before.Identity))break;
                                Check(DMCSCSL_RenameDevice(move.Item2,move.Item1),"rollback");
                                int at=expected.FindIndex(r=>r.Name==move.Item2);before.Name=move.Item1;expected[at]=before;
                                journal("ART Dev rollback: "+move.Item2+" -> "+move.Item1);
                            }
                            catch{break;}
                        }
                        throw new InvalidOperationException("ART Dev incomplete. Offline removals are not automatically restored. Backup: "+dir);
                    }
                    journal("ART Dev1…DevN: "+L10n.T("pass"));
                }
                finally{if(owned)mutex.ReleaseMutex();}
            }
        }
    }
}
