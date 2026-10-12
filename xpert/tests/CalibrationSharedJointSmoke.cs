using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CalibrationSharedJointSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Assembly module;static Type controlType,storeType,projectStoreType;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static object Call(object owner,string name,params object[] args)
    {Type t=owner as Type??owner.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length&&m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(v=>v)).Invoke(owner is Type?null:owner,args);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name,All).GetValue(owner,null);}
    static void Set(object owner,string name,object value){owner.GetType().GetProperty(name,All).SetValue(owner,value,null);}
    static void Field(object owner,string name,object value){owner.GetType().GetField(name,All).SetValue(owner,value);}
    static object FieldValue(object owner,string name){return owner.GetType().GetField(name,All).GetValue(owner);}
    static void Reject(Action action,string name){bool failed=false;try{action();}catch{failed=true;}Check(failed,name);}
    static Control Find(Control owner,string name){return owner.Controls.Find(name,true).Single();}
    static object[] Joints(Control c,int phase){return ((Array)Call(c,"GetPhaseJoints",phase)).Cast<object>().ToArray();}
    static string Block(string phase,string material="XLPE")
    {return "0\t"+phase+"\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\t"+material+"\t\r\n1\t终端\t终端\t400m\r\n";}
    static string Fixture(string root,string name,string mode,string cable)
    {
        string path=Path.Combine(root,name);Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"ITEM.in"),"电缆芯数: = \""+mode+"\"\r\n电缆长度: = \"400\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path,"CABLE.in"),cable,new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(path,"protected.raw"),new byte[]{1,2,3,4});return path;
    }
    static Control Control(string path,bool simulation=false)
    {
        var c=(Control)Activator.CreateInstance(controlType);Set(c,"SimulationMode",simulation);Set(c,"ProjectPath",path);
        Set(c,"SelectedPhaseIndex",0);Set(c,"SelectedLevelCode","TDR");return c;
    }
    static object Plan(string path,string[] phases,double position,string name,string before="",string after="")
    {return Call(Call(storeType,"Read",path),"Prepare",phases,position,name,"冷缩接头",before,after);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            controlType=module.GetType("HVX.Controls.CalibrationProgramControl",true);storeType=module.GetType("HVX.ProjectCables.CableJointStore",true);
            projectStoreType=module.GetType("HVX.Controls.CalibrationProjectStore",true);
            string root=Path.Combine(Path.GetFullPath(args[1]),"calibration-shared-joints-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string path=Fixture(root,"independent","单芯三相",Block("A相")+Block("B相","PE")+Block("C相","PVC"));
            byte[] item=File.ReadAllBytes(Path.Combine(path,"ITEM.in"));
            using(var c=Control(path))
            {
                Call(c,"CommitSharedJointPlan",Plan(path,new[]{"A","C"},200,"验证接头","材料甲","材料乙"));
                string text=File.ReadAllText(Path.Combine(path,"CABLE.in"));
                Check(text.Contains(Block("B相","PE")),"Unselected B phase byte-equivalent");
                Check(text.Split(new[]{"验证接头"},StringSplitOptions.None).Length==3,"A and C receive requested name exactly twice");
                Check(text.Split(new[]{"材料甲"},StringSplitOptions.None).Length==3&&text.Split(new[]{"材料乙"},StringSplitOptions.None).Length==3,"Requested before/after materials stored in selected phases");
                Check(Joints(c,0).Length==1&&Joints(c,1).Length==0&&Joints(c,2).Length==1&&Joints(c,-1).Length==0,"Markers follow actual selected phases; ALL not wildcard");
                Check((string)Get(Joints(c,0)[0],"Name")=="验证接头"&&(string)Get(Joints(c,2)[0],"MaterialAfter")=="材料乙","Marker name/material match committed bytes");
                Check(item.SequenceEqual(File.ReadAllBytes(Path.Combine(path,"ITEM.in")))&&File.ReadAllBytes(Path.Combine(path,"protected.raw")).SequenceEqual(new byte[]{1,2,3,4}),"Project info/raw acquisition unchanged");
                Check(Directory.GetFiles(path,"CABLE.before-joint-*.bak").Length==1,"Recoverable exact pre-edit backup created");
                Set(c,"SelectedPhaseIndex",-1);
                Check(((string[])Call(c,"JointTargetPhases",-1,false)).Length==0,"Automatic ALL does not expand to independent phases");
                Reject(()=>Call(projectStoreType,"AddJoints",path,new[]{"ALL"},new[]{240d}),"Legacy auto adapter rejects ALL wildcard");
                object stale=Plan(path,new[]{"B"},230,"过期计划");File.AppendAllText(Path.Combine(path,"CABLE.in"),"\r\n");
                Reject(()=>Call(c,"CommitSharedJointPlan",stale),"Stale plan rejected before marker update");Check(Joints(c,1).Length==0,"Failed write does not publish marker");
            }
            path=Fixture(root,"duplicate-a","单相",Block("A","XLPE")+Block("A","PVC")+Block("A","PE"));
            using(var c=Control(path))
            {
                Call(c,"CommitSharedJointPlan",Plan(path,new[]{"A"},120,"重复相接头"));
                string text=File.ReadAllText(Path.Combine(path,"CABLE.in"));
                Check(text.Split(new[]{"0\tA\t"},StringSplitOptions.None).Length==4&&!text.Contains("0\tB\t")&&!text.Contains("0\tC\t"),"Duplicate A labels are not renamed");
                Check(Joints(c,0).Length==1,"Repeated same-phase/same-position targets share one marker");
                string materials=(string)Get(Joints(c,0)[0],"MaterialBefore");
                Check(materials.Contains("XLPE")&&materials.Contains("PVC")&&materials.Contains("PE"),"Marker identifies differing retained materials without blocking explicit addition");
            }
            path=Fixture(root,"shared","三芯",Block("A")+Block("B")+Block("C"));
            using(var c=Control(path))
            {
                Set(c,"SelectedPhaseIndex",-1);Call(c,"CommitSharedJointPlan",Plan(path,new[]{"ALL"},125,"三芯接头"));
                Check(Joints(c,-1).Length==1&&Joints(c,0).Length==1&&Joints(c,1).Length==1&&Joints(c,2).Length==1,"Shared three-core has ALL display mirror and actual stored phase markers");
                Check(((string[])Call(c,"JointTargetPhases",-1,false)).SequenceEqual(new[]{"ALL"}),"Shared three-core automatically uses one shared choice");
            }
            path=Fixture(root,"simulation","单相",Block("A"));byte[] beforeBytes=File.ReadAllBytes(Path.Combine(path,"CABLE.in"));
            using(var c=Control(path,true))
            {
                Call(c,"CommitSharedJointPlan",Plan(path,new[]{"A"},170,"仿真接头","PE","PVC"));
                Check(beforeBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(path,"CABLE.in")))&&Directory.GetFiles(path,"*.bak").Length==0,"Simulation changes no real file and creates no backup");
                Check(Joints(c,0).Length==1,"Simulation marker appears in intended phase");
                Call(c,"AddPhaseJoint",1,"旧API",100d);Check(Joints(c,1).Length==1,"Legacy AddPhaseJoint API retained");
            }
            using(var c=Control("",true))
            {
                object snapshot=Call(c,"JointSnapshotForCurrentContext",0);object plan=Call(snapshot,"Prepare",new[]{"A"},10d,"无项目仿真","接头","","");
                Call(c,"CommitSharedJointPlan",plan);Check(Joints(c,0).Length==1,"Projectless simulation uses noncommittable preview geometry");
            }
            using(var c=Control("",true))
            {
                Set(c,"UseFirstArrivalTiming",false);Call(c,"SetPhaseParameters",0,2720d,170d,false,true);
                double[] wave=Enumerable.Range(0,8192).Select(i=>Math.Exp(-Math.Pow((i-1000)/8d,2))+.3*Math.Exp(-Math.Pow((i-3000)/8d,2))+.5*Math.Exp(-Math.Pow((i-5000)/8d,2))).ToArray();
                Call(c,"SetPhaseWaveform",0,"TDR",wave,0d,8e-9);Call(c,"SetPhaseBandwidth",0,true,1d);
                Call(c,"SetPhaseMarkers",0,"TDR",8e-6,40e-6,40e-6,2720d);
                object phase=Call(c,"GetPhaseStateNoLock",0),analysis=((IDictionary)FieldValue(phase,"Analyses"))["TDR"];
                Set(analysis,"IncidentIndex",1000);Set(analysis,"ReflectionIndex",5000);
                object source=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.CalibrationCaptureResult",true),true);
                object reflection=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.TdrCorrelationResult",true),true);
                Field(reflection,"Valid",true);Field(source,"Reflection",reflection);Field(source,"SampleRateHz",125e6);
                // Deliberately empty cached average: detection must use the active
                // displayed waveform, not this pre-low-pass capture cache.
                Field(source,"Average",new double[wave.Length]);double[][] windows=Enumerable.Range(0,4).Select(i=>(double[])wave.Clone()).ToArray();Field(source,"Windows",windows);
                Call(c,"DetectAndStoreJoints",0,"TDR",source);
                Check(Joints(c,0).Any(j=>Math.Abs((double)Get(j,"DistanceMeters")-1360)<10),"Joint detection uses displayed low-pass waveform, not source Average");
                Check(windows.All(w=>w.SequenceEqual(wave)),"Repeatability filtering preserves original high-pass pulse windows");
                Check(Joints(c,0).Where(j=>(double)Get(j,"ConfidencePercent")>=80).All(j=>(bool)Get(j,"Confirmed")),"Repeated high-confidence candidates default to confirmed");
            }
            path=Fixture(root,"dialog","单芯三相",Block("A")+Block("B")+Block("C"));
            using(var c=Control(path))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(900,650)})
            {
                host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                bool filled=false;int tick=0;Exception timerError=null;
                using(var timer=new Timer{Interval=30})
                {
                    timer.Tick+=delegate{
                        try{
                            Form dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CableJointDialog");
                            if(dialog==null){if(++tick>100)throw new Exception("Shared dialog did not open");return;}
                            timer.Stop();Check(dialog.Controls.Find("JointPhase_A",true).Length==1&&dialog.Controls.Find("JointPhase_B",true).Length==1,"Shared dialog includes phase selection with fields in one window");
                            ((CheckBox)Find(dialog,"JointPhase_A")).Checked=false;((CheckBox)Find(dialog,"JointPhase_B")).Checked=true;
                            Find(dialog,"JointName").Text="同窗手工接头";Find(dialog,"JointMaterialBefore").Text="XLPE";Find(dialog,"JointMaterialAfter").Text="PVC";
                            using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,dialog.Size));bitmap.Save(Path.Combine(root,"calibration-shared-joint-dialog.png"));}
                            Check(((NumericUpDown)Find(dialog,"JointPosition")).Value==210m,"Switching selected phase preserves manually proposed position");
                            ((Button)Find(dialog,"ConfirmJoint")).PerformClick();
                            Check(dialog.DialogResult==DialogResult.OK,"Shared dialog confirms without hidden validation failure");filled=true;
                        }catch(Exception error){timerError=error;timer.Stop();foreach(Form f in Application.OpenForms.Cast<Form>().Where(f=>f.Name=="CableJointDialog").ToArray()){f.DialogResult=DialogResult.Cancel;f.Close();}}
                    };
                    timer.Start();Call(c,"OpenSharedJointDialog",0,"TDR",210d);
                }
                if(timerError!=null)throw timerError;
                Check(filled&&Joints(c,1).Length==1&&Joints(c,0).Length==0,"Calibration manual dialog directly commits selected phase and updates actual phase marker");
                Check(File.ReadAllText(Path.Combine(path,"CABLE.in")).Contains("同窗手工接头\t"),"Manual dialog writes selected joint name");
            }
            path=Fixture(root,"sync","单芯三相",Block("A")+Block("B")+Block("C"));
            using(var c=Control(path))
            {
                int broadcasts=0;controlType.GetEvent("CableStructureChanged").AddEventHandler(c,new EventHandler((s,e)=>broadcasts++));
                double[] wave=Enumerable.Range(0,4096).Select(i=>Math.Exp(-Math.Pow((i-100)/8d,2))+.3*Math.Exp(-Math.Pow((i-688)/8d,2))).ToArray();
                Call(c,"SetPhaseWaveform",0,"TDR",wave,0d,8e-9);
                var phase=Call(c,"GetPhaseStateNoLock",0);object waveform=((IDictionary)FieldValue(phase,"Waveforms"))["TDR"];
                object candidate=Call(c,"AddPhaseJoint",0,"未保存候选",50d);Set(candidate,"AutomaticallyDetected",true);Set(candidate,"ConfidencePercent",65d);Set(candidate,"Confirmed",false);
                Call(c,"CommitSharedJointPlan",Plan(path,new[]{"A"},120,"已保存接头"));
                Check(broadcasts==1,"Actual calibration commit broadcasts exactly once");
                byte[] afterLocal=File.ReadAllBytes(Path.Combine(path,"CABLE.in"));
                Call(storeType,"Commit",Plan(path,new[]{"A"},200,"外部接头"));
                Field(c,"hostAcquisitionActive",true);Check(!(bool)Call(c,"RefreshCableStructure"),"Busy capture defers external metadata refresh");Field(c,"hostAcquisitionActive",false);
                Check((bool)Call(c,"RefreshCableStructure"),"Idle calibration refresh accepts metadata-only change");
                Check(Joints(c,0).Select(j=>(double)Get(j,"DistanceMeters")).SequenceEqual(new[]{50d,120d,200d}),"External addition retains both persisted and unsaved candidate markers");
                Check(Joints(c,0).Any(j=>ReferenceEquals(j,candidate))&&ReferenceEquals(((IDictionary)FieldValue(phase,"Waveforms"))["TDR"],waveform),"Refresh retains pending candidate and captured waveform objects");
                File.WriteAllBytes(Path.Combine(path,"CABLE.in"),afterLocal);
                Check((bool)Call(c,"RefreshCableStructure")&&Joints(c,0).Select(j=>(double)Get(j,"DistanceMeters")).SequenceEqual(new[]{50d,120d}),"External deletion drops only persisted removed marker");
                Check(broadcasts==1,"Cross-page refresh does not create broadcast loop");
                Field(c,"jointDialogOpen",true);Check(!(bool)Call(c,"RefreshCableStructure"),"Pending modal confirmation defers refresh");Field(c,"jointDialogOpen",false);
                Call(c,"NotifyPersistedCableStructureChanged");Check(broadcasts==1,"Unchanged CABLE produces no false commit event");
            }
            path=Fixture(root,"managed-edits","三芯",Block("A")+Block("B")+Block("C"));
            using(var c=Control(path)) {
                Set(c,"SelectedPhaseIndex",-1);
                Call(c,"CommitSharedJointPlan",Plan(path,new[]{"ALL"},100,"接头 1"));
                string cable=Path.Combine(path,"CABLE.in");File.SetAttributes(cable,File.GetAttributes(cable)|FileAttributes.ReadOnly);
                object edit=Call(Call(storeType,"Read",path),"PrepareEdit",0,1,140d,"已移动接头","中间接头","PE","PVC",true);
                Check((double)FieldValue(edit,"OriginalPositionM")==100d,"edit plan preserves old physical position");
                Call(c,"CommitSharedJointPlan",edit);
                foreach(int phaseIndex in new[]{-1,0,1,2}) {
                    Check(Joints(c,phaseIndex).Length==1&&(double)Get(Joints(c,phaseIndex)[0],"DistanceMeters")==140d,"move refreshes actual phases and shared ALL marker without old marker");
                    Check((string)Get(Joints(c,phaseIndex)[0],"Name")=="已移动接头"&&(string)Get(Joints(c,phaseIndex)[0],"MaterialBefore")=="PE","move refreshes name and materials");
                }
                object remove=Call(Call(storeType,"Read",path),"PrepareRemove",0,1,true);Call(c,"CommitSharedJointPlan",remove);
                Check(new[]{-1,0,1,2}.All(p=>Joints(c,p).Length==0),"remove does not republish deleted shared marker");
                Check((File.GetAttributes(cable)&FileAttributes.ReadOnly)!=0&&Directory.GetFiles(path,"CABLE.before-joint-*.bak").Length==3,"add/edit/remove retain recoverable backups and readonly metadata");
                Check(File.ReadAllBytes(Path.Combine(path,"protected.raw")).SequenceEqual(new byte[]{1,2,3,4}),"joint management leaves raw waveforms intact");
            }
            path=Fixture(root,"double-click","单芯三相",Block("A")+Block("B")+Block("C"));
            using(var c=Control(path))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(1100,720)})
            {
                Set(c,"EmbeddedMode",true);host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                double[] wave=Enumerable.Range(0,4096).Select(i=>Math.Exp(-Math.Pow((i-100)/8d,2))).ToArray();
                Call(c,"SetPhaseWaveform",0,"TDR",wave,0d,8e-9);
                object phase=Call(c,"GetPhaseStateNoLock",0);((IDictionary)FieldValue(phase,"Markers")).Clear();
                RectangleF plot=(RectangleF)Get(c,"CurrentPlotRectangle");
                bool opened=false;Exception timerError=null;
                using(var timer=new Timer{Interval=30})
                {
                    timer.Tick+=delegate{
                        Form dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CableJointDialog");if(dialog==null)return;
                        try{
                            timer.Stop();opened=true;
                            Check(((NumericUpDown)Find(dialog,"JointPosition")).Value==0m,"No reflection means no guessed double-click distance");
                            Check(!(bool)Call(c,"RefreshCableStructure"),"Double-click dialog protects pending input from host sync");
                            ((NumericUpDown)Find(dialog,"JointPosition")).Value=90m;
                            ((Button)Find(dialog,"ConfirmJoint")).PerformClick();Check(dialog.DialogResult==DialogResult.OK,"Manual distance commits from no-reflection dialog");
                        }catch(Exception error){timerError=error;dialog.DialogResult=DialogResult.Cancel;dialog.Close();}
                    };
                    timer.Start();Call(c,"OnMouseDoubleClick",new MouseEventArgs(MouseButtons.Left,2,(int)(plot.Left+plot.Width*.5),(int)(plot.Top+plot.Height*.5),0));
                }
                if(timerError!=null)throw timerError;
                Check(opened&&Joints(c,0).Any(j=>Math.Abs((double)Get(j,"DistanceMeters")-90)<.001),"Actual double-click without automatic reflection opens and adds joint");
                Check(File.ReadAllText(Path.Combine(path,"CABLE.in")).Contains("接头 1\t")&&Joints(c,0).Any(j=>(string)Get(j,"Name")=="接头 1"),"Automatic joint uses ordered number after manually corrected physical position");
            }
            path=Fixture(root,"automatic-removal","单相",Block("A"));
            using(var c=Control(path))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(950,680)})
            {
                host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                int broadcasts=0;controlType.GetEvent("CableStructureChanged").AddEventHandler(c,new EventHandler((s,e)=>broadcasts++));
                Call(projectStoreType,"AddJoints",path,new[]{"A"},new[]{80d});Call(c,"NotifyPersistedCableStructureChanged");
                object generated=Joints(c,0).Single();Set(generated,"AutomaticallyDetected",true);Set(generated,"ConfidencePercent",90d);
                object tentative=Call(c,"AddPhaseJoint",0,"待核对",140d);Set(tentative,"AutomaticallyDetected",true);Set(tentative,"ConfidencePercent",65d);Set(tentative,"Confirmed",false);
                Exception timerError=null;bool reviewSeen=false;
                using(var timer=new Timer{Interval=30})
                {
                    timer.Tick+=delegate{
                        Form dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CalibrationJointConfirmation");if(dialog==null)return;
                        try{timer.Stop();reviewSeen=true;var list=(CheckedListBox)Find(dialog,"CalibrationJointCandidates");
                            Check(list.Items.Count==2&&list.GetItemChecked(0)&&!list.GetItemChecked(1),"Confirmation list keeps high confidence checked and low confidence unchecked");
                            Check(!(bool)Call(c,"RefreshCableStructure"),"Joint review modal protects confirmation choices");
                        }catch(Exception error){timerError=error;}
                        dialog.DialogResult=DialogResult.Cancel;dialog.Close();
                    };
                    timer.Start();Call(c,"ShowJointConfirmation");
                }
                if(timerError!=null)throw timerError;Check(reviewSeen,"Joint confirmation button path exposes review list");
                bool deleteSeen=false;
                using(var timer=new Timer{Interval=30})
                {
                    timer.Tick+=delegate{
                        Form dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="DeleteJointDialog");if(dialog==null)return;
                        timer.Stop();deleteSeen=true;((Button)Find(dialog,"ConfirmDeleteJoint")).PerformClick();
                    };
                    timer.Start();Call(c,"ConfirmJointRemoval",0,"TDR",generated);
                }
                Check(deleteSeen&&!File.ReadAllText(Path.Combine(path,"CABLE.in")).Contains("校准识别\t80m"),"Automatic joint deletion updates actual CABLE file");
                Check(broadcasts==2&&Joints(c,0).Length==1&&ReferenceEquals(Joints(c,0)[0],tentative),"Automatic deletion broadcasts and preserves unrelated unconfirmed candidate");
            }
            Console.WriteLine("PASS CalibrationSharedJointSmoke checks="+checks+"; HardwareAccessed=False; Output="+root);return 0;
        }
        catch(Exception e){Console.Error.WriteLine("FAIL check "+checks+": "+e);return 1;}
    }
}
