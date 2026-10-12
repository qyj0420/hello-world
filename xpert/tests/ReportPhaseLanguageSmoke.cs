using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ReportPhaseLanguageSmoke
{
    static int checks;
    static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    static bool Contains(Control parent,string text)
    { if(parent.Text==text)return true;foreach(Control c in parent.Controls)if(Contains(c,text))return true;return false; }
    [STAThread] static int Main(string[] args){try{
        Application.EnableVisualStyles();
        var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
        var phase=asm.GetType("HVX.ReportGenerator.ReportCableStructurePreview",true).GetMethod("PhaseCaption",BindingFlags.Static|BindingFlags.NonPublic);
        string[] inputs={"A","B","C","ALL"," all ","b","","D"};
        string[] english={"L1","L2","L3","ALL","ALL","L2","Unspecified phase","D"};
        string[] chinese={"A相","B相","C相","ALL","ALL","B相","未指定相别","D"};
        for(int language=0;language<2;language++)for(int i=0;i<inputs.Length;i++)
            Check((string)phase.Invoke(null,new object[]{language,inputs[i]})==(language==0?chinese[i]:english[i]),"explicit phase label "+language+" "+inputs[i]);
        var recordType=asm.GetType("HVX.Controls.BrowserRecord",true);var waveType=asm.GetType("HVX.Controls.BrowserWaveform",true);object record=Activator.CreateInstance(recordType),wave=Activator.CreateInstance(waveType);
        recordType.GetField("Kind").SetValue(record,"dem");recordType.GetField("Phase").SetValue(record,"ALL");
        waveType.GetField("Y").SetValue(wave,new double[101]);waveType.GetField("VoltageKv").SetValue(wave,new double[101]);waveType.GetField("SampleRateHz").SetValue(wave,1000d);waveType.GetField("VoltageSampleRateHz").SetValue(wave,1000d);
        using(var bmp=new Bitmap(3000,1500))using(var g=Graphics.FromImage(bmp)){asm.GetType("HVX.ReportGenerator.ProjectWaveformChartRenderer",true).GetMethod("Draw",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{g,record,wave,1});bmp.Save(Path.Combine(args[1],"report-all-phase-fixed.png"));}
        var pressure=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
        using(var control=(Control)Activator.CreateInstance(pressure)){
            pressure.GetProperty("SimulationMode").SetValue(control,true,null);
            pressure.GetProperty("LanguageIndex").SetValue(control,1,null);
            Check(Contains(control,"PD and voltage waveform"),"actual waveform heading or navigation translated");
            Check(Contains(control,"Spectrum analysis"),"actual spectrum heading or navigation translated");
            Check(!Contains(control,"局放与电压波形")&&!Contains(control,"谱图分析"),"no remaining target Chinese static text");
            pressure.GetProperty("LanguageIndex").SetValue(control,0,null);
            Check(Contains(control,"局放与电压波形")&&Contains(control,"谱图分析"),"switch back to Chinese restores static text");
            pressure.GetProperty("LanguageIndex").SetValue(control,1,null);
            Check(!Contains(control,"局放与电压波形")&&!Contains(control,"谱图分析"),"repeated language switch retains translations");
        }
        Console.WriteLine("REPORT_PHASE_LANGUAGE_PASS="+checks);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
