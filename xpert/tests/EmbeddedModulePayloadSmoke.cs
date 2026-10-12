using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal static class EmbeddedModulePayloadSmoke
{
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string IndexName = "HVX.Modules.Index.v1";
    static Type loader;
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new InvalidOperationException(why); }
    static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
    static string FileHash(string path) { return Hash(File.ReadAllBytes(path)); }
    static object Call(string name, params object[] args)
    { return loader.GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args); }
    static string Resolve(Assembly assembly, string cache) { return (string)Call("ResolveModulesRoot", assembly, cache); }
    static void Reject(Action action, string why)
    {
        try { action(); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Check(error is IOException || error is InvalidDataException || error is ArgumentException, why + " rejects with a controlled integrity/path exception: " + error.GetType().Name);
            return;
        }
        throw new InvalidOperationException("Expected rejection: " + why);
    }
    static byte[] ReadIndex(Assembly assembly)
    { using (var stream = assembly.GetManifestResourceStream(IndexName)) using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); } }
    static string Index(string relative, long length, string hash)
    { return "XIONGXIN.Modules.v1\n" + relative + "\t" + length + "\t" + hash + "\tHVX.Modules.Blob." + hash + ".gz\n"; }
    static byte[] Gzip(byte[] bytes)
    {
        using (var output = new MemoryStream())
        { using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(bytes, 0, bytes.Length); return output.ToArray(); }
    }
    static Assembly FakePayload(string folder, string index, byte[] blob, string resource, bool extra = false)
    {
        string name = "SyntheticPayload" + Guid.NewGuid().ToString("N"), filename = name + ".dll";
        var builder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.RunAndSave, folder);
        var module = builder.DefineDynamicModule(name, filename);
        using (var source = new MemoryStream(Encoding.UTF8.GetBytes(index)))
        using (var packed = new MemoryStream(blob ?? new byte[0]))
        using (var spare = new MemoryStream(new byte[] { 1 }))
        {
            module.DefineManifestResource(IndexName, source, ResourceAttributes.Public);
            if (blob != null) module.DefineManifestResource(resource, packed, ResourceAttributes.Public);
            if (extra) module.DefineManifestResource("HVX.Modules.Blob." + new string('F', 64) + ".gz", spare, ResourceAttributes.Public);
            builder.Save(filename);
        }
        return Assembly.LoadFile(Path.Combine(folder, filename));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CreateSymbolicLink(string symbolicFileName, string targetFileName, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    static bool Junction(string path, string target)
    {
        Directory.CreateDirectory(path);
        byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
        byte[] printable = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        using (var bytes = new MemoryStream())
        using (var writer = new BinaryWriter(bytes))
        {
            writer.Write(0xA0000003u); writer.Write((ushort)(8 + substitute.Length + printable.Length + 4)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)substitute.Length); writer.Write((ushort)(substitute.Length + 2)); writer.Write((ushort)printable.Length);
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(printable); writer.Write((ushort)0); writer.Flush();
            byte[] buffer = bytes.ToArray();
            using (var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            { int returned; return !handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero); }
        }
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass embedded distribution root (or its assembly) and build-contained evidence root.");
            string supplied = Path.GetFullPath(args[0]), distribution = File.Exists(supplied) ? Path.GetDirectoryName(supplied) : supplied;
            string host = File.Exists(supplied) ? supplied : Path.Combine(distribution, "Xiongxin.OWTS.Desktop.exe");
            string qa = Path.GetFullPath(args[1]);
            if (host.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0 || qa.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0)
                throw new ArgumentException("Use isolated build output, not an installed program or real user cache.");
            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            string evidence = Path.Combine(qa, "embedded-" + id); Directory.CreateDirectory(evidence);
            string isolated = Path.Combine(evidence, "single-executable"); Directory.CreateDirectory(isolated);
            string copied = Path.Combine(isolated, Path.GetFileName(host)); File.Copy(host, copied);
            Check(!Directory.Exists(Path.Combine(isolated, "modules")), "Copied executable has no modules sidecar");
            Assembly owner = Assembly.LoadFile(copied);
            loader = owner.GetType("Xiongxin.OWTS.Desktop.EmbeddedModulePayload", true);
            byte[] index = ReadIndex(owner); string text = new UTF8Encoding(false, true).GetString(index);
            string[][] rows = text.Split('\n').Skip(1).Where(line => line.Length > 0).Select(line => line.Split('\t')).ToArray();
            Check(rows.Length > 0, "Executable contains module manifest");
            Call("ValidateIndex", text); Check(true, "Actual embedded index validates");
            // Keep isolated cache paths short enough to test the actual product
            // files, rather than accidentally test the legacy MAX_PATH guard.
            string build = host.Substring(0, host.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) + 6);
            string caches = Path.Combine(build, "ep-" + id); Directory.CreateDirectory(caches);
            File.WriteAllText(Path.Combine(evidence, "isolated-cache-root.txt"), caches, Encoding.UTF8);
            string cache = Path.Combine(caches, "main");
            string modules = Resolve(owner, cache);
            Check(Directory.Exists(modules) && modules.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Single executable unpacks into controlled cache");
            Check(!Directory.Exists(Path.Combine(isolated, "modules")), "Resolver does not recreate a deployment-side modules directory");
            Check(Directory.GetFiles(modules, "*", SearchOption.AllDirectories).Length == rows.Length, "Extracted inventory equals embedded inventory");
            foreach (string[] row in rows)
            {
                string path = Path.Combine(modules, row[0].Replace('/', Path.DirectorySeparatorChar));
                Check(new FileInfo(path).Length == long.Parse(row[1]) && FileHash(path) == row[2], "Exact verified file: " + row[0]);
            }
            Check(Resolve(owner, cache) == modules, "Verified cache is reused");
            var provisioning=owner.GetType("Xiongxin.OWTS.Desktop.FirstHardwareProvisioning",true);
            var connectorPath=provisioning.GetMethod("GetVerifiedConnectorPath",Flags,null,new[]{typeof(Assembly),typeof(string)},null);
            Check(connectorPath!=null,"Connector has the same isolated embedded resolver contract");
            string verifiedConnector=(string)connectorPath.Invoke(null,new object[]{owner,cache});
            Check(verifiedConnector==Path.Combine(modules,"HardwareIntegration","HardwareIntegration.exe"),"Device connector uses verified internal payload without an external module folder");
            string external = Path.Combine(isolated, "modules", "DataAnalysis"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"), "not a real DLL");
            Check(Resolve(owner, cache) == modules, "Untrusted same-name external DLL is ignored when embedded resources exist");

            string concurrent = Path.Combine(caches, "parallel");
            var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() => Resolve(owner, concurrent))).ToArray();
            Check(Task.WaitAll(tasks, TimeSpan.FromSeconds(90)), "Concurrent cache preparation terminates");
            Check(tasks.Select(task => task.Result).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1, "All concurrent callers receive one verified published version");
            Check(Directory.GetDirectories(concurrent).Length == 1, "Concurrency leaves only one complete version, no duplicated publication");

            string target = Path.Combine(modules, rows.OrderBy(row => long.Parse(row[1])).First(row => long.Parse(row[1]) > 0)[0].Replace('/', Path.DirectorySeparatorChar));
            byte[] original = File.ReadAllBytes(target), modified = (byte[])original.Clone(); modified[0] ^= 1;
            File.WriteAllBytes(target, modified);
            Reject(() => Resolve(owner, cache), "Same-length modified cached file");
            Check(File.ReadAllBytes(target).SequenceEqual(modified), "Fail-closed cache validation neither overwrites nor deletes suspect data");
            File.WriteAllBytes(target, original); Check(Resolve(owner, cache) == modules, "Exact original cached bytes validate again");
            string held = target + ".held"; File.Move(target, held);
            Reject(() => Resolve(owner, cache), "Missing/renamed cached file"); File.Move(held, target);
            string unknown = Path.Combine(modules, "injected.dll"); File.WriteAllText(unknown, "extra");
            Reject(() => Resolve(owner, cache), "Additional cached DLL"); File.Move(unknown, Path.Combine(evidence, "rejected-extra.dll"));
            string unexpectedDirectory = Path.Combine(modules, "unexpected-empty-folder"); Directory.CreateDirectory(unexpectedDirectory);
            Reject(() => Resolve(owner, cache), "Unexpected empty cache directory"); Directory.Move(unexpectedDirectory, Path.Combine(evidence, "rejected-empty-folder"));
            string receipt = Path.Combine(Path.GetDirectoryName(modules), "payload.index"); File.WriteAllText(receipt, "tampered");
            Reject(() => Resolve(owner, cache), "Modified cache receipt"); File.WriteAllBytes(receipt, index);
            Check(Resolve(owner, cache) == modules, "Restored exact receipt and file inventory validate");
            string hardLink = Path.Combine(evidence, "same-cache-file-hardlink");
            bool linked = CreateHardLink(hardLink, target, IntPtr.Zero);
            int linkError = Marshal.GetLastWin32Error();
            Check(linked, "Create isolated hard-link fixture; WindowsError=" + linkError);
            Reject(() => Resolve(owner, cache), "Hard-linked cached file even with correct hash");
            // Remove only the exactly named test link, not its cache target.
            File.Delete(hardLink);
            Check(Resolve(owner, cache) == modules, "Single-link cache verifies after removing its test-owned hard link");

            string sampleHash = Hash(new byte[] { 1, 2, 3 });
            foreach (string relative in new[] { "../escape.dll", "Module/../escape.dll", "C:/escape.dll", "/absolute.dll", "Module\\file.dll", "Module//file.dll", "Module/file.dll:stream", "Module/CON.dll", "Module/LPT1", "Module/file. ", "Module/file.", "Module/\tfile.dll", "Module/\0file.dll" })
                Reject(() => Call("ValidateIndex", Index(relative, 3, sampleHash)), "Invalid embedded path: " + relative);
            string valid = Index("Module/file.dll", 3, sampleHash);
            Reject(() => Call("ValidateIndex", valid + valid.Split('\n')[1] + "\n"), "Duplicate path");
            Reject(() => Call("ValidateIndex", valid + valid.Split('\n')[1].Replace("Module/file.dll", "module/FILE.DLL") + "\n"), "Case-insensitive duplicate path");
            Reject(() => Call("ValidateIndex", valid + valid.Split('\n')[1].Replace("Module/file.dll", "Module/file.dll/nested.dll") + "\n"), "File/directory collision");
            Reject(() => Call("ValidateIndex", valid.Replace("\t3\t", "\t-1\t")), "Negative expansion length");
            Reject(() => Call("ValidateIndex", valid.Replace("\t3\t", "\t268435457\t")), "Oversized expansion length");
            Reject(() => Call("ValidateIndex", valid.Replace(sampleHash, sampleHash.ToLowerInvariant())), "Noncanonical hash");
            Reject(() => Call("ValidateIndex", valid.TrimEnd('\n')), "Unterminated index");
            Reject(() => Call("ValidateIndex", valid.Replace("\n", "\r\n")), "Noncanonical index newlines");
            Reject(() => Resolve(Assembly.GetExecutingAssembly(), Path.Combine(caches, "no-index")), "Missing index never uses explicit-overload external fallback");
            Reject(() => Resolve(owner, "relative-cache"), "Relative cache root");
            Reject(() => Resolve(owner, Path.GetPathRoot(cache)), "Volume root cache");
            Reject(() => Resolve(owner, Path.Combine(caches, new string('a', 180))), "Too-long cache paths are rejected before extraction");

            string blobName = "HVX.Modules.Blob." + sampleHash + ".gz";
            var fakeCases = new[] {
                FakePayload(evidence, valid, null, blobName),
                FakePayload(evidence, valid, new byte[] { 1, 2, 3 }, blobName),
                FakePayload(evidence, valid, Gzip(new byte[] { 4, 5, 6 }), blobName),
                FakePayload(evidence, valid, Gzip(new byte[] { 1, 2, 3, 4 }), blobName),
                FakePayload(evidence, valid, Gzip(new byte[] { 1, 2 }), blobName),
                FakePayload(evidence, valid, Gzip(new byte[] { 1, 2, 3 }), blobName, true)
            };
            for (int i = 0; i < fakeCases.Length; i++)
            {
                string corruptCache = Path.Combine(caches, "corrupt-" + i);
                Assembly fake = fakeCases[i]; Reject(() => Resolve(fake, corruptCache), "Corrupt embedded payload case " + i);
                Check(!Directory.Exists(Path.Combine(corruptCache, Hash(Encoding.UTF8.GetBytes(valid)))), "Failed resource integrity never publishes a version");
            }
            string link = Path.Combine(caches, "linked-cache");
            if (CreateSymbolicLink(link, cache, 3))
            {
                Reject(() => Resolve(owner, link), "Reparse-point cache root");
                string fileLink = Path.Combine(modules, "linked-file.dll");
                Check(CreateSymbolicLink(fileLink, target, 2), "Create isolated file link fixture");
                Reject(() => Resolve(owner, cache), "Reparse-point cache file");
                Console.WriteLine("REPARSE_POINT_TEST=PASS");
            }
            else
            {
                Console.WriteLine("SYMLINK_TEST=UNAVAILABLE; WINDOWS_ERROR=" + Marshal.GetLastWin32Error());
                string junction = Path.Combine(caches, "junction-cache");
                Check(Junction(junction, cache) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "Create isolated junction without symlink privilege");
                Reject(() => Resolve(owner, junction), "Reparse-point cache root junction");
                string inner = Path.Combine(modules, "linked-module-folder");
                Check(Junction(inner, evidence), "Create isolated module-folder junction");
                Reject(() => Resolve(owner, cache), "Reparse-point cached directory junction");
                Console.WriteLine("REPARSE_POINT_JUNCTION_TEST=PASS");
            }
            Console.WriteLine("EMBEDDED_FILES=" + rows.Length + "; BUNDLE_SHA256=" + Hash(index));
            Console.WriteLine("EMBEDDED_MODULE_PAYLOAD_PASS=" + checks); Console.WriteLine("EVIDENCE=" + evidence); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
