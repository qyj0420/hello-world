using System;
using System.Reflection;
using System.Windows.Forms;
internal static class CalibrationIdentityBaselineSmoke
{
    [STAThread] static int Main(string[] args)
    {
        // Load an earlier fake-only QA assembly as data; do not invoke its entry point.
        var assembly=Assembly.LoadFrom(args[0]);var type=assembly.GetType("Xiongxin.Calibration.CalibrationForm",true);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using(var form=(Form)Activator.CreateInstance(type,flags,null,new object[]{null,true},null))
        {
            ((Timer)type.GetField("safetyTimer",flags).GetValue(form)).Stop();
            string art=((TextBox)type.GetField("device",flags).GetValue(form)).Text;
            string pico=((TextBox)type.GetField("picoSerial",flags).GetValue(form)).Text;
            object config=type.GetMethod("Settings",flags).Invoke(form,null);
            string serial=(string)config.GetType().GetField("Serial").GetValue(config);
            if(art!="Dev1"||pico!=""||serial!="")throw new Exception("Historical seam was not reproduced.");
            if((bool)type.GetProperty("HasOwnedHardwareResources",flags).GetValue(form,null))throw new Exception("Unexpected hardware owner");
            Console.WriteLine("BASELINE_CONFIRMED: integrated coordinator defaults to Dev1 and first OPEN Settings.Serial is empty; 4 read-only checks; no OPEN/worker/SDK invoked.");
        }
        return 0;
    }
}
