using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;

// Synthetic files only. Exercise the actual browser and analysis assemblies.
static class LocationReaderParitySmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type browser,record,analysis;static string project,path;static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static object Browse(){var r=Activator.CreateInstance(record);record.GetField("Path").SetValue(r,path);record.GetField("Kind").SetValue(r,"loc");return browser.GetMethod("LoadWaveform",new[]{record}).Invoke(null,new[]{r});}
    static object Analyze(){return analysis.GetMethod("Read",B).Invoke(null,new object[]{path,project});}
    static string[] Lines(string rate="Delta x [s]\t0.000000008",string additional="Reserved")
    {return new[]{"LOC","Date\t2026-10-09","Phase\t0","Target peak kV\t6.2","Cable-length\t170","Velocity (m/us)\t175",rate,"Charge unit\tpC x 100000",additional,"Reserved","Reserved"}.Concat(Enumerable.Range(0,64).Select(i=>(i*100001L-3210021).ToString(CultureInfo.InvariantCulture))).ToArray();}
    static void Put(string[] lines){File.WriteAllLines(path,lines,new UTF8Encoding(true));}
    static bool Rejected(Func<object> run){try{run();return false;}catch(TargetInvocationException e){return e.InnerException is InvalidDataException;}}
    static void Valid(string label,double rate=125e6)
    {
        var bytes=File.ReadAllBytes(path);var b=Browse();var a=Analyze();
        Check(Math.Abs((double)Field(b,"SampleRateHz")-rate)<=rate*1e-12&&Math.Abs((double)Field(a,"SampleRateHz")-rate)<=rate*1e-12,label+" matching timebase");
        var x=(double[])Field(b,"Y");var y=(double[])Field(a,"ChargeSamples");
        Check(x.Length==64&&x.SequenceEqual(y),label+" sample-by-sample parity");
        for(int i=0;i<64;i++)Check(x[i]==(i*100001L-3210021)/100000d,label+" integer scale");
        Check(bytes.SequenceEqual(File.ReadAllBytes(path)),label+" source unchanged");
    }
    static void Invalid(string label){Check(Rejected(Browse),label+" browser rejects");Check(Rejected(Analyze),label+" analysis rejects");}
    static void EngineRejects(string expected)
    {
        var asm=analysis.Assembly;var request=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
        request.GetType().GetField("ProjectPath",B).SetValue(request,project);request.GetType().GetField("LocationFilePath",B).SetValue(request,path);
        foreach(int method in new[]{5,7}){
            request.GetType().GetField("AlgorithmIndex",B).SetValue(request,method);
            var result=asm.GetType("HVX.PdReview.ReviewAnalysisEngine",true).GetMethod("Analyze",B).Invoke(null,new[]{request});
            Check(!(bool)Field(result,"Success")&&((string)Field(result,"Status")).Contains(expected),"actual A/B entry rejects "+expected);
        }
    }
    static int Main(string[] args)
    {try{
        string build=Path.GetFullPath(args[0]);project=Path.GetFullPath(args[1]);
        if(Directory.Exists(project))throw new Exception("Fresh fixture directory required");Directory.CreateDirectory(project);
        path=Path.Combine(project,"A_6.2kV_2026-10-09-12'00'00.loc");
        var b=Assembly.LoadFrom(Path.Combine(build,"modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll"));
        browser=b.GetType("HVX.Controls.BrowserReader",true);record=b.GetType("HVX.Controls.BrowserRecord",true);
        analysis=Assembly.LoadFrom(Path.Combine(build,"modules/DataAnalysis/HVX.PdReview.V1.0.dll")).GetType("HVX.PdReview.ReviewLocationFileReader",true);
        Put(Lines());Valid("current LOC");
        Put(Lines().Take(40).Concat(new[]{""}).Concat(Lines().Skip(40)).ToArray());Invalid("interior blank must not shift time");
        EngineRejects("missing interior sample");
        var bad=Lines();bad[40]="NaN";Put(bad);Invalid("nonfinite sample");EngineRejects("Invalid LOC sample");
        Put(Lines("Delta x [s]\t0.000000008","Sample rate Hz\t100000000"));Invalid("conflicting rates");
        Put(Lines("Delta x [s]\t0.000000008","Sample rate Hz\tNaN"));Invalid("invalid secondary rate");
        Put(Lines("Sampling rate MHz\t125"));Valid("sampling-rate alias");
        Put(Lines("Delta x [s]\t0.000000008","Sample rate Hz\t125000000"));Valid("equal rate aliases");
        Put(Lines().Concat(new[]{"",""}).ToArray());Valid("trailing blanks");
        Put(Lines("Delta x [s]\t0.000000008","Delta x [s]\t0.000000010"));Invalid("duplicate conflicting rate key");
        Console.WriteLine("LOCATION_READER_PARITY_PASS="+checks+"; HARDWARE_CALLS=0; SYNTHETIC_FILES_ONLY=TRUE");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
