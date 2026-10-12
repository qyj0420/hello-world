using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CalibrationEntryRenderingSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Type type;static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return type.GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
    static int Count(Bitmap image,Rectangle area,Func<Color,bool> predicate){int count=0;for(int x=area.Left;x<area.Right;x++)for(int y=area.Top;y<area.Bottom;y++)if(predicate(image.GetPixel(x,y)))count++;return count;}
    [STAThread]static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[1]);Application.EnableVisualStyles();
            var module=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            type=module.GetType("HVX.Controls.CalibrationProgramControl",true);
            using(var c=(Control)Activator.CreateInstance(type)) {
                int notices=0;Set(c,"ShowCalibrationEntryNoticeForTests",(Action<string>)(text=>{
                    notices++;Check((text.Contains("未确认")||text.Contains("不会加压")&&text.Contains("高压隔离"))&&!text.Contains("请按下急停"),"entry reminder is input-only and does not force LOW");
                    Check((text.Contains("未确认")||text.Contains("当前急停处于解除状态"))&&!text.Contains("已停止"),"entry reminder follows the observed or unknown state, not a fictitious stop");
                }));
                Set(c,"ReadCalibrationEntryEmergencyForTests",(Func<Task<bool?>>)(()=>Task.FromResult<bool?>(true)));
                Call(c,"NotifyCalibrationPageEntered");Check(notices==1,"explicit live page entry gives immediate reminder before capture");
                Check(!(bool)Get(c,"HasActiveHardwareSession")&&Get(c,"activeAcquisition")==null,"injected read does not create a hardware session");
                c.Size=new Size(1400,900);Call(c,"OnVisibleChanged",EventArgs.Empty);Call(c,"OnResize",EventArgs.Empty);
                Check(notices==1,"resize and restore do not reissue page-entry reminder");
                Call(c,"NotifyCalibrationPageEntered");Check(notices==2,"a second actual entry has its own reminder");
                Set(c,"ReadCalibrationEntryEmergencyForTests",(Func<Task<bool?>>)(()=>Task.FromResult<bool?>(false)));
                Call(c,"NotifyCalibrationPageEntered");Check(notices==2,"confirmed LOW entry does not repeat press-emergency reminder");
                Set(c,"ReadCalibrationEntryEmergencyForTests",(Func<Task<bool?>>)(()=>Task.FromResult<bool?>(null)));
                Call(c,"NotifyCalibrationPageEntered");Check(notices==3&&Get(c,"CalibrationEntryEmergencySignalHigh")==null,"unknown entry is not presented as HIGH or LOW");
                Set(c,"SimulationMode",true);Call(c,"NotifyCalibrationPageEntered");Check(notices==3,"simulation does not imply live high-voltage conditions");
            }
            using(var host=new Form{ClientSize=new Size(1600,1000),ShowInTaskbar=false,Opacity=0,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var c=(Control)Activator.CreateInstance(type)) {
                Set(c,"SimulationMode",true);Set(c,"EmbeddedMode",true);c.Dock=DockStyle.Fill;host.Controls.Add(c);host.Show();Application.DoEvents();
                Set(c,"SelectedLevelCode","5nC");Call(c,"SetPhaseParameters",-1,171.305d,159.206d,false,false);
                Call(c,"ShowNumericEditor",1);var editor=(TextBox)Get(c,"numericEditor");
                Check(editor.Text=="171.3","length editor shows one decimal");
                Call(c,"NumericEditorLostFocus",editor,EventArgs.Empty);
                Check(Math.Abs((double)Get(c,"CableLengthMeters")-171.305d)<1e-12,"untouched rounded editor does not overwrite calculation precision");
                Call(c,"ShowNumericEditor",1);editor.Text="172.123";Check((bool)Call(c,"CommitNumericEditor",true),"explicit length edit accepted");
                Check(Math.Abs((double)Get(c,"CableLengthMeters")-172.123d)<1e-12,"actual input retains internal precision");
                Check((string)Call(null,"MeasurementValue",171.305d,"m")=="171.3 m","result length display uses 0.1 m");
                Check((string)Call(null,"MeasurementValue",159.206d,"m/μs")=="159.206 m/μs","non-length calculation display is not indiscriminately rounded");
                var samples=Enumerable.Range(0,4096).Select(i=>Math.Exp(-Math.Pow((i-400)/8d,2))+.25*Math.Exp(-Math.Pow((i-1700)/8d,2))).ToArray();
                foreach(string level in new[]{"5nC","TDR"}) {
                    Set(c,"SelectedLevelCode",level);Call(c,"SetPhaseWaveform",-1,level,samples,0d,8e-9);
                    var phase=Call(c,"GetPhaseStateNoLock",-1);var wave=((IDictionary)Get(phase,"Waveforms"))[level];
                    var analysis=Call(c,"BuildManualResult",wave,phase,400,1700,null);
                    Call(c,"StoreAnalysisNoLock",phase,level,wave,analysis);
                    Call(c,"SetPhaseMarkers",-1,level,380*8e-9,1684*8e-9,2200*8e-9,171.305d);
                    var marker=((IDictionary)Get(phase,"Markers"))[level];
                    Call(c,"ResetView");Call(c,"EnsureWaveformView",wave);
                    var plot=Rectangle.Round((RectangleF)Get(c,"CurrentPlotRectangle"));
                    using(var bitmap=new Bitmap(2000,1200))using(var g=Graphics.FromImage(bitmap)) {
                        g.Clear(Color.White);Call(c,"DrawWaveformOverlay",g,wave,marker,analysis,159.206d);
                        bitmap.Save(Path.Combine(args[1],level+"-overlay.png"));
                        int orange=Count(bitmap,plot,p=>p.R==239&&p.G==143&&p.B==38);
                        Check(level=="TDR"?orange==0:orange>20,level=="TDR"?"TDR draws no decay curve":"charge calibration still draws physical peak decay");
                        var lower=new Rectangle(plot.Left,plot.Top+120,plot.Width,plot.Height-120);
                        Check(Count(bitmap,lower,p=>p.R==35&&p.G==138&&p.B==103)==0,"no extra green project-length reference line on "+level);
                        Check(Count(bitmap,lower,p=>p.R==196&&p.G==71&&p.B==71)>20,"actual incident timing marker remains on "+level);
                        Check(Count(bitmap,lower,p=>p.R==0&&p.G==163&&p.B==199)>20,"actual reflection marker remains on "+level);
                    }
                    foreach(int language in new[]{0,1}) {
                        Set(c,"LanguageIndex",language);
                        var lines=(string[])Call(c,"MeasurementInformationLines",-1,level,phase,wave,marker,analysis);
                        string decayLabel=language==0?"显示衰减系数：":"Display attenuation: ";
                        int decayLines=lines.Count(l=>l.StartsWith(decayLabel,StringComparison.Ordinal));
                        Check(decayLines==(level=="TDR"?0:1),"TDR and charge result information match decay applicability: "+level+"; language="+language+"; "+String.Join(" | ",lines));
                    }
                    Set(c,"LanguageIndex",0);
                    Check(samples.SequenceEqual((double[])Get(wave,"Samples")),"rendering and formatting do not alter waveform");
                }
                host.Close();
            }
            Console.WriteLine("CALIBRATION_ENTRY_RENDERING_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
