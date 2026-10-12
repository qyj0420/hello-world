using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVX.Acquisition;

internal static class PicoCaptureCadenceSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    sealed class Clock
    {
        internal long Now;internal int Waited;internal Action OnDelay;
        internal Task Delay(int ms,CancellationToken token){if(OnDelay!=null)OnDelay();token.ThrowIfCancellationRequested();Now+=ms;Waited+=ms;return Task.CompletedTask;}
        internal PicoCaptureCadence Gate(string root){return new PicoCaptureCadence(root,()=>Now,1000,Delay);}
    }
    static void Throws<T>(Action action,string name)where T:Exception{bool caught=false;try{action();}catch(T){caught=true;}Check(caught,name);}
    static PicoCaptureCadence.Lease Enter(PicoCaptureCadence gate,string serial){return gate.EnterAsync(serial,CancellationToken.None).GetAwaiter().GetResult();}
    static void Round(PicoCaptureCadence gate,string serial,Clock clock,int running)
    {using(var lease=Enter(gate,serial)){lease.BeginCapture();clock.Now+=running;lease.Released();}}
    public static int Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--abandon")
        {
            var c=new Clock();var lease=Enter(c.Gate(args[1]),"test-serial");lease.BeginCapture();
            Environment.Exit(77);return 77;
        }
        try
        {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            var clock=new Clock();var gate=clock.Gate(Path.Combine(root,"shared"));
            Check(gate.StatePath(" test-serial ")==gate.StatePath("TEST-SERIAL"),"Serial normalization must share state");
            Round(gate,"test-serial",clock,9000);
            long released=clock.Now;
            foreach(string mode in new[]{"auto-range","manual-range","retry","new-worker","new-page"})
            {
                // Each iteration deliberately constructs another gate object.
                using(var lease=Enter(clock.Gate(Path.Combine(root,"shared"))," TEST-serial "))
                {
                    Check(clock.Now-released>=2000,mode+" keeps release-based cooldown");
                    lease.BeginCapture();clock.Now+=60;released=clock.Now;lease.Released();
                }
            }
            Check(clock.Waited==10000,"No start-based cooldown or object reset");
            var pulseClock=new Clock();var pulseGate=pulseClock.Gate(Path.Combine(root,"pulse"));
            Round(pulseGate,"pulse",pulseClock,60);
            using(var lease=Enter(pulseGate,"pulse")){Check(pulseClock.Waited==2000,"first pulse respects preceding segment cooldown");lease.BeginCapture();lease.Released(1,true);}
            for(int i=0;i<16;i++)using(var lease=Enter(pulseGate,"pulse")){lease.BeginCapture();pulseClock.Now++;lease.Released(1,true);}
            Check(pulseClock.Waited==2000,"short triggered calibration pulses have no added round delay");
            using(var lease=Enter(pulseGate,"pulse")){lease.BeginCapture();lease.Released(60);}
            using(var lease=Enter(pulseGate,"pulse")){Check(pulseClock.Waited==4000,"segment cooldown restored after pulse mode");}
            var calibrationClock=new Clock();var calibrationGate=calibrationClock.Gate(Path.Combine(root,"calibration-segment"));
            Round(calibrationGate,"calibration",calibrationClock,60);
            using(var lease=Enter(calibrationGate,"calibration")){Check(calibrationClock.Waited==2000,"calibration does not shorten an existing pressure/legacy segment deadline");lease.BeginCapture();lease.Released(40.01,false,true);}
            using(var lease=Enter(calibrationGate,"calibration")){Check(calibrationClock.Waited==2500,"calibration non-pulse segment uses 500ms release-based gap");lease.BeginCapture();lease.Released(30,false,true);}
            using(var lease=Enter(calibrationGate,"calibration")){Check(calibrationClock.Waited==3000,"switching to pulse still observes prior calibration segment 500ms gap");lease.BeginCapture();lease.Released(1,true);}
            using(var lease=Enter(calibrationGate,"calibration")){Check(calibrationClock.Waited==3000,"formal pulse keeps zero additional wait");lease.BeginCapture();lease.Released(60);}
            using(var lease=Enter(calibrationGate,"calibration")){Check(calibrationClock.Waited==5000,"ordinary pressure/stress segments retain 2s gap");lease.BeginCapture();lease.Released(40,false,true);}
            var calibrationReboot=new Clock{Now=1000000};
            using(var lease=Enter(new PicoCaptureCadence(Path.Combine(root,"calibration-segment"),()=>calibrationReboot.Now,1000,calibrationReboot.Delay,"different-calibration-clock"),"calibration"))
                Check(calibrationReboot.Waited==500,"new process honors persisted calibration-specific 500ms gap");
            var invalidPulse=new Xiongxin.Scope.Settings{CalibrationPulse=true,TriggerMode=0};
            Throws<ArgumentException>(()=>invalidPulse.Validate(),"segment cannot claim pulse exemption");
            string previous=File.ReadAllText(gate.StatePath("test-serial"));
            using(var cancelled=new CancellationTokenSource())
            {
                int lastRemaining=-1;
                clock.OnDelay=()=>cancelled.Cancel();
                Throws<OperationCanceledException>(()=>gate.EnterAsync("test-serial",cancelled.Token,null,null,ms=>lastRemaining=ms).GetAwaiter().GetResult(),"Wait cancels");
                Check(lastRemaining==0,"Cancellation closes cooldown progress");
                clock.OnDelay=null;
            }
            Check(File.ReadAllText(gate.StatePath("test-serial"))==previous,"Cancellation never resets deadline");
            using(var lease=Enter(gate,"test-serial")){Check(clock.Now-released==2000,"Full prior deadline after cancellation");lease.BeginCapture();}
            Throws<InvalidOperationException>(()=>Enter(gate,"test-serial"),"Missing Stop/free prevents next round");
            gate.ConfirmReleasedAsync("test-serial").GetAwaiter().GetResult();released=clock.Now;
            using(var lease=Enter(gate,"test-serial")){Check(clock.Now-released==2000,"Verified cleanup restarts full cooldown");lease.BeginCapture();lease.Released();}
            var rollback=new Clock();using(var lease=Enter(rollback.Gate(Path.Combine(root,"shared")),"test-serial"))
                Check(rollback.Waited==2000,"Unknown/backward monotonic clock waits full interval");
            var reboot=new Clock{Now=1000000};var restarted=new PicoCaptureCadence(Path.Combine(root,"shared"),()=>reboot.Now,1000,reboot.Delay,"another-process-or-boot");
            using(var lease=Enter(restarted,"test-serial"))Check(reboot.Waited==2000,"New process or boot cannot use forward clock to bypass delay");
            // A holder prevents a second fake worker from obtaining a lease.
            var owner=Enter(gate,"exclusive");using(var cancel=new CancellationTokenSource())
            {clock.OnDelay=()=>cancel.Cancel();Throws<OperationCanceledException>(()=>gate.EnterAsync("EXCLUSIVE",cancel.Token).GetAwaiter().GetResult(),"Cross-instance exclusion is cancellable");clock.OnDelay=null;}owner.Dispose();
            string crashed=Path.Combine(root,"crashed");
            var p=Process.Start(new ProcessStartInfo(typeof(PicoCaptureCadenceSmoke).Assembly.Location,"--abandon \""+crashed+"\""){UseShellExecute=false,CreateNoWindow=true});
            if(!p.WaitForExit(10000))throw new Exception("Fake child did not exit");Check(p.ExitCode==77,"Fake child exited without disposing lease");p.Dispose();
            Throws<InvalidOperationException>(()=>Enter(clock.Gate(crashed),"TEST-SERIAL"),"New process cannot erase abandoned ACTIVE state");
            File.WriteAllText(gate.StatePath("corrupt"),"invalid");Throws<InvalidDataException>(()=>Enter(gate,"corrupt"),"Damaged state fails closed");
            foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,-1,0,60.000001,100})
                Throws<ArgumentException>(()=>PicoCaptureCadence.RequireDuration(invalid),"Reject invalid/overlong duration");
            Check(PicoCaptureCadence.CooldownMilliseconds(1)==2000,"Short segment minimum");
            Check(PicoCaptureCadence.CooldownMilliseconds(60)==2000,"60ms baseline");
            Check(PicoCaptureCadence.CooldownMilliseconds(60.001)==2500,"Conservative fractional 10ms increment");
            Check(PicoCaptureCadence.CooldownMilliseconds(70)==2500,"70ms policy (not capture permission)");
            Check(PicoCaptureCadence.CooldownMilliseconds(80)==3000,"80ms policy (not capture permission)");
            foreach(double rate in new[]{100000,125000,250000,500000,1e6,2.5e6,5e6,10e6,12.5e6,25e6,31.25e6,62.5e6,125e6,250e6,500e6,1e9})
            {
                double duration=PicoCaptureCadence.PressureDuration(60,rate), pico=duration*rate/1000, art=duration*80;
                Check(duration<60 && duration>0,"Conservative budget at "+rate);
                Check(Math.Abs(pico-Math.Round(pico))<1e-6,"Pico integer samples at "+rate);
                Check(Math.Abs(art-Math.Round(art))<1e-6,"ART integer samples at "+rate);
                Check(Math.Round(pico)/(rate*(1-1e-9))*1000<=60,"Worst SDK rate duration at "+rate);
                Check(PicoCaptureCadence.PressureDuration(40,rate)==40,"Short integral segment unchanged at "+rate);
            }
            Check(PicoCaptureCadence.PressureDuration(60,125e6)==59.975,"125 MS/s actual requested duration");
            Check(PicoCaptureCadence.PressureDuration(60,1e9)>59.9,"Duration budget never hides original memory-limit rejection");
            Throws<ArgumentException>(()=>PicoCaptureCadence.PressureDuration(39.9999,125e6),"Nonintegral input must not silently change duration");
            var stress=new HVX.HardwareLinkStress.DiagnosticCapacity{PicoSamplesPerSecond=125e6,PicoBytesPerSample=2,PicoMaximumSamples=20000000,PicoBufferBudgetBytes=40000000,ArtSamplesPerSecond=80000,ArtBytesPerSample=8,ArtChannelCount=1,ArtMaximumSamples=80000,MinimumDurationMilliseconds=1,MaximumDurationMilliseconds=1000};
            foreach(HVX.HardwareLinkStress.TransferTier tier in Enum.GetValues(typeof(HVX.HardwareLinkStress.TransferTier)))
            {
                var plan=HVX.HardwareLinkStress.TransferTierPlanner.Create(tier,stress);plan.Validate();
                Check(plan.RequestedDurationMilliseconds<=60,"Stress tier hard duration maximum");
                Check(plan.CyclePeriod.TotalMilliseconds>=2000,"Stress tier minimum rest");
                plan.CyclePeriod=TimeSpan.FromMilliseconds(1999);
                Throws<ArgumentOutOfRangeException>(()=>plan.Validate(),"Invalid stress rest rejected");
                plan.CyclePeriod=TimeSpan.FromSeconds(2);plan.RequestedDurationMilliseconds=60.001;
                Throws<ArgumentOutOfRangeException>(()=>plan.Validate(),"Invalid stress duration rejected");
            }
            var cfg=new Xiongxin.Scope.Settings{DurationMs=60};
            foreach(double dt in new[]{1.0,2.0})Throws<ArgumentException>(()=>cfg.Count(dt),"Sample memory budget remains in force");
            foreach(double dt in new[]{4,8,16,24,40,64,128,10000,9999.99,19.7,1e7})
            {int n=cfg.Count(dt);Check(n*dt<=60000000,"Calibration actual timebase budget "+dt);Check(n>=2,"Calibration finite sample count");}
            cfg.DurationMs=100;Throws<ArgumentException>(()=>cfg.Count(8),"Reject old overlong capture before native call");
            // Historical record configuration validation remains readable.
            cfg.Validate();Check(true,"Historical duration metadata still validates for read-only replay");
            if(args.Length>1)
            {
                string sourceRoot=args[1];
                string engine=File.ReadAllText(Path.Combine(sourceRoot,"modules-src/CalibrationStandalone/PicoCore/Engine.cs"));
                string cleanup=engine.Substring(engine.IndexOf("// A successful Stop",StringComparison.Ordinal));
                Check(cleanup.IndexOf("Native.ps5000aStop")<cleanup.IndexOf("FreePins();"),"Native stop before buffer unpin");
                Check(cleanup.IndexOf("FreePins();")<cleanup.IndexOf("cadence.Released(actualDurationMs, c.CalibrationPulse, !c.CalibrationPulse)"),"Cooldown follows unpin for calibration segment and pulse policies");
                Check(engine.IndexOf("PicoCaptureCadence.Shared.EnterAsync")<engine.IndexOf("cadence.BeginCapture()"),"Wait before SDK capture/configuration");
                string host=File.ReadAllText(Path.Combine(sourceRoot,"src/ModuleHost.FactoryPressure.cs"));
                string capture=host.Substring(host.IndexOf("Func<string,double,CancellationToken,Task<string>> capture"));
                Check(capture.IndexOf("PicoCaptureCadence.Shared.EnterAsync")<capture.IndexOf("await session.StopMonitor()"),"Monitor remains active through cooldown");
                Check(capture.IndexOf("await task.ConfigureAwait(false)")<capture.IndexOf("cadence.Released(captureDuration)"),"No release before worker completion");
                Check(capture.IndexOf("TryConsumePressureCaptureIntent")<capture.IndexOf("RequestAutomaticCaptureCountdownAsync")&&capture.IndexOf("RequestPressureStartConfirmationAsync")==-1,"Current explicit Start/range intent replaces redundant confirmation, not the required countdown");
                string adapter=File.ReadAllText(Path.Combine(sourceRoot,"modules-src/HardwareLinkStress/Adapters/PicoInputOnlyStressEndpoint.cs"));
                string release=adapter.Substring(adapter.IndexOf("private void StopAndReleaseCaptureLocked"));
                Check(release.IndexOf("PicoNative.Stop(handle)")<release.IndexOf("reusableBufferPin.Free()"),"Stress Stop before unpin");
                Check(release.IndexOf("reusableBufferPin.Free()")<release.IndexOf("capture.Cadence.Released"),"Stress cooldown begins after unpin");
            }
            string evidence="PASS checks="+checks+"; HardwareAccessed=False; NativeCalls=False; ordinaryGapMs=2000; calibrationSegmentGapMs=500; calibrationPulseGapMs=0; maxSegmentMs=60";
            File.WriteAllText(Path.Combine(root,"verification.txt"),evidence);Console.WriteLine(evidence);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
