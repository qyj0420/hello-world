using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Offline synthetic acquisition/atomic-save tests; no worker, driver or device.
internal static class PressureZeroVoltageAnalysisSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,Flags).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,Flags).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,Flags).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,Flags).SetValue(o,v);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(Flags).Single(x=>x.Name==n&&x.GetParameters().Length==args.Length).Invoke(o,args);}
    static T Finish<T>(Task<T> task){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.ElapsedMilliseconds<20000){Application.DoEvents();Thread.Sleep(1);}Check(task.IsCompleted,"bounded save completes");return task.GetAwaiter().GetResult();}
    static Dictionary<string,object> Meta(string project,string id){return new Dictionary<string,object>{{"schema","HVX.NoiseReference/1"},{"projectPath",project},{"phaseCode","A"},{"channelId","PD"},{"deviceId","QA-OFFLINE"},{"sampleRateHz",100000d},{"resolutionBits",16},{"rangeCode",5},{"fullScaleV",1d},{"chargeScalePcPerVolt",1000d},{"calibrationId","QA-OFFLINE"},{"sampleUnit","pC"},{"simulation",true},{"generatorZero",true},{"sourceId","SYNTHETIC-ORIGINAL-ZERO"},{"capturedUtc",DateTimeOffset.UtcNow},{"targetVoltageKv",0d},{"locationAcquisitionId",id}};}
    static string Begin(object c,bool enabled){Set(c,"AnalyzeZeroVoltageData",enabled);Field(c,"requestedTargetPeakVoltageKv",0d);Call(c,"BeginLocationCycleAcquisition");return (string)Get(c,"LocationAcquisitionId");}
    static Task<bool> Save(object c,string id,string path,double[] samples,string filter,CancellationToken ct){return (Task<bool>)Call(c,"SaveLocationCycleDataAsync",id,path,samples,100000d,0d,170d,filter,ct);}
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]),project=Path.Combine(Path.GetFullPath(args[1]),"zero-opt-in");Directory.CreateDirectory(project);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            var review=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            review.GetType("HVX.PdReview.ReviewAnalysisPreferences").GetField("PathProvider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(project,"display.json")));
            var module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1200,800),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var c=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.PressureTestControl",true))){
                form.Controls.Add(c);c.Dock=DockStyle.Fill;form.Show();Application.DoEvents();Set(c,"ProjectPath",project);Set(c,"SimulationMode",true);Set(c,"FrequencyReminderEnabled",false);
                Check(!(bool)Get(c,"AnalyzeZeroVoltageData"),"default is noise reference only");
                double[] samples=Enumerable.Range(0,1000).Select(i=>Math.Sin(i*.1)*20).ToArray();var original=(double[])samples.Clone();
                string id=Begin(c,false);Call(c,"SetAcquisitionWaveforms",new double[1000],samples,100000d,Meta(project,id));
                Check((bool)Get(c,"CurrentAcquisitionIsNoiseReference")&&!(bool)Get(c,"CurrentAcquisitionIsZeroVoltageAnalysis"),"default zero source is excluded");
                Check(!(bool)Call(c,"ReportAcquisitionCompleted",false),"default zero callback excluded");
                Set(c,"AnalyzeZeroVoltageData",true);Check((bool)Get(c,"CurrentAcquisitionIsNoiseReference"),"late option change never promotes old data");
                Check(!Finish(Save(c,id,Path.Combine(project,"A_0kV_default.loc"),samples,"",CancellationToken.None)),"old reference not saved as LOC");
                id=Begin(c,true);Call(c,"SetAcquisitionWaveforms",new double[1000],samples,100000d,Meta(project,id));
                Check(!(bool)Get(c,"CurrentAcquisitionIsNoiseReference")&&(bool)Get(c,"CurrentAcquisitionIsZeroVoltageAnalysis"),"new explicit opted-in zero acquisition admitted");
                Check(Field(c,"locationCycleWindow")==null,"no fake crossings or voltage cycle");
                Check(Double.IsNaN((double)Field(c,"oscillationFrequencyHz"))&&Double.IsNaN((double)Field(c,"equivalentCapacitanceUf")),"zero noise is not oscillation/capacitance");
                Call(c,"ApplyRealtimeMetrics",100d,.2d,300d,1d);Check(Double.IsNaN((double)Field(c,"oscillationFrequencyHz")),"later host metrics cannot invent frequency");
                string output=Path.Combine(project,"A_0kV_optin.loc");
                Check(Finish(Save(c,id,output,samples,"",CancellationToken.None)),"opted-in complete segment saved");
                string[] lines=File.ReadAllLines(output);Check(lines.Length==samples.Length+11,"full sample count preserved");
                Check(lines[2]=="Phase\tNaN"&&lines[3]=="Voltage (kV)\t0","unknown phase and true zero voltage retained");
                Check(!lines.Take(11).Any(x=>x.StartsWith("HVX_CycleReference")),"no cycle provenance fabricated");
                var metadata=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(lines[10].Substring("HVX_ZeroVoltageReference\t".Length));
                Check((string)metadata["schema"]=="HVX.ZeroVoltageAnalysis/1"&&(bool)metadata["analysisOptIn"]&&!(bool)metadata["phaseKnown"],"explicit zero-analysis provenance");
                Check((string)metadata["noiseReferenceSourceId"]=="SYNTHETIC-ORIGINAL-ZERO"&&(string)metadata["locationAcquisitionId"]==id&&(bool)metadata["simulation"],"actual source/id/simulation retained");
                Check(!metadata.ContainsKey("pdAcquisitionFilterId"),"absence does not claim high-pass filtering");
                Check(samples.SequenceEqual(original),"raw input preserved");
                Check(Convert.ToInt32(metadata["sourceSampleCount"])==1000&&Convert.ToInt32(metadata["sampleCount"])==1000,"short source is never padded to 20ms");
                for(int i=0;i<samples.Length;i++){
                    long stored;
                    Check(Int64.TryParse(lines[i+11],System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out stored),"stored charge is an integer");
                    Check(stored==checked((long)Math.Round(samples[i]*100000,MidpointRounding.AwayFromZero)),"exact integer scaling rule");
                    Check(Math.Abs(stored/100000d-samples[i])<=.0000051,"stored charge quantization bound");
                }
                Check(!Finish(Save(c,id,output,samples,"",CancellationToken.None)),"never overwrite existing LOC");
                string canceled=Path.Combine(project,"A_0kV_cancel.loc");using(var cancel=new CancellationTokenSource()){cancel.Cancel();Check(!Finish(Save(c,id,canceled,samples,"",cancel.Token)),"canceled save rejected");}Check(!File.Exists(canceled),"canceled save no commit");
                Check(!Finish(Save(c,id,Path.Combine(project,"A_1kV_wrong.loc"),samples,"",CancellationToken.None)),"reject fabricated nonzero name");
                Check(!Finish(Save(c,id,Path.Combine(project,"B_0kV_wrong.loc"),samples,"",CancellationToken.None)),"reject phase mismatch");
                Check(!Finish(Save(c,"stale",Path.Combine(project,"A_0kV_stale.loc"),samples,"",CancellationToken.None)),"stale ID rejected");
                Check(!Finish(Save(c,id,Path.Combine(project,"A_0kV_badfilter.loc"),samples,"invented",CancellationToken.None)),"unknown filter rejected");
                var invalid=(double[])samples.Clone();invalid[20]=Double.NaN;Check(!Finish(Save(c,id,Path.Combine(project,"A_0kV_invalid.loc"),invalid,"",CancellationToken.None)),"invalid sample rejected atomically");
                Check(Directory.GetFiles(project,"*.tmp").Length==0,"temporary files cleaned");
                var longer=Enumerable.Range(0,6000).Select(i=>Math.Sin(i*.031)*13).ToArray();
                var longerOriginal=(double[])longer.Clone();id=Begin(c,true);
                Call(c,"SetAcquisitionWaveforms",new double[6000],longer,100000d,Meta(project,id));
                string limited=Path.Combine(project,"A_0kV_20ms.loc");
                Check(Finish(Save(c,id,limited,longer,"",CancellationToken.None)),"60ms source commits 20ms LOC");
                var limitedLines=File.ReadAllLines(limited);
                Check(limitedLines.Length==2011,"exactly 2000 actual samples stored at 100kHz");
                var limitedMeta=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(limitedLines[10].Substring("HVX_ZeroVoltageReference\t".Length));
                Check(Convert.ToInt32(limitedMeta["sourceSampleCount"])==6000&&Convert.ToInt32(limitedMeta["sampleCount"])==2000,"source and saved lengths both explicit");
                Check(Convert.ToDouble(limitedMeta["savedDurationSeconds"])==.020,"20ms duration metadata");
                for(int i=0;i<2000;i++)Check(Int64.Parse(limitedLines[i+11],System.Globalization.CultureInfo.InvariantCulture)==checked((long)Math.Round(longer[i]*100000,MidpointRounding.AwayFromZero)),"retained prefix exact");
                Check(longer.SequenceEqual(longerOriginal),"long acquisition remains unchanged for noise/reference use");
                id=Begin(c,true);var preview=Meta(project,id);preview["referencePreview"]=true;Call(c,"SetAcquisitionWaveforms",new double[1000],samples,100000d,preview);
                Check(!(bool)Get(c,"CurrentAcquisitionIsZeroVoltageAnalysis"),"unverified reference preview cannot be promoted");
                Check(!(bool)Call(c,"ReportAcquisitionCompleted",false),"preview callback still excluded");
            }
            Console.WriteLine("PASS "+checks+" | Synthetic only; no hardware");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
