using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

// Synthetic UI state only. No driver, product Main, or real pressure request.
internal static class PressurePhasePlanResetSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            var module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1280,800),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var c=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"LanguageIndex",1);Set(c,"MaximumPeakVoltageKv",50d);
                Set(c,"FrequencyReminderEnabled",false);c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();Application.DoEvents();
                Call(c,"SetPressurePlan",new[]{0d,.5d,1d,0d},new[]{1,2,3,1},new[]{0d,4.35,8.7,0d});Call(c,"SetPressureProgramMode","PLAN");
                Call(c,"SetSelectedPlanStep",2);Call(c,"SetCurrentPlanRepetition",3);Call(c,"SetAdvancedMode",false);
                var volts=new[]{0d,8d,2d};var pd=new[]{1d,200d,4d};
                Field(c,"lastVoltageKv",volts);Field(c,"lastDischargePc",pd);Field(c,"dataSaved",true);Field(c,"dataAnalyzed",true);
                Field(c,"hasAcquisitionResult",true);Field(c,"postProcessStateCode","COMPLETE");Field(c,"requestedTargetPeakVoltageKv",8.7*Math.Sqrt(2));
                Call(c,"RememberIssuedPressurePlanNoLock");Call(c,"SetPressureState","COMPLETE",12.1d);
                int phaseEvents=0,pressureEvents=0;var button=(Button)Field(c,"pressureActionButton");var grid=(DataGridView)Field(c,"pressurePlanGrid");
                c.GetType().GetEvent("PhaseChanged").AddEventHandler(c,(EventHandler)((s,e)=>phaseEvents++));
                c.GetType().GetEvent("PressureRequested").AddEventHandler(c,(EventHandler)((s,e)=>pressureEvents++));
                foreach(string phase in new[]{"B","C","A"})
                {
                    Call(c,"SetSelectedPhase",phase);Application.DoEvents();
                    Check((string)Get(c,"SelectedPhaseCode")==phase,"legal idle phase change applied "+phase);
                    Check((int)Get(c,"SelectedPlanStepIndex")==0&&(int)Get(c,"CurrentPlanRepetition")==1,"phase begins at first zero row and repetition one "+phase);
                    Check((double)Get(c,"CalculatedTargetPeakVoltageKv")==0d&&grid.CurrentCell.RowIndex==0,"target and actual selected row synchronized "+phase);
                    Check(button.Text.Contains("Ready")&&button.Text.Contains("0.0 kV")&&button.Text.Contains("1/1")&&!button.Text.Contains("Completed"),"new phase is waiting, not previous completed result "+phase);
                    Check((bool)Field(c,"automaticPlanInterrupted")&&(int)Field(c,"issuedPlanStep")==-1&&(string)Field(c,"issuedPlanPhase")=="","old automatic plan is invalidated "+phase);
                    var continuation=(Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync");
                    Check(continuation.IsCompleted&&!continuation.Result,"old result cannot start next phase automatically "+phase);
                    Check((bool)Get(c,"DataSaved")&&(bool)Get(c,"DataAnalyzed")&&ReferenceEquals(volts,Field(c,"lastVoltageKv"))&&ReferenceEquals(pd,Field(c,"lastDischargePc")),"saved earlier results and waveforms retained "+phase);
                    Call(c,"SetSelectedPlanStep",2);Call(c,"SetCurrentPlanRepetition",2);
                }
                int before=phaseEvents;Call(c,"SetSelectedPhase","A");
                Check(phaseEvents==before&&(int)Get(c,"SelectedPlanStepIndex")==2&&(int)Get(c,"CurrentPlanRepetition")==2,"selecting same phase does not restart plan");
                Call(c,"SetPressureState","RUNNING",3d);Call(c,"SetSelectedPhase","B");
                Check((string)Get(c,"SelectedPhaseCode")=="A"&&(int)Get(c,"SelectedPlanStepIndex")==2&&phaseEvents==before,"active round rejects phase change");
                Call(c,"SetPressureState","COMPLETE",12.1d);Field(c,"postProcessStateCode","SAVE_REQUESTED");Call(c,"SetSelectedPhase","B");
                Check((string)Get(c,"SelectedPhaseCode")=="A"&&phaseEvents==before,"pending save rejects phase change");
                Field(c,"postProcessStateCode","COMPLETE");Field(c,"actionBusy",true);Call(c,"SetSelectedPhase","B");
                Check((string)Get(c,"SelectedPhaseCode")=="A"&&phaseEvents==before,"busy action rejects phase change");Field(c,"actionBusy",false);
                Call(c,"SetPressurePlan",new[]{1d,0d,2d,0d},new[]{2,1,3,1},new[]{8.7,0d,17.4,0d});Call(c,"SetSelectedPlanStep",2);Call(c,"SetSelectedPhase","B");
                Check((int)Get(c,"SelectedPlanStepIndex")==1&&(double)Get(c,"CalculatedTargetPeakVoltageKv")==0d,"nonstandard order selects first existing zero without mutating rows");
                Call(c,"SetPressurePlan",new[]{1d,2d},new[]{2,3},new[]{8.7,17.4});Call(c,"SetSelectedPhase","C");
                Check(((double[])Call(c,"GetPressurePlanVoltagesKv")).SequenceEqual(new[]{8.7,17.4})&&(int)Get(c,"SelectedPlanStepIndex")==0,"no-zero legacy plan retains authored rows, does not invent zero data");
                Call(c,"ClearPressurePlan");Call(c,"SetSelectedPhase","A");
                Check((int)Get(c,"SelectedPlanStepIndex")==-1&&(int)Get(c,"CurrentPlanRepetition")==1&&(string)Get(c,"PressureProgramModeCode")=="CUSTOM","empty custom plan remains empty");
                Check(pressureEvents==0&&(long)Get(c,"PressureRequestSequence")==0,"no phase operation issues acquisition or high voltage");
                form.Close();
            }
            Console.WriteLine("PRESSURE_PHASE_PLAN_RESET_PASS="+checks+"; HARDWARE=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
