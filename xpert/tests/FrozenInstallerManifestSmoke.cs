using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

// Loads resources AS DATA only. Neither executable entry point is run.
static class FrozenInstallerManifestSmoke
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr LoadResource(IntPtr module,IntPtr info);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module,IntPtr info);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    static int checks;
    static void Check(bool ok,string detail){checks++;if(!ok)throw new Exception(detail);}
    static string Level(string path)
    {
        var handle=LoadLibraryEx(Path.GetFullPath(path),IntPtr.Zero,0x22);
        if(handle==IntPtr.Zero)throw new IOException("Resource load failed: "+Marshal.GetLastWin32Error());
        try
        {
            var info=FindResource(handle,new IntPtr(1),new IntPtr(24));
            Check(info!=IntPtr.Zero,"Executable manifest present");
            uint size=SizeofResource(handle,info);Check(size>0&&size<1024*1024,"Bounded manifest size");
            var resource=LoadResource(handle,info);var pointer=LockResource(resource);
            Check(pointer!=IntPtr.Zero,"Manifest resource readable without entry point execution");
            byte[] bytes=new byte[(int)size];Marshal.Copy(pointer,bytes,0,bytes.Length);
            var document=new XmlDocument{XmlResolver=null};
            using(var input=new MemoryStream(bytes))using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024}))document.Load(reader);
            var level=document.SelectSingleNode("//*[local-name()='requestedExecutionLevel']") as XmlElement;
            Check(level!=null,"UAC level explicitly specified");
            return level.GetAttribute("level");
        }
        finally{FreeLibrary(handle);}
    }
    static int Main(string[] args)
    {
        try
        {
            Check(args.Length==3,"Pass installer, packaged main executable and frozen Inno script");
            string installerLevel=Level(args[0]);
            Console.WriteLine("INSTALLER_LOADER_MANIFEST_LEVEL="+installerLevel);
            // Inno SetupLdr is a non-elevated launcher; the installation process
            // requests elevation according to PrivilegesRequired. Resource-only
            // inspection cannot verify the Windows UAC interaction dynamically.
            Check(installerLevel=="asInvoker","Inno non-elevated loader level recorded");
            string script=File.ReadAllText(Path.GetFullPath(args[2]));
            Check(Regex.IsMatch(script,@"(?m)^PrivilegesRequired=admin\s*$"),"Installation configured for administrator privileges");
            Check(!Regex.IsMatch(script,@"(?m)^PrivilegesRequiredOverridesAllowed="),"No non-administrator override configured");
            Check(!Regex.IsMatch(script,@"(?mi)^\[Run\]"),"No automatic main/hardware launch after installation");
            Check(Level(args[1])=="asInvoker","Main application does not require elevation");
            Console.WriteLine("FROZEN_INSTALLER_RESOURCE_CONFIGURATION_PASS="+checks+"; DynamicElevationTested=False; InstallerExecuted=False; HardwareAccessed=False");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
