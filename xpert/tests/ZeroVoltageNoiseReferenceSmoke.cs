using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Noise;

internal static class ZeroVoltageNoiseReferenceSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods().Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static IEnumerable<Control> All(Control c){foreach(Control child in c.Controls){yield return child;foreach(var item in All(child))yield return item;}}
    static Dictionary<string,object> Metadata(string project,bool simulation=true)
    {return new Dictionary<string,object>{{"schema","HVX.NoiseReference/1"},{"projectPath",project},{"phaseCode","A"},{"channelId","PD"},{"deviceId","QA_DEVICE_1"},{"sampleRateHz",100000d},{"resolutionBits",16},{"rangeCode",0},{"fullScaleV",1d},{"chargeScalePcPerVolt",1000d},{"calibrationId","QA_V1"},{"sampleUnit","pC"},{"simulation",simulation},{"generatorZero",simulation},{"sourceId","QA zero source"},{"capturedUtc",DateTimeOffset.UtcNow},{"targetVoltageKv",0d}};}
    static void Throws(Action action,string message){try{action();throw new InvalidOperationException("Expected rejection: "+message);}catch(ArgumentException){checks++;}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string release=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"noise-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string project=Path.Combine(qa,"Project");Directory.CreateDirectory(project);File.WriteAllText(Path.Combine(project,"ITEM.in"),"start\tQA\r\n");File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t终端\t\t170\r\n");
            string zeroFile=Path.Combine(project,"A_0.00kV_1.loc");File.WriteAllBytes(zeroFile,new byte[256]);
            var samples=new[]{9d,11d,9d,11d};var meta=Metadata(project);
            var reference=new ZeroVoltageNoiseReference(samples,NoiseAcquisitionContext.Read(meta));Check(reference.State=="Simulation","explicit generator zero is SIM reference only");
            Check(reference.Mean==10&&reference.Rms==1&&reference.PeakToPeak==2,"AC RMS removes DC and is not total RMS");
            samples[0]=999;Check(reference.CopySamples()[0]==9,"input arrays cloned");var clone=reference.CopySamples();clone[0]=777;Check(reference.CopySamples()[0]==9,"returned arrays cannot mutate reference");
            Check(!(bool)reference.Summary()["automaticFilteringAllowed"]&&!(bool)reference.Summary()["includedInDischargeStatistics"],"reference never claims filtering or discharge status");
            var live=Metadata(project,false);reference=new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(live));Check(reference.State=="Pending","missing real measurement and trusted zero band stays pending");
            live["measuredVoltageKv"]=0d;Check(new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(live)).State=="Pending","zero number alone does not imply trusted actual return to zero");
            live["zeroMinimumKv"]=-.001;live["zeroMaximumKv"]=.001;live["voltageEvidenceVerified"]=true;live["voltageEvidenceSource"]="QA trusted meter specification";
            Check(new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(live)).State=="Verified","only explicit trustworthy zero-band evidence verifies live reference");
            live["measuredVoltageKv"]=.1;Check(new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(live)).State=="Rejected","outside specified band rejected without clamping");
            live["measuredVoltageKv"]=Double.NaN;Check(new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(live)).State=="Pending","NaN actual remains unknown");
            foreach(var changed in new Dictionary<string,object>{{"phaseCode","B"},{"channelId","OTHER"},{"deviceId","QA_DEVICE_2"},{"sampleRateHz",200000d},{"resolutionBits",15},{"rangeCode",1},{"fullScaleV",2d},{"chargeScalePcPerVolt",900d},{"calibrationId","QA_V2"},{"simulation",false},{"projectPath",Path.Combine(qa,"Other")}})
            {var other=new Dictionary<string,object>(meta);other[changed.Key]=changed.Value;Check(!NoiseAcquisitionContext.Read(meta).Comparable(NoiseAcquisitionContext.Read(other)),"incompatible reference blocked: "+changed.Key);}
            Throws(()=>new ZeroVoltageNoiseReference(new double[0],NoiseAcquisitionContext.Read(meta)),"no acquisition");Throws(()=>new ZeroVoltageNoiseReference(new[]{1d,Double.NaN},NoiseAcquisitionContext.Read(meta)),"NaN sample");
            var positive=new Dictionary<string,object>(meta);positive["targetVoltageKv"]=10d;Throws(()=>new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(positive)),"nonzero target");
            var missingScale=new Dictionary<string,object>(meta);missingScale.Remove("chargeScalePcPerVolt");Throws(()=>NoiseAcquisitionContext.Read(missingScale),"unknown pC/V is not comparable");
            var store=new NoiseReferenceStore();reference=new ZeroVoltageNoiseReference(new[]{1d,2d},NoiseAcquisitionContext.Read(meta));store.Add(reference);Check(store.Find(NoiseAcquisitionContext.Read(positive))==reference,"nonzero acquisition can look up matching reference for comparison");
            var otherPhase=new Dictionary<string,object>(positive);otherPhase["phaseCode"]="B";Check(store.Find(NoiseAcquisitionContext.Read(otherPhase))==null,"store does not cross phase");
            var reviewAssembly=Assembly.LoadFrom(Path.Combine(release,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            reviewAssembly.GetType("HVX.PdReview.ReviewAnalysisPreferences").GetField("PathProvider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            var pressureAssembly=Assembly.LoadFrom(Path.Combine(release,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1256,620),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var pressure=(Control)Activator.CreateInstance(pressureAssembly.GetType("HVX.Controls.PressureTestControl")))
            {
                form.Controls.Add(pressure);pressure.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                Set(pressure,"ProjectPath",project);Set(pressure,"SimulationMode",true);Set(pressure,"SelectedPhaseCode","A");
                var volts=new double[1000];var noise=Enumerable.Range(0,1000).Select(i=>2*Math.Sin(i*.2)).ToArray();var saved=(double[])noise.Clone();
                Call(pressure,"SetAcquisitionWaveforms",volts,noise,100000d,meta);Check((bool)Get(pressure,"CurrentAcquisitionIsNoiseReference")&&(string)Get(pressure,"NoiseReferenceStateCode")=="Simulation","pressure registers zero context before statistics");
                Call(pressure,"ApplyRealtimeMetrics",300d,.2,100d,1d);Check(Double.IsNaN((double)Get(pressure,"PartialDischargeMaximumPc")),"zero noise does not become PD maximum from host metric update");
                Call(pressure,"SetLocationData",new[]{25d},new[]{100d},170d,90d);var matrix=new double[1,36];matrix[0,2]=10;matrix[0,5]=170;matrix[0,13]=25;matrix[0,15]=matrix[0,16]=100;matrix[0,35]=1;
                Call(pressure,"SetSpectrumDataMatrix",matrix);object dashboard=pressure.GetType().GetField("spectrumDashboardView",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pressure);Check((int)Get(dashboard,"DataPointCount")==0,"zero reference excluded from all embedded statistics modes");
                Check(All(pressure).Single(c=>c.Name=="NoiseReferenceOnlyMessage").Visible,"zero acquisition shows neutral reference status, not a no-discharge diagnosis");
                Check(noise.SequenceEqual(saved)&&matrix[0,15]==100,"raw waveform and caller matrix not modified");
                foreach(int language in new[]{0,1,18}){Set(pressure,"LanguageIndex",language);Application.DoEvents();var panel=All(pressure).Single(c=>c.Name=="NoiseReferencePanel");Check(panel.Visible&&panel.Parent.ClientRectangle.Contains(panel.Bounds),"embedded reference visible within statistics workspace");foreach(Button b in All(panel).OfType<Button>())Check(b.Width>=TextRenderer.MeasureText(b.Text,b.Font,Size.Empty,TextFormatFlags.NoPadding).Width+12,"noise reference button readable");using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(qa,"pressure-noise-"+language+".png"));}}
                Call(pressure,"SetAcquisitionWaveforms",Enumerable.Repeat(10d,1000).ToArray(),noise,100000d,positive);Check(!(bool)Get(pressure,"CurrentAcquisitionIsNoiseReference")&&(string)Get(pressure,"NoiseReferenceStateCode")=="Simulation","next nonzero compatible acquisition uses reference only for comparison");
                Call(pressure,"SetSpectrumDataMatrix",matrix);Check((int)Get(dashboard,"DataPointCount")==1,"nonzero pulses not suppressed by noise reference");
                Check((bool)Call(pressure,"RegisterZeroVoltageNoiseReference",noise,meta)&&!(bool)Get(pressure,"CurrentAcquisitionIsNoiseReference")&&(int)Get(dashboard,"DataPointCount")==1,"registering a prior reference does not reclassify or erase current PD analysis");
                Call(pressure,"SetWaveforms",volts,noise,100000d);Check(!(bool)Get(pressure,"CurrentAcquisitionIsNoiseReference"),"legacy missing context is not inferred as zero acquisition");
                var badRate=new Dictionary<string,object>(meta);badRate["sampleRateHz"]=200000d;Call(pressure,"SetAcquisitionWaveforms",volts,noise,100000d,badRate);Check((string)Get(pressure,"NoiseReferenceStateCode")=="Pending","mismatched acquisition metadata cannot be applied");
                Set(pressure,"SelectedPhaseCode","B");Check((bool)Get(pressure,"CurrentAcquisitionIsNoiseReference")&&(string)Get(pressure,"NoiseReferenceStateCode")=="None","phase switch hides reference but preserves old acquisition exclusion");
                Call(pressure,"ApplyRealtimeMetrics",300d,.2,100d,1d);Call(pressure,"SetSpectrumDataMatrix",matrix);Check(Double.IsNaN((double)Get(pressure,"PartialDischargeMaximumPc"))&&(int)Get(dashboard,"DataPointCount")==0,"late zero-acquisition callbacks remain excluded after phase switch");
                Set(pressure,"SelectedPhaseCode","A");Call(pressure,"SetAcquisitionWaveforms",volts,noise,100000d,meta);Set(pressure,"SimulationMode",false);Check((string)Get(pressure,"NoiseReferenceStateCode")=="None","leaving simulation immediately clears visible SIM reference");
                Set(pressure,"SimulationMode",true);Call(pressure,"SetDischargeCalibration",1000d,"QA calibration",DateTimeOffset.UtcNow,"A",true);Call(pressure,"SetAcquisitionWaveforms",volts,noise,100000d,meta);Call(pressure,"SetSelectedRange","PICO:1");Check((string)Get(pressure,"NoiseReferenceStateCode")=="None","range switch immediately invalidates reference view");
                Call(pressure,"SetAcquisitionWaveforms",volts,noise,100000d,meta);Call(pressure,"SetDischargeCalibration",900d,"QA second calibration",DateTimeOffset.UtcNow,"A",true);Check((string)Get(pressure,"NoiseReferenceStateCode")=="None","recalibration immediately invalidates reference view");
                Set(pressure,"ProjectPath",Path.Combine(qa,"Other"));Check(((double[])pressure.GetType().GetField("lastDischargePc",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pressure)).Length==0&&(int)Get(dashboard,"DataPointCount")==0,"project switch clears previous raw acquisition and statistics");
            }
            using(var review=(Control)Activator.CreateInstance(reviewAssembly.GetType("HVX.PdReview.PdReviewControl")))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);meta["sourceId"]=zeroFile;
                Check((bool)Call(review,"RegisterZeroVoltageNoiseReference",new[]{1d,2d,1d,2d},meta),"analysis accepts explicit context-bound reference");
                Check((int)Get(review,"SourceRowCount")==0&&!(bool)Get(review,"HasUnsavedAnalysisChanges"),"comparison reference never becomes analysis rows or a diagnostic result");
                var tree=All(review).OfType<TreeView>().Single();Check(tree.Nodes.Cast<TreeNode>().SelectMany(Flatten).Any(n=>n.Text.Contains("仿真参考")||n.Text.Contains("SIM noise")),"zero tree entry explicitly shows simulation reference");
                Check((bool)Call(review,"SetNoiseReferenceComparisonContext",positive),"analysis comparison context finds same compatible reference");
                Check(!(bool)Call(review,"SetNoiseReferenceComparisonContext",otherPhase),"analysis never cross-applies phase reference");
                string other=Path.Combine(qa,"Other");Directory.CreateDirectory(other);Set(review,"ProjectPath",other);Check(((double[])Call(review,"GetNoiseReferenceSamples")).Length==0,"changing project clears noise reference context");
            }
            Console.WriteLine("ZERO_VOLTAGE_NOISE_REFERENCE_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static IEnumerable<TreeNode> Flatten(TreeNode node){yield return node;foreach(TreeNode child in node.Nodes)foreach(var n in Flatten(child))yield return n;}
}
