using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class CompetitionDefectRecognitionSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Assembly assembly;
    static void Check(bool pass,string name){checks++;if(!pass)throw new Exception(name);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void FieldSet(object o,string name,object value){o.GetType().GetField(name,All).SetValue(o,value);}
    static object Call(object o,string name,params object[] values){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(o,values);}
    static Control Named(Control parent,string name){if(parent.Name==name)return parent;foreach(Control child in parent.Controls){var found=Named(child,name);if(found!=null)return found;}return null;}
    static bool Complete(object task){var t=(Task<bool>)task;var watch=Stopwatch.StartNew();while(!t.IsCompleted&&watch.ElapsedMilliseconds<20000){Application.DoEvents();Thread.Sleep(3);}Check(t.IsCompleted,"background task settled");return t.Result;}
    static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    static void Matrix(string path,double[,] value){using(var writer=new BinaryWriter(File.Create(path))){writer.Write(Encoding.ASCII.GetBytes("HVXMAT01"));writer.Write(value.GetLength(0));writer.Write(value.GetLength(1));foreach(double item in value)writer.Write(item);}}
    static string Archive(string project,string id,int count,string created,string saved)
    {
        string directory=Path.Combine(project,"Analysis report",id);Directory.CreateDirectory(directory);var matrix=new double[count,36];
        for(int r=0;r<count;r++){matrix[r,0]=r==count-1?2:1;matrix[r,1]=1;matrix[r,2]=r%2==0?8.7:12.2;matrix[r,3]=r<3?32+r:130+r;matrix[r,5]=414;matrix[r,13]=r<3?80+r:260+r;matrix[r,15]=r%2==0?100+r:-100-r;matrix[r,16]=999;matrix[r,35]=r==count-2?0:1;}
        Matrix(Path.Combine(directory,"parameters.bin"),matrix);Matrix(Path.Combine(directory,"pdmap.bin"),new double[0,36]);File.WriteAllText(Path.Combine(directory,"session.json"),"{}");
        var files=new[]{"parameters.bin","pdmap.bin","session.json"}.Select(name=>new{Path=name,Bytes=new FileInfo(Path.Combine(directory,name)).Length,Sha256=Hash(Path.Combine(directory,name))}).ToArray();
        File.WriteAllText(Path.Combine(directory,"analysis.json"),new JavaScriptSerializer().Serialize(new{Schema="HVX.NamedAnalysis",Version=1,Id=id,Name="2026年05月05日16时31分 - 004",CreatedUtc=created,SavedUtc=saved,SourceRowCount=count,Files=files,Charts=new object[0],ExpandedNodes=new string[0],SelectedIdentity=new double[0],DisplayMode=0,ViewPlane=0}),new UTF8Encoding(false));return directory;
    }
    static void Draw(Control c,string path,int width,int height){var form=c.FindForm();if(form!=null)form.ClientSize=new Size(width,height);c.Size=new Size(width,height);c.PerformLayout();Application.DoEvents();using(var image=new Bitmap(width,height)){c.DrawToBitmap(image,c.ClientRectangle);image.Save(path);}}
    static string PlaneAxes(object spectrum)
    {
        object[] values={null,null,0d,0d,0d,0d};spectrum.GetType().GetMethod("GetPlaneAxes",All).Invoke(spectrum,values);return values[0]+"/"+values[1];
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            // Keep the fully isolated fixture below legacy .NET's directory limit, including undo history names.
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"c-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));Type type=assembly.GetType("HVX.PdReview.CompetitionDefectRecognitionControl",true);
            string p1=Path.Combine(qa,"project1"),p2=Path.Combine(qa,"project2");Directory.CreateDirectory(p1);Directory.CreateDirectory(p2);
            string archive=Archive(p1,"older-resaved",7,"2026-09-20T01:00:00.0000000Z","2026-09-29T01:00:00.0000000Z");Archive(p1,"newer-created",4,"2026-09-28T01:00:00.0000000Z","2026-09-28T01:00:00.0000000Z");Archive(p2,"other",5,"2026-09-28T01:00:00.0000000Z",null);
            var before=Directory.GetFiles(p1,"*",SearchOption.AllDirectories).ToDictionary(x=>x,Hash);string sample=Path.Combine(qa,"pending"),fakeModel=Path.Combine(qa,"test-only.pdm");File.WriteAllText(fakeModel,"test fixture, never used for inference");string modelHash=Hash(fakeModel);
            using(var control=(Control)Activator.CreateInstance(type))
            using(var host=new Form{StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ClientSize=new Size(1200,720),ShowInTaskbar=false,AutoScaleMode=AutoScaleMode.None})
            {
                control.Dock=DockStyle.Fill;host.Controls.Add(control);host.Show();Application.DoEvents();
                var handle=control.Handle;Check((bool)Get(control,"EnableAiFeatures"),"AI master defaults enabled");Check((int)Get(control,"SourceRowCount")==0&&!Named(control,"CompetitionRecognize").Enabled,"no project has no identification");Check((string)Get(Field(control,"spectrum"),"EmptyStateText")=="尚未加载分析数据","no analysis never presented as negative diagnosis");
                Check((bool)Call(control,"LoadProject",p1),"project follows external context");var entries=(Array)Get(control,"SavedAnalyses");Check(entries.Length==2&&(string)Get(entries.GetValue(0),"Id")=="older-resaved","newest save takes precedence over creation time");Check(!((string)Get(entries.GetValue(0),"DisplayName")).Contains("年"),"legacy automatic display name numeric");
                Check(Complete(Call(control,"LoadSavedAnalysisAsync","older-resaved")),"load verified analysis");Check((int)Get(control,"SourceRowCount")==7,"matrix rows loaded");Check(Named(control,"CompetitionSavedAnalysis").Text.Length>0,"saved-analysis selected label is visible");object spectrum=Field(control,"spectrum");Check(!(bool)Get(spectrum,"AutomaticConclusions"),"no Task15 analysis worker in separate viewer");
                Check((int)Get(spectrum,"ModeButtonCount")==11,"complete analysis plot mode buttons");Check(((int[])Call(spectrum,"GetVisibleModeIndices")).SequenceEqual(new[]{0,1,2,3,4,5,6,9,10,11,12}),"eleven ordinary analysis spectra include PRPD, F-T and real 3D L-PRPD");
                Check(((int[])Get(control,"VisibleSourceRowIndices")).Length==6,"accepted-only default respects source acceptance");
                Call(spectrum,"SetPhaseVisibility",new object[]{new[]{false,false,false}});Check((string)Get(spectrum,"EmptyStateText")=="当前筛选无可见脉冲","empty filtered view never presented as negative diagnosis");Call(spectrum,"SetPhaseVisibility",new object[]{new[]{true,true,true}});
                var menu=(ContextMenuStrip)Field(spectrum,"regionMenu");Check(menu.Items.Count==5&&!menu.Items.Cast<ToolStripItem>().Any(x=>x.Name=="DeleteSelectedRegion"||x.Name=="CancelReviewRangeFilters"),"context menu whitelist no editing/report/filter side effects");
                bool denied=false;try{Set(spectrum,"DisplayModeIndex",7);}catch(TargetInvocationException ex){denied=ex.InnerException is ArgumentOutOfRangeException;}Check(denied,"legacy adapter mode excluded from complete analysis profile");
                denied=false;try{Set(spectrum,"DisplayModeIndex",8);}catch(TargetInvocationException ex){denied=ex.InnerException is ArgumentOutOfRangeException;}Check(denied,"legacy adapter count mode excluded from complete analysis profile");
                Set(spectrum,"DisplayModeIndex",12);Check((string)Get(spectrum,"DisplayModeName")=="L-PRPD"&&(int)Get(spectrum,"ViewPlaneIndex")==0,"real L-PRPD starts in 3D");object view=Field(spectrum,"view");Check((bool)Field(view,"Is3D")&&(string)Field(view,"XKey")=="L"&&(string)Field(view,"YKey")=="Q"&&(string)Field(view,"ZKey")=="PHI","L-PRPD has distance, signed charge and phase axes");
                Call(spectrum,"PerformViewPlaneButtonClick",1);Check((bool)Get(spectrum,"RegionSelectionEnabled")&&PlaneAxes(spectrum)=="L/Q","L-PRPD XY exposes distance/signed-charge selection");
                var sourceBefore=(double[,])Get(spectrum,"ResultParameterMatrix");
                Call(spectrum,"AddSelectionRegion",79d,83d,-110d,110d,false);Set(spectrum,"RegionActionCommand",0);Check(((int[])Get(control,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"signed region preserves exact source-row identities including negative pulses");
                denied=false;try{Set(spectrum,"RegionActionCommand",1);}catch(TargetInvocationException ex){denied=ex.InnerException is InvalidOperationException;}Check(denied,"region deletion API remains denied in true L-PRPD");
                foreach(int plane in new[]{2,3,0,1})
                {
                    Call(spectrum,"PerformViewPlaneButtonClick",plane);Check(((int[])Get(control,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"regional source identities preserved by L-PRPD plane "+plane);
                    var selected=Call(control,"CaptureInput");Check(((int[])Field(selected,"Rows")).SequenceEqual(new[]{0,1,2})&&((double[])Field(selected,"Charges")).SequenceEqual(new[]{100d,-101d,102d}),"recognition snapshot follows same signed region in plane "+plane);
                }
                Check(sourceBefore.Cast<double>().SequenceEqual(((double[,])Get(spectrum,"ResultParameterMatrix")).Cast<double>()),"plane changes/denied delete leave all matrix values unchanged");
                Set(spectrum,"DisplayModeIndex",11);Check(((int[])Get(control,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"L region flows into PRPD without mutating matrix");
                var capture=Call(control,"CaptureInput");Check(((double[])Field(capture,"Charges")).SequenceEqual(new[]{100d,-101d,102d}),"recognition uses original signed pC, not compensated 999");
                Call(control,"ClearLinkedSelection");Set(spectrum,"DisplayModeIndex",5);view=Field(spectrum,"view");Check((string)Field(view,"XKey")=="L"&&(string)Field(view,"YKey")=="Q"&&(string)Field(view,"ZKey")=="U","U-Q-L retains actual distance, charge and voltage axes");
                var points=(double[,])Call(spectrum,"GetDisplayedPoints");Check(points.GetLength(0)==6&&Enumerable.Range(0,6).All(i=>points[i,2]==8.7||points[i,2]==12.2),"U-Q-L Z contains actual test voltage");
                Set(spectrum,"DisplayModeIndex",6);view=Field(spectrum,"view");points=(double[,])Call(spectrum,"GetDisplayedPoints");Check((string)Field(view,"YKey")=="N"&&Enumerable.Range(0,points.GetLength(0)).Sum(i=>points[i,1])==6,"U-N-L counts accepted source rows");
                foreach(int mode in new[]{0,1,2,3,4,5,6,9,10,11,12}){Set(spectrum,"DisplayModeIndex",mode);Draw(control,Path.Combine(qa,"mode-"+mode+".png"),1200,720);Check((bool)Get(spectrum,"SelfCheckPassed"),"mode self-check "+mode);}
                foreach(int mode in new[]{5,6,12})
                {
                    Set(spectrum,"DisplayModeIndex",mode);Check((bool)Get(spectrum,"ViewPlaneButtonsVisible"),"four plane buttons available in mode "+mode);int[] rows=(int[])Get(control,"VisibleSourceRowIndices");
                    string[] axes=mode==12?new[]{"L/Q","L/PHI","PHI/Q"}:mode==5?new[]{"L/Q","L/U","U/Q"}:new[]{"L/N","L/U","U/N"};
                    foreach(int plane in new[]{1,2,3,0})
                    {
                        Call(spectrum,"PerformViewPlaneButtonClick",plane);Check((int)Get(spectrum,"ViewPlaneIndex")==plane,"mode "+mode+" plane switch "+plane);
                        Check(((int[])Get(control,"VisibleSourceRowIndices")).SequenceEqual(rows),"plane switching preserves source identities "+mode+"/"+plane);
                        if(plane!=0)Check(PlaneAxes(spectrum)==axes[plane-1],"honest projection axes "+mode+"/"+plane+" = "+axes[plane-1]);
                        var camera=(Control)Field(spectrum,"cameraToolbar");Check(camera.Visible&&camera.Controls.Count==4,"actual four-button toolbar visible "+mode+"/"+plane);
                        Check(camera.Controls.Cast<Button>().Select(b=>b.Text).SequenceEqual(new[]{"XY","XZ","ZY","3D"}),"competition camera button captions "+mode);
                        Check(camera.Controls.Cast<Button>().Count(b=>b.AccessibleDescription.Length>0)==1,"one camera selected state "+mode+"/"+plane);
                        if(plane==0||plane==1)Draw(control,Path.Combine(qa,"mode-"+mode+"-plane-"+plane+".png"),1200,720);
                    }
                }
                Set(spectrum,"DisplayModeIndex",12);Call(spectrum,"PerformViewPlaneButtonClick",1);Draw(control,Path.Combine(qa,"compact.png"),760,520);Check(Named(control,"CompetitionLearn").Bottom<=Named(control,"CompetitionLearn").Parent.ClientSize.Height,"compact action buttons fit");
                Set(control,"LanguageIndex",18);Check((int)Get(control,"LanguageIndex")==18&&(Named(control,"CompetitionLoad").Text=="載入分析"||Named(control,"CompetitionLoad").Text=="加載分析"),"traditional Chinese index retained");Set(control,"LanguageIndex",1);Check(Named(control,"CompetitionLoad").Text=="Load analysis","English UI");
                Set(control,"ProjectPath",p1+Path.DirectorySeparatorChar);Check((int)Get(control,"SourceRowCount")==7,"equivalent repeated context does not reload");
                object old=Call(control,"LoadSavedAnalysisAsync","older-resaved");Set(control,"ProjectPath",p2);Check((int)Get(control,"SourceRowCount")==0&&Get(control,"CurrentDischargeRecognition")==null,"project switch immediately clears all old data/results");Check(!Complete(old),"stale async source load discarded");Check(Complete(Call(control,"LoadSavedAnalysisAsync","other")),"next project's load succeeds");
                Set(control,"ModelLibraryPath",Path.Combine(qa,"missing.pdm"));Check(Complete(Call(control,"RecognizeCurrentDischargeAsync")),"unavailable model handled in background");object result=Get(control,"CurrentDischargeRecognition");Check(!(bool)Get(result,"Success")&&(string)Get(result,"StatusCode")=="Unavailable","missing model never produces a defect verdict");
                string deployedModel=args.Length>2?Path.GetFullPath(args[2]):Path.Combine(build,"model-payload","5581bf7a60798cb8f349a3ee3a59c2f8c121d44ab98d23172453ec856d360d16.hxm");Check(File.Exists(deployedModel),"deployed encrypted model exists for real runtime test");string deployedHash=Hash(deployedModel);Set(control,"ModelLibraryPath",deployedModel);Set(spectrum,"PhaseFilterIndex",1);
                Check(Complete(Call(control,"RecognizeCurrentDischargeAsync")),"real deployed Task12 worker completes");result=Get(control,"CurrentDischargeRecognition");Check((string)Get(result,"StatusCode")!="Unavailable"&&((string)Get(result,"Diagnostic")).Length>0,"actual Task12 ClassifyPDData ran: "+Get(result,"StatusMessage"));Check(Hash(deployedModel)==deployedHash,"real deployed model unchanged by inference");
                Set(control,"EnableAiFeatures",false);Check(Get(control,"CurrentDischargeRecognition")==null&&!Named(control,"CompetitionLearn").Enabled&&!Named(control,"CompetitionRecognize").Enabled,"master disables both display verdict and action buttons");Check(Named(control,"CompetitionStatus").Text.Contains("this module")&&!Named(control,"CompetitionStatus").Text.Contains("main program"),"AI setting belongs to the independent module");Check(!Complete(Call(control,"AddConfirmedLearningSampleAsync","tip","qa","")),"learning API gated when disabled");Check(!Directory.Exists(sample),"AI gate creates no store");
                Set(control,"EnableAiFeatures",true);Set(control,"ModelLibraryPath",fakeModel);FieldSet(control,"LearningRootProviderForTests",(Func<string,string>)(_=>sample));Set(spectrum,"PhaseFilterIndex",1);
                Set(spectrum,"DisplayModeIndex",12);Call(spectrum,"PerformViewPlaneButtonClick",1);Call(spectrum,"AddSelectionRegion",79d,83d,-110d,110d,false);Set(spectrum,"RegionActionCommand",0);Call(spectrum,"PerformViewPlaneButtonClick",3);
                var learningInput=Call(control,"CaptureInput");Check(((int[])Field(learningInput,"Rows")).SequenceEqual(new[]{0,1,2}),"learning selection uses the regional rows after switching to ZY");
                Check(Complete(Call(control,"AddConfirmedLearningSampleAsync","","qa","人工未映射类别")),"explicit custom verified sample enters pending taxonomy review without inference runtime");Check(Directory.GetDirectories(Path.Combine(sample,"active")).Length==1,"exactly one pending transaction");string manifest=File.ReadAllText(Directory.GetFiles(sample,"manifest.json",SearchOption.AllDirectories).Single());Check(manifest.Contains("pending_taxonomy_review")&&manifest.Contains("\"production_model_modified\":false"),"sample is not represented as a trained model");
                var savedSample=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(manifest);Check(((object[])savedSample["source_rows"]).Select(Convert.ToInt32).SequenceEqual((int[])Field(learningInput,"Rows")),"persisted learning manifest matches exact recognition source rows");
                var csv=File.ReadAllLines(Directory.GetFiles(sample,"phase-charge.csv",SearchOption.AllDirectories).Single()).Skip(1).Select(line=>line.Split(',')).ToArray();
                Check(csv.Select(c=>Int32.Parse(c[0],System.Globalization.CultureInfo.InvariantCulture)).SequenceEqual((int[])Field(learningInput,"Rows"))&&csv.Select(c=>Double.Parse(c[3],System.Globalization.CultureInfo.InvariantCulture)).SequenceEqual((double[])Field(learningInput,"Charges")),"learning CSV retains same row indices and signed raw charges, not projected/compensated values");
                Check(Complete(Call(control,"LearningActionAsync","undo",null,null,null)),"explicit undo archives sample: "+Named(control,"CompetitionStatus").Text);Check(Directory.GetDirectories(Path.Combine(sample,"active")).Length==0&&Directory.GetDirectories(Path.Combine(sample,"history")).Length==1,"undo keeps recovery history");Check(Hash(fakeModel)==modelHash,"production/test model bytes not changed");
                object commitGate=Field(control,"learningCommitGate");Monitor.Enter(commitGate);object pending;
                try{pending=Call(control,"AddConfirmedLearningSampleAsync","","qa","待取消类别");Thread.Sleep(40);Set(control,"ProjectPath",p1);}finally{Monitor.Exit(commitGate);}
                Check(!Complete(pending),"switching project cancels a staged learning transaction before commit");Check(Directory.GetDirectories(Path.Combine(sample,"active")).Length==0,"cancelled sample is not published");
                Check(before.All(x=>Hash(x.Key)==x.Value),"original saved analysis files remain byte-identical");
                Check(!Complete(Call(control,"LoadSavedAnalysisAsync","..")),"path traversal analysis id rejected");Check((int)Get(control,"SourceRowCount")==0,"failed load never retains wrong source");
                File.AppendAllText(Path.Combine(archive,"session.json"),"changed");Check(!Complete(Call(control,"LoadSavedAnalysisAsync","older-resaved")),"archive integrity tampering rejected");
                Set(control,"ProjectPath","");Check((int)Get(control,"SourceRowCount")==0&&!Named(control,"CompetitionLoad").Enabled&&!(bool)Get(control,"IsBusy"),"clear context disables entry and settles busy");
            }
            Console.WriteLine("PASS "+checks+" competition read-only recognition checks; QA "+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
