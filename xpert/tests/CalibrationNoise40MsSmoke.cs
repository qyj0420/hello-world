using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationNoise40MsSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Field(object o,string name){return o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static void Field(object o,string name,object value){o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,value);}
    static void Reject(Action action,string message){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Check(failed,message);}
    sealed class InputOnly : IControlBoxIo
    {
        internal bool High,Fail;
        internal int Opens,Closes,Reads,OutputWrites;
        public void Open(){Opens++;}
        public void Close(){Closes++;}
        public bool ReadEmergencySignalHigh(){Reads++;if(Fail)throw new IOException("OFFLINE: input read failed");return High;}
        public void WriteShortCable(bool value){OutputWrites++;throw new Exception("Output must not be called");}
    }
    sealed class FakeCapture : ICalibrationWorker
    {
        internal int Commands;
        internal int TruncateBy;
        internal bool WrongRange,WrongCoupling;
        public bool Broken{get{return false;}}
        public void Cancel(){}
        public Task Close(int a=5000,int b=2000){return Task.FromResult(0);}
        public Task<bool> RetireUnresponsive(){return Task.FromResult(false);}
        public Task<Reply> Send(Command command,int timeout)
        {
            Commands++;Check(command.Op=="CAPTURE","Fake accepts only capture; no SDK/worker open or output command");
            Check(command.Config.DurationMs==40,"actual Send command uses fixed 40 ms");
            Check(command.Config.TriggerMode==0&&command.Config.PrePercent==0&&!command.Config.CalibrationPulse,"quality remains finite nonpulse capture");
            Check(command.Config.A.Coupling==CaptureVoltage.AcCoupling,"quality requests actual AC coupling");
            var config=Wire.Decode<Settings>(Wire.Encode(command.Config));
            if(WrongRange)config.A.Range++;
            if(WrongCoupling)config.A.Coupling=1;
            const double intervalNs=100;
            int count=config.Count(intervalNs)-TruncateBy;
            var adc=new short[count];
            for(int i=0;i<count;i++)adc[i]=(short)(100+10*Math.Sin(i*.13));
            var info=new Reply{Ok=true,Connected=true,Config=config,Samples=count,IntervalNs=intervalNs,MaxAdc=32767,Serial="OFFLINE-ONLY",Model="FAKE"};
            Wire.Save(new Capture{Info=info,A=adc},command.DataPath);
            return Task.FromResult(info);
        }
    }
    [STAThread]static int Main(string[] args)
    {
        Exception failure=null;Application.EnableVisualStyles();
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0}) {
            pump.Shown+=async delegate {
                try {
                    Policy();
                    string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                    foreach(bool high in new[]{false,true})foreach(decimal uiDuration in new[]{40m,60m})await Capture(root,high,uiDuration,0,false,false,false);
                    await Capture(root,true,60,1,false,false,false);
                    await Capture(root,false,60,0,true,false,false);
                    await Capture(root,true,60,0,false,true,false);
                    await Capture(root,false,60,0,false,false,true);
                    Console.WriteLine("CALIBRATION_NOISE_40MS_PASS="+checks+"; fake input/capture only; no physical hardware");
                }catch(Exception ex){failure=ex;Console.Error.WriteLine(ex);}
                finally{pump.Close();}
            };
            Application.Run(pump);
        }
        return failure==null?0:1;
    }
    static void Policy()
    {
        foreach(double duration in new[]{.1,30,40,40.01,60}) {
            var original=new Settings{Simulation=false,DurationMs=duration,TriggerMode=1,CalibrationPulse=true,PrePercent=20,A=new ChannelSettings{Enabled=true,Range=5,Coupling=0}};
            var quality=AcquisitionPlanning.QualitySettings(original);
            Check(quality.DurationMs==40&&quality.TriggerMode==0&&!quality.CalibrationPulse&&quality.PrePercent==0,"quality length/purpose independent of requested measurement");
            Check(original.DurationMs==duration&&original.TriggerMode==1&&original.CalibrationPulse,"policy clone never mutates caller measurement settings");
            foreach(double dt in new[]{8.0,100.0,160.0,7.9,32.0000001}) {
                int count=quality.Count(dt);
                var record=new Capture{Info=new Reply{Config=quality,Samples=count,IntervalNs=dt,MaxAdc=32767},A=new short[count]};
                AcquisitionPlanning.RequireQualityRecord(record,quality);Check(true,"40 ms integer and quantized sample budget accepted");
                record.Info.Samples--;record.A=new short[record.Info.Samples];
                Reject(()=>AcquisitionPlanning.RequireQualityRecord(record,quality),"one sample short cannot masquerade as complete 40 ms");
                record.Info.Samples=count+2;record.A=new short[count+2];
                Reject(()=>AcquisitionPlanning.RequireQualityRecord(record,quality),"extra long record cannot masquerade as fixed40 ms");
            }
        }
        var memory=new CalibrationPreflightMemory();var worker=new object();var input=new object();
        Check(memory.StartingRange(worker,input,"context")==10,"new source starts maximum");
        memory.Remember(worker,input,"context",5);
        Check(memory.StartingRange(worker,input,"context")==5,"same live owner/context can retain measured range hint");
        Check(memory.StartingRange(new object(),input,"context")==10,"new worker invalidates hint");
        Check(memory.StartingRange(worker,new object(),"context")==10,"new DI owner invalidates hint");
        Check(memory.StartingRange(worker,input,"changed")==10,"changed source/config invalidates hint");
        memory.Forget();Check(memory.StartingRange(worker,input,"context")==10,"explicit release/failure discards hint");
    }
    static async Task Capture(string root,bool high,decimal uiDuration,int truncate,bool wrongRange,bool wrongCoupling,bool inputFailure)
    {
        var io=new InputOnly{High=high};var box=new ControlBox(io);box.Connect();
        using(var form=new CalibrationForm(null,true)) {
            var fake=new FakeCapture{TruncateBy=truncate,WrongRange=wrongRange,WrongCoupling=wrongCoupling};
            Field(form,"box",box);Field(form,"client",fake);Field(form,"picoConnected",true);
            ((TextBox)Field(form,"projectPath")).Text=root;
            ((NumericUpDown)Field(form,"duration")).Value=uiDuration;
            int previews=0;Exception error=null;
            form.HostNoiseCaptured+=(values,x,dt)=>{previews++;Check(Math.Abs(values.Length*dt-.04)<1e-12,"actual displayed noise record is40 ms");};
            form.HostErrorReported+=ex=>error=ex;
            io.Fail=inputFailure;
            await (Task)typeof(CalibrationForm).GetMethod("CaptureQualityOnly",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
            bool expectedFailure=inputFailure||truncate!=0||wrongRange||wrongCoupling;
            Check((error!=null)==expectedFailure,"real input/record errors remain failures, HIGH/LOW themselves accepted");
            Check(previews==(expectedFailure?0:1),"only complete correctly configured actual40 ms record reaches preview");
            Check(fake.Commands==(inputFailure?0:1),"failed input readiness prevents capture");
            Check(io.OutputWrites==0,"integrated input-only entry never writes short cable/output");
            Check(!box.Capturing&&!(bool)Field(form,"capturing"),"capture state ends on success and failure");
            io.Fail=false;box.Disconnect();
            Check(io.Opens==1&&io.Closes==1,"test owns exactly one fake input lifecycle");
        }
    }
}
