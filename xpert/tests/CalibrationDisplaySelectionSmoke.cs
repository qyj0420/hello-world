using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationDisplaySelectionSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,B).GetValue(o,null);}
    static void Set(object o,string name,object v){o.GetType().GetProperty(name,B).SetValue(o,v,null);}
    static object F(object o,string name){return o.GetType().GetField(name,B).GetValue(o);}
    static void Check(bool pass,string text){checks++;if(!pass)throw new Exception(text);}
    static void Click(Control c,RectangleF r){Call(c,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,(int)(r.Left+r.Width/2),(int)(r.Top+r.Height/2),0));}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Directory.CreateDirectory(args[1]);
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.CalibrationProgramControl",true))) {
                Set(c,"SimulationMode",true);Set(c,"EmbeddedMode",true);c.Size=new Size(1500,950);c.CreateControl();
                var samples=Enumerable.Range(0,4096).Select(i=>Math.Exp(-Math.Pow((i-400)/8d,2))+.4*Math.Exp(-Math.Pow((i-1400)/8d,2))).ToArray();
                Call(c,"SetPhaseParameters",-1,680d,170d,false,true);
                Call(c,"SetPhaseWaveform",-1,"5nC",samples,0d,8e-9);Set(c,"SelectedLevelCode","5nC");
                Call(c,"SetPhaseMarkers",-1,"5nC",400*8e-9,1400*8e-9,1400*8e-9,680d);
                foreach(double d in new[]{110d,112d,114d,116d})Call(c,"AddPhaseJoint",-1,"接头 "+d,d);
                var phase=Call(c,"GetPhaseStateNoLock",-1);var wave=((IDictionary)F(phase,"Waveforms"))["5nC"];var markers=((IDictionary)F(phase,"Markers"))["5nC"];
                string signature=(string)Call(c,"LevelSignature",-1,"5nC");long changes=(long)Get(c,"JointChangedSequence");
                Check(!(bool)Get(c,"JointMarkersVisible"),"default hides joints");
                Set(c,"JointMarkersVisible",true);
                Call(c,"SavePng",Path.Combine(args[1],"joints-visible.png"),1500,950);
                Click(c,(RectangleF)Get(c,"JointVisibilityBounds"));
                Check(!(bool)Get(c,"JointMarkersVisible"),"checkbox hides joints");
                using(var b=new Bitmap(1500,950))using(var g=Graphics.FromImage(b))using(var f=new Font("Microsoft YaHei UI",8.4f)) {
                    var layouts=(IList)Call(c,"GetJointMarkerLayout",g,f,wave,Call(c,"GetPhaseJoints",-1),markers,170d);
                    Check(layouts.Count==0,"hidden lines and labels have no layout/hit regions");
                }
                Check(!(bool)Call(c,"TryShowJointMenu",new PointF(400,400),new Point(400,400)),"hidden joints cannot open delete menu");
                Call(c,"SavePng",Path.Combine(args[1],"joints-hidden.png"),1500,950);
                Check((RectangleF)F(c,"reflectionBadgeHit")!=RectangleF.Empty,"reflection remains visible");
                Check((long)Get(c,"JointChangedSequence")==changes,"display switch does not alter joints");
                Check((string)Call(c,"LevelSignature",-1,"5nC")==signature,"display switch does not dirty calibration");
                Check(samples.SequenceEqual((double[])F(wave,"Samples")),"raw samples unchanged");
                Call(c,"ActivateCalibrationAction","show-joints");
                Check((bool)Get(c,"JointMarkersVisible"),"keyboard action restores joints");
                foreach(int width in new[]{1024,1280,1500,2048}) {
                    c.Size=new Size(width,900);var ui=Get(c,"CompactLayout");var toggle=(RectangleF)Get(c,"JointVisibilityBounds");
                    Check(!toggle.IntersectsWith((RectangleF)F(ui,"QualityTab"))&&!toggle.IntersectsWith((RectangleF)F(ui,"SegmentMode"))&&toggle.Right<=((RectangleF)F(ui,"Center")).Right,"joint toggle fits width "+width);
                    Click(c,(RectangleF)F(ui,"AllLevels"));
                    Check((bool)Get(c,"ShowAllLevels")&&!(bool)Get(c,"CalibrateAllLevels"),"display expansion does not enable batch");
                    foreach(string level in new[]{"5nC","100pC","TDR"}) {
                        Set(c,"SelectedLevelCode",level);Call(c,"ActivateCalibrationAction","start");
                        Check((string)Get(c,"RequestedLevelCode")==level,"start keeps selected level "+level);
                        Check(((string[])Get(c,"RequestedLevelCodes")).SequenceEqual(new[]{level}),"one requested level "+level);
                        Check((bool)Get(c,"ShowAllLevels"),"start does not collapse list");
                    }
                    Set(c,"SelectedLevelCode","100pC");Click(c,(RectangleF)F(ui,"AllLevels"));
                    Check(!(bool)Get(c,"ShowAllLevels")&&(string)Get(c,"SelectedLevelCode")=="100pC","collapse retains selected level");
                    Check(((string[])Call(c,"GetVisibleLevelCodes")).Contains("100pC"),"collapsed list still shows selected extra level");
                }
                Set(c,"SelectedLevelCode","5nC");Set(c,"ShowAllLevels",true);c.Size=new Size(1500,950);
                Call(c,"SavePng",Path.Combine(args[1],"selected-level.png"),1500,950);
                Set(c,"CalibrateAllLevels",true);Check(((string[])Call(c,"GetSelectedCalibrationLevelCodes")).Length>1,"explicit legacy batch API is separate");
                Set(c,"ShowAllLevels",true);Check(!(bool)Get(c,"CalibrateAllLevels"),"UI display operation exits explicit batch mode");
            }
            Console.WriteLine("CALIBRATION_DISPLAY_SELECTION_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
