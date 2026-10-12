using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Synthetic UI feedback and plan selection only. No RequestPressure, SDK,
// acquisition backend or product Main is invoked.
internal static class PressureCompletedPlanSelectionSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static void SetField(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Choose(DataGridView grid,int row){grid.ClearSelection();grid.CurrentCell=grid.Rows[row].Cells[2];grid.Rows[row].Selected=true;Application.DoEvents();}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var form=new Form{ClientSize=new Size(1366,768),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var c=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"FrequencyReminderEnabled",false);
                Set(c,"LanguageIndex",1);Set(c,"MaximumPeakVoltageKv",50d);Set(c,"PlanVoltagesArePeak",false);
                c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();Application.DoEvents();
                Call(c,"SetPressurePlan",new[]{0d,1d,1d},new[]{1,3,2},new[]{0d,8.7,8.7});Call(c,"SetPressureProgramMode","PLAN");
                Call(c,"SetAdvancedMode",false);
                var grid=(DataGridView)Field(c,"pressurePlanGrid");var button=(Button)Field(c,"pressureActionButton");
                Choose(grid,0);Check((double)Get(c,"CalculatedTargetPeakVoltageKv")==0d,"fixture explicitly selects zero-voltage row before completing it");
                int pressureEvents=0,saveEvents=0;
                c.GetType().GetEvent("PressureRequested").AddEventHandler(c,(EventHandler)((s,e)=>pressureEvents++));
                c.GetType().GetEvent("SaveRequested").AddEventHandler(c,(EventHandler)((s,e)=>saveEvents++));
                var voltage=new[]{0d,.01,-.01};var pd=new[]{1d,2d,3d};
                SetField(c,"lastVoltageKv",voltage);SetField(c,"lastDischargePc",pd);
                SetField(c,"requestedTargetPeakVoltageKv",0d);SetField(c,"postProcessStateCode","COMPLETE");Call(c,"SetPressureState","COMPLETE",.01d);
                Check(button.Text.Contains("Completed")&&button.Text.Contains("0.0 kV")&&(double)Get(c,"ActualPeakVoltageKv")==.01d,"completed zero capture keeps actual result separately from preset caption");
                Choose(grid,1);
                Console.WriteLine("SELECTED_TARGET_BUTTON="+button.Text.Replace(Environment.NewLine," | "));
                Check(button.Text.Contains("Sequence pressure")&&button.Text.Contains("12.3 kV")&&!button.Text.Contains("Completed"),"new plan selection shows mode and its own RMS-converted target");
                Check(!button.Text.Contains(" / ")&&!(bool)Get(button,"MeasurementProgressVisible"),"new target is not labeled with prior actual/requested ratio");
                Check(!(bool)Get(button,"Success"),"unstarted target is not shown as completed success");
                Check(button.Text.Contains("1/3"),"new row repetition belongs to selected plan");
                Check((string)Get(c,"PressureStateCode")=="COMPLETE"&&(double)Get(c,"ActualPeakVoltageKv")==.01d&&(double)Get(c,"RequestedTargetPeakVoltageKv")==0d,"presentation never rewrites prior result or requested peak");
                Check(Object.ReferenceEquals(Field(c,"lastVoltageKv"),voltage)&&Object.ReferenceEquals(Field(c,"lastDischargePc"),pd),"prior raw waveforms remain intact");
                Choose(grid,0);Check(button.Text.Contains("Sequence pressure")&&!button.Text.Contains("Completed"),"returning to old row still needs a new round; no invented completion");
                Choose(grid,1);Call(c,"SetCurrentPlanRepetition",2);
                Check(button.Text.Contains("Sequence pressure")&&button.Text.Contains("2/3"),"repetition selection remains a pending target");
                SetField(c,"requestedTargetPeakVoltageKv",8.7*Math.Sqrt(2));Call(c,"SetPressureState","RUNNING",4d);
                Check(button.Text.Contains("12.3 kV")&&!button.Text.Contains("4.0 /")&&(bool)Get(button,"MeasurementProgressVisible"),"preset caption preserves measured-progress path");
                Call(c,"SetPressureState","COMPLETE",12.2d);
                Check(button.Text.Contains("Completed")&&button.Text.Contains("12.3 kV")&&(double)Get(c,"ActualPeakVoltageKv")==12.2d,"completed feedback retains actual value separately from target caption");
                Choose(grid,2);
                Check(button.Text.Contains("Sequence pressure")&&button.Text.Contains("1/2")&&!button.Text.Contains("Completed"),"equal-voltage different plan row is not a completed round");
                SetField(c,"currentAcquisitionReferencePreview",true);Call(c,"UpdatePressureUi");
                Check(button.Text.Contains("Ready")&&!button.Text.Contains("Preview complete"),"old preview completion cannot override newly selected waiting target");
                SetField(c,"currentAcquisitionReferencePreview",false);
                Call(c,"SetPressureState","FAULT",12.2d);Choose(grid,1);
                Check((bool)Get(button,"Danger")&&button.Text.Contains("Round incomplete"),"plan selection cannot conceal or clear fault status");
                Check((long)Get(c,"PressureRequestSequence")==0&&(long)Get(c,"SaveRequestSequence")==0&&pressureEvents==0&&saveEvents==0,"all selections and feedback remain UI-only without pressure/save requests");
                form.Close();
            }
            Console.WriteLine("PRESSURE_COMPLETED_PLAN_SELECTION_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
