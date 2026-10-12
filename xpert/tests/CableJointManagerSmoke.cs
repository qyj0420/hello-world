using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HVX.ProjectCables;
using HVX.Controls;

internal static class CableJointManagerSmoke
{
    static int checks;
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool pass, string why) { checks++; if (!pass) throw new Exception(why); }
    static T Find<T>(Control c, string name) where T : Control { return c.Controls.Find(name, true).Cast<T>().Single(); }
    static object Call(object target, string method, params object[] args) { return target.GetType().GetMethods(Flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(target, args); }
    static void Pump() { for (int i=0;i<4;i++) Application.DoEvents(); }
    static string Block(string material) { return "0\tA\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\t" + material + "\t\r\n1\t人工名\t预制接头\t80.123m\r\n2\t本体\tPVC\t\r\n1\t接头 2\t冷缩接头\t264.7m\r\n2\t本体\t油纸\t\r\n1\t终端\t冷缩\t400m\r\n"; }
    static CableJointSnapshot Fixture(string root, string name, string mode)
    {
        string dir=Path.Combine(root,name); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"ITEM.in"),"[ITEM]\r\n电缆芯数: = \""+mode+"\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir,"CABLE.in"),Block("XLPE")+Block("橡胶2")+Block("油纸"),new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(dir,"raw.cal"),new byte[]{1,2,3}); return CableJointStore.Read(dir);
    }
    static void Select(CableJointDialog dialog,int row)
    { var grid=Find<DataGridView>(dialog,"ExistingJoints"); grid.ClearSelection(); grid.CurrentCell=grid.Rows[row].Cells[0]; grid.Rows[row].Selected=true; Pump(); }
    static void Show(Form form) { form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-1800,-1200); form.Show(); Pump(); }
    static void CheckVisible(Control c, string title)
    {
        Check(c.Visible && c.Width>0 && c.Height>0,title+" visible");
        for(Control p=c.Parent;p!=null;p=p.Parent)
            Check(p.ClientRectangle.Contains(p.RectangleToClient(c.RectangleToScreen(c.ClientRectangle))),title+" inside "+p.GetType().Name);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.Combine(Path.GetFullPath(args[0]),"jm-"+Guid.NewGuid().ToString("N").Substring(0,8)); Directory.CreateDirectory(root);
            string settings=Path.Combine(root,"references.ini");
            var references=ReferenceCatalog.CreateDefaults(); references.Materials.Add(new MaterialData{Name="橡胶2",Velocity=145.25}); references.Save(settings);
            var snapshot=Fixture(root,"shared","一体三芯"); byte[] original=File.ReadAllBytes(snapshot.CablePath),item=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(snapshot.CablePath),"ITEM.in"));
            using(var d=new CableJointDialog(snapshot,200,"ALL",false,null))
            {
                d.ConfigureMaterialCatalog(settings); Show(d);
                var grid=Find<DataGridView>(d,"ExistingJoints");
                Check(grid.Rows.Count==2,"shared physical joints appear once, not repeated A rows");
                Check(grid.Columns.Contains("AttachmentType")&&grid.Columns["Before"].Visible&&grid.Columns["After"].Visible,"list includes attachment and both materials");
                Check(grid.Rows[0].Cells[0].Value.ToString().Contains("ALL"),"common scope explicit");
                Check(grid.Rows[0].Cells["Before"].ToolTipText.Contains("橡胶2"),"each underlying block material retained in tooltip");
                Check(Find<ComboBox>(d,"JointMaterialBefore").Items.Cast<object>().Any(x=>x.ToString()=="橡胶2"),"page dictionary loaded into common dialog");
                Select(d,0); string error;
                d.ConfirmChange=t=>false;
                Check(!d.TryPrepareRemoval(out error)&&d.Plan==null&&File.ReadAllBytes(snapshot.CablePath).SequenceEqual(original),"cancelled mixed removal makes no plan or write");
                string confirmation=""; d.ConfirmChange=t=>{confirmation=t;return true;};
                Check(d.TryPrepare(out error)&&d.Plan.OriginalPositionM==80.123&&d.Plan.PositionM==80.123,"unchanged one-decimal editor preserves exact original coordinate");
                Find<NumericUpDown>(d,"JointPosition").Value=220;
                Check(Find<TextBox>(d,"JointName").Text=="人工名","move preserves human joint name");
                Check(d.TryPrepare(out error)&&d.Plan.OperationCode=="EditJoint"&&d.Plan.OriginalPositionM==80.123&&d.Plan.PositionM==220&&d.Plan.SharedThreeCore,"move metadata retains old and new position plus shared identity");
                Check(d.Plan.ResolvedTargets.Length==3&&confirmation.Contains("block")==false&&confirmation.Contains("结构段 3"),"confirmation lists three actual source blocks");
                int calls=0; d.ConfigureCommit((plan,operation)=>{calls++;throw new IOException("injected metadata write failure");});
                Find<Button>(d,"ConfirmJoint").PerformClick(); Pump();
                Check(calls==1&&d.Visible&&d.DialogResult!=DialogResult.OK&&d.Plan==null,"failed commit remains open and never reports success");
                Check(Find<NumericUpDown>(d,"JointPosition").Value==220&&Find<TextBox>(d,"JointName").Text=="人工名","failed commit keeps user inputs");
                Check(File.ReadAllBytes(snapshot.CablePath).SequenceEqual(original),"failed callback no project write");
                string backup=""; d.ConfigureCommit((plan,operation)=>{calls++;backup=CableJointStore.CommitWithResult(plan).BackupPath;});
                Find<Button>(d,"ConfirmJoint").PerformClick(); Pump();
                Check(calls==2&&d.DialogResult==DialogResult.OK&&!d.Visible,"successful retry closes exactly once");
                Check(File.ReadAllBytes(backup).SequenceEqual(original),"backup retains full original cable");
                Check(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(snapshot.CablePath),"ITEM.in")).SequenceEqual(item),"ITEM untouched by edit");
            }
            var moved=CableJointStore.Read(Path.GetDirectoryName(snapshot.CablePath));
            Check(moved.Blocks.All(b=>b.Phase=="A"&&b.Positions.Contains(220)),"shared edit updates geometry without fabricating phase names");
            using(var d=new CableJointDialog(moved,200,"ALL",true,null))
            {
                d.ConfigureMaterialCatalog(settings); Show(d); Select(d,0); d.ConfirmChange=t=>true;
                int count=0; d.ConfigureCommit((p,o)=>{Check(o=="RemoveJoint"&&p.OriginalPositionM==220,"delete callback operation and original coordinate"); count++;CableJointStore.CommitWithResult(p);});
                Find<Button>(d,"DeleteJoint").PerformClick(); Pump(); Check(count==1&&!d.Visible,"delete successful once");
            }
            var split=Fixture(root,"separate","三相单芯");
            using(var d=new CableJointDialog(split,200,"A",true,null))
            {
                d.ConfigureMaterialCatalog(settings); Show(d);
                var grid=Find<DataGridView>(d,"ExistingJoints"); Check(grid.Rows.Count==6,"independent repeated-A records not merged");
                Check(grid.Rows[4].Cells[0].Value.ToString().Contains("block 3"),"independent source block explicit");
                string error; Check(d.TrySaveMaterial("橡胶3",147.75,false,out error),"dialog persists custom name with explicit velocity: "+error);
                Check(ReferenceCatalog.Load(settings).Materials.Any(m=>m.Name=="橡胶3"&&m.Velocity==147.75),"dialog dictionary saved in creation format");
                d.ConfigureMaterials("橡胶3","橡胶3");
                Check(d.TryDeleteMaterial("橡胶3",out error)&&Find<ComboBox>(d,"JointMaterialBefore").Text=="橡胶3","deleting catalog entry preserves current project/editor material");
                var stale=JointMaterialCatalog.Read(settings); var fresh=JointMaterialCatalog.Read(settings); fresh.Entries.Add(new JointMaterialEntry{Name="CAS",Velocity=160});fresh.Save();
                bool rejected=false;try{stale.Save();}catch(IOException){rejected=true;}Check(rejected&&JointMaterialCatalog.Read(settings).Entries.Any(e=>e.Name=="CAS"),"stale cache refuses overwrite");
                byte[] bytes=File.ReadAllBytes(settings); File.SetAttributes(settings,FileAttributes.ReadOnly);
                try{Check(!d.TrySaveMaterial("no-write",100,false,out error)&&File.ReadAllBytes(settings).SequenceEqual(bytes),"read-only material cache stays unchanged");}finally{File.SetAttributes(settings,FileAttributes.Normal);}
                d.Close();
            }
            using(var page=new ProjectCreationControl())
            {
                page.ShowDialogMessages=false;page.SettingsPath=settings;
                var before=(ComboBox)typeof(ProjectCreationControl).GetField("beforeBodyTypeCombo",Flags).GetValue(page);
                var oldCatalog=typeof(ProjectCreationControl).GetField("catalog",Flags).GetValue(page);
                var oldList=oldCatalog.GetType().GetField("AttachmentTypes",Flags).GetValue(oldCatalog);
                var external=JointMaterialCatalog.Read(settings);external.Entries.Add(new JointMaterialEntry{Name="双向同步",Velocity=155.5});external.Save();
                Call(page,"RefreshSharedMaterialChoices");
                Check(before.Items.Cast<object>().Any(x=>x.ToString()=="双向同步"),"external dialog additions refreshed in page");
                var latest=typeof(ProjectCreationControl).GetField("catalog",Flags).GetValue(page);
                Check(Object.ReferenceEquals(oldCatalog,latest)&&Object.ReferenceEquals(oldList,latest.GetType().GetField("AttachmentTypes",Flags).GetValue(latest)),"refresh keeps reference menu list identity");
                Check((bool)Call(page,"TrySaveMaterial","从主页面添加",156.5,false,before),"page material addition persists after dialog change");
                Check(JointMaterialCatalog.Read(settings).Entries.Any(e=>e.Name=="双向同步")&&JointMaterialCatalog.Read(settings).Entries.Any(e=>e.Name=="从主页面添加"),"two-way additions survive sequential writes");
            }
            for(int cycle=0;cycle<12;cycle++)
            using(var d=new CableJointDialog(split,200,"A",cycle%2!=0,null))
            {
                d.ConfigureMaterialCatalog(settings);d.Font=new Font("Microsoft YaHei UI",cycle%3==0?14f:9.5f);d.ClientSize=cycle%3==0?new Size(650,600):new Size(900,780);Show(d);
                Select(d,cycle%6);Find<Button>(d,"NewJoint").PerformClick();Pump();
                foreach(string name in new[]{"ConfirmJoint","DeleteJoint","NewJoint","CancelJoint"})CheckVisible(Find<Button>(d,name),name+" cycle"+cycle);
                var headers=Find<DataGridView>(d,"ExistingJoints");
                Check(headers.ColumnHeadersHeight>=headers.Font.Height,"large-font column headers retain text height");
                foreach(DataGridViewColumn col in headers.Columns)Check(col.Width>=TextRenderer.MeasureText(col.HeaderText,headers.Font).Width+12,"complete header width: "+col.Name);
                using(var bitmap=new Bitmap(d.Width,d.Height)){d.DrawToBitmap(bitmap,new Rectangle(Point.Empty,d.Size));if(cycle<4)bitmap.Save(Path.Combine(root,"manager-"+cycle+".png"));}
                using(var nested=new Form{Icon=d.Icon,Font=d.Font,ClientSize=new Size(500,230)}){Show(nested);using(var bitmap=new Bitmap(nested.Width,nested.Height))nested.DrawToBitmap(bitmap,new Rectangle(Point.Empty,nested.Size));nested.Close();}
                if(cycle<4)
                {
                    Exception modalFailure=null; bool materialSubmitted=false;
                    using(var timer=new Timer{Interval=50})
                    {
                        timer.Tick+=(s,e)=>
                        {
                            var editor=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="JointMaterialEditor");if(editor==null)return;
                            timer.Stop();
                            try
                            {
                                Find<TextBox>(editor,"MaterialName").Text="Lifecycle material "+cycle;
                                Find<NumericUpDown>(editor,"MaterialVelocity").Value=151.25m;
                                using(var bitmap=new Bitmap(editor.Width,editor.Height)){editor.DrawToBitmap(bitmap,new Rectangle(Point.Empty,editor.Size));bitmap.Save(Path.Combine(root,"material-"+cycle+".png"));}
                                Find<Button>(editor,"SaveMaterial").PerformClick();materialSubmitted=true;
                            }
                            catch(Exception failure){modalFailure=failure;editor.Close();}
                        };
                        var materialBox=Find<ComboBox>(d,"JointMaterialBefore");var menu=materialBox.ContextMenuStrip;
                        menu.Show(materialBox,Point.Empty);timer.Start();((ToolStripMenuItem)menu.Items[0]).PerformClick();menu.Close();timer.Stop();
                    }
                    if(modalFailure!=null)throw modalFailure;
                    Check(materialSubmitted&&JointMaterialCatalog.Read(settings).Entries.Any(e=>e.Name=="Lifecycle material "+cycle&&e.Velocity==151.25),"actual nested material editor submits with shared icon/font lifecycle");
                }
                using(var bitmap=new Bitmap(d.Width,d.Height))d.DrawToBitmap(bitmap,new Rectangle(Point.Empty,d.Size));
                d.Close();Pump();
            }
            Console.WriteLine("CABLE_JOINT_MANAGER_PASS="+checks);Console.WriteLine("QA="+root);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
