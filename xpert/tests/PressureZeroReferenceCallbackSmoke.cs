using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Synthetic UI/callback regression only. No SDK, driver, device or user project.
internal static class PressureZeroReferenceCallbackSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new InvalidOperationException(text);}
    static object Field(object o,string name){return o.GetType().GetField(name,Flags).GetValue(o);}
    static void SetField(object o,string name,object value){o.GetType().GetField(name,Flags).SetValue(o,value);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,Flags).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,Flags).SetValue(o,value,null);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Listen(object o,string name,EventHandler handler){o.GetType().GetEvent(name,Flags).GetAddMethod(true).Invoke(o,new object[]{handler});}
    static void Pump(int milliseconds){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(5);}}
    static Dictionary<string,object> Metadata(string project,double target)
    {return new Dictionary<string,object>{{"schema","HVX.NoiseReference/1"},{"projectPath",project},{"phaseCode","A"},{"channelId","PD"},{"deviceId","QA_DEVICE_1"},{"sampleRateHz",100000d},{"resolutionBits",16},{"rangeCode",0},{"fullScaleV",1d},{"chargeScalePcPerVolt",1000d},{"calibrationId","QA_V1"},{"sampleUnit","pC"},{"simulation",true},{"generatorZero",target==0},{"sourceId","QA synthetic source"},{"capturedUtc",DateTimeOffset.UtcNow},{"targetVoltageKv",target}};}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"zero-callback");Directory.CreateDirectory(qa);
            var review=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            review.GetType("HVX.PdReview.ReviewAnalysisPreferences").GetField("PathProvider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1256,720),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true))) {
                form.Controls.Add(control);control.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                Set(control,"ProjectPath",qa);Set(control,"SimulationMode",true);Set(control,"SelectedPhaseCode","A");Set(control,"AutomaticPostProcessEnabled",true);
                int saves=0,analyses=0,decisions=0,adviceChanges=0;
                Listen(control,"SaveRequested",(s,e)=>saves++);Listen(control,"AnalysisRequested",(s,e)=>analyses++);Listen(control,"OverRangeDecisionRequested",(s,e)=>decisions++);
                var dashboard=Field(control,"spectrumDashboardView");var spectrum=Field(dashboard,"Review");var advisor=Field(dashboard,"advisor");
                Listen(dashboard,"DisplayRecommendationChanged",(s,e)=>adviceChanges++);
                var matrix=new double[1,36];matrix[0,2]=10;matrix[0,5]=170;matrix[0,13]=25;matrix[0,15]=matrix[0,16]=100;matrix[0,35]=1;
                // Queue valid-data advice, then replace it with a zero-reference
                // before the UI timer executes. It must never apply to zero data.
                Call(dashboard,"LoadMatrix",matrix);Set(dashboard,"StatisticsFilterEnabled",true);Set(spectrum,"ConfidenceClassificationEnabled",true);Set(dashboard,"AutomaticDisplayRecommendation",true);
                Check((bool)Field(advisor,"dirty"),"positive-data advice is queued before replacement");
                var noise=Enumerable.Range(0,1000).Select(i=>2*Math.Sin(i*.2)).ToArray();var original=(double[])noise.Clone();
                Call(control,"SetAcquisitionWaveforms",new double[1000],noise,100000d,Metadata(qa,0));int beforeAdvice=adviceChanges;
                Check((bool)Get(control,"CurrentAcquisitionIsNoiseReference"),"explicit zero context remains noise-only");
                Pump(350);
                Check(!(bool)Field(advisor,"dirty")&&!(bool)Field(advisor,"running")&&!((System.Windows.Forms.Timer)Field(advisor,"timer")).Enabled,"zero data cancels pending recommendation work");
                Check(Field(dashboard,"advice")==null&&adviceChanges==beforeAdvice,"no recommendation result or automatic-change event from zero data");
                Check((bool)Get(dashboard,"StatisticsFilterEnabled")&&(bool)Get(spectrum,"ConfidenceClassificationEnabled"),"zero data preserves chosen filter/classification settings");
                Check(!(bool)Get(control,"DataSaved")&&!(bool)Get(control,"DataAnalyzed"),"display-only zero input does not fabricate persistence or analysis");
                Check(!(bool)Call(control,"ReportAcquisitionCompleted",false)&&!(bool)Call(control,"ReportAcquisitionCompleted",true),"normal and overrange zero callbacks rejected from PD pipeline");
                Check(saves==0&&analyses==0&&decisions==0,"zero acquisition callbacks publish no PD save, analysis or overrange decision");
                // Emulate stale state left by an external/older host. Return
                // values must reject its ACK without rewriting raw-save evidence.
                SetField(control,"dataSaved",true);
                SetField(control,"postProcessStateCode","SAVE_REQUESTED");
                Check(!(bool)Call(control,"CompleteSave",true,"")&&!(bool)Call(control,"CompleteSave",false,"old failure"),"old zero save ACKs cannot start analysis");
                Check((bool)Get(control,"DataSaved")&&!(bool)Get(control,"DataAnalyzed"),"late save ACK does not erase existing raw evidence or invent analysis");
                SetField(control,"postProcessStateCode","ANALYSIS_REQUESTED");
                Check(!(bool)Call(control,"CompleteAnalysis",true,"")&&!(bool)Call(control,"CompleteAnalysis",false,"old failure"),"old zero analysis ACKs cannot mark data analyzed");
                SetField(control,"postProcessStateCode","OVERRANGE_DECISION_REQUIRED");
                Check(!(bool)Call(control,"ResolveOverRangeDecision",true)&&!(bool)Call(control,"ResolveOverRangeDecision",false),"old zero overrange ACKs cannot enter PD saving");
                SetField(control,"postProcessStateCode","COMPLETE");
                Call(control,"SetSpectrumDataMatrix",matrix);Set(dashboard,"AutomaticDisplayRecommendation",true);beforeAdvice=adviceChanges;Pump(300);
                Check((int)Get(dashboard,"DataPointCount")==0&&Field(dashboard,"advice")==null&&adviceChanges==beforeAdvice,"late matrix and auto toggle do not analyze/recommend zero data");
                Check((bool)Get(dashboard,"StatisticsFilterEnabled")&&(bool)Get(spectrum,"ConfidenceClassificationEnabled"),"late zero callbacks preserve display preferences");
                Check(((double[])Call(control,"GetNoiseReferenceSamples")).SequenceEqual(original)&&noise.SequenceEqual(original),"zero comparison samples and input remain intact");
                Check(saves==0&&analyses==0&&decisions==0&&!(bool)Get(control,"DataAnalyzed"),"all legacy callbacks leave zero analysis excluded");
                Check(!Directory.EnumerateFiles(qa,"*.loc",SearchOption.AllDirectories).Any(),"no location file generated for zero reference");
                Check(((Label)Field(dashboard,"adviceLabel")).Text.Contains("零电压"),"zero reference has explicit user-facing no-analysis advice status");
                // A later real nonzero acquisition must still restore analysis
                // display and advice eligibility; no permanent disable latch.
                SetField(control,"postProcessStateCode","READY");
                Call(control,"SetAcquisitionWaveforms",Enumerable.Repeat(10d,1000).ToArray(),noise,100000d,Metadata(qa,10));
                Call(control,"SetSpectrumDataMatrix",matrix);Pump(500);
                Check(!(bool)Get(control,"CurrentAcquisitionIsNoiseReference")&&(int)Get(dashboard,"DataPointCount")==1,"next nonzero matrix is admitted");
                Check(Field(dashboard,"advice")!=null,"next nonzero data can receive recommendation");
                Set(dashboard,"StatisticsFilterEnabled",true);Set(spectrum,"ConfidenceClassificationEnabled",true);Set(dashboard,"AutomaticDisplayRecommendation",true);Call(dashboard,"ClearData");beforeAdvice=adviceChanges;Pump(300);
                Check(Field(dashboard,"advice")==null&&adviceChanges==beforeAdvice&&!(bool)Field(advisor,"dirty"),"empty nonzero display cancels advice too");
                Check((bool)Get(dashboard,"StatisticsFilterEnabled")&&(bool)Get(spectrum,"ConfidenceClassificationEnabled"),"empty display does not overwrite choices");
                var preview=Metadata(qa,10);preview["referencePreview"]=true;Call(control,"SetAcquisitionWaveforms",new double[1000],noise,100000d,preview);
                Check((bool)Get(control,"CurrentAcquisitionIsReferencePreview")&&!(bool)Call(control,"ReportAcquisitionCompleted",false),"reference preview also stays outside PD completion pipeline");
                Check((long)Get(control,"PressureRequestSequence")==0,"offline callback regression never requested pressure");
                form.Close();
            }
            Console.WriteLine("PRESSURE_ZERO_REFERENCE_CALLBACK_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
