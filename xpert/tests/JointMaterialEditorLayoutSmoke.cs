using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HVX.ProjectCables;

internal static class JointMaterialEditorLayoutSmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.Combine(args[0],"material-layout-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型\t一体三芯\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t户内冷缩\t400m\r\n",Encoding.UTF8);
            foreach(int language in new[]{0,1,18})foreach(float size in new[]{9.5f,18f,26f})
            using(var brand=new Icon(args[1]))
            using(var owner=new CableJointDialog(CableJointStore.Read(root),100,"ALL",language,brand))
            using(var font=new Font("Microsoft YaHei UI",size))
            using(var timer=new Timer{Interval=100}) {
                owner.Font=font;owner.ConfigureMaterialCatalog(Path.Combine(root,"materials.ini"));
                var before=(ComboBox)typeof(CableJointDialog).GetField("before",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);
                Exception failure=null;bool observed=false;
                timer.Tick+=(s,e)=>{
                    var editor=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="JointMaterialEditor");if(editor==null)return;
                    timer.Stop();observed=true;
                    try {
                        editor.PerformLayout();var grid=(TableLayoutPanel)editor.Controls[0];
                        Check(grid.ColumnStyles[0].SizeType==SizeType.Percent,"translated label column is responsive");
                        foreach(Control c in grid.Controls) {
                            Check(c.Width>0&&c.Height>0&&grid.ClientRectangle.Contains(c.Bounds),"editor control remains within content: "+c.Name);
                            if(c is Label||c is Button){var measured=TextRenderer.MeasureText(c.Text,c.Font,new Size(Math.Max(20,c.Width-(c is Button?6:0)),int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);Check(c.Height>=measured.Height,"complete text: "+c.Text+" actual="+c.Size+" measured="+measured+" language="+language+" font="+size);}
                        }
                        Check(editor.Icon!=null,"brand icon retained");
                        Check(!editor.AutoScroll,"editor does not depend on scrolling");
                    }catch(Exception ex){failure=ex;}finally{editor.Close();}
                };
                timer.Start();before.ContextMenuStrip.Items[0].PerformClick();
                Check(observed,"real material editor opened");if(failure!=null)throw failure;
                Check(!File.Exists(Path.Combine(root,"materials.ini")),"cancelled layout inspection writes no catalog");
            }
            Console.WriteLine("JOINT_MATERIAL_EDITOR_LAYOUT_PASS="+checks+"; QA="+root);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
