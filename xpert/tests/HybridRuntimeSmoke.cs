using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;

internal static class HybridRuntimeSmoke
{
    private static int checks;
    private static int symlinkExecuted, symlinkSkipped;
    private const string LocationAssemblyName="fc40b1d6ae7648e9b4abf7b9.V1.0.17";
    private static PrivateMapping locationMap;
    private static void Check(bool pass, string name) { checks++; if (!pass) throw new Exception(name); }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--reject") return Reject(args[1], false);
            if (args.Length == 3 && args[0] == "--reject-hot")
            {locationMap=new PrivateMapping(args[2],args[1]);return Reject(args[1],true);}
            if (args.Length != 2) throw new ArgumentException("BuildRoot and isolated QA root required.");
            string build = Path.GetFullPath(args[0]), qa = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(qa);
            string pressure = Path.Combine(build, "modules", "PressureTest", "Xiongxin.OWTS.PressureTest.dll");
            Assembly owner = Assembly.LoadFrom(pressure);
            Type type = owner.GetType("Xiongxin.OWTS.Data.LocationCycleReference", true);
            Type bridge = owner.GetType("Xiongxin.OWTS.Data.HybridPhaseCore", true);
            Check((bool)bridge.GetProperty("Enabled", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null), "hybrid must be enabled in the actual pressure DLL");
            double[] crossings = { -.0003, .0123, .0307, .0439, .0710 };
            int count = 72;
            for (int direction = 0; direction != 2; direction++)
            {
                object reference = Make(type, crossings, count, 1000, direction != 0);
                type.GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(reference, null);
                for (int i = 0; i < count * 16; i++)
                {
                    double index = i / 16.0;
                    if (index >= count) continue;
                    double actual = Phase(type, reference, index);
                    double expected = Managed(index, 1000, crossings, direction != 0);
                    Check((Double.IsNaN(actual) && Double.IsNaN(expected)) || Math.Abs(actual - expected) <= 1e-11, "native/managed irregular half-cycle phase parity");
                }
                Check(Double.IsNaN(Phase(type, reference, -1)), "negative index does not invent phase");
                Check(Double.IsNaN(Phase(type, reference, count)), "past-the-end index does not invent phase");
                Check(Double.IsNaN(Phase(type, reference, Double.NaN)), "NaN does not enter native core");
                var batchIndices = new double[10003];
                for (int i = 0; i < batchIndices.Length; i++) batchIndices[i] = i % count + .125;
                batchIndices[1] = -1; batchIndices[2] = Double.NaN;
                var phases = (double[])type.GetMethod("PhasesAt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(reference, new object[] { batchIndices });
                for (int i = 0; i < phases.Length; i++)
                {
                    double expected = batchIndices[i] < 0 || batchIndices[i] >= count || Double.IsNaN(batchIndices[i]) ? Double.NaN : Managed(batchIndices[i], 1000, crossings, direction != 0);
                    Check((Double.IsNaN(phases[i]) && Double.IsNaN(expected)) || Math.Abs(phases[i] - expected) <= 1e-11, "actual consumer 4096 chunk phase parity");
                }
            }
            string location = Path.Combine(build, "modules", "PressureTest", "fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
            locationMap=FindLocationMapping(build,location);
            CheckMappingGuards(location,Path.Combine(qa,"private-map-guards"));
            CheckLocationHotPath(location);
            CheckPinnedLeaf(bridge, Path.Combine(qa, "pressure-leaf-handle"));
            CheckPinnedLeaf(ResolveType(Assembly.LoadFrom(location),"Xiongxin.OWTS.Data.HybridPhaseCore"), Path.Combine(qa, "location-leaf-handle"));
            object edge = Make(type, new[] { 1e-18, .01, .019 }, 20, 1000, true);
            Check(Phase(type, edge, 0) == 360, "preserve C# tiny-negative plus360 rounding edge");
            Check((bool)bridge.GetProperty("Loaded", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null), "real DLL was loaded");
            string native = Path.Combine(build, "modules", "Hybrid", "HvxHybridCore.dll");
            bool writeBlocked = false;
            try { using (var file = new FileStream(native, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {} }
            catch (IOException) { writeBlocked = true; }
            Check(writeBlocked, "loaded approved DLL remains write/delete locked");
            foreach (string actualConsumer in new[] { pressure, location })
            foreach (string scenario in new[] { "missing", "tampered", "hardlink", "directory", "symlink" })
            {
                bool hot = actualConsumer == location;
                string modules = Path.Combine(qa, (hot ? "location-hot-" : "pressure-") + scenario, "modules");
                string consumer = Path.Combine(modules, "PressureTest"), hybrid = Path.Combine(modules, "Hybrid");
                Directory.CreateDirectory(consumer); Directory.CreateDirectory(hybrid);
                string copied = Path.Combine(consumer, Path.GetFileName(actualConsumer));
                File.Copy(actualConsumer, copied);
                string target = Path.Combine(hybrid, "HvxHybridCore.dll");
                if (scenario == "tampered") File.WriteAllBytes(target, new byte[2048]);
                if (scenario == "hardlink")
                {
                    string other = Path.Combine(hybrid, "owned-copy.dll"); File.Copy(native, other);
                    if (!CreateHardLink(target, other, IntPtr.Zero)) throw new Exception("Cannot create isolated hardlink fixture.");
                }
                if (scenario == "directory") Directory.CreateDirectory(target);
                if (scenario == "symlink")
                {
                    string other = Path.Combine(hybrid, "owned-copy.dll"); File.Copy(native, other);
                    if (!TryFileSymlink(target, other, hot ? "Location" : "PressureTest")) continue;
                    Type leafOwner=ResolveType(Assembly.LoadFrom(actualConsumer),"Xiongxin.OWTS.Data.HybridPhaseCore");
                    Invalid(()=>OpenLeaf(leafOwner,target),"actual leaf opener rejects the symlink entry without following its approved target");
                    // Failure must release the link entry's handle, not leave the fixture locked.
                    File.Delete(target);
                    Check(!File.Exists(target),"rejected symlink leaf handle is disposed");
                    if (!CreateFileSymlink(target,other)) throw new Exception("Cannot recreate the permitted isolated symlink fixture.");
                }
                using (var process = Process.Start(new ProcessStartInfo { FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = (hot ? "--reject-hot" : "--reject") + " \"" + copied + "\""+(hot?" \""+locationMap.Path+"\"":""), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Native rejection subprocess timed out."); }
                    Check(process.ExitCode == 0, "fail closed for " + (hot ? "actual Location binding " : "PressureTest ") + scenario + ": " + process.StandardError.ReadToEnd());
                }
            }
            Console.WriteLine("HYBRID_RUNTIME_PASS=" + checks + ";hardware=false;actualConsumers=PressureTest,LocationA4HotPath;scalar=CSharp;batch=Native;symlinkExecuted="+symlinkExecuted+";symlinkSkipped="+symlinkSkipped); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static int Reject(string path, bool hot)
    {
        try
        {
            Assembly owner=Assembly.LoadFrom(path);
            Type type = ResolveType(owner,"Xiongxin.OWTS.Data.LocationCycleReference");
            object reference=Make(type, new[] { 0.0, .01, .019 }, 20, 1000, true);
            if(hot)
            {
                Type engine=ResolveType(owner,"HVX.Task17.Location.LocationEngine");
                HotContext(engine,Activator.CreateInstance(engine,true),reference,1000);
            }
            else Phase(type, reference, 1);
            Console.Error.WriteLine("Unapproved native core was accepted."); return 1;
        }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException is IOException || ex.InnerException is InvalidDataException) return 0;
            Console.Error.WriteLine(ex); return 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private const BindingFlags AllInstance=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private const BindingFlags AllStatic=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    private static Type ResolveType(Assembly owner,string originalName)
    {
        if(owner.GetName().Name!=LocationAssemblyName)return owner.GetType(originalName,true);
        if(locationMap==null)throw new InvalidDataException("The protected consumer requires its exact QA mapping authority.");
        return locationMap.Type(owner,originalName);
    }
    private static MethodInfo ResolveMethod(Type owner,string originalName,BindingFlags flags,Type result,params Type[] parameters)
    {
        MethodInfo method=owner.Assembly.GetName().Name==LocationAssemblyName?
            locationMap.Method(owner,originalName,flags,parameters):owner.GetMethod(originalName,flags,null,parameters,null);
        if(method==null||method.ReturnType!=result)throw new MissingMethodException(owner.FullName,originalName+" exact signature");
        return method;
    }
    private static FileStream OpenLeaf(Type bridge,string path)
    {return (FileStream)ResolveMethod(bridge,"OpenPinnedLibraryLeaf",AllStatic,typeof(FileStream),typeof(string)).Invoke(null,new object[]{path});}
    private static void SharingBlocked(Action operation,string name)
    {
        bool blocked=false;
        try{operation();}
        catch(IOException error){int code=error.HResult&0xffff;blocked=code==32||code==33;}
        Check(blocked,name);
    }
    private static void CheckPinnedLeaf(Type bridge,string qa)
    {
        Directory.CreateDirectory(qa);
        string path=Path.Combine(qa,"owned-regular-leaf.bin"),moved=Path.Combine(qa,"owned-moved-leaf.bin");
        var data=new byte[2048];data[0]=73;data[data.Length-1]=109;File.WriteAllBytes(path,data);
        using(var held=OpenLeaf(bridge,path))
        {
            Check(held.Length==data.Length&&held.ReadByte()==data[0],"real production leaf opener reads the verified original regular entry");
            SharingBlocked(()=>{using(var write=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){}},"held original leaf rejects write handles");
            SharingBlocked(()=>File.Delete(path),"held original leaf rejects deletion");
            SharingBlocked(()=>File.Move(path,moved),"held original leaf rejects rename/path replacement");
            Check(File.Exists(path)&&!File.Exists(moved),"held original leaf path remains unchanged");
        }
        using(var write=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))write.WriteByte(data[0]);
        File.Move(path,moved);Check(File.Exists(moved)&&!File.Exists(path),"successful stream disposal releases the original leaf handle");
        File.Move(moved,path);
        string linked=Path.Combine(qa,"owned-hardlink.bin");
        if(!CreateHardLink(linked,path,IntPtr.Zero))throw new Exception("Cannot create isolated leaf hardlink fixture.");
        Invalid(()=>OpenLeaf(bridge,linked),"real leaf opener rejects multi-link regular files");
        using(var write=new FileStream(linked,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))write.WriteByte(data[0]);
        File.Move(linked,moved);Check(File.Exists(moved),"rejected hardlink leaf handle is disposed");
        string directory=Path.Combine(qa,"owned-directory-leaf"),renamed=Path.Combine(qa,"owned-directory-renamed");
        Directory.CreateDirectory(directory);
        Invalid(()=>OpenLeaf(bridge,directory),"real leaf opener rejects a directory entry");
        Directory.Move(directory,renamed);Check(Directory.Exists(renamed),"rejected directory leaf handle is disposed");
        Invalid(()=>OpenLeaf(bridge,Path.Combine(qa,"missing.bin")),"real leaf opener fails closed for a missing entry");
    }
    private static bool CreateFileSymlink(string name,string target)
    {
        if(CreateSymbolicLink(name,target,2u))return true;
        if(Marshal.GetLastWin32Error()==87)return CreateSymbolicLink(name,target,0u);
        return false;
    }
    private static bool TryFileSymlink(string name,string target,string consumer)
    {
        if(CreateFileSymlink(name,target)){symlinkExecuted++;return true;}
        int code=Marshal.GetLastWin32Error();
        if(code!=1314&&code!=5&&code!=50)throw new Exception("Cannot create isolated symlink fixture: "+code);
        symlinkSkipped++;
        Console.WriteLine("HYBRID_RUNTIME_SKIP=consumer="+consumer+";scenario=symlink;win32="+code+";reason=permission_or_filesystem_capability;not_counted_as_pass=true");
        return false;
    }
    private static object HotContext(Type engine, object instance, object reference, double rate)
    {
        object context=Activator.CreateInstance(ResolveType(engine.Assembly,"HVX.Task17.Location.LocationEngine/A4Context"),true);
        context.GetType().GetField("Fs",AllInstance).SetValue(context,rate);
        BindHot(engine,instance,context,reference);return context;
    }
    private static void BindHot(Type engine, object instance, object context, object reference)
    {ResolveMethod(engine,"A4ConfigureAnalysisPhase",AllInstance,typeof(void),context.GetType(),typeof(double),typeof(double),ResolveType(engine.Assembly,"Xiongxin.OWTS.Data.LocationCycleReference")).Invoke(instance,new object[]{context,0d,50d,reference});}
    private static Func<double,double> HotPhase(Type engine,object context)
    {return (Func<double,double>)Delegate.CreateDelegate(typeof(Func<double,double>),context,ResolveMethod(engine,"A4PhaseAt",AllStatic,typeof(double),context.GetType(),typeof(double)));}
    private static Action<System.Collections.Generic.IEnumerable<double>> HotBatch(Type engine,object context)
    {return (Action<System.Collections.Generic.IEnumerable<double>>)Delegate.CreateDelegate(typeof(Action<System.Collections.Generic.IEnumerable<double>>),context,ResolveMethod(engine,"A4PrepareCyclePhases",AllStatic,typeof(void),context.GetType(),typeof(IEnumerable<double>)));}
    private static object HotSession(object context)
    {return context.GetType().GetProperty("HybridCycleSession",AllInstance).GetValue(context,null);}
    private static double Expected(double index,double rate,int count,double[] crossings,bool positive)
    {return Double.IsNaN(index)||Double.IsInfinity(index)||index<0||index>=count?Double.NaN:Managed(index,rate,crossings,positive);}
    private static void Same(double expected,double actual,string name)
    {Check((Double.IsNaN(expected)&&Double.IsNaN(actual))||(!Double.IsNaN(expected)&&!Double.IsNaN(actual)&&Math.Abs(expected-actual)<=1e-11),name);}
    private static void Invalid(Action action,string name)
    {
        bool rejected=false;
        try{action();}catch(TargetInvocationException error){rejected=error.InnerException is InvalidDataException||error.InnerException is IOException;}
        Check(rejected,name);
    }
    private static void CheckLocationHotPath(string path)
    {
        Assembly owner=Assembly.LoadFrom(path);
        Type engine=ResolveType(owner,"HVX.Task17.Location.LocationEngine");
        Type referenceType=ResolveType(owner,"Xiongxin.OWTS.Data.LocationCycleReference");
        object instance=Activator.CreateInstance(engine,true);
        for(int direction=0;direction!=2;direction++)
        {
            bool positive=direction!=0;double[] original={-.0003,.0123,.0307,.0439,.071};
            double[] crossings=(double[])original.Clone();int count=72;double rate=1000;
            object reference=Make(referenceType,crossings,count,rate,positive);
            object context=HotContext(engine,instance,reference,rate);
            var phase=HotPhase(engine,context);var batch=HotBatch(engine,context);
            for(int i=0;i<count*16;i++)Same(Expected(i/16d,rate,count,original,positive),phase(i/16d),"actual A4 immutable scalar parity");
            foreach(double index in new[]{-1d,(double)count,Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity,71.125})
                Same(Expected(index,rate,count,original,positive),phase(index),"actual A4 scalar invalid/outside-window preserves NaN");
            var cached=new double[]{0,1,10,31,50,71,71.125,-1,Double.NaN};batch(cached);
            foreach(double index in cached)Same(Expected(index,rate,count,original,positive),phase(index),"actual A4 native cache parity and invalid index handling");
            batch(Enumerable.Range(0,5000).Select(i=>i/1000d));
            Check(((System.Collections.IDictionary)context.GetType().GetField("HybridPhaseCache",AllInstance).GetValue(context)).Count==4096,
                "actual A4 candidate cache remains bounded to 4096 unique entries");
            Same(Expected(4.999,rate,count,original,positive),phase(4.999),"uncached candidate uses the same immutable session");
            var eligible=(Func<int,bool>)Delegate.CreateDelegate(typeof(Func<int,bool>),context,ResolveMethod(engine,"A4AnalysisPhaseEligible",AllStatic,typeof(bool),context.GetType(),typeof(int)));
            for(int mask=0;mask<16;mask++)
            {
                bool[] quadrants={ (mask&1)!=0,(mask&2)!=0,(mask&4)!=0,(mask&8)!=0 };
                context.GetType().GetField("AnalysisPhaseQuadrants",AllInstance).SetValue(context,quadrants);
                foreach(int index in new[]{-1,0,5,10,31,50,71,72})
                {
                    double degrees=Expected(index,rate,count,original,positive);bool expected=mask==15;
                    if(mask!=0&&mask!=15&&!Double.IsNaN(degrees)){degrees%=360;if(degrees<0)degrees+=360;expected=quadrants[Math.Min(3,(int)(degrees/90))];}
                    Check(eligible(index)==expected,"all/none/partial quadrant policy original truth table including invalid sample");
                }
            }
            object review=ResolveMethod(engine,"A4ReviewContext",AllStatic,context.GetType(),context.GetType()).Invoke(null,new[]{context});
            var reviewPhase=HotPhase(engine,review);
            Check(ReferenceEquals(HotSession(context),HotSession(review)),"manual review shares immutable session");
            Check(review.GetType().GetField("HybridPhaseCache",AllInstance).GetValue(review)==null,"manual review does not share mutable cache");
            crossings[1]=.0183;
            Same(Expected(10,rate,count,original,positive),phase(10),"active cached analysis retains reference snapshot after source mutation");
            Same(Expected(11,rate,count,original,positive),phase(11),"active uncached analysis retains reference snapshot after source mutation");
            batch(new[]{11d});Same(Expected(11,rate,count,original,positive),phase(11),"native batch uses same old analysis snapshot");
            Same(Expected(10,rate,count,original,positive),reviewPhase(10),"manual review snapshot survives source mutation");
            Same(Expected(10,rate,count,crossings,positive),Phase(referenceType,reference,10),"ordinary Reference.PhaseAt still detects source-array mutation");
            object newer=HotContext(engine,instance,reference,rate);
            Check(!ReferenceEquals(HotSession(context),HotSession(newer)),"new analysis binds changed reference into a fresh session");
            Same(Expected(10,rate,count,crossings,positive),HotPhase(engine,newer)(10),"new analysis uses changed reference");
            double[] replacement=(double[])original.Clone();replacement[1]=.0153;
            object next=Make(referenceType,replacement,count,rate,positive);object previousSession=HotSession(context);
            BindHot(engine,instance,context,next);
            Check(!ReferenceEquals(previousSession,HotSession(context)),"rebind to a distinct record replaces session");
            Check(context.GetType().GetField("HybridPhaseCache",AllInstance).GetValue(context)==null,"rebind clears old native phase cache");
            Same(Expected(10,rate,count,replacement,positive),phase(10),"rebound actual consumer uses new record");
            Same(Expected(10,rate,count,original,positive),reviewPhase(10),"existing manual review does not follow later rebind");
            object active=HotSession(context);replacement[1]=Double.NaN;
            Invalid(()=>BindHot(engine,instance,context,next),"invalid mutated reference rejected before a new analysis binds");
            Check(ReferenceEquals(active,HotSession(context)),"failed reference acquisition does not replace active session");
            Same(Expected(10,rate,count,new[]{-.0003,.0153,.0307,.0439,.071},positive),phase(10),"previous immutable analysis remains usable after rejected rebind");
        }
        object rounding=HotContext(engine,instance,Make(referenceType,new[]{1e-18,.01,.019},20,1000,true),1000);
        Check(HotPhase(engine,rounding)(0)==360,"actual A4 scalar preserves exact 360 rounding endpoint");
        HotBatch(engine,rounding)(new[]{0d});Check(HotPhase(engine,rounding)(0)==360,"actual A4 native batch preserves exact 360 rounding endpoint");
        foreach(double[] invalid in new[]{new[]{0d,.019},new[]{0d,.005,.01,.019},new[]{0d,.01,.01,.015,.019}})
            Invalid(()=>HotContext(engine,instance,Make(referenceType,invalid,20,1000,true),1000),"analysis binding retains native full-reference validation");
        object valid=Make(referenceType,new[]{0d,.01,.019},20,1000,true);
        Invalid(()=>HotContext(engine,instance,valid,2000),"analysis binding rejects sample-rate mismatch");
        object legacy=HotContext(engine,instance,null,1000);
        Check(HotSession(legacy)==null,"legacy input does not acquire an invented cycle session");
        Same(54,HotPhase(engine,legacy)(3),"legacy uniform-frequency phase unchanged");
        Type bridge=ResolveType(owner,"Xiongxin.OWTS.Data.HybridPhaseCore");
        Check((bool)bridge.GetProperty("Loaded",AllStatic).GetValue(null,null),"actual Location context binding still loads and pins native DLL");
    }
    private static object Make(Type type, double[] crossing, int count, double rate, bool positive)
    {
        object reference = Activator.CreateInstance(type, true);
        foreach (var field in new[] { new object[] {"CrossingSeconds", crossing}, new object[] {"SampleCount", count},
            new object[] {"SampleRateHz", rate}, new object[] {"FirstCrossingPositive", positive}, new object[] {"CaptureStartSeconds", 0.0} })
            type.GetField((string)field[0], BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reference, field[1]);
        return reference;
    }
    private static double Phase(Type type, object reference, double index)
    { return (double)ResolveMethod(type,"PhaseAt",AllInstance,typeof(double),typeof(double)).Invoke(reference, new object[] { index }); }
    private static string Hash(string path)
    {using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
    private static Dictionary<string,object> Json(string path)
    {return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));}
    private static string Text(Dictionary<string,object> row,string key)
    {object value;if(!row.TryGetValue(key,out value)||!(value is string))throw new InvalidDataException("Missing QA binding field "+key);return (string)value;}
    private static PrivateMapping FindLocationMapping(string build,string location)
    {
        var matches=new List<string>();string actual=Hash(location);
        foreach(string module in Directory.GetDirectories(build,"module-build-*",SearchOption.TopDirectoryOnly))
        {
            string manifest=System.IO.Path.Combine(module,"location-source-build","recorded-phase-overlay-source-manifest.json");
            if(!File.Exists(manifest))continue;
            var row=Json(manifest);object bound;
            if(row.TryGetValue("ProtectedOutputSHA256",out bound)&&String.Equals(bound as string,actual,StringComparison.Ordinal))
                matches.Add(System.IO.Path.Combine(module,"location-source-build","protection","mapping.xml"));
        }
        if(matches.Count!=1)throw new InvalidDataException("Exactly one QA mapping bound to the actual protected consumer is required; no unprotected fallback.");
        var mapping=new PrivateMapping(matches[0],location);
        Console.WriteLine("HYBRID_PRIVATE_MAP_SHA256="+Hash(mapping.Path)+";PROTECTED_CONSUMER_SHA256="+actual+";QA_ONLY=true");
        return mapping;
    }
    private static void CheckMappingGuards(string location,string qa)
    {
        string parent=System.IO.Path.GetDirectoryName(locationMap.Path);
        bool rejected=false;
        try{new PrivateMapping(locationMap.Path,System.IO.Path.Combine(parent,"unprotected-input.dll"));}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"QA rejects unprotected algorithm bytes rather than falling back");
        Directory.CreateDirectory(System.IO.Path.Combine(qa,"protection"));
        string badMap=System.IO.Path.Combine(qa,"protection","mapping.xml");
        File.Copy(locationMap.Path,badMap);
        using(var file=new FileStream(badMap,FileMode.Append,FileAccess.Write))file.WriteByte(32);
        File.Copy(System.IO.Path.Combine(parent,"receipt.json"),System.IO.Path.Combine(qa,"protection","receipt.json"));
        File.Copy(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(parent),"recorded-phase-overlay-source-manifest.json"),System.IO.Path.Combine(qa,"recorded-phase-overlay-source-manifest.json"));
        rejected=false;try{new PrivateMapping(badMap,location);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"QA rejects a mapping hash mismatch before resolving private metadata");
    }
    // Only the QA executable consumes this map. Product consumers remain unchanged.
    private sealed class PrivateMapping
    {
        internal readonly string Path;
        private readonly string protectedHash;
        private readonly Dictionary<string,string> renamedTypes=new Dictionary<string,string>(StringComparer.Ordinal);
        private readonly Dictionary<string,string> originalTypes=new Dictionary<string,string>(StringComparer.Ordinal);
        private readonly Dictionary<string,string> renamedMethods=new Dictionary<string,string>(StringComparer.Ordinal);
        private readonly HashSet<string> skippedMethods=new HashSet<string>(StringComparer.Ordinal);
        internal PrivateMapping(string path,string assemblyPath)
        {
            Path=System.IO.Path.GetFullPath(path);
            string directory=System.IO.Path.GetDirectoryName(Path);
            string receiptPath=System.IO.Path.Combine(directory,"receipt.json");
            var receipt=Json(receiptPath);
            var manifest=Json(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory),"recorded-phase-overlay-source-manifest.json"));
            protectedHash=Text(manifest,"ProtectedOutputSHA256");
            object numeric,hot;
            if(Text(receipt,"Schema")!="HVX.AlgorithmProtection.v1"||
                !receipt.TryGetValue("NumericRegressionPassed",out numeric)||!Object.Equals(numeric,true)||
                Text(receipt,"OutputSha256")!=protectedHash||Hash(assemblyPath)!=protectedHash||
                Text(receipt,"InputSha256")==protectedHash||Text(manifest,"OutputSHA256")!=Text(receipt,"InputSha256")||
                Hash(Path)!=Text(manifest,"ProtectionMappingSHA256")||Hash(receiptPath)!=Text(manifest,"ProtectionReceiptSHA256")||
                !manifest.TryGetValue("HybridHotPath",out hot)||!(hot is Dictionary<string,object>)||
                !Object.Equals(((Dictionary<string,object>)hot)["Compiled"],true))
                throw new InvalidDataException("Private mapping must be exactly bound to protected hybrid output; unprotected fallback is prohibited.");
            if(Hash(System.IO.Path.Combine(directory,"unprotected-input.dll"))!=Text(receipt,"InputSha256"))
                throw new InvalidDataException("Protected QA source receipt does not match its retained input bytes.");
            var document=new XmlDocument {XmlResolver=null};
            using(var reader=XmlReader.Create(Path,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))document.Load(reader);
            foreach(XmlElement row in document.SelectNodes("//renamedClass"))
            {
                string oldName=StripAssembly(row.GetAttribute("oldName")),newName=StripAssembly(row.GetAttribute("newName"));
                renamedTypes.Add(oldName,newName);originalTypes.Add(newName,oldName);
            }
            foreach(XmlElement row in document.SelectNodes("//renamedMethod"))renamedMethods.Add(row.GetAttribute("oldName"),row.GetAttribute("newName"));
            foreach(XmlElement row in document.SelectNodes("//skippedMethod"))skippedMethods.Add(row.GetAttribute("name"));
        }
        private static string StripAssembly(string value)
        {int end=value.IndexOf(']');if(!value.StartsWith("[",StringComparison.Ordinal)||end<1)throw new InvalidDataException("Malformed private mapping type.");return value.Substring(end+1);}
        internal Type Type(Assembly owner,string originalName)
        {
            if(owner.GetName().Name!=LocationAssemblyName||Hash(owner.Location)!=protectedHash)throw new InvalidDataException("Private map belongs to a different protected consumer.");
            string renamed;if(!renamedTypes.TryGetValue(originalName,out renamed))throw new TypeLoadException("Required private type has no exact QA mapping: "+originalName);
            return owner.GetType(renamed.Replace('/','+'),true);
        }
        private string Qualified(Type value)
        {
            if(value.IsArray)return Qualified(value.GetElementType())+"[]";
            if(value.IsGenericType&&!value.IsGenericTypeDefinition)return Qualified(value.GetGenericTypeDefinition())+"<"+String.Join(",",value.GetGenericArguments().Select(Qualified)) +">";
            string name=value.FullName.Replace('+','/'),original;
            if(originalTypes.TryGetValue(name,out original))name=original;
            return "["+value.Assembly.GetName().Name+"]"+name;
        }
        internal MethodInfo Method(Type owner,string originalName,BindingFlags flags,Type[] parameters)
        {
            string signature=Qualified(owner)+"::"+originalName+"["+parameters.Length+"]("+String.Join(",",parameters.Select(Qualified))+")";
            string renamed;
            if(!renamedMethods.TryGetValue(signature,out renamed))
            {if(!skippedMethods.Contains(signature))throw new MissingMethodException("Required private method has no exact QA signature mapping: "+signature);renamed=originalName;}
            return owner.GetMethod(renamed,flags,null,parameters,null);
        }
    }
    private static double Managed(double index, double rate, double[] crossings, bool positive)
    {
        double time = index / rate;
        if (time > crossings[crossings.Length - 1] + 1e-12 || time < crossings[0] - 1e-12) return Double.NaN;
        int lo = 0, hi = crossings.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (crossings[mid] <= time) lo = mid; else hi = mid; }
        double phase = (positive ? 0 : 180) + 180 * (lo + (time - crossings[lo]) / (crossings[hi] - crossings[lo]));
        phase %= 360; return phase < 0 ? phase + 360 : phase;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateHardLink(string name, string existing, IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLink(string name,string target,uint flags);
}
