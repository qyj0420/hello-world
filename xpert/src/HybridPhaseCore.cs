using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Xiongxin.OWTS.Data
{
    // Opt-in managed scalar / native batch computation: no SDK, outputs,
    // model download or training. Scalar phase mathematics remains in C#.
    internal static class HybridPhaseCore
    {
#if HYBRID_CORE
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint AbiDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CycleDelegate(double index, double rate,
            uint samples, [In] double[] crossings, uint crossingCount, int positive, out double output);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
        [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity info);
        private static readonly object Gate = new object();
        private static IntPtr library;
        private static FileStream lockedLibrary;
        private static CycleDelegate cycle;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CycleBatchDelegate([In] double[] indices, uint count, double rate,
            uint samples, [In] double[] crossings, uint crossingCount, int positive, [Out] double[] output);
        private static CycleBatchDelegate cycleBatch;
        internal static bool Enabled { get { return true; } }
        internal static bool Loaded { get { lock (Gate) return cycle != null; } }

        internal sealed class CycleSession
        {
            private readonly double[] source, ownedCrossings;
            private readonly double rate;
            private readonly int samples;
            private readonly bool positive;
            internal double SampleRateHz { get { return rate; } }
            internal double InitialPhaseDegrees { get { return PhaseAt(0); } }
            internal double AverageFrequencyHz { get { return (ownedCrossings.Length - 1) / (2 * (ownedCrossings[ownedCrossings.Length - 1] - ownedCrossings[0])); } }
            internal CycleSession(double rate, int samples, double[] crossings, bool positive)
            {
            if (crossings == null || samples < 16 || samples > 16000000 || crossings.Length < 3 || crossings.Length > 201)
                throw new InvalidDataException("Invalid native cycle reference dimensions.");
            this.rate = rate; this.samples = samples; this.positive = positive; source = crossings;
            ownedCrossings = (double[])crossings.Clone();
            EnsureLoaded();
            // Validate native reference once, even if the first requested index is outside it.
            double output; int status = cycle(0, rate, (uint)samples, ownedCrossings, (uint)ownedCrossings.Length, positive ? 1 : 0, out output);
            if (status != 0) throw new InvalidDataException("Invalid native cycle reference (" + status + ").");
            }
            internal bool Matches(double rate, int samples, double[] crossings, bool positive)
            {
                if (this.rate != rate || this.samples != samples || !ReferenceEquals(source, crossings) || this.positive != positive) return false;
                for (int i = 0; i < ownedCrossings.Length; i++) if (crossings[i] != ownedCrossings[i]) return false;
                return true;
            }
            private bool InWindow(double index)
            {
            if (Double.IsNaN(index) || Double.IsInfinity(index) || index < 0 || index >= samples) return false;
            double time = index / rate;
            return time >= ownedCrossings[0] - 1e-12 && time <= ownedCrossings[ownedCrossings.Length - 1] + 1e-12;
            }
            internal double PhaseAt(double index)
            {
                // Keep the original C# order of operations and its exact 360-degree
                // floating-point edge. The constructor has already pinned/loaded
                // the approved DLL and validated this private immutable reference.
                if (Double.IsNaN(index) || Double.IsInfinity(index) || index < 0 || index >= samples) return Double.NaN;
                double t = index / rate;
                if (t < ownedCrossings[0] - 1e-12 || t > ownedCrossings[ownedCrossings.Length - 1] + 1e-12) return Double.NaN;
                int lo = 0, hi = ownedCrossings.Length - 1;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if (ownedCrossings[mid] <= t) lo = mid; else hi = mid; }
                double phase = (positive ? 0 : 180) + 180 * (lo + (t - ownedCrossings[lo]) / (ownedCrossings[hi] - ownedCrossings[lo]));
                phase %= 360; return phase < 0 ? phase + 360 : phase;
            }
            internal double[] PhasesAt(double[] indices)
            {
                if (indices == null || indices.Length > 16000000) throw new ArgumentException("Invalid cycle phase batch.");
                var result = new double[indices.Length];
                if (indices.Length == 0) return result;
                int capacity = Math.Min(4096, indices.Length);
                var input = new double[capacity]; var output = new double[capacity]; var positions = new int[capacity];
                for (int start = 0; start < indices.Length; start += capacity)
                {
                    int used = 0;
                    for (int i = start; i < Math.Min(indices.Length, start + capacity); i++)
                    {
                        double index = indices[i];
                        if (!InWindow(index)) { result[i] = Double.NaN; continue; }
                        input[used] = index; positions[used++] = i;
                    }
                    if (used == 0) continue;
                    int status = cycleBatch(input, (uint)used, rate, (uint)samples, ownedCrossings, (uint)ownedCrossings.Length, positive ? 1 : 0, output);
                    for (int i = 0; i < used; i++) { CheckResult(status, output[i]); result[positions[i]] = output[i]; }
                }
                return result;
            }
        }
        private static void CheckResult(int status, double output)
        {
            // A tiny negative phase + 360 can round to exactly 360 in the
            // original C# contract. Preserve that existing floating-point edge.
            if (status != 0 || Double.IsNaN(output) || Double.IsInfinity(output) || output < 0 || output > 360)
                throw new InvalidDataException("Native cycle-phase calculation rejected this record (" + status + ").");
        }

        private static FileStream OpenPinnedLibraryLeaf(string path)
        {
            // Open the original leaf entry without following a reparse point.
            // Checking attributes before an ordinary FileStream open is not enough:
            // a substituted file symlink would lock its target, not this path entry.
            SafeFileHandle leaf = CreateFile(path, 0x80000000u, 1u, IntPtr.Zero, 3u, 0x00200000u, IntPtr.Zero);
            try
            {
                if (leaf.IsInvalid) throw new InvalidDataException("Cannot open the original native library leaf (" + Marshal.GetLastWin32Error() + ").");
                FileIdentity info;
                if (!GetFileInformationByHandle(leaf, out info) || (info.Attributes & (0x400u | 0x10u)) != 0 || info.LinkCount != 1)
                    throw new InvalidDataException("The native library must be an original, single-link regular file.");
                var stream = new FileStream(leaf, FileAccess.Read, 4096, false);
                leaf = null; // FileStream now owns this same verified read/no-write/no-delete handle.
                return stream;
            }
            finally { if (leaf != null) leaf.Dispose(); }
        }
        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (cycle != null) return;
                if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The hybrid core requires an x64 process.");
                string modules;
#if HYBRID_ROOT
                modules = Xiongxin.OWTS.Desktop.EmbeddedModulePayload.ResolveModulesRoot();
#else
                string owner = Path.GetDirectoryName(typeof(HybridPhaseCore).Assembly.Location);
                if (String.IsNullOrEmpty(owner)) throw new InvalidDataException("Hybrid module has no verified file location.");
                modules = Path.GetDirectoryName(owner);
#endif
                string path = Path.GetFullPath(Path.Combine(modules, "Hybrid", "HvxHybridCore.dll"));
                string walk = path;
                while (!String.IsNullOrEmpty(walk))
                {
                    if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked hybrid library paths are prohibited.");
                    walk = Path.GetDirectoryName(walk);
                }
                string expected;
                using (Stream pin = typeof(HybridPhaseCore).Assembly.GetManifestResourceStream("HVX.HybridNative.Pin"))
                {
                    if (pin == null || pin.Length < 64 || pin.Length > 128) throw new InvalidDataException("Missing compiled native-library pin.");
                    using (var reader = new StreamReader(pin)) expected = reader.ReadToEnd().Trim();
                }
                if (expected.Length != 64) throw new InvalidDataException("Invalid native-library pin.");
                foreach (char value in expected) if (!((value >= '0' && value <= '9') || (value >= 'A' && value <= 'F')))
                    throw new InvalidDataException("Invalid native-library pin encoding.");
                FileStream held = null;
                IntPtr loaded = IntPtr.Zero;
                var parents = new List<SafeFileHandle>();
                try
                {
                    var paths = new List<string>();
                    walk = Path.GetDirectoryName(path);
                    while (!String.IsNullOrEmpty(walk)) { paths.Add(walk); walk = Path.GetDirectoryName(walk); }
                    paths.Reverse();
                    foreach (string parent in paths)
                    {
                        var handle = CreateFile(parent, 0x80u, 3u, IntPtr.Zero, 3u, 0x02200000u, IntPtr.Zero);
                        parents.Add(handle);
                        FileIdentity directoryInfo;
                        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out directoryInfo) || (directoryInfo.Attributes & 0x400u) != 0)
                            throw new InvalidDataException("Cannot pin the native library's original directory path.");
                    }
                    held = OpenPinnedLibraryLeaf(path);
                    if (held.Length < 1024 || held.Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid native library length.");
                    string actual;
                    using (SHA256 sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(held)).Replace("-", "");
                    if (!String.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidDataException("Native library integrity check failed; calculation is blocked.");
                    // No PATH/current-directory dependency search; the core is statically CRT-linked.
                    loaded = LoadLibraryEx(path, IntPtr.Zero, 0x100u | 0x800u);
                    if (loaded == IntPtr.Zero) throw new InvalidDataException("Cannot load the approved native phase core (" + Marshal.GetLastWin32Error() + ").");
                    IntPtr abiAddress = GetProcAddress(loaded, "hx_core_abi_version"), phaseAddress = GetProcAddress(loaded, "hx_phase_cycle"), batchAddress = GetProcAddress(loaded, "hx_phase_cycle_batch");
                    if (abiAddress == IntPtr.Zero || phaseAddress == IntPtr.Zero || batchAddress == IntPtr.Zero) throw new InvalidDataException("Missing native ABI exports.");
                    var abi = (AbiDelegate)Marshal.GetDelegateForFunctionPointer(abiAddress, typeof(AbiDelegate));
                    if (abi() != 1) throw new InvalidDataException("Unsupported native phase ABI.");
                    var approved = (CycleDelegate)Marshal.GetDelegateForFunctionPointer(phaseAddress, typeof(CycleDelegate));
                    var approvedBatch = (CycleBatchDelegate)Marshal.GetDelegateForFunctionPointer(batchAddress, typeof(CycleBatchDelegate));
                    library = loaded; lockedLibrary = held; cycleBatch = approvedBatch; cycle = approved;
                    loaded = IntPtr.Zero; held = null;
                }
                finally { if (loaded != IntPtr.Zero) FreeLibrary(loaded); if (held != null) held.Dispose(); foreach (var parent in parents) parent.Dispose(); }
            }
        }
#else
        internal static bool Enabled { get { return false; } }
        internal static bool Loaded { get { return false; } }
#endif
    }
}
