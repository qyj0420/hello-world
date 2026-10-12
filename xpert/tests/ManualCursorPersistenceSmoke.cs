using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ManualCursorPersistenceSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type controlType;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
    static Control Create(string project){var c=(Control)Activator.CreateInstance(controlType);Set(c,"PromptSavedAnalysisOnProjectLoad",false);Set(c,"ProjectPath",project);Set(c,"AutomaticDisplayRecommendation",false);return c;}
    static bool[] Exact(object c){return (bool[])Get(c,"ExactManualPositionRows");}
    static void At(object c,int sample,string message)
    {
        var map=Get(c,"PdMap");Check((int)Get(c,"SelectedSourceRowIndex")==0&& (int)Get(map,"CurrentReflectionIndex")==sample,message+": exact cursor expected="+sample+" actual="+Get(map,"CurrentReflectionIndex")+" row="+Get(c,"SelectedSourceRowIndex"));
        var result=(double[,])Get(c,"ResultParameterMatrix");Check(result[0,11]==sample,message+": matrix matches cursor");
        Check(!(bool)Get(map,"PositionIsApproximated"),message+": not approximated");
        var effective=(double[,])Get(c,"EffectiveResultParameterMatrix");Check(effective[0,13].Equals(result[0,13]),message+": effective display retains manual location");
        Check(result[0,35]==1&&!((bool[])Get(c,"AcceptanceConfirmedRows"))[0],message+": neither excludes nor fabricates manual acceptance");
    }
    static object[] Export(object c){object[] a={null,null,null,null};Check((bool)Call(c,"ExportAnalysisSession",a),"session exports: "+Get(c,"LastError"));return a;}
    static string[,] Change(string[,] settings,string value,bool remove=false)
    {
        var indices=Enumerable.Range(0,settings.GetLength(0)).Where(i=>!remove||settings[i,0]!="ExactManualPositionRows").ToArray();var copy=new string[indices.Length,2];
        for(int i=0;i<indices.Length;i++){copy[i,0]=settings[indices[i],0];copy[i,1]=copy[i,0]=="ExactManualPositionRows"?value:settings[indices[i],1];}return copy;
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"manual-persistence");Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            controlType=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
            string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"414\"\r\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t414m\r\n",new UTF8Encoding(false));
            string first=Path.Combine(project,"A_13.40kV_1.loc"),second=Path.Combine(project,"A_13.40kV_2.loc");File.WriteAllBytes(first,new byte[256]);File.WriteAllBytes(second,new byte[256]);
            using(var c=Create(project))using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000),ClientSize=new Size(1300,900)}){
                host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                var catalog=(double[,])Call(Get(c,"AnalysisTree"),"GetDataCatalog");Check(catalog.GetLength(0)==2,"two synthetic source files");
                var p=new double[3,36];var w=new double[3,4096];
                for(int r=0;r<3;r++){int file=r==2?1:0;p[r,0]=catalog[file,0];p[r,1]=catalog[file,1];p[r,2]=catalog[file,2];p[r,3]=45;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;p[r,9]=r;p[r,10]=100;p[r,11]=500;p[r,12]=3.2;p[r,13]=200;p[r,14]=609;p[r,15]=p[r,16]=180;p[r,20]=100;p[r,21]=.92;p[r,23]=100;p[r,24]=100000;p[r,32]=4095;p[r,33]=.91;p[r,35]=1;for(int i=0;i<4096;i++)w[r,i]=180*Math.Exp(-Math.Pow((i-100)/8d,2))+60*Math.Exp(-Math.Pow((i-500)/7d,2));}
                Check((bool)Call(c,"LoadDataMatrices",p,w),"load matrices");Set(c,"AutomaticDisplayRecommendation",false);var map=Get(c,"PdMap");
                Check(!Exact(c).Any(v=>v),"automatic load creates no manual flags");Call(c,"SelectSourceRow",0);
                Call(map,"MoveReflectionToIndex",518);Call(map,"SetConnectors",new[]{(double)Get(map,"LocationM")+0.8});Call(c,"SelectSourceRow",1);Call(c,"SelectSourceRow",0);At(c,518,"manual near joint row round trip");
                Call(map,"MoveReflectionToIndex",105);At(c,105,"manual near endpoint");Check(Exact(c).SequenceEqual(new[]{true,false,false}),"manual flag belongs only to source row");
                Check((bool)Call(c,"NextPdMap")&&(bool)Call(c,"PreviousPdMap"),"explicit next and previous work");At(c,105,"next previous");
                foreach(int mode in new[]{0,1}){Set(map,"PositioningMode",mode);Set(map,"ApproximateCablePositions",false);Set(map,"ApproximateCablePositions",true);At(c,105,"timing/display option changes");}
                Check((bool)Call(c,"LoadDataMatrices",p,w),"unchanged source refresh succeeds");At(c,105,"same source refresh");
                Check(!(bool)Call(c,"ForceReloadDataMatrices",p,new double[0,0]),"failed load rejected");At(c,105,"failed reload rollback");
                Set(c,"DischargePositionFilterM",new[]{190d,210d});Call(c,"SelectSourceRow",0);Call(map,"MoveReflectionToIndex",106);At(c,106,"edit outside statistics");
                Check(!((int[])Get(c,"FilteredSourceRowIndices")).Contains(0),"inspection does not reinclude filtered row");
                Set(c,"AutomaticDisplayRecommendation",true);Call(c,"QueueDisplayAdvice");var until=DateTime.UtcNow.AddMilliseconds(1400);while(DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(10);}At(c,106,"automatic display advice");Check(!((int[])Get(c,"FilteredSourceRowIndices")).Contains(0),"async advice retains position exclusion");Set(c,"AutomaticDisplayRecommendation",false);
                var saved=Export(c);var settings=(string[,])saved[3];Check(Enumerable.Range(0,settings.GetLength(0)).Any(i=>settings[i,0]=="ExactManualPositionRows"&&settings[i,1]=="100"),"row-aligned manual metadata saved");
                using(var restored=Create(project)){
                    Check((bool)Call(restored,"LoadAnalysisSession",project,saved[0],saved[1],saved[2],saved[3]),"fresh instance restores session: "+Get(restored,"LastError"));Call(restored,"SelectSourceRow",0);At(restored,106,"fresh instance session");Check(Exact(restored).SequenceEqual(new[]{true,false,false}),"restored manual provenance");
                    Call(restored,"SelectSourceRow",1);Call(restored,"SelectSourceRow",0);At(restored,106,"restored row round trip");
                    foreach(string invalid in new[]{"10","1000","1x0",null}){Check(!(bool)Call(restored,"LoadAnalysisSession",project,saved[0],saved[1],saved[2],Change(settings,invalid)),"invalid manual mask rejected");At(restored,106,"bad metadata preserves active state");}
                    Check((bool)Call(restored,"DeleteLocationAnalysis",second),"remove another source file");Call(restored,"SelectSourceRow",1);Call(restored,"SelectSourceRow",0);At(restored,106,"retained rows after file removal");Check(Exact(restored).SequenceEqual(new[]{true,false}),"retained row mask follows subset");
                    var rollback=(Action)Call(restored,"CaptureNamedAnalysisRollback");Call(Get(restored,"PdMap"),"MoveReflectionToIndex",107);rollback();Call(restored,"SelectSourceRow",0);At(restored,106,"archive rollback");
                }
                using(var legacy=Create(project)){Check((bool)Call(legacy,"LoadAnalysisSession",project,saved[0],saved[1],saved[2],Change(settings,null,true)),"legacy session without marker remains readable");Check(!Exact(legacy).Any(v=>v),"legacy session does not invent manual provenance");}
                Check(((double[,])Get(c,"OriginalParameterMatrix"))[0,11]==500,"original timing untouched");
                Check((bool)Call(c,"ForceReloadDataMatrices",p,w)&&!Exact(c).Any(v=>v),"new analysis resets manual provenance");
            }
            Console.WriteLine("MANUAL_CURSOR_PERSISTENCE_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine("FAIL "+checks+": "+e);return 1;}
    }
}
