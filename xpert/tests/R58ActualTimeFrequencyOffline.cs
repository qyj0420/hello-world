using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Read-only source archive replay. No UI constructor, SDK or device call.
static class R58ActualTimeFrequencyOffline
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool pass,string label){if(!pass)throw new Exception(label);checks++;}
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=64*1024*1024,RecursionLimit=128};}
    static string Hash(string path){using(var file=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(file));}
    static double[,] Matrix(string path)
    {using(var file=File.OpenRead(path))using(var reader=new BinaryReader(file)){Check(Encoding.ASCII.GetString(reader.ReadBytes(8))=="HVXMAT01","bounded matrix signature");int rows=reader.ReadInt32(),cols=reader.ReadInt32();Check(rows>=0&&cols>=0&&(long)rows*cols<=32000000&&file.Length==16+8L*rows*cols,"bounded matrix length");var result=new double[rows,cols];for(int row=0;row<rows;row++)for(int col=0;col<cols;col++)result[row,col]=reader.ReadDouble();return result;}}
    static string Decode(string text)
    {const string prefix="HVX.Diagnostics.Gzip.v1:";if(!text.StartsWith(prefix,StringComparison.Ordinal))return text;using(var input=new MemoryStream(Convert.FromBase64String(text.Substring(prefix.Length))))using(var zip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(zip,Encoding.UTF8))return reader.ReadToEnd();}
    static object Call(Type type,string name,params object[] args)
    {return type.GetMethods(Flags).Single(method=>method.Name==name&&method.GetParameters().Length==args.Length).Invoke(null,args);}
    static bool Same(double a,double b){return BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);}
    static int Main(string[] args)
    {
        try {
            if(args.Length!=3)throw new ArgumentException("Expected module.dll, original project root, archive identifier.");
            string module=Path.GetFullPath(args[0]),project=Path.GetFullPath(args[1]),id=args[2],archive=Path.Combine(project,"Analysis report",id);
            var names=new[]{"parameters.bin","pdmap.bin","session.json","analysis.json"};var hashes=names.ToDictionary(name=>name,name=>Hash(Path.Combine(archive,name)));
            var parameters=Matrix(Path.Combine(archive,"parameters.bin"));var waves=Matrix(Path.Combine(archive,"pdmap.bin"));
            var before=(double[,])parameters.Clone();Check(parameters.GetLength(0)==644,"actual archive 023 has 644 source rows");
            int[] accepted=Enumerable.Range(0,parameters.GetLength(0)).Where(row=>parameters[row,0]==3&&parameters[row,35]==1).ToArray();
            Check(accepted.Length==169,"actual C accepted selection contains 169 pulses");
            Check(accepted.All(row=>parameters[row,24]>=304&&parameters[row,24]<305),"old F column reproduces oscillation-frequency contamination");
            var payload=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(Path.Combine(archive,"session.json"),new UTF8Encoding(false,true)));
            var settings=((object[])payload["settings"]).Cast<object[]>().ToDictionary(row=>(string)row[0],row=>(string)row[1]);
            var encoded=(Dictionary<string,object>)Json().DeserializeObject(settings["AlgorithmDiagnostics"]);
            var diagnostics=encoded.ToDictionary(pair=>pair.Key,pair=>Decode((string)pair.Value),StringComparer.OrdinalIgnoreCase);
            var statusByIdentity=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(object[] file in (object[])payload["files"]) {
                string status;if(diagnostics.TryGetValue((string)file[0],out status))statusByIdentity.Add((string)file[1]+":"+(string)file[2],status);
            }
            var assembly=Assembly.LoadFrom(module);var adapter=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);var review=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            var clock=Stopwatch.StartNew();var display=(double[,])Call(adapter,"CreateTimeFrequencyDisplay",parameters,waves,statusByIdentity);clock.Stop();
            Check(accepted.All(row=>!Double.IsNaN(display[row,23])&&display[row,23]>0&&!Double.IsNaN(display[row,24])&&display[row,24]>1000),"all 169 C pulses recover trustworthy incident-packet RMS features");
            Check(accepted.Select(row=>display[row,24]).Distinct().Count()>10,"recovered bandwidths no longer form a synthetic constant vertical line");
            for(int row=0;row<parameters.GetLength(0);row++)for(int col=0;col<parameters.GetLength(1);col++) {
                Check(Same(parameters[row,col],before[row,col]),"original parameter matrix unchanged");
                if(col!=23&&col!=24)Check(Same(display[row,col],before[row,col]),"non-feature field remains bit-for-bit unchanged");
            }
            var missing=(double[,])Call(adapter,"CreateTimeFrequencyDisplay",parameters,new double[0,0],statusByIdentity);
            Check(accepted.All(row=>Double.IsNaN(missing[row,23])&&Double.IsNaN(missing[row,24])),"missing verified raw support is unavailable, never guessed from 304 Hz");
            var ai=Call(review,"CompetitionParametersForDisplay",project,id,(Func<bool>)(()=>true));
            var aiOriginal=(double[,])ai.GetType().GetField("Parameters",Flags).GetValue(ai);var aiDisplay=(double[,])ai.GetType().GetField("DisplayParameters",Flags).GetValue(ai);
            Check(aiOriginal.GetLength(0)==644&&aiDisplay.GetLength(0)==644,"AI display and recognition input retain all rows");
            for(int row=0;row<parameters.GetLength(0);row++)for(int col=0;col<parameters.GetLength(1);col++) {
                Check(Same(aiOriginal[row,col],parameters[row,col]),"AI recognition/learning input remains original");
                Check(Same(aiDisplay[row,col],display[row,col]),"AI uses the same isolated feature projection as ordinary analysis");
            }
            foreach(string name in names)Check(Hash(Path.Combine(archive,name))==hashes[name],"original archive member unchanged");
            Console.WriteLine("R58_FT_ACTUAL023_PASS="+checks+";Caccepted="+accepted.Length+";oldBandwidthHz=304.1..304.2;newBandwidthHz="+accepted.Min(row=>display[row,24]).ToString("R",CultureInfo.InvariantCulture)+".."+accepted.Max(row=>display[row,24]).ToString("R",CultureInfo.InvariantCulture)+";newTeqNs="+accepted.Min(row=>display[row,23]).ToString("R",CultureInfo.InvariantCulture)+".."+accepted.Max(row=>display[row,23]).ToString("R",CultureInfo.InvariantCulture)+";featureReplayMs="+clock.ElapsedMilliseconds+";ProductConstructed=false;SDKCalled=false;HardwareUsed=false;SourceProjectWritten=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
