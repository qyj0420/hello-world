using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Actual managed PressureTest controls; synthetic lifecycle state only. No
// capture backend, product/worker entry, SDK or device operation is configured.
internal static class PressureContinuousDisplayLockSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type type;static int checks,starts;
    static object Call(object o,string n,params object[] a){return type.GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Field(object o,string n){return type.GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){type.GetField(n,F).SetValue(o,v);}
    static object Prop(object o,string n){return type.GetProperty(n,F).GetValue(o,null);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void Refresh(object o){Call(o,"UpdateAdvancedUi");Call(o,"UpdateRangeUi");Call(o,"UpdatePostProcessUi");Call(o,"UpdateActionBusyUi");Application.DoEvents();}
    static void Issued(object o,int repetition,int total)
    {
        Field(o,"advancedMode",false);Field(o,"planMode",true);Field(o,"automaticPressureEnabled",true);
        Field(o,"planMultipliers",new[]{1d});Field(o,"planVoltagesKv",new[]{6.2d});Field(o,"planRepetitions",new[]{total});
        Field(o,"selectedPlanStepIndex",0);Field(o,"currentPlanRepetition",repetition);
        Field(o,"finishCurrentRoundRequested",false);Field(o,"automaticPlanInterrupted",false);Field(o,"pressureEmergencyLatched",false);
        Field(o,"pressureStateCode","RAMPING");Field(o,"postProcessStateCode","WAITING_FOR_ACQUISITION");
        Field(o,"zeroHardwareRunning",true);Field(o,"actionBusy",true);Field(o,"actionPending",true);Field(o,"actionCode","PRESSURE");
        Call(o,"RememberIssuedPressurePlanNoLock");Refresh(o);
    }
    static void Complete(object o)
    {
        Field(o,"zeroHardwareRunning",false);Field(o,"actionBusy",false);Field(o,"actionPending",false);
        Field(o,"pressureStateCode","COMPLETE");Field(o,"postProcessStateCode","COMPLETE");
        Field(o,"hasAcquisitionResult",true);Field(o,"dataSaved",true);Field(o,"dataAnalyzed",true);Field(o,"deleteEnabled",true);
        Refresh(o);
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            string path=Path.GetFullPath(args[0]),dir=Path.GetDirectoryName(path);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            type=Assembly.LoadFrom(path).GetType("HVX.Controls.PressureTestControl",true);
            using(var form=new Form{ClientSize=new Size(1366,900),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
            using(var c=(Control)Activator.CreateInstance(type)){
                c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();Application.DoEvents();
                type.GetProperty("SimulationMode",F).SetValue(c,true,null);
                type.GetEvent("PressureRequested").AddEventHandler(c,(EventHandler)((s,e)=>starts++));
                Check(Field(c,"zeroHardwareCapture")==null&&Field(c,"pressureHardwareCapture")==null,"no hardware or fake acquisition backend configured");
                var parameters=new List<Control>();
                foreach(DictionaryEntry p in (IDictionary)Field(c,"phaseButtons"))parameters.Add((Control)p.Value);
                foreach(string n in new[]{"normalModeButton","automaticRangeButton","manualRangeButton","rangeSelectorButton","deleteButton","automaticPressureCheck"})parameters.Add((Control)Field(c,n));
                var stop=(Control)Field(c,"pressureActionButton");
                Issued(c,1,3);
                Check(parameters.All(p=>!p.Enabled),"phase/range/save/auto controls disabled during actual managed active-round state");
                Check(stop.Enabled&&(bool)Prop(c,"CanStopCurrentPressure"),"Stop remains enabled while hardware owner is active");
                int changed=0;foreach(var p in parameters)p.EnabledChanged+=(s,e)=>changed++;
                long seq=(long)Prop(c,"PressureRequestSequence");string phase=(string)Prop(c,"SelectedPhaseCode"),range=(string)Prop(c,"SelectedRangeCode");
                Complete(c);
                Check(!(bool)Prop(c,"PressureParametersLocked")&&(bool)Prop(c,"PressureControlsDisplayLocked"),"completed round gap holds only presentation lock, not execution permission");
                Check(changed==0&&parameters.All(p=>!p.Enabled),"COMPLETE to next continuation does not flash parameters enabled");
                Check(stop.Enabled&&(bool)Prop(c,"CanFinishCurrentRound"),"Stop-future remains available in completed-round gap");
                for(int i=0;i<25;i++)Refresh(c);
                Check(changed==0,"unchanged polling does not raise EnabledChanged on parameter controls");
                Issued(c,2,3);
                Check(changed==0&&parameters.All(p=>!p.Enabled)&&stop.Enabled,"next repetition preserves control states and available Stop");
                Complete(c);
                Check((bool)Call(c,"RequestFinishCurrentRound"),"explicit stop-future accepted in the gap");Refresh(c);
                Check(!(bool)Prop(c,"PressureControlsDisplayLocked")&&(bool)Prop(c,"AutomaticPressureStopped"),"explicit stop releases display hold and latches future scheduling off");
                Check(parameters.Any(p=>p.Enabled),"permitted idle controls recover after explicit stop");
                Check((string)Prop(c,"SelectedPhaseCode")==phase&&(string)Prop(c,"SelectedRangeCode")==range&&(bool)Prop(c,"AutomaticPressureEnabled"),"display changes preserve phase/range/operator auto-save setting");
                Issued(c,1,3);Complete(c);Field(c,"pressureStateCode","FAULT");Field(c,"postProcessStateCode","CANCELED");Refresh(c);
                Check(!(bool)Prop(c,"PressureControlsDisplayLocked")&&!(bool)Field(c,"continuousPressureDisplayLock"),"real terminal fault releases only presentation hold");
                Issued(c,3,3);Check(!(bool)Field(c,"continuousPressureDisplayLock"),"last scheduled repetition does not retain a nonexistent next-round display hold");
                Complete(c);Check(!(bool)Prop(c,"PressureControlsDisplayLocked")&&parameters.Any(p=>p.Enabled),"last completed repetition restores idle controls");
                Issued(c,1,3);Complete(c);Call(c,"InterruptPressureContinuation");Refresh(c);
                Check(!(bool)Prop(c,"PressureControlsDisplayLocked"),"navigation/context interruption releases display hold");
                Check(starts==0&&(long)Prop(c,"PressureRequestSequence")==seq,"presentation regression never dispatches or automatically resumes pressure");
                Check(Field(c,"zeroHardwareCapture")==null&&Field(c,"pressureHardwareCapture")==null,"no backend configured throughout regression");
                // Do not leave a synthetic active owner for Dispose to process.
                Field(c,"zeroHardwareRunning",false);Field(c,"actionBusy",false);Field(c,"actionPending",false);
            }
            Console.WriteLine("PRESSURE_CONTINUOUS_DISPLAY_PASS="+checks+";ProductStarted=False;SDKCalled=False;ActualWorkerStarted=False;HardwareAccessed=False;BackendConfigured=False;PhysicalAcceptance=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
