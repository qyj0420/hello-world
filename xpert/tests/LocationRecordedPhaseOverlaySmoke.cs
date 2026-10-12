using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;

[Serializable] public sealed class RecordedPhaseResult { public bool Ok;public string Status;public double[,] Rows; }
public sealed class RecordedPhaseDomain : MarshalByRefObject
{
    Assembly algorithm;
    public override object InitializeLifetimeService(){return null;}
    public void Load(string path){algorithm=Assembly.LoadFile(Path.GetFullPath(path));}
    public RecordedPhaseResult Run(string dir,int method,bool memory,bool first,bool[] quadrants,double rate,bool manual)
    {
        const BindingFlags f=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        object r=typeof(LocationCyclePhaseSmoke).GetMethod("Analyze",f).Invoke(null,new object[]{algorithm,dir,method,memory,first,quadrants,rate,manual});
        return new RecordedPhaseResult{Ok=(bool)r.GetType().GetField("Ok",f).GetValue(r),Status=(string)r.GetType().GetField("Status",f).GetValue(r),Rows=(double[,])r.GetType().GetField("Rows",f).GetValue(r)};
    }
}

// Phase-only overlay regression against the exact frozen A/B build, not an older kernel.
internal static class LocationRecordedPhaseOverlaySmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    const double Fs=125000000;
    static int checks;
    static object Helper(string name,params object[] args){return typeof(LocationCyclePhaseSmoke).GetMethod(name,All).Invoke(null,args);}
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    static bool Same(double a,double b){return Double.IsNaN(a)?Double.IsNaN(b):Double.IsNaN(b)?false:Math.Abs(a-b)<1e-8*Math.Max(1,Math.Abs(a));}
    static object Run(RecordedPhaseDomain a,string dir,int method,bool first,bool memory=false,bool[] quadrants=null,bool manual=false,double rate=Fs)
    {return a.Run(dir,method,memory,first,quadrants,rate,manual);}
    static double[,] Rows(object r,string label){Check((bool)Field(r,"Ok"),label+": "+Field(r,"Status"));return (double[,])Field(r,"Rows");}
    static string Header(int version)
    {
        string h=((string)Helper("Header",false,125000,Fs)).Replace((4095/Fs).ToString("R",CultureInfo.InvariantCulture),(124999/Fs).ToString("R",CultureInfo.InvariantCulture));
        return version==1?h:h.Replace("HVX.LocationCycles/1","HVX.LocationCycles/2").Replace("\"sampleCount\":125000,","\"sampleCount\":125000,\"savedDurationSeconds\":0.001,");
    }
    static string WriteLoc(string dir,string reference)
    {
        string file=(string)Helper("WriteLoc",dir,reference);var lines=File.ReadAllLines(file).ToList();
        for(int i=4096;i<125000;i++)lines.Add((.01*Math.Sin(i*.73)*100000).ToString("R",CultureInfo.InvariantCulture));
        File.WriteAllLines(file,lines);return file;
    }
    static double Phase(double sample)
    {
        double[] boundaries={0,600,1800,2800,124999};int j=0;while(j<3&&sample>=boundaries[j+1])j++;
        return (180+180*(j+(sample-boundaries[j])/(boundaries[j+1]-boundaries[j])))%360;
    }
    static int Main(string[] args)
    {
        try{
            var baseline=(RecordedPhaseDomain)AppDomain.CreateDomain("frozen").CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location,typeof(RecordedPhaseDomain).FullName);
            var overlay=(RecordedPhaseDomain)AppDomain.CreateDomain("overlay").CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location,typeof(RecordedPhaseDomain).FullName);
            baseline.Load(args[0]);overlay.Load(args[1]);
            string dir=(string)Helper("WriteFixture",args[2]);
            for(int method=0;method<=7;method++)foreach(bool first in new[]{false,true})foreach(bool manual in new[]{false,true}){
                WriteLoc(dir,null);
                var prior=Rows(Run(baseline,dir,method,first,false,null,manual),"frozen "+method);
                var same=Rows(Run(overlay,dir,method,first,false,null,manual),"legacy overlay "+method);
                Check(prior.GetLength(0)>0&&prior.GetLength(0)==same.GetLength(0),"same legacy row count method"+method);
                for(int r=0;r<prior.GetLength(0);r++)for(int c=0;c<36;c++)Check(Same(prior[r,c],same[r,c]),"unchanged legacy numerical matrix "+method+" row="+r+" col="+c);
                foreach(int version in new[]{1,2}){
                    string path=WriteLoc(dir,Header(version));byte[] bytes=File.ReadAllBytes(path);
                    object result=Run(overlay,dir,method,first,false,null,manual);var current=Rows(result,"recorded v"+version+" method"+method);
                    Check(current.GetLength(0)==prior.GetLength(0),"phase does not change candidate count");
                    for(int r=0;r<prior.GetLength(0);r++){
                        // In this fixture the old uniform phase is <360, so invert it
                        // to recover that exact kernel's canonical phase sample anchor.
                        double anchor=Math.Round((prior[r,3]-13)*Fs/(360*50));
                        Check(anchor>=0&&anchor<125000,"frozen anchor remains original sample");
                        double expected=Phase(anchor);
                        Check(Same(current[r,3],expected),"recorded phase at unchanged canonical anchor v"+version+" method"+method+" first="+first+" row="+r+" actual="+current[r,3]+" expected="+expected);
                        for(int c=0;c<36;c++)if(c!=3)Check(Same(prior[r,c],current[r,c]),"phase-only overlay preserves other numerical columns "+method+" col="+c);
                    }
                    Check(((string)Field(result,"Status")).Contains("recorded_alternating_zero_crossings"),"recorded source diagnostic");
                    Check(bytes.SequenceEqual(File.ReadAllBytes(path)),"reader never changes original LOC");
                    var none=Rows(Run(overlay,dir,method,first,false,new[]{false,false,false,false},manual),"all quadrants off");Check(none.GetLength(0)==0,"none mask excludes all rows");
                    var partial=Rows(Run(overlay,dir,method,first,false,new[]{false,false,false,true},manual),"recorded partial mask");
                    Check(partial.GetLength(0)>0,"partial recorded quadrant has real candidate");
                    for(int r=0;r<partial.GetLength(0);r++)Check(partial[r,3]>=270&&partial[r,3]<360,"recorded mask uses true crossing phase");
                }
            }
            WriteLoc(dir,Header(2));
            foreach(int method in new[]{0,5,7}){
                var bad=Run(overlay,dir,method,false,false,null,false,Fs/2);Check(!(bool)Field(bad,"Ok"),"caller rate mismatch fails closed "+method);
                var oldMem=Rows(Run(baseline,dir,method,false,true),"frozen memory");var newMem=Rows(Run(overlay,dir,method,false,true),"overlay memory");
                Check(oldMem.GetLength(0)==newMem.GetLength(0),"memory ignores disk phase");
                for(int r=0;r<oldMem.GetLength(0);r++)for(int c=0;c<36;c++)Check(Same(oldMem[r,c],newMem[r,c]),"memory numerical contract unchanged");
            }
            Console.WriteLine("LOCATION_RECORDED_PHASE_OVERLAY_PASS="+checks+"; HARDWARE=False; QA="+dir);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
