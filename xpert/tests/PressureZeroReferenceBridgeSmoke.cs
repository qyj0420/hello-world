using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PressureZeroReferenceBridgeSmoke
{
    static int checks;
    static void Check(bool value,string reason){checks++;if(!value)throw new InvalidOperationException(reason);}
    static object Get(object c,string name){return c.GetType().GetProperty(name).GetValue(c,null);}
    static void Set(object c,string name,object value){c.GetType().GetProperty(name).SetValue(c,value,null);}
    static object Call(object c,string name,params object[] values){return c.GetType().GetMethod(name).Invoke(c,values);}
    static void Acquire(Control c,double voltage,string phase)
    {
        Set(c,"SelectedPhaseCode",phase);
        var values=new Dictionary<string,object>{{"targetPeakVoltageKv",voltage},{"positivePolarity",true},{"cableLengthMeters",175.4},{"phaseCode",phase}};
        typeof(OfflineSimulationBridge).GetMethod("InstallPressureSimulation",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{c,values});
        Application.DoEvents();
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string release=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(qa.IndexOf("\\build\\",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidOperationException("QA must remain under build.");
            Directory.CreateDirectory(qa);
            Assembly.LoadFrom(Path.Combine(release,"modules","PressureTest","HVX.PdReview.V1.0.dll"));
            var assembly=Assembly.LoadFrom(Path.Combine(release,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var c=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(c,"ProjectPath",qa);Set(c,"SimulationMode",true);Set(c,"AutomaticPostProcessEnabled",true);
                Acquire(c,0,"A");
                Check((bool)Get(c,"CurrentAcquisitionIsNoiseReference"),"0 kV classified before matrix callbacks");
                var summary=(IDictionary<string,object>)Call(c,"GetNoiseReferenceSummary");
                Check((string)summary["state"]=="Simulation","explicitly labeled simulated reference");
                Check(!(bool)summary["includedInDischargeStatistics"]&&!(bool)summary["automaticFilteringAllowed"],"reference not PD or filter authorization");
                Check(((double[])Call(c,"GetNoiseReferenceSamples")).Any(x=>x!=0),"bridge retains actual synthetic noise not a zero line");
                Check(((int[])Get(c,"StatisticsFilteredSourceRows")).Length==0,"0 reference never enters downstream PD rows");
                Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"display never falsely acknowledges persisted acquisition or full analysis");
                var savedSamples=(double[])Call(c,"GetNoiseReferenceSamples");
                Acquire(c,12,"A");
                Check(!(bool)Get(c,"CurrentAcquisitionIsNoiseReference"),"next nonzero waveform is regular acquisition");
                Check(((int[])Get(c,"StatisticsFilteredSourceRows")).Length>0,"normal synthetic PD remains visible");
                Check(((double[])Call(c,"GetNoiseReferenceSamples")).SequenceEqual(savedSamples),"same-configuration A uses retained raw reference");
                Acquire(c,12,"B");
                Check(((double[])Call(c,"GetNoiseReferenceSamples")).Length==0,"A reference is not applied to B");
                Acquire(c,0,"B");Acquire(c,12,"B");
                Check(((double[])Call(c,"GetNoiseReferenceSamples")).Length>0,"independent B reference available");
                Acquire(c,12,"A");
                Check(((double[])Call(c,"GetNoiseReferenceSamples")).SequenceEqual(savedSamples),"return to A finds A reference");
                string next=Path.Combine(qa,"another-project");Directory.CreateDirectory(next);Set(c,"ProjectPath",next);Acquire(c,12,"A");
                Check(((double[])Call(c,"GetNoiseReferenceSamples")).Length==0,"project switch cannot reuse old reference");
            }
            Console.WriteLine("PRESSURE_ZERO_REFERENCE_BRIDGE_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
