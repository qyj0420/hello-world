using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationViewportFeedbackSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Type type;static int checks;
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(owner,args);}
    static object Field(object owner,string name){return owner.GetType().GetField(name,All).GetValue(owner);}
    static void SetField(object owner,string name,object value){owner.GetType().GetField(name,All).SetValue(owner,value);}
    static void Set(object owner,string name,object value){owner.GetType().GetProperty(name,All).SetValue(owner,value,null);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name,All).GetValue(owner,null);}
    static void Check(bool yes,string name){checks++;if(!yes)throw new InvalidOperationException(name);}
    static double[] Window(double start,double end,double dx,double length,double velocity,double incident,double reflection)
    {return (double[])type.GetMethod("CalculateDisplayWindow",All).Invoke(null,new object[]{start,end,dx,length,velocity,incident,reflection});}
    static void Near(double a,double b,string name){Check(Math.Abs(a-b)<1e-12,name);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass release root and QA output directory.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Assembly module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            type=module.GetType("HVX.Controls.CalibrationProgramControl",true);
            double cap=10.0*180/170*1e-6;
            double[] a=Window(0,65e-6,8e-9,180,170,1e-6,3.117647e-6);
            Near(a[1]-a[0],cap,"Five cable distances use distance=v*t/2: 10.588 us, not 65 us");
            Check(a[0]<=1e-6&&a[1]>=3.117647e-6,"Incident and first reflection included");
            double[] delayed=Window(0,65e-6,8e-9,180,170,50e-6,52.117647e-6);
            Near(delayed[1]-delayed[0],cap,"Late trigger offset still capped");
            Check(delayed[0]<=50e-6&&delayed[1]>=52.117647e-6,"Late incident and reflection visible");
            double[] shortData=Window(0,2e-6,8e-9,180,170,1e-6,double.NaN);
            Near(shortData[1],2e-6,"Short capture never extended with invented samples");Check(shortData[3]==3,"Capture too short diagnosed");
            foreach(double invalid in new[]{0d,-1d,double.NaN,double.PositiveInfinity})
            {
                double[] value=Window(0,65e-6,8e-9,invalid,170,1e-6,3e-6);
                Check(value[3]==1&&double.IsPositiveInfinity(value[2]),"Unknown length not fabricated");
                value=Window(0,65e-6,8e-9,180,invalid,1e-6,3e-6);Check(value[3]==1,"Unknown velocity not fabricated");
            }
            Check(Window(0,65e-6,8e-9,180,170,1e-6,20e-6)[3]==2,"Incompatible markers explicit instead of silently expanding beyond cap");
            Check(Window(0,65e-6,8e-9,.0001,170,1e-6,2e-6)[3]==4,"Insufficient sample resolution diagnosed");
            using(Control control=(Control)Activator.CreateInstance(type))
            {
                Set(control,"EmbeddedMode",true);control.Size=new Size(1280,720);
                Call(control,"SetPhaseParameters",0,180d,170d,false,false);Set(control,"SelectedPhaseIndex",0);Set(control,"SelectedLevelCode","TDR");
                double[] samples=Enumerable.Range(0,8126).Select(i=>Pulse(i*8e-9,1e-6,.08e-6,1)+Pulse(i*8e-9,3.117647e-6,.1e-6,.4)+(i>7400?5d:0d)).ToArray();
                Call(control,"SetPhaseWaveform",0,"TDR",samples,0d,8e-9);Call(control,"SetPhaseMarkers",0,"TDR",1e-6,3.117647e-6,3.117647e-6,180d);Call(control,"ResetView");
                Call(control,"SavePng",Path.Combine(output,"calibration-5lv-window.png"),1280,720);
                Near((double)Field(control,"viewX1")-(double)Field(control,"viewX0"),cap,"Rendered viewport capped");
                Check((double)Field(control,"viewY1")<2,"Y range uses visible pulse window, not late distant amplitude");
                object phase=Call(control,"GetPhaseStateNoLock",0);object raw=((IDictionary)Field(phase,"Waveforms"))["TDR"];
                Check(samples.SequenceEqual((double[])Field(raw,"Samples")),"All 8126 original samples preserved");
                object marker=((IDictionary)Field(phase,"Markers"))["TDR"];
                Near((double)Field(marker,"StartTimeSeconds"),1e-6,"Incident cursor unchanged by display window");
                Near((double)Field(marker,"DistanceMarkerSeconds"),3.117647e-6,"Reflection cursor unchanged by display window");
                float hoverX=Convert.ToSingle(Call(control,"TimeToReferenceX",2e-6));
                Call(control,"UpdateHoverMeasurement",new PointF(hoverX,350),phase,raw,marker);
                Check((bool)Get(control,"HoverMeasurementAvailable"),"Hover valid within waveform");
                Check(Math.Abs((double)Get(control,"HoverDeltaSeconds")-1e-6)<1e-10,"Hover delta referenced to incident");
                Check(Math.Abs((double)Get(control,"HoverReflectionDeltaSeconds")+1.117647e-6)<1e-10,"Hover signed delta referenced to reflection");
                Check(Math.Abs((double)Get(control,"HoverDistanceMeters")-85)<.01,"Hover distance still referenced to cable start");
                Call(control,"SavePng",Path.Combine(output,"calibration-hover-two-references.png"),1280,720);
                Call(control,"ClearHoverMeasurement");
                SetField(control,"viewX0",0d);SetField(control,"viewX1",65e-6);Call(control,"LimitDisplayWindow",raw,2e-6);
                Check((double)Field(control,"viewX1")-(double)Field(control,"viewX0")<=cap+1e-12,"Zoom-out cannot exceed 5L/v");
                Call(control,"SetPhaseParameters",0,90d,170d,false,false);Call(control,"SavePng",Path.Combine(output,"calibration-length-changed.png"),1280,720);
                Near((double)Field(control,"viewX1")-(double)Field(control,"viewX0"),cap/2,"Same waveform view updates after phase length change");
                Call(control,"SetPhaseParameters",1,340d,170d,false,false);Call(control,"SetPhaseWaveform",1,"TDR",samples,0d,8e-9);Set(control,"SelectedPhaseIndex",1);
                Call(control,"SavePng",Path.Combine(output,"calibration-phase-b.png"),1280,720);
                Near((double)Field(control,"viewX1")-(double)Field(control,"viewX0"),20e-6,"Phase switch uses its independent geometry");
                foreach(int height in new[]{600,720})
                {
                    control.Size=new Size(1280,height);Set(control,"CalibrateAllLevels",true);SetField(control,"compactLevelsOpen",true);
                    object geometry=Get(control,"CompactLayout");int count=((string[])Call(control,"GetVisibleLevelCodes")).Length;
                    RectangleF previous=RectangleF.Empty;
                    for(int i=0;i<count;i++)
                    {
                        RectangleF row=(RectangleF)Call(geometry,"PopupRow",i,count);
                        if(i>0){Near(row.Left,previous.Left,"Every dropdown row uses same column");Check(row.Top>=previous.Bottom,"Dropdown rows do not overlap");}
                        Check(row.Left>=0&&row.Right<=control.Width&&row.Top>=0&&row.Bottom<=control.Height,"Complete dropdown visible without horizontal scroll");previous=row;
                    }
                    Call(control,"SavePng",Path.Combine(output,"calibration-single-column-levels-"+height+".png"),1280,height);
                }
                SetField(control,"compactLevelsOpen",false);
                object phaseB=Call(control,"GetPhaseStateNoLock",1);SetField(phaseB,"CableLengthMeters",double.NaN);
                Call(control,"ResetView");Call(control,"SavePng",Path.Combine(output,"calibration-unknown-geometry.png"),1280,720);
                Check((string)Get(control,"DisplayWindowStatus")=="UnknownGeometry","Invalid geometry gets explicit UI status");
            }
            Console.WriteLine("CALIBRATION_VIEWPORT_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static double Pulse(double t,double center,double half,double peak){return peak*Math.Max(0,1-Math.Abs(t-center)/half);}
}
