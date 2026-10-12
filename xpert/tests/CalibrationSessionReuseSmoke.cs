using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.Controls;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationSessionReuseSmoke
{
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static object Get(object owner,string name){return owner.GetType().GetField(name,Hidden).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Hidden).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args);}
    static async Task Reject(Func<Task> run,string reason){try{await run();}catch{checks++;return;}throw new Exception("Expected rejection: "+reason);}
    sealed class Io:IControlBoxIo
    {
        internal int Opens,Closes,Writes,Reads;
        internal bool High,CloseFailure;
        public void Open(){Opens++;}
        public bool ReadEmergencySignalHigh(){Reads++;return High;}
        public void WriteShortCable(bool value){Writes++;}
        public void Close(){Closes++;if(CloseFailure)throw new IOException("fake retained ART task");}
    }
    sealed class Worker:ICalibrationWorker
    {
        internal int Opens,Closes,Cancels;
        internal bool CloseFailure;
        internal TaskCompletionSource<bool> CloseGate;
        public bool Broken{get{return false;}}
        public Task<Reply> Send(Command c,int timeout){if(c.Op!="OPEN"||c.Config.Serial!="ABCDE/1234")throw new Exception("Fixture permits only exact-identity OPEN, no SDK or capture commands.");Opens++;return Task.FromResult(new Reply{Ok=true,Connected=true,Serial=c.Config.Serial,Model="fake"});}
        public void Cancel(){Cancels++;}
        public async Task Close(int commandTimeout=5000,int exitTimeout=2000){Closes++;if(CloseGate!=null)await CloseGate.Task;if(CloseFailure)throw new IOException("fake retained Pico handle");}
        public Task<bool> RetireUnresponsive(){throw new Exception("No real or fake process retirement allowed.");}
    }
    sealed class Context:IDisposable
    {
        internal readonly Io Io=new Io();internal readonly Worker Worker=new Worker();
        internal readonly CalibrationForm Form=new CalibrationForm(null,true);
        internal readonly CalibrationProgramControl Page=new CalibrationProgramControl();
        internal int Captures,CompletedStops;
        internal Context(string root)
        {
            ((Timer)Get(Form,"safetyTimer")).Stop();Form.HostErrorReported+=e=>{};
            ((TextBox)Get(Form,"projectPath")).Text=root;
            Page.CalibrationHardwareIdentityProvider=()=>new[]{"fake-bound-session","FakeArt","1023417009","ABCDE/1234"};
            Form.HostHardwareIdentityProvider=Page.CalibrationHardwareIdentityProvider;
            Form.HostKeepSessionOpen=true;Form.HostControlBoxForTests=()=>Io;Form.HostWorkerForTests=()=>Worker;
            Form.HostParameterConfirmationForTests=()=>DialogResult.OK;
            Form.HostCaptureForTests=()=>{Captures++;CompletedStops++;return Task.FromResult(true);};
            Set(Page,"activeAcquisition",Form);
        }
        internal Task Capture(double pc=100,bool shortCable=false){return Form.HostAcquireAsync(180,170,pc,shortCable);}
        public void Dispose(){Form.Dispose();Page.Dispose();}
    }
    static async Task ReuseAndRelease(string root)
    {
        using(var c=new Context(root))
        {
            await c.Capture(100);await c.Capture(5000,true);await c.Capture(1000);
            Check(c.Io.Opens==1&&c.Worker.Opens==1,"three successful levels open each device once");
            Check(c.Captures==3&&c.CompletedStops==3&&c.Worker.Closes==0&&c.Io.Closes==0,"complete fake segments do not Close resident devices");
            Check(c.Io.Writes==0,"integrated rounds do not create impedance/output writes");
            Check(c.Form.HostSessionReusable&&c.Page.HasReusableHardwareSession&&c.Page.HasActiveHardwareSession&&!c.Page.IsBusy,"idle owned is reusable not busy, ownership remains visible to host");
            Check(Object.ReferenceEquals(Get(c.Page,"activeAcquisition"),c.Form),"same coordinator retained");
            Check(((TextBox)Get(c.Form,"picoSerial")).Text=="ABCDE/1234","actual OPEN identity pinned for later rounds");
            bool denied=false;try{c.Page.SimulationMode=true;}catch(InvalidOperationException){denied=true;}
            Check(denied&&!c.Page.SimulationMode,"mode cannot switch while live handles remain");
            c.Worker.CloseGate=new TaskCompletionSource<bool>();var release=c.Page.ReleaseHardwareSessionAsync();
            Check(!release.IsCompleted&&c.Page.IsBusy&&!c.Page.HasReusableHardwareSession,"navigation release remains busy until actual acknowledgements");
            Check(!await c.Page.ReleaseHardwareSessionAsync(),"duplicate release cannot race owner");
            c.Worker.CloseGate.SetResult(true);Check(await release,"navigation release completes");
            Check(c.Worker.Closes==1&&c.Io.Closes==1&&!c.Page.HasActiveHardwareSession&&c.Form.IsDisposed,"only explicit release closes and retires coordinator");
        }
    }
    static async Task FailureAndContext(string root)
    {
        using(var c=new Context(root))
        {
            await c.Capture();c.Worker.CloseFailure=true;c.Io.CloseFailure=true;
            await Reject(async()=>{await c.Page.ReleaseHardwareSessionAsync();},"failed release");
            Check(c.Page.IsBusy&&!c.Page.HasReusableHardwareSession&&c.Page.HasActiveHardwareSession&&!c.Form.IsDisposed,"failed close retains owner and forbids capture");
            int captures=c.Captures;await Reject(()=>c.Capture(),"cleanup pending cannot start another round");
            Check(c.Captures==captures&&c.Io.Opens==1&&c.Worker.Opens==1,"failed cleanup cannot auto reconnect");
            c.Worker.CloseFailure=false;c.Io.CloseFailure=false;
            Check(await c.Page.RetryHardwareCleanupAsync()&&!c.Page.IsBusy,"explicit cleanup retry releases remaining handles");
        }
        using(var c=new Context(root))
        {
            await c.Capture();((TextBox)Get(c.Form,"device")).Text="DifferentDevice";
            await Reject(()=>c.Capture(),"identity/configuration drift");
            Check(c.Worker.Closes==1&&c.Io.Closes==1&&c.Captures==1&&c.Worker.Opens==1,"changed configuration closes old session without automatic new Open");
        }
        using(var c=new Context(root))
        {
            await c.Capture();c.Form.HostParameterConfirmationForTests=()=>DialogResult.Cancel;
            await c.Capture(5000,true);
            Check(c.Io.Writes==0&&c.Captures==1&&c.Worker.Closes==1&&c.Io.Closes==1,"cancelled next-level prompt closes resident session without impedance/capture writes");
        }
        using(var c=new Context(root))
        {
            await c.Capture();c.Io.High=true;
            await c.Capture();
            Check(c.Captures==2&&c.Worker.Closes==0&&c.Io.Closes==0&&c.Io.Writes==0&&c.Form.HostSessionReusable,"fresh HIGH remains a valid input observation without closing or output writes");
        }
        using(var c=new Context(root))
        {
            await c.Capture();c.Worker.CloseGate=new TaskCompletionSource<bool>();
            Check(c.Page.RequestHardwareStop(),"explicit stop accepted for normally idle resident session");
            Check(c.Worker.Cancels==1&&c.Worker.Closes==1&&c.Page.IsBusy&&c.Page.HasActiveHardwareSession,"idle stop initiates controlled close but does not claim release early");
            c.Worker.CloseGate.SetResult(true);await Task.Delay(10);
            Check(c.Form.IsDisposed&&!c.Page.HasActiveHardwareSession&&c.Io.Closes==1,"idle stop releases both owners after acknowledgement");
            Check((bool)Get(c.Page,"hostConnectionUnavailable"),"connection-loss stop still requires fresh host connection review");
        }
        using(var c=new Context(root))
        {
            await c.Capture();c.Form.HostCaptureForTests=()=>{c.Form.HostCancel();return Task.FromResult(true);};
            await c.Capture();
            Check(!c.Form.LastCaptureSucceeded&&!c.Form.HostSessionReusable&&!c.Form.HasOwnedHardwareResources&&c.Worker.Closes==1&&c.Io.Closes==1,"stop arriving during a successful callback cannot retain or restart session");
        }
    }
    static async Task RoundCallbacks(string root)
    {
        using(var c=new Context(root))
        {
            // Exercise the actual per-level wrapper, with only acquisition itself injected.
            Set(c.Page,"projectPath",root);Set(c.Page,"hostAcquisitionActive",true);
            Action<double> previous=null;long previousGeneration=0;
            c.Form.HostCaptureForTests=()=>{
                c.Captures++;c.CompletedStops++;
                if(previous!=null){previous(999);Check(((Dictionary<int,double>)Get(c.Page,"automaticNoiseThresholds"))[0]!=999,"old round callback discarded while same coordinator is acquiring next level");}
                previous=c.Form.HostAutomaticThresholdResolved;previous(.25+c.Captures);
                previousGeneration=(long)Get(c.Page,"hardwareSessionGeneration");
                Check((bool)Call(c.Page,"IsCurrentHardwareAcquisition",c.Form,previousGeneration),"current round owner accepted");
                return Task.FromResult(true);
            };
            foreach(string level in new[]{"100pC","5nC"})
            {
                var request=new CalibrationRequestEventArgs(1,"START",0,level,180,170,false,false,0,0,0,"",false,new[]{level});
                Check(await (Task<bool>)Call(c.Page,"AcquireHardwareLevel",request,level),"actual per-level wrapper completed "+level);
                Check(Object.ReferenceEquals(Get(c.Page,"activeAcquisition"),c.Form),"per-level wrapper retains original coordinator");
                Check(Get(c.Form,"HostWaveformCaptured")==null&&Get(c.Form,"HostCaptureCompleted")==null&&c.Form.HostAutomaticThresholdResolved==null,"finished round unsubscribes all retained handlers");
                Check(!(bool)Call(c.Page,"IsCurrentHardwareAcquisition",c.Form,previousGeneration),"finished round generation invalidated immediately");
            }
            Set(c.Page,"hostAcquisitionActive",false);
            Check(c.Captures==2&&c.Io.Opens==1&&c.Worker.Opens==1&&c.Worker.Closes==0,"real wrapper reuses one fake device session across charge levels");
            await c.Page.ReleaseHardwareSessionAsync();
        }
    }
    static async Task IdentitySelection(string root)
    {
        using(var c=new Context(root))
        {
            c.Form.HostHardwareIdentityProvider=null;
            await Reject(()=>c.Capture(),"missing verified selection");
            Check(c.Io.Opens==0&&c.Worker.Opens==0&&c.Io.Writes==0&&c.Captures==0,"missing binding does not use Dev1 or enumerate a Pico");
        }
        foreach(string[] invalid in new[]{new[]{"","FakeArt","1023417009","ABCDE/1234"},new[]{"bound","","1023417009","ABCDE/1234"},
            new[]{"bound","FakeArt","0x3D003711","ABCDE/1234"},new[]{"bound","FakeArt","1023417009",""}})
        using(var c=new Context(root))
        {
            c.Form.HostHardwareIdentityProvider=()=>invalid;
            await Reject(()=>c.Capture(),"incomplete or noncanonical identity");
            Check(c.Io.Opens==0&&c.Worker.Opens==0&&c.Io.Writes==0,"invalid selection never reaches I/O");
        }
        using(var c=new Context(root))
        {
            await c.Capture();
            c.Form.HostHardwareIdentityProvider=()=>new[]{"other-bound-session","DifferentArt","1023417010","FGHIJ/5678"};
            await Reject(()=>c.Capture(),"selected pair changes between rounds");
            Check(c.Io.Closes==1&&c.Worker.Closes==1&&c.Worker.Opens==1&&c.Captures==1,"different pair releases old owner and never auto-selects/reopens new devices");
        }
        using(var c=new Context(root))
        {
            await c.Capture();c.Form.HostHardwareIdentityProvider=()=>null;
            await Reject(()=>c.Capture(),"lost host verification");
            Check(c.Io.Closes==1&&c.Worker.Closes==1&&c.Captures==1,"missing current identity retires idle owner");
        }
        using(var c=new Context(root))
        {
            c.Form.HostParameterConfirmationForTests=()=>{c.Form.HostHardwareIdentityProvider=()=>new[]{"drift","OtherArt","1023417010","FGHIJ/5678"};return DialogResult.OK;};
            await Reject(()=>c.Capture(),"identity changes during confirmation");
            Check(c.Io.Opens==0&&c.Worker.Opens==0&&c.Io.Writes==0&&c.Captures==0,"post-confirmation identity checked before ART open/impedance/Pico");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        Exception failure=null;Application.EnableVisualStyles();
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0})
        {
            pump.Shown+=async delegate{try{string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                await ReuseAndRelease(root);await FailureAndContext(root);await RoundCallbacks(root);await IdentitySelection(root);
                Check((IntPtr)typeof(Art).GetField("module",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==IntPtr.Zero,"ART SDK never loaded");
                Console.WriteLine("CALIBRATION_SESSION_REUSE_PASS="+checks+"; injected I/O/worker/segment only; no SDK or physical capture.");
            }catch(Exception ex){failure=ex;Console.Error.WriteLine(ex);}finally{pump.Close();}};
            Application.Run(pump);
        }
        return failure==null?0:1;
    }
}
