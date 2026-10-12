using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Xiongxin.OWTS.Data;

// Offline microbenchmark. Compile with the ACTUAL LocationCycleRecord.cs, without
// HYBRID_CORE, so the managed comparison is the original implementation, not a
// transcription. Timing/working-set figures are diagnostics, never pass thresholds.
internal static class HybridPhasePerformanceSmoke
{
    private const int Count = 100000, ChunkSize = 4096, MeasuredRounds = 5, Repeats = 3;
    private static double sink;
    private static readonly object gate = new object();
    private static readonly MethodInfo AllocationCounter = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ScalarDelegate(double index, double rate, uint samples,
        [In] double[] crossings, uint crossingCount, int positive, out double phase);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BatchDelegate([In] double[] indices, uint length, double rate,
        uint samples, [In] double[] crossings, uint crossingCount, int positive,
        [Out] double[] phases);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity info);

    private sealed class Native : IDisposable
    {
        private readonly FileStream pinned;
        private IntPtr library;
        internal readonly ScalarDelegate Scalar;
        internal readonly BatchDelegate Batch;
        internal readonly double LoadMilliseconds;
        internal Native(string file, string expected)
        {
            if (IntPtr.Size != 8 || !Path.IsPathRooted(file)) throw new InvalidDataException("Absolute x64 native library path required.");
            string path = Path.GetFullPath(file), walk = path;
            while (!String.IsNullOrEmpty(walk))
            {
                if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("No linked benchmark inputs.");
                walk = Path.GetDirectoryName(walk);
            }
            if (expected == null || expected.Length != 64) throw new InvalidDataException("Independently pinned SHA-256 required.");
            foreach (char value in expected) if (!Uri.IsHexDigit(value)) throw new InvalidDataException("Invalid SHA-256.");
            var clock = Stopwatch.StartNew();
            try
            {
                pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (pinned.Length < 1024 || pinned.Length > 16 * 1024 * 1024) throw new InvalidDataException("Native file budget exceeded.");
                FileIdentity identity;
                if (!GetFileInformationByHandle(pinned.SafeFileHandle, out identity) || identity.LinkCount != 1)
                    throw new InvalidDataException("Native file must be a verified single-link file.");
                using (SHA256 sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(pinned)).Replace("-", "");
                    if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native SHA mismatch.");
                }
                library = LoadLibraryEx(path, IntPtr.Zero, 0x100u | 0x800u);
                if (library == IntPtr.Zero) throw new InvalidDataException("Approved native library did not load.");
                var abi = Export<AbiDelegate>("hx_core_abi_version");
                if (abi() != 1) throw new InvalidDataException("Unsupported ABI.");
                Scalar = Export<ScalarDelegate>("hx_phase_cycle");
                Batch = Export<BatchDelegate>("hx_phase_cycle_batch");
                clock.Stop(); LoadMilliseconds = clock.Elapsed.TotalMilliseconds;
            }
            catch { Dispose(); throw; }
        }
        private T Export<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(library, name);
            if (address == IntPtr.Zero) throw new InvalidDataException("Missing approved native export.");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        public void Dispose()
        {
            if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
            if (pinned != null) pinned.Dispose();
        }
    }

    private static long? AllocatedBytes()
    {
        return AllocationCounter == null ? (long?)null : (long)AllocationCounter.Invoke(null, null);
    }

    private static Dictionary<string, object> Measure(string name, Action action, long bufferBytes, int callsPerRepeat)
    {
        action(); // JIT/marshaler warm-up is not included in measurements.
        var rounds = new List<object>();
        var elapsed = new List<double>();
        for (int number = 1; number <= MeasuredRounds; number++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long before = GC.GetTotalMemory(false), workingBefore;
            using (var process = Process.GetCurrentProcess()) { process.Refresh(); workingBefore = process.WorkingSet64; }
            long? allocationBefore = AllocatedBytes();
            var clock = Stopwatch.StartNew();
            for (int repeat = 0; repeat < Repeats; repeat++) action();
            clock.Stop();
            long? allocationAfter = AllocatedBytes();
            long after = GC.GetTotalMemory(false), workingAfter;
            using (var process = Process.GetCurrentProcess()) { process.Refresh(); workingAfter = process.WorkingSet64; }
            elapsed.Add(clock.Elapsed.TotalMilliseconds);
            rounds.Add(new Dictionary<string, object> {
                {"round",number}, {"elapsedMilliseconds",clock.Elapsed.TotalMilliseconds},
                {"gen0Collections",GC.CollectionCount(0)-gc0}, {"gen1Collections",GC.CollectionCount(1)-gc1},
                {"gen2Collections",GC.CollectionCount(2)-gc2}, {"managedRetainedBytesBefore",before},
                {"managedRetainedBytesAfter",after}, {"workingSetBeforeBytes",workingBefore},
                {"workingSetAfterBytes",workingAfter},
                {"allocatedBytes",allocationBefore.HasValue && allocationAfter.HasValue ? (object)(allocationAfter.Value-allocationBefore.Value) : null}
            });
        }
        elapsed.Sort();
        return new Dictionary<string, object> {
            {"name",name}, {"pointsPerRepeat",Count}, {"repeatsPerRound",Repeats},
            {"interopCallsPerRepeat",callsPerRepeat}, {"reusableDataBufferBytes",bufferBytes},
            {"medianMillisecondsPer100000Points",elapsed[elapsed.Count/2]/Repeats}, {"rounds",rounds}
        };
    }

    private static LocationCycleReference Reference(int crossingCount)
    {
        var crossing = new double[crossingCount];
        double rate = 100000, first = -.25/rate, last = (Count-.5)/rate, weight = 0;
        var weights = new double[crossingCount-1];
        for(int i=0;i<weights.Length;i++){weights[i]=1+.2*Math.Sin(i*.42);weight+=weights[i];}
        crossing[0]=first;
        double cumulative=0;
        for(int i=1;i<crossingCount;i++){cumulative+=weights[i-1];crossing[i]=first+(last-first)*(cumulative/weight);}
        crossing[crossingCount-1]=last;
        var reference = new LocationCycleReference {CrossingSeconds=crossing,SampleRateHz=rate,SampleCount=Count,
            CaptureStartSeconds=0,FirstCrossingPositive=false};
        reference.Validate(); return reference;
    }

    private static Dictionary<string, object> Scenario(Native native, int crossingCount)
    {
        var reference=Reference(crossingCount);
        var indices=new double[Count];
        for(int i=0;i<indices.Length;i++)indices[i]=i+(i%2==0?0:.125);
        var snapshot=(double[])reference.CrossingSeconds.Clone();
        var fullOutput=new double[Count];
        var chunkIndices=new double[ChunkSize];var chunkOutput=new double[ChunkSize];
        uint length=(uint)snapshot.Length;
        int status=native.Batch(indices,Count,reference.SampleRateHz,Count,snapshot,length,0,fullOutput);
        if(status!=0)throw new InvalidDataException("Native batch reference failed.");
        double maximumError=0;
        for(int i=0;i<indices.Length;i++)
        {
            double scalar;status=native.Scalar(indices[i],reference.SampleRateHz,Count,snapshot,length,0,out scalar);
            if(status!=0)throw new InvalidDataException("Native scalar reference failed.");
            double managed=reference.PhaseAt(indices[i]);
            maximumError=Math.Max(maximumError,Math.Max(Math.Abs(managed-scalar),Math.Abs(managed-fullOutput[i])));
            if(!LocationCycleReference.Finite(managed)||!LocationCycleReference.Finite(scalar)||!LocationCycleReference.Finite(fullOutput[i])||maximumError>2e-9)
                throw new InvalidDataException("Actual managed/native phase parity failed.");
        }
        var measurements=new List<object>();
        measurements.Add(Measure("actual_managed_PhaseAt",()=>{
            double sum=0;for(int i=0;i<Count;i++)sum+=reference.PhaseAt(indices[i]);sink=sum;
        },indices.LongLength*sizeof(double),0));
        measurements.Add(Measure("native_scalar_private_snapshot_once",()=>{
            double sum=0,phase;for(int i=0;i<Count;i++){
                int error=native.Scalar(indices[i],reference.SampleRateHz,Count,snapshot,length,0,out phase);
                if(error!=0)throw new InvalidDataException("Scalar performance request failed.");sum+=phase;
            }sink=sum;
        },(indices.LongLength+snapshot.LongLength)*sizeof(double),Count));
        measurements.Add(Measure("old_hot_path_scalar_clone_and_gate_each_call_simulation",()=>{
            double sum=0,phase;for(int i=0;i<Count;i++){
                var copied=(double[])reference.CrossingSeconds.Clone();
                lock(gate){if(native.Scalar==null)throw new InvalidDataException("No approved delegate.");}
                int error=native.Scalar(indices[i],reference.SampleRateHz,Count,copied,(uint)copied.Length,0,out phase);
                if(error!=0)throw new InvalidDataException("Scalar snapshot simulation failed.");sum+=phase;
            }sink=sum;
        },indices.LongLength*sizeof(double),Count));
        measurements.Add(Measure("native_batch_100000",()=>{
            int error=native.Batch(indices,Count,reference.SampleRateHz,Count,snapshot,length,0,fullOutput);
            if(error!=0)throw new InvalidDataException("Full batch performance request failed.");
            double sum=0;for(int i=0;i<Count;i++)sum+=fullOutput[i];sink=sum;
        },(indices.LongLength+fullOutput.LongLength+snapshot.LongLength)*sizeof(double),1));
        measurements.Add(Measure("native_chunks_4096_reuse_buffers",()=>{
            double sum=0;for(int first=0;first<Count;first+=ChunkSize){
                int count=Math.Min(ChunkSize,Count-first);Array.Copy(indices,first,chunkIndices,0,count);
                int error=native.Batch(chunkIndices,(uint)count,reference.SampleRateHz,Count,snapshot,length,0,chunkOutput);
                if(error!=0)throw new InvalidDataException("Chunk performance request failed.");
                for(int i=0;i<count;i++)sum+=chunkOutput[i];
            }sink=sum;
        },(indices.LongLength+chunkIndices.LongLength+chunkOutput.LongLength+snapshot.LongLength)*sizeof(double),(Count+ChunkSize-1)/ChunkSize));
        return new Dictionary<string,object>{{"crossingCount",crossingCount},{"nonUniformPeriods",true},
            {"checkedScalarAndBatchAgainstActualManagedPointCount",Count},{"maximumPhaseErrorDegrees",maximumError},
            {"measurements",measurements},{"accuracyStatus","PASS"}};
    }

    private static string Hash(string path)
    {
        using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
    }
    private static int Main(string[] args)
    {
        string outputRoot=null;
        try
        {
            if(args.Length!=3||!Path.IsPathRooted(args[0])||!Path.IsPathRooted(args[2]))throw new ArgumentException("Arguments: absoluteApprovedDll pinnedSha256 absoluteNewProofDirectory.");
            outputRoot=Path.GetFullPath(args[2]);
            if(Directory.Exists(outputRoot)||File.Exists(outputRoot))throw new IOException("Proof directory already exists; overwrite prohibited.");
            Directory.CreateDirectory(outputRoot);
            var scenarios=new List<object>();double load;
            using(var native=new Native(args[0],args[1])){
                load=native.LoadMilliseconds;scenarios.Add(Scenario(native,3));scenarios.Add(Scenario(native,201));
            }
            var report=new Dictionary<string,object>{{"schema","HVX.HybridPhasePerformance/1"},{"status","PASS"},
                {"utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)},{"architecture",IntPtr.Size==8?"x64":"x86"},
                {"hardwareAccessed",false},{"settingsModified",false},{"wholeApplicationAcceptance",false},
                {"nativeLibrarySha256",Hash(args[0])},{"benchmarkExeSha256",Hash(Assembly.GetExecutingAssembly().Location)},
                {"nativeLoadMilliseconds",load},{"pointsPerScenario",Count},{"measuredRoundsPerImplementation",MeasuredRounds},
                {"warmupExcluded",true},{"strictTimingThresholdApplied",false},{"scenarios",scenarios},
                {"sink",sink},{"limitations",new[]{"Offline CPU/interop microbenchmark, not GUI responsiveness or physical-device acceptance.",
                "Old hot path is a clearly labelled clone+gate simulation, not a direct production wrapper benchmark.",
                "Working-set figures are diagnostics and may include JIT/allocator/system noise; no strict small-time threshold.",
                "Chunk data buffers add only 65536 bytes plus crossings; input indices are bench fixtures, not a proposed 16M-phase allocation."}}};
            string json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Serialize(report);
            using(var file=new FileStream(Path.Combine(outputRoot,"verification.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(file)){writer.Write(json);writer.WriteLine();}
            Console.WriteLine("PASS offline phase interop microbenchmark; exact managed/native comparisons 200000; proof="+outputRoot);
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1;}
    }
}
