using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HVX.PdReview
{
    // diagnostics.bin inside a named analysis: the spilled statuses referenced
    // by session.json, stored as their compressed spill payloads.
    //   "HVXDIAG1" | int32 count | count x (sha256[32], int32 decoded,
    //   int32 ledger, int64 offset, int32 length) | payloads
    // Each payload is inflated and verified (length, SHA-256, E ledger) on its
    // own while a session is restored; the pack is never read as a whole.
    internal sealed class ReviewDiagnosticArchivePack : IDisposable
    {
        internal const string MemberName="diagnostics.bin";
        const string Magic="HVXDIAG1";
        const int HeaderBytes=12,SlotBytes=52,MaximumEntries=100000;
        sealed class Slot{internal int DecodedBytes,LedgerBytes,Length;internal long Offset;}
        readonly FileStream stream;
        readonly Dictionary<string,Slot> slots;
        ReviewDiagnosticArchivePack(FileStream source,Dictionary<string,Slot> index){stream=source;slots=index;}
        public void Dispose(){stream.Dispose();}

        internal static long Write(string path,IEnumerable<string> references)
        {
            var distinct=new List<string>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(string reference in references) {
                string sha;int decoded,ledger;
                if(!ReviewDiagnosticSpill.TryParseReference(reference,out sha,out decoded,out ledger))throw new ArgumentException("Invalid diagnostics reference.");
                if(seen.Add(sha))distinct.Add(reference);
            }
            if(distinct.Count>MaximumEntries)throw new InvalidOperationException("Too many spilled algorithm statuses for one analysis archive.");
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            using(var writer=new BinaryWriter(file,Encoding.UTF8,true)) {
                long dataStart=HeaderBytes+(long)distinct.Count*SlotBytes;
                file.SetLength(dataStart);file.Position=dataStart;
                var written=new List<KeyValuePair<string,long[]>>(distinct.Count);
                foreach(string reference in distinct) {
                    byte[] compressed=ReviewDiagnosticSpill.ReadVerifiedCompressed(reference);
                    long offset=file.Position;
                    if(offset+compressed.Length>ReviewAnalysisMetadataBudget.DiagnosticsArchiveBytes)
                        throw new InvalidOperationException("Spilled algorithm diagnostics exceed the 1 GiB archive budget; split the analysis batch.");
                    file.Write(compressed,0,compressed.Length);
                    written.Add(new KeyValuePair<string,long[]>(reference,new[]{offset,compressed.Length}));
                }
                file.Position=0;writer.Write(Encoding.ASCII.GetBytes(Magic));writer.Write(distinct.Count);
                foreach(var item in written) {
                    string sha;int decoded,ledger;ReviewDiagnosticSpill.TryParseReference(item.Key,out sha,out decoded,out ledger);
                    writer.Write(HexBytes(sha));writer.Write(decoded);writer.Write(ledger);writer.Write(item.Value[0]);writer.Write((int)item.Value[1]);
                }
                writer.Flush();file.Flush(true);
                return file.Length;
            }
        }
        internal static ReviewDiagnosticArchivePack Open(string path)
        {
            if(!File.Exists(path))throw new InvalidDataException("The analysis archive is missing its algorithm diagnostics ("+MemberName+").");
            var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try{
                long length=file.Length;
                if(length<HeaderBytes||length>ReviewAnalysisMetadataBudget.DiagnosticsArchiveBytes)throw new InvalidDataException("Invalid algorithm diagnostics archive size.");
                var reader=new BinaryReader(file,Encoding.UTF8,true);
                if(Encoding.ASCII.GetString(reader.ReadBytes(8))!=Magic)throw new InvalidDataException("Invalid algorithm diagnostics archive signature.");
                int count=reader.ReadInt32();long dataStart=HeaderBytes+(long)count*SlotBytes;
                if(count<0||count>MaximumEntries||dataStart>length)throw new InvalidDataException("Invalid algorithm diagnostics archive index.");
                var index=new Dictionary<string,Slot>(count,StringComparer.Ordinal);
                for(int i=0;i<count;i++) {
                    string sha=BitConverter.ToString(reader.ReadBytes(32)).Replace("-","");
                    var slot=new Slot{DecodedBytes=reader.ReadInt32(),LedgerBytes=reader.ReadInt32(),Offset=reader.ReadInt64(),Length=reader.ReadInt32()};
                    if(sha.Length!=64||slot.DecodedBytes<1||slot.DecodedBytes>ReviewDiagnosticSpill.MaximumStatusBytes||
                        slot.LedgerBytes<0||slot.LedgerBytes>ReviewEAnalysis.MaximumLedgerBytes||slot.Length<1||slot.Length>ReviewDiagnosticSpill.MaximumCompressedBytes||
                        slot.Offset<dataStart||slot.Offset>length-slot.Length||index.ContainsKey(sha))
                        throw new InvalidDataException("Invalid algorithm diagnostics archive entry.");
                    index.Add(sha,slot);
                }
                return new ReviewDiagnosticArchivePack(file,index);
            }catch{file.Dispose();throw;}
        }
        // Verifies one archived status and returns its canonical spill reference.
        internal string Import(string sha,int decodedBytes,int ledgerBytes)
        {
            Slot slot;
            if(!slots.TryGetValue(sha,out slot)||slot.DecodedBytes!=decodedBytes||slot.LedgerBytes!=ledgerBytes)
                throw new InvalidDataException("A referenced algorithm status is missing from "+MemberName+".");
            string known=ReviewDiagnosticSpill.Adopt(ReviewDiagnosticSpill.ReferencePrefix+sha+":"+decodedBytes+":"+ledgerBytes);
            if(known!=null)return known;
            var compressed=new byte[slot.Length];stream.Position=slot.Offset;int filled=0,read;
            while(filled<compressed.Length&&(read=stream.Read(compressed,filled,compressed.Length-filled))>0)filled+=read;
            if(filled!=compressed.Length)throw new InvalidDataException("Truncated algorithm diagnostics archive entry.");
            byte[] bytes=ReviewDiagnosticSpill.Inflate(compressed,decodedBytes);
            if(ReviewDiagnosticSpill.Hash(bytes)!=sha)throw new InvalidDataException("Archived algorithm status failed verification.");
            string status=new UTF8Encoding(false,true).GetString(bytes);
            if(ReviewEAnalysis.ValidateLedgerStatus(status)!=ledgerBytes)throw new InvalidDataException("Archived E ledger size does not match its reference.");
            return ReviewDiagnosticSpill.RetainVerifiedCompressed(sha,decodedBytes,ledgerBytes,compressed);
        }
        static byte[] HexBytes(string hex)
        {return Enumerable.Range(0,hex.Length/2).Select(i=>Convert.ToByte(hex.Substring(2*i,2),16)).ToArray();}
    }
}
