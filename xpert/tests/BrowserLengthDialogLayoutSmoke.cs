using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class BrowserLengthDialogLayoutSmoke
{
    static int checks;
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static T Find<T>(Control root,string name)where T:Control{return (T)root.Controls.Find(name,true).Single();}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass isolated release root and evidence directory.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Type type=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"))
                .GetType("HVX.Controls.ProjectDataBrowserControl",true);
            foreach(bool english in new[]{false,true})foreach(float points in new[]{10f,15f,20f})foreach(bool small in new[]{false,true})
            using(var font=new Font("Microsoft YaHei UI",points))
            using(var browser=(Control)Activator.CreateInstance(type))
            {
                browser.Font=font;type.GetProperty("EnglishVersion").SetValue(browser,english,null);
                Rectangle area=small?new Rectangle(0,0,640,320):new Rectangle(0,0,1280,720);
                using(var dialog=(Form)type.GetMethod("CreateLengthDialog",Flags).Invoke(browser,new object[]{414.8,new[]{"ALL_5nC","A_100pC"},area}))
                {
                    Check(dialog.StartPosition==FormStartPosition.CenterScreen,"Unowned dialog centered");
                    Check(dialog.Icon!=null,"XIONGXIN dialog icon supplied");
                    Check(dialog.ClientSize.Width<=area.Width-48&&dialog.ClientSize.Height<=area.Height-80,"Dialog fits supplied work area");
                    dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-30000,-30000);dialog.Show();
                    for(int i=0;i<4;i++)Application.DoEvents();
                    var content=Find<Panel>(dialog,"CorrectCableLengthContent");var scroll=Find<Panel>(dialog,"CorrectCableLengthScroll");
                    var footer=Find<Panel>(dialog,"CorrectCableLengthFooter");
                    var length=Find<NumericUpDown>(dialog,"CorrectCableLengthValue");var reference=Find<ComboBox>(dialog,"CorrectCableLengthReference");
                    Check(length.Value==414.8m&&reference.Items.Count==3&&reference.SelectedIndex==0,"Length and default reference matching preserved");
                    foreach(Label label in content.Controls.OfType<Label>())
                    {
                        int required=TextRenderer.MeasureText(label.Text,label.Font,new Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;
                        Check(label.Height>=required,"Label fits at "+points+"pt: "+label.Name);
                    }
                    foreach(Control child in content.Controls)Check(content.ClientRectangle.Contains(child.Bounds),"Content bounds contain "+child.Name);
                    Control[] rows={Find<Label>(dialog,"CorrectCableLengthNote"),Find<Label>(dialog,"CorrectCableLengthLabel"),Find<Label>(dialog,"CorrectCableLengthReferenceLabel"),Find<Label>(dialog,"CorrectCableLengthHint")};
                    Check(rows.Skip(1).Select((c,i)=>c.Top>=rows[i].Bottom).All(v=>v),"Rows do not overlap");
                    Check(reference.Top>=length.Bottom,"Inputs do not overlap");
                    Check(!scroll.HorizontalScroll.Visible,"No horizontal content scrollbar");
                    if(content.Height>scroll.ClientSize.Height)Check(scroll.VerticalScroll.Visible,"Small work area scrolls overflowing content instead of clipping");
                    var save=Find<Button>(dialog,"CorrectCableLengthSave");var cancel=Find<Button>(dialog,"CorrectCableLengthCancel");
                    Check(footer.ClientRectangle.Contains(save.Bounds)&&footer.ClientRectangle.Contains(cancel.Bounds)&&save.Right<=cancel.Left,"Save and cancel remain visible without overlap");
                    Check(dialog.AcceptButton==save&&dialog.CancelButton==cancel,"Enter and Escape semantics preserved");
                    using(var bitmap=new Bitmap(dialog.Width,dialog.Height))
                    {
                        dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,dialog.Size));
                        bitmap.Save(Path.Combine(output,"length-dialog-"+(english?"en":"zh")+"-"+points+"pt-"+(small?"small":"normal")+".png"));
                    }
                    // Never invoke Save/CorrectCableLength, and never load a real project.
                    dialog.Close();
                }
            }
            Console.WriteLine("BROWSER_LENGTH_LAYOUT_PASS="+checks);Console.WriteLine("EVIDENCE="+output);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
