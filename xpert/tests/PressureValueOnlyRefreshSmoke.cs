using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Real WinForms controls with managed synthetic callbacks only. No product Main,
// RequestPressure, acquisition delegate, Worker, native SDK or device access.
internal static class PressureValueOnlyRefreshSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Get(object c,string n){return c.GetType().GetProperty(n,All).GetValue(c,null);}
    static void Set(object c,string n,object v){c.GetType().GetProperty(n,All).SetValue(c,v,null);}
    static object Field(object c,string n){return c.GetType().GetField(n,All).GetValue(c);}
    static void Field(object c,string n,object v){c.GetType().GetField(n,All).SetValue(c,v);}
    static object Call(object c,string n,params object[] a)
    {return c.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length&&m.GetParameters().Select((p,i)=>a[i]==null||p.ParameterType.IsInstanceOfType(a[i])).All(x=>x)).Invoke(c,a);}
    static void Check(bool ok,string why){++checks;if(!ok)throw new Exception(why);}
    static IEnumerable<Control> Tree(Control c){yield return c;foreach(Control child in c.Controls)foreach(var d in Tree(child))yield return d;}
    static void Settle(){for(int i=0;i<3;i++)Application.DoEvents();}
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string module=Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(module,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            var a=Assembly.LoadFrom(Path.Combine(module,"Xiongxin.OWTS.PressureTest.dll"));
            using(var resizedFont=new Font("Microsoft YaHei UI",14f))
            using(var form=new Form{ClientSize=new Size(1366,768),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
            using(var c=(Control)Activator.CreateInstance(a.GetType("HVX.Controls.PressureTestControl",true))) {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"FrequencyReminderEnabled",false);
                c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();Settle();
                Call(c,"SetPressurePlan",new[]{0d,1d,2d},new[]{1,3,2},new[]{0d,10d,20d});
                Set(c,"PlanVoltagesArePeak",true);Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetSelectedPlanStep",1);
                long sequence=83;Field(c,"pressureRequestSequence",sequence);Field(c,"requestedTargetPeakVoltageKv",10d);
                Field(c,"postProcessStateCode","WAITING_FOR_ACQUISITION");Call(c,"SetPressureState","RAMPING",Double.NaN);
                var action=(Control)Field(c,"pressureActionButton");var grid=(DataGridView)Field(c,"pressurePlanGrid");
                var wave=(Control)Field(c,"waveformView");var bottom=(Control)Field(c,"taskInformationLabel");
                int arrangements=0,layouts=0,selection=0,texts=0,enables=0,fillInvalidates=0,waveInvalidates=0;
                foreach(string name in new[]{"arrangePressureBody","arrangeDisplaySettings","arrangePressureActions","arrangePressureSettings","arrangeRealtimeSidebar"}) {
                    var f=c.GetType().GetField(name,All);var original=(Action)f.GetValue(c);
                    f.SetValue(c,(Action)(()=>{++arrangements;original?.Invoke();}));
                }
                foreach(var node in Tree(c)){node.Layout+=(s,e)=>++layouts;node.TextChanged+=(s,e)=>++texts;node.EnabledChanged+=(s,e)=>++enables;}
                grid.SelectionChanged+=(s,e)=>++selection;action.Invalidated+=(s,e)=>++fillInvalidates;wave.Invalidated+=(s,e)=>++waveInvalidates;
                Call(c,"ReportPressureVoltageProgress",sequence,2d,"DIV fake");Settle();
                arrangements=layouts=selection=texts=enables=fillInvalidates=waveInvalidates=0;
                string caption=action.Text;int selected=grid.CurrentCell.RowIndex;
                for(int i=0;i<100;i++){Call(c,"ReportPressureVoltageProgress",sequence,2d,"DIV fake");Call(c,"ReportPressureWorkerStage",sequence,"STEP_HV_UP_RUNNING");}
                Settle();
                Console.WriteLine("IDENTICAL_PROGRESS layout="+layouts+" arrange="+arrangements+" text="+texts+" selection="+selection+" fillInvalidates="+fillInvalidates);
                Check(layouts==0&&arrangements==0&&texts==0&&selection==0&&enables==0&&fillInvalidates==0&&waveInvalidates==0,"100 unchanged progress callbacks must not touch unrelated controls or paint");
                for(int i=0;i<100;i++)Call(c,"ReportPressureVoltageProgress",sequence,2d+i*.05,"DIV fake");Settle();
                Console.WriteLine("CHANGED_PROGRESS layout="+layouts+" arrange="+arrangements+" text="+texts+" selection="+selection+" fillInvalidates="+fillInvalidates);
                Check(layouts==0&&arrangements==0&&texts==0&&selection==0&&enables==0&&waveInvalidates==0,"100 changed progress callbacks only update measured fill");
                Check(fillInvalidates>0&&Math.Abs((double)Get(action,"MeasurementProgress")-.695)<1e-12,"changed real display sample updates actual/target fill");
                Check(action.Text==caption&&grid.CurrentCell.RowIndex==selected&&(long)Get(c,"PressureRequestSequence")==sequence,"display preserves preset, row and request identity");
                Call(c,"ReportPressureWorkerStage",sequence-1,"STEP_HV_UP_PASSED");Check((double)Get(action,"MeasurementProgress")<1,"old PASS does not fill current request");
                Call(c,"ReportPressureWorkerStage",sequence,"STEP_HV_UP_PASSED");Check((double)Get(action,"MeasurementProgress")==1,"request-bound backend PASS fills current request");
                Call(c,"ReportPressureVoltageProgress",sequence-1,9d,"old");Check(Math.Abs((double)Get(c,"CurrentPressureFeedbackKv")-6.95)<1e-12,"old feedback cannot alter current measured voltage");
                selection=0;for(int i=0;i<100;i++)Call(c,"SelectPlanGridRow",selected);Check(selection==0,"same explicit plan row does not clear and reselect");

                Call(c,"SetPressureState","WAITING",Double.NaN);Field(c,"postProcessStateCode","READY");
                Set(c,"UseCompensationCapacitance",true);Set(c,"CompensationCapacitanceUf",.1d);
                Call(c,"ApplyRealtimeMetrics",12.4d,.6234d,50.1234d,.1234d);Settle();
                var labels=new[]{(Label)Field(c,"maxPdValueLabel"),(Label)Field(c,"capacitanceValueLabel"),(Label)Field(c,"frequencyValueLabel"),(Label)Field(c,"tanDeltaValueLabel")};
                int[] metricChanges=new int[4];for(int j=0;j<4;j++){int capture=j;labels[j].TextChanged+=(s,e)=>++metricChanges[capture];}
                waveInvalidates=0;for(int i=0;i<100;i++)Call(c,"ApplyRealtimeMetrics",12.4d,.6234d,50.1234d,.1234d);Settle();
                Check(metricChanges.All(n=>n==0)&&waveInvalidates==0,"100 same metric samples have zero text changes and display-window invalidation");
                Check(labels.Select(x=>x.Text).SequenceEqual(new[]{"12","0.52","50.12","0.12"}),"PD integer and final compensated metrics use two decimals");
                Call(c,"ApplyRealtimeMetrics",13.4d,.7334d,51.2234d,.2334d);Settle();
                Console.WriteLine("METRIC_TEXT_CHANGES="+String.Join(",",metricChanges));
                Check(metricChanges.All(n=>n==1),"each changed metric writes its final text exactly once");
                Check(labels.Select(x=>x.Text).SequenceEqual(new[]{"13","0.63","51.22","0.23"}),"changed metric text has no raw or alternate-format intermediate value");
                Check((double)Get(c,"RawEquivalentCapacitanceUf")==.7334d&&(double)Get(c,"OscillationFrequencyHz")==51.2234d&&(double)Get(c,"RawDissipationFactorPercent")==.2334d,"display formatting does not round stored measurements");
                Call(wave,"SetDisplayWindow","MAXIMUM",51.2234d);waveInvalidates=0;for(int i=0;i<100;i++)Call(wave,"SetDisplayWindow","MAXIMUM",51.2234d);Check(waveInvalidates==0,"same display-window key does not invalidate plot");
                Call(wave,"SetDisplayWindow","ONE",51.2234d);Check(waveInvalidates==1,"real display-window change invalidates plot once");

                Field(c,"backgroundPressureAnalysisStatus","后台分析 A 组");Call(c,"UpdateTaskInformationUi");int bottomChanges=0;bottom.TextChanged+=(s,e)=>++bottomChanges;
                for(int i=0;i<100;i++)Call(c,"UpdateTaskInformationUi");Check(bottomChanges==0,"status bar does not clear base text then reappend background status");
                Field(c,"backgroundPressureAnalysisStatus","后台分析 B 组");Call(c,"UpdateTaskInformationUi");Check(bottomChanges==1&&bottom.Text.Contains("B 组"),"real status changes write complete status once");
                Call(c,"UpdatePostProcessUi");Settle();arrangements=layouts=0;string key=(string)Field(c,"pressureSidebarGeometryInput");
                for(int i=0;i<100;i++)Call(c,"UpdatePostProcessUi");Settle();
                Console.WriteLine("IDENTICAL_POSTPROCESS layout="+layouts+" arrange="+arrangements);
                Check(arrangements==0&&layouts==0&&(string)Field(c,"pressureSidebarGeometryInput")==key,"same post-process state reuses geometry without relayout");
                Field(c,"projectPlanError","changed accessible diagnostic");Call(c,"UpdatePostProcessUi");Check(arrangements==0,"diagnostics-only content does not rearrange page");
                Check(c.Controls.Find("PostProcessHintLabel",true).Single().AccessibleDescription=="changed accessible diagnostic","changed diagnostics remain accessible");
                arrangements=0;Set(c,"LanguageIndex",1);Settle();Check(arrangements>0&&action.Text.Contains("Start pressure"),"language change still applies full layout and real translated state");
                arrangements=0;form.ClientSize=new Size(1120,520);Settle();Check(arrangements>0,"actual resize still recalculates necessary layout");
                arrangements=0;c.Font=resizedFont;Settle();Check(arrangements>0,"font/DPI-representative change still recalculates layout");
                Check((long)Get(c,"PressureRequestSequence")==sequence,"all layout and metric checks issue no pressure request");
                Call(c,"SetPressureState","STOPPED",Double.NaN);Settle();Check(action.Text.Contains("Start pressure")&&!(bool)Get(action,"MeasurementProgressVisible"),"actual state transition still updates complete button presentation");

                // Producer thread executes while the UI is deliberately not pumping.
                // LOW/UNKNOWN must revoke permission synchronously before repaint.
                Set(c,"SimulationMode",false);long source=(long)Call(c,"BeginP04FeedbackSource");
                Call(c,"ReportP04StatusForSource",source,true,true,DateTime.UtcNow);Check((bool)Get(c,"IsHighVoltageReady"),"fresh synthetic HIGH primes permission");
                Exception workerError=null;var thread=new Thread(()=>{try{Call(c,"ReportP04StatusForSource",source,true,false,DateTime.UtcNow);}catch(Exception e){workerError=e;}});thread.Start();Check(thread.Join(2000)&&workerError==null,"LOW producer never waits for UI dispatch");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"LOW revokes permission before UI callback runs");Settle();
                Call(c,"ReportP04StatusForSource",source,true,true,DateTime.UtcNow.AddMilliseconds(1));Check((bool)Get(c,"IsHighVoltageReady"),"new actual HIGH can restore idle permission");
                thread=new Thread(()=>{try{Call(c,"ReportP04StatusForSource",source,false,false,DateTime.MinValue);}catch(Exception e){workerError=e;}});thread.Start();Check(thread.Join(2000)&&workerError==null,"UNKNOWN producer never waits for UI dispatch");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"UNKNOWN revokes permission before UI callback runs");Settle();
                Check((long)Get(c,"PressureRequestSequence")==sequence&&!(bool)Get(c,"ActionPending"),"fake P04 reports do not send output or parameter actions");
            }
            Console.WriteLine("PRESSURE_VALUE_ONLY_REFRESH_PASS="+checks+" | HardwareAccessed=False; SDKCalled=False; ProductEntryStarted=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
