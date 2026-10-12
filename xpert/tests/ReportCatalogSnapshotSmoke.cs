using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ReportCatalogSnapshotSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks; static Assembly creation,report;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object x,string n,params object[] a){Type t=x as Type??x.GetType();return t.GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(x is Type?null:x,a);}
    static object F(object x,string n){return x.GetType().GetField(n,All).GetValue(x);}
    static object P(object x,string n){return x.GetType().GetProperty(n,All).GetValue(x,null);}
    static void Set(object x,string n,object v){x.GetType().GetProperty(n,All).SetValue(x,v,null);}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(Exception e){while(e is TargetInvocationException)e=e.InnerException;rejected=e is IOException||e is InvalidOperationException||e is InvalidDataException;}Check(rejected,why);}
    static string Block(string phase,double joint){return "0\t"+phase+"\t\t\r\n1\t始端\t冷缩\t0\r\n2\t本体\tXLPE\t\r\n1\tJoint\t预制\t"+joint+"\r\n2\t本体\tPVC\t\r\n1\t终端\t冷缩\t400\r\n";}
    static string Project(string qa,string name,string mode,string cable){string root=Path.Combine(qa,name);Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n始端地点=Start\r\n终端地点=End\r\n电缆芯数="+mode+"\r\nA相电缆长度=400\r\nB相电缆长度=400\r\nC相电缆长度=400\r\n电缆长度=400\r\n相电压=8.7kV\r\n",Encoding.UTF8);if(cable!=null)File.WriteAllText(Path.Combine(root,"CABLE.in"),cable,Encoding.UTF8);return root;}
    static void MaterialCases(string qa)
    {
        Type reference=creation.GetType("HVX.Controls.ReferenceCatalog",true),manager=creation.GetType("HVX.ProjectCables.JointMaterialCatalog",true),entryType=creation.GetType("HVX.ProjectCables.JointMaterialEntry",true);
        string path=Path.Combine(qa,"catalog.ini");object a=Call(reference,"Load",path),b=Call(manager,"Read",path);
        object entry=Activator.CreateInstance(entryType,true);entryType.GetField("Name",All).SetValue(entry,"New material from manager B");entryType.GetField("Velocity",All).SetValue(entry,167.25d);((IList)F(b,"Entries")).Add(entry);Call(b,"Save");
        byte[] saved=File.ReadAllBytes(path);((IList)F(a,"Organizations")).Add("Creation organization A");Reject(()=>Call(a,"Save",path),"missing baseline rejects catalog created by another editor");
        Check(saved.SequenceEqual(File.ReadAllBytes(path)),"other editor bytes remain exact");object clone=Call(a,"Clone");Reject(()=>Call(clone,"Save",path),"clone retains missing baseline protection");
        a=Call(reference,"Load",path);((IList)F(a,"Organizations")).Add("Creation organization A");Call(a,"Save",path);Check(File.ReadAllText(path).Contains("New material from manager B")&&File.ReadAllText(path).Contains("Creation organization A"),"explicit reload preserves both material and organization");
        b=Call(manager,"Read",path);a=Call(reference,"Load",path);((IList)F(a,"Operators")).Add("Updated operator");Call(a,"Save",path);saved=File.ReadAllBytes(path);Reject(()=>Call(b,"Save"),"reverse shared editor stale hash is rejected");Check(saved.SequenceEqual(File.ReadAllBytes(path)),"reverse conflict leaves bytes exact");
        string fresh=Path.Combine(qa,"fresh.ini");a=Call(reference,"Load",fresh);Call(a,"Save",fresh);Call(a,"Save",fresh);Check(File.Exists(fresh),"missing baseline permits first save and rebase permits next save");
        string competing=Path.Combine(qa,"competing.ini");a=Call(reference,"Load",competing);b=Call(reference,"Load",competing);Call(a,"Save",competing);Reject(()=>Call(b,"Save",competing),"two first-time creation editors cannot overwrite each other");
        a=Call(reference,"Load",fresh);File.Delete(fresh);Reject(()=>Call(a,"Save",fresh),"existing baseline deletion is not silently recreated");
    }
    static void ReportCases(string qa)
    {
        Type reader=report.GetType("HVX.ReportGenerator.ProjectReportDataReader"),renderer=report.GetType("HVX.ReportGenerator.CableTopologyRenderer");
        string root=Project(qa,"shared-conflict","一体三芯",Block("A相",80)+Block("B相",120)+Block("C相",160));object data=Call(reader,"Read",root);var rows=(Array)Call(renderer,"ReadRows",data);
        Check(rows.Length==3,"conflicting equal-length three-core blocks remain three real rows");for(int i=0;i<3;i++){Check(!(bool)F(rows.GetValue(i),"Integrated"),"conflicting row not presented as shared");Check(((double[])F(rows.GetValue(i),"Positions")).SequenceEqual(new[]{80d+40*i}),"actual per-block joint is not unioned");}
        Check(!((string)P(data,"JointSummary")).Contains("ALL")&&((string[])P(data,"Warnings")).Any(w=>w.Contains("blocks disagree")),"conflicting source phases stay explicit in text with a warning");
        Call(renderer,"Render",data,0,Path.Combine(qa,"shared-conflict-fixed.png"));
        root=Project(qa,"common","一体三芯",Block("A相",80)+Block("A相",80)+Block("A相",80));data=Call(reader,"Read",root);rows=(Array)Call(renderer,"ReadRows",data);Check(rows.Length==1&&(bool)F(rows.GetValue(0),"Integrated"),"genuinely identical shared geometry remains integrated");
        root=Project(qa,"conflicting-repeat-A","一体三芯",Block("A相",80)+Block("A相",120));data=Call(reader,"Read",root);rows=(Array)Call(renderer,"ReadRows",data);Check(rows.Length==2&&rows.Cast<object>().All(r=>(string)F(r,"Phase")=="A"),"conflicting repeated A does not invent B or C");
        root=Project(qa,"snapshot","单相",Block("A相",80));
        using(var control=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl")))
        {
            Set(control,"PromptAnalysisOnLoad",false);Check((bool)Call(control,"LoadProject",root),"report loads initial metadata");data=F(control,"projectData");File.WriteAllText(Path.Combine(root,"CABLE.in"),Block("A相",160),Encoding.UTF8);
            Call(control,"ShowProjectInformation");rows=(Array)F(F(control,"projectStructure"),"diagramRows");var values=new Dictionary<string,string>();Call(control,"ApplyReportValues",values);
            Check(((double[])F(rows.GetValue(0),"Positions")).SequenceEqual(new[]{80d})&&values["cable.joint_1"]=="80 m","preview and template values retain one consistent snapshot");
            Check(((Label)F(control,"previewDescription")).Text.Contains("重新加载"),"external change is visible in preview");
            string output=Path.Combine(qa,"stale.png");Reject(()=>Call(renderer,"Render",data,0,output),"direct export rejects stale snapshot");Check(!File.Exists(output),"stale export creates no chart");
            Reject(()=>Call(control,"BuildGenerationPlan",Path.Combine(qa,"stale.docx"),"",false),"report generation plan refuses externally changed structure");
            Check((bool)Call(control,"LoadProject",root),"explicit reload adopts new geometry");data=F(control,"projectData");Call(control,"ShowProjectInformation");rows=(Array)F(F(control,"projectStructure"),"diagramRows");values.Clear();Call(control,"ApplyReportValues",values);Check(((double[])F(rows.GetValue(0),"Positions")).SequenceEqual(new[]{160d})&&values["cable.joint_1"]=="160 m","reloaded graph and values both use new joint");
            Call(renderer,"Render",data,0,Path.Combine(qa,"snapshot-reloaded.png"));File.AppendAllText(Path.Combine(root,"ITEM.in"),"备注=changed\r\n");Reject(()=>Call(data,"RequireCurrentMetadata"),"ITEM changes also invalidate report snapshot");
        }
        var hook=reader.GetField("MetadataReadCompletedForTests",All);root=Project(qa,"during-read","单相",Block("A相",80));hook.SetValue(null,new Action(()=>File.AppendAllText(Path.Combine(root,"CABLE.in"),"\r\n")));
        try{Reject(()=>Call(reader,"Read",root),"metadata changed during load cannot publish a mixed model");}finally{hook.SetValue(null,null);}
        root=Project(qa,"missing-cable","单相",null);data=Call(reader,"Read",root);rows=(Array)Call(renderer,"ReadRows",data);Check(rows.Length==0,"legacy/missing structure still readable without inferred rows");Call(renderer,"Render",data,1,Path.Combine(qa,"structure-unavailable.png"));Check(File.Exists(Path.Combine(qa,"structure-unavailable.png")),"missing structure exports explicit unavailable placeholder");
    }
    static void OperationCases(string qa)
    {
        string root=Project(qa,"operations","单芯三相",Block("A相",80));File.WriteAllBytes(Path.Combine(root,"raw.dem"),new byte[]{1,2,3});byte[] raw=File.ReadAllBytes(Path.Combine(root,"raw.dem"));
        using(var control=(Control)Activator.CreateInstance(creation.GetType("HVX.Controls.ProjectCreationControl")))
        {
            Set(control,"ShowDialogMessages",false);Set(control,"EmbeddedMode",true);Set(control,"OnlineWeatherEnabled",false);Set(control,"AutoRefreshWeatherOnHardwareSample",false);Set(control,"SettingsPath",Path.Combine(qa,"operation-settings.ini"));Set(control,"DataRootDirectory",qa);
            Check((bool)Call(control,"LoadProject",root),"measured project loads for manager operation callback");var store=creation.GetType("HVX.ProjectCables.CableJointStore");object snapshot=Call(store,"Read",root);object plan=Call(snapshot,"PrepareEdit",0,1,80d,"Renamed joint","","",true);Call(control,"CommitMeasuredJoint",plan);
            Check((bool)P(control,"LastOperationSucceeded")&&(string)P(control,"LastOperation")=="EditJoint","manager edit commit publishes EditJoint, not AddJoint");snapshot=Call(store,"Read",root);plan=Call(snapshot,"PrepareRemove",0,1,true);Call(control,"CommitMeasuredJoint",plan);Check((string)P(control,"LastOperation")=="RemoveJoint","manager delete commit publishes RemoveJoint");
            Check(File.ReadAllBytes(Path.Combine(root,"raw.dem")).SequenceEqual(raw),"operation status correction leaves original waveform untouched");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"catalog-snapshot-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);creation=Assembly.LoadFrom(Path.Combine(build,"modules/ProjectCreation/Xiongxin.OWTS.ProjectCreation.dll"));report=Assembly.LoadFrom(Path.Combine(build,"modules/ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll"));MaterialCases(qa);ReportCases(qa);OperationCases(qa);Console.WriteLine("REPORT_CATALOG_SNAPSHOT_PASS="+checks+"; QA="+qa);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
