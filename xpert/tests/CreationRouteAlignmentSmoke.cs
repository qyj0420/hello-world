using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CreationRouteAlignmentSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks++;}
    static void Pump(){for(int i=0;i<6;i++)Application.DoEvents();}
    static Rectangle Screen(Control c){return c.RectangleToScreen(c.ClientRectangle);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Directory.CreateDirectory(args[1]);
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            using(var module=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl",true)))
            using(var host=new Form{AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000)})
            {
                Set(module,"EmbeddedMode",true);Set(module,"ShowDialogMessages",false);Set(module,"OnlineWeatherEnabled",false);
                Set(module,"AutoRefreshWeatherOnHardwareSample",false);Set(module,"SettingsPath",Path.Combine(args[1],"route-settings.ini"));
                Set(module,"DataRootDirectory",args[1]);
                host.Controls.Add(module);module.Dock=DockStyle.Fill;host.Show();
                var first=(TextBox)Field(module,"startNameText");var last=(TextBox)Field(module,"endNameText");
                first.Text="Route start";last.Text="Route end";
                var caption=(Label)Field(module,"routeCaptionLabel");var mark=(TableLayoutPanel)caption.Parent;
                var arrow=mark.Controls.Find("ProjectRouteArrow",true).Single();
                var firstFrame=first.Parent;var lastFrame=last.Parent.Parent;
                var firstField=(TableLayoutPanel)firstFrame.Parent;var lastField=(TableLayoutPanel)lastFrame.Parent;
                var electrical=(Control)Field(module,"electricalFields");
                foreach(var size in new[]{new Size(1065,500),new Size(1256,620),new Size(1850,990)})
                foreach(int language in new[]{0,1})
                foreach(float points in new[]{10f,15f,20f})
                {
                    host.ClientSize=size;Set(module,"LanguageIndex",language);Pump();
                    var font=new Font("Microsoft YaHei UI",points);
                    first.Font=font;last.Font=font;caption.Font=font;
                    ((Label)Field(module,"startNameCaptionLabel")).Font=font;
                    ((Label)Field(module,"endNameCaptionLabel")).Font=font;
                    Pump();string scenario=size+" / language="+language+" / font="+points;
                    Rectangle a=Screen(firstFrame),b=Screen(lastFrame),d=Screen(arrow),c=Screen(caption),next=Screen(electrical);
                    Check(a.Top==b.Top&&a.Height==b.Height,"name frames share exact input row: "+scenario);
                    Check(d.Top==a.Top&&d.Height==a.Height,"direction arrow shares input frame bounds: "+scenario);
                    Check(c.Bottom<=d.Top&&c.Top==Screen(firstField).Top&&c.Top==Screen(lastField).Top,"route label shares name-caption row: "+scenario);
                    int width=TextRenderer.MeasureText(caption.Text,caption.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                    Check(c.Width>=width+4,"entire direction caption remains readable: "+scenario);
                    Check(d.Left>=a.Right&&d.Right<=b.Left,"direction remains strictly between name editors: "+scenario);
                    Check(a.Bottom<=next.Top&&b.Bottom<=next.Top&&d.Bottom<=next.Top,"route fields never overlap electrical row: "+scenario);
                    Check(Screen(module).Contains(a)&&Screen(module).Contains(b)&&Screen(module).Contains(d),"all route inputs remain inside viewport: "+scenario);
                    Check(first.Text=="Route start"&&last.Text=="Route end"&&!first.ReadOnly&&!last.ReadOnly,"layout preserves name values and editability: "+scenario);
                    if(points==20&&size.Width==1065)
                        using(var image=new Bitmap(module.Width,module.Height)){module.DrawToBitmap(image,module.ClientRectangle);image.Save(Path.Combine(args[1],"route-font20-language"+language+".png"));}
                }
                host.ClientSize=new Size(1065,500);caption.Font=new Font("Microsoft YaHei UI",9.5f);Pump();
                var style=(RowStyle)Field(module,"requiredRowStyle");
                Check(style.Height<=160,"returning to normal compact fonts does not retain historical large-field heights");
                Check(Screen(arrow).Top==Screen(firstFrame).Top&&Screen(arrow).Height==Screen(firstFrame).Height,"compact resize retains exact direction alignment");
            }
            Console.WriteLine("CREATION_ROUTE_ALIGNMENT_PASS="+checks+"; HARDWARE=False; USER_PROJECTS_WRITTEN=False; PHYSICAL_DPI=False");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
