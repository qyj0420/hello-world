using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PressurePreferencesSmoke
{
    static int checks; static Type type; static string qa;
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Get(object v,string n){return v.GetType().GetProperty(n,All).GetValue(v,null);}
    static void Set(object v,string n,object x){v.GetType().GetProperty(n,All).SetValue(v,x,null);}
    static object Call(object v,string n,params object[] args){var m=v.GetType().GetMethods(All).Single(x=>x.Name==n&&x.GetParameters().Length==args.Length&&x.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(y=>y));return m.Invoke(v,args);}
    static object Field(object v,string n){return v.GetType().GetField(n,All).GetValue(v);}
    static void Pump(int ms=40){var w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(w.ElapsedMilliseconds<ms);}
    static void Wait(Task<bool> task){var w=Stopwatch.StartNew();while(!task.IsCompleted&&w.ElapsedMilliseconds<3500)Pump(10);Check(task.IsCompleted,"scheduled task completes");}
    static void Metrics(Control c,double cap,double hz){Call(c,"ApplyRealtimeMetrics",1d,cap,hz,0.01d);Pump();}
    static void Run(Action<Control,Form> test)
    {
        using(var f=new Form{ClientSize=new Size(1280,760),StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(type))using(var feedback=new System.Windows.Forms.Timer{Interval=20})
        {
            c.Dock=DockStyle.Fill;f.Controls.Add(c);Set(c,"EmbeddedMode",true);Set(c,"FrequencyReminderEnabled",false);
            Set(c,"ProjectPath",qa);Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetPressureState","COMPLETE",0d);
            // Synthetic healthy input for timing/preferences only; no SDK call.
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            feedback.Tick+=delegate{if(!c.IsDisposed&&(bool)Get(c,"AutomaticPressureCountdownPending"))Call(c,"ReportP04Status",true,true,DateTime.UtcNow);};
            f.Show();Pump();feedback.Start();try{test(c,f);}finally{feedback.Stop();}f.Close();Pump();
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]);qa=Path.Combine(Path.GetFullPath(args[1]),"pressure-preferences-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            var a=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=a.GetType("HVX.Controls.PressureTestControl",true);
            Run((c,f)=>
            {
                Check((string)Get(c,"FrequencyStatusCode")=="Unknown"&&(string)Get(c,"CapacitanceStatusCode")=="Unknown","initial zero is not green data");
                var frequency=(Label)Field(c,"frequencyValueLabel");var cap=(Label)Field(c,"capacitanceValueLabel");
                Check(frequency.Text=="—"&&cap.Text=="—","missing metrics display dash");
                var tanDelta=(Label)Field(c,"tanDeltaValueLabel");
                var tanCaption=c.Controls.Find("MetricTanDeltaCaption",true).Single();
                Check(tanCaption.Text.Contains("未配置适用判据"),"tan delta explicitly has no applicable criterion");
                Check(tanDelta.Text=="—"&&Double.IsNaN((double)Get(c,"RawDissipationFactorPercent")),"unmeasured tan delta is unknown rather than zero");
                Color neutral=tanDelta.ForeColor;
                foreach(int language in new[]{0,1,18})
                {
                    Set(c,"LanguageIndex",language);
                    Check(tanCaption.Text.Contains(language==1?"Criteria not configured":language==18?"未配置適用判據":"未配置适用判据"),"language change itself retains criterion status");
                    foreach(double value in new[]{.0001,1d,99d,Double.NaN,-.5})
                    {
                        Call(c,"ApplyRealtimeMetrics",1d,.5,50d,value);
                        Check(tanDelta.ForeColor==neutral,"tan delta never fabricates a severity color from magnitude");
                        Check(tanCaption.Text.Contains(language==1?"Criteria not configured":language==18?"未配置適用判據":"未配置适用判据"),"criterion status visible in each supported test language");
                        if(Double.IsNaN(value))Check(tanDelta.Text=="—"&&Double.IsNaN((double)Get(c,"RawDissipationFactorPercent")),"NaN tan delta never displays a measured zero");
                        if(value<0){double displayed;Check(Double.TryParse(tanDelta.Text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out displayed)&&displayed==value&&(double)Get(c,"RawDissipationFactorPercent")==value&&tanCaption.Text.Contains(language==1?"Invalid":language==18?"無效":"无效"),"negative estimate retains displayed/raw signed value and explicit invalid label");}
                    }
                }
                Set(c,"LanguageIndex",0);
                foreach(var x in new[]{new[]{19d,0d},new[]{20d,1d},new[]{500d,1d},new[]{500.001,2d},new[]{850d,2d},new[]{850.001,3d}})
                {Metrics(c,.5,x[0]);Check((string)Get(c,"FrequencyStatusCode")==new[]{"Low","Normal","High","Danger"}[(int)x[1]],"frequency boundary "+x[0]);}
                Color red=frequency.ForeColor;Metrics(c,.5,500);Color green=frequency.ForeColor;Metrics(c,.5,501);Color orange=frequency.ForeColor;
                Check(red!=green&&green!=orange&&red!=orange,"three distinct frequency colors");
                foreach(double value in new[]{.02,8d}){Metrics(c,value,50);Check((string)Get(c,"CapacitanceStatusCode")=="Normal","capacitance inclusive normal boundary "+value+" status="+Get(c,"CapacitanceStatusCode")+" raw="+Get(c,"RawEquivalentCapacitanceUf")+" displayed="+Get(c,"DisplayedEquivalentCapacitanceUf")+" compensation="+Get(c,"UseCompensationCapacitance"));}
                foreach(double value in new[]{.019,8.001}){Metrics(c,value,50);Check((string)Get(c,"CapacitanceStatusCode")=="Warning","capacitance warning boundary "+value);}
                Metrics(c,Double.NaN,Double.NaN);Check((string)Get(c,"FrequencyStatusCode")=="Unknown"&&(string)Get(c,"CapacitanceStatusCode")=="Unknown","invalid metrics are unknown");
                Check(Double.IsNaN((double)Get(c,"RawEquivalentCapacitanceUf")),"unknown raw capacitance remains unknown for recording");
                Check(frequency.ForeColor!=green,"invalid data not green");
                Metrics(c,-.1,50);Check((double)Get(c,"RawEquivalentCapacitanceUf")==-.1&&(string)Get(c,"CapacitanceStatusCode")=="Invalid","invalid negative input preserves forensic raw value");
                Set(c,"FrequencyReminderMaxHz",400d);Set(c,"FrequencyDangerThresholdHz",700d);Metrics(c,.5,401);Check((string)Get(c,"FrequencyStatusCode")=="High","configured upper used");Metrics(c,.5,701);Check((string)Get(c,"FrequencyStatusCode")=="Danger","configured danger used");
                Check(!(bool)Get(c,"UseCompensationCapacitance")&&(double)Get(c,"CompensationCapacitanceUf")==.2,"compensation default off and 200 nF");
                Metrics(c,.5,50);Call(c,"SetUseCompensationCapacitance",true);
                for(int i=0;i<5;i++){Call(c,"UpdateMetricsUi");Check(Math.Abs((double)Get(c,"DisplayedEquivalentCapacitanceUf")-.3)<1e-12,"compensation is applied exactly once");}
                Check((double)Get(c,"EquivalentCapacitanceUf")==.5&&(double)Get(c,"RawEquivalentCapacitanceUf")==.5,"raw and legacy C retained");
                Metrics(c,.1,50);Check((double)Get(c,"DisplayedEquivalentCapacitanceUf")<0&&!(bool)Get(c,"IsDisplayedCapacitanceValid")&&(string)Get(c,"CapacitanceStatusCode")=="Invalid","negative compensation result is invalid not zero");
                Check(cap.Text=="无效","negative result visibly invalid");
                Metrics(c,.5,50);string json=(string)Call(c,"CaptureActionParameters");Check(json.Contains("\"useCompensationCapacitance\":true")&&json.Contains("\"compensationCapacitanceUf\":0.2")&&json.Contains("\"rawCapacitanceUf\":0.5"),"request metadata retains compensation and raw value");
                Call(c,"SetUseCompensationCapacitance",false);Check((double)Get(c,"DisplayedEquivalentCapacitanceUf")==.5,"disable restores raw");
                Call(c,"ClearDisplayData");Check((string)Get(c,"CapacitanceStatusCode")=="Unknown"&&cap.Text=="—","clear resets observed data");
                Check(tanDelta.Text=="—"&&Double.IsNaN((double)Get(c,"RawDissipationFactorPercent")),"clear resets tan delta availability");
            });
            Run((c,f)=>
            {
                int changes=0;type.GetEvent("PressurePreferencesChanged").AddEventHandler(c,new EventHandler((s,e)=>changes++));
                Set(c,"FrequencyReminderEnabled",true);Metrics(c,.5,10);
                var dialog=(Form)Field(c,"frequencyReminderDialog");Check(dialog!=null&&dialog.Visible,"low-frequency reminder shown");
                Check(dialog.StartPosition==FormStartPosition.CenterParent,"warning begins centered");
                Check(((Label)Field(dialog,"message")).Text.Contains("20–500"),"same configured bounds used in reminder");
                Metrics(c,.5,11);Check(ReferenceEquals(dialog,Field(c,"frequencyReminderDialog")),"same excursion does not spam windows");
                ((CheckBox)Field(dialog,"suppress")).Checked=true;((Button)dialog.Controls.Find("ConfirmFrequencyReminder",true)[0]).PerformClick();Pump();
                Check(!(bool)Get(c,"FrequencyReminderEnabled")&&changes==1,"explicit suppression event for persistence");
                Metrics(c,.5,900);Check(Field(c,"frequencyReminderDialog")==null,"suppressed reminder stays closed");
                Set(c,"FrequencyReminderEnabled",true);Metrics(c,.5,900);Check(Field(c,"frequencyReminderDialog")!=null&&changes==1,"settings can restore without persistence loop");
            });
            // The 2026-10-10 user instruction supersedes the pre-plan minimum
            // only; the separate capture-cooldown assertions below remain 2 s.
            Run((c,f)=>{Set(c,"AutomaticPressureCountdownSeconds",0);long before=(long)Get(c,"PressureRequestSequence");var watch=Stopwatch.StartNew();var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check(!task.IsCompleted&&Field(c,"pressureCountdownDialog")!=null,"legacy zero seconds shows minimum countdown");Wait(task);Check(task.Result&&watch.ElapsedMilliseconds>=1000,"legacy zero seconds still waits one full second");Check((long)Get(c,"PressureRequestSequence")==before+1,"only one mailbox request sent");});
            Run((c,f)=>{Set(c,"AutomaticPressureCountdownSeconds",1);Set(c,"AutomaticPressureCountdownEnabled",false);long before=(long)Get(c,"PressureRequestSequence");var watch=Stopwatch.StartNew();var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check(!task.IsCompleted&&Field(c,"pressureCountdownDialog")==null,"explicitly hidden countdown still delays");Wait(task);Check(task.Result&&watch.ElapsedMilliseconds>=1000&&(long)Get(c,"PressureRequestSequence")==before+1,"hidden interval honors minimum one second before one request");Pump(100);Check((long)Get(c,"PressureRequestSequence")==before+1,"no automatic repeated HV loop");});
            foreach(string reason in new[]{"cancel","stop","fault","hide","remove","project","phase-return","voltage","token","dispose"})
                Run((c,f)=>
                {
                    Set(c,"AutomaticPressureCountdownSeconds",1);Set(c,"AutomaticPressureCountdownEnabled",false);var token=new CancellationTokenSource();long before=(long)Get(c,"PressureRequestSequence");
                    var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync",token.Token);Check(!task.IsCompleted,"countdown pending before "+reason);
                    switch(reason){case "cancel":Call(c,"CancelAutomaticPressureScheduling");break;case "stop":Call(c,"SetPressureState","WAITING",0d);break;case "fault":Call(c,"SetPressureState","FAULT",0d);break;case "hide":c.Hide();break;case "remove":f.Controls.Remove(c);break;
                        case "project":Set(c,"ProjectPath",Path.Combine(qa,"other"));Check((string)Get(c,"ProjectPath")==qa,"public project change rejected during countdown");type.GetField("noiseProjectPath",All).SetValue(c,Path.Combine(qa,"other"));break;
                        case "phase-return":Call(c,"SetSelectedPhase","B");Call(c,"SetSelectedPhase","A");Check((string)Get(c,"SelectedPhaseCode")=="A","public phase adjustment rejected during countdown");type.GetField("selectedPhaseCode",All).SetValue(c,"B");Pump(100);type.GetField("selectedPhaseCode",All).SetValue(c,"A");break;
                        case "voltage":Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{100d});Check((double)Get(c,"CalculatedTargetPeakVoltageKv")==0,"public target adjustment rejected during countdown");type.GetField("planVoltagesKv",All).SetValue(c,new[]{100d});break;
                        case "token":token.Cancel();break;case "dispose":c.Dispose();break;}
                    // Private field seams above retain the original stale-context
                    // defense tests; these changes are not allowed user actions.
                    Wait(task);Check(!task.Result&&(long)Get(c,"PressureRequestSequence")==before,"cancelled schedule never requests pressure: "+reason);token.Dispose();
                });
            Run((c,f)=>{Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{100d});Set(c,"MaximumPeakVoltageKv",20d);Set(c,"AutomaticPressureCountdownSeconds",0);var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check(task.IsCompleted&&!task.Result&&(long)Get(c,"PressureRequestSequence")==0,"countdown never bypasses original peak voltage limit");});
            Run((c,f)=>
            {
                Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{5d});Set(c,"MaximumPeakVoltageKv",20d);Set(c,"AutomaticPressureCountdownSeconds",1);
                Call(c,"ReportP04Status",false,false,DateTime.UtcNow);
                var denied=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check(denied.IsCompleted&&!denied.Result&&Field(c,"pressureCountdownDialog")==null,"unknown HV feedback cannot even schedule positive voltage");
                Call(c,"ReportP04Status",true,false,DateTime.UtcNow);denied=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check(denied.IsCompleted&&!denied.Result,"not-started HV rejects positive scheduling");
                Pump(20); // A new high sample must have a later capture time than the blocking low sample.
                Call(c,"ReportP04Status",true,true,DateTime.UtcNow);var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check(!task.IsCompleted,"explicit ready HV permits scheduling");
                Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Check(!task.IsCompleted,"unchanged ready heartbeat does not cancel countdown");
                Call(c,"ReportHighVoltageState",false,false);Wait(task);Check(!task.Result&&(long)Get(c,"PressureRequestSequence")==0,"lost feedback cancels scheduled positive voltage");
            });
            Run((c,f)=>{Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{5d});Set(c,"MaximumPeakVoltageKv",20d);Set(c,"SimulationMode",true);Set(c,"AutomaticPressureCountdownSeconds",0);var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Wait(task);Check(task.Result,"simulation is exempt from live HV feedback only");});
            Run((c,f)=>{Set(c,"AutomaticPressureCountdownSeconds",0);type.GetEvent("PressureRequested").AddEventHandler(c,new EventHandler((s,e)=>{throw new IOException("isolated observer failure");}));var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Wait(task);Check(task.IsFaulted&&task.Exception.ToString().Contains("do not retry automatically")&&(long)Get(c,"PressureRequestSequence")==1,"observer failure cannot become a hanging task or duplicate request");});
            Run((c,f)=>{Set(c,"AutomaticPressureCountdownSeconds",1);var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");var dialog=(Form)Field(c,"pressureCountdownDialog");Check(dialog!=null&&dialog.StartPosition==FormStartPosition.CenterParent,"countdown popup begins centered");((CheckBox)Field(dialog,"suppress")).Checked=true;((Button)dialog.Controls.Find("CancelCountdown",true)[0]).PerformClick();Wait(task);Check(!task.Result&&!(bool)Get(c,"AutomaticPressureCountdownEnabled"),"cancel stops schedule and explicit do-not-remind persists event");});
            Run((c,f)=>{
                int changes=0;type.GetEvent("PressurePreferencesChanged").AddEventHandler(c,new EventHandler((s,e)=>changes++));
                var first=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");var dialog=(Form)Field(c,"pressureCountdownDialog");
                ((CheckBox)Field(dialog,"suppress")).Checked=true;Wait(first);
                Check(first.Result&&changes==1&&!(bool)Get(c,"AutomaticPressureCountdownEnabled"),"elapsed reminder persists explicit suppression once");
                Call(c,"SetPressureState","COMPLETE",0d);var hidden=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");
                Check(!hidden.IsCompleted&&Field(c,"pressureCountdownDialog")==null,"next countdown respects suppression without skipping time");Call(c,"CancelAutomaticPressureScheduling");Wait(hidden);
                Set(c,"AutomaticPressureCountdownEnabled",true);var restored=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");
                Check(Field(c,"pressureCountdownDialog")!=null&&changes==1,"settings checkbox restores reminders without persistence recursion");Call(c,"CancelAutomaticPressureScheduling");Wait(restored);
            });
            Run((c,f)=>{
                Set(c,"AutomaticPressureCountdownSeconds",3);Set(c,"AutomaticPressureCountdownEnabled",false);
                var watch=Stopwatch.StartNew();var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Wait(task);
                Check(task.Result&&watch.ElapsedMilliseconds>=3000,"longer configured countdown is preserved");
            });
            foreach(bool cancel in new[]{false,true})Run((c,f)=>{
                Call(c,"SetAdvancedMode",false);Set(c,"ActionHandshakeEnabled",true);Call(c,"RequestPressure");
                var source=new CancellationTokenSource();type.GetField("zeroHardwareRunning",All).SetValue(c,true);type.GetField("zeroHardwareCancellation",All).SetValue(c,source);
                long sequence=(long)Get(c,"PressureRequestSequence");var watch=Stopwatch.StartNew();
                var task=(Task<bool>)Call(c,"RequestAutomaticCaptureCountdownAsync",source.Token);
                Check(!task.IsCompleted&&Field(c,"pressureCountdownDialog")!=null,"first automatic round has fallback countdown");
                if(cancel)source.Cancel();Wait(task);
                Check(task.Result==!cancel&&(long)Get(c,"PressureRequestSequence")==sequence,"capture countdown never publishes another pressure request");
                if(!cancel){Check(watch.ElapsedMilliseconds>=1000,"first round fallback waits one second");var again=(Task<bool>)Call(c,"RequestAutomaticCaptureCountdownAsync",source.Token);Check(again.IsCompleted&&again.Result&&Field(c,"pressureCountdownDialog")==null,"same request/context reuses completed countdown");
                    Call(c,"ReportCaptureCooldown",2500);var dialog=(Form)Field(c,"captureCooldownDialog");Check(dialog!=null&&((Label)Field(dialog,"message")).Text.Contains("3"),"actual cooldown rounds up remaining seconds");
                    Call(c,"ReportCaptureCooldown",1200);Check(ReferenceEquals(dialog,Field(c,"captureCooldownDialog")),"one cooldown window reused");
                    ((CheckBox)Field(dialog,"suppress")).Checked=true;Call(c,"ReportCaptureCooldown",0);Check(Field(c,"captureCooldownDialog")==null&&!(bool)Get(c,"AutomaticPressureCountdownEnabled"),"cooldown completion closes and remembers suppression");
                    Call(c,"ReportCaptureCooldown",2000);Check(Field(c,"captureCooldownDialog")==null&&(int)Get(c,"CaptureCooldownRemainingMilliseconds")==2000,"hidden cooldown retains actual remaining time");
                    Set(c,"AutomaticPressureCountdownEnabled",true); // Busy setters do not rewrite a running context.
                    type.GetField("automaticPressureCountdownEnabled",All).SetValue(c,true);Call(c,"ReportCaptureCooldown",1000);dialog=(Form)Field(c,"captureCooldownDialog");dialog.Close();Check(source.IsCancellationRequested,"cooldown cancel reaches owned capture token");
                }
                type.GetField("zeroHardwareRunning",All).SetValue(c,false);type.GetField("zeroHardwareCancellation",All).SetValue(c,null);source.Dispose();
            });
            Run((c,f)=>{
                Call(c,"SetAdvancedMode",false);Call(c,"SetPressurePlan",new[]{0d,0d},new[]{1,1},new[]{0d,0d});Call(c,"SetSelectedPlanStep",0);Call(c,"RequestPressure");
                // A completed zero-voltage reference has no nonzero range review
                // or PD analysis. Synthesize only its acknowledged UI result.
                type.GetField("currentAcquisitionReferenceOnly",All).SetValue(c,true);
                type.GetField("hasAcquisitionResult",All).SetValue(c,true);
                type.GetField("postProcessStateCode",All).SetValue(c,"COMPLETE");Call(c,"SetPressureState","COMPLETE",0d);
                Check((bool)Call(c,"MayContinueIssuedPressurePlanNoLock"),"completed reference fixture is eligible for automatic continuation");
                long before=(long)Get(c,"PressureRequestSequence");var task=(Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync");
                Check(!task.IsCompleted&&(bool)Get(c,"AutomaticPressureCountdownPending"),"real plan continuation enters countdown: task="+task.Status+", step="+Get(c,"SelectedPlanStepIndex")+", state="+Get(c,"PressureStateCode")+", post="+Get(c,"PostProcessStateCode")+", busy="+Get(c,"IsBusy")+", target="+Get(c,"CalculatedTargetPeakVoltageKv")+", can="+Call(c,"CanScheduleAutomaticPressure")+", rows="+((double[])Field(c,"planMultipliers")).Length+", cancellation="+Get(c,"LastCountdownCancellationSource"));Wait(task);
                Check(task.Result&&(long)Get(c,"PressureRequestSequence")==before+1,"plan continuation publishes only after countdown");
            });
            foreach(int language in new[]{0,1,18})foreach(float points in new[]{9.5f,15f,20f})Run((c,f)=>
            {
                Set(c,"LanguageIndex",language);Set(c,"FrequencyReminderEnabled",true);
                Font prior=c.Font;
                using(var font=new Font("Microsoft YaHei UI",points))
                {
                    try{
                    c.Font=font;Metrics(c,.5,900);var dialog=(Form)Field(c,"frequencyReminderDialog");dialog.ClientSize=new Size(450,230);Pump();
                    var button=(Button)dialog.Controls.Find("ConfirmFrequencyReminder",true)[0];var check=(CheckBox)Field(dialog,"suppress");var message=(Label)Field(dialog,"message");var viewport=(Panel)dialog.Controls.Find("PressureReminderViewport",true)[0];
                    Check(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)),"popup action always visible "+language+"/"+points);
                    Check(button.Width>=button.GetPreferredSize(Size.Empty).Width&&button.Height>=button.GetPreferredSize(Size.Empty).Height,"popup button caption complete");
                    Check(message.Height>=message.GetPreferredSize(new Size(message.Width,0)).Height,"popup full warning has measured height");
                    int needed=TextRenderer.MeasureText(check.Text,check.Font,new Size(Math.Max(100,check.Width-28),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+4;
                    Check(check.Height>=needed,"popup suppression text wraps completely");viewport.ScrollControlIntoView(check);Pump();
                    using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(qa,"reminder-"+language+"-"+points.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));}
                    Check(viewport.RectangleToScreen(viewport.ClientRectangle).Contains(check.RectangleToScreen(check.ClientRectangle)),"suppression remains reachable at large font "+language+"/"+points+" viewport="+viewport.RectangleToScreen(viewport.ClientRectangle)+" check="+check.RectangleToScreen(check.ClientRectangle)+" scroll="+viewport.AutoScrollPosition+" qa="+qa);
                    button.PerformClick();Pump();
                    }finally{c.Font=prior;}
                }
            });
            Console.WriteLine("PRESSURE_PREFERENCES_PASS="+checks);Console.WriteLine("HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False;EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
