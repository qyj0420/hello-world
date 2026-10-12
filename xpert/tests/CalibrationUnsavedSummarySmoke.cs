using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HVX.Controls;

internal static class CalibrationUnsavedSummarySmoke
{
    static int checks;
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static Dictionary<string,string> Saved(CalibrationProgramControl control)
    {return (Dictionary<string,string>)typeof(CalibrationProgramControl).GetField("savedLevelSignatures",Hidden).GetValue(control);}
    static void RememberFixture(CalibrationProgramControl control,int phase,string level)
    {
        // Synthetic in-memory predicate fixture only: no file is represented as
        // a real saved calibration and no acquisition or save API is invoked.
        Saved(control)[phase+":"+level]=(string)typeof(CalibrationProgramControl)
            .GetMethod("LevelSignature",Hidden).Invoke(control,new object[]{phase,level});
    }
    static bool HasLine(string text,string prefix)
    {foreach(string line in text.Split(new[]{"\r\n"},StringSplitOptions.None))if(line.StartsWith(prefix,StringComparison.Ordinal))return true;return false;}
    static double[] Wave()
    {var wave=new double[2000];for(int i=0;i<wave.Length;i++)wave[i]=Math.Max(0,1-Math.Abs(i-300)/15d)+.4*Math.Max(0,1-Math.Abs(i-800)/18d);return wave;}
    [STAThread] static int Main()
    {
        try
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            Console.InputEncoding=new UTF8Encoding(false);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var control=new CalibrationProgramControl())
            {
                control.LanguageIndex=0;
                Check(!control.SimulationMode&&!control.HasUnsavedCalibrationChanges,"empty live fixture is clean");
                Check(control.UnsavedCalibrationSummary==String.Empty,"empty clean state has no pending item");
                control.SetPhaseParameters(-1,170,170,false,false);
                var wave=Wave();control.SetPhaseWaveform(-1,"TDR",wave,0,4e-9);
                control.SetPhaseWaveform(-1,"5nC",wave,0,4e-9);
                RememberFixture(control,-1,"5nC");
                string savedCharge=Saved(control)["-1:5nC"];
                string summary=control.UnsavedCalibrationSummary;
                Check(HasLine(summary,"ALL · TDR："),"TDR is the actual pending level");
                Check(!HasLine(summary,"ALL · 5nC："),"saved 5nC is not falsely listed as pending");
                Check(summary.Contains("测距结果／电缆长度")&&summary.Contains("保存电缆长度"),"TDR describes its separate length save action");
                Check(summary.Contains("不会保存 TDR"),"charge save does not imply ranging saved");
                Check(control.HasUnsavedCalibrationChanges&&control.GetLevelSaveState(-1,"5nC")=="Saved","summary leaves dirty TDR and saved charge state unchanged");
                Check(Saved(control).Count==1&&Saved(control)["-1:5nC"]==savedCharge,"summary never creates or alters save signatures");
                Check(wave[300]==1,"summary never edits input waveform");
                RememberFixture(control,-1,"TDR");
                Check(!control.HasUnsavedCalibrationChanges&&control.UnsavedCalibrationSummary==String.Empty,"all matching in-memory signatures yield no unsaved list");
                var trackedFiles=(Dictionary<string,string>)typeof(CalibrationProgramControl).GetField("liveSavedFiles",Hidden).GetValue(control);
                trackedFiles[Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"absent-fixture-"+Guid.NewGuid().ToString("N")+".cal") ]="not-a-valid-file-hash";
                Check(!control.HasUnsavedCalibrationChanges&&!control.IsCalibrationSavedValid,"missing tracked file is invalid even with matching signatures");
                Check(control.UnsavedCalibrationSummary.Contains("未通过核验"),"invalid saved file is not mislabeled as a specific unsaved level");
                trackedFiles.Clear();
                control.SetPhaseParameters(-1,171,171,false,false);
                summary=control.UnsavedCalibrationSummary;
                Check(HasLine(summary,"ALL · TDR：")&&HasLine(summary,"ALL · 5nC："),"shared geometry changes invalidate both affected levels");
                Check(summary.Contains("已修改，需重新保存"),"previously saved modified levels explicitly need re-save");
                control.SetPhaseParameters(1,200,160,false,false);control.SetPhaseWaveform(1,"100nC",Wave(),0,4e-9);
                summary=control.UnsavedCalibrationSummary;
                Check(HasLine(summary,"B · 100nC：")&&summary.Contains("校准系数 未保存"),"another phase/charge level is individually identified");
                Check(!HasLine(summary,"A ·")&&!HasLine(summary,"C ·"),"empty phases are not invented");
                control.LanguageIndex=1;summary=control.UnsavedCalibrationSummary;
                Console.WriteLine("SUMMARY_EN="+summary.Replace("\r\n"," | "));
                Check(summary.Contains("ranging result / cable length")&&summary.IndexOf("calibration coefficient",StringComparison.OrdinalIgnoreCase)>=0,"English purpose labels are available");
                Check(summary.Contains("changed; save again")&&summary.Contains("not saved"),"English dirty reasons distinguish changed and new levels");
                Check(control.HasUnsavedCalibrationChanges,"reading translated summary cannot clear dirty state");
            }
            using(var simulation=new CalibrationProgramControl())
            {
                simulation.LanguageIndex=1;simulation.SimulationMode=true;
                Check(simulation.UnsavedCalibrationSummary==String.Empty,"empty simulation stays clean");
                Check(simulation.SetSimulationDataOrigin("SIM:synthetic-only-origin"),"synthetic metadata-only change accepted");
                string summary=simulation.UnsavedCalibrationSummary;
                Check(simulation.HasUnsavedCalibrationChanges&&summary.Contains("archive or parameters"),"metadata-only dirty state has an explicit generic explanation");
                Check(!summary.Contains("Select the listed TDR"),"simulation does not promise a live cable-length save workflow");
            }
            Console.WriteLine("CALIBRATION_UNSAVED_SUMMARY_PASS="+checks+"; HARDWARE_COMMANDS=0; REAL_CALIBRATION_FILES_WRITTEN=0");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
