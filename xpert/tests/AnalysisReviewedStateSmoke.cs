using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class AnalysisReviewedStateSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks; static Type controlType;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static object Call(object value,string name,params object[] args)
    {return value.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static object Get(object value,string name)
    {var p=value.GetType().GetProperty(name,All);return p!=null?p.GetValue(value,null):value.GetType().GetField(name,All).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetProperty(name,All).SetValue(value,data,null);}
    static bool[] Viewed(object value){return (bool[])Get(value,"ReviewedRows");}
    static bool Same(double[,] a,double[,] b)
    {return a.GetLength(0)==b.GetLength(0)&&a.GetLength(1)==b.GetLength(1)&&a.Cast<double>().Zip(b.Cast<double>(),(x,y)=>x.Equals(y)).All(x=>x);}
    static IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes)
    {foreach(TreeNode n in nodes){yield return n;foreach(var child in Nodes(n.Nodes))yield return child;}}
    static TreeNode Record(Control review,string path)
    {var tree=(TreeView)Get(Get(review,"AnalysisTree"),"tree");return Nodes(tree.Nodes).Single(n=>n.ToolTipText.Contains(path));}
    static Control Create(string project)
    {var c=(Control)Activator.CreateInstance(controlType);Set(c,"PromptSavedAnalysisOnProjectLoad",false);Set(c,"LanguageIndex",0);Set(c,"ProjectPath",project);return c;}
    static void Matrices(double[,] catalog,int count,out double[,] p,out double[,] w)
    {
        p=new double[count,36];w=new double[count,4096];
        for(int r=0;r<count;r++){
            int file=r==2?1:0;p[r,0]=catalog[file,0];p[r,1]=catalog[file,1];p[r,2]=catalog[file,2];p[r,3]=45;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;
            p[r,9]=r;p[r,10]=100;p[r,11]=710;p[r,12]=4.88;p[r,13]=200;p[r,15]=p[r,16]=180;p[r,20]=100;p[r,21]=.92;p[r,32]=4095;p[r,33]=.91;
            for(int i=0;i<4096;i++)w[r,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
        }
    }
    static object[] Export(Control c)
    {object[] outputs={null,null,null,null};Check((bool)Call(c,"ExportAnalysisSession",outputs),"session export succeeds: "+Get(c,"LastError"));return outputs;}
    static string Setting(string[,] settings,string key)
    {return Enumerable.Range(0,settings.GetLength(0)).Where(i=>settings[i,0]==key).Select(i=>settings[i,1]).Single();}
    static string[,] ChangeSetting(string[,] settings,string key,string value,bool remove=false)
    {
        var rows=Enumerable.Range(0,settings.GetLength(0)).Where(i=>!remove||settings[i,0]!=key).ToArray();var result=new string[rows.Length,2];
        for(int r=0;r<rows.Length;r++){result[r,0]=settings[rows[r],0];result[r,1]=settings[rows[r],0]==key?value:settings[rows[r],1];}return result;
    }
    static bool Load(Control c,string project,object[] session,string[,] settings=null)
    {return (bool)Call(c,"LoadAnalysisSession",project,session[0],session[1],session[2],settings??(string[,])session[3]);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"reviewed-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            controlType=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
            string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"414\"\r\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t414m\r\n",new UTF8Encoding(false));
            string first=Path.Combine(project,"A_13.40kV_1.loc"),second=Path.Combine(project,"A_13.40kV_2.loc");
            File.WriteAllBytes(first,new byte[256]);File.WriteAllBytes(second,new byte[256]);
            double[,] p,w;
            using(var c=Create(project))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(1200,800)})
            {
                host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                var catalog=(double[,])Call(Get(c,"AnalysisTree"),"GetDataCatalog");Check(catalog.GetLength(0)==2,"two temporary LOC records recognized");
                Matrices(catalog,3,out p,out w);Check((bool)Call(c,"LoadDataMatrices",p,w),"source matrices load");
                var before=(double[,])Get(c,"ResultParameterMatrix");int visible=(int)Get(Get(c,"Spectrum"),"VisiblePointCount");
                Check(Viewed(c).SequenceEqual(new[]{false,false,false}),"automatic first selection does not imply viewed");
                Check(Record(c,first).Text.Contains("待复核"),"unseen unaccepted file remains pending review");
                Call(c,"MarkAnalysisClean");Set(c,"LanguageIndex",1);Set(c,"LanguageIndex",0);Check(!(bool)Get(c,"HasUnsavedAnalysisChanges"),"language alone remains clean");
                Check((bool)Call(c,"SelectSourceRow",0),"explicitly select current initial row");
                Check(Viewed(c).All(v=>!v)&&!(bool)Get(c,"HasUnsavedAnalysisChanges"),"programmatic selection does not imply operator viewing");
                Check(Record(c,first).Nodes[0].StateImageIndex==7,"pending pulse uses amber question state");
                Check((bool)Call(c,"MarkCurrentReviewed"),"explicit review confirmation succeeds");
                Check(Viewed(c).SequenceEqual(new[]{true,false,false})&&(bool)Get(c,"HasUnsavedAnalysisChanges"),"confirmation marks only the current row");
                Check(Record(c,first).Nodes[0].StateImageIndex==9,"reviewed unaccepted pulse uses blue-gray dash state");
                Check(Record(c,first).Text.Contains("待复核"),"partially viewed file remains pending");
                Check((bool)Call(c,"SelectSourceRow",1)&&(bool)Call(c,"MarkCurrentReviewed")&&Record(c,first).Text.Contains("不确定"),"all explicitly undecided file is uncertain");
                Check(Record(c,first).ToolTipText.Contains("已接受：0")&&Record(c,first).ToolTipText.Contains("不计入统计"),"tooltip distinguishes review from acceptance");
                Check(Same(before,(double[,])Get(c,"ResultParameterMatrix"))&&(int)Get(Get(c,"Spectrum"),"VisiblePointCount")==visible,"viewing alters no matrix column or accepted statistics");
                Check(!(bool)Call(c,"SelectSourceRow",99)&&Viewed(c).SequenceEqual(new[]{true,true,false}),"failed selection does not mark any other row");
                Check((bool)Call(c,"LoadDataMatrices",p,w)&&Viewed(c).SequenceEqual(new[]{true,true,false}),"same-input host loop preserves review state");
                Check(!(bool)Call(c,"ForceReloadDataMatrices",p,new double[0,0])&&Viewed(c).SequenceEqual(new[]{true,true,false}),"failed reload preserves previous review state atomically");
                Check((bool)Call(c,"ForceReloadDataMatrices",p,w)&&Viewed(c).All(v=>!v),"explicit new analysis resets review state");
                Call(c,"SelectSourceRowCore",1,false);Call(c,"EnsureVisibleSelection");Check(Viewed(c).All(v=>!v),"automatic selection and visibility repair remain unreviewed");
                Call(c,"SelectSourceRow",0);Call(c,"MarkCurrentReviewed");Call(c,"SelectSourceRow",2);Call(c,"MarkCurrentReviewed");
                var evidence=(Dictionary<string,string>)Get(c,"algorithmDiagnostics");
                evidence[Path.GetFileName(first)]="{\"current_analysis\":{\"source_identity_requires_review\":true,\"acceptance_semantics\":\"engineering_candidate_not_manual_or_physical_confirmation\"}}";
                evidence[Path.GetFileName(second)]="{\"current_analysis\":{\"source_identity_supported\":false}}";
                string originalDiagnostics=(string)Get(c,"AlgorithmDiagnosticsJson");
                Set(c,"LocationFilterStrength",61d);Set(c,"LocationFilterEnabled",false);
                Set(Get(c,"Spectrum"),"ConfidenceClassificationEnabled",true);
                Check(!(bool)Get(c,"AutomaticDisplayRecommendation"),"manual display choices disable automatic selection");
                var session=Export(c);Check(Setting((string[,])session[3],"ReviewedRows")=="101","session stores exact row-aligned reviewed bitstring");
                Check(Setting((string[,])session[3],"AutomaticDisplayRecommendation")=="0"&&Setting((string[,])session[3],"LocationFilterStrength")=="61"&&Setting((string[,])session[3],"ConfidenceClassificationEnabled")=="1","session records display choices and exact strength");
                Check(Setting((string[,])session[3],"AlgorithmDiagnostics")==originalDiagnostics,"full source-identity status is saved separately from matrix and review states");
                Check(Load(c,project,session)&&Viewed(c).SequenceEqual(new[]{true,false,true}),"session restores viewed independently from acceptance");
                Check(!(bool)Get(c,"AutomaticDisplayRecommendation")&&!(bool)Get(c,"LocationFilterEnabled")&&(double)Get(c,"LocationFilterStrength")==61d&&(bool)Get(Get(c,"Spectrum"),"ConfidenceClassificationEnabled"),"manual display choices round trip independently of review state");
                Check(!Load(c,project,session,ChangeSetting((string[,])session[3],"LocationFilterStrength","101"))&&(double)Get(c,"LocationFilterStrength")==61d,"invalid display strength rejected atomically");
                Check(Load(c,project,session,ChangeSetting((string[,])session[3],"AutomaticDisplayRecommendation","1"))&&(bool)Get(c,"AutomaticDisplayRecommendation"),"automatic preference restores without recomputing saved display choices");
                Application.DoEvents();Check((double)Get(c,"LocationFilterStrength")==61d&&(bool)Get(Get(c,"Spectrum"),"ConfidenceClassificationEnabled"),"stale advice cannot overwrite restored session");
                Check(Load(c,project,session),"restore manual choices for remaining archive regression");
                Check((string)Get(c,"LocationFilterAlgorithmId")=="A","existing sessions explicitly persist filter A");
                Set(c,"LocationFilterAlgorithmId","B");Set(c,"LocationFilterBChargeThresholdPc",24.5d);Set(c,"LocationFilterBVoltageLimitKv",25d);
                Set(c,"LocationFilterBMinimumClusterSize",4);Set(c,"LocationFilterBFromM",12);Set(c,"LocationFilterBToM",(int?)400);
                var filterBSession=Export(c);Check(Setting((string[,])filterBSession[3],"LocationFilterAlgorithmId")=="B","filter B identity saved independently of Task17 method");
                Check(Load(c,project,filterBSession)&&(string)Get(c,"LocationFilterAlgorithmId")=="B"&&(double)Get(c,"LocationFilterBChargeThresholdPc")==24.5d&&
                    (double)Get(c,"LocationFilterBVoltageLimitKv")==25d&&(int)Get(c,"LocationFilterBMinimumClusterSize")==4&&(int)Get(c,"LocationFilterBFromM")==12&&(int?)Get(c,"LocationFilterBToM")==400,"B parameters round trip without overriding saved manual selection");
                var bBefore=(double[,])Get(c,"ResultParameterMatrix");
                foreach(var invalid in new[]{new[]{"LocationFilterAlgorithmId","C"},new[]{"LocationFilterBChargeThresholdPc","NaN"},new[]{"LocationFilterBVoltageLimitKv","-1"},new[]{"LocationFilterBMinimumClusterSize","2.5"},new[]{"LocationFilterBFromM","401"},new[]{"LocationFilterBToM","100001"},new[]{"LocationFilterBVersion","999"}})
                    Check(!Load(c,project,filterBSession,ChangeSetting((string[,])filterBSession[3],invalid[0],invalid[1]))&&(string)Get(c,"LocationFilterAlgorithmId")=="B"&&(double)Get(c,"LocationFilterBChargeThresholdPc")==24.5d&&Same(bBefore,(double[,])Get(c,"ResultParameterMatrix")),"invalid filter setting rejected atomically: "+invalid[0]);
                Check(!Load(c,project,filterBSession,ChangeSetting((string[,])filterBSession[3],"LocationFilterBVoltageLimitKv",null,true)),"incomplete B snapshot cannot invent missing parameters");
                Check(Load(c,project,session,ChangeSetting((string[,])session[3],"LocationFilterAlgorithmId",null,true))&&(string)Get(c,"LocationFilterAlgorithmId")=="A","legacy session without filter identity defaults to A");
                Check(Load(c,project,session),"restore A manual choices after B archive checks");
                Check((string)Get(c,"AlgorithmDiagnosticsJson")==originalDiagnostics,"source diagnostics survive session restoration unchanged");
                Check(((double[,])Get(c,"ResultParameterMatrix")).Cast<double>().Where((v,i)=>i%36==35).All(v=>v==0),"session restore never accepts viewed pulses");
                var old=ChangeSetting((string[,])session[3],"ReviewedRows",null,true);
                Check(Load(c,project,session,old)&&Viewed(c).All(v=>!v),"legacy session without viewed field defaults to unreviewed");
                Check(Load(c,project,session),"restore exact review state before invalid archive checks");
                foreach(string invalid in new[]{"10","1010","1x1",null})
                    Check(!Load(c,project,session,ChangeSetting((string[,])session[3],"ReviewedRows",invalid))&&Viewed(c).SequenceEqual(new[]{true,false,true}),"invalid review bits rejected atomically: "+invalid);
                var rollback=(Action)Call(c,"CaptureNamedAnalysisRollback");Call(c,"SelectSourceRow",1);Call(c,"MarkCurrentReviewed");rollback();
                Check(Viewed(c).SequenceEqual(new[]{true,false,true}),"archive rollback restores exact reviewed rows without marking selection");
                Check((string)Get(c,"AlgorithmDiagnosticsJson")==originalDiagnostics,"archive rollback restores exact source diagnostics");
                Check((bool)Call(c,"SaveNamedAnalysis","reviewed-state"),"named archive saves viewed state: "+Get(c,"LastError"));string archive=(string)Get(c,"LastSavedAnalysisId");
                Call(c,"SelectSourceRow",1);Call(c,"MarkCurrentReviewed");Check((bool)Call(c,"LoadSavedAnalysis",archive)&&Viewed(c).SequenceEqual(new[]{true,false,true}),"named archive restoration selection does not fabricate viewing");
                Check(!(bool)Get(c,"HasUnsavedAnalysisChanges"),"restored reviewed archive is clean");
                double[,] merged,mw;Matrices(catalog,4,out merged,out mw);
                Check((bool)Call(c,"ReloadWithRetainedReview",merged,mw,new[]{0,1,2,-1})&&Viewed(c).SequenceEqual(new[]{true,false,true,false}),"merge retains old rows while new analysis starts unreviewed");
                Check((bool)Call(c,"DeleteLocationAnalysis",first)&&Viewed(c).SequenceEqual(new[]{true}),"deleting one LOC retains remaining row's reviewed state");
                Check(Load(c,project,session),"restore full session before changed source trimming");
                File.AppendAllText(first,"changed");Check(Load(c,project,session)&&Viewed(c).SequenceEqual(new[]{true})&&(int)Get(c,"SourceRowCount")==1,"session changed-file keep mapping clips reviewed bits with matrix rows");
                Check(!((Dictionary<string,string>)Get(c,"algorithmDiagnostics")).ContainsKey(Path.GetFileName(first))&&((Dictionary<string,string>)Get(c,"algorithmDiagnostics")).ContainsKey(Path.GetFileName(second)),"changed source cannot inherit stale reflection identity diagnostics");
                File.WriteAllBytes(first,new byte[256]);Check(Load(c,project,session),"fixture source restored");
                Call(c,"ForceReloadDataMatrices",p,w);Call(c,"SelectSourceRow",0);
                Check((bool)Call(c,"AcceptCurrent"),"explicit acceptance succeeds independently");
                Check(((double[,])Get(c,"ResultParameterMatrix"))[0,35]==1&&Viewed(c)[0],"accepted row is also reviewed");
                Check(Record(c,first).Nodes[0].StateImageIndex==8,"accepted pulse uses green tick state");
                Check((bool)Call(c,"DeleteCurrent")&&((double[,])Get(c,"ResultParameterMatrix"))[0,35]==0&&Viewed(c)[0],"cancel acceptance retains reviewed state");
                Check(((bool[])Get(c,"RemovedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==10,"explicit removal uses muted red cross");
                var removedSession=Export(c);Check(Setting((string[,])removedSession[3],"RemovedRows")=="100","removal persists separately from acceptance");
                Check((bool)Call(c,"MarkCurrentReviewed")&&!((bool[])Get(c,"RemovedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==9,"explicit review can restore neutral unaccepted state");
                Check(Load(c,project,removedSession)&&((bool[])Get(c,"RemovedRows"))[0],"session restores removed state");
                Check((bool)Call(c,"SelectSourceRow",0)&&(bool)Call(c,"AcceptCurrent")&&!((bool[])Get(c,"RemovedRows"))[0],"reaccept clears removed state");
                Check(Load(c,project,removedSession),"restore removal for invalid-state checks");
                foreach(string invalid in new[]{"10","1000","1x0",null})Check(!Load(c,project,removedSession,ChangeSetting((string[,])removedSession[3],"RemovedRows",invalid))&&((bool[])Get(c,"RemovedRows"))[0],"invalid removal bits rejected atomically");
                var acceptedInvalid=(double[,])((double[,])removedSession[0]).Clone();acceptedInvalid[0,35]=1;
                Check(!(bool)Call(c,"LoadAnalysisSession",project,acceptedInvalid,removedSession[1],removedSession[2],removedSession[3]),"accepted and removed contradiction is rejected");
                var legacy=ChangeSetting((string[,])removedSession[3],"RemovedRows",null,true);
                Check(!Load(c,project,removedSession,legacy),"removing the deletion flag cannot fabricate an active confirmed acceptance");
                legacy=ChangeSetting(legacy,"AcceptanceConfirmedRows",null,true);
                Check(Load(c,project,removedSession,legacy)&&((bool[])Get(c,"RemovedRows")).All(v=>!v),"legacy rejection is never inferred as removal");
                Call(c,"SelectSourceRow",1);var intact=(double[,])Get(c,"ResultParameterMatrix");
                Check((bool)Call(c,"DeleteCurrent")&&((bool[])Get(c,"RemovedRows"))[1],"explicitly rejecting an unaccepted pulse is supported");
                Check(Same(intact,(double[,])Get(c,"ResultParameterMatrix")),"rejecting an unaccepted pulse preserves all scientific matrix values");
                Call(c,"SelectSourceRow",0);Check((bool)Call(c,"AcceptCurrent"),"fixture pulse is accepted for region undo");
                var plot=Get(c,"Spectrum");Set(plot,"DisplayModeIndex",0);Call(plot,"AddSelectionRegion",0d,414d,0d,1000d,false);Set(plot,"RegionActionCommand",1);
                Check(((bool[])Get(c,"RemovedRows"))[0]&&((double[,])Get(c,"ResultParameterMatrix"))[0,35]==0,"region deletion sets the same red-cross state");
                Check((bool)Call(plot,"UndoLastRegionDeletion")&&!((bool[])Get(c,"RemovedRows"))[0]&&((double[,])Get(c,"ResultParameterMatrix"))[0,35]==1,"undo restores acceptance and clears removed status consistently");
                Check(((bool[])Get(c,"AcceptanceConfirmedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==8,"region undo retains the prior explicit acceptance decision");
                var candidate=(double[,])p.Clone();candidate[0,35]=1;
                Check((bool)Call(c,"ForceReloadDataMatrices",candidate,w)&&(bool)Call(c,"SelectSourceRow",0),"engineering candidate fixture loads without operator confirmation");
                Check(!((bool[])Get(c,"AcceptanceConfirmedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==8,"algorithm-accepted output is green without fabricating manual confirmation");
                Check(Record(c,first).ToolTipText.Contains("已接受：1")&&Record(c,first).Nodes[0].ToolTipText.Contains("自动分析"),"accepted count matches result inclusion and retains automatic provenance");
                Check((bool)Call(c,"ViewSourceRow",0)&&Record(c,first).Nodes[0].StateImageIndex==8&&Same(candidate,(double[,])Get(c,"ResultParameterMatrix")),"viewing preserves algorithm-accepted status and scientific inclusion");
                Check(!(bool)Get(c,"CanAccept")&&!(bool)Call(c,"AcceptCurrent")&&!((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"an already accepted automatic result disables repeat acceptance without fabricated manual provenance");
                Check((bool)Call(c,"MarkCurrentUncertain")&&(bool)Call(c,"AcceptCurrent")&&((bool[])Get(c,"AcceptanceConfirmedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==8,"explicit uncertain-to-accepted transition records manual provenance");
                var confirmedSession=Export(c);Check(Setting((string[,])confirmedSession[3],"AcceptanceConfirmedRows")=="100","explicit acceptance provenance persists by source row");
                Check(Load(c,project,confirmedSession)&&((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"session restores explicit acceptance provenance");
                foreach(string invalid in new[]{"10","1000","1x0",null})Check(!Load(c,project,confirmedSession,ChangeSetting((string[,])confirmedSession[3],"AcceptanceConfirmedRows",invalid))&&((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"invalid acceptance provenance is rejected atomically");
                Check(Load(c,project,confirmedSession,ChangeSetting((string[,])confirmedSession[3],"AcceptanceConfirmedRows",null,true))&&((bool[])Get(c,"AcceptanceConfirmedRows")).All(value=>!value)&&Record(c,first).Nodes[0].StateImageIndex==8,"legacy accepted rows remain green without inventing manual confirmation");
                Check((bool)Call(c,"SelectSourceRow",0)&&(bool)Call(c,"MarkCurrentReviewed")&&Record(c,first).Nodes[0].StateImageIndex==9&&!((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"explicit legacy review API now makes an uncertain decision");
                Check((bool)Call(Get(c,"AnalysisTree"),"SetMapAcceptedBySourceRow",0,true)&&((bool[])Get(c,"AcceptanceConfirmedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==8,"explicit tree acceptance confirms an uncertain candidate");
                Check((bool)Call(Get(c,"AnalysisTree"),"SetMapAcceptedBySourceRow",0,false)&&((bool[])Get(c,"RemovedRows"))[0]&&Record(c,first).Nodes[0].StateImageIndex==10,"explicit tree removal uses the same persisted decision state");
                Check(c.Controls.Find("ReviewMarkReviewed",true).Length==0,"separate mark-reviewed action removed from all layouts");
                Check((bool)Call(c,"ForceReloadDataMatrices",p,w)&&Viewed(c).All(value=>!value),"new load remains unseen");
                var untouched=(double[,])Get(c,"ResultParameterMatrix");
                Call(Get(c,"AnalysisTree"),"Activate",Record(c,first).Nodes[0]);
                Check(Viewed(c).SequenceEqual(new[]{false,false,false})&&Record(c,first).Nodes[0].StateImageIndex==7,"human tree activation does not decide uncertainty");
                Check(Same(untouched,(double[,])Get(c,"ResultParameterMatrix"))&&((bool[])Get(c,"AcceptanceConfirmedRows")).All(value=>!value),"viewing does not accept or change numerical inclusion");
                Check((bool)Call(c,"NextPdMap")&&!Viewed(c)[1],"explicit next navigation keeps pending decision");
                Check((bool)Call(c,"PreviousPdMap")&&(bool)Call(c,"AcceptCurrent"),"operator can confirm an opened pulse");
                Check((bool)Call(c,"ViewSourceRow",0)&&Record(c,first).Nodes[0].StateImageIndex==8,"viewing never clears an accepted decision");
                Check((bool)Call(c,"DeleteCurrent")&&(bool)Call(c,"ViewSourceRow",0)&&Record(c,first).Nodes[0].StateImageIndex==10,"viewing never clears an excluded decision");
                var viewedSession=Export(c);Check(Load(c,project,viewedSession)&&Viewed(c).SequenceEqual(new[]{true,false,false}),"decisions persist without marking merely viewed rows on restore");
                var automatic=(double[,])p.Clone();automatic[0,35]=automatic[1,35]=1;
                Check((bool)Call(c,"ForceReloadDataMatrices",automatic,w)&&Viewed(c).All(value=>!value),"accepted fixture remains not manually viewed on load");
                Check(Record(c,first).Text.Contains("已接受")&&Record(c,first).Nodes.Cast<TreeNode>().All(n=>n.StateImageIndex==8),"fully accepted unviewed file never says pending review");
                Check(Record(c,first).ToolTipText.Contains("已接受：2")&&Record(c,first).ToolTipText.Contains("待复核：0"),"record counters exclude accepted rows from pending review");
                var automaticSession=Export(c);Check(Load(c,project,automaticSession)&&Record(c,first).Text.Contains("已接受")&&((bool[])Get(c,"AcceptanceConfirmedRows")).All(value=>!value),"accepted automatic state survives archive without creating manual provenance");
                Set(c,"LanguageIndex",1);Check(Record(c,first).Text.Contains("Accepted")&&Record(c,first).Nodes[0].ToolTipText.Contains("automatic analysis"),"English acceptance matches automatic result state");Set(c,"LanguageIndex",0);
                Call(Get(c,"AnalysisTree"),"Toggle",Record(c,first).Nodes[0]);
                Check(Record(c,first).Nodes[0].StateImageIndex==10&&((double[,])Get(c,"ResultParameterMatrix"))[0,35]==0,"green automatic icon toggles to excluded, not another acceptance");
                Check(Record(c,first).Text.Contains("已处理")&&!Record(c,first).Text.Contains("待复核"),"accepted and removed mixture is resolved despite unviewed accepted row");
                Call(Get(c,"AnalysisTree"),"Toggle",Record(c,first).Nodes[0]);
                Check(Record(c,first).Text.Contains("已接受")&&((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"reaccepting removed row preserves explicit confirmation");
                Check((bool)Call(c,"ForceReloadDataMatrices",candidate,w)&&Record(c,first).Text.Contains("待复核"),"unaccepted unseen row alone keeps mixed record pending");
                Check((bool)Call(c,"ViewSourceRow",1)&&Record(c,first).Text.Contains("待复核")&&Record(c,first).Nodes[0].StateImageIndex==8&&Record(c,first).Nodes[1].StateImageIndex==7,"accepted plus merely viewed pending remains pending");
                Check((bool)Call(c,"MarkCurrentUncertain")&&Record(c,first).Text.Contains("不确定")&&Record(c,first).Nodes[1].StateImageIndex==9,"explicit uncertainty is reflected in pulse and record");
                Call(Get(c,"AnalysisTree"),"SetMapAcceptedBySourceRow",0,false);Call(Get(c,"AnalysisTree"),"SetMapAcceptedBySourceRow",1,false);
                Check(Record(c,first).Text.Contains("已删除")&&Record(c,first).Nodes.Cast<TreeNode>().All(n=>n.StateImageIndex==10),"all excluded file shows removed, never pending or accepted");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(qa,"reviewed-tree.png"));}
                host.Close();
            }
            Console.WriteLine("ANALYSIS_REVIEWED_STATE_PASS="+checks+"; HardwareAccessed=False; QA="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("FAIL check "+checks+": "+ex);return 1;}
    }
}
