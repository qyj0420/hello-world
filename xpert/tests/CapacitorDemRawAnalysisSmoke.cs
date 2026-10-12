using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;

internal static class CapacitorDemRawAnalysisSmoke
{
    const BindingFlags All=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type engine,inputType,requestType;static int checks;
    const string HP="PD-HP10K-SYMMETRIC-FIR4-v1";
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    sealed class Fixture{internal string Root,Dem,Raw,Settings,Cal;internal Dictionary<string,object> Context;internal string[] Headers;internal short[] Adc;internal double[] Art;internal DateTime Captured;}
    static void Check(bool v,string message){checks++;if(!v)throw new InvalidOperationException(message);}
    static object Get(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
    static object Prop(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Put(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] args){var t=o as Type??o.GetType();var m=t.GetMethods(All).Single(x=>x.Name==n&&x.GetParameters().Length==args.Length);try{return m.Invoke(o is Type?null:o,args);}catch(TargetInvocationException e){throw e.InnerException;}}
    static string Hash(string p,bool hex){using(var s=File.OpenRead(p))using(var h=SHA256.Create()){var b=h.ComputeHash(s);return hex?BitConverter.ToString(b).Replace("-",""):Convert.ToBase64String(b);}}
    static object Request(Fixture f,bool zero=true){var r=Activator.CreateInstance(requestType);Set(r,"ProjectPath",f.Root);Set(r,"LocationFilePath",f.Dem);Set(r,"LocationDataId",7L);Set(r,"ManualThresholdEnabled",true);Set(r,"ManualThreshold",20d);Set(r,"AnalyzeZeroVoltageData",zero);return r;}
    static object Analyze(Fixture f,bool zero=true){return Call(engine,"Analyze",Request(f,zero));}
    static void Rejected(Fixture f,string name){var result=Analyze(f);Check(!(bool)Get(result,"Success"),name+" must fail closed");Console.WriteLine("REJECT "+name+": "+Get(result,"Status"));}
    static string SaveCal(string root)
    {
        var t=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);var r=Activator.CreateInstance(t);
        Set(r,"MeasurementKind","Capacitor");Set(r,"Phase","ALL");Set(r,"Level","5nC");Set(r,"DeviceSerial","QA-PICO");Set(r,"FilterId",HP);
        Set(r,"SampleRateHz",1e7);Set(r,"CoefficientPcPerV",1000d);Set(r,"ChargePc",5000d);Set(r,"IncidentSeconds",20e-6);Set(r,"IncidentPeakIndex",200);
        Set(r,"SnrDb",40d);Set(r,"PulseCount",3);Set(r,"CreatedUtc",DateTime.UtcNow.ToString("O"));Set(r,"ReflectionPeakIndex",-1);
        var average=new double[1024];average[200]=5;average[201]=-2;Set(r,"Average",average);Set(r,"ProjectHash",Call(t,"Identity",root));
        var rt=module.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);var raws=Array.CreateInstance(rt,3);
        for(int i=0;i<3;i++){string name="cal-raw-"+i+".csv",p=Path.Combine(root,name);File.WriteAllText(p,"0,0\r\n1,1\r\n");var rr=Activator.CreateInstance(rt);Set(rr,"RelativePath",name);Set(rr,"Sha256",Hash(p,false));raws.SetValue(rr,i);}
        Set(r,"RawFiles",raws);return (string)Call(r,"Save",root);
    }
    static Fixture Make(string qa,string name,bool shared=true,bool zero=false,bool simulation=false)
    {
        string root=Path.Combine(qa,name);Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"Load type = Capacitor\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t17m\r\n");
        var f=new Fixture{Root=root,Captured=DateTime.UtcNow,Adc=new short[500000],Art=new double[4000]};f.Cal=SaveCal(root);
        string dir=Path.Combine(root,"Acquisition","Pressure",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);f.Raw=Path.Combine(dir,"capture.bin");f.Settings=Path.Combine(dir,"settings.xml");
        for(int i=0;i<f.Adc.Length;i++)f.Adc[i]=100;f.Adc[50010]=12100;f.Adc[50055]=-15900;f.Adc[250000]=9100;
        for(int i=0;i<f.Art.Length;i++)f.Art[i]=zero?0:2.5*Math.Sin(2*Math.PI*500*i/80000+.3);
        var s=new Dictionary<string,string>{{"RealHardware",(!simulation).ToString()},{"PicoSerial","QA-PICO"},{"ArtSerial","QA-ART"},{"CaptureDurationMs","50"},{"RequestedPicoRateHz","10000000"},
            {"PdCalibrationPcPerV","1000"},{"PreTriggerPercent","0"},{"RangeIndex","6"},{"ResolutionBits","14"},{"TargetVoltageV","0"},{"DividerVPerV","5000"},{"ArtTrigger","/Dev1/PFI0"},{"FallingTrigger","false"},
            {"PicoChannelAEnabled","true"},{"PicoAnalogOffsetVolts","0"},{"PicoCoupling","1"},{"ArtDevice","Dev1"},{"ExternalTriggerVolts","1.5"}};
        new XDocument(new XElement("BenchSettings",s.Select(v=>new XElement(v.Key,v.Value)))).Save(f.Settings);
        WriteRaw(f,simulation,shared?"offline fixture | ART PFI0 / Pico EXT 共同物理检测上升沿":"offline fixture | ZERO_INPUT_ONLY; FREE_RUN; NO_COMMON_TRIGGER",false);
        f.Context=new Dictionary<string,object>{{"measurementKind","Capacitor"},{"locationApplicable",false},{"rawHasDerivedChargeFilter",false},{"rawSampleUnits","Pico ADC counts; ART input volts"},
            {"highPassFilterId",HP},{"locationAcquisitionId",Guid.NewGuid().ToString("N")},{"phase","A"},{"targetPeakKv",zero?0d:12.5},{"picoRateHz",1e7},{"artRateHz",80000d},
            {"zeroVoltageAnalysisOptIn",zero},{"overRange",false},{"overRangeAccepted",false},{"rawCaptureDirectory",dir.Substring(root.Length+1)},{"rawCaptureFile","capture.bin"},{"rawSettingsFile","settings.xml"},
            {"rawSha256",Hash(f.Raw,true)},{"rawSettingsSha256",Hash(f.Settings,true)},{"capturedUtc",f.Captured.ToString("O")},{"simulation",simulation},{"rawPicoFullScaleV",1d},{"rawMaximumAdc",32767},
            {"calibrationCoefficientPcPerV",1000d},{"rawArtSamples",4000},{"rawPicoSamples",500000},{"calibrationSourceHash",Hash(f.Cal,false)}};
        f.Dem=Path.Combine(root,"A_"+(zero?"0":"12.5")+"kV_1.dem");
        f.Headers=new[]{"Date:\t2026/10/10","Time:\t10:00:00","Cal.File-path:\t"+f.Cal,"Cal.value (pC):\t5000","Cal.max (mV):\tNaN","V-scale max (kV):\t"+(zero?"0":"12.5"),
            "PD-scale max(pC):\t1000","Number of Samples:\t4000","Delta-x [s]:\t0.0000125","Hz:\t500","C[uF]:\t1","D-losses:\t0","","Detection:\t80k absolute peak bins; signed voltage; full-segment high-pass before detection","DATA-TOTAL:","PD (pC):\tVoltage (kV):"};
        WriteDem(f);return f;
    }
    static void WriteRaw(Fixture f,bool simulation,string runtime,bool overflow)
    {using(var w=new BinaryWriter(File.Create(f.Raw))){w.Write("XIONGXIN-ZERO-BENCH-1");w.Write(simulation);w.Write(f.Captured.ToBinary());w.Write("QA-ART");w.Write("QA-PICO");w.Write(runtime);w.Write(80000d);w.Write(1e7);w.Write(32767);w.Write(1d);w.Write(14);w.Write(overflow);w.Write(f.Art.Length);foreach(var v in f.Art)w.Write(v);w.Write(f.Adc.Length);foreach(var v in f.Adc)w.Write(v);}}
    static void WriteDem(Fixture f)
    {f.Headers[12]="Acquisition context:\t"+new JavaScriptSerializer().Serialize(f.Context);using(var w=new StreamWriter(f.Dem,false,new UTF8Encoding(true))){foreach(var line in f.Headers)w.WriteLine(line);for(int i=0;i<4000;i++)w.WriteLine((i==400?"488":"0")+"\t"+(f.Art[i]*5).ToString("R",Inv));}}
    static void Config(Fixture f,string key,string value){var x=XDocument.Load(f.Settings);x.Root.Element(key).Value=value;x.Save(f.Settings);f.Context["rawSettingsSha256"]=Hash(f.Settings,true);WriteDem(f);}
    static void Pump(Func<bool> done){var end=DateTime.UtcNow.AddSeconds(20);while(!done()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(5);}Check(done(),"offline asynchronous batch completed");}
    [STAThread]static int Main(string[] args)
    {
        try{
            Console.OutputEncoding=Encoding.UTF8;
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);module=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            module.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,new Func<string>(()=>Path.Combine(qa,"isolated-preferences.json")));
            module.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",All).SetValue(null,new Func<string>(()=>Path.Combine(qa,"isolated-journals")));
            engine=module.GetType("HVX.PdReview.ReviewAnalysisEngine",true);inputType=module.GetType("HVX.PdReview.ReviewCapacitorDemInput",true);requestType=module.GetType("HVX.PdReview.ReviewAnalysisRequest",true);
            Check(Directory.GetFiles(Path.GetDirectoryName(module.Location)).Length==1,"no Task17 location runtime available; cannot silently call localization");
            var f=Make(qa,"valid");var result=Analyze(f);Check((bool)Get(result,"Success"),(string)Get(result,"Status"));var p=(double[,])Get(result,"Parameters");
            Check(p.GetLength(0)==3,"three real ADC packets, including two pulses in one DEM bin, not a DEM envelope count");
            for(int i=0;i<3;i++){Check(p[i,26]==-2&&p[i,13]==0&&Double.IsNaN(p[i,5])&&Double.IsNaN(p[i,6])&&Double.IsNaN(p[i,11])&&Double.IsNaN(p[i,22]),"capacitor has placeholder 0m; no fake geometry/reflection/confidence");
                Check(p[i,7]==1e7&&p[i,0]==1&&p[i,1]==7&&p[i,2]==12.5,"real raw rate and bound identity retained");Check(p[i,3]>=0&&p[i,3]<360,"phase from verified common trigger and measured ART crossings");}
            Check(p[0,10]==50010&&p[1,10]==50055&&p[2,10]==250000,"raw sample identities not DEM bin IDs");Check(p[0,15]>320&&p[0,15]<400&&p[1,15]<-450&&p[1,15]>-520,"median removal and one captured pC coefficient preserve charge sign");
            Check(((string)Get(result,"Status")).Contains("measured-ART-crossings-only"),"measured phase provenance explicit");
            var display=Call(inputType,"Display",f.Dem,f.Root);Check((double)Get(display,"SampleRateHz")==80000&&((double[])Get(display,"ChargeSamples")).Length==4000&&Double.IsNaN((double)Get(display,"VelocityMPerUs")),"DEM display retains 80k rate and no velocity");
            var noCommon=Make(qa,"no-common",false);var q=Analyze(noCommon);Check((bool)Get(q,"Success"),"Q statistics do not require phase sync");var qp=(double[,])Get(q,"Parameters");Check(qp.GetLength(0)==3&&Enumerable.Range(0,3).All(i=>Double.IsNaN(qp[i,3])),"no common trigger never invents PRPD phase");
            var edge=Make(qa,"wrong-edge");Config(edge,"FallingTrigger","true");Check(Double.IsNaN(((double[,])Get(Analyze(edge),"Parameters"))[0,3]),"opposite trigger edge cannot claim known phase");
            edge=Make(qa,"wrong-pfi");Config(edge,"ArtTrigger","/Dev1/PFI1");Check(Double.IsNaN(((double[,])Get(Analyze(edge),"Parameters"))[0,3]),"wrong PFI line cannot claim common phase");
            var sim=Make(qa,"simulation",true,false,true);var sp=(double[,])Get(Analyze(sim),"Parameters");Check(sp.GetLength(0)==3&&Double.IsNaN(sp[0,3]),"simulated runtime token alone cannot prove real common phase");
            var zero=Make(qa,"zero",false,true);var zr=Analyze(zero);Check((bool)Get(zr,"Success"),"explicit zero-voltage opt-in permits Q only");var zp=(double[,])Get(zr,"Parameters");Check(zp.GetLength(0)==3&&zp[0,2]==0&&Double.IsNaN(zp[0,3]),"zero voltage cannot fabricate phase");Check(!(bool)Get(Analyze(zero,false),"Success"),"current zero analysis setting remains required");
            zero.Context["zeroVoltageAnalysisOptIn"]=false;WriteDem(zero);Rejected(zero,"stored zero opt-in missing");
            var bad=Make(qa,"raw-tamper");using(var s=File.OpenWrite(bad.Raw)){s.Position=s.Length-2;s.WriteByte(12);}Rejected(bad,"raw hash tamper");
            bad=Make(qa,"settings-tamper");File.AppendAllText(bad.Settings," ");Rejected(bad,"settings hash tamper");
            bad=Make(qa,"coefficient");Config(bad,"PdCalibrationPcPerV","2000");Rejected(bad,"settings coefficient mismatch");
            bad=Make(qa,"raw-rate");Config(bad,"RequestedPicoRateHz","125000000");Rejected(bad,"actual requested sample rate mismatch");
            bad=Make(qa,"offset");Config(bad,"PicoAnalogOffsetVolts","0.1");Rejected(bad,"unsupported analog offset cannot be ignored");
            bad=Make(qa,"channel");Config(bad,"PicoChannelAEnabled","false");Rejected(bad,"disabled raw source channel");
            bad=Make(qa,"foreign-dir");bad.Context["rawCaptureDirectory"]="..\\valid";WriteDem(bad);Rejected(bad,"path traversal");
            bad=Make(qa,"wrong-kind");bad.Context["measurementKind"]="Cable";WriteDem(bad);Rejected(bad,"Cable context cannot enter capacitor route");
            bad=Make(qa,"pre-filtered");bad.Context["rawHasDerivedChargeFilter"]=true;WriteDem(bad);Rejected(bad,"raw filter provenance mismatch");
            bad=Make(qa,"quality");bad.Context["overRange"]=true;WriteDem(bad);Rejected(bad,"unacknowledged acquisition quality");bad.Context["overRangeAccepted"]=true;WriteDem(bad);
            var warning=Analyze(bad);Check((bool)Get(warning,"Success")&&((string)Get(warning,"Status")).Contains("operator-acknowledged-warning"),"operator-acknowledged quality warning retained, not renamed quality pass");
            bad=Make(qa,"overflow");WriteRaw(bad,false,"fixture | ART PFI0 / Pico EXT 共同物理检测上升沿",true);bad.Context["rawSha256"]=Hash(bad.Raw,true);WriteDem(bad);Rejected(bad,"unacknowledged actual raw overflow");
            bad=Make(qa,"cal-tamper");File.SetAttributes(bad.Cal,FileAttributes.Normal);File.AppendAllText(bad.Cal,"changed");Rejected(bad,"calibration source changed");
            bad=Make(qa,"missing-raw");File.Move(bad.Raw,bad.Raw+".retained");Rejected(bad,"missing raw cannot fall back to DEM pulses");
            bad=Make(qa,"pending");File.WriteAllText(Path.ChangeExtension(bad.Dem,".pressure-pending"),"pending");
            string marker=(string)Call(module.GetType("HVX.Storage.PressureRecordPublication",true),"MarkerPath",bad.Dem);File.WriteAllText(marker,"pending");Rejected(bad,"pending publication");
            bad=Make(qa,"bad-count");bad.Context["rawPicoSamples"]=500001;WriteDem(bad);Rejected(bad,"raw count mismatch");
            bad=Make(qa,"no-crossings");Array.Clear(bad.Art,0,bad.Art.Length);WriteRaw(bad,false,"fixture | ART PFI0 / Pico EXT 共同物理检测上升沿",false);bad.Context["rawSha256"]=Hash(bad.Raw,true);WriteDem(bad);var nr=Analyze(bad);Check((bool)Get(nr,"Success")&&Double.IsNaN(((double[,])Get(nr,"Parameters"))[0,3]),"missing real voltage crossings retains Q without PRPD");
            Check(Call(module.GetType("HVX.PdReview.PressureAnalysisCache",true),"Key","scope",Request(f))==null,"no DEM-only cache hides changed raw/CAL inputs");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var host=new Form{ClientSize=new Size(1200,800),ShowInTaskbar=false})using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.PdReview.PdReviewControl",true))){
                host.Controls.Add(control);host.Show();Put(control,"PromptSavedAnalysisOnProjectLoad",false);Put(control,"ProjectPath",f.Root);Check((bool)Prop(control,"CapacitorStatisticsMode"),"UI explicit capacitor mode");
                var tree=Get(control,"analysisTree");var paths=(string[])Call(tree,"GetDataPaths");Check(paths.Length==1&&paths[0]==f.Dem,"formal DEM-only project appears in analysis catalog");
                Call(control,"SetFileAnalysisThreshold",f.Dem,true,20d);
                var batch=(Task<double[,]>)Call(control,"AnalyzeSavedPressureFilesAsync",new object[]{new[]{f.Dem}});Pump(()=>batch.IsCompleted);var bp=batch.GetAwaiter().GetResult();Check(bp.GetLength(0)==3&&bp[0,26]==-2,"actual background API consumes DEM raw without LOC or speed; rows="+bp.GetLength(0));
                var waves=new double[9,((double[,])Get(result,"Raw")).GetLength(1)];var matrices=new[]{(double[,])Get(result,"Raw"),(double[,])Get(result,"Filtered"),(double[,])Get(result,"Equivalent")};
                Check((bool)Call(control,"SetProjectLocationIds",new[]{f.Dem},new[]{7d}),"session binds DEM source ID");for(int block=0;block<3;block++)for(int r=0;r<3;r++)for(int c=0;c<waves.GetLength(1);c++)waves[block*3+r,c]=matrices[block][r,c];
                Check((bool)Call(control,"ForceReloadDataMatrices",p,waves),"real capacitor pulse matrices accepted without reflection: "+Prop(control,"LastError"));
                object[] session={null,null,null};Check((bool)control.GetType().GetMethod("ExportAnalysisSessionUtf8",All).Invoke(control,session),"capacitor DEM session exports");
                Check((bool)Call(control,"LoadAnalysisSessionUtf8",f.Root,session[0],session[1],session[2]),"capacitor DEM session reloads: "+Prop(control,"LastError"));
                byte[] original=File.ReadAllBytes(f.Raw);using(var stream=File.OpenWrite(f.Raw)){stream.Position=stream.Length-2;stream.WriteByte(77);}
                Check(!(bool)Call(control,"LoadAnalysisSessionUtf8",f.Root,session[0],session[1],session[2]),"saved statistics cannot bypass changed bound raw on session restore");File.WriteAllBytes(f.Raw,original);
                // Unknown phase retains Q/L=0 statistics, but creates no PRPD
                // points; neither the UI nor saved-session loader fills NaNs.
                Put(control,"ProjectPath",noCommon.Root);Call(control,"SetProjectLocationIds",new[]{noCommon.Dem},new[]{7d});
                Check((bool)Call(control,"ForceReloadDataMatrices",qp,waves),"phase-unavailable capacitor rows retained for Q statistics");
                var spectrum=Prop(control,"Spectrum");Put(spectrum,"DisplayModeIndex",0);Application.DoEvents();
                var view=Get(spectrum,"view");Check(((System.Collections.ICollection)Get(view,"Points")).Count==3,"Q-L retains genuine pulses at placeholder 0m");
                foreach(object point in (System.Collections.IEnumerable)Get(view,"Points"))Check((double)Get(point,"X")==0,"Q-L position is not fabricated localization");
                Put(spectrum,"DisplayModeIndex",2);Application.DoEvents();view=Get(spectrum,"view");Check(((System.Collections.ICollection)Get(view,"Points")).Count==0,"Q-phase plot excludes unknown phase instead of zero-degree PRPD");
                Put(spectrum,"DisplayModeIndex",11);Application.DoEvents();view=Get(spectrum,"view");Check(((System.Collections.ICollection)Get(view,"Points")).Count==0,"PRPD density excludes phase-unavailable pulses");
                session=new object[]{null,null,null};Check((bool)control.GetType().GetMethod("ExportAnalysisSessionUtf8",All).Invoke(control,session),"phase-unavailable DEM session exports");
                Check((bool)Call(control,"LoadAnalysisSessionUtf8",noCommon.Root,session[0],session[1],session[2]),"phase-unavailable DEM session reloads without fake phase");
            }
            var legacy=Make(qa,"legacy-loc");string loc=Path.Combine(legacy.Root,"A_12.5kV_2.loc");
            using(var writer=new StreamWriter(loc,false,new UTF8Encoding(true))){foreach(string line in new[]{"Cable length\t0","Velocity (m/us)\t0","Delta x\t1E-7","Phase\t10","Voltage\t12.5","F(Hz)\t50","Sample units\tpC*100000","Reserved\t0","Reserved2\t0","Reserved3\t0","Data"})writer.WriteLine(line);
                for(int i=0;i<200000;i++)writer.WriteLine((i==50000?"10000000":i==100000?"-20000000":"0"));}
            var lr=Request(legacy);Set(lr,"LocationFilePath",loc);var legacyResult=Call(engine,"Analyze",lr);Check((bool)Get(legacyResult,"Success"),"legacy already-calibrated capacitor LOC remains compatible");
            var lp=(double[,])Get(legacyResult,"Parameters");Check(lp.GetLength(0)==2&&lp[0,15]>90&&lp[0,15]<110&&lp[1,15]<-190&&lp[1,15]>-210,"legacy pC is not multiplied by captured CAL a second time");
            var cable=Make(qa,"not-cap");File.WriteAllText(Path.Combine(cable.Root,"ITEM.in"),"Load type = Three-core\r\n");Rejected(cable,"cable cannot accept DEM raw capacitor route");
            Console.WriteLine("PASS CapacitorDemRawAnalysisSmoke checks="+checks+"; hardware=false; Task17=false; fixture="+qa);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
