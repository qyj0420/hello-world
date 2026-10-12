using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ProjectCreationGuideSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Set(object owner,string name,object value){owner.GetType().GetProperty(name).SetValue(owner,value,null);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name).GetValue(owner,null);}
    static void Pump(){for(int i=0;i<3;i++)Application.DoEvents();}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var type=Assembly.LoadFrom(Path.Combine(args[0],"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll")).GetType("HVX.Controls.ProjectCreationControl",true);
            foreach(int language in new[]{0,1,18})
            using(var host=new Form{ClientSize=new Size(1280,800),ShowInTaskbar=false,Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual})
            using(var control=(Control)Activator.CreateInstance(type))
            {
                Set(control,"EmbeddedMode",true);Set(control,"LanguageIndex",language);Set(control,"StartName","QA draft start");Set(control,"EndName","QA draft end");
                control.Dock=DockStyle.Fill;host.Controls.Add(control);host.Show();Pump();
                string page=(string)Get(control,"ExpandedOptionalSectionCode");bool dirty=(bool)Get(control,"IsDirty");
                Image[] pages=(Image[])type.GetMethod("CreateProjectGuidePages").Invoke(control,null);
                Check(pages.Length==3&&pages.All(p=>p!=null&&p.Width==1850&&p.Height==990),"three current UI guide captures");
                Check((string)Get(control,"StartName")=="QA draft start"&&(string)Get(control,"EndName")=="QA draft end","live draft retained");
                Check((string)Get(control,"ExpandedOptionalSectionCode")==page&&(bool)Get(control,"IsDirty")==dirty,"capture does not switch live page or dirty state");
                for(int i=0;i<pages.Length;i++)pages[i].Save(Path.Combine(qa,"guide-"+language+"-"+i+".png"));
                // Dialog owns the captures. Use independent dialog instances for
                // each font to exercise resource ownership and navigation.
                using(var font=new Font("Microsoft YaHei",12f))
                using(var dialog=new ProjectCreationGuideDialog(pages,language,font))
                {
                    dialog.Location=new Point(-20000,-20000);dialog.StartPosition=FormStartPosition.Manual;dialog.Show();Pump();
                    var previous=(Button)dialog.Controls.Find("GuidePrevious",true).Single();var next=(Button)dialog.Controls.Find("GuideNext",true).Single();
                    Check(dialog.PageIndex==0&&!previous.Enabled&&next.Enabled,"first page navigation");
                    next.PerformClick();Pump();Check(dialog.PageIndex==1&&previous.Enabled&&next.Enabled,"next page");
                    next.PerformClick();Pump();Check(dialog.PageIndex==2&&previous.Enabled&&!next.Enabled,"last page navigation");
                    previous.PerformClick();Check(dialog.PageIndex==1,"previous page");
                    var suppression=(CheckBox)dialog.Controls.Find("SuppressProjectCreationGuide",true).Single();
                    Check(!dialog.DoNotShowAgain,"suppression is opt in");suppression.Checked=true;Check(dialog.DoNotShowAgain,"explicit suppression captured");
                    foreach(float size in new[]{10f,16f,22f})using(var large=new Font("Microsoft YaHei",size))
                    {
                        dialog.Font=large;
                        foreach(Size bounds in new[]{new Size(720,520),new Size(1100,760)})
                        {
                            dialog.Size=bounds;Pump();
                            foreach(Control c in new Control[]{suppression,previous,next,dialog.Controls.Find("GuideClose",true).Single()})
                            {
                                Check(c.Visible&&c.Width>0&&c.Height>=c.Font.Height,"guide action has visible text height");
                                var box=dialog.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                                Check(dialog.ClientRectangle.Contains(box),"guide action inside window at font "+size+" / "+bounds);
                            }
                            var picture=FindPicture(dialog);Check(picture.Width>=160&&picture.Height>=100,"image retains usable view at font "+size+" / "+bounds);
                        }
                    }
                    dialog.Controls.Find("GuideClose",true).OfType<Button>().Single().PerformClick();Pump();Check(!dialog.Visible,"close exits guide");
                }
            }
            string path=Path.Combine(qa,"guide-preference.xml");var settings=new AppSettings();Check(settings.ShowProjectCreationGuide,"new and legacy config default guide on");
            settings.DataRoot=Path.Combine(qa,"different-data-root");settings.Save(path);MainForm.SaveProjectGuidePreference(path,false);
            AppSettings loaded;string error;Check(AppSettings.TryLoad(path,out loaded,out error)&&!loaded.ShowProjectCreationGuide&&loaded.DataRoot==settings.DataRoot,"suppression saves only guide flag, retaining current other settings");
            MainForm.SaveProjectGuidePreference(path,true);Check(AppSettings.TryLoad(path,out loaded,out error)&&loaded.ShowProjectCreationGuide,"general setting can restore guide");
            File.WriteAllText(path,"<broken>");byte[] before=File.ReadAllBytes(path);bool rejected=false;
            try{MainForm.SaveProjectGuidePreference(path,false);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&before.SequenceEqual(File.ReadAllBytes(path)),"malformed settings remain unchanged");
            Console.WriteLine("PROJECT_CREATION_GUIDE_PASS="+checks+";HARDWARE=False;LIVE_PROJECT_WRITTEN=False;LIVE_SETTINGS_MODIFIED=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static PictureBox FindPicture(Control root){foreach(Control c in root.Controls){if(c is PictureBox)return (PictureBox)c;var nested=FindPicture(c);if(nested!=null)return nested;}return null;}
}
