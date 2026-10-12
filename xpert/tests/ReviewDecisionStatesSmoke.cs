using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ReviewDecisionStatesSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type controlType;
    static void Check(bool v,string n){checks++;if(!v)throw new InvalidOperationException(n);}
    static object Call(object v,string n,params object[] a){return v.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(v,a);}
    static object Get(object v,string n){var p=v.GetType().GetProperty(n,All);return p!=null?p.GetValue(v,null):v.GetType().GetField(n,All).GetValue(v);}
    static void Set(object v,string n,object a){v.GetType().GetProperty(n,All).SetValue(v,a,null);}
    static IEnumerable<TreeNode> Nodes(TreeNodeCollection ns){foreach(TreeNode n in ns){yield return n;foreach(var c in Nodes(n.Nodes))yield return c;}}
    static TreeNode Record(Control c,string path){return Nodes(((TreeView)Get(Get(c,"AnalysisTree"),"tree")).Nodes).Single(n=>n.ToolTipText.Contains(path));}
    static object[] Export(Control c){object[] o={null,null,null,null};Check((bool)Call(c,"ExportAnalysisSession",o),"Export: "+Get(c,"LastError"));return o;}
    static bool Load(Control c,string p,object[] s,string[,] settings=null){return (bool)Call(c,"LoadAnalysisSession",p,s[0],s[1],s[2],settings??(string[,])s[3]);}
    static string[,] Change(string[,] s,string key,string val,bool omit=false){var rows=Enumerable.Range(0,s.GetLength(0)).Where(r=>!omit||s[r,0]!=key).ToArray();var t=new string[rows.Length,2];for(int r=0;r<rows.Length;r++){t[r,0]=s[rows[r],0];t[r,1]=t[r,0]==key?val:s[rows[r],1];}return t;}
    static void State(Control c,string file,string state,int icon,bool a,bool d,bool u,int badge)
    {
        Check((string)Get(c,"CurrentReviewState")==state,"State "+state);
        Check(Record(c,file).Nodes[0].StateImageIndex==icon,"Tree "+state);
        Check(((Button)Get(c,"acceptButton")).Enabled==a,"Accept enabled "+state);
        Check(((Button)Get(c,"deleteButton")).Enabled==d,"Delete enabled "+state);
        Check(((Button)Get(c,"uncertainButton")).Enabled==u,"Uncertain enabled "+state);
        var labels=(Label[])Get(c,"classIndicators");
        for(int i=0;i<3;i++)Check((labels[i].ForeColor==Color.White)==(i==badge),"Badge "+i+" "+state);
    }
    [STAThread] static int Main(string[] args)
    {
        string qa=Path.GetFullPath(args[0]);string report=Path.Combine(qa,"review-decision-verification.json");
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var asm=Assembly.LoadFrom(Path.Combine(qa,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));controlType=asm.GetType("HVX.PdReview.PdReviewControl",true);
            string testRoot=Path.Combine(qa,"review-decisions-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
            asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(testRoot,"prefs.json")));
            string project=Path.Combine(testRoot,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"414\"\r\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t414m\r\n",new UTF8Encoding(false));
            string file=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(file,new byte[256]);
            using(var c=(Control)Activator.CreateInstance(controlType))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(1600,1000)}){
                Set(c,"PromptSavedAnalysisOnProjectLoad",false);Set(c,"LanguageIndex",0);Set(c,"ProjectPath",project);
                host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                var catalog=(double[,])Call(Get(c,"AnalysisTree"),"GetDataCatalog");
                var p=new double[1,36];var w=new double[1,4096];
                p[0,0]=catalog[0,0];p[0,1]=catalog[0,1];p[0,2]=catalog[0,2];p[0,3]=45;p[0,5]=414;p[0,6]=170;p[0,7]=125e6;p[0,10]=100;p[0,11]=710;p[0,12]=4.88;p[0,13]=200;p[0,15]=p[0,16]=180;p[0,20]=100;p[0,21]=.92;p[0,32]=4095;p[0,33]=.91;
                for(int i=0;i<4096;i++)w[0,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
                Check((bool)Call(c,"LoadDataMatrices",p,w),"Load synthetic matrix");
                State(c,file,"Pending",7,true,true,true,-1);
                Call(c,"ViewSourceRow",0);State(c,file,"Pending",7,true,true,true,-1);
                Check((bool)Call(c,"MarkCurrentUncertain"),"Pending -> uncertain");State(c,file,"Uncertain",9,true,true,false,1);
                Check(!(bool)Call(c,"MarkCurrentUncertain"),"Repeated uncertain is no-op");
                var uncertain=Export(c);Check(((double[,])uncertain[0])[0,35]==0,"Uncertain excluded from accepted statistics");
                Check(((double[,])uncertain[1]).Cast<double>().SequenceEqual(w.Cast<double>()),"Source waveform retained");
                Check((bool)Call(c,"AcceptCurrent"),"Uncertain -> accepted");State(c,file,"Accepted",8,false,true,true,0);
                Check(((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"Manual accept provenance");
                Check(!(bool)Call(c,"AcceptCurrent"),"Repeated accept is no-op");
                Check((bool)Call(c,"DeleteCurrent"),"Accepted -> deleted");State(c,file,"Removed",10,true,false,true,2);
                Check(!(bool)Call(c,"DeleteCurrent"),"Repeated deleted is no-op");
                Check((bool)Call(c,"MarkCurrentUncertain"),"Deleted -> uncertain");State(c,file,"Uncertain",9,true,true,false,1);
                Check(!((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"Uncertainty does not retain current manual acceptance");
                Check((bool)Call(c,"DeleteCurrent")&&(bool)Call(c,"AcceptCurrent"),"Deleted -> accepted");State(c,file,"Accepted",8,false,true,true,0);
                Check((bool)Call(c,"MarkCurrentUncertain"),"Accepted -> uncertain");State(c,file,"Uncertain",9,true,true,false,1);
                Check(Load(c,project,uncertain),"Uncertain session reload");State(c,file,"Uncertain",9,true,true,false,1);
                foreach(var bad in new[]{"1","3",null})Check(!Load(c,project,uncertain,Change((string[,])uncertain[3],"ReviewDecisionVersion",bad))&&(string)Get(c,"CurrentReviewState")=="Uncertain","Reject invalid decision version atomically");
                Check(Load(c,project,uncertain,Change((string[,])uncertain[3],"ReviewDecisionVersion",null,true)),"Legacy viewed session migration");State(c,file,"Pending",7,true,true,true,-1);
                Check(Load(c,project,uncertain),"Restore uncertainty");var undo=(Action)Call(c,"CaptureNamedAnalysisRollback");Call(c,"DeleteCurrent");undo();State(c,file,"Uncertain",9,true,true,false,1);
                Check((bool)Call(c,"SaveNamedAnalysis","review-decision"),"Named archive save: "+Get(c,"LastError"));string id=(string)Get(c,"LastSavedAnalysisId");Call(c,"AcceptCurrent");Check((bool)Call(c,"LoadSavedAnalysis",id),"Named archive reload");State(c,file,"Uncertain",9,true,true,false,1);
                Check((bool)Call(c,"ForceReloadDataMatrices",p,w),"Reset source analysis");State(c,file,"Pending",7,true,true,true,-1);
                var automatic=(double[,])p.Clone();automatic[0,35]=1;Check((bool)Call(c,"ForceReloadDataMatrices",automatic,w),"Automatic acceptance");State(c,file,"Accepted",8,false,true,true,0);
                Check(!((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"Automatic result does not invent manual acceptance");
                Check((bool)Call(Get(c,"AnalysisTree"),"SetMapAcceptedBySourceRow",0,true)&&!((bool[])Get(c,"AcceptanceConfirmedRows"))[0],"Tree repeated acceptance leaves provenance automatic");
                var missing=(double[,])p.Clone();missing[0,11]=Double.NaN;
                Check((bool)Call(c,"ForceReloadDataMatrices",missing,w),"Missing reflection can be reviewed");
                Check(!(bool)Get(c,"CanAccept")&&!(bool)Call(c,"AcceptCurrent"),"Unresolved cable reflection cannot be accepted");
                Check((bool)Call(c,"DeleteCurrent")&&(bool)Get(c,"CanMarkUncertain")&&!(bool)Get(c,"CanAccept"),"Deleted unresolved pulse still permits uncertainty, not invalid acceptance");
                Check((bool)Call(c,"MarkCurrentUncertain"),"Deleted unresolved -> uncertain");
                Check((bool)Call(c,"ForceReloadDataMatrices",p,w),"Reload valid reflection fixture");
                var mutable=(double[,])Get(c,"working");double priorLength=mutable[0,5];mutable[0,5]=1000000;
                Check(!(bool)Get(c,"CanAccept"),"Geometry-invalidated completeness cannot be accepted");mutable[0,5]=priorLength;
                Check((bool)Get(c,"CanAccept"),"Valid geometry restores eligibility");
                foreach(int language in new[]{0,1,2})foreach(Size size in new[]{new Size(1600,1000),new Size(1200,800)}){
                    Set(c,"LanguageIndex",language);host.ClientSize=size;Application.DoEvents();
                    var btn=(Button)Get(c,"uncertainButton");Check(btn.Visible&&btn.Parent.ClientRectangle.Contains(btn.Bounds),"Uncertain button present, language "+language+" size "+size);
                }
                Set(c,"EmbeddedMode",true);
                foreach(int language in new[]{0,1,18})foreach(Size size in new[]{new Size(1065,416),new Size(1256,620)}){
                    Set(c,"LanguageIndex",language);host.ClientSize=size;Call(c,"SelectCompactSidebar",false,false);Application.DoEvents();
                    foreach(string button in new[]{"acceptButton","deleteButton","uncertainButton","rangeFilterButton","evaluateButton","reportButton"}){
                        var btn=(Button)Get(c,button);var bounds=c.RectangleToClient(btn.RectangleToScreen(btn.ClientRectangle));
                        Check(btn.Visible&&c.ClientRectangle.Contains(bounds),"Low-height visible "+button+" language="+language+" size="+size+" bounds="+bounds+" canvas="+c.ClientRectangle+" min="+Get(c,"RecommendedMinimumSize")+" font="+c.Font.Height);
                    }
                    Check(!((ScrollableControl)c).HorizontalScroll.Visible&&!((ScrollableControl)c).VerticalScroll.Visible,"Low-height action area needs no workspace scrollbars");
                    using(var bitmap=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bitmap,new Rectangle(Point.Empty,c.Size));bitmap.Save(Path.Combine(qa,"review-decision-low-"+language+"-"+size.Height+".png"));}
                }
                Set(c,"EmbeddedMode",false);Set(c,"LanguageIndex",0);host.ClientSize=new Size(1600,1000);Call(c,"MarkCurrentUncertain");Application.DoEvents();
                using(var bitmap=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(bitmap,new Rectangle(Point.Empty,host.Size));bitmap.Save(Path.Combine(qa,"review-decision-ui.png"));}
            }
            File.WriteAllText(report,new JavaScriptSerializer().Serialize(new{Status="PASS",Checks=checks,HardwareAccessed=false,ProductMainInvoked=false,UserProjectsModified=false,Utc=DateTime.UtcNow.ToString("o")}),Encoding.UTF8);
            Console.WriteLine("PASS "+checks+" review decision checks; no hardware, no product Main");return 0;
        }catch(Exception e){File.WriteAllText(report,new JavaScriptSerializer().Serialize(new{Status="FAIL",Checks=checks,Error=e.ToString(),HardwareAccessed=false}),Encoding.UTF8);Console.Error.WriteLine(e);return 1;}
    }
}
