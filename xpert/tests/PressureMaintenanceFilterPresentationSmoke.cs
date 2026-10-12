using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// UI-only regression. No RequestPressure, capture delegate, SDK, worker or Main.
// Latest feedback supersedes only the old pending/failed primary-caption rule
// in PressurePresetVoltageDisplaySmoke; its measured/epoch/raw-data tests remain.
internal static class PressureMaintenanceFilterPresentationSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static object Get(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void Set(object value,string name,object content){value.GetType().GetProperty(name,All).SetValue(value,content,null);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Field(object value,string name,object content){value.GetType().GetField(name,All).SetValue(value,content);}
    static object Call(object value,string name,params object[] arguments)
    {return value.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length).Invoke(value,arguments);}
    static IEnumerable<Control> Children(Control value){foreach(Control child in value.Controls){yield return child;foreach(var nested in Children(child))yield return nested;}}
    static Control Named(Control value,string name){return Children(value).Single(c=>c.Name==name);}
    static void Pump(){for(int i=0;i<5;i++)Application.DoEvents();}
    static void State(Control page,string state,string post,double actual)
    {Field(page,"postProcessStateCode",post);Call(page,"SetPressureState",state,actual);Pump();}
    static string Title(Button button){return button.Text.Replace("\r",String.Empty).Split('\n')[0];}
    static Bitmap Paint(Button button)
    {
        var bitmap=new Bitmap(button.Width,button.Height);
        using(var graphics=Graphics.FromImage(bitmap))Call(button,"OnPaint",new PaintEventArgs(graphics,button.ClientRectangle));
        return bitmap;
    }
    static void Filter(Control page,Form form,string qa)
    {
        Set(page,"AutomaticDisplayRecommendation",false);
        var before=(long)Get(page,"PressureRequestSequence");
        var choice=Named(page,"PressureLocationFilterAlgorithm");
        var parameters=Named(page,"PressureLocationFilterBParameters");
        Set(page,"StatisticsLocationFilterAlgorithmId","B");
        var options=Call(page,"GetStatisticsLocationFilterBOptions");
        Set(options,"Strength",41d);Set(options,"ChargeThresholdPc",55d);
        Set(options,"MinimumClusterSize",4);Set(options,"FromM",25);Set(options,"ToM",(int?)300);
        Call(page,"SetStatisticsLocationFilterBOptions",options);
        Set(options,"ChargeThresholdPc",99d);
        var active=Call(page,"GetStatisticsLocationFilterBOptions");
        Check((double)Get(active,"ChargeThresholdPc")==55d&&(double)Get(active,"Strength")==41d&&
            (int)Get(active,"MinimumClusterSize")==4&&(int)Get(active,"FromM")==25&&Convert.ToInt32(Get(active,"ToM"))==300,
            "maintenance API and defensive B parameter copy retained");
        foreach(int language in Enumerable.Range(0,19))
        foreach(var size in new[]{new Size(1120,416),new Size(1256,720)})
        {
            form.ClientSize=size;Set(page,"LanguageIndex",language);Pump();
            var tab=Named(page,"CompactSpectrumTab") as Button;if(tab.Visible)tab.PerformClick();Pump();
            Check(!choice.Visible&&!parameters.Visible&&!choice.CanSelect&&!parameters.CanSelect,"maintenance-only controls hidden and not focusable "+language+" "+size);
            Check((string)Get(page,"StatisticsLocationFilterAlgorithmId")=="B"&&(double)Get(Call(page,"GetStatisticsLocationFilterBOptions"),"ChargeThresholdPc")==55d,
                "language/resize cannot reset maintenance algorithm or parameters");
            var bar=Named(page,"PressureStatisticsFilterBar");
            foreach(Control child in bar.Controls)Check(bar.ClientRectangle.Contains(child.Bounds),"complete filter strip bounds "+child.Name);
            var chart=Children(page).Single(c=>c.GetType().Name=="UnifiedSpectrumControl");
            Check(chart.Parent.ClientRectangle.Contains(chart.Bounds)&&chart.Bottom<=bar.Top,"local row removal leaves chart separate from filter");
        }
        Set(page,"LanguageIndex",1);form.ClientSize=new Size(1256,720);Pump();
        var dashboard=Field(page,"spectrumDashboardView");Call(dashboard,"UpdateLocationFilterNotice",2,null);Pump();
        var notice=Named(page,"PressureLocationFilterNotice");var filter=Named(page,"PressureStatisticsFilterBar");
        Check(notice.Visible&&notice.Text.Contains("retained")&&filter.ClientRectangle.Contains(notice.Bounds),"boundary diagnostics remain visible, not hidden with settings");
        Set(page,"StatisticsLocationFilterAlgorithmId","A");
        Check((string)Get(page,"StatisticsLocationFilterAlgorithmId")=="A"&&!choice.Visible&&!parameters.Visible,"maintenance may select A without exposing a page editor");
        Check((long)Get(page,"PressureRequestSequence")==before,"display settings do not issue pressure requests");
        using(var image=new Bitmap(page.Width,page.Height)){page.DrawToBitmap(image,new Rectangle(Point.Empty,page.Size));image.Save(Path.Combine(qa,"maintenance-filter-hidden.png"));}
    }
    static void Presentation(Control page,string qa)
    {
        Set(page,"LanguageIndex",1);Set(page,"SimulationMode",true);Set(page,"FrequencyReminderEnabled",false);
        Call(page,"SetPressureProgramMode","CUSTOM");Call(page,"SetAdvancedMode",true);
        Call(page,"SetCustomVoltageMode","PEAK");Call(page,"SetCustomVoltageValue",10d);
        long sequence=41;Field(page,"pressureRequestSequence",sequence);Field(page,"requestedTargetPeakVoltageKv",10d);
        var rawVoltage=new[]{0d,1d,-1d};var rawCharge=new[]{3d,2d,1d};
        Field(page,"lastVoltageKv",rawVoltage);Field(page,"lastDischargePc",rawCharge);
        var button=(Button)Field(page,"pressureActionButton");
        var status=(Control)Field(page,"taskInformationLabel");
        int pressures=0,saves=0;
        page.GetType().GetEvent("PressureRequested").AddEventHandler(page,(EventHandler)((s,e)=>pressures++));
        page.GetType().GetEvent("SaveRequested").AddEventHandler(page,(EventHandler)((s,e)=>saves++));
        State(page,"RUNNING","WAITING_FOR_ACQUISITION",Double.NaN);
        Call(page,"ReportPressureVoltageProgress",sequence,4d,"DIV offline");
        Check(Title(button)=="Pressurizing"&&!((bool)Get(button,"Danger")),"running retains normal presentation");
        Check((double)Get(page,"CurrentPressureFeedbackKv")==4d&&Math.Abs((double)Get(button,"MeasurementProgress")-.4)<1e-12,"measured fill still uses actual feedback");
        Call(page,"ReportPressureVoltageProgress",sequence-1,9d,"old request");
        Check((double)Get(page,"CurrentPressureFeedbackKv")==4d,"stale feedback remains rejected");
        Field(page,"pressureVoltageReachedSequence",sequence);
        State(page,"COMPLETE","COMPLETE",9.8d);
        Check(Title(button)=="Pressure complete"&&(bool)Get(button,"Success")&&!(bool)Get(button,"Danger"),"true completed round is successful, never dangerous");
        Check((double)Get(button,"MeasurementProgress")==1d&&(double)Get(page,"ActualPeakVoltageKv")==9.8d,"normal full bar does not rewrite measured peak");
        using(var image=Paint(button)){
            Color fill=image.GetPixel(Math.Min(15,image.Width-1),Math.Min(7,image.Height-1));
            Check(fill.ToArgb()==Color.FromArgb(103,214,230).ToArgb(),"successful measured fill keeps normal cyan, not pink/red");
            image.Save(Path.Combine(qa,"pressure-complete-normal.png"));
        }
        foreach(var item in new[]{"SAVE_REQUESTED|Saving","AWAITING_MANUAL_SAVE|Awaiting save","ANALYSIS_REQUESTED|Analyzing"})
        {
            var parts=item.Split('|');State(page,"COMPLETE",parts[0],9.8d);
            Check(Title(button).StartsWith(parts[1],StringComparison.Ordinal)&&Title(button)!="Pressure complete","pending result has accurate presentation "+parts[0]);
            Check(!(bool)Get(button,"Danger")&&!(bool)Get(button,"Success")&&(string)Get(page,"PostProcessStateCode")==parts[0],"pending result is not a red failure or fabricated success");
        }
        foreach(var item in new[]{"SAVE_FAILED|Save failed","ANALYSIS_FAILED|Analysis failed","ACQUISITION_INVALID|Round incomplete","ACQUISITION_FAILED|Round incomplete","OVERRANGE_REJECTED|Round incomplete"})
        {
            var parts=item.Split('|');Field(page,"lastPostProcessError","SYNTHETIC_FAILURE_DETAIL");State(page,"COMPLETE",parts[0],9.8d);
            Check(Title(button)==parts[1]&&(bool)Get(button,"Danger")&&!(bool)Get(button,"Success"),"real failure stays red and is not labelled completed "+parts[0]);
            Check((string)Get(page,"PostProcessStateCode")==parts[0]&&(double)Get(page,"ActualPeakVoltageKv")==9.8d,"failure presentation preserves workflow and measured result");
            using(var image=Paint(button))Check(image.GetPixel(Math.Min(15,image.Width-1),Math.Min(7,image.Height-1)).ToArgb()==Color.FromArgb(255,209,212).ToArgb(),"actual failure measured fill stays pink/red");
        }
        State(page,"FAULT","READY",9.8d);Check(Title(button)=="Round incomplete"&&(bool)Get(button,"Danger"),"hardware fault cannot be rendered as successful completion");
        State(page,"STOPPED","CANCELED",9.8d);Check(Title(button)=="Start pressure"&&!(bool)Get(button,"Success"),"stopped round remains stopped, not successful");
        State(page,"COMPLETE","COMPLETE",9.8d);
        Check(!(bool)Get(button,"Danger")&&Title(button)=="Pressure complete","new genuine successful UI state does not inherit old red palette");
        Check(Object.ReferenceEquals(rawVoltage,Field(page,"lastVoltageKv"))&&Object.ReferenceEquals(rawCharge,Field(page,"lastDischargePc")),"presentation leaves raw waveform objects unchanged");
        Check((long)Get(page,"PressureRequestSequence")==sequence&&pressures==0&&saves==0,"presentation publishes no request/save or new epoch");
        Check(button.Text.Replace("\r",String.Empty).Split('\n')[1]=="10.0 kV","second line remains selected preset, not measured replacement");
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            if(args.Length!=2)throw new ArgumentException("Exact build root and synthetic QA directory required.");
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var analysis=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"synthetic-review-preferences.json")));
            var pressure=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1256,720),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var page=(Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(page,"EmbeddedMode",true);Set(page,"SimulationMode",true);Set(page,"FrequencyReminderEnabled",false);
                page.Dock=DockStyle.Fill;form.Controls.Add(page);form.Show();Pump();
                Filter(page,form,qa);Presentation(page,qa);form.Close();
            }
            Console.WriteLine("PRESSURE_MAINTENANCE_FILTER_PRESENTATION_PASS="+checks+";Languages=19;HardwareAccessed=False;SDKCalled=False;ProductMainExecuted=False;UserSettingsWritten=False;UserProjectsWritten=False");
            Console.WriteLine("Superseded=only old three-title rule for pending/failed postprocessing; current measured feedback, source epoch, raw data and workflow state remain required.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
