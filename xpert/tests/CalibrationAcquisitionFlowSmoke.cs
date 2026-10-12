using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationAcquisitionFlowSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Field(object o,string name,object value){o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,value);}
    static object Field(object o,string name){return o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    sealed class Io : IControlBoxIo
    {
        public void Open(){}public void Close(){}public bool ReadEmergencySignalHigh(){return false;}
        public void WriteShortCable(bool on){}
    }
    sealed class Replay : ICalibrationWorker
    {
        internal CalibrationForm Form;
        internal int Preflights,Samples;
        internal string Mode;
        internal bool CancelDuringPreflight;
        internal bool NoSignal, FailCapture;
        internal bool Capacitor;
        internal bool Supplement;
        internal int SegmentCount=-1;
        internal int LastPreflightRange;
        internal bool Repeating;
        internal readonly System.Collections.Generic.List<int> PreflightRanges=new System.Collections.Generic.List<int>();
        public bool Broken {get{return false;}}
        public void Cancel(){}
        public Task Close(int a=5000,int b=2000){return Task.FromResult(0);}
        public Task<bool> RetireUnresponsive(){return Task.FromResult(false);}
        public Task<Reply> Send(Command command,int timeout)
        {
            Check(command.Op=="CAPTURE","No device-open or voltage commands in replay");
            bool preflight=Path.GetFileName(command.DataPath).StartsWith("preflight_");
            bool dialog=Application.OpenForms.Cast<Form>().Any(f=>f is CalibrationSelfCheckWindow);
            var config=Wire.Decode<Settings>(Wire.Encode(command.Config));
            Check(config.DurationMs<=60,"every requested segment respects 60 ms upper limit");
            Check(config.A.Coupling==0,"preflight, pulse, segment and supplement all request AC coupling");
            if(preflight) {
                LastPreflightRange=config.A.Range;
                PreflightRanges.Add(config.A.Range);
                Preflights++;Check(dialog,"Self-check window present during preflight");
                Check(config.DurationMs==40&&config.TriggerMode==0,"Preflight requests exactly 40 ms, not inherited 60 ms");
                if(Preflights==1)Check(config.A.Range==10,"Starts at maximum Pico range");
                else if(!NoSignal&&!Repeating)Check(config.A.Range==5,"Second preflight uses measured ±0.5V range");
                if(CancelDuringPreflight){Form.HostCancel();return Task.FromResult(new Reply{Ok=false,Message="cancelled"});}
                if(FailCapture)return Task.FromResult(new Reply{Ok=false,Message="TEST: Pico disconnected"});
                var checkWindow=Application.OpenForms.Cast<Form>().Single(f=>f is CalibrationSelfCheckWindow);
                Check(checkWindow.Controls.OfType<Label>().Single().Text=="内部参数适配中…","Operator sees concise adaptation text, no range/trigger details");
            } else {
                Samples++;Check(!dialog,"Self-check window closed before measurement capture");
                Check(config.TriggerMode==(Mode=="Segment"?0:1),"Selected mode reaches capture command");
                Check(config.A.Range==5&&config.ThresholdV>.01&&config.ThresholdV<.08,"Final range and signal-derived threshold used");
            }
            const double fs=10e6;int count=(int)Math.Ceiling(config.DurationMs*fs/1000);
            var values=new double[count];
            if(!NoSignal) {
                for(int i=0;i<count;i++)values[i]=.0003*Math.Sin(i*1.618);
                if(preflight||Mode=="Segment")for(int peak=50000,ordinal=0;peak<count-2000;peak+=100000,ordinal++) {if(preflight||SegmentCount<0||ordinal<SegmentCount)Pulse(values,peak);}
                else { Pulse(values,3000); if(Supplement&&Samples<=2)for(int j=-40;j<=40;j++)values[3000+j]+=.09*Math.Exp(-Math.Pow(j/15d,2)); }
            }
            // Deliberately retain an ADC offset even though AC was requested.
            // Software correction must occur before preview/filter/averaging.
            var adc=values.Select(v=>(short)(Math.Round(v/Settings.Ranges[config.A.Range]*32767)+128)).ToArray();
            var reply=new Reply{Ok=true,Connected=true,Config=config,IntervalNs=100,Samples=count,MaxAdc=32767,Serial="OFFLINE-REPLAY",Model="TEST-ONLY"};
            Wire.Save(new Capture{Info=reply,A=adc,B=null},command.DataPath);
            return Task.FromResult(reply);
        }
        void Pulse(double[] v,int peak)
        {
            for(int j=-30;j<=30;j++){v[peak+j]+=.15*Math.Exp(-Math.Pow(j/4d,2));if(!Capacitor)v[peak+146+j]+=.06*Math.Exp(-Math.Pow(j/4d,2));}
        }
    }
    [STAThread]static int Main(string[] args)
    {
        Exception failure=null;
        Application.EnableVisualStyles();
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0}) {
            pump.Shown+=async delegate {
                try {
                    string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                    foreach(string mode in new[]{"Pulse","Segment"})await Run(root,mode,false);
                    await Run(root,"Pulse",true);
                    await Run(root,"Pulse",false,true);
                    await Run(root,"Pulse",false,false,true);
                    await Run(root,"Pulse",false,false,false,true);
                    await Run(root,"Segment",false,false,false,true);
                    await Run(root,"Pulse",false,false,false,false,true);
                    await Run(root,"Pulse",false,false,false,false,true,1);
                    await Run(root,"Pulse",false,false,false,false,true,2);
                    await Run(root,"Pulse",false,false,false,false,true,3);
                    await Run(root,"Pulse",false,false,false,false,true,4);
                    await Run(root,"Pulse",false,false,false,false,false,5);
                    foreach(int requested in new[]{1,16,256})await Run(root,"Segment",false,false,false,true,false,0,requested);
                    foreach(int count in new[]{0,1,2})await Run(root,"Segment",false,false,false,true,false,0,16,count);
                    await Run(root,"Segment",false,false,false,true,false,0,16,-1,true);
                    await RunRepeated(root);
                    Console.WriteLine("ACQUISITION_FLOW_PASS="+checks+"; no physical hardware accessed");
                }catch(Exception ex){failure=ex;Console.Error.WriteLine(ex);}
                finally{pump.Close();}
            };
            Application.Run(pump);
        }
        return failure==null?0:1;
    }

    static async Task RunRepeated(string root)
    {
        using(var form=new CalibrationForm(null,true)) {
            var box=new ControlBox(new Io());box.Connect();
            var replay=new Replay{Form=form,Mode="Pulse",Capacitor=true,Repeating=true};
            Field(form,"box",box);Field(form,"client",replay);Field(form,"picoConnected",true);
            ((TextBox)Field(form,"projectPath")).Text=root;
            form.HostCapacitiveLoad=true;form.HostDistanceMode=false;form.HostAverageCount=3;
            Exception reported=null;form.HostErrorReported+=ex=>reported=ex;
            int completed=0;string[] previous=null;
            form.HostCaptureCompleted+=r=>{
                completed++;Check(r.Assessment.CoefficientPcPerV>0&&r.Windows.Length==3,"repeat computes a new finite coefficient from actual three pulses");
                Check(r.Reflection!=null&&!r.Reflection.Valid&&r.Bandwidth==null,"capacitor repeat does not invent geometry/damping");
                Check(previous==null||!r.RawPaths.Intersect(previous).Any(),"repeat never reuses previous waveform as a new measurement");
                previous=r.RawPaths;
            };
            try {
                await (Task)typeof(CalibrationForm).GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                Check(replay.Preflights==2&&completed==1&&reported==null,"first source starts maximum and measures final range");
                int before=replay.Preflights;
                await (Task)typeof(CalibrationForm).GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                Check(replay.Preflights-before==1&&replay.LastPreflightRange==5&&completed==2&&reported==null,"same session/source uses one fresh 40 ms preflight at measured range");
                Console.WriteLine("CALIBRATION_REPEAT_MEASURED=first_preflight_segments:2,first_preflight_requested_ms:80,repeat_preflight_segments:1,repeat_preflight_requested_ms:40,formal_pulse_segments_per_round:3; all source files unique");
                ((NumericUpDown)Field(form,"charge")).Value=5000;
                before=replay.Preflights;
                await (Task)typeof(CalibrationForm).GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                Check(replay.Preflights-before==2&&replay.PreflightRanges[before]==10&&completed==3&&reported==null,"source charge change invalidates range hint and starts maximum");
                replay.NoSignal=true;before=replay.Preflights;
                await (Task)typeof(CalibrationForm).GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                Check(reported!=null&&!form.LastCaptureSucceeded&&completed==3,"missing signal never completes a repeated calibration");
                reported=null;replay.NoSignal=false;before=replay.Preflights;
                await (Task)typeof(CalibrationForm).GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                Check(replay.Preflights-before==2&&replay.PreflightRanges[before]==10&&completed==4&&reported==null,"failed calibration invalidates previous range hint for explicit retry");
            }finally{box.Disconnect();}
        }
    }
    static async Task Run(string root,string mode,bool cancel,bool noSignal=false,bool failCapture=false,bool capacitor=false,bool supplement=false,int supplementChoice=0,int requested=3,int segmentCount=-1,bool segmentTemplate=false)
    {
        using(var form=new CalibrationForm(null,true)) {
            var io=new Io();var box=new ControlBox(io);box.Connect();
            var replay=new Replay{Form=form,Mode=mode,CancelDuringPreflight=cancel,NoSignal=noSignal,FailCapture=failCapture,Capacitor=capacitor,Supplement=supplement,SegmentCount=segmentCount};
            Field(form,"box",box);Field(form,"client",replay);Field(form,"picoConnected",true);
            ((TextBox)Field(form,"projectPath")).Text=root;
            ((NumericUpDown)Field(form,"length")).Value=1240;
            ((NumericUpDown)Field(form,"velocity")).Value=170;
            form.HostAverageCount=requested;form.HostAcquisitionMode=mode;form.HostDistanceMode=true;
            form.HostPhaseCode="B";form.HostLevelCode="TDR";
            if(supplementChoice==4)form.HostScreeningThresholdV=.3;
            if(supplementChoice==5)form.HostScreeningThresholdV=.1;
            form.HostCapacitiveLoad=capacitor;
            int retries=0,excluded=0;
            int segmentExclusionCalls=0;
            if(mode=="Segment") {
                form.SupplementChoiceForTests=(details,canReview)=>{throw new Exception("segment mode must not ask to collect to N");};
                if(segmentTemplate)form.HostPulseExcluded=(wave,peak,rate)=>++segmentExclusionCalls==1;
            }
            if(supplement) {
                HVX.SignalProcessing.NoisePulseTemplate template=null;
                int exclusionCalls=0;
                form.HostPulseExcluded=(wave,peak,rate)=>{
                    if(supplementChoice==4)return false;
                    if(supplementChoice==1) { bool drop=++exclusionCalls<=3;if(drop)excluded++;return drop; }
                    if(template==null)template=new HVX.SignalProcessing.NoisePulseTemplate{Id="offline",FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,SampleRateHz=rate,PeakIndex=peak,Samples=(double[])wave.Clone()};
                    bool match=HVX.SignalProcessing.NoisePulseTemplates.Matches(new[]{template},wave,peak,rate,1);
                    if(match)excluded++;return match;
                };
                form.SupplementChoiceForTests=(details,canReview)=>{
                    retries++;Check(!box.Capturing&&((CalibrationAcquisitionFlow)Field(form,"acquisitionFlow")).Stage==CalibrationAcquisitionStage.Review,"supplement prompt occurs only after capture stopped");
                    Check(details.Contains(supplementChoice==1||supplementChoice==4?"0 / 3":"1 / 3"),"supplement count includes only accepted pulses");
                    Check(canReview==(supplementChoice!=1&&supplementChoice!=4),"empty pool cannot continue a fabricated average");
                    if(supplementChoice==2||supplementChoice==4)return DialogResult.Cancel;
                    if(supplementChoice==3)return DialogResult.Ignore;
                    return retries==1?DialogResult.Retry:DialogResult.Cancel;
                };
            }
            Exception reported=null;
            form.HostErrorReported+=ex=>{reported=ex;Check(!Application.OpenForms.Cast<Form>().Any(f=>f is CalibrationSelfCheckWindow),"Error never stacks over stale self-check");};
            int noise=0,completed=0;
            form.HostNoiseCaptured+=(v,x,dt)=>{noise++;Check(v.Length*dt>=.04,"Every completed preflight is displayed with actual sample interval");Check(completed==0,"Current data visible before calibration/save verdict");if(noSignal)Check(v.All(s=>s==0),"No-signal preview is measured raw data, no synthetic pulse");};
            form.HostCaptureCompleted+=r=>{
                Check(r.PicoCoupling==0&&r.DcRemoval=="full-record-adc-mean-v1","capture retains actual AC and DC removal provenance");
                completed++;Check(!box.Capturing&&!(bool)Field(form,"capturing"),"Sampling ended before quality/save callback");
                if(supplementChoice==5)Check(r.ManualScreeningThreshold&&r.ScreeningThresholdV==.1&&Math.Abs(r.ThresholdV)<.08,"manual screening propagates separately from automatic device trigger");
                Check(((CalibrationAcquisitionFlow)Field(form,"acquisitionFlow")).Stage==CalibrationAcquisitionStage.Review,"Save callback is review-only stage");
                if(supplementChoice==3) {
                    Check(r.QualityOverrideForReview&&r.Windows.Length==1,"continue uses available non-template pulse and retains review-only flag");
                    Check(r.QualityReviewReason.Contains("1 / 3")&&File.Exists(Path.Combine(r.Folder,"quality-review-choice.txt")),"quality override is traceable");
                } else if(mode=="Segment"&&segmentCount>0&&segmentCount<3)
                    Check(!r.Assessment.SaveRecommended&&r.Assessment.CoefficientPcPerV>0&&!Double.IsInfinity(r.Assessment.CoefficientPcPerV)&&!r.QualityOverrideForReview,"few segment pulses retain finite coefficient and honest quality warning without invented override");
                else Check(r.Assessment.SaveRecommended&&(capacitor?!r.Reflection.Valid:r.Reflection.Valid),"Cable retains reflection gate; capacitor uses incident-only quality");
                Check(r.CapacitiveLoad==capacitor,"Capture result retains explicit load kind");
                if(capacitor)Check(r.Bandwidth==null&&r.Reflection.Message.Contains("不进行"),"Capacitor does not attempt reflection bandwidth/propagation");
                int expected=mode=="Segment"?(segmentCount>=0?segmentCount:4)-(segmentTemplate?1:0):(supplementChoice==3?1:3);
                Check(r.RequestedCount==(mode=="Segment"?0:requested)&&r.Assessment.PulseCount==expected&&r.Windows.Length==expected,"reported average count is the actual accepted count, not requested N");
                if(mode=="Segment") {
                    Check(replay.Samples==1,"one measurement segment regardless of N or surviving population");
                    if(segmentTemplate)Check(segmentExclusionCalls==4,"each source window is evaluated once by interference template");
                    for(int index=0;index<r.Average.Length;index+=17)Check(Math.Abs(r.Average[index]-r.Windows.Sum(w=>w[index]/r.Windows.Length))<1e-12,"mean uses all retained windows at matching sample positions");
                    string audit=File.ReadAllText(Path.Combine(r.Folder,"calibration-review.txt"));
                    Check(audit.Contains("本段全部有效脉冲")&&audit.Contains("实际平均次数："+expected)&&!audit.Contains("请求平均次数"),"segment provenance reports actual averaging without fixed N");
                }
                Check(r.RawPaths.Length>=3&&r.RawPaths.All(File.Exists),"Formal result retains all range-check records and actual capture evidence, including single-segment mode");
                if(supplement)Check(retries==1&&excluded==(supplementChoice==1?3:2)&&replay.Samples==(supplementChoice==1?6:supplementChoice==3?3:5)&&r.RawPaths.Distinct().Count()==r.RawPaths.Length&&r.QualityOverrideForReview==(supplementChoice==3),"supplement/review excludes interference, records real counts and never overwrites raw files");
            };
            try {
                await (Task)typeof(CalibrationForm).GetMethod("Acquire",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                int stoppedCount=replay.Samples;
                await Task.Delay(80);
                Check(replay.Samples==stoppedCount&&!box.Capturing,"No automatic reacquisition after completion");
                Check(!Application.OpenForms.Cast<Form>().Any(f=>f is CalibrationSelfCheckWindow),"No residual self-check window");
                if(cancel)Check(replay.Preflights==1&&replay.Samples==0&&completed==0&&noise==0&&!form.LastCaptureSucceeded,"Cancel during preflight does not acquire or save");
                else if(supplementChoice==2||supplementChoice==4)Check(completed==0&&replay.Samples==3&&!form.LastCaptureSucceeded&&reported==null,"stop supplement/high screening threshold retains raw records without completion or automatic restart");
                else if(supplementChoice==3)Check(completed==1&&replay.Samples==3&&!form.LastCaptureSucceeded&&reported==null,"review-only result does not advance automatic multi-level calibration");
                else if(mode=="Segment"&&segmentCount==0)Check(replay.Samples==1&&completed==0&&reported!=null&&!form.LastCaptureSucceeded,"empty segment retained without coefficient, retry or completion");
                else if(noSignal) {
                    Check(noise==replay.Preflights&&noise>=2&&completed==0&&replay.Samples==0&&!form.LastCaptureSucceeded,"No-signal retains all current preflights and never saves coefficients");
                    Check(reported!=null&&reported.Message=="没有检测到校准信号，请核对是否正确接入了视在放电量校准器！","Requested no-calibrator prompt");
                    Check(File.Exists((string)Field(form,"finalPreflightPath")),"No-signal raw evidence saved");
                } else if(failCapture)Check(noise==0&&completed==0&&reported!=null&&reported.Message=="TEST: Pico disconnected","Transport error is not rewritten as absent calibration signal");
                else Check(replay.Preflights==2&&noise==2&&completed==1&&form.LastCaptureSucceeded&&reported==null,"Each preflight preview and exactly one completed waveform");
                if(supplement) {
                    var log=new System.Xml.XmlDocument{XmlResolver=null};
                    log.LoadXml("<events>"+String.Join("",Directory.GetFiles(Path.Combine(root,"Calibration","Logs"),"*.xml.log").Select(File.ReadAllText))+"</events>");
                    var events=log.SelectNodes("/events/event");
                    var last=(System.Xml.XmlElement)events[events.Count-1];
                    string choice=supplementChoice==2||supplementChoice==4?"Cancel":supplementChoice==3?"Ignore":"Retry";
                    Check(last.GetAttribute("phase")=="B"&&last.GetAttribute("level")=="TDR"&&last.GetAttribute("action")=="QualityCaptureChoice"&&last.GetAttribute("outcome")==choice,"retry, continue and stop choices are logged under actual project, phase and level");
                    Check(((System.Xml.XmlElement)events[events.Count-2]).GetAttribute("action")=="QualityCapturePrompt"&&last["detail"].InnerText.Contains("/ 3"),"quality prompt and pulse counts precede each operator choice");
                }
            }finally {box.Disconnect();}
        }
    }
}
