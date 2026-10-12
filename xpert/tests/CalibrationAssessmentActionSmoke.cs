using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationAssessmentActionSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            string output=Path.Combine(Path.GetFullPath(args[1]),"assessment-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            using(var c=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.CalibrationProgramControl",true))) {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"SelectedLevelCode","100pC");
                Call(c,"SetPhaseParameters",-1,180d,180d,false,false);
                var samples=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))+.04*Math.Exp(-Math.Pow((i-600)/8d,2))).ToArray();
                Call(c,"SetPhaseWaveform",-1,"100pC",samples,0d,1e-8);
                foreach(int width in new[]{1024,1280,1920})foreach(int height in new[]{600,900}) {
                    c.Size=new Size(width,height);Call(c,"SavePng",Path.Combine(output,"assessment-"+width+"x"+height+".png"),width,height);
                    object ui=Get(c,"CompactLayout");var button=(RectangleF)Field(ui,"Assessment");var pulse=(RectangleF)Field(ui,"PulseMode");var plot=(RectangleF)Field(ui,"Plot");
                    Check(button.Left>=pulse.Right&&!button.IntersectsWith(plot)&&button.Right<=width,"assessment follows pulse mode and lies outside waveform");
                    var card=(RectangleF)Field(c,"measurementInformationHit");
                    Check(card.Width>0&&card.Bottom<=plot.Bottom&&card.Right<=plot.Right,"wrapped larger-font summary stays in plot");
                    string text=(string)Field(c,"measurementInformationText");
                    Check(!text.Contains("幅值衰减")&&text.Contains("衰减系数")&&text.Contains("非概率"),"amplitude attenuation hidden, decay and scoring evidence retained");
                    var center=new Point((int)(card.Left+card.Width*.5),(int)(card.Top+card.Height*.5));
                    Check(!(bool)Call(c,"HandleCompactMouseUp",new MouseEventArgs(MouseButtons.Left,1,center.X,center.Y,0)),"summary card no longer consumes waveform clicks");
                }
                c.Size=new Size(1280,720);bool seen=false;Exception failure=null;int ticks=0;
                using(var timer=new Timer{Interval=50}) {
                    timer.Tick+=delegate {
                        var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CalibrationAssessmentDialog");
                        if(dialog==null) { if(++ticks<60)return;timer.Stop();failure=new Exception("assessment dialog did not appear");return; }
                        timer.Stop();
                        try { seen=true;var details=(TextBox)dialog.Controls.Find("CalibrationMeasurementEvidence",true)[0];
                            Check(details.ReadOnly&&details.WordWrap&&details.Text.Contains("校准系数"),"explicit assessment opens complete readonly evidence");
                            Check(dialog.Icon!=null,"owned assessment dialog has branding icon");
                        }catch(Exception error){failure=error;}finally{dialog.Close();}
                    };
                    timer.Start();var button=(RectangleF)Field(Get(c,"CompactLayout"),"Assessment");
                    Check((bool)Call(c,"HandleCompactMouseUp",new MouseEventArgs(MouseButtons.Left,1,(int)(button.Left+button.Width/2),(int)(button.Top+button.Height/2),0)),"explicit toolbar action handles its own click");
                }
                if(failure!=null)throw failure;Check(seen,"assessment action opened exactly named dialog");
                Check(samples[400]>.14,"input waveform untouched");
            }
            Console.WriteLine("CALIBRATION_ASSESSMENT_PASS="+checks+"; HARDWARE=False; QA="+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
