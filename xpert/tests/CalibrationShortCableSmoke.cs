using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.Controls;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationShortCableSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Field(object o,string name){return o.GetType().GetField(name,B).GetValue(o);}
    static void Field(object o,string name,object value){o.GetType().GetField(name,B).SetValue(o,value);}
    static object Call(object o,string name,params object[] values){return o.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(o,values);}
    static void Captions()
    {
        string[] chinese={"建议开启短电缆阻抗；请待采集停止后手动设置。","请待采集停止并确认设备状态后再设置阻抗。","阻抗设置已确认；已有波形保留原采集配置。","阻抗切换未完成，请检查设备状态后重试。"};
        string[] english={"Short-cable impedance is recommended. Set it manually after acquisition has stopped.","Wait for acquisition to stop and the device state to be confirmed before setting impedance.","Impedance setting is confirmed. Existing waveforms retain their original acquisition setting.","Impedance switching is incomplete. Check the device state before retrying."};
        string[] traditional={"建議開啟短電纜阻抗；請待擷取停止後手動設定。","請待擷取停止並確認設備狀態後再設定阻抗。","阻抗設定已確認；已有波形保留原擷取設定。","阻抗切換未完成，請檢查設備狀態後重試。"};
        string[] translatedCodes={"zh-CN","en","bg","hr","cs","fr","de","hu","it","pl","ro","ru","sr","sk","sl","es","tr","zh-TW","pt-BR","sv","nl","es-419","en-GB","de-CH","fr-CH","it-CH","be","uk","pt-PT"};
        using(var control=new CalibrationProgramControl())foreach(string code in translatedCodes) {
            control.SetLanguageCode(code);
            for(int i=0;i<english.Length;i++) {
                string catalog;Check(Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(control.LanguageIndex,english[i],out catalog)&&!String.IsNullOrWhiteSpace(catalog),code+" exact caption available");
                string actual=(string)Call(control,"ShortCableText",chinese[i],english[i]);
                Check(actual==catalog,code+" UI uses catalog without text corruption");
                Check(code=="en"||code=="en-GB"||actual!=english[i],code+" non-English caption is translated");
                if(code=="zh-CN")Check(actual==chinese[i],"simplified exact source");
                if(code=="zh-TW")Check(actual==traditional[i],"traditional exact mapping, not ambiguous conversion");
            }
        }
        using(var control=new CalibrationProgramControl())foreach(string code in new[]{"rm-CH","vi","ja","kk","uz","az","hy","ky","tg","tk","ka"}) {
            control.SetLanguageCode(code);
            for(int i=0;i<english.Length;i++)Check((string)Call(control,"ShortCableText",chinese[i],english[i])==english[i],code+" explicit retained English fallback");
        }
        Check(Xiongxin.OWTS.Localization.UiLanguage.Codes.Length==40,"all existing language selections preserved");
        string[] failureChinese={"设备释放未完成，请检查设备状态后重试。","校准未完成，请检查设备连接和采集设置后重试。"};
        string[] failureEnglish={"Device release is incomplete. Check the device state and retry.","Calibration is incomplete. Check the device connection and acquisition settings, then retry."};
        using(var control=new CalibrationProgramControl()) {
            foreach(string code in new[]{"en","zh-CN","de","fr","es","it","pt-BR","sv","nl"}) {
                control.SetLanguageCode(code);
                for(int i=0;i<2;i++) {
                    string caption;Check(Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(control.LanguageIndex,failureEnglish[i],out caption),code+" short customer failure caption available");
                    Check((string)Call(control,"CalibrationText",failureChinese[i],failureEnglish[i])==caption,code+" customer caption uses existing catalog");
                }
            }
            string diagnostic=null;control.CalibrationEntryDiagnosticSink=value=>diagnostic=value;
            Call(control,"WriteCalibrationDiagnostic","OFFLINE technical details are retained only in diagnostics");
            Check(diagnostic=="OFFLINE technical details are retained only in diagnostics","technical diagnostic sink remains available");
        }
    }
    sealed class Io:IControlBoxIo
    {
        internal int Writes,Opens,Closes;internal bool FailWrite,FailRead;internal bool Last;
        public void Open(){Opens++;}public void Close(){Closes++;}
        public bool ReadEmergencySignalHigh(){if(FailRead)throw new IOException("TEST read failure");return true;}
        public void WriteShortCable(bool on){Writes++;if(FailWrite)throw new IOException("TEST write failure");Last=on;}
    }
    sealed class Worker:ICalibrationWorker
    {
        internal int Calls;
        public bool Broken{get{return false;}}
        public void Cancel(){Calls++;throw new Exception("No worker cancellation authorized by this setting.");}
        public Task Close(int a=5000,int b=2000){Calls++;throw new Exception("No Pico Close authorized by this setting.");}
        public Task<bool> RetireUnresponsive(){Calls++;throw new Exception("No worker retirement authorized by this setting.");}
        public Task<Reply> Send(Command command,int timeout){Calls++;throw new Exception("No Pico command authorized by this setting.");}
    }
    static void Policy()
    {
        double t,r;
        Check(AutomaticShortCablePolicy.ShouldEnable(150,150,1,.90001,false,out t,out r)&&t==2,"approximately two microseconds and strictly over 90 percent");
        Check(!AutomaticShortCablePolicy.ShouldEnable(150,150,1,.9,false,out t,out r),"exactly 90 percent is not over threshold");
        Check(!AutomaticShortCablePolicy.ShouldEnable(150.1,150,1,.99,false,out t,out r),"longer cable is not recommended");
        Check(AutomaticShortCablePolicy.ShouldEnable(50,150,.2,.4,false,out t,out r),"short cable with stronger reflection");
        Check(!AutomaticShortCablePolicy.ShouldEnable(150,150,1,1,true,out t,out r),"capacitor not cable");
        foreach(double bad in new[]{0,-1,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity}) {
            Check(!AutomaticShortCablePolicy.ShouldEnable(bad,150,1,1,false,out t,out r),"invalid length");
            Check(!AutomaticShortCablePolicy.ShouldEnable(100,bad,1,1,false,out t,out r),"invalid velocity");
            Check(!AutomaticShortCablePolicy.ShouldEnable(100,150,bad,1,false,out t,out r),"invalid incident");
            Check(!AutomaticShortCablePolicy.ShouldEnable(100,150,1,bad,false,out t,out r),"invalid reflection");
        }
    }
    static CalibrationForm Coordinator(Io io,Worker worker)
    {
        var f=new CalibrationForm(null,true);var box=new ControlBox(io);box.Connect();
        string[] identity={"UNUSED-SDK-PATH","OfflineDevice","1234","ABCDE/1234"};
        f.HostHardwareIdentityProvider=()=>identity;
        Field(f,"box",box);Field(f,"client",worker);Field(f,"picoConnected",true);Field(f,"hostReusableSession",true);
        Field(f,"hostBoundIdentity",String.Join("|",identity));Field(f,"hostArtSerial",identity[2]);
        ((TextBox)Field(f,"device")).Text=identity[1];((TextBox)Field(f,"picoSerial")).Text=identity[3];
        return f;
    }
    static async Task Ui(string root)
    {
        Directory.CreateDirectory(root);
        var io=new Io();var worker=new Worker();using(var f=Coordinator(io,worker))using(var c=new CalibrationProgramControl()) {
            c.SimulationMode=false;c.ProjectPath=root;c.UseFirstArrivalTiming=false;c.ShowReflectionSelectionNoticeForTests=_=>{};
            c.SetPhaseParameters(-1,150,150,false,false);c.SelectedLevelCode="TDR";
            Field(c,"activeAcquisition",f);
            var samples=new double[1000];
            for(int j=-12;j<=12;j++){samples[250+j]=1-Math.Abs(j)/13.0;samples[450+j]=.96*(1-Math.Abs(j)/13.0);}
            c.SetPhaseWaveform(-1,"TDR",samples,0,1e-8);
            Check(c.SetPhasePacketPeaks(-1,"TDR",250,450),"selected real peak pair accepted");
            Call(c,"ReviewShortCableRecommendation",-1,"TDR");
            Check(c.ShortCableRecommended&&!c.ShortCable&&io.Writes==0,"recommendation never writes or flips the Boolean");
            var original=new CalibrationCaptureResult{ShortCable=false};
            ((IDictionary)Field(c,"liveCaptures"))["-1:TDR"]=original;
            Field(c,"hostAcquisitionActive",true);
            Check(!await c.SetManualShortCableWhenIdleAsync(-1,"TDR",true)&&io.Writes==0&&!c.ShortCable,"active capture blocks explicit setting without output");
            Field(c,"hostAcquisitionActive",false);
            Check(await c.SetManualShortCableWhenIdleAsync(-1,"TDR",true),"explicit idle setting gets real injected receipt");
            Check(c.ShortCable&&io.Last&&io.Writes==1&&((ControlBox)Field(f,"box")).ShortCableVerified,"Boolean follows confirmed write");
            Check(!original.ShortCable,"old capture provenance is not retagged");
            Check(worker.Calls==0,"no Pico Stop, capture, close or worker task");
            Check(await c.SetManualShortCableWhenIdleAsync(-1,"TDR",false)&&!c.ShortCable&&!io.Last&&io.Writes==2,"explicit idle clear uses same owner and truthful Boolean");
            io.FailWrite=true;
            Check(!await c.SetManualShortCableWhenIdleAsync(-1,"TDR",true),"write failure is not success");
            Check(!c.ShortCable&&((ControlBox)Field(f,"box")).CleanupPending&&((ControlBox)Field(f,"box")).Connected&&
                !((ControlBox)Field(f,"box")).ShortCableVerified,"failed write retains owner and prior UI Boolean, not a claim of physical state");
            Check(!await c.SetManualShortCableWhenIdleAsync(-1,"TDR",true)&&io.Writes==3,"failed owner is not silently recreated or retried");
            Check(worker.Calls==0,"failure does not operate Pico or restart pressure");
            Field(c,"activeAcquisition",null);((ControlBox)Field(f,"box")).Disconnect();
        }
        io=new Io();worker=new Worker();using(var f=Coordinator(io,worker)) {
            var box=(ControlBox)Field(f,"box");box.BeginCapture();
            try {await f.HostSetShortCableWhenIdleAsync(true);throw new Exception("Expected active-box rejection.");}
            catch(InvalidOperationException){checks++;}
            Check(io.Writes==0&&!box.ShortCableVerified,"ControlBox enforces capture-idle even if outer UI flag is stale");
            box.EndCapture();long old=box.ImpedanceRevision;
            await f.HostSetShortCableWhenIdleAsync(true);
            Check(box.ImpedanceRevision==old+1&&box.ShortCableVerified,"only confirmed write advances revision");
            try {box.RequireStableImpedance(old);throw new Exception("Expected revision rejection.");}
            catch(IOException){checks++;}
            Check(worker.Calls==0,"idle impedance does not invoke any worker command");box.Disconnect();
        }
    }
    [STAThread]static int Main(string[] args)
    {
        Exception failure=null;Policy();Captions();
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0}) {
            pump.Shown+=async delegate {try{await Ui(Path.GetFullPath(args[0]));}catch(Exception error){failure=error;}finally{pump.Close();}};
            Application.Run(pump);
        }
        if(failure!=null){Console.Error.WriteLine(failure);return 1;}
        typeof(CalibrationArtNativeOwnershipSmoke).GetMethod("Main",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        Check((IntPtr)typeof(Art).GetField("module",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==IntPtr.Zero,"native ART never loaded");
        Console.WriteLine("CALIBRATION_SHORT_CABLE_PASS="+checks);return 0;
    }
}
