using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xiongxin.OWTS.Desktop;

static class DefectModelTrustSmoke
{
    static int checks;
    static void Check(bool result,string reason){checks++;if(!result)throw new Exception(reason);}
    static void Reject(Action action,string reason){bool rejected=false;try{action();}catch(IOException){rejected=true;}catch(InvalidDataException){rejected=true;}catch(ArgumentException){rejected=true;}Check(rejected,reason);}
    static void Read(string path){using(var lease=DefectModelCatalog.OpenVerified(path))Check(lease.Hash.Length==64,"verified encrypted content");}
    static int Main(string[] args)
    {
        try
        {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            string[] sources={args[1],args[2]},hashes={DefectModelCatalog.StandardHash,DefectModelCatalog.CompetitionHash};
            for(int i=0;i<sources.Length;i++)
            {
                string folder=Path.Combine(qa,"model"+i);Directory.CreateDirectory(folder);
                string copy=Path.Combine(folder,"renamed-"+i+".hXm");File.Copy(sources[i],copy);
                using(var lease=DefectModelCatalog.OpenVerified(copy))
                {
                    Check(lease.Hash==hashes[i],"renamed approved model retains content identity");
                    using(var reader=File.OpenRead(copy))Check(reader.Length>32,"engine-style read remains possible");
                    Reject(()=>{using(var writer=File.OpenWrite(copy)){}},"write blocked while verified");
                    Reject(()=>File.Delete(copy),"delete blocked while verified");
                    Reject(()=>File.Move(copy,copy+".moved"),"leaf rename blocked while verified");
                    Reject(()=>Directory.Move(folder,folder+"-moved"),"parent rename blocked while verified");
                }
                using(var writer=File.OpenWrite(copy))Check(writer.CanWrite,"lease disposal releases owner");
                Read(copy);Read(sources[i]);
                string bad=Path.Combine(folder,"unknown.pdm");File.Copy(copy,bad);
                using(var writer=new FileStream(bad,FileMode.Open,FileAccess.Write)){writer.Position=writer.Length-1;writer.WriteByte(0x5a);}
                Reject(()=>Read(bad),"renamed modified ciphertext rejected before inference");
                using(var writer=File.OpenWrite(bad))Check(writer.CanWrite,"failed validation releases all files");
                Reject(()=>DefectModelCatalog.ValidatePinnedHash("new-name.pdm",new string('0',64)),"unreviewed custom hash rejected independent of filename");
            }
            DefectModelCatalog.ValidatePinnedHash("user-standard.pdm",hashes[0].ToLowerInvariant());Check(true,"case-insensitive exact hash accepted");
            Reject(()=>DefectModelCatalog.ValidatePinnedHash(DefectModelCatalog.FileName("standard"),hashes[1]),"wrong content in standard deployment slot rejected");
            Reject(()=>DefectModelCatalog.ValidatePinnedHash("model.pdm",null),"null identity rejected");
            string shortFile=Path.Combine(qa,"short.pdm");File.WriteAllBytes(shortFile,new byte[]{80,68,77,50});Reject(()=>Read(shortFile),"truncation rejected");
            string fake=Path.Combine(qa,"fake.pdm");var bytes=new byte[80];bytes[0]=80;bytes[1]=68;bytes[2]=77;bytes[3]=50;File.WriteAllBytes(fake,bytes);Reject(()=>Read(fake),"magic alone never establishes trust");
            string oversize=Path.Combine(qa,"large.pdm");using(var writer=File.Create(oversize)){writer.SetLength(16*1024*1024+1);}Reject(()=>Read(oversize),"oversize ciphertext rejected before hashing");
            Reject(()=>Read(Path.Combine(qa,"absent.pdm")),"missing file rejected");
            Reject(()=>Read(Path.Combine(qa,"wrong.onnx")),"noncontainer suffix rejected");
            var concurrent=Enumerable.Range(0,4).Select(_=>Task.Run(()=>{using(var lease=DefectModelCatalog.OpenVerified(sources[0]))return lease.Hash;})).ToArray();
            Task.WaitAll(concurrent);Check(concurrent.All(t=>t.Result==hashes[0]),"concurrent readers share consistent content");
            Console.WriteLine("MODEL_TRUST_PASS="+checks+"; DECRYPTION=False; INFERENCE=False; HARDWARE=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
