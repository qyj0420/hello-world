using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using HVX.Controls;
using Xiongxin.Calibration;

// Synthetic averages/windows only: never starts product Main, worker or SDK.
internal static class CalibrationPacketTimingSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Phase(CalibrationProgramControl c){return Call(c,"GetPhaseStateNoLock",-1);}
    static double[] Samples(CalibrationProgramControl c){return (double[])Field(((IDictionary)Field(Phase(c),"Waveforms"))["100pC"],"Samples");}
    static object Marker(CalibrationProgramControl c){return ((IDictionary)Field(Phase(c),"Markers"))["100pC"];}
    static double Time(CalibrationProgramControl c,string name){return (double)Field(Marker(c),name);}
    static double[] Wave()
    {
        var x=Enumerable.Range(0,1100).Select(i=>1e-6*Math.Sin(.73*i)).ToArray();
        Action<int,int,double> pulse=(peak,width,amplitude)=>{for(int i=peak-width;i<=peak+width;i++)x[i]+=amplitude*Math.Max(0,1-Math.Abs(i-peak)/(double)width);};
        pulse(250,10,.10);pulse(400,8,.15);pulse(630,16,.08);pulse(700,20,.06);pulse(850,12,.30);return x;
    }
    static string Project(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"一体三芯电缆\"\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t259.2m\r\n");return root;
    }
    static CalibrationProgramControl Open(string root,bool arrival)
    {
        var c=new CalibrationProgramControl();c.UseFirstArrivalTiming=arrival;c.ShowReflectionSelectionNoticeForTests=_=>{};
        c.ConfirmQualityWarningSaveForTests=_=>true;c.ProjectPath=root;c.SelectedLevelCode="100pC";return c;
    }
    static CalibrationProgramControl OpenSimulation(string root,bool arrival)
    {
        var c=new CalibrationProgramControl();c.UseFirstArrivalTiming=arrival;c.SimulationMode=true;
        c.ShowReflectionSelectionNoticeForTests=_=>{};c.ProjectPath=root;c.SelectedLevelCode="100pC";return c;
    }
    static bool HasPeakSelection(CalibrationProgramControl c)
    {return ((IDictionary)Field(c,"reflectionSelections")).Values.Cast<object>().Any(s=>(bool)Field(s,"PeakAnchored"));}
    // Independent legacy encoder: no use of the new writer and no added peak flag.
    static byte[] LegacySimulationPayload(int version,bool arrival,string identity)
    {
        using(var buffer=new MemoryStream())using(var w=new BinaryWriter(buffer,Encoding.UTF8)) {
            w.Write(version);w.Write(true);w.Write(arrival);w.Write(identity);
            for(int phase=0;phase<4;phase++) {
                w.Write(259.2);w.Write(180d);w.Write(false);w.Write(false);w.Write(60d);w.Write(phase==0?1:0);
                if(phase==0) {
                    w.Write("100pC");w.Write(0d);w.Write(1e-8);var wave=Wave();w.Write(wave.Length);foreach(double sample in wave)w.Write(sample);
                    double start=(arrival?393:400)*1e-8,end=(arrival?681:700)*1e-8;
                    w.Write(true);w.Write(start);w.Write(end);w.Write(start+2*259.2/(180*1e6));w.Write((end-start)*180*1e6/2);
                    w.Write(400);w.Write(700);w.Write(393);w.Write(681);w.Write(0d);w.Write(false);
                }
                w.Write(false);w.Write(0);w.Write(0);
            }
            if(version>=2)w.Write("SIM: legacy fixture");w.Flush();return buffer.ToArray();
        }
    }
    static void WriteSimulationEnvelope(string path,byte[] payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using(var s=new FileStream(path,FileMode.CreateNew))using(var w=new BinaryWriter(s,Encoding.UTF8))using(var sha=SHA256.Create()) {
            w.Write(Encoding.ASCII.GetBytes("HVCSIM01"));w.Write(payload.Length);w.Write(sha.ComputeHash(payload));w.Write(payload);
        }
    }
    static void SimulationRoundtrips(string testRoot)
    {
        foreach(bool arrival in new[]{false,true}) {
            string root=Project(Path.Combine(testRoot,"simulation-v3-"+(arrival?"arrival":"peak"))),path,digest;
            using(var c=OpenSimulation(root,arrival)) {
                c.SetPhaseParameters(-1,259.2,180,false,false);c.SetPhaseWaveform(-1,"100pC",Wave(),0,1e-8);
                Check(c.SetPhasePacketPeaks(-1,"100pC",250,700),"simulation explicitly selects nonautomatic incident packet");
                c.SetSimulationDataOrigin("SIM: packet-selection offline fixture");
                Check(c.SaveCalibrationFile(),"simulation v3 saves selected peaks: "+c.LastCalibrationError);
                path=c.CalibrationFilePath;digest=LiveCalibrationRecord.HashFile(path);
                Check(BitConverter.ToInt32(File.ReadAllBytes(path),44)==3,"new simulation writes payload v3 within existing hashed envelope");
                Check(HasPeakSelection(c)&&!c.HasUnsavedCalibrationChanges&&c.IsCalibrationSavedValid,"detached archive validation never erases current peak provenance");
            }
            using(var c=OpenSimulation(root,arrival)) {
                Check(c.IsCalibrationSavedValid&&!c.HasUnsavedCalibrationChanges&&HasPeakSelection(c),"simulation v3 reload restores explicit provenance without a new edit");
                Check(c.GetIncidentPeakIndex(-1,"100pC")==250&&c.GetReflectionPeakIndex(-1,"100pC")==700,"simulation reload retains physical selected pair");
                Check(c.GetIncidentFirstArrivalIndex(-1,"100pC")==241&&c.GetReflectionFirstArrivalIndex(-1,"100pC")==681,"simulation reload retains each selected packet onset");
                c.UseFirstArrivalTiming=!arrival;c.SetPhaseBandwidth(-1,true,30);c.SetPhaseBandwidth(-1,false,60);
                Check(c.GetIncidentPeakIndex(-1,"100pC")==250&&c.GetReflectionPeakIndex(-1,"100pC")==700,"simulation mode/filter refresh cannot reselect stronger packets");
                Check(Math.Abs(Time(c,"StartTimeSeconds")-(!arrival?241:250)*1e-8)<1e-15&&
                    Math.Abs(Time(c,"DistanceMarkerSeconds")-(!arrival?681:700)*1e-8)<1e-15,"simulation refresh changes timing mode, not packet identity");
                Check(LiveCalibrationRecord.HashFile(path)==digest,"simulation reload and in-memory mode change never rewrite the stored archive");
            }
        }
        foreach(int version in new[]{1,2})foreach(bool arrival in new[]{false,true}) {
            string root=Project(Path.Combine(testRoot,"simulation-legacy-v"+version+"-"+arrival)),path=Path.Combine(root,"Calibration","Simulation","calibration.hvcsim");
            using(var c=OpenSimulation(root,arrival))WriteSimulationEnvelope(path,LegacySimulationPayload(version,arrival,(string)Call(c,"CalibrationProjectIdentity")));
            string digest=LiveCalibrationRecord.HashFile(path);
            using(var c=OpenSimulation(root,arrival)) {
                Check(c.IsCalibrationSavedValid&&!c.HasUnsavedCalibrationChanges,"legacy simulation v"+version+" remains readable without canonical rewrite");
                Check(!HasPeakSelection(c)&&c.GetIncidentPeakIndex(-1,"100pC")==400&&c.GetReflectionPeakIndex(-1,"100pC")==700,"legacy simulation is not relabelled new selected-peak evidence");
                Check(Math.Abs(Time(c,"StartTimeSeconds")-(arrival?393:400)*1e-8)<1e-15&&
                    Math.Abs(Time(c,"DistanceMarkerSeconds")-(arrival?681:700)*1e-8)<1e-15,"legacy simulation keeps exact stored propagation coordinates");
                Check(LiveCalibrationRecord.HashFile(path)==digest&&BitConverter.ToInt32(File.ReadAllBytes(path),44)==version,"legacy simulation file bytes and version remain unchanged");
            }
        }
        string pending=Project(Path.Combine(testRoot,"simulation-v3-unconfirmed"));
        using(var c=OpenSimulation(pending,true)) {
            var wave=new double[800];wave[200]=.15;wave[600]=.06;c.SetPhaseWaveform(-1,"100pC",wave,0,1e-8);
            Check(c.SetPhasePacketPeaks(-1,"100pC",200,600)&&Marker(c)==null,"simulation pending pair keeps unknown onset");
            Check(c.SaveCalibrationFile(),"simulation saves pending peak evidence without inventing first-arrival timing");
        }
        using(var c=OpenSimulation(pending,true)) {
            Check(c.IsCalibrationSavedValid&&HasPeakSelection(c)&&Marker(c)==null,"simulation pending pair reloads without a fabricated measurement marker");
            c.SetPhaseBandwidth(-1,true,30);c.SetPhaseBandwidth(-1,false,60);
            Check(c.GetIncidentPeakIndex(-1,"100pC")==200&&c.GetReflectionPeakIndex(-1,"100pC")==600&&Marker(c)==null,"pending selected packets survive refresh without borrowing another onset");
            byte[] future=(byte[])Call(c,"CreateCalibrationPayload");Array.Copy(BitConverter.GetBytes(4),future,4);
            bool rejected=false;try{Call(c,"ReadCalibrationPayload",future);}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidDataException;}
            Check(rejected&&HasPeakSelection(c)&&Marker(c)==null,"unsupported simulation version fails closed without mutating current selection");
        }
    }
    static void Inject(CalibrationProgramControl c,string root,double[] wave)
    {
        c.SetPhaseParameters(-1,259.2,180,false,false);c.SetPhaseWaveform(-1,"100pC",wave,0,1e-8);
        var windows=Enumerable.Range(0,4).Select(_=>(double[])wave.Clone()).ToArray();
        var quality=HVX.OWTS.Task38.CalibrationIncidentQuality.Analyze(windows,200,336,465,100,2,false,"synthetic",null);
        string raw=Path.Combine(root,"synthetic-original.bin");File.WriteAllBytes(raw,new byte[]{1,3,5,7});
        var capture=new CalibrationCaptureResult{Average=wave,Windows=windows,SampleRateHz=1e8,Assessment=quality,RawPaths=new[]{raw},
            DeviceSerial="OFFLINE-PACKET",DeviceModel="Synthetic",RangeIndex=7,AcquisitionMode="Pulse",
            Reflection=new TdrCorrelationResult{Valid=false,IncidentIndex=400,ReflectionIndex=-1,Message="Synthetic unresolved pair."}};
        Call(c,"ReceiveLiveCapture",-1,"100pC",capture);
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[0]);
            SimulationRoundtrips(args[0]);
            foreach(bool arrival in new[]{false,true}) {
                string root=Project(Path.Combine(args[0],arrival?"arrival":"peak"));double[] wave=Wave();
                string rawDigest;
                using(var c=Open(root,arrival)) {
                    Inject(c,root,wave);rawDigest=LiveCalibrationRecord.HashFile(Path.Combine(root,"synthetic-original.bin"));
                    Check(c.SetPhasePacketPeaks(-1,"100pC",400,700),"user peak pair accepted");
                    Check(c.GetIncidentPeakIndex(-1,"100pC")==400&&c.GetReflectionPeakIndex(-1,"100pC")==700,"selected packet identities never replaced by stronger adjacent packets");
                    int i=c.GetIncidentFirstArrivalIndex(-1,"100pC"),r=c.GetReflectionFirstArrivalIndex(-1,"100pC");
                    Check(i==393&&r==681,"each selected triangle uses its own connected onset, not another packet or peak: "+i+","+r);
                    Check(Math.Abs(Time(c,"StartTimeSeconds")-(arrival?i:400)*1e-8)<1e-15,"incident timing follows preference");
                    Check(Math.Abs(Time(c,"DistanceMarkerSeconds")-(arrival?r:700)*1e-8)<1e-15,"reflection timing follows preference");
                    object gui=Call(c,"PacketGuiMarkersNoLock",Phase(c),"100pC",Marker(c));
                    Check(Math.Abs((double)Field(gui,"StartTimeSeconds")-4e-6)<1e-15&&Math.Abs((double)Field(gui,"DistanceMarkerSeconds")-7e-6)<1e-15,"GUI handles remain on physical peaks, not travel-time markers");
                    Check(c.SetPhasePacketPeaks(-1,"100pC",399,699)&&c.GetIncidentPeakIndex(-1,"100pC")==400&&c.GetReflectionPeakIndex(-1,"100pC")==700,"two-sample pixel rounding resolves only the chosen local peaks");
                    Check(!c.SetPhasePacketPeaks(-1,"100pC",370,700)&&c.GetIncidentPeakIndex(-1,"100pC")==400,"blank/rising-foot selection cannot jump to another peak");
                    c.EmbeddedMode=true;c.Size=new Size(1280,720);c.CreateControl();
                    using(var bitmap=new Bitmap(1280,720))using(var graphics=Graphics.FromImage(bitmap)) {
                        Call(c,"RenderFrame",graphics,new Rectangle(Point.Empty,bitmap.Size));
                        bitmap.Save(Path.Combine(root,"peak-selection-ui.png"));
                    }
                    double before=Time(c,"DistanceMarkerSeconds");
                    Set(c,"viewX0",0d);Set(c,"viewX1",1099e-8);
                    Set(c,"packetDragIncident",400);Set(c,"packetDragReflection",700);Set(c,"draggingCursor",2);Set(c,"cursorGrabOffsetX",0f);
                    Call(c,"PreviewDraggedCursor",new PointF((float)Call(c,"TimeToReferenceX",8.5e-6),200));
                    Check(Time(c,"DistanceMarkerSeconds")==before,"drag preview never publishes provisional propagation timing");
                    Call(c,"FinishCursorDrag");Call(c,"EndPlotGesture");
                    Check(c.GetReflectionPeakIndex(-1,"100pC")==850,"GUI reflection drag commits the selected peak packet");
                    Check(!arrival||c.GetReflectionFirstArrivalIndex(-1,"100pC")<850,"GUI reflection drag does not call peak an arrival");
                    Set(c,"packetDragIncident",400);Set(c,"packetDragReflection",850);Set(c,"draggingCursor",1);
                    Call(c,"PreviewDraggedCursor",new PointF((float)Call(c,"TimeToReferenceX",2.5e-6),200));
                    Call(c,"FinishCursorDrag");Call(c,"EndPlotGesture");
                    Check(c.GetIncidentPeakIndex(-1,"100pC")==250,"GUI incident drag commits its own selected packet: "+c.GetIncidentPeakIndex(-1,"100pC"));
                    Check(c.GetIncidentFirstArrivalIndex(-1,"100pC")==241,"GUI incident drag resolves the selected packet's onset");
                    Check(c.SetPhasePacketPeaks(-1,"100pC",400,700),"restore selected pair explicitly");
                    var savedWave=(double[])Samples(c).Clone();
                    c.SetPhaseBandwidth(-1,true,30);c.SetPhaseBandwidth(-1,false,60);
                    Check(c.GetIncidentFirstArrivalIndex(-1,"100pC")==i&&c.GetReflectionFirstArrivalIndex(-1,"100pC")==r,"display filtering cannot replace selected raw onset evidence");
                    c.UseFirstArrivalTiming=!arrival;
                    Check(c.GetIncidentPeakIndex(-1,"100pC")==400&&c.GetReflectionPeakIndex(-1,"100pC")==700,"mode switch retains selected peaks");
                    Check(Math.Abs(Time(c,"DistanceMarkerSeconds")-(!arrival?r:700)*1e-8)<1e-15,"mode switch recomputes timing instead of reusing old manual time");
                    c.UseFirstArrivalTiming=arrival;
                    Check(Samples(c).SequenceEqual(savedWave)&&wave.SequenceEqual(savedWave),"selection and timing leave original average unchanged");
                    Check((bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"selected pair saves with retained raw provenance: "+c.LastCalibrationError);
                    var record=LiveCalibrationRecord.Load(root,LiveCalibrationRecord.RecordPath(root,"ALL","100pC"));
                    Check(record.PeakAnchoredSelection&&record.ReflectionSelectionMethod=="Manual","new peak-selection provenance is explicit");
                    Check(record.IncidentPeakIndex==400&&record.ReflectionPeakIndex==700&&record.ManualIncidentSeconds==4e-6&&record.ManualReflectionSeconds==7e-6,"stored editing coordinates and peak indexes agree");
                    Check(Math.Abs(record.IncidentSeconds-Time(c,"StartTimeSeconds"))<1e-15&&Math.Abs(record.ReflectionSeconds-Time(c,"DistanceMarkerSeconds"))<1e-15,"stored propagation uses actual selected timing mode");
                    Check(record.CoefficientIncidentPeakIndex==400&&Math.Abs(record.CoefficientPcPerV-100/.15)<.1,"coefficient uses selected HP-only peak, never onset amplitude");
                    Check(Math.Abs(record.VelocityMPerUs-2*record.LengthM/((record.ReflectionSeconds-record.IncidentSeconds)*1e6))<1e-9,"velocity and selected timing are consistent");
                    Check(LiveCalibrationRecord.HashFile(Path.Combine(root,"synthetic-original.bin"))==rawDigest,"raw source untouched");
                    double old=record.ManualReflectionSeconds;record.ManualReflectionSeconds=8.5e-6;
                    bool rejected=false;try{record.Validate(root,false);}catch(InvalidDataException){rejected=true;}Check(rejected,"archive rejects mismatched new selected-peak coordinate");record.ManualReflectionSeconds=old;
                    if(arrival){old=record.ReflectionSeconds;record.ReflectionSeconds=record.ManualReflectionSeconds;rejected=false;try{record.Validate(root,false);}catch(InvalidDataException){rejected=true;}Check(rejected,"first arrival cannot masquerade as selected peak");record.ReflectionSeconds=old;}
                    c.SelectedLevelCode="TDR";c.SetPhaseParameters(-1,259.2,180,false,false);c.SetPhaseWaveform(-1,"TDR",wave,0,1e-8);
                    Check(c.SetPhasePacketPeaks(-1,"TDR",400,700),"TDR selects the same physical pair independently of charge calibration");
                    var tdrSource=new CalibrationCaptureResult {DeviceSerial="OFFLINE-PACKET",DeviceModel="Synthetic",RangeIndex=7,SampleRateHz=1e8,
                        Windows=Enumerable.Range(0,4).Select(_=>(double[])wave.Clone()).ToArray(),RawPaths=new[]{Path.Combine(root,"synthetic-original.bin")},AcquisitionMode="Pulse"};
                    object tdrMarker=((IDictionary)Field(Phase(c),"Markers"))["TDR"];
                    var tdr=(LiveCalibrationRecord)Call(c,"PrepareTdrArchive",tdrSource,Phase(c),tdrMarker);
                    Check(tdr.PeakAnchoredSelection&&tdr.IsTdr&&Double.IsNaN(tdr.CoefficientPcPerV),"TDR retains peak provenance without inventing charge coefficient");
                    Check(Math.Abs(tdr.LengthM-(arrival?259.2:270))<1e-9,"TDR length follows selected first-arrival or peak delta");
                    string projectBefore=LiveCalibrationRecord.Identity(root),tdrFile=tdr.Save(root);
                    var tdrReload=LiveCalibrationRecord.Load(root,tdrFile);
                    Check(tdrReload.IncidentPeakIndex==400&&tdrReload.ReflectionPeakIndex==700&&tdrReload.FirstArrival==arrival,"TDR archive roundtrip retains peaks and actual timing preference");
                    Check(LiveCalibrationRecord.Identity(root)==projectBefore&&LiveCalibrationRecord.HashFile(Path.Combine(root,"synthetic-original.bin"))==rawDigest,"preparing/storing TDR evidence does not rewrite project geometry or raw source");
                }
                using(var c=Open(root,arrival)) {
                    Check(c.IsCalibrationSavedValid&&c.SelectedCalibrationSaved,"new archive reload validates");
                    Check(c.GetIncidentPeakIndex(-1,"100pC")==400&&c.GetReflectionPeakIndex(-1,"100pC")==700,"reload never interprets onset as reflection peak");
                    Check(c.GetIncidentFirstArrivalIndex(-1,"100pC")==393&&c.GetReflectionFirstArrivalIndex(-1,"100pC")==681,"reload reconstructs same anchored arrivals");
                    Check((bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"reloaded average can save unchanged pair without acquisition windows");
                }
            }
            string changed=Project(Path.Combine(args[0],"changed-incident"));using(var c=Open(changed,true)) {
                Inject(c,changed,Wave());Check(c.SetPhasePacketPeaks(-1,"100pC",250,700),"different incident packet explicitly selected");
                Check(c.GetIncidentFirstArrivalIndex(-1,"100pC")==241&&c.GetReflectionFirstArrivalIndex(-1,"100pC")==681,"both new selected packets own their arrivals");
                Check((bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"changed incident saves with current per-pulse evidence");
                var record=LiveCalibrationRecord.Load(changed,LiveCalibrationRecord.RecordPath(changed,"ALL","100pC"));
                Check(record.CoefficientIncidentPeakIndex==250&&Math.Abs(record.CoefficientPcPerV-100/.10)<.1,"coefficient follows the selected incident rather than old acquisition peak");
            }
            string historical=Project(Path.Combine(args[0],"legacy-manual"));
            var oldRecord=LiveCalibrationRecord.Load(changed,LiveCalibrationRecord.RecordPath(changed,"ALL","100pC"));
            oldRecord.PeakAnchoredSelection=false;oldRecord.ManualIncidentSeconds=oldRecord.IncidentSeconds;oldRecord.ManualReflectionSeconds=oldRecord.ReflectionSeconds;
            string historicalFile=oldRecord.Save(historical),historicalDigest=LiveCalibrationRecord.HashFile(historicalFile);
            using(var c=Open(historical,true)) {
                Check(c.GetIncidentPeakIndex(-1,"100pC")==250&&c.GetReflectionPeakIndex(-1,"100pC")==700,"legacy manual archive retains stored peaks, never substitutes timing indexes");
                Check(Math.Abs(Time(c,"StartTimeSeconds")-oldRecord.ManualIncidentSeconds)<1e-15&&Math.Abs(Time(c,"DistanceMarkerSeconds")-oldRecord.ManualReflectionSeconds)<1e-15,"legacy manual timing coordinates remain exact");
                c.UseFirstArrivalTiming=false;
                Check(Math.Abs(Time(c,"DistanceMarkerSeconds")-oldRecord.ManualReflectionSeconds)<1e-15,"historical manual timing is not silently relabelled new peak-selection provenance");
                Check(!LiveCalibrationRecord.Load(historical,historicalFile).PeakAnchoredSelection&&LiveCalibrationRecord.HashFile(historicalFile)==historicalDigest,"legacy loading and mode switch do not rewrite the record");
            }
            string pendingRoot=Project(Path.Combine(args[0],"legacy-unconfirmed"));
            oldRecord.ReflectionStatus="NotDetected";oldRecord.VelocitySource="Configured";oldRecord.VelocityMPerUs=180;
            oldRecord.ReflectionSeconds=oldRecord.AttenuationDb=oldRecord.Correlation=Double.NaN;oldRecord.ReflectionPeakIndex=-1;
            oldRecord.ManualReflectionSeconds=7e-6;string pendingFile=oldRecord.Save(pendingRoot),pendingHash=LiveCalibrationRecord.HashFile(pendingFile);
            using(var c=Open(pendingRoot,true)) {
                Check(c.SelectedCalibrationSaved&&Marker(c)==null,"historical unconfirmed editing coordinate loads without inventing measured reflection");
                Check(c.GetReflectionPeakIndex(-1,"100pC")==-1,"historical missing reflection does not index negative array position or create a peak");
                Check(LiveCalibrationRecord.HashFile(pendingFile)==pendingHash,"pending historical coordinate remains untouched on disk");
            }
            using(var c=new CalibrationProgramControl()) {
                c.UseFirstArrivalTiming=true;c.SimulationMode=true;var spikes=new double[800];spikes[200]=.15;spikes[600]=.06;
                c.SetPhaseWaveform(-1,"100pC",spikes,0,1e-8);
                Check(c.SetPhasePacketPeaks(-1,"100pC",200,600),"unresolved peaks remain selectable editing evidence");
                Check(Marker(c)==null&&!String.IsNullOrEmpty(c.GetTimingStatus(-1,"100pC")),"one-sample peaks cannot fabricate first arrival or propagation marker");
                Check(Call(c,"PacketGuiMarkersNoLock",Phase(c),"100pC",null)!=null,"unconfirmed travel timing does not hide selectable peak handles");
                c.UseFirstArrivalTiming=false;Check(Marker(c)!=null&&Time(c,"DistanceMarkerSeconds")==6e-6,"explicit peak mode may use actual selected peaks without claiming arrivals");
            }
            // Truncated/sustained support and invalid endpoints fail closed.
            var cut=Enumerable.Repeat(.1,400).ToArray();cut[200]=.2;
            Check(CalibrationArrivalMath.FindSelectedPacket(cut,200,0,399,200,0,1e-6)==-1,"no local quiet support cannot borrow another packet or crop edge");
            var finite=Wave();finite[690]=Double.NaN;
            Check(CalibrationArrivalMath.FindSelectedPacket(finite,700,401,1099,300,0,1e-6)==-1,"nonfinite selected packet has no arrival");
            Check(CalibrationArrivalMath.FindSelectedPacket(Wave(),0,0,1099,300,0,1e-6)==-1,"endpoint cannot establish selected onset");
            Console.WriteLine("CALIBRATION_PACKET_TIMING_PASS="+checks+"; SDK_CALLS=0; PRODUCT_STARTED=0; ACTUAL_WORKER_STARTED=0; ORIGINAL_DATA_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
