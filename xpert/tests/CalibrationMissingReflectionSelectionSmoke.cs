using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationMissingReflectionSelectionSmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object New(string n){return Activator.CreateInstance(module.GetType(n,true),true);}
    static object Phase(Control c){return Call(c,"GetPhaseStateNoLock",-1);}
    static object Analysis(Control c){return ((IDictionary)Get(Phase(c),"Analyses"))["100pC"];}
    static object Marker(Control c){return ((IDictionary)Get(Phase(c),"Markers"))["100pC"];}
    static object Selection(Control c){return Call(c,"ReflectionSelectionNoLock",Phase(c),"100pC",true);}
    static void Mouse(Control c,string n,MouseButtons b,int x,int y){Call(c,n,new MouseEventArgs(b,1,x,y,0));}
    static int X(Control c,int index){return (int)Math.Round(Convert.ToDouble(Call(c,"TimeToReferenceX",index*1e-8)));}
    static void Paint(Control c,string file){Call(c,"SavePng",file,1500,950);}
    static object Capture(double[] wave,string[] raw){
        var windows=Enumerable.Range(0,8).Select(i=>(double[])wave.Clone()).ToArray();
        object quality=module.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",true).GetMethod("Analyze").Invoke(null,new object[]{windows,200,336,465,100d,.2,false,"offline",null});
        object c=New("Xiongxin.Calibration.CalibrationCaptureResult");Set(c,"Average",wave);Set(c,"Windows",windows);Set(c,"SampleRateHz",100e6);Set(c,"Assessment",quality);Set(c,"RawPaths",raw);
        Set(c,"DeviceSerial","OFFLINE-ONLY");Set(c,"DeviceModel","synthetic");Set(c,"AcquisitionMode","Pulse");Set(c,"RangeIndex",4);
        object r=New("Xiongxin.Calibration.TdrCorrelationResult");Set(r,"Valid",false);Set(r,"IncidentIndex",400);Set(r,"ReflectionIndex",-1);Set(c,"Reflection",r);return c;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"selection");Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.Combine(root,"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            File.WriteAllText(Path.Combine(qa,"ITEM.in"),"start\tQA\r\n");File.WriteAllText(Path.Combine(qa,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t180m\r\n");
            string[] raw=Enumerable.Range(0,8).Select(i=>{string p=Path.Combine(qa,"raw"+i+".bin");File.WriteAllBytes(p,new byte[]{1,2,3,(byte)i});return p;}).ToArray();
            double[] wave=Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))+.045*Math.Exp(-Math.Pow((i-700)/8d,2))).ToArray();
            int notices=0,qualityPrompts=0;
            using(var host=new Form{ShowInTaskbar=false,Opacity=0,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ClientSize=new Size(1500,950)})
            using(var c=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(c,"EmbeddedMode",true);Set(c,"UseFirstArrivalTiming",false);Set(c,"ProjectPath",qa);Set(c,"SelectedLevelCode","100pC");Call(c,"SetPhaseParameters",-1,180d,180d,false,false);
                Set(c,"ShowReflectionSelectionNoticeForTests",(Action<string>)(m=>{notices++;Check(m.Contains("拖"),"one-time notice offers manual reflection drag");}));
                Set(c,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(m=>{qualityPrompts++;return true;}));
                c.Dock=DockStyle.Fill;host.Controls.Add(c);host.Show();Application.DoEvents();
                var capture=Capture(wave,raw);Call(c,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);Call(c,"ReceiveLiveCapture",-1,"100pC",capture);Paint(c,Path.Combine(qa,"unconfirmed.png"));
                Check(notices==1&&Marker(c)==null&&(int)Get(Analysis(c),"ReflectionIndex")==-1,"pending handle never becomes a measured pair");
                var badge=(RectangleF)Get(c,"pendingReflectionBadgeHit");Check(!badge.IsEmpty,"pending reflection has a visible draggable label");
                Check(!badge.IntersectsWith((RectangleF)Get(c,"measurementInformationHit")),"pending selection label is not hidden behind result card");
                var plot=(RectangleF)Get(c,"CurrentPlotRectangle");int y=(int)(plot.Top+plot.Height*.55),original=(int)Get(Selection(c),"Index");
                Mouse(c,"OnMouseDown",MouseButtons.Left,X(c,original),y);Check((int)Get(c,"draggingCursor")==3&&!(bool)Get(c,"boxZoomActive"),"pending line enters selection drag, not zoom");
                Mouse(c,"OnMouseMove",MouseButtons.Left,X(c,700),y);
                Check(Marker(c)==null&&(int)Get(Analysis(c),"ReflectionIndex")==-1,"drag preview cannot publish a measured reflection");
                Call(c,"ClearCursorSelection");Check((int)Get(Selection(c),"Index")==original&&Marker(c)==null,"cancel restores editing handle without a measurement");
                Mouse(c,"OnMouseDown",MouseButtons.Left,X(c,original),y);Mouse(c,"OnMouseMove",MouseButtons.Left,X(c,710),y);c.Capture=false;
                Check((int)Get(Selection(c),"Index")==original&&Marker(c)==null&&(int)Get(c,"draggingCursor")==0,"capture loss cancels pending selection without committing");
                Paint(c,Path.Combine(qa,"pending.png"));badge=(RectangleF)Get(c,"pendingReflectionBadgeHit");
                int downX=(int)(badge.Left+Math.Min(40,badge.Width/2)),downY=(int)(badge.Top+badge.Height/2);
                Mouse(c,"OnMouseDown",MouseButtons.Left,downX,downY);Check((int)Get(c,"draggingCursor")==3,"pending label itself is draggable");
                // Preserve the pointer's grab offset, just as the real gesture does.
                int destination=X(c,700)+(downX-X(c,original));
                Mouse(c,"OnMouseMove",MouseButtons.Left,destination,downY);Mouse(c,"OnMouseUp",MouseButtons.Left,destination,downY);
                int actual=(int)Get(Analysis(c),"ReflectionIndex");Check(Math.Abs(actual-700)<=2&&Marker(c)!=null,"mouse release selects an actual waveform sample");
                double selectedTime=(double)Get(Marker(c),"DistanceMarkerSeconds");Check(Math.Abs(selectedTime-actual*1e-8)<1e-12,"measured time is selected sample time");
                Check((bool)Get(Selection(c),"Confirmed")&&notices==1,"only committed manual selection is confirmed, without a second prompt");
                Call(c,"SetPhaseBandwidth",-1,true,20d);Check((int)Get(Analysis(c),"ReflectionIndex")==actual&&Math.Abs((double)Get(Marker(c),"DistanceMarkerSeconds")-selectedTime)<1e-12,"same-source bandwidth refresh preserves manual sample and timing");
                Call(c,"SetPhaseBandwidth",-1,false,60d);Call(c,"ReceiveLiveCapture",-1,"100pC",capture);
                Check((int)Get(Analysis(c),"ReflectionIndex")==actual&&notices==1,"duplicate completion cannot erase manual selection or re-prompt");
                Check((bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"manually selected real pair can save: "+Get(c,"LastCalibrationError"));
                object record=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord").GetMethod("Load").Invoke(null,new object[]{qa,Path.Combine(qa,"ALL_100pC.cal"),true});
                Check((string)Get(record,"ReflectionSelectionMethod")=="Manual"&&(string)Get(record,"ReflectionStatus")=="Detected","archive distinguishes actual manual selection");
                Check((int)Get(record,"ReflectionPeakIndex")==actual&&Math.Abs((double)Get(record,"ReflectionSeconds")-selectedTime)<1e-12,"archive uses manual sample, never pending estimate");
                Check(qualityPrompts==0,"good manually selected pulse does not trigger missing-reflection confirmation");
                double[] actualRaw=(double[])Get(((IDictionary)Get(Phase(c),"Waveforms"))["100pC"],"Samples");Check(actualRaw.SequenceEqual(wave),"all edits leave acquired samples unchanged");
                Paint(c,Path.Combine(qa,"selected.png"));
                Call(c,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);Call(c,"ReceiveLiveCapture",-1,"100pC",Capture(wave,raw));
                Check(notices==2&&Marker(c)==null&&!(bool)Get(Selection(c),"Confirmed"),"new capture invalidates old selection and owns one new notice");
                host.Close();
            }
            using(var c=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(c,"UseFirstArrivalTiming",false);Set(c,"ShowReflectionSelectionNoticeForTests",(Action<string>)(m=>{throw new Exception("reload prompt");}));Set(c,"ProjectPath",qa);Set(c,"SelectedLevelCode","100pC");
                Check(Marker(c)!=null&&(bool)Get(Selection(c),"Confirmed"),"saved manual pair reload retains manual selection provenance");
                int before=(int)Get(Analysis(c),"ReflectionIndex");Call(c,"SetPhaseBandwidth",-1,true,20d);
                Check((int)Get(Analysis(c),"ReflectionIndex")==before&&Marker(c)!=null,"reload plus filter refresh preserves selected pair");
            }
            using(var host=new Form{ShowInTaskbar=false,Opacity=0,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ClientSize=new Size(1500,950)})
            using(var c=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(c,"EmbeddedMode",true);Set(c,"UseFirstArrivalTiming",true);Set(c,"ProjectPath",qa);Set(c,"SelectedLevelCode","100pC");Call(c,"SetPhaseParameters",-1,180d,180d,false,false);
                int arrivalNotices=0;Set(c,"ShowReflectionSelectionNoticeForTests",(Action<string>)(m=>{arrivalNotices++;}));c.Dock=DockStyle.Fill;host.Controls.Add(c);host.Show();Application.DoEvents();
                Call(c,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);Call(c,"ReceiveLiveCapture",-1,"100pC",Capture(wave,raw));Paint(c,Path.Combine(qa,"first-arrival-pending.png"));
                int initial=(int)Get(Selection(c),"Index");var plot=(RectangleF)Get(c,"CurrentPlotRectangle");int y=(int)(plot.Top+plot.Height*.55);
                Mouse(c,"OnMouseDown",MouseButtons.Left,X(c,initial),y);Mouse(c,"OnMouseMove",MouseButtons.Left,X(c,692),y);Mouse(c,"OnMouseUp",MouseButtons.Left,X(c,692),y);
                int arrival=(int)Get(Analysis(c),"ReflectionFirstArrivalIndex"),peak=(int)Get(Analysis(c),"ReflectionIndex");double time=(double)Get(Marker(c),"DistanceMarkerSeconds");
                Check(Math.Abs(arrival-692)<=2&&Math.Abs(peak-700)<=2&&peak>arrival,"first-arrival timing stays selected; amplitude independently uses the connected pulse peak");
                Check(Math.Abs(time-arrival*1e-8)<1e-12,"arrival marker reports actual selected sample time");
                Call(c,"SetPhaseBandwidth",-1,true,20d);
                Check((int)Get(Analysis(c),"ReflectionFirstArrivalIndex")==arrival&&(int)Get(Analysis(c),"ReflectionIndex")==peak&&Math.Abs((double)Get(Marker(c),"DistanceMarkerSeconds")-time)<1e-12,"same-source exact selected sample survives filter refresh");
                Set(c,"UseFirstArrivalTiming",false);Check((int)Get(Analysis(c),"ReflectionIndex")==peak&&Math.Abs((double)Get(Marker(c),"DistanceMarkerSeconds")-time)<1e-12,"turning first-arrival off does not relocate manual selection");
                Set(c,"UseFirstArrivalTiming",true);Check((int)Get(Analysis(c),"ReflectionIndex")==peak&&Math.Abs((double)Get(Marker(c),"DistanceMarkerSeconds")-time)<1e-12,"turning first-arrival on does not search a new onset");
                foreach(int target in new[]{696,685,688,692}) {
                    Call(c,"StoreManualCursorAnalysis",-1,"100pC",target,false);
                    Check(Math.Abs((int)Get(Analysis(c),"ReflectionIndex")-700)<=2&&(int)Get(Analysis(c),"ReflectionFirstArrivalIndex")==target&&Math.Abs((double)Get(Marker(c),"DistanceMarkerSeconds")-target*1e-8)<1e-12,"repeated onset edits retain peak amplitude and current timing, not the older cache: "+target);
                    double start=(double)Get(Marker(c),"StartTimeSeconds"),distance=(double)Get(Marker(c),"DistanceMeters");
                    Check(Math.Abs(distance-(target*1e-8-start)*180e6/2)<1e-7,"distance uses exact selected time and current velocity");
                }
                Check(arrivalNotices==1,"manual selection and filter refresh do not prompt again");
                Call(c,"ResetReflectionToInitial");Check(!(bool)Get(Selection(c),"Confirmed")&&arrivalNotices==1,"explicit reset clears manual override without re-prompt");
                host.Close();
            }
            string zero=Path.Combine(qa,"zero-point");Directory.CreateDirectory(zero);File.Copy(Path.Combine(qa,"ITEM.in"),Path.Combine(zero,"ITEM.in"));File.Copy(Path.Combine(qa,"CABLE.in"),Path.Combine(zero,"CABLE.in"));
            var zeroRaw=Enumerable.Range(0,8).Select(i=>{string p=Path.Combine(zero,"raw"+i+".bin");File.WriteAllBytes(p,new byte[]{1,2,3,(byte)i});return p;}).ToArray();
            double[] zeroWave=(double[])wave.Clone();zeroWave[695]=0;
            using(var c=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(c,"ProjectPath",zero);Set(c,"SelectedLevelCode","100pC");Set(c,"ShowReflectionSelectionNoticeForTests",(Action<string>)(m=>{}));Set(c,"ConfirmQualityWarningSaveForTests",(Func<string,bool>)(m=>true));
                Call(c,"SetPhaseParameters",-1,180d,180d,false,false);Call(c,"SetPhaseWaveform",-1,"100pC",zeroWave,0d,1e-8);Call(c,"ReceiveLiveCapture",-1,"100pC",Capture(zeroWave,zeroRaw));
                Call(c,"StoreManualCursorAnalysis",-1,"100pC",695,false);Check((int)Get(Analysis(c),"ReflectionIndex")==695,"zero-amplitude sample is not snapped elsewhere");
                Check((bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"coefficient can save while preserving manual zero-point: "+Get(c,"LastCalibrationError"));
            }
            using(var c=(Control)New("HVX.Controls.CalibrationProgramControl")) {
                Set(c,"ProjectPath",zero);Set(c,"SelectedLevelCode","100pC");
                Check(Marker(c)!=null&&(bool)Get(Selection(c),"Confirmed")&&(int)Get(Analysis(c),"ReflectionIndex")==695,"save/reload retains exact manual low point rather than redetecting a nearby peak");
                Check(Math.Abs((double)Get(Marker(c),"DistanceMarkerSeconds")-695e-8)<1e-12,"reloaded manual coordinate is unchanged");
            }
            Console.WriteLine("CALIBRATION_MISSING_REFLECTION_SELECTION_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
