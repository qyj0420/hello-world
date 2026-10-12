using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Web.Script.Serialization;

// Reflection only; never calls Program.Main, a device connector or settings Save.
static class V30BatchValidationIsolationSmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);
            var host=Assembly.LoadFrom(Path.Combine(root,"Xiongxin.OWTS.Desktop.exe"));
            var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
            var paths=host.GetType("Xiongxin.OWTS.Desktop.AppPaths",true);
            var build=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root,"build-manifest.json"))) as Dictionary<string,object>;
            bool standalone=build.ContainsKey("PressureStandalone")&&(bool)build["PressureStandalone"];
            string expectedProfile=standalone?"Pressure-Standalone-20261008":"Validation-V30-Batch-20261008";
            Check((string)paths.GetField("ProfileDirectoryName",flags).GetRawConstantValue()==expectedProfile,"build-specific isolated preference profile");
            string settings=(string)paths.GetProperty("SettingsFile",flags).GetValue(null,null);
            Check(settings.EndsWith("\\"+expectedProfile+"\\settings.xml",StringComparison.OrdinalIgnoreCase),"No legacy preference fallback");
            var provisioning=host.GetType("Xiongxin.OWTS.Desktop.FirstHardwareProvisioning",true);
            string parent=(string)provisioning.GetField("ParentState",flags).GetValue(null);
            Check(parent.EndsWith("\\XIONGXIN\\"+(standalone?"HVXPressureStandalone":"HVXValidationV30Batch")+"\\install-state.xml",StringComparison.OrdinalIgnoreCase),"Unrelated parent installation state cannot affect this package");
            Check(parent.IndexOf("HVX100UnifiedParent",StringComparison.OrdinalIgnoreCase)<0,"No old parent completion marker consumed");
            var s=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.AppSettings",true));
            var hw=s.GetType().GetProperty("Hardware").GetValue(s,null);
            Check(!(bool)hw.GetType().GetProperty("Enabled").GetValue(hw,null),"First use remains offline until operator acts");
            Check(host.GetManifestResourceNames().Contains("HVX.Modules.Index.v1"),"Complete module bundle embedded");
            // The isolated tests use sidecar modules; prove the delivered EXE
            // embeds exactly the same bytes, not merely an index with that name.
            string index;
            using(var input=host.GetManifestResourceStream("HVX.Modules.Index.v1"))
            using(var reader=new StreamReader(input))index=reader.ReadToEnd();
            var manifest=new JavaScriptSerializer { MaxJsonLength=16*1024*1024 }.DeserializeObject(File.ReadAllText(Path.Combine(root,"module-manifest.json"))) as Dictionary<string,object>;
            var expected=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var item in (object[])manifest["Modules"]){var row=(Dictionary<string,object>)item;expected.Add((string)row["Path"],(string)row["Sha256"]);}
            var lines=index.Split('\n');
            Check(lines[0]=="XIONGXIN.Modules.v1","Embedded index schema");
            int count=0;
            foreach(string line in lines.Skip(1).Where(x=>x.Length>0)){
                string[] parts=line.Split('\t');Check(parts.Length==4,"Embedded index row schema");
                string key="modules/"+parts[0];
                Check(expected.ContainsKey(key)&&expected[key]==parts[2],"Embedded manifest equals tested sidecar: "+key);
                using(var packed=host.GetManifestResourceStream(parts[3]))
                using(var gzip=new GZipStream(packed,CompressionMode.Decompress))
                using(var sha=SHA256.Create()){
                    string digest=BitConverter.ToString(sha.ComputeHash(gzip)).Replace("-","");
                    Check(digest==parts[2],"Embedded resource bytes equal sidecar: "+key);
                }
                expected.Remove(key);count++;
            }
            Check(count>0&&expected.Count==0,"Exact embedded module closure");
            Check(host.GetManifestResourceNames().Contains("Brand.ico"),"Executable brand icon retained");
            Check(File.Exists(Path.Combine(root,"assets","shortcuts","256.ico")),"User shortcut icon included");
            Check(File.Exists(Path.Combine(root,"assets","shortcuts","512.ico")),"Alternate original shortcut icon included");
            Check(System.Diagnostics.FileVersionInfo.GetVersionInfo(host.Location).FileVersion=="1.0.30.9","Exact validation build version");
            Check(host.GetName().Version.ToString()=="1.0.30.9","Assembly identity agrees with validation version");
            string exe=Path.Combine(root,"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe");
            Check(System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion=="1.0.30.9","V30 pressure worker version");
            Console.WriteLine("V30_BATCH_VALIDATION_ISOLATION_PASS="+checks+"; HARDWARE_ACCESSED=False; SETTINGS_WRITTEN=False");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
