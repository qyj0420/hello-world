using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

static class CalibrationRootArchiveSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;static Type store;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static object Static(string n,params object[] a){return store.GetMethod(n,B).Invoke(null,a);}
    static bool Rejects(Action a){try{a();return false;}catch(TargetInvocationException e){return e.InnerException is IOException||e.InnerException is InvalidDataException;}}
    static string Cable(int length,bool joint=false){return "0\tALL\t\t\n1\tStart\tTerminal\t0m\n2\tBody\tXLPE\t\n"+
        (joint?"1\tJoint 1\tConfirmed joint\t40m\n2\tBody\tXLPE\t\n":"")+"1\tEnd\tTerminal\t"+length+"m\n";}
    static void Item(string root,int length){File.WriteAllText(Path.Combine(root,"ITEM.in"),"项目日期: = \"2026-10-09\"\n负载类型: = \"一体三芯电缆\"\n电缆长度: = \""+length+"\"\nA相电缆长度: = \""+length+"\"\nALL相采用波速: = \"170\"\n");}
    static object Record(Assembly assembly,string root)
    {
        object r=Activator.CreateInstance(store);
        foreach(var pair in new[]{new[]{"Phase","ALL"},new[]{"Level","5nC"},new[]{"DeviceSerial","OFFLINE-ROOT"},new[]{"DeviceModel","fixture"},
            new[]{"CreatedUtc",DateTime.UtcNow.ToString("o")},new[]{"ProjectHash",(string)Static("Identity",root)},new[]{"FilterId","HP10K_LINEAR_V1"}})Set(r,pair[0],pair[1]);
        Type filter=assembly.GetType("HVX.SignalProcessing.PdLinearHighPass",true);Set(r,"FilterId",filter.GetField("FilterId",B).GetValue(null));
        foreach(var pair in new[]{Tuple.Create("LengthM",170d),Tuple.Create("VelocityMPerUs",170d),Tuple.Create("SampleRateHz",100e6),
            Tuple.Create("IncidentSeconds",20/100e6),Tuple.Create("ReflectionSeconds",220/100e6),Tuple.Create("CoefficientPcPerV",5000d),
            Tuple.Create("ChargePc",5000d),Tuple.Create("SnrDb",40d),Tuple.Create("Correlation",.99)})Set(r,pair.Item1,pair.Item2);
        Set(r,"RangeIndex",5);Set(r,"PulseCount",3);Set(r,"IncidentPeakIndex",20);Set(r,"ReflectionPeakIndex",220);Set(r,"FirstArrival",true);
        double[] wave=new double[512];wave[20]=1;wave[220]=.5;Set(r,"Average",wave);
        Type reference=assembly.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);Array refs=Array.CreateInstance(reference,3);
        for(int i=0;i<3;i++){string name="raw"+i+".bin",path=Path.Combine(root,name);File.WriteAllBytes(path,new byte[]{1,2,(byte)i});object v=Activator.CreateInstance(reference);Set(v,"RelativePath",name);Set(v,"Sha256",Static("HashFile",path));refs.SetValue(v,i);}
        Set(r,"RawFiles",refs);return r;
    }
    static void WriteHistorical(object r,string path)
    {
        Set(r,"ProjectScopeHash",null);
        using(var payload=new MemoryStream()){
            new XmlSerializer(store).Serialize(payload,r);byte[] bytes=payload.ToArray(),hash;
            using(var sha=SHA256.Create())hash=sha.ComputeHash(bytes);
            using(var writer=new BinaryWriter(File.Create(path))){writer.Write(Encoding.ASCII.GetBytes("HVCLIVE1"));writer.Write(bytes.Length);writer.Write(hash);writer.Write(bytes);}
        }
    }
    static int Main(string[] args)
    {
        try {
            string build=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);Item(root,170);File.WriteAllText(Path.Combine(root,"CABLE.in"),Cable(170));
            Assembly module=Assembly.LoadFrom(Path.Combine(build,"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));store=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
            object r=Record(module,root);string file=(string)store.GetMethod("Save").Invoke(r,new object[]{root});
            Check(file==Path.Combine(root,"ALL_5nC.cal"),"formal archive saves directly in project root with .cal suffix");
            Check((bool)Static("IsArchiveFile",file),"formal .cal detected by container magic");
            Check((string)Get(r,"SourcePath")==file,"successful save retains actual source path");
            string historical=Path.Combine(root,"old-no-scope.hvcal");WriteHistorical(r,historical);
            Check(Static("Load",root,historical,true)!=null,"historical exact project identity remains readable");
            r=Static("Load",root,file,true);Check(!String.IsNullOrEmpty((string)Get(r,"ProjectScopeHash")),"new save binds stable project scope");
            string folder=Path.Combine(root,"Calibration","Live");Directory.CreateDirectory(folder);string old=Path.Combine(folder,"ALL_5nC.hvcal");File.Move(file,old);
            object best=Static("BestForProject",root,"A",true,"OFFLINE-ROOT");
            Check(best!=null&&(string)Get(best,"SourcePath")==old,"legacy folder selection retains real source, not nonexistent canonical .cal");
            Check(((string[])Static("EnumerateRecordFiles",root)).SequenceEqual(new[]{old}),"legacy formal folder is enumerated");
            file=(string)store.GetMethod("Save").Invoke(best,new object[]{root});
            Check(File.Exists(old)&&File.Exists(file),"new root save preserves historical formal file");
            Check(((string[])Static("EnumerateRecordFiles",root)).SequenceEqual(new[]{file}),"root formal record shadows same phase-level historical record without duplication");
            string legacyCal=Path.Combine(root,"B_100pC.cal");File.WriteAllText(legacyCal,"Date\t2026-10-09\nNI_Calib_factor\t1\n0\n");
            Check(!(bool)Static("IsArchiveFile",legacyCal),"text legacy .cal is not misread as formal binary archive");
            string scope=(string)Static("ScopeIdentity",root),exact=(string)Static("Identity",root);Item(root,180);File.WriteAllText(Path.Combine(root,"CABLE.in"),Cable(180,true));
            Check((string)Static("Identity",root)!=exact&&(string)Static("ScopeIdentity",root)==scope,"saved length and reviewed joint changes do not change calibration project scope");
            object loaded=Static("Load",root,file,true);
            Check((double)Get(loaded,"CoefficientPcPerV")==5000&&(double)Get(loaded,"LengthM")==170&&(string)Get(loaded,"ProjectHash")==exact,"reload retains acquired coefficient and original geometry provenance");
            Check(Static("BestForProject",root,"A",true,"OFFLINE-ROOT")!=null,"saved electrical calibration remains selectable after optional TDR length save");
            Check(Rejects(()=>Static("Load",root,historical,true)),"unknown historical geometry mismatch remains strict, not silently rebound");
            foreach(string consumer in new[]{"DataBrowser","ReportGenerator"}){
                Assembly assembly=Assembly.LoadFrom(Path.Combine(build,"modules",consumer,"Xiongxin.OWTS."+consumer+".dll"));Type reader=assembly.GetType("HVX.Controls.BrowserReader",true);
                object data=reader.GetMethods(B).Single(m=>m.Name=="LoadProject"&&m.GetParameters().Length==1).Invoke(null,new object[]{root});
                object formal=((IEnumerable)Get(data,"Records")).Cast<object>().Single(o=>(string)Get(o,"Path")==file);
                Check((bool)Get(formal,"IsValid"),consumer+" reads new .cal as valid formal calibration");
                object wave=reader.GetMethods(B).Single(m=>m.Name=="LoadWaveform"&&m.GetParameters().Length==1).Invoke(null,new[]{formal});
                Check(((double[])Get(wave,"Y")).SequenceEqual((double[])Get(loaded,"Average")),consumer+" reloads lossless formal waveform");
            }
            File.AppendAllText(Path.Combine(root,"ITEM.in"),"项目标识: = \"other-project\"\n");
            Check(Rejects(()=>Static("Load",root,file,true)),"unrelated project metadata changes still reject calibration identity");
            Console.WriteLine("CALIBRATION_ROOT_ARCHIVE_PASS="+checks+"; HARDWARE_CALLS=0; USER_RECORD_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
