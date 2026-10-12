using System;
using System.IO;
using System.Linq;
using System.Text;
using HVX.ProjectCables;

internal static class SharedCableJointStoreSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}catch(IOException){rejected=true;}Check(rejected,message);}
    static string Block(string phase,int length=400,string material="XLPE")
    {return "0\t"+phase+"\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\t"+material+"\t\r\n1\t终端\t冷缩\t"+length+"m\r\n";}
    static string Fixture(string root,string mode,string text,Encoding encoding=null)
    {
        string dir=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"ITEM.in"),"电缆芯数: = \""+mode+"\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir,"CABLE.in"),text,encoding??new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(dir,"protected.cal"),new byte[]{4,2,7});return dir;
    }
    static CableJointPlan Plan(CableJointSnapshot s,string[] phases,double p=80,string name="检测接头",string before="",string after="")
    {return s.Prepare(phases,p,name,"预制接头",before,after);}
    static int Main(string[] args)
    {
        try {
            string root=Path.Combine(Path.GetFullPath(args[0]),"joint-store-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string dir=Fixture(root,"三相单芯",Block("A")+Block("B",500,"PVC")+Block("C",600,"油纸"));
            var s=CableJointStore.Read(dir);Check(s.Mode==CableJointMode.SeparatePhases&&s.PhaseChoices.SequenceEqual(new[]{"A","B","C"}),"ABC checkboxes based on actual phases");
            byte[] original=File.ReadAllBytes(s.CablePath),item=File.ReadAllBytes(Path.Combine(dir,"ITEM.in"));
            var plan=Plan(s,new[]{"A","C"},80,"J-AC","XLPE","PVC");
            Check(File.ReadAllBytes(s.CablePath).SequenceEqual(original),"Preparing or cancelling never writes");
            Check(plan.ResolvedTargets.Length==2&&plan.ResolvedTargets.All(t=>t.Name=="J-AC"&&t.MaterialBefore=="XLPE"&&t.MaterialAfter=="PVC"),"Resolved names and both materials exact");
            string backup=CableJointStore.Commit(plan),result=File.ReadAllText(s.CablePath);
            Check(File.ReadAllBytes(backup).SequenceEqual(original),"Exact recoverable backup");
            Check(result.Contains(Block("B",500,"PVC")),"Unselected B block byte text unchanged");
            Check(result.Split(new[]{"1\tJ-AC\t预制接头\t80m"},StringSplitOptions.None).Length==3,"Only A and C receive joint");
            Check(File.ReadAllBytes(Path.Combine(dir,"ITEM.in")).SequenceEqual(item)&&File.ReadAllBytes(Path.Combine(dir,"protected.cal")).SequenceEqual(new byte[]{4,2,7}),"ITEM and calibration raw data unchanged");
            Check(CableJointStore.Read(dir).Blocks.Where(b=>b.Phase!="B").All(b=>b.Positions.Contains(80)),"Saved structure reloads");
            Reject(()=>CableJointStore.Commit(plan),"Stale edit plan rejected");

            dir=Fixture(root,"三相单芯",Block("A")+Block("B",500,"PVC")+Block("C",600,"油纸"));s=CableJointStore.Read(dir);
            plan=Plan(s,s.PhaseChoices);Check(plan.ResolvedTargets.Select(t=>t.MaterialAfter).SequenceEqual(new[]{"XLPE","PVC","油纸"}),"Blank material preserves each selected segment independently");
            CableJointStore.Commit(plan);Check(CableJointStore.Read(dir).Blocks.All(b=>b.Positions.Contains(80)),"Select all writes every actual phase");
            Reject(()=>Plan(s,new[]{"ALL"}),"ALL is not a wildcard in independent phases");
            Reject(()=>Plan(s,new string[0]),"No phase rejected");Reject(()=>Plan(s,new[]{"D"}),"Nonexistent phase rejected");
            Reject(()=>Plan(s,new[]{"A","B"},450),"If outside any selected cable all phases rejected");
            foreach(double pos in new[]{0,-1,.0001,400,401,Double.NaN,Double.PositiveInfinity})Reject(()=>Plan(s,new[]{"A"},pos),"Endpoint/nonfinite/outside rejected");
            foreach(string bad in new[]{"","x\tA","x\n1",new string('a',81)})Reject(()=>Plan(s,new[]{"A"},100,bad),"Unsafe/missing name rejected");
            Reject(()=>Plan(s,new[]{"A"},100,"终端"),"Duplicate accessory name rejected");

            dir=Fixture(root,"三芯",Block("A")+Block("A")+Block("A"));s=CableJointStore.Read(dir);
            Check(s.Mode==CableJointMode.SharedThreeCore&&s.PhaseChoices.SequenceEqual(new[]{"ALL"}),"Three core has one shared scope");
            plan=Plan(s,new[]{"ALL"});Check(plan.ResolvedTargets.Length==3,"Repeated A accepted as three existing shared blocks");CableJointStore.Commit(plan);
            s=CableJointStore.Read(dir);Check(s.Blocks.Count==3&&s.Blocks.All(b=>b.Phase=="A"&&b.Positions.Contains(80)),"Repeated A never renamed to B/C");

            dir=Fixture(root,"三相单芯",Block("A")+Block("A")+Block("B"));s=CableJointStore.Read(dir);
            Check(s.PhaseChoices.SequenceEqual(new[]{"A","B"})&&s.ScopeDescription.Contains("A：2"),"Duplicate actual labels grouped with affected count");
            CableJointStore.Commit(Plan(s,new[]{"A"}));s=CableJointStore.Read(dir);
            Check(s.Blocks.Take(2).All(b=>b.Positions.Contains(80))&&!s.Blocks[2].Positions.Contains(80),"A selection applies only both A blocks");

            dir=Fixture(root,"单相",Block("B相"));s=CableJointStore.Read(dir);Check(s.Mode==CableJointMode.SinglePhase&&s.PhaseChoices.SequenceEqual(new[]{"B"}),"Single cable shows only its actual B phase");
            CableJointStore.Commit(Plan(s,new[]{"B"}));Check(File.ReadAllText(s.CablePath).StartsWith("0\tB相\t"),"Original phase label spelling preserved");
            dir=Fixture(root,"单相",Block("A")+Block("B"));Check(CableJointStore.Read(dir).Mode==CableJointMode.Unknown,"Contradictory single-phase metadata not silently applied");
            dir=Fixture(root,"",Block("A")+Block("B")+Block("C"));Check(CableJointStore.Read(dir).Mode==CableJointMode.Unknown,"Equal lengths do not imply shared three-core");

            foreach(var enc in new Encoding[]{new UTF8Encoding(false),new UTF8Encoding(true),Encoding.GetEncoding(936),new UnicodeEncoding(false,true),new UnicodeEncoding(true,true)}) {
                dir=Fixture(root,"三芯",Block("ALL"),enc);s=CableJointStore.Read(dir);original=File.ReadAllBytes(s.CablePath);
                backup=CableJointStore.Commit(Plan(s,new[]{"ALL"},123.456,"自定义接头","油纸","交联聚乙烯"));
                Check(File.ReadAllBytes(backup).SequenceEqual(original),"Encoding backup exact "+enc.WebName);
                var loaded=CableJointStore.Read(dir);Check(loaded.Encoding.CodePage==enc.CodePage&&loaded.Preamble.SequenceEqual(enc.GetPreamble())&&loaded.Blocks[0].Positions.Contains(123.456),"Encoding/BOM/precision preserved "+enc.WebName);
            }
            dir=Fixture(root,"三芯",Block("ALL"));s=CableJointStore.Read(dir);plan=Plan(s,new[]{"ALL"});
            File.AppendAllText(Path.Combine(dir,"ITEM.in"),"修改: = \"1\"\r\n");original=File.ReadAllBytes(s.CablePath);
            Reject(()=>CableJointStore.Commit(plan),"Metadata mode/source changed after opening is rejected");Check(File.ReadAllBytes(s.CablePath).SequenceEqual(original),"Metadata conflict leaves cable unchanged");
            plan=Plan(CableJointStore.Read(dir),new[]{"ALL"});
            using(var gate=new FileStream(Path.Combine(dir,".DataBrowser.write.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))Reject(()=>CableJointStore.Commit(plan),"Existing metadata editor lock blocks write");
            Check(!Directory.GetFiles(dir,"*.tmp").Any(),"No staging files left after rejected commit");
            var preview=CableJointStore.CreatePreview("A",400,"XLPE");Reject(()=>CableJointStore.Commit(Plan(preview,new[]{"A"})),"Preview cannot be persisted");
            foreach(string bad in new[]{Block("A").Replace("2\t本体\tXLPE\t\r\n",""),Block("A").Replace("400m","0m"),Block("A")+"2\t本体\tXLPE\t\r\n"}) {
                dir=Fixture(root,"",bad);Reject(()=>CableJointStore.Read(dir),"Malformed structure fails without mutation");Check(File.ReadAllText(Path.Combine(dir,"CABLE.in"))==bad,"Malformed file untouched");
            }
            Console.WriteLine("SHARED_CABLE_JOINT_STORE_PASS="+checks+"; fixtures="+root);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
