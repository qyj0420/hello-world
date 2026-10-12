using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HVX.PdReview
{
    // Process-wide, content-addressed store for per-file algorithm statuses.
    // A session dictionary keeps only a short reference; the complete UTF-8
    // status is gzip-compressed in a private temporary directory and restored
    // losslessly (exact length and SHA-256) one file at a time.
    //
    // An entry is removed only after every reference string handed out for it
    // has been garbage collected, so rollback snapshots, probes and display
    // caches that still hold a reference can always resolve it.
    internal static class ReviewDiagnosticSpill
    {
        internal const string ReferencePrefix="HVX.Diagnostics.Spill.v1:";
        // Small statuses without an E/E+ ledger stay inline in the dictionary.
        internal const int InlineStatusBytes=4096;
        // 1 Mi UTF-16 characters (the per-status limit) in worst-case UTF-8.
        internal const int MaximumStatusBytes=4*1024*1024;
        internal const int MaximumCompressedBytes=MaximumStatusBytes+64*1024;
        const string RootVariable="HVX_DIAGNOSTICS_SPILL_ROOT";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);

        sealed class Entry
        {
            internal string Sha,FilePath;
            internal int DecodedBytes,LedgerBytes,StoredBytes,Leases;
            internal byte[] Memory;
            internal WeakReference Reference;
        }
        sealed class Lease
        {
            readonly Entry entry;
            internal Lease(Entry value){entry=value;}
            ~Lease(){Interlocked.Decrement(ref entry.Leases);}
        }
        static readonly object gate=new object();
        static readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        static readonly ConditionalWeakTable<string,Lease> leases=new ConditionalWeakTable<string,Lease>();
        static long diskBytes,memoryBytes;
        static int storesSinceSweep;
        static string directory;
        static FileStream ownerLock;
        static bool diskUnavailable;

        internal static long StoredBytes{get{lock(gate)return diskBytes+memoryBytes;}}
        internal static int EntryCount{get{lock(gate)return entries.Count;}}
        internal static string DirectoryPath{get{lock(gate)return directory??"";}}

        internal static bool IsReference(string value)
        {string sha;int decoded,ledger;return TryParseReference(value,out sha,out decoded,out ledger);}
        internal static bool TryParseReference(string value,out string sha,out int decodedBytes,out int ledgerBytes)
        {
            sha=null;decodedBytes=ledgerBytes=0;
            if(value==null||value.Length>ReferencePrefix.Length+64+24||!value.StartsWith(ReferencePrefix,StringComparison.Ordinal))return false;
            var parts=value.Substring(ReferencePrefix.Length).Split(':');
            if(parts.Length!=3||parts[0].Length!=64||parts[0].Any(c=>!(c>='0'&&c<='9'||c>='A'&&c<='F')))return false;
            if(!Count(parts[1],1,MaximumStatusBytes,out decodedBytes)||!Count(parts[2],0,ReviewEAnalysis.MaximumLedgerBytes,out ledgerBytes))return false;
            sha=parts[0];return true;
        }
        static bool Count(string text,int minimum,int maximum,out int value)
        {
            value=0;
            if(text.Length==0||text.Length>9||text.Any(c=>c<'0'||c>'9')||text.Length>1&&text[0]=='0')return false;
            value=Int32.Parse(text,NumberStyles.None,CultureInfo.InvariantCulture);
            return value>=minimum&&value<=maximum;
        }
        // Returns the status itself when it may stay inline, otherwise a
        // reference to its spilled, verified copy.
        internal static string Retain(string status,int ledgerBytes)
        {
            if(status==null)throw new ArgumentNullException("status");
            if(ledgerBytes<0||ledgerBytes>ReviewEAnalysis.MaximumLedgerBytes)throw new InvalidDataException("Invalid E ledger size.");
            byte[] bytes=Utf8.GetBytes(status);
            if(bytes.Length>MaximumStatusBytes)throw new InvalidDataException("Algorithm status exceeds its size limit.");
            if(bytes.Length<=InlineStatusBytes&&ledgerBytes==0&&!status.StartsWith(ReferencePrefix,StringComparison.Ordinal))return status;
            if(bytes.Length==0)return status;
            string sha=Hash(bytes);
            lock(gate){Entry known;if(entries.TryGetValue(sha,out known))return Known(known,bytes.Length,ledgerBytes);}
            return Store(sha,bytes.Length,ledgerBytes,Compress(bytes));
        }
        // The caller has already inflated and verified this exact payload.
        internal static string RetainVerifiedCompressed(string sha,int decodedBytes,int ledgerBytes,byte[] compressed)
        {
            lock(gate){Entry known;if(entries.TryGetValue(sha,out known))return Known(known,decodedBytes,ledgerBytes);}
            return Store(sha,decodedBytes,ledgerBytes,(byte[])compressed.Clone());
        }
        // A reference parsed from text is honoured only while this process
        // still holds the identical entry; the canonical instance is returned.
        internal static string Adopt(string reference)
        {
            string sha;int decoded,ledger;
            if(!TryParseReference(reference,out sha,out decoded,out ledger))return null;
            lock(gate){Entry known;return entries.TryGetValue(sha,out known)&&known.DecodedBytes==decoded&&known.LedgerBytes==ledger?Reference(known):null;}
        }
        internal static string Resolve(string stored)
        {
            string sha;int decoded,ledger;
            if(!TryParseReference(stored,out sha,out decoded,out ledger))return stored;
            byte[] bytes=Inflate(ReadCompressed(sha,decoded,ledger),decoded);
            if(Hash(bytes)!=sha)throw new InvalidDataException("Spilled algorithm diagnostics failed verification.");
            return Utf8.GetString(bytes);
        }
        internal static byte[] ReadVerifiedCompressed(string reference)
        {
            string sha;int decoded,ledger;
            if(!TryParseReference(reference,out sha,out decoded,out ledger))throw new ArgumentException("Not a spilled diagnostics reference.");
            byte[] compressed=ReadCompressed(sha,decoded,ledger);
            if(Hash(Inflate(compressed,decoded))!=sha)throw new InvalidDataException("Spilled algorithm diagnostics failed verification.");
            return compressed;
        }
        internal static byte[] Inflate(byte[] compressed,int decodedBytes)
        {
            if(compressed==null||compressed.Length>MaximumCompressedBytes||decodedBytes<1||decodedBytes>MaximumStatusBytes)
                throw new InvalidDataException("Invalid spilled diagnostics size.");
            var output=new byte[decodedBytes];int filled=0;
            using(var input=new MemoryStream(compressed,false))using(var gzip=new GZipStream(input,CompressionMode.Decompress)) {
                int read;
                while(filled<output.Length&&(read=gzip.Read(output,filled,output.Length-filled))>0)filled+=read;
                if(filled!=output.Length||gzip.Read(new byte[1],0,1)!=0)throw new InvalidDataException("Spilled algorithm diagnostics length mismatch.");
            }
            return output;
        }
        internal static string Hash(byte[] bytes)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
        // Forces collection of unreachable references, then deletes their
        // entries. Never called while holding the gate: finalizers may run.
        internal static void CollectUnreferenced()
        {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            lock(gate)Sweep();
        }

        static string Known(Entry known,int decodedBytes,int ledgerBytes)
        {
            if(known.DecodedBytes!=decodedBytes||known.LedgerBytes!=ledgerBytes)throw new InvalidDataException("Spilled algorithm diagnostics identity mismatch.");
            return Reference(known);
        }
        // Caller holds the gate. One live reference instance per entry; a new
        // instance (and lease) is created only after the previous one died.
        static string Reference(Entry entry)
        {
            string existing=entry.Reference==null?null:entry.Reference.Target as string;
            if(existing!=null)return existing;
            string text=String.Concat(ReferencePrefix,entry.Sha,":",entry.DecodedBytes.ToString(CultureInfo.InvariantCulture),":",entry.LedgerBytes.ToString(CultureInfo.InvariantCulture));
            Interlocked.Increment(ref entry.Leases);
            leases.Add(text,new Lease(entry));
            entry.Reference=new WeakReference(text);
            return text;
        }
        static byte[] ReadCompressed(string sha,int decodedBytes,int ledgerBytes)
        {
            string path;byte[] memory;int length;
            lock(gate){
                Entry entry;
                if(!entries.TryGetValue(sha,out entry)||entry.DecodedBytes!=decodedBytes||entry.LedgerBytes!=ledgerBytes)
                    throw new InvalidDataException("Spilled algorithm diagnostics are no longer available.");
                path=entry.FilePath;memory=entry.Memory;length=entry.StoredBytes;
            }
            if(memory!=null)return memory;
            byte[] bytes;
            try{
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(stream.Length!=length)throw new InvalidDataException("Spilled algorithm diagnostics changed on disk.");
                    bytes=new byte[length];int filled=0,read;
                    while(filled<length&&(read=stream.Read(bytes,filled,length-filled))>0)filled+=read;
                    if(filled!=length)throw new InvalidDataException("Spilled algorithm diagnostics are truncated.");
                }
            }catch(Exception error) when(error is IOException||error is UnauthorizedAccessException){
                throw new InvalidDataException("Spilled algorithm diagnostics could not be read: "+error.Message,error);
            }
            return bytes;
        }
        static byte[] Compress(byte[] bytes)
        {
            using(var output=new MemoryStream()) {
                using(var gzip=new GZipStream(output,CompressionLevel.Fastest,true))gzip.Write(bytes,0,bytes.Length);
                if(output.Length>MaximumCompressedBytes)throw new InvalidDataException("Compressed algorithm status exceeds its size limit.");
                return output.ToArray();
            }
        }
        static string Store(string sha,int decodedBytes,int ledgerBytes,byte[] compressed)
        {
            if(compressed.Length<1||compressed.Length>MaximumCompressedBytes)throw new InvalidDataException("Compressed algorithm status exceeds its size limit.");
            bool full;
            lock(gate){
                full=diskBytes+memoryBytes+compressed.Length>ReviewAnalysisMetadataBudget.DiagnosticsSpillBytes||
                    (directory==null&&diskUnavailable&&memoryBytes+compressed.Length>ReviewAnalysisMetadataBudget.DiagnosticsSpillMemoryFallbackBytes);
                if(!full&&++storesSinceSweep>=64){storesSinceSweep=0;Sweep();}
            }
            if(full)CollectUnreferenced();
            lock(gate){
                Entry known;if(entries.TryGetValue(sha,out known))return Known(known,decodedBytes,ledgerBytes);
                if(diskBytes+memoryBytes+compressed.Length>ReviewAnalysisMetadataBudget.DiagnosticsSpillBytes)
                    throw new InvalidOperationException("Algorithm diagnostics exceed the bounded 1 GiB spill store; save the analysis and split the batch.");
                var entry=new Entry{Sha=sha,DecodedBytes=decodedBytes,LedgerBytes=ledgerBytes,StoredBytes=compressed.Length};
                entry.FilePath=WriteFile(sha,compressed);
                if(entry.FilePath!=null)diskBytes+=compressed.Length;
                else {
                    if(memoryBytes+compressed.Length>ReviewAnalysisMetadataBudget.DiagnosticsSpillMemoryFallbackBytes)
                        throw new InvalidOperationException("No writable temporary folder for algorithm diagnostics and the 256 MiB in-memory fallback is full; split the analysis batch.");
                    entry.Memory=compressed;memoryBytes+=compressed.Length;
                }
                entries.Add(sha,entry);
                return Reference(entry);
            }
        }
        // Caller holds the gate.
        static void Sweep()
        {
            foreach(var entry in entries.Values.Where(e=>Volatile.Read(ref e.Leases)<=0&&(e.Reference==null||!e.Reference.IsAlive)).ToList()) {
                entries.Remove(entry.Sha);
                if(entry.Memory!=null){memoryBytes-=entry.StoredBytes;entry.Memory=null;continue;}
                diskBytes-=entry.StoredBytes;
                try{File.Delete(entry.FilePath);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
        // Caller holds the gate. Null means use the in-memory fallback.
        static string WriteFile(string sha,byte[] compressed)
        {
            string folder=SpillDirectory();if(folder==null)return null;
            string path=Path.Combine(folder,sha+".gz"),temporary=path+".tmp";
            try{
                using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))stream.Write(compressed,0,compressed.Length);
                if(File.Exists(path))File.Delete(path);
                File.Move(temporary,path);
                return path;
            }catch(Exception error) when(error is IOException||error is UnauthorizedAccessException){
                try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
                return null;
            }
        }
        // Caller holds the gate.
        static string SpillDirectory()
        {
            if(directory!=null||diskUnavailable)return directory;
            try{
                string root=Environment.GetEnvironmentVariable(RootVariable);
                if(String.IsNullOrWhiteSpace(root))root=Path.Combine(Path.GetTempPath(),"HVX.DiagnosticsSpill");
                root=Path.GetFullPath(root);Directory.CreateDirectory(root);
                RemoveStaleDirectories(root);
                int pid=Process.GetCurrentProcess().Id;
                string mine=Path.Combine(root,"p"+pid.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,12));
                Directory.CreateDirectory(mine);
                if((File.GetAttributes(mine)&FileAttributes.ReparsePoint)!=0)throw new IOException("Spill folder is a link.");
                ownerLock=new FileStream(Path.Combine(mine,".owner"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
                directory=mine;
                AppDomain.CurrentDomain.ProcessExit+=(sender,args)=>DeleteOwnDirectory();
                AppDomain.CurrentDomain.DomainUnload+=(sender,args)=>DeleteOwnDirectory();
            }catch(Exception error) when(error is IOException||error is UnauthorizedAccessException||error is System.Security.SecurityException||
                error is ArgumentException||error is NotSupportedException||error is InvalidOperationException){
                diskUnavailable=true;directory=null;
            }
            return directory;
        }
        static void RemoveStaleDirectories(string root)
        {
            foreach(string candidate in Directory.GetDirectories(root,"p*-*")) {
                try{
                    string name=Path.GetFileName(candidate);int dash=name.IndexOf('-'),pid;
                    if(dash<2||!Int32.TryParse(name.Substring(1,dash-1),NumberStyles.None,CultureInfo.InvariantCulture,out pid))continue;
                    if(pid==Process.GetCurrentProcess().Id||ProcessAlive(pid))continue;
                    string owner=Path.Combine(candidate,".owner");
                    if(File.Exists(owner))using(new FileStream(owner,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
                    Directory.Delete(candidate,true);
                }catch(Exception error) when(error is IOException||error is UnauthorizedAccessException){}
            }
        }
        static bool ProcessAlive(int pid)
        {
            try{using(var process=Process.GetProcessById(pid))return !process.HasExited;}
            catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}
            catch(System.ComponentModel.Win32Exception){return true;}
        }
        static void DeleteOwnDirectory()
        {
            string folder;
            lock(gate){folder=directory;if(folder==null)return;directory=null;diskUnavailable=true;if(ownerLock!=null){ownerLock.Dispose();ownerLock=null;}}
            try{Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
