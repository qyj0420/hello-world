using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

// Inspect only; LOAD_LIBRARY_AS_DATAFILE prevents the application entry point
// and native initialization from running. No device or installed-state access.
internal static class NeutralExecutableResourceSmoke
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
    static extern bool EnumResourceNames(IntPtr module, IntPtr type, ResourceName callback, IntPtr state);
    delegate bool ResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr state);
    static int checks;
    static void Check(bool condition, string text)
    { if (!condition) throw new InvalidDataException(text); checks++; }
    static int Main(string[] args)
    {
        IntPtr module = IntPtr.Zero;
        try
        {
            Check(args.Length == 1 && File.Exists(args[0]), "Supply the newly built, unexecuted desktop executable.");
            var identity = FileVersionInfo.GetVersionInfo(args[0]);
            Check(identity.ProductName == "HV-DAC Xpert", "Neutral product name");
            Check(String.IsNullOrWhiteSpace(identity.CompanyName), "No default OEM publisher");
            Check(String.IsNullOrWhiteSpace(identity.LegalCopyright), "No default OEM copyright");
            module = LoadLibraryEx(Path.GetFullPath(args[0]), IntPtr.Zero, 2u);
            Check(module != IntPtr.Zero, "Open as data only");
            int icons = 0;
            ResourceName count = (m, t, n, s) => { icons++; return true; };
            foreach (int type in new[] { 3, 14 })
            {
                bool found = EnumResourceNames(module, new IntPtr(type), count, IntPtr.Zero);
                int error = Marshal.GetLastWin32Error();
                // 1813=type not found; 1812=no resource section (not expected here).
                Check(found || error == 1813 || error == 1812, "Resource enumeration succeeded or is explicitly absent");
            }
            GC.KeepAlive(count);
            Check(icons == 0, "No embedded vendor Win32 icon; Windows supplies a generic application icon");
            Console.WriteLine("NEUTRAL_EXECUTABLE_RESOURCES_PASS=" + checks + "; PRODUCT_ENTRY_EXECUTED=false; HARDWARE_ACCESSED=false");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (module != IntPtr.Zero) FreeLibrary(module); }
    }
}
