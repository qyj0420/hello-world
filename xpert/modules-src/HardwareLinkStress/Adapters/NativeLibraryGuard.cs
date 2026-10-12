using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HVX.HardwareLinkStress.Adapters
{
    // These native libraries are deliberately loaded from an exact x64 path.
    // Do not alter the process-wide DLL search path used by the host application.
    internal static class NativeLibraryGuard
    {
        private const uint LoadWithAlteredSearchPath = 0x00000008;
        private static readonly object LoadGate = new object();

        internal static string RequireX64File(string path, string expectedName)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new ArgumentException("An absolute vendor DLL path is required.", "path");
            string full = Path.GetFullPath(path);
            if (!String.Equals(Path.GetFileName(full), expectedName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Unexpected vendor DLL name: " + full, "path");
            if (!File.Exists(full)) throw new FileNotFoundException("Required x64 vendor DLL is missing.", full);
            using (var stream = File.OpenRead(full))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
                    throw new BadImageFormatException("Not a Windows PE library: " + full);
                stream.Position = 0x3C;
                int offset = reader.ReadInt32();
                if (offset < 64 || offset > stream.Length - 26)
                    throw new BadImageFormatException("Invalid PE header: " + full);
                stream.Position = offset;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                    throw new BadImageFormatException("The vendor library must be x64: " + full);
            }
            return full;
        }

        internal static void LoadExact(string path)
        {
            if (!Environment.Is64BitProcess)
                throw new BadImageFormatException("Hardware link stress requires an x64 process.");
            lock (LoadGate)
            {
                string name = Path.GetFileName(path);
                IntPtr existing = GetModuleHandle(name);
                if (existing != IntPtr.Zero)
                {
                    var actual = new StringBuilder(32768);
                    if (GetModuleFileName(existing, actual, actual.Capacity) == 0 ||
                        !String.Equals(Path.GetFullPath(actual.ToString()), path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A different " + name + " is already loaded in this process; restart before switching SDK version.");
                    return;
                }
                if (LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load exact vendor DLL: " + path);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int size);
    }
}
