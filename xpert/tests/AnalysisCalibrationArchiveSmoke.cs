using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Managed fixtures only. No SDK, worker, hardware or product Main is started.
internal static class AnalysisCalibrationArchiveSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks; static Assembly module; static Type store,selector;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Static(string n,params object[] a){return store.GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(null,a);}
    static object[] Catalog(string root){object[] args={root,null};return ((IEnumerable)selector.GetMethod("ReadCatalog",B).Invoke(null,args)).Cast<object>().ToArray();}
    static void Project(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"start\tQA\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(false));
    }
    static double[] Wave(bool reflection)
    {
        return Enumerable.Range(0,2048).Select(i=>.000001*Math.Sin(i*.73)+.25*Math.Exp(-Math.Pow((i-104)/5d,2))+
            (reflection?.10*Math.Exp(-Math.Pow((i-304)/5d,2)):0)).ToArray();
    }
    static object Record(string root,bool reflection,int version=3)
    {
        object r=Activator.CreateInstance(store);Set(r,"Version",version);
        foreach(var p in new[]{new[]{"Phase","ALL"},new[]{"Level","5nC"},new[]{"DeviceSerial","OFFLINE-FIXTURE"},new[]{"DeviceModel","managed"},
            new[]{"CreatedUtc",DateTime.UtcNow.ToString("o")},new[]{"ProjectHash",(string)Static("Identity",root)},new[]{"ProjectScopeHash",(string)Static("ScopeIdentity",root)},
            new[]{"AcquisitionMode","Pulse"},new[]{"CoefficientProcessing","HP10K_NO_LP"},new[]{"ReflectionStatus",reflection?"Detected":"NotDetected"},
            new[]{"VelocitySource",reflection?"Measured":"Configured"}})Set(r,p[0],p[1]);
        Set(r,"FilterId",module.GetType("HVX.SignalProcessing.PdLinearHighPass",true).GetField("FilterId",B).GetValue(null));
        foreach(var p in new[]{Tuple.Create("LengthM",170d),Tuple.Create("VelocityMPerUs",170d),Tuple.Create("SampleRateHz",100e6),
            Tuple.Create("IncidentSeconds",100/100e6),Tuple.Create("ReflectionSeconds",reflection?300/100e6:Double.NaN),
            Tuple.Create("CoefficientPcPerV",5000d),Tuple.Create("ChargePc",5000d),Tuple.Create("SnrDb",40d),Tuple.Create("Correlation",reflection?.9:Double.NaN),
            Tuple.Create("AttenuationDb",reflection?20*Math.Log10(2.5):Double.NaN)})Set(r,p.Item1,p.Item2);
        Set(r,"RangeIndex",8);Set(r,"PulseCount",3);Set(r,"IncidentPeakIndex",104);Set(r,"ReflectionPeakIndex",reflection?304:-1);Set(r,"FirstArrival",true);
        double[] wave=Wave(reflection);Set(r,"Average",wave);if(version==2)Set(r,"EffectiveWindows",Enumerable.Range(0,3).Select(i=>(double[])wave.Clone()).ToArray());
        Type reference=module.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);Array refs=Array.CreateInstance(reference,3);
        for(int i=0;i<3;i++){string name="raw"+i+".bin",path=Path.Combine(root,name);File.WriteAllBytes(path,new byte[]{1,2,(byte)i});object v=Activator.CreateInstance(reference);Set(v,"RelativePath",name);Set(v,"Sha256",Static("HashFile",path));refs.SetValue(v,i);}
        Set(r,"RawFiles",refs);return r;
    }
    static string Save(object r,string root){return (string)store.GetMethod("Save",B).Invoke(r,new object[]{root});}
    static Control Map(string root,double rate=50e6)
    {
        var map=(Control)Activator.CreateInstance(module.GetType("LabVIEWPdMap.PdMapControl",true));Set(map,"ProjectPath",root);
        var p=new double[1,36];p[0,0]=1;p[0,5]=170;p[0,6]=170;p[0,7]=rate;p[0,9]=0;p[0,10]=200;p[0,11]=Double.NaN;p[0,12]=p[0,13]=Double.NaN;
        p[0,15]=150;p[0,16]=Double.NaN;p[0,20]=200;p[0,21]=Double.NaN;p[0,31]=0;p[0,32]=1023;p[0,33]=Double.NaN;
        var w=new double[1,1024];for(int i=0;i<1024;i++)w[0,i]=150*Math.Exp(-Math.Pow((i-200)/5d,2));
        Call(map,"SetPdMapData",p,w,0d,new[]{true,true,false,false,false,false,true,true},new double[0],null,null,5d,false);
        return map;
    }
    static void Legacy(string root)
    {
        string[] header={"Date\t2026-10-09","Time\t10:00:00","Cal.value\t100","Cable length\t170","V\t170","NI_Calib_factor\t5000",
            "SR\t100000000","Start_point (s)\t0.000001","Final_point (s)\t0.000003","Calib_Attenuation\t8","Unused1\t0","Unused2\t0","Unused3\t0"};
        File.WriteAllText(Path.Combine(root,"B_100pC.cal"),String.Join("\r\n",header.Concat(Wave(true).Select(x=>x.ToString("R",CultureInfo.InvariantCulture))))+"\r\n",new UTF8Encoding(false));
    }
    static void SyntheticLoc(string path,double[,] waves,int row)
    {
        // A catalog entry is not sufficient source evidence. These are managed
        // waveforms, not a simulated hardware capture or a validity assertion.
        string[] header={"Fixture\tSynthetic managed waveform; no hardware capture","Date\t2026-10-11","Time\t10:00:00",
            "Phase\tA","Voltage\t12.3","Cable length\t170","Velocity (m/us)\t170","SR\t100000000",
            "ChargeUnit\tpC","SampleCount\t1024","StoredChargeMultiplier\t100000"};
        var samples=Enumerable.Range(0,waves.GetLength(1)).Select(i=>(waves[row,i]*100000d).ToString("R",CultureInfo.InvariantCulture));
        File.WriteAllText(path,String.Join("\r\n",header.Concat(samples))+"\r\n",new UTF8Encoding(false));
    }
    static void ReloadPreservesReview(string qa)
    {
        string root=Path.Combine(qa,"reload");Project(root);object record=Record(root,true);Save(record,root);
        foreach(string name in new[]{"A_12.3kV_1.loc","A_12.3kV_2.loc"})File.WriteAllBytes(Path.Combine(root,name),new byte[256]);
        module.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
        module.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
        using(var review=(Control)Activator.CreateInstance(module.GetType("HVX.PdReview.PdReviewControl",true))) {
            Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",root);object tree=Get(review,"AnalysisTree"),map=Get(review,"PdMap");
            string[] paths=(string[])Call(tree,"GetDataPaths");var catalog=(double[,])Call(tree,"GetDataCatalog");
            Check(paths.Length==2,"reload fixture retains two actual LOC catalog entries");
            var p=new double[2,36];var w=new double[2,1024];
            for(int row=0;row<2;row++) {
                p[row,0]=catalog[row,0];p[row,1]=catalog[row,1];p[row,2]=catalog[row,2];p[row,3]=45;p[row,5]=170;p[row,6]=170;p[row,7]=100e6;
                p[row,9]=row;p[row,10]=100;p[row,11]=300;p[row,12]=2;p[row,13]=170;p[row,15]=p[row,16]=150;p[row,20]=100;p[row,21]=.92;p[row,32]=1023;p[row,33]=.91;
                for(int i=0;i<1024;i++)w[row,i]=150*Math.Exp(-Math.Pow((i-100)/5d,2))+60*Math.Exp(-Math.Pow((i-300)/5d,2));
            }
            Check(!(bool)Call(review,"LoadDataMatrices",p,w)&&((string)Get(review,"LastError")).Contains("LOC header requires 11 bounded lines"),
                "NUL placeholder is rejected by the source quality reader, not accepted as a captured LOC");
            Check((int)Get(review,"SourceRowCount")==0&&((double[,])Get(review,"ResultParameterMatrix")).GetLength(0)==0,
                "invalid source metadata does not partially publish source matrices");
            for(int row=0;row<paths.Length;row++)SyntheticLoc(paths[row],w,row);
            bool loaded=(bool)Call(review,"LoadDataMatrices",p,w);
            Check(loaded,"reload fixture source matrices load: "+Get(review,"LastError"));
            Check((bool)Call(review,"SelectSourceRow",0)&&(bool)Call(review,"AcceptCurrent"),"fixture operator explicitly accepts row zero");
            Check((bool)Call(review,"SelectSourceRow",1)&&(bool)Call(review,"DeleteCurrent"),"fixture operator explicitly excludes row one");
            var before=(double[,])Get(review,"ResultParameterMatrix");var accepted=(bool[])Get(review,"AcceptanceConfirmedRows");var removed=(bool[])Get(review,"RemovedRows");
            int reads=(int)Get(map,"ProjectCalibrationReadCount"),selected=(int)Get(review,"SelectedSourceRowIndex");
            Set(record,"Level","100nC");Set(record,"ChargePc",100000d);Set(record,"CoefficientPcPerV",100000d);Save(record,root);
            Set(review,"ProjectPath",root);Check((int)Get(map,"ProjectCalibrationReadCount")==reads,"same project assignment does not silently reset review/catalog");
            Call(review,"ReloadProjectCalibration");
            Check((int)Get(map,"ProjectCalibrationReadCount")==reads+1&&(int)Get(map,"ProjectCalibrationCandidateCount")==2&&
                ((string)Get(map,"SelectedCalibrationPath")).EndsWith("ALL_100nC.cal"),"explicit same-project reload sees newly saved CAL and updates selected reference");
            Check(((string[])Call(tree,"GetDataPaths")).SequenceEqual(paths)&&(int)Get(review,"SourceRowCount")==2,"CAL refresh preserves LOC catalog and source rows");
            Check(((double[,])Get(review,"ResultParameterMatrix")).Cast<double>().Zip(before.Cast<double>(),(a,b)=>a.Equals(b)).All(x=>x)&&
                ((bool[])Get(review,"AcceptanceConfirmedRows")).SequenceEqual(accepted)&&((bool[])Get(review,"RemovedRows")).SequenceEqual(removed)&&
                (int)Get(review,"SelectedSourceRowIndex")==selected,"CAL refresh does not overwrite manual acceptance/exclusion, result data or selection");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));store=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
            selector=module.GetType("LabVIEWPdMap.ProjectCalibrationImplementation.Selector",true);
            string root=Path.Combine(qa,"live");Project(root);object r=Record(root,true);string file=Save(r,root);string hash=(string)Static("HashFile",file);Legacy(root);
            var catalog=Catalog(root);Check(catalog.Length==2&&catalog.All(c=>(bool)Get(c,"Valid")),"new binary and old text CAL are both readable");
            object candidate=catalog.Single(c=>(string)Get(c,"Phase")=="ALL");
            Check((int)Get(candidate,"RangeIndex")==8&&(bool)Get(candidate,"FirstArrival"),"live archive keeps real capture range and timing mode");
            Check(((double[])Get(candidate,"Wave")).SequenceEqual((double[])Get(r,"Average")),"catalog keeps exact saved voltage samples, without pC/V multiplication");
            using(var map=Map(root)){
                Check((string)Get(map,"SelectedCalibrationPath")==file,"A row uses applicable ALL archive");
                Check((double)Get(map,"SelectedCalibrationSampleRateHz")==100e6&&(int)Get(map,"SelectedCalibrationRangeIndex")==8,"CAL rate and range are not overwritten by LOC");
                Check((string)Get(map,"SelectedCalibrationTimingMode")=="FirstArrival","stored timing provenance is not guessed from current mode");
                Check((string)Get(map,"ProjectCalibrationDisplayMode")=="DisplayNormalized","overlay explicitly records display-only normalization");
                var curve=(double[,])Call(map,"GetReportCurve",1);
                Check(curve.GetLength(0)==1024&&Math.Abs(curve[200,1]-150)<.01,"calibration overlay is visible and normalized at incident peak");
                Check(Math.Abs(curve[300,1]-60)<.01&&Math.Abs(curve[400,1])<.01,"CAL reflection uses separate real sample rate; it is not stretched by point counts");
                Check(Double.IsNaN((double)Get(map,"LocationM"))&&!(bool)Get(map,"reflectionSelected"),"curve loading does not invent an unselected PD reflection/location");
                Check(((double[])Get(map,"calibration")).SequenceEqual((double[])Get(r,"Average")),"display normalization never mutates raw voltage waveform");
                Set(r,"CoefficientPcPerV",2500d);Save(r,root);Call(map,"ReloadProjectCalibration");
                Check(((double[,])Call(map,"GetReportCurve",1)).Cast<double>().SequenceEqual(curve.Cast<double>()),"changing electrical coefficient does not change displayed CAL waveform");
            }
            Check((string)Static("HashFile",file)!=hash,"only explicit fixture save above modifies fixture archive");
            string old=Path.Combine(root,"Calibration","Live","ALL_5nC.hvcal");Directory.CreateDirectory(Path.GetDirectoryName(old));File.Copy(file,old);
            Check(Catalog(root).Length==2,"root archive shadows same historical phase-level without duplicate selection");
            File.Move(file,Path.Combine(root,"saved-copy.bin"));
            using(var map=Map(Path.Combine(root,"ITEM.in")))Check((string)Get(map,"SelectedCalibrationPath")==old,"historical folder and ITEM.in path retain actual archive path");
            foreach(int version in new[]{1,2}){
                string historical=Path.Combine(qa,"v"+version);Project(historical);Save(Record(historical,true,version),historical);
                Check(Catalog(historical).All(c=>(bool)Get(c,"Valid")),"archive V"+version+" is compatible without format guessing");
            }
            string incident=Path.Combine(qa,"incident");Project(incident);Save(Record(incident,false),incident);
            using(var map=Map(incident)){
                Check(((double[,])Call(map,"GetReportCurve",1)).GetLength(0)>0,"incident-only electrical calibration remains viewable");
                Check((int)Get(map,"SelectedCalibrationReflectionPeakIndex")==-1&&Double.IsNaN((double)Get(map,"ProjectDampingSlopePerNs")),"incident-only reference has no invented reflection/decay");
                Check(((double[,])Call(map,"GetReportCurve",2)).GetLength(0)==0&&!(bool)Get(map,"HasFilteredDampingCurve"),"neither raw nor filtered damping is fabricated");
                Check(Double.IsNaN((double)Get(map,"LocationM"))&&!((double[])Get(map,"calibration")).Any(Double.IsNaN),"valid CAL waveform does not promote unknown location");
            }
            string broken=Path.Combine(qa,"broken");Project(broken);string bad=Save(Record(broken,true),broken);File.SetAttributes(bad,FileAttributes.Normal);byte[] bytes=File.ReadAllBytes(bad);bytes[bytes.Length-1]^=1;File.WriteAllBytes(bad,bytes);
            Check(!((bool)Get(Catalog(broken).Single(),"Valid")),"checksum corruption remains rejected");
            using(var map=Map(broken))Check(((double[,])Call(map,"GetReportCurve",1)).GetLength(0)==0,"invalid archive cannot display an invented reference");
            File.AppendAllText(Path.Combine(root,"ITEM.in"),"project-id\tOTHER\r\n");Check(!((bool)Get(Catalog(root).Single(c=>(string)Get(c,"Phase")=="ALL"),"Valid")),"different project scope remains rejected");
            ReloadPreservesReview(qa);
            Console.WriteLine("ANALYSIS_CALIBRATION_ARCHIVE_PASS="+checks+"; HARDWARE_CALLS=0; USER_RECORD_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
