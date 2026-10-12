using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Fake controls/bridge only. No product Main, worker, SDK or user settings.
internal static class PressureP04SourceScopeSmoke
{
    public sealed class Page:Control
    {
        long source;
        public int Calls;public bool Known,High;
        public int Resets;
        public bool AdvancedMode {get;set;}
        public double CalculatedTargetPeakVoltageKv {get;set;}
        public bool HasActiveHardwareSession {get{return false;}}
        public void ResetP04FeedbackSession(){++source;Known=false;Resets++;}
        public long BeginP04FeedbackSource(){return ++source;}
        public void ReportP04StatusForSource(long generation,bool known,bool high,DateTime utc)
        {if(generation!=source)return;Calls++;Known=known;High=high;}
        public void ReportP04Status(bool known,bool high,DateTime utc)
        {throw new Exception("Scoped production callback must not use legacy untimed source.");}
    }
    public static class Bridge
    {
        public static bool HasDeviceSession{get{return false;}}
        public static void RevokeBatch(){}
        public static Task CloseSessionAsync(){return Task.CompletedTask;}
    }
    static int checks;
    static void Check(bool condition,string text){checks++;if(!condition)throw new Exception(text);}
    [STAThread] static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            using(var page=new Page()) {
                var handle=page.Handle;
                var session=new FactoryPressureSession(page,typeof(Bridge),"",new AppSettings(),qa,new PressureAnalysisTrial(qa),()=>{},new Diagnostics(Path.Combine(qa,"logs")));
                try {
                    int disconnected=0;session.ConnectionUnavailable=()=>disconnected++;
                    var zero=session.CreateProgressSource();zero("EMERGENCY_ACTIVE",DateTime.UtcNow.ToString("O")+"|ZeroSessionId=zero");
                    Check(page.Calls==1&&page.Known&&!page.High,"zero LOW accepted as a physical sample");
                    var resident=session.CreateProgressSource();resident("EMERGENCY_RELEASED",DateTime.UtcNow.ToString("O")+"|SessionId=resident");
                    Check(page.Calls==2&&page.Known&&page.High,"new resident sample accepted");
                    foreach(string old in new[]{"EMERGENCY_UNKNOWN","EMERGENCY_ACTIVE","EMERGENCY_RELEASED","PRESSURE_SESSION_UNAVAILABLE","LINK_PICO_ERROR"})zero(old,"old or malformed");
                    Application.DoEvents();
                    Check(page.Calls==2&&page.Known&&page.High&&disconnected==0,"old zero source cannot erase state or fabricate a disconnect");
                    resident("EMERGENCY_UNKNOWN",DateTime.UtcNow.ToString("O"));Application.DoEvents();
                    Check(page.Calls==3&&!page.Known&&disconnected==0,"current unknown denies permission without declaring disconnect");
                    resident("PRESSURE_SESSION_UNAVAILABLE","synthetic terminal transport failure");Application.DoEvents();
                    Check(disconnected==1,"current terminal failure still reaches connection owner");
                    resident("EMERGENCY_RELEASED",DateTime.UtcNow.ToString("O"));
                    Check(page.Calls==4&&page.High,"status recovery is input-only");
                    var pending=new TaskCompletionSource<bool>();
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    typeof(FactoryPressureSession).GetField("monitorInputOnly",flags).SetValue(session,(bool?)false);
                    typeof(FactoryPressureSession).GetField("monitorTask",flags).SetValue(session,pending.Task);
                    int resets=page.Resets;
                    foreach(double target in new[]{8.7,9.6,12.3}){
                        page.CalculatedTargetPeakVoltageKv=target;
                        typeof(FactoryPressureSession).GetMethod("RefreshMonitorMode",flags).Invoke(session,null);
                    }
                    Check(page.Resets==resets&&page.Calls==4&&page.Known&&page.High,"nonzero plan targets preserve the same live DI subscription and current sample");
                    pending.SetResult(true);
                    session.Dispose();resident("EMERGENCY_UNKNOWN",DateTime.UtcNow.ToString("O"));
                    Check(page.Calls==4,"disposed producer callback ignored");
                } finally {session.Dispose();}
            }
            Console.WriteLine("P04_SOURCE_SCOPE_PASS="+checks+";HardwareAccessed=false;ProductMain=false;SDKCalled=false");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
