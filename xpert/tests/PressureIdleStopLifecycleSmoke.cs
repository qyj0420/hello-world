using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Managed state transitions only; never SDK, worker entry or real device.
internal static class PressureIdleStopLifecycleSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static Type type;static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Get(object c,string n){return type.GetProperty(n,All).GetValue(c,null);}
    static void Set(object c,string n,object v){type.GetProperty(n,All).SetValue(c,v,null);}
    static void Field(object c,string n,object v){type.GetField(n,All).SetValue(c,v);}
    static object Field(object c,string n){return type.GetField(n,All).GetValue(c);}
    static object Call(object c,string n,params object[] a){return type.GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(c,a);}
    static void Case(string state,bool emergency,bool owned,bool saving)
    {
        using(var form=new Form{ClientSize=new Size(1366,768),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(type)) {
            form.Controls.Add(c);c.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});
            Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetAdvancedMode",false);
            Field(c,"pressureStateCode","COMPLETE");Field(c,"postProcessStateCode",state);
            Field(c,"dataSaved",false);Field(c,"dataAnalyzed",false);
            Field(c,"zeroHardwareRunning",owned);
            long sequence=(long)Get(c,"ActionSequence"),requests=(long)Get(c,"PressureRequestSequence");
            if(saving){Field(c,"actionBusy",true);Field(c,"actionPending",true);Field(c,"actionCode","PRESSURE");}
            if(emergency)Call(c,"ReportP04Status",true,false,DateTime.UtcNow);
            else {Check((bool)Call(c,"RequestFinishCurrentRound"),"stop accepted for pending round");}
            Application.DoEvents();
            if(owned||saving) {
                Check((bool)Get(c,"PressureNavigationLocked"),"real owner/save still locks navigation");
                Check((string)Get(c,"PostProcessStateCode")==state,"never fabricate device/save completion");
                if(saving)Check((bool)Get(c,"IsBusy")&&(bool)Get(c,"ActionPending"),"no fabricated mailbox ACK");
            } else {
                Check((string)Get(c,"PostProcessStateCode")=="CANCELED","idle range/acquisition decision becomes terminal");
                Check(!(bool)Get(c,"PressureNavigationLocked")&&!(bool)Get(c,"PressureParametersLocked"),"stop permits navigation once real owner ended");
                Check(!(bool)Get(c,"FinishCurrentRoundPending"),"no permanent finish latch busy state");
                Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"unaccepted raw record not promoted");
                Check((bool)Get(c,"AutomaticPressureStopped")||emergency,"future start authorization revoked");
            }
            Check((long)Get(c,"ActionSequence")==sequence&&(long)Get(c,"PressureRequestSequence")==requests,"stop publishes no new pressure request");
            Field(c,"zeroHardwareRunning",false);Field(c,"actionBusy",false);Field(c,"actionPending",false);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            type=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
            Application.EnableVisualStyles();
            foreach(string pending in new[]{"OVERRANGE_DECISION_REQUIRED","WAITING_FOR_ACQUISITION"}) {
                Case(pending,false,false,false);Case(pending,true,false,false);Case(pending,false,true,false);
            }
            Case("SAVE_REQUESTED",false,false,true);
            using(var f=new Form{ClientSize=new Size(1366,768),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
            using(var c=(Control)Activator.CreateInstance(type)) {
                f.Controls.Add(c);c.Dock=DockStyle.Fill;f.Show();Application.DoEvents();Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
                var status=(Control)Field(c,"highVoltageLabel");
                Check(status.ForeColor==Color.White,"emergency status white text");
                Check(status.GetType().Name=="PressureEmergencyStatusLabel","rounded emergency status renderer");
                var button=(Control)Field(c,"pressureActionButton");
                Check(button.Height>=111,"primary button 20 percent taller baseline");
                Check(button.Text.StartsWith("开始加压"),"idle simple pressure title");
                Set(c,"BackgroundPressureAnalysisEnabled",true);
                Call(c,"ReportBackgroundPressureAnalysis","offline-id","A 12.3 kV #3",1,7,2,"");
                Check(((Control)Field(c,"taskInformationLabel")).Text.Contains("A 12.3 kV #3"),"background group in bottom status");
                Check(!button.Text.Contains("分析")&&!button.Text.Contains("收尾"),"no analysis/finalization in pressure control");
            }
            Console.WriteLine("PASS "+checks+" | HardwareAccessed=False; UserProjectsWritten=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
