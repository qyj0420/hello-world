using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationPhaseSharingSmoke
{
    static Type record,control; static string output; static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Static(string method,params object[] args){return record.GetMethod(method).Invoke(null,args);}
    static void Set(object obj,string name,object value){obj.GetType().GetField(name).SetValue(obj,value);}
    static object Get(object obj,string name){return obj.GetType().GetField(name).GetValue(obj);}
    static string Block(string phase,double length){return "0\t"+phase+"\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t"+length.ToString(System.Globalization.CultureInfo.InvariantCulture)+"m\r\n";}
    static string Project(string name,bool unequal=false)
    {
        string root=Path.Combine(output,name);Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"三相单芯电缆\"\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),Block("A",170)+Block("B",unequal?180:170)+Block("C",170));return root;
    }
    static object Save(string root,string phase,string serial="OFFLINE",bool timing=true,Action<object> configure=null)
    {
        object r=Activator.CreateInstance(record);
        foreach(var pair in new[]{new[]{"Phase",phase},new[]{"Level","5nC"},new[]{"DeviceSerial",serial},new[]{"DeviceModel","fixture"},
            new[]{"FilterId",""},new[]{"ProjectHash",(string)Static("Identity",root)},new[]{"CreatedUtc",DateTime.UtcNow.ToString("o")}})Set(r,pair[0],pair[1]);
        var filter=record.Assembly.GetType("HVX.SignalProcessing.PdLinearHighPass",true);
        Set(r,"FilterId",filter.GetField("FilterId",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).GetValue(null));
        Set(r,"LengthM",170d);Set(r,"VelocityMPerUs",170d);Set(r,"SampleRateHz",100e6);Set(r,"IncidentSeconds",20/100e6);Set(r,"ReflectionSeconds",220/100e6);
        Set(r,"CoefficientPcPerV",5000d);Set(r,"ChargePc",5000d);Set(r,"SnrDb",40d);Set(r,"Correlation",.99);Set(r,"RangeIndex",5);
        Set(r,"PulseCount",3);Set(r,"IncidentPeakIndex",20);Set(r,"ReflectionPeakIndex",220);Set(r,"FirstArrival",timing);
        var wave=new double[512];wave[20]=1;wave[220]=.5;Set(r,"Average",wave);
        var rawType=record.Assembly.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);var raw=Array.CreateInstance(rawType,3);
        for(int i=0;i<3;i++){string name=phase+"-"+i+".raw";File.WriteAllBytes(Path.Combine(root,name),new byte[]{1,2,(byte)i});var rr=Activator.CreateInstance(rawType);
            Set(rr,"RelativePath",name);Set(rr,"Sha256",Static("HashFile",Path.Combine(root,name)));raw.SetValue(rr,i);}
        Set(r,"RawFiles",raw);if(configure!=null)configure(r);record.GetMethod("Save").Invoke(r,new object[]{root});return r;
    }
    static object Best(string root,string phase,bool timing=true,string serial="OFFLINE"){return Static("BestForProject",root,phase,timing,serial);}
    static string Ready(string root,string phase)
    {
        using(var ui=(Control)Activator.CreateInstance(control)){
            control.GetProperty("SimulationMode").SetValue(ui,false,null);control.GetProperty("ProjectPath").SetValue(ui,root,null);
            return (string)control.GetMethod("GetCalibrationReadiness").Invoke(ui,new object[]{phase,false,true});}
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            output=Path.Combine(Path.GetFullPath(args[1]),"phase-sharing");Directory.CreateDirectory(output);
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            record=asm.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);control=asm.GetType("HVX.Controls.CalibrationProgramControl",true);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            foreach(string source in new[]{"ALL","A","B","C"}){
                string root=Project("source-"+source);Save(root,source);
                string path=(string)Static("RecordPath",root,source,"5nC");byte[] before=File.ReadAllBytes(path);
                Check(!(bool)Static("RequiresSeparatePhases",root),"equal lengths share");
                foreach(string target in new[]{"ALL","A","B","C"}){
                    object hit=Best(root,target);Check(hit!=null&&(string)Get(hit,"Phase")==source,"one phase serves "+target+" from "+source);
                    Check(Ready(root,target)=="","UI uses same rule "+source+" -> "+target);}
                Check(Best(root,"A",false)==null,"timing mismatch still rejected");Check(Best(root,"A",true,"OTHER")==null,"device mismatch still rejected");
                Check(Best(root,"UNKNOWN")==null,"unknown phase not invented");Check(before.SequenceEqual(File.ReadAllBytes(path)),"source archive unchanged");
                Check(Path.GetDirectoryName(path)==root&&Path.GetExtension(path)==".cal","formal calibration uses the project-root .cal contract");
                string[] archives=((string[])Static("EnumerateRecordFiles",root));
                Check(archives.Length==1&&archives[0]==path,"one source archive serves all phases without fabricated copies");
                Check(Directory.GetFiles(root,"*.cal",SearchOption.AllDirectories).Length==1&&
                    Directory.GetFiles(root,"*.hvcal",SearchOption.AllDirectories).Length==0,"no duplicate current or legacy phase archives");
            }
            string multi=Project("priority");Save(multi,"B");Save(multi,"ALL");Save(multi,"A");
            Check((string)Get(Best(multi,"A"),"Phase")=="A","own phase wins");Check((string)Get(Best(multi,"C"),"Phase")=="ALL","ALL precedes another phase");
            File.AppendAllText(Path.Combine(multi,"ALL-0.raw"),"bad");Check((string)Get(Best(multi,"C"),"Phase")!="ALL","corrupt raw cannot be shared");
            File.AppendAllText(Path.Combine(multi,"ITEM.in"),"changed");Check(Best(multi,"A")==null,"project identity invalidates candidates");
            string unequal=Project("unequal",true);Save(unequal,"ALL");Save(unequal,"A");
            Check((bool)Static("RequiresSeparatePhases",unequal),"different lengths require separate calibration");
            Check(Best(unequal,"A")!=null&&Ready(unequal,"A")=="","own phase remains usable");
            foreach(string phase in new[]{"B","C","ALL"}){
                Check(Best(unequal,phase)==null,"unequal does not share into "+phase);
                Check(Ready(unequal,phase).Contains("分别")||Ready(unequal,phase).Contains("separately"),"explicit separate-phase reminder");}
            Save(unequal,"B");Save(unequal,"C");Check(Best(unequal,"B")!=null&&Best(unequal,"C")!=null,"separate records complete unequal project");
            string broken=Project("bad-geometry");Save(broken,"ALL");File.WriteAllText(Path.Combine(broken,"CABLE.in"),"not geometry");
            Check(Ready(broken,"A")!="","invalid metadata not treated as equal length");
            string common=Project("shared-geometry");File.WriteAllText(Path.Combine(common,"CABLE.in"),Block("ALL",170));Save(common,"C");
            Check(Best(common,"A")!=null,"single common structure accepts one phase");
            string ranking=Project("unknown-snr-ranking");
            Save(ranking,"A",configure:r=>{Set(r,"Level","20pC");Set(r,"ChargePc",20d);Set(r,"SnrDb",Double.NaN);Set(r,"QualityWarningAcknowledged",true);Set(r,"QualityWarning","Synthetic unknown SNR accepted for fixture");});
            Save(ranking,"A");
            Check((string)Get(Best(ranking,"A"),"Level")=="5nC","unknown SNR cannot prevent a finite-quality same-phase candidate from winning");
            string unknownPath=(string)Static("RecordPath",ranking,"A","20pC");byte[] unknownBefore=File.ReadAllBytes(unknownPath);
            Check(Double.IsNaN((double)Get(Static("Load",ranking,unknownPath,true),"SnrDb")),"ranking retains unknown quality without inventing SNR");
            Check(unknownBefore.SequenceEqual(File.ReadAllBytes(unknownPath)),"selection does not rewrite operator-accepted source");
            Console.WriteLine("CALIBRATION_PHASE_SHARING_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
