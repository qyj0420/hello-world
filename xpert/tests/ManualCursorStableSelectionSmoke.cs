using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class ManualCursorStableSelectionSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
    static void Mouse(object c,string n,MouseButtons button,int x,int y){Call(c,n,new MouseEventArgs(button,1,x,y,0));}
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            var a=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            a.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(output,"prefs.json")));
            using(var form=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000),ClientSize=new Size(1400,900)})
            using(var review=(Control)Activator.CreateInstance(a.GetType("HVX.PdReview.PdReviewControl",true))) {
                review.Dock=DockStyle.Fill;form.Controls.Add(review);form.Show();Application.DoEvents();Set(review,"AutomaticDisplayRecommendation",false);
                var p=new double[3,36];var w=new double[3,4096];
                for(int r=0;r<3;r++) {
                    p[r,0]=1;p[r,1]=1;p[r,2]=13.4;p[r,3]=45;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;p[r,9]=r;
                    p[r,10]=100;p[r,11]=500;p[r,12]=3.2;p[r,13]=300;p[r,14]=609;p[r,15]=p[r,16]=180;p[r,20]=100;p[r,21]=.92;p[r,23]=100;p[r,24]=100000;p[r,32]=4095;p[r,33]=.91;p[r,35]=1;
                    for(int i=0;i<4096;i++)w[r,i]=180*Math.Exp(-Math.Pow((i-100)/8d,2))+60*Math.Exp(-Math.Pow((i-500)/7d,2))+52*Math.Exp(-Math.Pow((i-540)/7d,2));
                }
                Check((bool)Call(review,"LoadDataMatrices",p,w),"synthetic maps load");Set(review,"AutomaticDisplayRecommendation",false);
                var map=Get(review,"PdMap");Set(Get(review,"Spectrum"),"AutomaticConclusions",false);
                Set(review,"DischargePositionFilterM",new[]{290d,310d});Call(review,"SelectSourceRow",0);
                var original=(double[,])Get(review,"OriginalParameterMatrix");int sequence=(int)Get(review,"SelectionSequence");
                foreach(int mode in new[]{1,0,1}) {
                    Set(map,"PositioningMode",mode);Set(map,"ApproximateCablePositions",true);
                    foreach(int target in new[]{518,526,512,537,508,545}) {
                        Call(map,"MoveReflectionToIndex",target);
                        Check((int)Get(map,"CurrentReflectionIndex")==target,"manual low/dual-peak selection is exact in mode "+mode+": "+target);
                        Check((int)Get(review,"SelectedSourceRowIndex")==0&&(int)Get(map,"CurrentParameterRowIndex")==0,"manual edit keeps source row when its result leaves filter");
                        var rows=(double[,])Get(review,"ResultParameterMatrix");Check(rows[0,11]==target&&rows[1,11]==500&&rows[2,11]==500,"only current row receives manual reflection");
                        Check(!(bool)Get(map,"PositionIsApproximated"),"manual point is not moved onto a nearby attachment");
                    }
                }
                Check((int)Get(review,"SelectionSequence")==sequence,"manual edit never emits a selection transition");
                Check(!((int[])Get(review,"FilteredSourceRowIndices")).Contains(0),"edited map is genuinely outside statistics filter but remains available for inspection");
                Check((double[,])Get(review,"OriginalParameterMatrix")!=null&&((double[,])Get(review,"OriginalParameterMatrix"))[0,11]==original[0,11],"immutable original timing retained");
                Call(map,"ResetView");Application.DoEvents();Call(map,"PrepareInteraction");var plot=(Rectangle)Get(map,"lastPlot");
                double x0=(double)Get(map,"viewX0"),x1=(double)Get(map,"viewX1");int startIndex=(int)Get(map,"CurrentReflectionIndex");
                int start=(int)Math.Round(plot.Left+(startIndex-x0)/(x1-x0)*plot.Width),y=plot.Top+plot.Height/2;
                Mouse(map,"OnMouseDown",MouseButtons.Left,start,y);Check((int)Get(map,"draggingCursor")==2,"actual mouse gesture captures reflection");
                int expected=startIndex;
                foreach(int offset in new[]{3,7,-3,9,-1,5}) {
                    expected=(int)Math.Round(startIndex+offset*(x1-x0)/plot.Width);Mouse(map,"OnMouseMove",MouseButtons.Left,start+offset,y);
                    Check((int)Get(map,"CurrentReflectionIndex")==expected&&(int)Get(review,"SelectedSourceRowIndex")==0,"continuous drag tracks each sample without selecting another PDmap");
                }
                Mouse(map,"OnMouseUp",MouseButtons.Left,start+5,y);Check((int)Get(map,"CurrentReflectionIndex")==expected&&(int)Get(review,"SelectedSourceRowIndex")==0,"release does not snap or jump rows");
                Call(review,"RefreshStatisticsAfterEdit");Application.DoEvents();Check((int)Get(review,"SelectedSourceRowIndex")==0,"post-edit refresh retains explicit waveform inspection");
                Check((bool)Call(review,"NextPdMap")&&(int)Get(review,"SelectedSourceRowIndex")==1,"explicit next navigation still works");
            }
            var browser=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000),ClientSize=new Size(1100,650)})
            using(var plot=(Control)Activator.CreateInstance(browser.GetType("CalibrationWaveform.CalibrationWaveformControl",true))) {
                plot.Dock=DockStyle.Fill;host.Controls.Add(plot);host.Show();Application.DoEvents();
                double[] wave=Enumerable.Range(0,1000).Select(i=>Math.Sin(i*.2)).ToArray();Call(plot,"SetWaveform",wave,0d,.01d);Call(plot,"SetCursorPositions",2d,5d);
                var bounds=(Rectangle)Call(plot,"GetPlotRectangle",plot.ClientRectangle);int start=(int)Math.Round(Convert.ToDouble(Call(plot,"XToPixel",5d,bounds))),y=bounds.Top+bounds.Height/2;
                Mouse(plot,"OnMouseDown",MouseButtons.Left,start,y);
                foreach(int offset in new[]{12,-7,25,2,-11}) {
                    double expected=(double)Call(plot,"PixelToX",start+offset,bounds);Mouse(plot,"OnMouseMove",MouseButtons.Left,start+offset,y);
                    Check(Math.Abs((double)Get(plot,"ReflectionCursorX")-expected)<1e-12,"browser calibration drag directly uses pointer time");
                }
                double released=(double)Get(plot,"ReflectionCursorX");Mouse(plot,"OnMouseUp",MouseButtons.Left,start-11,y);Check((double)Get(plot,"ReflectionCursorX")==released,"browser release never performs onset/peak search");
            }
            Console.WriteLine("MANUAL_CURSOR_STABLE_SELECTION_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
