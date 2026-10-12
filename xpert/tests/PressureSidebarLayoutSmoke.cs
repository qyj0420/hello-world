using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
internal static class PressureSidebarLayoutSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks,failureCount;static readonly List<string> failures=new List<string>();
    static IEnumerable<Control> Children(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in Children(child))yield return nested;}}
    static object Call(object c,string name,params object[] args){return c.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(c,args);}
    static void Set(object c,string name,object value){c.GetType().GetProperty(name).SetValue(c,value,null);}
    static void Field(object c,string name,object value){c.GetType().GetField(name,All).SetValue(c,value);}
    static void Capture(Control c,string path){using(var bmp=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bmp,new Rectangle(Point.Empty,c.Size));bmp.Save(path);}}
    static void Check(bool ok,string s){checks++;if(!ok){failureCount++;if(failures.Count<80)failures.Add(s);}}
    static void Settle(Control c){for(int n=0;n<3;n++){Call(c,"RefreshPressureSidebarLayout");c.PerformLayout();Application.DoEvents();}}
    static void CheckPane(Control pane,Control module,string tag)
    {
        foreach(var c in Children(pane).Where(x=>x.Visible&&(x is ButtonBase||x is Label||x is TextBox)))
        {
            if(String.IsNullOrWhiteSpace(c.Text))continue;
            var rect=c.RectangleToScreen(c.ClientRectangle);bool contained=rect.Width>0&&rect.Height>0;
            for(var p=c.Parent;p!=null;p=p.Parent){var area=p.RectangleToScreen(p.ClientRectangle);contained&=area.Contains(rect);if(p==module)break;}
            Check(contained,tag+" clipped "+c.Name+" '"+c.Text.Replace('\n',' ') +"' "+rect+" parents="+ParentBounds(c,module));
            if(c is Label||c is ButtonBase){int inset=c is ButtonBase?14:0;var flags=c is Label&&((Label)c).AutoEllipsis?TextFormatFlags.SingleLine:TextFormatFlags.WordBreak;var measured=TextRenderer.MeasureText(c.Text,c.Font,new Size(Math.Max(1,c.ClientSize.Width-inset),Int32.MaxValue),flags|TextFormatFlags.NoPadding);Check(c.ClientSize.Height>=measured.Height+(c is ButtonBase?6:0),tag+" text clipped "+c.Name+" "+c.Text+" actual="+c.ClientSize+" need="+measured);}
        }
        foreach(var p in Children(pane).OfType<ScrollableControl>())Check(!p.AutoScroll&&!p.VerticalScroll.Visible&&!p.HorizontalScroll.Visible,tag+" no sidebar scrolling "+p.Name);
    }
    static void CheckSidebarAllocation(Control module,float scale,string tag)
    {
        var body=(TableLayoutPanel)module.Controls.Find("BodyLayout",true).Single();
        var left=module.Controls.Find("RealtimeSidebar",true).Single();
        var right=module.Controls.Find("PressureSidebar",true).Single();
        var center=module.Controls.Find("CenterChartsHost",true).Single();
        Check(body.GetColumnWidths()[1]<=6&&body.GetColumnWidths()[3]<=6,tag+" narrow noninteractive pane dividers");
        Check(center.ClientSize.Width>=module.ClientSize.Width*.42f,tag+" waveform retains useful width");
        if(scale==1f&&module.ClientSize.Width>=1366)
        {
            Check(left.Width>=160&&left.Width<=190,tag+" narrow left information sidebar, actual="+left.Width);
            Check(right.Width>=215&&right.Width<=290,tag+" narrow right control sidebar, actual="+right.Width);
            Check(center.Width>=module.ClientSize.Width-510,tag+" spare width assigned to charts, actual="+center.Width);
        }
        Console.WriteLine(tag+" SIDEBARS="+left.Width+","+right.Width+" CENTER="+center.Width);
    }
    static string ParentBounds(Control c,Control module){var rows=new List<string>();for(var p=c.Parent;p!=null;p=p.Parent){rows.Add(p.Name+":"+p.RectangleToScreen(p.ClientRectangle));if(p==module)break;}return String.Join("/",rows);}
    static void CheckLongMessages(Assembly asm,string buildRoot,string qa)
    {
        foreach(int language in new[]{0,1,18})foreach(float scale in new[]{1f,1.5f})
        foreach(Size size in new[]{new Size(1065,416),new Size(1280,480),new Size(1366,768)})
        using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
        using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
        {
            string icon=Path.Combine(buildRoot,"assets","xiongxin.ico");
            Check(File.Exists(icon),"long-message QA includes XIONGXIN icon");
            using(var brand=File.Exists(icon)?new Icon(icon):null)
            {
                if(brand!=null)form.Icon=brand;
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"LanguageIndex",language);
                var fonts=Children(c).Concat(new[]{c}).Select(x=>new{C=x,F=x.Font}).ToArray();
                foreach(var e in fonts)e.C.Font=new Font(e.F.FontFamily,e.F.SizeInPoints*scale,e.F.Style);
                c.Dock=DockStyle.None;c.Bounds=new Rectangle(Point.Empty,size);form.Controls.Add(c);form.Show();c.Size=size;Settle(c);
                Control right=c.Controls.Find("PressureSidebar",true).Single();
                foreach(int length in new[]{0,80,1200,4000})
                {
                    string tag="long-lang"+language+"-scale"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+"-"+size.Width+"x"+size.Height+"-chars"+length;
                    string chunk=language==1?"Pressure plan error: waveform reference invalid; check file D:\\QA\\measurement.loc. ":"加压计划错误：波形参考无效，请检查 QA 文件和完整诊断； ";
                    string message=length==0?String.Empty:String.Concat(Enumerable.Repeat(chunk,Math.Max(1,length/chunk.Length+1))).Substring(0,length-8)+" [ENDQA]";
                    Field(c,"projectPlanError",message);Call(c,"UpdatePostProcessUi");Settle(c);
                    CheckPane(right,c,tag);
                    var hint=(Label)c.Controls.Find("PostProcessHintLabel",true).Single();
                    var entry=(Button)c.Controls.Find("PressureMessageDetailsButton",true).Single();
                    var settings=c.Controls.Find("PressureSettingsScroll",true).Single();
                    Check(settings.Height>=100,tag+" settings not collapsed, actual="+settings.Height);
                    Check(!entry.Visible&&!hint.Visible,tag+" redundant details strip removed; debug source remains available");
                    Check(!hint.Visible||hint.Height<=hint.Font.Height*3+12,tag+" summary bounded to at most three lines");
                    Check((string)c.GetType().GetField("pressureActionFullMessage",All).GetValue(c)==message,tag+" full source message retained verbatim");
                    if(length>0)
                    {
                        Check(entry.AccessibleDescription==message,tag+" accessible full message");
                        using(var details=(Form)Call(c,"CreatePressureMessageDetailsDialog"))
                        {
                            var full=(TextBox)details.Controls.Find("PressureMessageFullText",true).Single();
                            Check(full.Text==message,tag+" details contain full original including tail");
                            Check(full.ReadOnly&&full.Multiline&&full.ScrollBars==ScrollBars.Vertical,tag+" scroll only inside read-only details");
                            Check(details.Text==(language==1?"Full message":language==18?"完整提示":"完整提示"),tag+" localized dialog title");
                            Check(details.Icon!=null&&details.Icon.Size==form.Icon.Size,tag+" owner branding cloned");
                            string copied=null;Call(details,"CopyFullMessage",new Action<string>(text=>copied=text));
                            Check(copied==message,tag+" copy preserves full text without touching real clipboard");
                            Call(details,"CopyFullMessage",new Action<string>(text=>{throw new System.Runtime.InteropServices.ExternalException("offline clipboard unavailable");}));
                            var status=details.Controls.Find("PressureMessageCopyStatus",true).Single();
                            Check(status.Text.Contains(language==1?"Clipboard unavailable":language==18?"剪貼板":"剪贴板"),tag+" copy failure gives localized alternative");
                            Check(full.Text==message,tag+" failed copy retains full text");
                            details.StartPosition=FormStartPosition.Manual;details.Location=new Point(-20000,-20000);details.Show();Application.DoEvents();
                            if(length==4000&&language==0&&scale==1f&&size.Width==1065)Capture(details,Path.Combine(qa,"full-message-dialog-4000.png"));
                            details.Close();
                        }
                        foreach(string tab in new[]{"CompactModeTab","CompactRangeTab","CompactProgramTab"})
                        {
                            var button=(Button)c.Controls.Find(tab,true).Single();
                            if(button.Visible){Check(button.Enabled,tag+" settings tab reachable "+tab);Call(button,"OnClick",EventArgs.Empty);Settle(c);CheckPane(right,c,tag+" "+tab);}
                        }
                    }
                    if(length==4000&&(scale==1f||language==1&&size.Width==1065))Capture(c,Path.Combine(qa,tag+".png"));
                    Check(!(bool)c.GetType().GetProperty("IsBusy").GetValue(c,null),tag+" no acquisition started");
                    Check(Convert.ToInt64(c.GetType().GetProperty("PressureRequestSequence").GetValue(c,null))==0L,tag+" no pressure or hardware request written");
                }
                Field(c,"projectPlanError",String.Empty);Call(c,"UpdatePostProcessUi");Settle(c);
                Check(!c.Controls.Find("PressureMessageDetailsButton",true).Single().Visible,"cleared error removes full-message entry");
                form.Close();
            }
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"pressure-layout-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            string buildRoot=Path.GetFullPath(args[0]);
            var asm=Assembly.LoadFrom(Path.Combine(buildRoot,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            foreach(int language in new[]{0,1,18})foreach(float scale in new[]{1f,1.25f,1.5f})foreach(Size size in new[]{new Size(1280,600),new Size(1920,960),new Size(1280,480),new Size(1920,1080),new Size(1366,648)})
            using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
            {
                string tag="lang"+language+"-scale"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+"-"+size.Width+"x"+size.Height;
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"LanguageIndex",language);
                var fonts=Children(c).Concat(new[]{c}).Select(x=>new{C=x,F=x.Font}).ToArray();
                foreach(var e in fonts)e.C.Font=new Font(e.F.FontFamily,e.F.SizeInPoints*scale,e.F.Style);
                // Layout the actual embedded control at the requested dimensions,
                // not a top-level window which Windows caps to the QA monitor.
                c.Dock=DockStyle.None;c.Bounds=new Rectangle(Point.Empty,size);form.Controls.Add(c);form.Show();c.Size=size;Settle(c);
                Check(c.ClientSize==size,tag+" actual test viewport "+c.ClientSize);
                CheckSidebarAllocation(c,scale,tag);
                Control left=c.Controls.Find("RealtimeSidebar",true).Single(),right=c.Controls.Find("PressureSidebar",true).Single();
                var max=(Button)c.Controls.Find("MaximumWindowButton",true).Single();Check(max.Text=="∞",tag+" full-time window uses infinity");
                Call(c,"SetWaveformWindow","ONE");Call(max,"OnClick",EventArgs.Empty);Check((string)c.GetType().GetProperty("WaveformWindowCode").GetValue(c,null)=="MAXIMUM",tag+" infinity retains full record window semantics");
                CheckPane(left,c,tag+" metrics");CheckPane(right,c,tag+" voltage");
                var taskBar=c.Controls.Find("TaskInformationBar",true).Single();CheckPane(taskBar,c,tag+" task status");
                Check(c.Controls.Find("UseCompensationCapacitance",true).Single().Visible,tag+" compensation option available beside capacitance");
                var tanCaption=c.Controls.Find("MetricTanDeltaCaption",true).Single();
                Check(tanCaption.Text.Contains(language==1?"Criteria not configured":language==18?"未配置適用判據":"未配置适用判据"),tag+" neutral tan delta criterion caption visible before measurements");
                if(size.Height==600||size.Width==1920||size.Width==1366||(size.Height==480&&scale==1.5f))Capture(c,Path.Combine(qa,tag+"-metrics.png"));
                Call(c,"ApplyRealtimeMetrics",1d,.5,50d,-.5);Settle(c);CheckPane(left,c,tag+" negative tan delta estimate");
                Call(c,"ClearDisplayData");Settle(c);
                Set(c,"AdvancedMode",true);Set(c,"AutomaticRange",false);
                Call(c,"SetDischargeCalibration",10000d,"QA synthetic range coefficient",DateTimeOffset.UtcNow.AddMinutes(-1),"A",true);
                Call(c,"SetSelectedRange","PICO:5");Settle(c);
                var rangeTab=(Button)c.Controls.Find("CompactRangeTab",true).Single();
                if(rangeTab.Visible){Call(rangeTab,"OnClick",EventArgs.Empty);Settle(c);}
                CheckPane(right,c,tag+" calibrated Pico range");
                if(scale==1f&&size.Width==1366)Capture(c,Path.Combine(qa,tag+"-pico-range.png"));
                Call(c,"BeginCustomVoltageEdit");Settle(c);CheckPane(right,c,tag+" voltage editor");Call(c,"EndCustomVoltageEdit",false);
                foreach(string tab in new[]{"RealtimeMoreMetricsTab","RealtimeDisplayTab","CompactModeTab","CompactRangeTab"}){var button=(Button)c.Controls.Find(tab,true).Single();if(button.Visible){Call(button,"OnClick",EventArgs.Empty);Settle(c);CheckPane(tab.StartsWith("Realtime")?left:right,c,tag+" "+tab);}}
                Set(c,"VlfPolarityAvailable",true);Settle(c);CheckPane(left,c,tag+" VLF");
                if(size.Height==600)Capture(c,Path.Combine(qa,tag+".png"));
                foreach(string state in new[]{"SAVE_REQUESTED","OVERRANGE_DECISION_REQUIRED","SAVE_FAILED"})
                {
                    // Only exercise notification layout; no acquisition or save request.
                    Field(c,"postProcessStateCode",state);Call(c,"UpdatePostProcessUi");Settle(c);CheckPane(right,c,tag+" "+state);
                    var button=(Button)c.Controls.Find("CompactProgramTab",true).Single();if(button.Visible){Call(button,"OnClick",EventArgs.Empty);Settle(c);CheckPane(right,c,tag+" "+state+" custom");}
                }
                Field(c,"postProcessStateCode","READY");Call(c,"UpdatePostProcessUi");Settle(c);
                Set(c,"SimulationMode",false);Settle(c);CheckPane(right,c,tag+" live unknown");
                Call(c,"ReportP04Status",true,false,DateTime.UtcNow);Settle(c);CheckPane(right,c,tag+" live not started");
                Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Settle(c);CheckPane(right,c,tag+" live enabled");
                Call(c,"SetPressurePlan",new double[]{.5,1},new int[]{2,3},new double[]{5,10});
                Call(c,"SetPressureProgramMode","PLAN");Settle(c);CheckPane(right,c,tag+" plan");
                Check((string)c.GetType().GetProperty("PressureProgramModeCode").GetValue(c,null)=="PLAN",tag+" grouped plan retains actual mode");
                Check(!(bool)c.GetType().GetProperty("IsBusy").GetValue(c,null),tag+" layout does not start acquisition");form.Close();
            }
            CheckLongMessages(asm,buildRoot,qa);
            foreach(string failure in failures)Console.WriteLine("FAIL "+failure);
            Console.WriteLine("PRESSURE_SIDEBAR_LAYOUT_CHECKS="+checks+" FAILURES="+failureCount+";DETAILS_RETAINED="+failures.Count+";HARDWARE=False;QA="+qa);return failureCount==0?0:1;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
    }
}
