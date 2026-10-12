using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

static class CalibrationProjectAuditSmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static bool Write(string project,string detail,out string error)
    {
        object[] values={project,"A","5nC","QualityCaptureChoice","Ignore",detail,null};
        bool result=(bool)module.GetType("Xiongxin.Calibration.CalibrationProjectAudit",true).GetMethod("TryWrite",B).Invoke(null,values);
        error=(string)values[6];return result;
    }
    static XmlDocument Read(string project)
    {
        var doc=new XmlDocument{XmlResolver=null};
        string folder=Path.Combine(project,"Calibration","Logs");
        doc.LoadXml("<events>"+String.Join("",Directory.GetFiles(folder,"*.xml.log").Select(File.ReadAllText))+"</events>");return doc;
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            module=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            string project=Path.Combine(args[1],"project"),other=Path.Combine(args[1],"other");Directory.CreateDirectory(project);Directory.CreateDirectory(other);
            string detail="质量不足 < & > \"\r\n人工继续",error;
            Check(Write(project,detail,out error)&&error=="","quality event is persisted");
            var doc=Read(project);var entry=(XmlElement)doc.SelectSingleNode("/events/event");
            Check(entry.GetAttribute("phase")=="A"&&entry.GetAttribute("level")=="5nC","event retains phase and level");
            Check(entry.GetAttribute("action")=="QualityCaptureChoice"&&entry.GetAttribute("outcome")=="Ignore","event retains action and choice");
            Check(entry["detail"].InnerText.Replace("\r","")==detail.Replace("\r",""),"XML escaping retains readable details");
            DateTimeOffset parsed;Check(DateTimeOffset.TryParse(entry.GetAttribute("utc"),out parsed)&&DateTimeOffset.TryParse(entry.GetAttribute("local"),out parsed),"real timestamps recorded");
            string log=Directory.GetFiles(Path.Combine(project,"Calibration","Logs"),"*.xml.log").Single();
            string first=File.ReadAllText(log);var attributes=File.GetAttributes(log);File.SetAttributes(log,attributes|FileAttributes.ReadOnly);
            Check(Write(project,"second",out error),"read-only project log appends safely");
            Check((File.GetAttributes(log)&FileAttributes.ReadOnly)!=0,"read-only attribute restored");
            Check(File.ReadAllText(log).StartsWith(first,StringComparison.Ordinal),"existing log bytes not replaced");
            Parallel.For(0,8,i=>{string e;if(!Write(project,"parallel "+i,out e))throw new Exception(e);});
            Check(Read(project).SelectNodes("/events/event").Count==10,"concurrent events remain complete");
            Check(Write(other,"other project",out error)&&Read(other).SelectNodes("/events/event").Count==1,"events remain project specific");
            Check(!Write("","invalid",out error)&&error.Length>0,"missing project is reported without fallback elsewhere");
            string blocked=Path.Combine(args[1],"blocked");Directory.CreateDirectory(blocked);File.WriteAllText(Path.Combine(blocked,"Calibration"),"existing file");
            Check(!Write(blocked,"invalid",out error)&&error.Length>0,"unwritable log location reports failure");
            Check(File.ReadAllText(Path.Combine(blocked,"Calibration"))=="existing file","logging failure preserves existing data");
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.CalibrationProgramControl",true))) {
                control.GetType().GetProperty("ProjectPath",B).SetValue(control,blocked,null);
                bool saved=(bool)control.GetType().GetMethod("SaveLiveCalibration",B).Invoke(control,new object[]{-1,"5nC"});
                string notice=(string)control.GetType().GetProperty("LastCalibrationError",B).GetValue(control,null);
                Check(!saved&&notice.Contains("日志未写入"),"save failure exposes separate log failure without disabling source guard");
            }
            File.SetAttributes(log,attributes);
            Console.WriteLine("CALIBRATION_PROJECT_AUDIT_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
