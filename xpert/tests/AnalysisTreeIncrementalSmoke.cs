using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Synthetic UI only: no analysis algorithm, SDK, hardware or user project.
static class AnalysisTreeIncrementalSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static object Get(object value,string name){var p=value.GetType().GetProperty(name,B);return p!=null?p.GetValue(value,null):value.GetType().GetField(name,B).GetValue(value);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static void Event(object value,string name,EventHandler handler){value.GetType().GetEvent(name).AddEventHandler(value,handler);}
    static bool Same(double[,] first,double[,] second){return first.GetLength(0)==second.GetLength(0)&&first.GetLength(1)==second.GetLength(1)&&first.Cast<double>().Zip(second.Cast<double>(),(a,b)=>a.Equals(b)).All(x=>x);}
    static double[,] Matrix(int count)
    {
        var p=new double[count,36];
        for(int row=0;row<count;row++){int file=row/200;p[row,0]=file==2?2:1;p[row,1]=file;p[row,2]=file==0?6:12;p[row,9]=row%200;p[row,10]=20;p[row,13]=row;p[row,15]=100;p[row,35]=0;}
        return p;
    }
    static void ReviewDecisionContract(Assembly asm,string qa)
    {
        string project=Path.Combine(qa,"review-project");Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"414\"\r\n");
        File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t414m\r\n");
        File.WriteAllBytes(Path.Combine(project,"A_13.40kV_1.loc"),new byte[256]);
        asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"review-preferences.json")));
        using(var page=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.PdReviewControl",true)))
        using(var form=new Form{ClientSize=new Size(1200,800),Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false}){
            page.GetType().GetProperty("EnableAiFeatures").SetValue(page,false,null);
            page.GetType().GetProperty("PromptSavedAnalysisOnProjectLoad").SetValue(page,false,null);
            page.GetType().GetProperty("ProjectPath").SetValue(page,project,null);
            form.Controls.Add(page);page.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            object tree=Get(page,"AnalysisTree");var catalog=(double[,])Call(tree,"GetDataCatalog");
            var p=new double[3,36];var w=new double[3,4096];
            for(int r=0;r<3;r++){
                p[r,0]=catalog[0,0];p[r,1]=catalog[0,1];p[r,2]=catalog[0,2];p[r,3]=45;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;
                p[r,9]=r;p[r,10]=100;p[r,11]=710;p[r,12]=4.88;p[r,13]=200;p[r,15]=p[r,16]=180;p[r,20]=100;p[r,21]=.92;p[r,32]=4095;p[r,33]=.91;
                for(int i=0;i<4096;i++)w[r,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
            }
            Check((bool)Call(page,"LoadDataMatrices",p,w)&&(bool)Call(page,"SelectSourceRow",0),"Actual review fixture selects an exact valid source row");
            int notifications=0,treeNotices=0;Event(page,"ResultsChanged",(s,e)=>notifications++);
            foreach(string name in new[]{"DataChanged","PendingSelectionChanged","SelectedOutputChanged"})Event(tree,name,(s,e)=>treeNotices++);
            foreach(string action in new[]{"AcceptCurrent","DeleteCurrent","MarkCurrentUncertain"}){
                int sequence=(int)Get(page,"ResultChangeSequence");long pass=(long)Get(tree,"StateRefreshPassCount"),nodes=(long)Get(tree,"StateRefreshNodeCount"),rebuild=(long)Get(tree,"TreeRebuildCount");notifications=0;
                Check((bool)Call(page,action),"Actual review decision "+action);
                Check(notifications==1&&(int)Get(page,"ResultChangeSequence")==sequence+1,"Actual "+action+" publishes one current ResultsChanged/chart update contract");
                Check((long)Get(tree,"StateRefreshPassCount")==pass+1&&(long)Get(tree,"StateRefreshNodeCount")==nodes+4&&(long)Get(tree,"TreeRebuildCount")==rebuild,"Actual "+action+" commits review flags and changed row in one local refresh");
                int priorTreeNotices=treeNotices;Call(page,"SyncAnalysisTree",false);Call(page,"SyncReviewedTree");
                Check(notifications==1&&treeNotices==priorTreeNotices&&(long)Get(tree,"StateRefreshPassCount")==pass+1,"Repeated actual host synchronization is an equal-value no-op, including source selection notices");
            }
            Check(((double[,])Get(page,"ResultParameterMatrix"))[0,35]==0&&((bool[])Get(page,"ReviewedRows"))[0]&&!((bool[])Get(page,"RemovedRows"))[0],"Uncertain remains reviewed and excluded, not a fabricated accepted position");
            form.Close();
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("Fresh QA fixture required.");Directory.CreateDirectory(qa);
            var asm=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            using(var control=(Control)Activator.CreateInstance(asm.GetType("LabVIEWLocationTree.LocationTreeControl",true)))
            using(var host=new Form{ClientSize=new Size(320,600),Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false}){
                host.Controls.Add(control);control.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                var paths=Enumerable.Range(0,4).Select(i=>Path.Combine(qa,"record-"+i+".loc")).ToArray();
                Check((bool)Call(control,"LoadRecords",paths,new[]{1,1,2,2},new[]{6d,12d,12d,18d},new[]{1,2,1,2},new[]{0d,1d,2d,3d}),"Load synthetic exact record identities");
                var matrix=Matrix(600);Check((bool)Call(control,"SetParameterMatrix",matrix),"Initial matrix");
                int data=0,pending=0,selected=0;
                Event(control,"DataChanged",(s,e)=>data++);Event(control,"PendingSelectionChanged",(s,e)=>pending++);Event(control,"SelectedOutputChanged",(s,e)=>selected++);
                long sequence=(long)Get(control,"MatrixChangeSequence");
                var watch=Stopwatch.StartNew();
                for(int i=0;i<150;i++)Check((bool)Call(control,"SetParameterMatrix",(double[,])matrix.Clone()),"Repeated exact matrix accepted "+i);
                watch.Stop();
                Check(data==0&&pending==0&&selected==0&&(long)Get(control,"MatrixChangeSequence")==sequence,"Equal matrix causes zero notifications and zero new commit");
                Console.WriteLine("EQUAL_600_ROW_COMMITS=150;ELAPSED_MS="+watch.Elapsed.TotalMilliseconds.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
                var tree=(TreeView)Get(control,"tree");
                Check(!(bool)Call(control,"SelectMap",1),"Repeated local MAP ID is ambiguous, not a row identity");
                Check((bool)Call(control,"SelectSourceRow",230)&&(int)Get(control,"SelectedMatrixRowIndex")==230,"Exact row selection is retained across duplicate MAP IDs");
                Check((bool)Call(control,"SetDataSelected",paths[3],true),"Pending file checkbox remains independent");
                var beforeNode=tree.SelectedNode;var top=tree.TopNode;string[] expanded=(string[])Call(control,"GetExpandedNodeKeys");
                var reviewed=new bool[600];var removed=new bool[600];var confirmed=new bool[600];
                reviewed[230]=confirmed[230]=true;matrix[230,35]=1;
                data=pending=selected=0;long passes=(long)Get(control,"StateRefreshPassCount"),nodes=(long)Get(control,"StateRefreshNodeCount"),rebuild=(long)Get(control,"TreeRebuildCount");
                Check((bool)Call(control,"SetParameterMatrixAndReviewStates",matrix,reviewed,removed,confirmed),"Atomic acceptance+review snapshots");
                Check((long)Get(control,"StateRefreshPassCount")==passes+1&&(long)Get(control,"StateRefreshNodeCount")==nodes+4,"One changed MAP refreshes only MAP, file, phase, root once");
                Check((long)Get(control,"TreeRebuildCount")==rebuild&&Object.ReferenceEquals(beforeNode,tree.SelectedNode)&&Object.ReferenceEquals(top,tree.TopNode),"Nonstructural commit keeps selected and scrolled node objects");
                Check(((string[])Call(control,"GetExpandedNodeKeys")).SequenceEqual(expanded),"Expanded state unchanged");
                Check(((string[])Call(control,"GetSelectedDataPaths")).SequenceEqual(new[]{paths[3]}),"Pending checkbox preserved");
                Check(tree.SelectedNode.StateImageIndex==8&&tree.SelectedNode.ToolTipText.Contains("已接受")&&(int)Get(control,"SelectedMatrixRowIndex")==230,"Atomic state displays acceptance and original row without stale selection");
                Check(data==1&&pending==1&&selected==1,"One real matrix commit publishes each existing contract event once");
                passes=(long)Get(control,"StateRefreshPassCount");nodes=(long)Get(control,"StateRefreshNodeCount");data=pending=selected=0;
                Check((bool)Call(control,"SetParameterMatrixAndReviewStates",(double[,])matrix.Clone(),(bool[])reviewed.Clone(),(bool[])removed.Clone(),(bool[])confirmed.Clone()),"Equal atomic snapshot accepted");
                Check((long)Get(control,"StateRefreshPassCount")==passes&&(long)Get(control,"StateRefreshNodeCount")==nodes&&data==0&&pending==0&&selected==0,"Equal atomic snapshot performs zero refresh and notification");
                reviewed[231]=true;removed[231]=true;
                Check((bool)Call(control,"SetReviewStates",reviewed,removed,confirmed),"Review-only atomic state");
                Check((long)Get(control,"StateRefreshPassCount")==passes+1&&data==0&&pending==0&&selected==0,"Review-only state refresh does not manufacture matrix or analysis notification");
                passes=(long)Get(control,"StateRefreshPassCount");
                Check((bool)Call(control,"SetReviewedRows",reviewed)&&(bool)Call(control,"SetRemovedRows",removed)&&(bool)Call(control,"SetAcceptanceConfirmedRows",confirmed),"Existing public single setters remain compatible");
                Check((long)Get(control,"StateRefreshPassCount")==passes,"Equal legacy single setters cause zero refresh");
                var bad=(double[,])matrix.Clone();bad[230,35]=0;
                Check(!(bool)Call(control,"SetParameterMatrixAndReviewStates",bad,reviewed,new bool[599],confirmed),"Wrong row-count snapshot rejected before matrix mutation");
                Check(Same(matrix,(double[,])Call(control,"GetParameterMatrix")),"Bad atomic snapshot leaves all matrix values unchanged");
                string selectedKey=(string)Call(control,"GetSelectedNodeKey"),topKey=(string)Call(control,"GetTopNodeKey");
                var reordered=(double[,])matrix.Clone();for(int col=0;col<36;col++){double value=reordered[230,col];reordered[230,col]=reordered[231,col];reordered[231,col]=value;}
                bool valueState=reviewed[230];reviewed[230]=reviewed[231];reviewed[231]=valueState;valueState=removed[230];removed[230]=removed[231];removed[231]=valueState;valueState=confirmed[230];confirmed[230]=confirmed[231];confirmed[231]=valueState;
                Check((bool)Call(control,"SetParameterMatrixAndReviewStates",reordered,reviewed,removed,confirmed),"Identity reorder uses normal full rebuild");
                Check((long)Get(control,"TreeRebuildCount")==rebuild+1&&!Object.ReferenceEquals(beforeNode,tree.SelectedNode),"Structural snapshot cannot share old MAP nodes");
                Check((string)Call(control,"GetSelectedNodeKey")==selectedKey&&(string)Call(control,"GetTopNodeKey")==topKey&&((string[])Call(control,"GetExpandedNodeKeys")).SequenceEqual(expanded),"Structural rebuild restores exact selection, scroll and expansion keys");
                Check((int)Get(control,"SelectedMatrixRowIndex")==231&&(bool)Call(control,"SelectSourceRow",230)&&(int)Get(control,"SelectedMatrixRowIndex")==230,"Row-to-node cache now uses the new snapshot identities");
                bool reentered=false;int lateOutputs=0;
                Event(control,"DataChanged",(s,e)=>{if(reentered)return;reentered=true;Call(control,"LoadRecords",new[]{Path.Combine(qa,"new-session.loc")},new[]{3},new[]{9d},new[]{1},new[]{90d});});
                Event(control,"SelectedOutputChanged",(s,e)=>lateOutputs++);
                reordered[0,13]+=1;Check((bool)Call(control,"SetParameterMatrix",reordered),"Host can synchronously replace the record snapshot");
                Check(reentered&&lateOutputs==1&&(int)Get(control,"SelectedMatrixRowIndex")==-1&&!(bool)Call(control,"SelectSourceRow",230),"Old commit stops notifications after source generation changes and cannot select an old row");
                host.Close();
            }
            ReviewDecisionContract(asm,qa);
            Console.WriteLine("ANALYSIS_TREE_INCREMENTAL_PASS="+checks+";HARDWARE_ACCESSED=False;SDK_CALLED=False;ALGORITHM_EXECUTED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
