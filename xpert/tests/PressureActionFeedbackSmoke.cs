using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PressureActionFeedbackSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    static object Get(object c,string n){return c.GetType().GetProperty(n,All).GetValue(c,null);}
    static void Set(object c,string n,object v){c.GetType().GetProperty(n,All).SetValue(c,v,null);}
    static object Field(object c,string n){return c.GetType().GetField(n,All).GetValue(c);}
    static void Field(object c,string n,object v){c.GetType().GetField(n,All).SetValue(c,v);}
    static object Call(object c,string n,params object[] a){return c.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(c,a);}
    static void Click(Control button){Check(button.Enabled,"enabled button "+button.Name);Call(button,"OnClick",EventArgs.Empty);Application.DoEvents();}
    static void State(Control c,string state,string post,double actual){Field(c,"postProcessStateCode",post);Call(c,"SetPressureState",state,actual);Application.DoEvents();}
    [STAThread] static int Main(string[] args)
    {
        try {
            double value;string source;
            Check(PressureFeedbackDisplay.DividerOnly("<FactorySettings><DividerOnlyFeedback>true</DividerOnlyFeedback></FactorySettings>")==true,"reads actual request XML DIV selection");
            Check(PressureFeedbackDisplay.DividerOnly("<FactorySettings/>")==null,"missing source is unknown, not app-settings guess");
            string payload="100;2;1;-4;1;2;2;1;1;-4;-4;16;0.5";
            Check(PressureFeedbackDisplay.TryRead(payload,true,out value,out source)&&value==4&&source.Contains("median"),"DIV signed real median magnitude");
            Check(PressureFeedbackDisplay.TryRead(payload,false,out value,out source)&&value==2&&source.Contains("VMON"),"dual-loop lower measured voltage, not arbitrary VMON or DIV");
            Check(!PressureFeedbackDisplay.TryRead(payload,null,out value,out source),"unidentified source never fabricates feedback");
            Check(!PressureFeedbackDisplay.TryRead(payload.Replace("-4","NaN"),true,out value,out source),"malformed/nonfinite sample rejected");
            Check(!PressureFeedbackDisplay.TryRead("99;1",true,out value,out source),"wrong payload rejected");
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
            using(var f=new Form{ClientSize=new Size(1366,768),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-25000,-25000)}) {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"LanguageIndex",1);Set(c,"FrequencyReminderEnabled",false);
                c.Dock=DockStyle.Fill;f.Controls.Add(c);f.Show();Application.DoEvents();
                var button=(Button)Field(c,"pressureActionButton");var record=(Button)Field(c,"deleteButton");
                Call(c,"SetPressurePlan",new[]{0d,1d},new[]{1,3},new[]{0d,8.7});Set(c,"PlanVoltagesArePeak",false);Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetSelectedPlanStep",1);
                Call(c,"SetAutomaticRange",false);string range=(string)Get(c,"SelectedRangeCode");
                Call(c,"SetAdvancedMode",false);Check(!(bool)Get(c,"AdvancedMode")&&(bool)Get(c,"AutomaticRange"),"AUTO implies automatic range");
                Call(c,"SetAutomaticRange",false);Check((bool)Get(c,"AutomaticRange"),"API cannot combine AUTO with manual range");
                Call(c,"SetPressureProgramMode","CUSTOM");Check((bool)Get(c,"AdvancedMode")&&(string)Get(c,"PressureProgramModeCode")=="CUSTOM","custom forces manual pressure");
                Call(c,"SetAdvancedMode",false);Check((bool)Get(c,"AdvancedMode")&&!((Control)Field(c,"normalModeButton")).Enabled,"custom rejects AUTO at property and control boundaries");
                Check((string)Get(c,"SelectedRangeCode")==range,"switching policy never changes physical range");
                Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetAdvancedMode",false);Call(c,"ClearPressurePlan");
                Check((bool)Get(c,"AdvancedMode")&&(string)Get(c,"PressureProgramModeCode")=="CUSTOM","clearing/loading no plan enforces manual custom");
                Call(c,"SetPressurePlan",new[]{0d,1d},new[]{1,3},new[]{0d,8.7});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetSelectedPlanStep",1);
                var grid=(DataGridView)Field(c,"pressurePlanGrid");double target=(double)Get(c,"CalculatedTargetPeakVoltageKv");long seq=(long)Get(c,"VoltageSettingChangedSequence");
                for(int i=0;i<20;i++){Call(c,"SetPlanVoltageDisplayMode","PEAK");Check(grid.Rows[1].Cells[2].Value.ToString()==(8.7*Math.Sqrt(2)).ToString("0.0##",CultureInfo.InvariantCulture),"RMS display converts once "+i);Call(c,"SetPlanVoltageDisplayMode","RMS");}
                Check(grid.Rows[1].Cells[2].Value.ToString()=="8.7"&&(double)Get(c,"CalculatedTargetPeakVoltageKv")==target&&(long)Get(c,"VoltageSettingChangedSequence")==seq,"display toggles no rounding accumulation or target/action changes");
                Check(grid.SelectedRows[0].Index==1&&(int)Get(c,"CurrentPlanRepetition")==1,"display toggle retains selected run");
                Field(c,"requestedTargetPeakVoltageKv",10d);Field(c,"pressureRequestSequence",7L);State(c,"RUNNING","WAITING_FOR_ACQUISITION",Double.NaN);
                Call(c,"ReportPressureVoltageProgress",6L,9d,"DIV");Check(Double.IsNaN((double)Get(c,"CurrentPressureFeedbackKv")),"stale request ignored");
                Call(c,"ReportPressureVoltageProgress",7L,4d,"DIV");Check((double)Get(c,"CurrentPressureFeedbackKv")==4&&button.Text.Contains("4.0 / 10.0")&&(double)Get(button,"MeasurementProgress")==.4,"current real sample updates numeric voltage and bar");
                Check(Double.IsNaN((double)Get(c,"ActualPeakVoltageKv")),"feedback does not overwrite final measured peak");
                Check(grid.SelectedRows[0].Index==(int)Get(c,"SelectedPlanStepIndex"),"active plan row stays highlighted");
                Call(c,"ReportPressureVoltageProgress",7L,11d,"DIV");Check((double)Get(button,"MeasurementProgress")==.95,"target reached not falsely complete");
                State(c,"COMPLETE","SAVE_REQUESTED",9.8);Check((double)Get(button,"MeasurementProgress")<1&&button.Text.Contains("Saving"),"pending persistence not complete");
                State(c,"COMPLETE","COMPLETE",9.8);Check((double)Get(button,"MeasurementProgress")==1&&button.Text.Contains("9.8 / 10.0"),"successful completion full, real number retained");
                State(c,"COMPLETE","SAVE_FAILED",9.8);Check(button.Text.Contains("Save failed")&&!button.Text.Contains("Completed")&&(double)Get(button,"MeasurementProgress")<1,"failed save does not retain a completed title or full bar");
                State(c,"FAULT","ACQUISITION_FAILED",11);Call(c,"ReportPressureVoltageProgress",7L,1d,"DIV");Check((double)Get(button,"MeasurementProgress")<1&&(bool)Get(button,"Danger")&&button.Text.Contains("11.0 / 10.0"),"fault latched against late feedback, never full");
                Field(c,"pressureRequestSequence",8L);Field(c,"requestedTargetPeakVoltageKv",0d);State(c,"RUNNING","WAITING_FOR_ACQUISITION",Double.NaN);
                Check(Double.IsNaN((double)Get(c,"CurrentPressureFeedbackKv")),"new request resets previous feedback identity");
                Call(c,"ReportPressureVoltageProgress",8L,.001d,"DIV");Check(Double.IsNaN((double)Get(button,"MeasurementProgress")),"zero voltage does not divide or fabricate ramp");
                using(var cancel=new CancellationTokenSource()) {
                    Field(c,"zeroHardwareCancellation",cancel);Field(c,"zeroHardwareRunning",true);Call(c,"UpdateActionBusyUi");
                    int revocations=0;c.GetType().GetEvent("FinishCurrentRoundRequested").AddEventHandler(c,(EventHandler)((s,e)=>revocations++));
                    Click(button);Check(cancel.IsCancellationRequested&&revocations==1,"main button routes immediate stop plus authorization revocation");
                    Check((bool)Get(c,"HasActiveHardwareSession")&&(string)Get(c,"PressureStateCode")=="RUNNING"&&(bool)Get(c,"PressureNavigationLocked"),"stop request never invents cleanup or unlocks owner");
                    Check(button.Text.Contains("Stopping")&&(double)Get(button,"MeasurementProgress")!=1,"stop pending visible, not completed");
                    Field(c,"zeroHardwareRunning",false);Field(c,"zeroHardwareCancellation",null);
                }
                State(c,"FAULT","ACQUISITION_FAILED",0);Call(c,"ResetFinishCurrentRound");Call(c,"UpdateActionBusyUi");
                Field(c,"hasAcquisitionResult",true);Field(c,"dataSaved",false);Field(c,"cyclePressureAttempt",false);Field(c,"cycleWaveformSeen",false);
                State(c,"COMPLETE","AWAITING_MANUAL_SAVE",9.8);int saves=0;c.GetType().GetEvent("SaveRequested").AddEventHandler(c,(EventHandler)((s,e)=>saves++));
                Check(record.Text=="Save"&&!((Control)Field(c,"manualSaveButton")).Visible,"single state control is save before persistence");Click(record);
                Check(saves==1&&(string)Get(c,"PostProcessStateCode")=="SAVE_REQUESTED","single save invokes existing transaction");
                Field(c,"dataSaved",true);Field(c,"deleteEnabled",true);State(c,"COMPLETE","COMPLETE",9.8);Check(record.Text=="Delete"&&record.Enabled,"confirmed saved record switches to delete");
                Check(!((Control)Field(c,"pressureMessageLayout")).Visible&&!((Control)Field(c,"pressureMessageDetailsButton")).Visible,"removed detail area never returns");
                Check((long)Get(c,"PressureRequestSequence")==8L,"all synthetic UI scenarios started no hardware request");
            }
            Console.WriteLine("PRESSURE_ACTION_FEEDBACK_PASS="+checks+"; Hardware=false; SDK=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
