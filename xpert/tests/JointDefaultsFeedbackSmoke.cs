using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using HVX.Controls;
using HVX.ProjectCables;

internal static class JointDefaultsFeedbackSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static string Block(string phase,string material){return "0\t"+phase+"\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\t"+material+"\t\r\n1\t接头 1\t预制\t80m\r\n2\t本体\t油纸\t\r\n1\t人工名称\t预制\t200m\r\n2\t本体\tXLPE\t\r\n1\t接头_3\t预制\t300m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t400m\r\n";}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            string root=Path.Combine(Path.GetFullPath(args[0]),"joint-defaults-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string item=Path.Combine(root,"ITEM.in"),cable=Path.Combine(root,"CABLE.in");
            File.WriteAllText(item,"电缆芯数: = \"三相单芯\"\r\n",new UTF8Encoding(false));
            File.WriteAllText(cable,Block("A","XLPE")+Block("A","PVC")+Block("B","油纸"),new UTF8Encoding(false));
            byte[] original=File.ReadAllBytes(cable),itemBytes=File.ReadAllBytes(item);
            var snapshot=CableJointStore.Read(root);
            Check(snapshot.SuggestedJointName(new[]{"A"},40)=="接头 1"&&snapshot.SuggestedJointName(new[]{"A"},240)=="接头 3","suggested number tracks insertion order");
            Check(snapshot.InsertionMaterials(new[]{"A"},40).SequenceEqual(new[]{"XLPE","PVC"}),"duplicate A materials remain separate");
            using(var dialog=new CableJointDialog(snapshot,120.34,"A",false,null)){
                var pos=(NumericUpDown)dialog.Controls.Find("JointPosition",true)[0];
                var name=(TextBox)dialog.Controls.Find("JointName",true)[0];
                var before=(ComboBox)dialog.Controls.Find("JointMaterialBefore",true)[0];
                Check(pos.DecimalPlaces==1&&pos.Value==120.3m&&name.Text=="接头 2"&&before.Text=="油纸","one decimal and current insertion material shown");
                pos.Value=40;Check(name.Text=="接头 1"&&before.Text=="","multi-material inheritance never copies one phase to another");
                string error;Check(dialog.TryPrepare(out error),"UI prepares additive plan: "+error);
                Check(dialog.Plan.ResolvedTargets.Select(t=>t.MaterialAfter).SequenceEqual(new[]{"XLPE","PVC"}),"default UI inheritance stored per actual segment");
                name.Text="用户人工标记";pos.Value=250;Check(name.Text=="用户人工标记","manual text is not renamed on cursor edit");
            }
            var plan=snapshot.Prepare(new[]{"A"},40,"接头 1","预制","","");string backup=CableJointStore.Commit(plan);
            var saved=CableJointStore.Read(root);
            foreach(var block in saved.Blocks.Take(2)){
                Check(block.Phase=="A"&&block.Length==400,"duplicate A phases/terminal length preserved");
                Check(block.AccessoryLines.Skip(1).Take(4).Select(i=>saved.Lines[i].Split('\t')[1]).SequenceEqual(new[]{"接头 1","接头 2","人工名称","接头 4"}),"position-order automatic numbering keeps manual name");
            }
            Check(saved.Lines.Skip(saved.Blocks[2].AccessoryLines[0]-1).SequenceEqual(snapshot.Lines.Skip(snapshot.Blocks[2].AccessoryLines[0]-1)),"unselected B block unchanged");
            Check(File.ReadAllBytes(backup).SequenceEqual(original)&&File.ReadAllBytes(item).SequenceEqual(itemBytes),"exact backup and ITEM unchanged");
            using(var control=new ProjectCreationControl()){
                control.ShowDialogMessages=false;control.CableMode="单相";control.SetPhaseCableLengths(400,400,400);
                Check(control.SetPhaseStructure(0,400,new[]{80d,200d},new[]{"预制","预制"},new[]{"XLPE","油纸","PVC"}),"create mixed segment fixture");
                Check(control.AddJoint("A","预制","",120.36),"create-page append succeeds");
                Check(control.GetPhaseJointRows("A")[1].StartsWith("接头 2\t")&&control.GetPhaseJointPositions(0)[1]==120.4,"creation uses ordinal names and one decimal");
                Check(control.GetPhaseSegmentBodyTypes(0).SequenceEqual(new[]{"XLPE","油纸","油纸","PVC"}),"creation blank material inherits containing segment");
                Check(!control.RemoveJoint("A",0)&&control.GetPhaseJointPositions(0).SequenceEqual(new[]{80d,120.4,200d}),"suppressing dialogs is not consent to merge different materials");
                int mergeDecisions=0;
                control.ConfirmJointMaterialMerge=message=>{mergeDecisions++;return message.Contains("XLPE")&&message.Contains("油纸")&&message.Contains("→");};
                Check(control.RemoveJoint("A",0)&&control.GetPhaseJointRows("A")[0].StartsWith("接头 1\t"),"allowed removal renumbers automatic names");
                Check(mergeDecisions==1,"explicit material merge decision is requested exactly once");
            }
            foreach(int width in new[]{360,700,1300})using(var canvas=new CableTopologyCanvas{Size=new Size(width,450),StartName="始端变电站一二三四五六七八九十十一十二十三十四",EndName="终端配电站一二三四五六七八九十十一十二十三十四",LengthA=400})
            using(var image=new Bitmap(width,450)){
                canvas.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                Rectangle start=canvas.StartNameBounds,end=canvas.EndNameBounds;
                Check(start.Width>(width-50)/2&&end.Right<=width,"endpoint names use available halves");
                Check(!start.IntersectsWith(end)&&(width>=700||start.Height>20),"long endpoint text has multiline space when needed, no ellipsis");
                image.Save(Path.Combine(root,"endpoint-"+width+".png"));
            }
            Console.WriteLine("JOINT_DEFAULTS_FEEDBACK_PASS="+checks+";hardware=false;QA="+root);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
