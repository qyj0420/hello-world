using System;
using System.IO;
using System.Text;
using HVX.ProjectModel;
using HVX.OWTS.Task38;
using Xiongxin.Calibration;

internal static class ProjectMeasurementModeSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    [STAThread]static int Main(string[] args)
    {
        try {
            string root=Path.Combine(Path.GetFullPath(args[0]),"mode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Check(!ProjectMeasurementMode.IsCapacitor(root),"No type never implies capacitor");
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t0\n1\t始端\t\t0\n1\t终端\t电容\t0");
            foreach(var encoding in new[]{Encoding.UTF8,Encoding.Unicode,Encoding.BigEndianUnicode,Encoding.GetEncoding(936)}) {
                foreach(string value in new[]{"电容局放","电容","电容器","Capacitor PD","Capacitor"}) {
                    File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\n负载类型 = "+value+"\n电缆长度 = 0",encoding);
                    Check(ProjectMeasurementMode.IsCapacitor(root),"Explicit capacitor supported in metadata encoding");
                    var d=ProjectDefaults.Load(root);Check(d.CapacitiveLoad&&d.LengthM==0&&d.VelocityMPerUs==0,"Capacitor defaults do not invent geometry");
                }
            }
            foreach(string text in new[]{"电缆长度 = 0","电容量 = 1.0","电缆芯数 = 单芯\n电容器名称 = 容器","负载类型 = 三芯","Load type = Three-phase single-core"}) {
                File.WriteAllText(Path.Combine(root,"ITEM.in"),text);Check(!ProjectMeasurementMode.IsCapacitor(root),"Cable/zero length/capacitance reading does not select capacitor semantics");
            }
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型 = 电容\n电缆芯数 = 三芯");
            bool rejected=false;try{ProjectMeasurementMode.IsCapacitor(root);}catch(InvalidDataException){rejected=true;}Check(rejected,"Conflicting explicit load types rejected");
            var settings=new CalibrationWindowSettings{CapacitiveLoad=true,CableLengthM=0,VelocityMPerUs=0};settings.Validate();
            Check(settings.WindowSamples==5657,"Capacitor 40us envelope based on actual 125MS/s, independent of cable geometry");
            settings.SampleRateHz=10000000;Check(settings.WindowSamples==1057,"Capacitor envelope follows actual sample rate");
            settings.CapacitiveLoad=false;rejected=false;try{settings.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"Cable cannot use zero geometry to bypass validation");
            Console.WriteLine("PROJECT_MEASUREMENT_MODE_PASS="+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
