using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

internal static class LegacyCalibrationReadOnlyProbe
{
    static string Hash(string path){using(var s=SHA256.Create())using(var f=File.OpenRead(path))return BitConverter.ToString(s.ComputeHash(f));}
    [STAThread] static int Main(string[] args)
    {
        try {
            // Real existing sample, read-only. No applicability binding or
            // project writes, and no device or executable entry is invoked.
            string project=@"D:\OWTSData\050537-050538\2026年05月05日16时31分";
            string path=Path.Combine(project,"ALL_5nC.cal");
            var files=new[]{path,Path.Combine(project,"ITEM.in"),Path.Combine(project,"CABLE.in")};
            var hashes=files.Select(Hash).ToArray();
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            var type=assembly.GetType("HVX.CalibrationStorage.LegacyCalibrationBinding",true);
            var c=type.GetMethod("ReadCandidate").Invoke(null,new object[]{project,path});
            double coefficient=(double)c.GetType().GetField("CoefficientPcPerV").GetValue(c);
            double rate=(double)c.GetType().GetField("SampleRateHz").GetValue(c);
            int samples=(int)c.GetType().GetField("WaveformSampleCount").GetValue(c);
            double recordedRate=(double)c.GetType().GetField("RecordedSampleRateHz").GetValue(c);
            if(Math.Abs(coefficient-2174.1755689735)>1e-9||rate!=125000000||recordedRate!=125000004||samples<16)throw new Exception("Actual legacy CAL values did not round-trip with exact known-rate correction.");
            if(!hashes.SequenceEqual(files.Select(Hash)))throw new Exception("Read-only project files changed.");
            Console.WriteLine("LEGACY_READONLY_SAMPLE_PASS=4; COEFFICIENT="+coefficient+"; SAMPLE_RATE="+rate+"; SAMPLES="+samples+"; USER_PROJECTS_WRITTEN=0; HARDWARE_CALLS=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
