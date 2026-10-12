using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Reflection only: no Program.Main, device/algorithm call, settings or project write.
static class FrozenPreviewIsolationSmoke
{
    static int checks;
    static void Check(bool ok, string detail) { checks++; if (!ok) throw new Exception(detail); }
    static int Main(string[] args)
    {
        try
        {
            var root=Path.GetFullPath(args[0]);
            var host=Assembly.LoadFrom(Path.Combine(root,"Xiongxin.OWTS.Desktop.exe"));
            var paths=host.GetType("Xiongxin.OWTS.Desktop.AppPaths",true);
            var flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
            Check((string)paths.GetField("ProfileDirectoryName",flags).GetRawConstantValue()=="Frozen-20261006-Internal01","New frozen installation has its own profile");
            var file=(string)paths.GetProperty("SettingsFile",flags).GetValue(null,null);
            Check(file.EndsWith("\\Frozen-20261006-Internal01\\settings.xml",StringComparison.OrdinalIgnoreCase),"No legacy preference fallback");
            var setting=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.AppSettings",true));
            var hardware=setting.GetType().GetProperty("Hardware").GetValue(setting,null);
            Check(hardware!=null && !(bool)hardware.GetType().GetProperty("Enabled").GetValue(hardware,null),"New configuration defaults offline");
            Check(host.GetManifestResourceNames().Contains("Brand.ico"),"Brand icon embedded");
            Check(host.GetManifestResourceNames().Contains("Brand.png"),"Complete brand embedded");
            Check(host.GetManifestResourceNames().Contains("HVX.Modules.Index.v1"),"Independent module payload embedded");
            Check(File.Exists(Path.Combine(root,"Xiongxin.OWTS.Desktop.exe.config")),"Framework configuration accompanies executable");
            Console.WriteLine("FROZEN_PREVIEW_ISOLATION_PASS="+checks);
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
