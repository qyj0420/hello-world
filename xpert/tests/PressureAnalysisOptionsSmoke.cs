using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class PressureAnalysisOptionsSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Reject(Action run,string why){try{run();}catch(Exception ex){if(ex is TargetInvocationException)ex=ex.InnerException;if(ex is ArgumentException||ex is InvalidOperationException){Check(true,why);return;}throw;}throw new Exception(why);}
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("fresh fixture directory required");Directory.CreateDirectory(qa);
            var trial=new PressureAnalysisTrial(qa);string a=Path.Combine(qa,"A.loc"),b=Path.Combine(qa,"B.loc");
            File.WriteAllText(a,"fixture");File.WriteAllText(b,"fixture");trial.Commit("a",a);trial.Commit("b",b);
            var first=trial.SnapshotThresholds(a,true,42.02,false);
            Check(first[a].Manual&&first[a].Pc==42.02&&!first[b].Manual,"manual threshold only current round");
            var next=trial.SnapshotThresholds(b,true,61.5,false);
            Check(next[a].Pc==42.02&&next[b].Pc==61.5,"prior round selection retained");
            next.Clear();Check(trial.SnapshotThresholds(b,false,100,false)[a].Pc==42.02,"returned dictionary cannot mutate trial");
            var all=trial.SnapshotThresholds(b,true,75,true);Check(all.Values.All(t=>t.Manual&&t.Pc==75),"all changes all saved rounds in trial");
            Check(trial.SnapshotThresholds(a,false,100,false)[b].Pc==75,"turning all off preserves other round threshold");
            Reject(()=>trial.SnapshotThresholds(a,true,double.NaN,true),"NaN rejected");
            Reject(()=>trial.SnapshotThresholds(a,true,-1,true),"negative rejected");
            Reject(()=>trial.SnapshotThresholds(Path.Combine(qa,"not-committed.loc"),true,1,true),"uncommitted record rejected");
            var separate=new PressureAnalysisTrial(qa);separate.Commit("a",a);
            Check(!separate.SnapshotThresholds(a,false,100,false)[a].Manual,"new trial does not inherit old manual selection");
            trial.End();Reject(()=>trial.SnapshotThresholds(a,true,1,true),"ended trial rejected");
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var page=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true))) {
                Check(!(bool)Get(page,"ManualAnalysisThresholdEnabled")&&!(bool)Get(page,"ApplyAnalysisThresholdToAllRounds"),"defaults automatic and per-round");
                Call(page,"SetPressureAnalysisThreshold",true,42.02,true);
                Check((bool)Get(page,"ManualAnalysisThresholdEnabled")&&(double)Get(page,"ManualAnalysisThresholdPc")==42.02&&(bool)Get(page,"ApplyAnalysisThresholdToAllRounds"),"public atomic options setter");
                Check(!(bool)Get(page,"ActionPending"),"editing threshold never publishes a hardware action");
                var dashboard=(Control)Get(page,"spectrumDashboardView");var bar=dashboard.Controls.Find("PressureAnalysisOptionsBar",true).Single();
                var manual=(CheckBox)bar.Controls.Find("PressureManualThreshold",true).Single();var number=(NumericUpDown)bar.Controls.Find("PressureThresholdPc",true).Single();
                manual.Checked=false;Check(!number.Enabled&&!(bool)Get(page,"ManualAnalysisThresholdEnabled"),"automatic disables manual numeric input");
                manual.Checked=true;number.Value=23.45m;Check((double)Get(page,"ManualAnalysisThresholdPc")==23.45,"UI numeric value reaches public snapshot");
                Reject(()=>Call(page,"SetPressureAnalysisThreshold",true,double.PositiveInfinity,true),"UI API rejects infinity");
                page.GetType().GetField("postProcessStateCode",B).SetValue(page,"ANALYSIS_REQUESTED");Call(page,"UpdateActionBusyUi");
                Check(!bar.Enabled,"analysis locks option edits immediately");
                Reject(()=>Call(page,"SetPressureAnalysisThreshold",true,10,false),"API cannot alter in-flight analysis options");
                page.GetType().GetField("postProcessStateCode",B).SetValue(page,"");Call(page,"UpdateActionBusyUi");
                Check(bar.Enabled,"completed round restores option editing");
                var comp=(CheckBox)bar.Controls.Find("PressureCompensatedCharge",true).Single();comp.Checked=true;
                Check((bool)Get(Get(dashboard,"Review"),"UseCompensatedCharge"),"compensation checkbox reaches shared spectrum");
                var endpoint=(CheckBox)bar.Controls.Find("PressureEndpointReview",true).Single();
                Check(!endpoint.Enabled,"endpoint inspection disabled before saved analysis");
                int requests=0;page.GetType().GetEvent("EndpointReviewRequested").AddEventHandler(page,(EventHandler)((s,e)=>requests++));
                Call(page,"SetEndpointReviewAvailable",true);endpoint.Checked=true;
                long inspection=(long)Get(page,"EndpointReviewSequence");
                Check(requests==1&&(bool)Get(page,"EndpointInspectionBusy")&&(bool)Get(page,"PressureNavigationLocked"),"endpoint action locks pressure and navigation");
                long pressureSequence=(long)Get(page,"PressureRequestSequence");Call(page,"RequestPressure");
                Check((long)Get(page,"PressureRequestSequence")==pressureSequence&&!(bool)Get(page,"ActionPending"),"inspection cannot issue pressure or hardware mailbox");
                Check(!(bool)Call(page,"TryRequestDelete"),"inspection blocks deletion of source");
                Call(page,"CompleteEndpointReview",inspection-1);Check((bool)Get(page,"EndpointInspectionBusy"),"old completion cannot unlock current inspection");
                Call(page,"CompleteEndpointReview",inspection);Check(!(bool)Get(page,"EndpointInspectionBusy")&&!endpoint.Checked&&bar.Enabled,"matching completion restores options");
                page.GetType().GetProperty("ProjectPath").SetValue(page,qa,null);
                Check(!(bool)Get(page,"ManualAnalysisThresholdEnabled")&&!comp.Checked,"changing project resets threshold and compensation");
                using(var form=new Form{ClientSize=new Size(1400,850)}) {
                    form.Controls.Add(page);page.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                    using(var png=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)){form.DrawToBitmap(png,form.ClientRectangle);png.Save(Path.Combine(qa,"pressure-options.png"));}
                    Check(bar.Height>20&&bar.Visible,"analysis options visible beneath waveform");
                    Check(!bar.Bounds.IntersectsWith(((Control)Get(dashboard,"Review")).Bounds),"fill chart does not cover options strip");
                    foreach(int width in new[]{900,430}) {
                        dashboard.Width=width;dashboard.PerformLayout();bar.PerformLayout();
                        Check(bar.Controls.Cast<Control>().All(c=>c.Bottom<=bar.ClientSize.Height&&c.Right<=bar.ClientSize.Width),"options wrap within width "+width);
                    }
                    form.Close();
                }
            }
            var reviewAssembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            using(var review=(Control)Activator.CreateInstance(reviewAssembly.GetType("HVX.PdReview.PdReviewControl",true))) {
                Call(review,"SetFileAnalysisThreshold",a,true,42.02);Call(review,"SetFileAnalysisThreshold",b,false,100d);
                object ra=Call(review,"CreateFileAnalysisRequest",a,1L,true,1),rb=Call(review,"CreateFileAnalysisRequest",b,2L,true,2);
                Check((bool)Get(ra,"ManualThresholdEnabled")&&(double)Get(ra,"ManualThreshold")==42.02,"actual batch request uses manual pC");
                Check(!(bool)Get(rb,"ManualThresholdEnabled"),"actual batch request retains automatic for other round");
                string geometryProject=Path.Combine(qa,"geometry");Directory.CreateDirectory(geometryProject);
                File.WriteAllText(Path.Combine(geometryProject,"ITEM.in"),"[ITEM]\r\nCable length = 180\r\n");
                File.WriteAllText(Path.Combine(geometryProject,"CABLE.in"),"0\tALL\r\nVelocity: 180 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tJoint\tJoint\t70m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t180m\r\n");
                review.GetType().GetProperty("PromptSavedAnalysisOnProjectLoad").SetValue(review,false,null);
                review.GetType().GetProperty("ProjectPath").SetValue(review,geometryProject,null);
                object geometry=Call(review,"CapturePressureSpectrumGeometry");
                using(var chart=(Control)Activator.CreateInstance(reviewAssembly.GetType("HVX.UnifiedSpectrum.UnifiedSpectrumControl",true))) {
                    double[,] rows=new double[4,36];
                    for(int i=0;i<4;i++){rows[i,0]=1;rows[i,5]=180;rows[i,13]=70.5;rows[i,15]=100;rows[i,16]=200;rows[i,35]=1;}
                    rows[1,5]=200;rows[2,13]=1;rows[2,26]=6;rows[3,13]=-1;
                    Call(chart,"LoadDataMatrix",rows);Call(geometry,"Apply",chart,true,false);
                    var projection=(Func<int,double,double>)Get(chart,"reviewPositionProjection");
                    Check(projection(0,70.5)==70,"snap uses actual project joint");
                    Check(projection(1,70.5)==70.5,"different cable length cannot use foreign joint");
                    Check(projection(2,1)==1,"method 7 endpoint requires evidence not display snap");
                    Check(projection(3,-1)==0,"boundary projection retained");
                    Call(geometry,"Apply",chart,true,true);projection=(Func<int,double,double>)Get(chart,"reviewPositionProjection");
                    Check(projection(0,70.5)==110,"reverse follows snapped DUT coordinate");
                    Call(geometry,"Apply",chart,false,true);projection=(Func<int,double,double>)Get(chart,"reviewPositionProjection");
                    Check(projection(0,70.5)==109.5,"snap can be disabled independently of reverse");
                    Check(((double[,])Get(chart,"ResultParameterMatrix"))[0,13]==70.5&&rows[0,13]==70.5,"geometry display preserves original location");
                    geometry.GetType().GetMethod("Clear").Invoke(null,new object[]{chart});Check(Get(chart,"reviewPositionProjection")==null,"project reset removes stale projection");
                }
            }
            Console.WriteLine("PRESSURE_ANALYSIS_OPTIONS_PASS="+checks+"; HARDWARE_CALLS=0; USER_RECORD_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
