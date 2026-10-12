using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Synthetic controls and command mailbox only. No host, worker, native SDK,
// capture delegate, connection, or physical output is instantiated.
internal static class PressureCustomVoltageMinimumSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type type;
    static int checks;
    static void Check(bool condition,string message){++checks;if(!condition)throw new InvalidOperationException(message);}
    static object Get(object c,string name){return type.GetProperty(name,All).GetValue(c,null);}
    static void Set(object c,string name,object value){type.GetProperty(name,All).SetValue(c,value,null);}
    static object Field(object c,string name){return type.GetField(name,All).GetValue(c);}
    static void Field(object c,string name,object value){type.GetField(name,All).SetValue(c,value);}
    static object Call(object c,string name,params object[] args)
    {return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(c,args);}
    static Control Create()
    {
        var c=(Control)Activator.CreateInstance(type);
        Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"FrequencyReminderEnabled",false);
        Set(c,"MaximumPeakVoltageKv",30d);Set(c,"BaseU0VoltageKv",8.7d);
        Call(c,"SetPressureProgramMode","CUSTOM");Call(c,"SetAdvancedMode",true);
        return c;
    }
    static void Modes()
    {
        using(var c=Create())
        {
            int requests=0;type.GetEvent("PressureRequested").AddEventHandler(c,(EventHandler)((s,e)=>++requests));
            foreach(string mode in new[]{"PEAK","RMS","U0"})
            {
                Call(c,"SetCustomVoltageMode",mode);
                double factor=mode=="PEAK"?1d:Math.Sqrt(2d)*(mode=="U0"?8.7d:1d);
                foreach(double raw in new[]{.0001,.1/factor,.5/factor,.999/factor})
                {
                    Call(c,"SetCustomVoltageValue",raw);
                    Check(Math.Abs((double)Get(c,"CalculatedTargetPeakVoltageKv")-1d)<1e-12,"positive input uses 1 kV peak minimum: "+mode);
                    Check(Math.Abs((double)Get(c,"CustomVoltageValue")-1d/factor)<1e-12,"minimum converted in active units: "+mode);
                    Call(c,"ChangeCustomVoltage",-.1d);
                    Check((double)Get(c,"CalculatedTargetPeakVoltageKv")>=1d-1e-12,"decrease cannot pass positive peak floor: "+mode);
                }
                var editor=(Control)Field(c,"customVoltageValueButton");
                string displayed=editor.Text.Split(new[]{"U0"," kV"},StringSplitOptions.None)[0];
                double actualValue=(double)Get(c,"CustomVoltageValue");
                Check(displayed==actualValue.ToString("0.0",CultureInfo.InvariantCulture),"all voltage modes show exactly one decimal: "+mode);
                Check(Math.Abs((double)Get(c,"CalculatedTargetPeakVoltageKv")-1d)<1e-12,"display rounding does not alter the exact converted peak: "+mode);
                Call(c,"BeginCustomVoltageEdit");
                ((TextBox)Field(c,"customVoltageInlineEditor")).Text="0.5";
                Call(c,"CommitCustomVoltageEditor");
                Check((double)Get(c,"CalculatedTargetPeakVoltageKv")>=1d-1e-12,"inline commit shares same floor: "+mode);
                Call(c,"BeginCustomVoltageEdit");
                ((TextBox)Field(c,"customVoltageInlineEditor")).Text="0.0001";
                Call(c,"CommitCustomVoltageEditor");
                Check(Math.Abs((double)Get(c,"CalculatedTargetPeakVoltageKv")-1d)<1e-12,"tiny positive inline value cannot round into zero reference: "+mode);
                double unedited=(double)Get(c,"CustomVoltageValue");
                long voltageSequence=(long)Field(c,"voltageSettingChangedSequence");
                Call(c,"BeginCustomVoltageEdit");Call(c,"CommitCustomVoltageEditor");
                Check((double)Get(c,"CustomVoltageValue")==unedited&&(long)Field(c,"voltageSettingChangedSequence")==voltageSequence,
                    "opening/closing unedited converted minimum retains complete precision and emits no refresh: "+mode);
                Call(c,"SetCustomVoltageValue",0d);
                Check((double)Get(c,"CalculatedTargetPeakVoltageKv")==0,"explicit reference zero is never raised: "+mode);
                Call(c,"ChangeCustomVoltage",-.1d);
                Check((double)Get(c,"CalculatedTargetPeakVoltageKv")==0,"zero decrease never creates output: "+mode);
                Call(c,"ChangeCustomVoltage",.1d);
                Check((double)Get(c,"CalculatedTargetPeakVoltageKv")>=1d-1e-12,"first positive step respects peak floor: "+mode);
                Set(c,"MaximumPeakVoltageKv",1d);Call(c,"SetCustomVoltageValue",20d);
                Check((double)Get(c,"CalculatedTargetPeakVoltageKv")>=1d-1e-12&&(double)Get(c,"CalculatedTargetPeakVoltageKv")<=1d+1e-12,"1 kV hard limit and converted floor coexist: "+mode);
                Set(c,"MaximumPeakVoltageKv",.5d);Call(c,"SetCustomVoltageValue",.1d);
                Check((double)Get(c,"CustomVoltageValue")>0,"too-low hard limit does not silently turn positive request into zero: "+mode);
                long prior=(long)Get(c,"PressureRequestSequence");Call(c,"RequestPressure");
                Check((long)Get(c,"PressureRequestSequence")==prior&&requests==0,"too-low configured maximum blocks publication: "+mode);
                Set(c,"MaximumPeakVoltageKv",30d);
            }
            Call(c,"SetCustomVoltageMode","U0");Call(c,"SetCustomVoltageValue",.5d);Set(c,"BaseU0VoltageKv",.1d);
            Check(Math.Abs((double)Get(c,"CalculatedTargetPeakVoltageKv")-1d)<1e-12,"changing U0 revalidates current custom value");
            Check(requests==0,"all input and layout changes publish no pressure requests");
        }
    }
    static void Submission()
    {
        foreach(string mode in new[]{"PEAK","RMS","U0"})using(var c=Create())
        {
            Call(c,"SetCustomVoltageMode",mode);
            double factor=mode=="PEAK"?1d:Math.Sqrt(2d)*(mode=="U0"?8.7d:1d);
            // Bypass the editor to model stale persisted/internal data. The
            // request must reject it rather than silently publishing zero.
            Field(c,"customVoltageValue",.7/factor);
            int requests=0;type.GetEvent("PressureRequested").AddEventHandler(c,(EventHandler)((s,e)=>++requests));
            Call(c,"RequestPressure");
            Check(requests==0&&(long)Get(c,"PressureRequestSequence")==0&&!(bool)Get(c,"IsBusy"),"submit rejects stale subminimum target: "+mode);
            Check(((string)Get(c,"LastActionCode")).Contains("BLOCKED"),"submit has explicit blocked status: "+mode);
        }
        using(var c=Create())
        {
            int requests=0;type.GetEvent("PressureRequested").AddEventHandler(c,(EventHandler)((s,e)=>++requests));
            Call(c,"SetPressurePlan",new[]{0d,1d},new[]{1,1},new[]{0d,8.7d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetSelectedPlanStep",0);
            Set(c,"MaximumPeakVoltageKv",0d);
            Check((double)Get(c,"CalculatedTargetPeakVoltageKv")==0,"planned noise reference remains exactly zero");
            Call(c,"RequestPressure");
            Check(requests==1&&(double)Get(c,"RequestedTargetPeakVoltageKv")==0,"zero reference mailbox is not coerced to 1 kV; no host subscribed");
        }
    }
    static void SaveWidth()
    {
        using(var f=new Form{ClientSize=new Size(1366,768),ShowInTaskbar=false,Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual})
        using(var c=Create())
        {
            c.Dock=DockStyle.Fill;f.Controls.Add(c);f.Show();Application.DoEvents();
            var save=(Control)Field(c,"deleteButton");var automatic=(Control)Field(c,"automaticPressureCheck");
            foreach(int language in new[]{0,1,3,5,7,18})
            {
                Set(c,"LanguageIndex",language);Call(c,"ArrangePressureMessage");Application.DoEvents();
                int text=TextRenderer.MeasureText(save.Text,save.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                Check(save.Width>=90&&save.Width>=text+20,"shared Save/Delete widened with full translated caption: "+language);
                Check(save.Right<=automatic.Left&&automatic.Width>=40,"save widening preserves automatic-save hit area: "+language);
                Check(!((Control)Field(c,"manualSaveButton")).Visible,"still one Save/Delete control; no duplicate manual button");
            }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            type=assembly.GetType("HVX.Controls.PressureTestControl",true);
            Modes();Submission();SaveWidth();
            Console.WriteLine("PRESSURE_CUSTOM_MINIMUM_PASS="+checks+";hardware=false;sdk=false;product=false;physicalAcceptance=false");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
