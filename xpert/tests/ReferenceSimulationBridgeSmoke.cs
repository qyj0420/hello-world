using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ReferenceSimulationBridgeSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static object Get(object x,string key){return x.GetType().GetProperty(key).GetValue(x,null);}
    static void Set(object x,string key,object value){x.GetType().GetProperty(key).SetValue(x,value,null);}
    static object Call(object x,string name,params object[] values){return x.GetType().GetMethods().First(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(x,values);}
    static object Field(object x,string key){return x.GetType().GetField(key,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(x);}
    static void Action(OfflineSimulationBridge bridge,string name,Control c,Dictionary<string,object> p)
    {typeof(OfflineSimulationBridge).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bridge,new object[]{c,p});}
    static Dictionary<string,object> Pressure(double voltage,string phase="A")
    {return new Dictionary<string,object>{{"targetPeakVoltageKv",voltage},{"phaseCode",phase},{"positivePolarity",true},{"cableLengthMeters",400.0}};}
    static Dictionary<string,object> Calibration(double length=400,double velocity=170,string level="5nC")
    {return new Dictionary<string,object>{{"cableLengthMeters",length},{"velocityMPerUs",velocity},{"levelCode",level},{"phaseIndex",0}};}
    static bool Fails(System.Action action){try{action();return false;}catch{return true;}}
    static bool Near(double a,double b){return Math.Abs(a-b)<=Math.Max(1e-15,Math.Abs(b)*1e-10);}

    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string release=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(qa.IndexOf("\\build\\",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidOperationException("QA must be under build.");
            Directory.CreateDirectory(qa);
            string source=Path.Combine(release,ReferenceSimulationProfile.RelativeAssetPath.Replace('/',Path.DirectorySeparatorChar));
            string local=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,ReferenceSimulationProfile.RelativeAssetPath.Replace('/',Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            if(!String.Equals(source,local,StringComparison.OrdinalIgnoreCase))File.Copy(source,local,true);
            var profile=ReferenceSimulationProfile.LoadDefault();
            using(var fake=new PreviewControl())using(var bridge=new OfflineSimulationBridge(true))
            {
                bridge.Start(fake,"PressureTest");Check(!bridge.ReferenceProfileEnabled,"reference source opt-in only");
                int changed=0;bridge.DescriptionChanged+=(s,e)=>changed++;
                fake.IsBusy=true;Check(Fails(()=>bridge.ReferenceProfileEnabled=true),"busy source switch blocked");fake.IsBusy=false;
                fake.ActionPending=true;Check(Fails(()=>bridge.ReferenceProfileEnabled=true),"pending source switch blocked");fake.ActionPending=false;
                bridge.ReferenceProfileEnabled=true;Check(changed==1&&fake.ClearCount==1,"successful source change clears old display and announces provenance");
                var cached=(ReferenceSimulationProfile)Field(bridge,"referenceProfile");
                var donor=cached.PressureRecords.First(x=>x.Phase=="A"&&Near(x.NominalVoltageKv,12.3));
                double[] originalV=(double[])donor.VoltageKv.Clone(),originalQ=(double[])donor.DischargePc.Clone();
                Action(bridge,"InstallReferencePressure",fake,Pressure(12));
                Check(fake.V.Length==Math.Min(donor.VoltageKv.Length,donor.DischargePc.Length)&&fake.Q.Length==fake.V.Length,"only paired common samples displayed");
                Check(fake.Q.SequenceEqual(originalQ.Take(fake.Q.Length)),"original charge pC retained without voltage scaling");
                Check(Near(fake.V.Max(x=>Math.Abs(x)),12),"voltage peak adapted to requested target");
                Check(Near(fake.Rate,donor.SampleRateHz),"estimated source time axis retained");
                Check(fake.Metadata.ContainsKey("referencePreview")&&!fake.Metadata.ContainsKey("deviceId")&&!fake.Metadata.ContainsKey("resolutionBits")&&!fake.Metadata.ContainsKey("calibrationId"),"missing ADC and calibration evidence not fabricated");
                Check(fake.Matrix.GetLength(0)==0&&!fake.Saved&&!fake.Analyzed&&Double.IsNaN(fake.Actual),"no fake position, persistence, completed analysis or measured voltage");
                Check(bridge.SourceDescription.Contains("估算")&&bridge.SourceDescription.Contains("电荷")&&!bridge.SourceDescription.Contains(":\\"),"visible transformation limits without private absolute paths");
                var p=Pressure(10);p["positivePolarity"]=false;Action(bridge,"InstallReferencePressure",fake,p);
                Check(Near(fake.V.Min(),-10)&&fake.Q.SequenceEqual(originalQ.Take(fake.Q.Length)),"polarity adaptation does not alter charge");
                Action(bridge,"InstallReferencePressure",fake,Pressure(20,"B"));
                var b=profile.PressureRecords.First(x=>x.Phase=="B"&&Near(x.NominalVoltageKv,20.91));
                Check(fake.Q.SequenceEqual(b.DischargePc.Take(fake.Q.Length)),"same-phase source selected");
                Action(bridge,"InstallReferencePressure",fake,Pressure(0,"C"));
                var zero=profile.PressureRecords.First(x=>x.Phase=="C"&&x.NominalVoltageKv==0);
                Check(fake.V.SequenceEqual(zero.VoltageKv)&&fake.Q.SequenceEqual(zero.DischargePc.Take(fake.Q.Length)),"zero source residual voltage and charge preserved");
                Check(!(fake.Metadata.ContainsKey("generatorZero")),"historical zero is not trusted generator zero");
                Check(Fails(()=>Action(bridge,"InstallReferencePressure",fake,Pressure(12,"ALL"))),"unavailable phase not silently substituted");
                Check(Fails(()=>Action(bridge,"InstallReferencePressure",fake,Pressure(Double.NaN))),"invalid target rejected");
                Check(donor.VoltageKv.SequenceEqual(originalV)&&donor.DischargePc.SequenceEqual(originalQ),"source profile buffers not mutated");
                fake.SimulationMode=false;Check(Fails(()=>Action(bridge,"InstallReferencePressure",fake,Pressure(12))),"reference data cannot enter live control");fake.SimulationMode=true;
                bridge.ReferenceProfileEnabled=false;Check(fake.ClearCount==2&&!bridge.ReferenceProfileEnabled,"switch back clears reference data");
            }
            using(var bridge=new OfflineSimulationBridge(false))Check(Fails(()=>bridge.ReferenceProfileEnabled=true),"real bridge cannot enable reference mode");
            using(var fake=new PreviewControl())using(var bridge=new OfflineSimulationBridge(true))
            {
                bridge.Start(fake,"CalibrationTest");fake.ClearAllowed=false;
                Check(Fails(()=>bridge.ReferenceProfileEnabled=true)&&!bridge.ReferenceProfileEnabled,"calibration clear failure preserves source selection");
                fake.ClearAllowed=true;bridge.ReferenceProfileEnabled=true;
                var donor=((ReferenceSimulationProfile)Field(bridge,"referenceProfile")).Calibrations.First(x=>x.LevelCode=="10nC");var original=(double[])donor.VoltageSamples.Clone();
                Action(bridge,"InstallReferenceCalibration",fake,Calibration());
                var wave=fake.Waves["5nC"];
                Check(wave.Item1.SequenceEqual(donor.VoltageSamples.Select(x=>x*.5)),"charge-selected donor amplitude scales on copy");
                double scale=(2*400/(170*1e6))/(donor.SourceReflectionSeconds-donor.SourceIncidentSeconds);
                Check(Near(wave.Item2,scale/donor.SampleRateHz),"time axis adapts to current length and velocity");
                Check(fake.Noise.Length==0,"no invented reference background recording");
                Check(donor.VoltageSamples.SequenceEqual(original),"calibration donor immutable");
                Check(bridge.SourceDescription.Contains("ALL")&&bridge.SourceDescription.Contains("历史标记"),"ALL donor and old timing markers disclosed");
                Check(fake.SimulationDataOrigin.StartsWith("SIM:")&&fake.SimulationDataOrigin.Contains("REFERENCE[rdfz-jmf-v1]"),"calibration origin recorded before waveforms");
                int prior=fake.Waves.Count;Check(Fails(()=>Action(bridge,"InstallReferenceCalibration",fake,Calibration(400,170,"invalid")))&&prior==fake.Waves.Count,"invalid level cannot partially replace calibration");
                var all=Calibration();all["allLevels"]=true;all["levelCodes"]=new[]{"TDR","100nC","10nC","1nC","500pC"};
                Action(bridge,"InstallReferenceCalibration",fake,all);Check(fake.Waves.ContainsKey("TDR")&&fake.Waves.ContainsKey("500pC"),"all-level and TDR reference previews supported");
            }
            VerifyPressureModule(release,qa);
            VerifyCalibrationModule(release,qa);
            Console.WriteLine("REFERENCE_SIMULATION_BRIDGE_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static void VerifyPressureModule(string release,string qa)
    {
        Assembly.LoadFrom(Path.Combine(release,"modules","PressureTest","HVX.PdReview.V1.0.dll"));
        var asm=Assembly.LoadFrom(Path.Combine(release,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
        using(var form=new Form{ClientSize=new Size(1400,850),Text="Reference simulation QA"})
        using(var bridge=new OfflineSimulationBridge(true))
        {
            Set(c,"ProjectPath",qa);Set(c,"SimulationMode",true);form.Controls.Add(c);c.Dock=DockStyle.Fill;form.Show();
            bridge.Start(c,"PressureTest");bridge.ReferenceProfileEnabled=true;
            Action(bridge,"InstallReferencePressure",c,Pressure(12));Application.DoEvents();
            Check((bool)Get(c,"CurrentAcquisitionIsReferencePreview"),"module classifies reference before signal-only metrics");
            Check(((double[])Call(c,"ReadRealtimeMetrics")).All(Double.IsNaN),"module suppresses inferred source C/loss/charge metrics");
            Check(((int[])Get(c,"StatisticsFilteredSourceRows")).Length==0,"module no fabricated location rows");
            Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"real module never reports reference as saved or analyzed");
            Control actionButton=(Control)Field(c,"pressureActionButton");
            Check(!actionButton.Text.Contains("kV")&&actionButton.Text.Contains("非实测"),"reference completion button never represents unknown voltage as measured zero");
            Call(c,"ApplyRealtimeMetrics",500.0,.2,500.0,1.0);Call(c,"SetLocationData",new[]{10.0},new[]{20.0},400.0,99.0);
            Check(((double[])Call(c,"ReadRealtimeMetrics")).All(Double.IsNaN)&&((int[])Get(c,"StatisticsFilteredSourceRows")).Length==0,"late callbacks cannot reclassify reference as PD statistics");
            foreach(int language in new[]{0,1,18})
            {
                Set(c,"LanguageIndex",language);Action(bridge,"InstallReferencePressure",c,Pressure(12));Application.DoEvents();
                Control message=c.Controls.Find("NoiseReferenceOnlyMessage",true).Single();
                Check(message.Visible&&message.Text.Contains(language==1?"No verified":"定位"),"reference neutral message visible language "+language);
                using(var image=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(qa,"reference-pressure-"+language+".png"));}
            }
            Action(bridge,"InstallReferencePressure",c,Pressure(0));
            Check((bool)Get(c,"CurrentAcquisitionIsNoiseReference")&&(string)Get(c,"NoiseReferenceStateCode")=="Pending","source zero explicitly excluded but awaiting acquisition evidence");
            Check(((double[])Call(c,"GetNoiseReferenceSamples")).Length==0,"unverified source zero cannot become a reusable calibrated noise reference");
            Call(c,"ClearDisplayData");Check(!(bool)Get(c,"CurrentAcquisitionIsReferencePreview"),"clear resets reference latch");
            var method=typeof(OfflineSimulationBridge).GetMethod("InstallPressureSimulation",BindingFlags.NonPublic|BindingFlags.Static);
            method.Invoke(null,new object[]{c,Pressure(12)});
            Check(!(bool)Get(c,"CurrentAcquisitionIsReferencePreview")&&((int[])Get(c,"StatisticsFilteredSourceRows")).Length>0,"synthetic mode unaffected and restores statistics");
            form.Close();
        }
    }
    static void VerifyCalibrationModule(string release,string qa)
    {
        var asm=Assembly.LoadFrom(Path.Combine(release,"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
        string project=Path.Combine(qa,"reference-calibration-project");Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"QA only");File.WriteAllText(Path.Combine(project,"CABLE.in"),"QA only");
        using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.CalibrationProgramControl",true)))using(var bridge=new OfflineSimulationBridge(true))
        {
            Set(c,"ProjectPath",project);Set(c,"SimulationMode",true);Set(c,"UseFirstArrivalTiming",true);
            Call(c,"SetPhaseParameters",0,400.0,170.0,false,false);
            bridge.Start(c,"CalibrationTest");bridge.ReferenceProfileEnabled=true;
            Action(bridge,"InstallReferenceCalibration",c,Calibration());
            Check((bool)Get(c,"HasCalibrationData")&&(bool)Get(c,"HasUnsavedCalibrationChanges"),"reference cal genuine module has unsaved simulation data");
            Check((double)Call(c,"GetPhaseCableLengthMeters",0)==400&&(double)Call(c,"GetPhaseVelocityMPerUs",0)==170,"reference generation never changes project geometry");
            Check(Fails(()=>bridge.ReferenceProfileEnabled=false)&&bridge.ReferenceProfileEnabled,"unsaved reference cal cannot silently disappear on source change");
            Check(!Directory.Exists(Path.Combine(project,"Calibration")),"reference preview does not create archives automatically");
            Action(bridge,"InstallReferenceCalibration",c,Calibration(400,170,"10nC"));
            Check((bool)Call(c,"SaveCalibrationFile"),"explicit save persists reference simulation with provenance: "+Get(c,"LastCalibrationError"));
            string savedOrigin=(string)Get(c,"SimulationDataOrigin"),archive=(string)Get(c,"CalibrationFilePath");
            byte[] diskBefore=File.ReadAllBytes(archive);
            using(var reopened=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.CalibrationProgramControl",true)))using(var next=new OfflineSimulationBridge(true))
            {
                Set(reopened,"UseFirstArrivalTiming",true);Set(reopened,"ProjectPath",project);Set(reopened,"SimulationMode",true);
                Check((bool)Get(reopened,"HasCalibrationData")&&(bool)Get(reopened,"IsCalibrationSavedValid"),"saved reference archive is loaded independently of generation selector");
                next.Start(reopened,"CalibrationTest");
                Check(!next.ReferenceProfileEnabled&&next.SourceDescription.Contains("REFERENCE[rdfz-jmf-v1]")&&(string)Get(reopened,"SimulationDataOrigin")==savedOrigin,"default synthetic generator clearly discloses loaded reference data without changing it");
                Check(!(bool)Get(reopened,"HasUnsavedCalibrationChanges")&&File.ReadAllBytes(archive).SequenceEqual(diskBefore),"starting bridge neither dirties loaded archive nor rewrites it");
                var process=typeof(OfflineSimulationBridge).GetMethod("ProcessCalibrationAction",BindingFlags.Instance|BindingFlags.NonPublic);
                process.Invoke(next,new object[]{reopened,"START",Calibration()});
                string mixed=(string)Get(reopened,"SimulationDataOrigin");
                Check(mixed.Contains("REFERENCE[rdfz-jmf-v1]")&&mixed.Contains("SYNTHETIC[V1]")&&mixed.Contains("Mixed simulation origins"),"new synthetic level cannot relabel remaining reference levels as synthetic");
                Check(next.SourceDescription.Contains("Mixed simulation origins"),"actual START final visible description discloses mixed origin");
                process.Invoke(next,new object[]{reopened,"START",Calibration()});
                Check((string)Get(reopened,"SimulationDataOrigin")==mixed,"repeated same-source assignment does not grow origin indefinitely");
                Check(File.ReadAllBytes(archive).SequenceEqual(diskBefore),"new mixed preview never overwrites archived simulation without save");
                Call(reopened,"DiscardCalibrationChanges");next.ReferenceProfileEnabled=true;
                Check(!(bool)Get(reopened,"HasCalibrationData")&&(string)Get(reopened,"SimulationDataOrigin")==""&&!(bool)Get(reopened,"HasUnsavedCalibrationChanges"),"explicit source switch clears only memory and origin cleanly");
                process.Invoke(next,new object[]{reopened,"START",Calibration()});
                Check(!((string)Get(reopened,"SimulationDataOrigin")).Contains("SYNTHETIC[V1]"),"fresh reference generation has pure source after explicit clear");
            }
            Call(c,"DiscardCalibrationChanges");bridge.ReferenceProfileEnabled=false;
            Check(!(bool)Get(c,"HasCalibrationData")&&!(bool)Get(c,"HasUnsavedCalibrationChanges")&&File.ReadAllBytes(archive).SequenceEqual(diskBefore),"explicit discard and source switch preserve disk archive");
        }
    }
    sealed class PreviewControl:Control
    {
        public bool SimulationMode{get;set;}=true;
        public bool ActionHandshakeEnabled{get;set;}
        public bool IsBusy{get;set;}
        public bool ActionPending{get;set;}
        public int LanguageIndex{get;set;}
        public string ProjectPath{get;set;}="QA";
        public string SimulationDataOrigin{get;private set;}="";
        public bool ClearAllowed=true,Saved,Analyzed;
        public int ClearCount;
        public double[] V,Q,Noise;public double Rate,Actual;
        public double[,] Matrix;
        public IDictionary<string,object> Metadata;
        public Dictionary<string,Tuple<double[],double>> Waves=new Dictionary<string,Tuple<double[],double>>();
        public bool ReadPendingAction(out long sequence,out string code,out string json){sequence=0;code=json="";return false;}
        public void CompleteAction(long sequence,bool ok,string error){}
        public void ClearDisplayData(){ClearCount++;}
        public bool ClearSimulationDisplay(){if(!ClearAllowed)return false;ClearCount++;Waves.Clear();SimulationDataOrigin="";return true;}
        public bool SetSimulationDataOrigin(string value){SimulationDataOrigin=value;return true;}
        public void SetAcquisitionWaveforms(double[] v,double[] q,double rate,IDictionary<string,object> metadata){V=v;Q=q;Rate=rate;Metadata=metadata;}
        public void SetReferencePreviewStatus(string message){}
        public void SetSpectrumDataMatrix(double[,] values){Matrix=values;}
        public void SetPressureState(string state,double actual){Actual=actual;}
        public void ReportAcquisitionCompleted(bool value){}
        public void CompleteSave(bool saved,string status){Saved=saved;}
        public void CompleteAnalysis(bool analyzed,string status){Analyzed=analyzed;}
        public void SetPhaseNoiseWaveform(int phase,double[] values,double x0,double dx){Noise=values;}
        public void SetPhaseWaveform(int phase,string level,double[] values,double x0,double dx){Waves[level]=Tuple.Create(values,dx);}
        public void AutoAnalyzePhaseWaveform(int phase,string level){}
    }
}
