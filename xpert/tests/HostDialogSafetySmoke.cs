using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Built-executable/module UI checks. Never starts Program.Main, discovers a
// device, grants settings access, saves a project or invokes hardware actions.
static class HostDialogSafetySmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks; static string output;
    static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    static object Make(Assembly a,string name,params object[] values){return Activator.CreateInstance(a.GetType(name,true),All,null,values,null);}
    static object Call(object value,string method,params object[] args){return value.GetType().GetMethod(method,All).Invoke(value,args);}
    static void Set(object value,string field,object data){value.GetType().GetField(field,All).SetValue(value,data);}
    static Control[] Children(Control root){return root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Children(c))).ToArray();}
    static void Pump(Form form){for(int i=0;i<4;i++){form.PerformLayout();Application.DoEvents();}}
    static void Show(Form form){form.ShowInTaskbar=false;form.Show();Pump(form);}
    static void Shot(Form form,string name){using(var b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,form.Size));b.Save(Path.Combine(output,name+".png"));}}
    static void TextFits(Label label,string scenario){int height=TextRenderer.MeasureText(label.Text,label.Font,new Size(Math.Max(1,label.Width-label.Padding.Horizontal),int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;if(label.Height-label.Padding.Vertical<height&&label.FindForm()!=null)Shot(label.FindForm(),"failed-label-"+label.Name+checks);Check(label.Height-label.Padding.Vertical>=height,scenario+" required="+height+" actual="+label.Height+" bounds="+label.Bounds+" max="+label.MaximumSize+" font="+label.Font.Size);}
    static void ActionsFit(Form form,string name)
    {
        var footer=form.Controls.Find(name,true).Single();Check(footer.Height>0&&footer.Visible,name+" visible");
        foreach(Button button in footer.Controls.OfType<Button>()){
            Check(footer.ClientRectangle.Contains(button.Bounds),name+" button contained: "+button.Text);
            Check(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),name+" button on window: "+button.Text);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);if(Directory.Exists(output))throw new IOException("Use a fresh QA directory.");Directory.CreateDirectory(output);
            var host=Assembly.LoadFrom(args.Length>2?Path.GetFullPath(args[2]):Path.Combine(root,"Xiongxin.OWTS.Desktop.exe"));
            var pressure=Assembly.LoadFrom(Path.Combine(root,"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
            Check(pressure.GetManifestResourceNames().Contains("XIONGXIN.Brand.ico"),"range host has brand resource");
            foreach(float points in new[]{9.5f,14.25f,18f,24f})using(var font=new Font("Microsoft YaHei UI",points)){
                object quality=Make(pressure,"Xiongxin.ZeroBench.LocationRangeResult");
                object context=Make(pressure,"Xiongxin.ZeroBench.RangeDecisionContext",quality,5,6,0d,0,true,false,false);
                using(var range=(Form)Make(pressure,"Xiongxin.ZeroBench.PressureRangeDecisionDialog",context,null)){
                    range.Font=font;Show(range);Call(range,"FitWorkingArea",new Rectangle(0,0,1280,680));Pump(range);
                    Check(range.Bottom<=680&&range.Right<=1280,"range fits 1280x680 working area");ActionsFit(range,"RangeDecisionActions");
                    var details=(TextBox)range.Controls.Find("RangeDecisionDetails",true).Single();
                    Check(details.ReadOnly&&details.Multiline&&details.WordWrap,"range verdict remains read-only and wraps");
                    Check(new[]{"量程暂无法判断，请检查采集数据。","量程过小","量程过大","当前量程无需调整"}.Contains(details.Text),"range details contain only an operator verdict, not internal diagnostics");
                    int verdictHeight=TextRenderer.MeasureText(details.Text,details.Font,new Size(Math.Max(1,details.ClientSize.Width-8),int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;
                    Check(details.ClientSize.Height>=verdictHeight+4,"compact range verdict fits at font "+points+" required="+verdictHeight+" actual="+details.ClientSize.Height);
                    Check(((Panel)range.Controls.Find("RangeDecisionViewport",true).Single()).AutoScroll,"range warnings remain reachable by scrolling");
                    Check(range.AcceptButton==null,"Enter never implicitly accepts range decision");
                    range.Close();Check(CallProperty(range,"Decision").ToString()=="Dismiss","closing range does not ignore or retry");
                }
                using(var after=new Form()){Show(after);Check(after.Icon!=null,"default icon survives no-owner range close");}
            }
            foreach(string language in new[]{"zh-CN","en","zh-TW"})foreach(float points in new[]{9.5f,18f,24f})using(var font=new Font("Microsoft YaHei UI",points)){
                object settings=Make(host,"Xiongxin.OWTS.Desktop.AppSettings");settings.GetType().GetProperty("LanguageCode").SetValue(settings,language,null);
                using(var owner=(Form)Make(host,"Xiongxin.OWTS.Desktop.SettingsForm",settings)){
                    owner.Font=font;
                    var access=host.GetType("Xiongxin.OWTS.Desktop.SettingsAccessControl");
                    bool guarded=access!=null&&(bool)access.GetField("Enabled",BindingFlags.Static|BindingFlags.NonPublic).GetRawConstantValue();
                    foreach(string section in new[]{"Device","SystemAdmin","PlusData","Oem","Expert"}){
                        var enumType=host.GetType("Xiongxin.OWTS.Desktop.SettingsSection",true);if(!Enum.IsDefined(enumType,section))continue;
                        Call(owner,"SelectSection",Enum.Parse(enumType,section));
                        Check(owner.Controls.Find("AuthorizationPassword",true).Length==0,"navigation does not prompt or retain password on "+section);
                        if(guarded){
                            Check(owner.Controls.Find("UnlockSettings"+section,true).Length==1,"maintenance entry remains locked on "+section);
                            Check(!(bool)Call(owner,"IsSectionAuthorized",Enum.Parse(enumType,section)),"navigation never authorizes "+section);
                            string editor=section=="Device"?"dividerRatio":section=="Expert"?"minus3Attenuation":section=="Oem"?"softwareModel":section=="SystemAdmin"?"hardwareEnabled":"minus3AlgorithmIndex";
                            Check(owner.GetType().GetField(editor,All).GetValue(owner)==null,"locked editor is not created on "+section);
                        }
                        Check(owner.DialogResult==DialogResult.None,"opening settings does not apply configuration");

                    }
                }
                foreach(string state in new[]{"Empty","Incomplete","Ambiguous","InUse","Available","Expired"}){
                    DateTime now=state=="Expired"?DateTime.UtcNow.AddSeconds(-31):DateTime.UtcNow;
                    string entries=state=="Empty"?"":"<Device Mac=\"001122334455\" Ip=\"192.0.2.1\" ControllerLocation=\""+(state=="Incomplete"||state=="Ambiguous"?"":"1-1")+"\" AcquisitionLocation=\""+(state=="Incomplete"||state=="Ambiguous"?"":"1-2")+"\" PersistentBound=\"false\" Complete=\""+(state=="Incomplete"||state=="Ambiguous"?"false":"true")+"\" State=\""+(state=="Expired"?"Available":state)+"\"/>";
                    string xml="<HardwareDiscovery Schema=\"1\" State=\"Complete\" MaxAgeSeconds=\"30\" Utc=\""+now.ToString("O",CultureInfo.InvariantCulture)+"\">"+entries+"</HardwareDiscovery>";
                    var parser=host.GetType("Xiongxin.OWTS.Desktop.HardwareDeviceDiscovery",true).GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic);
                    object receipt=parser.Invoke(null,new object[]{xml,now});
                    using(var dialog=(Form)Make(host,"Xiongxin.OWTS.Desktop.DetectionDeviceSelectionForm",receipt,language,"")){
                        dialog.Font=font;Show(dialog);Pump(dialog);ActionsFit(dialog,"DiscoveryActions");
                        var intro=(Label)dialog.Controls.Find("DiscoveryIntroduction",true).Single();TextFits(intro,"discovery "+state);
                        Check(!dialog.Controls.Find("ConnectSelectedDetector",true).Single().Enabled,"showing "+state+" never implicitly selects device");
                        Check(!(bool)Call(dialog,"TrySelect",DateTime.UtcNow),"no explicit selection cannot connect");
                        if(state=="Empty")Check(intro.Text.Contains(language=="en"?"No detectors":"未"),"honest empty intro");
                        if(state=="Expired")Check(dialog.Controls.Find("DiscoveryStatus",true).Single().Text.Contains(language=="en"?"Expired":"期"),"expiry shown immediately");
                        if(state=="Empty"&&language=="en"&&points==18)Shot(dialog,"discovery-empty-en-18pt");
                    }
                }
            }
            foreach(int lang in new[]{0,1,18})foreach(float points in new[]{9.5f,18f,24f})using(var font=new Font("Microsoft YaHei UI",points)){
                object nc=Make(host,"Xiongxin.OWTS.Noise.NoiseAcquisitionContext");Set(nc,"Target",0d);Set(nc,"Simulation",true);Set(nc,"GeneratorZero",true);Set(nc,"Phase","A");Set(nc,"Source",new string('X',1024));Set(nc,"Rate",125000000d);Set(nc,"Bits",12);Set(nc,"CapturedUtc",DateTimeOffset.UtcNow);
                object reference=Make(host,"Xiongxin.OWTS.Noise.ZeroVoltageNoiseReference",new double[]{0,1,0,-1},nc);
                using(var panel=(Control)Make(host,"Xiongxin.OWTS.Noise.NoiseReferencePanel"))using(var timer=new Timer{Interval=50}){
                    panel.Font=font;Call(panel,"SetReference",reference,lang);Exception failure=null;bool seen=false;
                    timer.Tick+=(s,e)=>{
                        var f=Application.OpenForms.Cast<Form>().FirstOrDefault(x=>x.Controls.Find("NoiseReferenceUseNotice",true).Length==1);if(f==null)return;
                        timer.Stop();try{
                            f.ClientSize=new Size(620,470);Pump(f);var notice=(Label)f.Controls.Find("NoiseReferenceUseNotice",true).Single();TextFits(notice,"noise safety notice");Check(f.ClientRectangle.Contains(f.RectangleToClient(notice.RectangleToScreen(notice.ClientRectangle))),"noise notice remains on screen");Check(f.Controls.Find("NoiseReferenceSource",true).Single().Text.Length==1024,"long source retained");if(lang==1&&points==18)Shot(f,"noise-en-18pt");
                            f.Size=new Size(620,360);Pump(f);TextFits(notice,"minimum-window noise safety notice");Check(f.ClientRectangle.Contains(f.RectangleToClient(notice.RectangleToScreen(notice.ClientRectangle))),"noise notice visible at actual minimum outer size");
                            var viewport=(Panel)f.Controls.Find("NoiseReferenceViewport",true).Single();var plot=f.Controls.Find("NoiseReferencePlot",true).Single();
                            Check(viewport.Height>=60&&plot.Height>=160,"minimum noise window retains scrolling body and meaningful plot");
                            Check(viewport.AutoScroll,"noise body can reach overflowed content");viewport.AutoScrollPosition=new Point(0,plot.Parent.Height);Pump(f);
                            var plotOnViewport=viewport.RectangleToClient(plot.RectangleToScreen(plot.ClientRectangle));
                            if(plotOnViewport.Bottom>viewport.ClientSize.Height||plotOnViewport.Bottom<=0)Shot(f,"failed-noise-scroll-"+lang+"-"+points);
                            Check(plotOnViewport.Bottom<=viewport.ClientSize.Height&&plotOnViewport.Bottom>0,"noise plot bottom/axis reachable at end of scroll; plot="+plotOnViewport+" viewport="+viewport.ClientSize+" scroll="+viewport.AutoScrollPosition+" maximum="+viewport.VerticalScroll.Maximum+" body="+plot.Parent.Bounds+" focus="+f.ActiveControl);
                            if(lang==1&&points==24)Shot(f,"noise-minimum-en-24pt");seen=true;
                        }catch(Exception error){failure=error;}finally{f.Close();}
                    };timer.Start();Call(panel,"ShowReference");if(failure!=null)throw failure;Check(seen,"noise dialog exercised");
                }
            }
            foreach(float points in new[]{9.5f,14.25f,18f,24f})using(var font=new Font("Microsoft YaHei UI",points))
            using(var plot=(Control)Make(host,"Xiongxin.OWTS.Noise.NoiseReferencePanel+NoiseReferencePlot",new double[]{0,1,0,-1})){
                plot.Font=font;plot.Size=new Size(620,180);var box=(Rectangle)Call(plot,"GetPlotBounds");
                Check(box.Bottom+3+TextRenderer.MeasureText("0",font).Height<=plot.Height,"noise axis fits at "+points+"pt");
                Check(TextRenderer.MeasureText("pC",font).Width+5<box.Left,"noise unit fits left margin");
                Call(plot,"EnsureEnvelope",2);var lows=(double[])plot.GetType().GetField("envelopeLow",All).GetValue(plot);var highs=(double[])plot.GetType().GetField("envelopeHigh",All).GetValue(plot);
                Check(lows.SequenceEqual(new double[]{0,-1})&&highs.SequenceEqual(new double[]{1,0}),"noise paint bins retain exact extrema");
                Call(plot,"EnsureEnvelope",2);Check(Object.ReferenceEquals(lows,plot.GetType().GetField("envelopeLow",All).GetValue(plot)),"unchanged-size repaint reuses immutable envelope");
                Call(plot,"EnsureEnvelope",3);Check(!Object.ReferenceEquals(lows,plot.GetType().GetField("envelopeLow",All).GetValue(plot)),"resizing refreshes display envelope");
            }
            var fitWork=host.GetType("Xiongxin.OWTS.Desktop.AppTheme",true).GetMethod("FitDialogToWorkingArea",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(Form),typeof(Rectangle)},null);
            foreach(var work in new[]{new Rectangle(0,0,1280,680),new Rectangle(-1280,20,1280,680),new Rectangle(1600,100,500,390)})using(var f=new Form{Size=new Size(1800,1200),MinimumSize=new Size(900,700),Location=new Point(3000,3000)}){
                fitWork.Invoke(null,new object[]{f,work});Check(work.Contains(f.Bounds),"dialog constrained to chosen work area including negative monitor coordinates");
            }
            Console.WriteLine("PASS HostDialogSafetySmoke checks="+checks+";hardware=false;userData=false;physicalDpi=false;QA="+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static object CallProperty(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
}
