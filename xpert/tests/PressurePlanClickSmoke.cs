using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Exercises the grid's SelectionChanged subscriber, not a pressure output.
internal static class PressurePlanClickSmoke
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static object Get(object c,string n){return c.GetType().GetProperty(n,Flags).GetValue(c,null);}
    static void Set(object c,string n,object v){c.GetType().GetProperty(n,Flags).SetValue(c,v,null);}
    static object Field(object c,string n){return c.GetType().GetField(n,Flags).GetValue(c);}
    static void SetField(object c,string n,object v){c.GetType().GetField(n,Flags).SetValue(c,v);}
    static object Call(object c,string n,params object[] a){return c.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(c,a);}
    static void Choose(DataGridView grid,int row){grid.ClearSelection();grid.CurrentCell=grid.Rows[row].Cells[2];grid.Rows[row].Selected=true;Application.DoEvents();}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(args[1]);
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1366,768),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true))) {
                form.Controls.Add(control);control.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                Set(control,"SimulationMode",true);Set(control,"FrequencyReminderEnabled",false);Set(control,"MaximumPeakVoltageKv",50d);Set(control,"PlanVoltagesArePeak",false);
                Call(control,"SetPressurePlan",new[]{0d,.5,1d},new[]{1,2,3},new[]{0d,4.35,8.7});Call(control,"SetPressureProgramMode","PLAN");
                var grid=(DataGridView)Field(control,"pressurePlanGrid");int selections=0,voltageChanges=0;
                control.GetType().GetEvent("ActionPublished").AddEventHandler(control,(EventHandler)((s,e)=>{
                    Check((string)Get(control,"ActionCode")=="PLAN_STEP","row selection publishes only local plan selection");selections++;
                    Check((bool)Call(control,"CompleteAction",(long)Get(control,"ActionSequence"),true,""),"local plan acknowledgement succeeds");
                }));
                control.GetType().GetEvent("VoltageSettingChanged").AddEventHandler(control,(EventHandler)((s,e)=>voltageChanges++));
                Set(control,"ActionHandshakeEnabled",true);Choose(grid,2);
                Check((int)Get(control,"SelectedPlanStepIndex")==2&&grid.SelectedRows[0].Index==2,"grid row selects corresponding plan step");
                Check(Math.Abs((double)Get(control,"CalculatedTargetPeakVoltageKv")-8.7*Math.Sqrt(2))<1e-10,"selected RMS voltage updates actual peak target");
                Check((int)Get(control,"CurrentPlanRepetitionTotal")==3&&(int)Get(control,"CurrentPlanRepetition")==1,"selected row updates count and resets repetition");
                Check(selections==1&&voltageChanges==1&&!((bool)Get(control,"IsBusy")),"one selection notification completes without retained local busy state");
                Choose(grid,0);Check((double)Get(control,"CalculatedTargetPeakVoltageKv")==0d,"zero row selects zero voltage directly");
                Check((long)Get(control,"PressureRequestSequence")==0,"row selection never starts pressure");
                SetField(control,"pressureStateCode","RUNNING");Call(control,"UpdateActionBusyUi");
                Check(!grid.Enabled,"active pressure disables plan grid");Call(control,"SetSelectedPlanStep",2);
                Check((int)Get(control,"SelectedPlanStepIndex")==0,"active pressure rejects changing plan target");
                SetField(control,"pressureStateCode","IDLE");Call(control,"UpdateActionBusyUi");
                Check(grid.Enabled,"idle plan can be selected again");Choose(grid,1);
                Check((int)Get(control,"SelectedPlanStepIndex")==1&&Math.Abs((double)Get(control,"CalculatedTargetPeakVoltageKv")-4.35*Math.Sqrt(2))<1e-10,"later row selection updates target after lock releases");
                Check((long)Get(control,"PressureRequestSequence")==0&&!(bool)Get(control,"IsBusy"),"synthetic grid test leaves no pressure action or busy mailbox");
                form.Close();
            }
            Console.WriteLine("PRESSURE_PLAN_CLICK_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
