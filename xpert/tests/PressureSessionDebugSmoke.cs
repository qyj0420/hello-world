using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Fake bridge and isolated cadence/log directory only. No SDK or product Main.
internal static class PressureSessionDebugSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly host;static int checks;static long now;static int waited;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object New(string n,params object[] a){return host.GetType("Xiongxin.OWTS.Desktop."+n,true).GetConstructors(F).Single(c=>c.GetParameters().Length==a.Length).Invoke(a);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    public sealed class Page:Control {public void ResetP04FeedbackSession(){} }
    public static class Bridge
    {
        public static bool Owned,Fail;public static int Closes;
        public static bool HasDeviceSession{get{return Owned;}}
        public static void RevokeBatch(){}
        public static Task CloseSessionAsync(){Closes++;if(Fail)return Task.FromException(new IOException("SYNTHETIC_CLOSE_FAILURE"));Owned=false;return Task.CompletedTask;}
    }
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
        host=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));
        var filter=host.GetType("Xiongxin.OWTS.Desktop.PressureDebugForm",true).GetMethod("FilterPolling",F);
        string raw="[INFO] PressureWorker.LINK_ART_NAME - Dev1\r\n[ERROR] PressureWorker.LINK_ART_NAME - FAILED\r\n[INFO] PressureWorker.LINK_PICO_ONLINE - sample\r\n[ERROR] Worker - ORIGINAL_ERROR";
        string visible=(string)filter.Invoke(null,new object[]{raw,false});
        Check(!visible.Contains("Dev1")&&!visible.Contains("sample"),"ordinary polling hidden by default");
        Check(visible.Contains("FAILED")&&visible.Contains("ORIGINAL_ERROR"),"errors never hidden as polling");
        Check((string)filter.Invoke(null,new object[]{raw,true})==raw,"raw debug export remains unfiltered");
        var cadenceType=host.GetType("HVX.Acquisition.PicoCaptureCadence",true);
        var cadence=cadenceType.GetConstructors(F).Single(c=>c.GetParameters().Length==5).Invoke(new object[]{Path.Combine(qa,"cadence"),(Func<long>)(()=>now),1000L,
            (Func<int,CancellationToken,Task>)((ms,ct)=>{ct.ThrowIfCancellationRequested();now+=ms;waited+=ms;return Task.CompletedTask;}),"offline-fixture"});
        cadenceType.GetField("Shared",F).SetValue(null,cadence);
        var log=New("Diagnostics",Path.Combine(qa,"logs"));var settings=New("AppSettings");
        foreach(string mode in new[]{"not-owned","failed-close","successful-close"}){
            string serial="offline-"+mode,path=(string)Call(cadence,"StatePath",serial);
            var enter=(Task)Call(cadence,"EnterAsync",serial,CancellationToken.None,null,null,null);enter.GetAwaiter().GetResult();
            var lease=enter.GetType().GetProperty("Result").GetValue(enter,null);Call(lease,"BeginCapture");((IDisposable)lease).Dispose();
            Check(File.ReadAllText(path).StartsWith("BLOCKED|"),mode+" starts blocked");
            Bridge.Owned=mode!="not-owned";Bridge.Fail=mode=="failed-close";Bridge.Closes=0;
            using(var page=new Page()){
                var session=New("FactoryPressureSession",page,typeof(Bridge),"",settings,qa,New("PressureAnalysisTrial",qa),(Action)(()=>{}),log);
                session.GetType().GetField("CaptureSerial",F).SetValue(session,serial);
                bool failed=false;try{((Task)Call(session,"CloseAsync")).GetAwaiter().GetResult();}catch(IOException){failed=true;}
                Check(failed==Bridge.Fail,mode+" propagates cleanup result");
                Check(Bridge.Closes==1,mode+" close exactly once");
                Check(File.ReadAllText(path).StartsWith(mode=="successful-close"?"RELEASED|":"BLOCKED|"),mode+" never clears without owned successful close");
                if(mode=="successful-close"){
                    int before=waited;var next=(Task)Call(cadence,"EnterAsync",serial,CancellationToken.None,null,null,null);next.GetAwaiter().GetResult();
                    ((IDisposable)next.GetType().GetProperty("Result").GetValue(next,null)).Dispose();Check(waited-before==2000,"recovery retains full cooling interval");
                }
                ((IDisposable)session).Dispose();
            }
        }
        Call(log,"Error","Synthetic", "RAW_ERROR_42");
        Call(log,"Error","Synthetic", "ASYNC_WRAPPER",new AggregateException(new IOException("INNER_NATIVE_ERROR_123")));
        Check(File.ReadAllText((string)log.GetType().GetProperty("CurrentLogPath").GetValue(log,null)).Contains("INNER_NATIVE_ERROR_123"),"original inner error is retained, not only async wrapper");
        using(var form=(Form)New("PressureDebugForm",log,(Func<string>)(()=>"阶段：FAULT / ACQUISITION_FAILED\r\n最近错误：RAW_ERROR_42"),SystemIcons.Application)){
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();
            var sw=System.Diagnostics.Stopwatch.StartNew();
            while(sw.ElapsedMilliseconds<3000&&!((TextBox)Field(form,"logBox")).Text.Contains("RAW_ERROR_42")){Application.DoEvents();Thread.Sleep(10);}
            Check(((TextBox)Field(form,"stateBox")).Text.Contains("FAULT"),"debug stage visible");
            Check(((TextBox)Field(form,"logBox")).Text.Contains("RAW_ERROR_42"),"debug displays original errors from isolated log");
            foreach(string name in new[]{"stateBox","pathBox","logBox"})Check(((TextBox)Field(form,name)).ReadOnly,name+" read only");
            Check(((TextBox)Field(form,"pathBox")).Text.StartsWith(qa),"test never reads user log directory");
            using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));bitmap.Save(Path.Combine(qa,"pressure-debug.png"));}
            form.Close();Check(!((System.Windows.Forms.Timer)Field(form,"timer")).Enabled,"closed debug timer stopped");
        }
        Console.WriteLine("PRESSURE_SESSION_DEBUG_CHECKS="+checks+";HardwareAccessed=false;ProcessesStarted=0;CadenceRoot="+qa);return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
