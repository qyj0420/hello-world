using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class PressureManualThresholdDragSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Get(object value,string name)
    {var property=value.GetType().GetProperty(name,B);return property!=null?property.GetValue(value,null):value.GetType().GetField(name,B).GetValue(value);}
    static void Field(object value,string name,object content){value.GetType().GetField(name,B).SetValue(value,content);}
    static object Call(object value,string name,params object[] arguments)
    {return value.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length).Invoke(value,arguments);}
    static void Mouse(Control control,string name,MouseButtons button,Point point)
    {Call(control,name,new MouseEventArgs(button,1,point.X,point.Y,0));}
    static int Y(Rectangle plot,double value,double maximum){return plot.Bottom-(int)Math.Round(value/maximum*plot.Height);}
    static double Pc(Rectangle plot,int y,double maximum){return Math.Round(Math.Max(0,Math.Min(1,(plot.Bottom-y)/(double)plot.Height))*maximum,2);}
    static double Value(Control waveform){return (double)Get(waveform,"ManualThresholdGuidePc");}
    static void Synced(Control page,Control waveform,NumericUpDown numeric,double value,string why)
    {Check(Value(waveform)==value&&(double)numeric.Value==value&&(double)Get(page,"ManualAnalysisThresholdPc")==value,why);}
    [STAThread]static int Main(string[] arguments)
    {
        try{
            Application.EnableVisualStyles();string root=Path.GetFullPath(arguments[0]),qa=Path.GetFullPath(arguments[1]);
            if(Directory.Exists(qa))throw new Exception("fresh fixture required");Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var page=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            using(var form=new Form{ClientSize=new Size(900,420),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000)}){
                var waveform=(Control)Get(page,"waveformView");var dashboard=(Control)Get(page,"spectrumDashboardView");
                var manual=(CheckBox)dashboard.Controls.Find("PressureManualThreshold",true).Single();
                var numeric=(NumericUpDown)dashboard.Controls.Find("PressureThresholdPc",true).Single();
                waveform.Parent=null;form.Controls.Add(waveform);waveform.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                Call(waveform,"SetData",new[]{0d,5d,-5d,0d},1000d,new[]{0d,20d,80d,0d},1000d);
                Call(waveform,"SetDischargeScale",true,100d);Call(page,"SetPressureAnalysisThreshold",true,40d,true);
                var voltage=(double[])Get(waveform,"voltage");var discharge=(double[])Get(waveform,"discharge");
                double duration=(double)Get(waveform,"cachedFullDuration"),baseline=(double)Get(waveform,"cachedPdBaseline"),peak=(double)Get(waveform,"cachedPdMaximum");
                Rectangle plot=(Rectangle)Call(waveform,"ManualThresholdPlotBounds");int x=plot.Left+plot.Width/2;
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,40,100)));
                Check((bool)Get(waveform,"manualThresholdDragging")&&waveform.Capture,"left threshold hit captures sole drag gesture");
                int upY=Y(plot,75,100);Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,upY));
                Synced(page,waveform,numeric,Pc(plot,upY,100),"drag up uses pC left axis and synchronizes original numeric/public parameters");
                Check((double)Get(waveform,"DisplayPdMaximum")==100&&(bool)Get(page,"ApplyAnalysisThresholdToAllRounds"),"drag preserves fixed axis and existing apply-all option");
                Mouse(waveform,"OnMouseUp",MouseButtons.Left,new Point(x,upY));
                Check(!(bool)Get(waveform,"manualThresholdDragging")&&!waveform.Capture,"release commits and ends capture");
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,upY));int downY=Y(plot,25,100);
                Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,downY));Mouse(waveform,"OnMouseUp",MouseButtons.Left,new Point(x,downY));
                Synced(page,waveform,numeric,Pc(plot,downY,100),"drag down updates same threshold binding");
                double before=Value(waveform);Mouse(waveform,"OnMouseDown",MouseButtons.Right,new Point(x,downY));
                Mouse(waveform,"OnMouseMove",MouseButtons.Right,new Point(x,plot.Top));Mouse(waveform,"OnMouseUp",MouseButtons.Right,new Point(x,plot.Top));
                Synced(page,waveform,numeric,before,"right click never edits threshold");
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,plot.Top+30));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,plot.Top));
                Mouse(waveform,"OnMouseUp",MouseButtons.Left,new Point(x,plot.Top));Synced(page,waveform,numeric,before,"off-guide gesture never becomes threshold drag");
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(plot.Left-10,downY));
                Check(!(bool)Get(waveform,"manualThresholdDragging"),"axis margin is not a threshold hit");
                manual.Checked=false;Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,downY));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,plot.Top));
                Check(!(bool)Get(waveform,"ManualThresholdGuideEnabled")&&!(bool)Get(waveform,"manualThresholdDragging")&&Value(waveform)==before,"unchecked guide hidden and cannot drag");
                manual.Checked=true;numeric.Value=1200m;Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,plot.Top));
                Check(!(bool)Get(waveform,"manualThresholdDragging")&&(double)Get(waveform,"DisplayPdMaximum")==100&&numeric.Value==1200m,"outside fixed display has no phantom draggable line or axis expansion");
                numeric.Value=40m;Field(page,"postProcessStateCode","SAVE_REQUESTED");
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,40,100)));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,upY));
                Synced(page,waveform,numeric,40,"pending real save prevents threshold edits");Check(!(bool)Get(waveform,"manualThresholdDragging"),"locked threshold hit does not capture");
                Field(page,"postProcessStateCode","");
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,40,100)));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,upY));
                waveform.Capture=false;Application.DoEvents();Synced(page,waveform,numeric,40,"lost capture rolls back uncommitted drag through original binding");
                Check(!(bool)Get(waveform,"manualThresholdDragging"),"capture loss ends gesture");
                Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,40,100)));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,plot.Top-30));
                Synced(page,waveform,numeric,100,"drag above plot clamps to current pC range without expanding it");
                Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,plot.Bottom+30));Synced(page,waveform,numeric,0,"drag below plot clamps at zero pC");
                Mouse(waveform,"OnMouseUp",MouseButtons.Left,new Point(x,plot.Bottom+30));
                Check(Object.ReferenceEquals(voltage,Get(waveform,"voltage"))&&Object.ReferenceEquals(discharge,Get(waveform,"discharge"))&&
                    (double)Get(waveform,"cachedFullDuration")==duration&&(double)Get(waveform,"cachedPdBaseline")==baseline&&
                    (double)Get(waveform,"cachedPdMaximum")==peak&&(double)Get(waveform,"voltageRateHz")==1000&&(double)Get(waveform,"dischargeRateHz")==1000,
                    "all mouse gestures preserve raw samples, rates, time extent and analysis statistics");
                Check(!(bool)Get(page,"ActionPending")&&(long)Get(page,"PressureRequestSequence")==0,"threshold mouse gestures issue no hardware action or pressure request");
                numeric.Value=40m;Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,40,100)));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,upY));
                var message=new Message();Check((bool)Call(waveform,"ProcessCmdKey",message,Keys.Escape),"Escape owns only the active threshold gesture");
                Synced(page,waveform,numeric,40,"Escape rolls back and synchronizes the original parameter");
                Check(!(bool)Get(waveform,"manualThresholdDragging")&&!waveform.Capture,"Escape releases capture without hardware cancellation");
                numeric.Value=40m;Mouse(waveform,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,40,100)));Mouse(waveform,"OnMouseMove",MouseButtons.Left,new Point(x,upY));
                Call(waveform,"ClearData");Synced(page,waveform,numeric,40,"new data lifecycle cancels stale threshold gesture");
                Check(!(bool)Get(waveform,"manualThresholdDragging"),"clear data releases old drag capture");
                form.Close();
            }
            Console.WriteLine("PRESSURE_MANUAL_THRESHOLD_DRAG_PASS="+checks+";HARDWARE_ACCESSED=False;SDK_CALLED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
