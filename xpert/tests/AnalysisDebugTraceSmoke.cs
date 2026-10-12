using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;

// Pure managed trace test only: no control construction, algorithm execution,
// project access, SDK loading or hardware operations. Run without the marker.
internal static class AnalysisDebugTraceSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static int Main(string[] args)
    {
        try
        {
            Check(args.Length==1,"Expected the newly built DataAnalysis DLL path.");
            Check(!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"analysis-debug.enabled")),"Test must run without the opt-in marker.");
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var trace=assembly.GetType("HVX.PdReview.ReviewAnalysisDebugTrace",true);
            const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
            const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            Check(!(bool)trace.GetProperty("Enabled",Static).GetValue(null,null),"Tracing remains disabled by default.");
            var queue=(ICollection)trace.GetField("pending",Static).GetValue(null);
            int before=queue.Count;
            var scope=trace.GetMethod("Begin",Static).Invoke(null,new object[]{"test","no raw data"});
            var point=scope.GetType().GetMethod("Point",Instance);
            var watch=Stopwatch.StartNew();
            for(int i=0;i<10000;i++)point.Invoke(scope,new object[]{"disabled-point",""});
            watch.Stop();
            scope.GetType().GetMethod("Failure",Instance).Invoke(scope,new object[]{new InvalidOperationException("test-only")});
            scope.GetType().GetMethod("End",Instance).Invoke(scope,new object[]{"done"});
            ((IDisposable)scope).Dispose();((IDisposable)scope).Dispose();
            Check(queue.Count==before,"Disabled trace must not enqueue or write diagnostics.");
            Check((int)trace.GetField("identityQueued",Static).GetValue(null)==0,"Disabled trace must not start binary hashing.");
            Check((bool)trace.GetMethod("WaitForIdle",Static).Invoke(null,new object[]{1000}),"Disabled trace completes immediately.");
            var escape=trace.GetMethod("Escape",Static);
            Check((string)escape.Invoke(null,new object[]{"a\tb\r\nc\\d"})=="a\\tb\\r\\nc\\\\d","TSV escaping preserves complete exception lines without breaking records.");
            var fileVersion=FileVersionInfo.GetVersionInfo(args[0]).FileVersion;
            Check(fileVersion=="1.0.24.1","The direct-debug module has its own real file version, not the host version.");
            var information=(AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyInformationalVersionAttribute));
            Check(information!=null&&information.InformationalVersion=="1.0.24.1-debug-d01-20261010","The direct-debug provenance is explicit.");
            Check(assembly.GetName().Version==new Version(1,0,24,0),"Assembly ABI identity remains stable.");
            Console.WriteLine("PASS checks="+checks+"; disabled 10000 reflective points ms="+watch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; fileVersion="+fileVersion);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
