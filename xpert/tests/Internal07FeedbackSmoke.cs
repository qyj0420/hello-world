using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

class Internal07FeedbackSmoke
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static string Hash(string path) { using(var sha=SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args[0]), project=Path.GetFullPath(args[1]);
            var browser=Assembly.LoadFrom(Path.Combine(root,"modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll"));
            var recordType=browser.GetType("HVX.Controls.BrowserRecord",true);
            var reader=browser.GetType("HVX.Controls.BrowserReader",true);
            foreach(var path in Directory.GetFiles(project,"*.cal"))
            {
                string before=Hash(path); object record=Activator.CreateInstance(recordType);
                recordType.GetField("Path").SetValue(record,path);
                object wave=reader.GetMethod("LoadWaveform",new[]{recordType}).Invoke(null,new[]{record});
                Type wt=wave.GetType(); double rate=(double)wt.GetField("SampleRateHz").GetValue(wave);
                Check(rate==125000000,"Legacy CAL rate: "+path);
                Check(((double[])wt.GetField("Y").GetValue(wave)).Length>=16,"Waveform loaded");
                Check(double.IsNaN((double)wt.GetField("ReflectionSeconds").GetValue(wave)),"Missing reflection is not fabricated");
                Check(before==Hash(path),"Original CAL unchanged");
                Console.WriteLine(Path.GetFileName(path)+": 125 MHz, loaded, original unchanged");
            }
            var pressure=Assembly.LoadFrom(Path.Combine(root,"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
            Type waveType=browser.GetType("HVX.Controls.BrowserWaveform",true);
            object chartWave=Activator.CreateInstance(waveType);
            double[] signedCharge={-50,-2,0,30,200}, signedVoltage={-10,5,0,-5,10};
            waveType.GetField("Y").SetValue(chartWave,signedCharge);
            waveType.GetField("VoltageKv").SetValue(chartWave,signedVoltage);
            Type chartType=browser.GetType("HVX.Controls.BrowserTestWaveformControl",true);
            using(var chart=(Control)Activator.CreateInstance(chartType,true))
            {
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                chartType.GetMethod("SetWaveform",flags).Invoke(chart,new[]{chartWave,(object)1});
                object[] range={0.0,0.0}; chartType.GetMethod("ChargeRange",flags).Invoke(chart,range);
                Check((double)range[0]==0,"Charge axis starts at zero");
                Check((double)range[1]>=200,"Positive peaks retained");
                Check(signedCharge[0]==-50&&signedVoltage[0]==-10,"Signed source arrays unchanged");
            }
            Type pt=pressure.GetType("HVX.Controls.PressureTestControl",true);
            using(var control=(Control)Activator.CreateInstance(pt))
            {
                Check((bool)pt.GetProperty("AutomaticPostProcessEnabled").GetValue(control,null),"Auto-save defaults on");
                Check((bool)pt.GetMethod("ReportAcquisitionCompleted").Invoke(control,new object[]{false}),"Valid acquisition requests save");
                Check((bool)pt.GetMethod("CompleteSave").Invoke(control,new object[]{true,""}),"Save acknowledged");
                Check((bool)pt.GetProperty("DeleteEnabled").GetValue(control,null),"Saved data may be deleted");
                Check(!(bool)pt.GetProperty("DataAnalyzed").GetValue(control,null),"Save does not claim analysis");
                Check((long)pt.GetProperty("AnalysisRequestSequence").GetValue(control,null)==0,"Save does not request analysis");
            }
            Console.WriteLine("PASS "+checks+" checks"); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
