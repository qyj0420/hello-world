using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;
class Internal04Smoke
{
    static int checks;
    static void Check(bool ok,string name) { checks++; if(!ok)throw new Exception(name); }
    static object Call(object o,string name,params object[] args) { return o.GetType().GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Invoke(o,args); }
    static object Field(object o,string name) { return o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o); }
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            using(var form=new SettingsForm(new AppSettings())) {
                Check(!(bool)Field(form,"systemAdminUnlocked"),"locked initially");
                Call(form,"OpenSection",SettingsSection.SystemAdmin);Check((bool)Field(form,"systemAdminUnlocked"),"administration opens directly");
                foreach(var field in new[]{"expertUnlocked","deviceUnlocked","oemUnlocked"}) Check(!(bool)Field(form,field),"other permissions retained");
                Check(((TabPage)Field(form,"systemAdminTab")).Controls.OfType<TabControl>().Single().TabPages.Count==2,"hardware and logs only");
            }
            string settingsPath=Path.Combine(args[1],"oem-title.xml");Directory.CreateDirectory(args[1]);
            var settings=new AppSettings { WindowTitle="OEM · Test",LanguageCode="en" }; settings.Save(settingsPath);
            Check(AppSettings.Load(settingsPath).WindowTitle==settings.WindowTitle,"title persistence");
            using(var form=new MainForm(settings,new ApplicationState(),true)) Check(form.Text==settings.WindowTitle,"OEM title applied");
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            using(var review=(Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl",true)))
            using(var timer=new Timer{Interval=80})
            {
                Exception failure=null;bool seen=false;
                timer.Tick+=(s,e)=>{
                    var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Controls.OfType<CheckedListBox>().Any());
                    if(dialog==null)return;timer.Stop();seen=true;dialog.Location=new System.Drawing.Point(-30000,-30000);
                    try {
                        var list=dialog.Controls.OfType<CheckedListBox>().Single();
                        Check(list.Height>=list.ItemHeight*Math.Min(8,list.Items.Count),"sample labels have enough height");
                        foreach(var label in dialog.Controls.OfType<Label>()) Check(!label.Text.Contains("Task12"),"operator text excludes task identifier");
                        var footer=dialog.Controls.OfType<FlowLayoutPanel>().Single();
                        foreach(Button button in footer.Controls)Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+12,"sample action text complete");
                        using(var bitmap=new System.Drawing.Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,dialog.Size));bitmap.Save(Path.Combine(args[1],"sample-dialog.png"));}
                    }catch(Exception ex){failure=ex;}
                    finally{dialog.Close();}
                };
                timer.Start();Call(review,"ShowConfirmLearningSample");timer.Stop();
                if(failure!=null)throw failure;Check(seen,"sample dialog inspected");
            }
            using(var map=(Control)Activator.CreateInstance(assembly.GetType("LabVIEWPdMap.PdMapControl",true))) {
                var wave=new double[1024];
                for(int i=0;i<wave.Length;i++) wave[i]=.001*Math.Sin(i*1.77);
                for(int i=200;i<=215;i++) wave[i]+=(i-199)*.1;
                for(int i=216;i<=235;i++) wave[i]+=(236-i)*.08;
                for(int i=500;i<=515;i++) wave[i]-=(i-499)*.1;
                for(int i=516;i<=535;i++) wave[i]-=(536-i)*.08;
                Call(map,"SetWaveform",wave,8.0);Call(map,"SetInitialIndices",200,500);Call(map,"SetCableParameters",170.0,414.0);Call(map,"SetSelectionWindow",50,50);
                map.GetType().GetProperty("PositioningMode").SetValue(map,0,null);
                int onset=(int)Call(map,"SnapReflectionForMode",515);
                Check(onset>=498&&onset<=502,"negative reflection leading edge before trough: "+onset);
                Check((int)Call(map,"SnapReflectionForMode",700)==700,"noise retains manual release");
                int displayPeak=(int)Call(map,"DisplayIncidentPeak");
                Check(displayPeak>=215&&displayPeak<=216,"overlay follows incident peak");
                Check((int)Field(map,"incident")==200,"overlay does not modify arrival timing");
                if(args.Length>2)
                {
                    var raw=File.ReadAllLines(args[2]);
                    var actual=new double[1400];
                    for(int i=0;i<actual.Length;i++)actual[i]=double.Parse(raw[11+76475+i].Trim(),System.Globalization.CultureInfo.InvariantCulture)/100000;
                    Call(map,"SetWaveform",actual,8.0);Call(map,"SetInitialIndices",200,400);Call(map,"SetCableParameters",170.36,414.0);Call(map,"SetSelectionWindow",50,50);
                    int refined=(int)Call(map,"SnapReflectionForMode",400);
                    Check(refined>=411&&refined<=416,"local reflection rise instead of precursor: "+refined);
                    Console.WriteLine("REAL_WAVEFORM_ONSET="+(76475+refined)+"; ORIGINAL_UNMODIFIED=1");
                }
            }
            Console.WriteLine("INTERNAL04_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }
}
