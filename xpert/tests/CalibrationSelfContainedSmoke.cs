using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HVX.CalibrationStorage;

static class CalibrationSelfContainedSmoke
{
    static int checks;
    static void Check(bool pass,string message){checks++;if(!pass)throw new Exception(message);}
    static bool Rejects(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(EndOfStreamException){return true;}}
    static string Block(string phase){return "0\t"+phase+"\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n";}
    static void Mutate(string file,string target,Action<byte[]> modify,bool checksum=true)
    {
        byte[] bytes=File.ReadAllBytes(file);modify(bytes);
        if(checksum)using(var sha=SHA256.Create())Buffer.BlockCopy(sha.ComputeHash(bytes,44,bytes.Length-44),0,bytes,12,32);
        File.WriteAllBytes(target,bytes);
    }
    static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);
            if(Directory.Exists(root))throw new Exception("Use a fresh isolated fixture directory.");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"三相单芯电缆\"\r\n");
            File.WriteAllText(Path.Combine(root,"CABLE.in"),Block("A")+Block("B")+Block("C"));
            var raw=new CalibrationRawReference[3];
            for(int i=0;i<3;i++) {
                string name="raw"+i+".bin",path=Path.Combine(root,name);File.WriteAllBytes(path,new byte[]{1,2,(byte)i});
                raw[i]=new CalibrationRawReference{RelativePath=name,Sha256=LiveCalibrationRecord.HashFile(path)};
            }
            var wave=Enumerable.Range(0,512).Select(i=>Math.Sin(i*.02)*.01).ToArray();wave[20]=1;wave[220]=.5;
            var record=new LiveCalibrationRecord{Phase="A",Level="5nC",DeviceSerial="OFFLINE",DeviceModel="fixture",
                FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,ProjectHash=LiveCalibrationRecord.Identity(root),CreatedUtc=DateTime.UtcNow.ToString("o"),
                LengthM=170,VelocityMPerUs=170,SampleRateHz=100e6,IncidentSeconds=20/100e6,ReflectionSeconds=220/100e6,
                CoefficientPcPerV=5000,ChargePc=5000,SnrDb=40,Correlation=.99,RangeIndex=5,PulseCount=3,
                IncidentPeakIndex=20,ReflectionPeakIndex=220,FirstArrival=true,RawFiles=raw,Average=wave,CoefficientProcessing="HP10K_NO_LP"};
            string file=record.Save(root),legacy=Path.Combine(root,"legacy.hvcal");File.Copy(file,legacy);
            Check(LiveCalibrationRecord.Load(root,legacy).Version==1,"legacy format remains readable");
            record.Version=2;record.EffectiveWindows=new[]{(double[])wave.Clone(),(double[])wave.Clone(),(double[])wave.Clone()};
            record.Save(root);
            Check(Encoding.ASCII.GetString(File.ReadAllBytes(file),0,8)=="HVCLIVE2","explicit new container version");
            var loaded=LiveCalibrationRecord.Load(root,file);
            Check(loaded.Average.SequenceEqual(wave),"lossless average samples");
            Check(loaded.EffectiveWindows.Length==3&&loaded.EffectiveWindows.All(w=>w.SequenceEqual(wave)),"lossless effective pulse samples");
            Check(loaded.CoefficientPcPerV==5000&&loaded.SampleRateHz==100e6&&loaded.RawFiles[0].Sha256==raw[0].Sha256,"metadata and provenance preserved");
            for(int i=0;i<3;i++)File.Move(Path.Combine(root,raw[i].RelativePath),Path.Combine(root,raw[i].RelativePath+".fixture-moved"));
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,legacy)),"legacy missing evidence still rejected");
            Check(LiveCalibrationRecord.Load(root,file).Version==2,"formal v2 independent of old cache");
            loaded.Save(root);
            Check(LiveCalibrationRecord.BestForProject(root,"A",true,"OFFLINE")!=null,"pressure selection consumes independent archive");
            Check(Directory.GetFiles(Path.GetDirectoryName(file),"*.bak").Length==0,"repeat save does not accumulate backups");
            Check(Directory.GetFiles(Path.GetDirectoryName(file),"*.tmp").Length==0,"successful save leaves no staging file");
            string before=LiveCalibrationRecord.HashFile(file);
            loaded.EffectiveWindows[0][0]=double.NaN;
            Check(Rejects(()=>loaded.Save(root)),"nonfinite pulse rejected before overwrite");
            Check(before==LiveCalibrationRecord.HashFile(file),"invalid save preserves latest valid record");
            loaded=LiveCalibrationRecord.Load(root,file);loaded.EffectiveWindows=new[]{wave};
            Check(Rejects(()=>loaded.Validate(root,true)),"pulse count mismatch rejected");
            loaded=LiveCalibrationRecord.Load(root,file);loaded.EffectiveWindows[0]=new double[128];
            Check(Rejects(()=>loaded.Validate(root,true)),"window length mismatch rejected");
            loaded=LiveCalibrationRecord.Load(root,file);loaded.RawFiles[0].Sha256="invalid";
            Check(Rejects(()=>loaded.Validate(root,true)),"invalid source digest rejected");
            string bad=Path.Combine(root,"bad.hvcal");
            Mutate(file,bad,b=>b[b.Length-1]^=1,false);
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,bad)),"payload corruption rejected");
            Mutate(file,bad,b=>Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue),0,b,44,4));
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,bad)),"oversized XML rejected before allocation");
            Mutate(file,bad,b=>{int start=48+BitConverter.ToInt32(b,44);Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue),0,b,start,4);});
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,bad)),"oversized sample count rejected before allocation");
            Mutate(file,bad,b=>b[7]=(byte)'1');
            bool rejected=false;try{LiveCalibrationRecord.Load(root,bad);}catch(Exception ex){rejected=ex is InvalidDataException||ex is InvalidOperationException;}
            Check(rejected,"container mismatch rejected");
            loaded=LiveCalibrationRecord.Load(root,file);
            long oldLength=new FileInfo(file).Length;
            loaded.Version=3;loaded.EffectiveWindows=null;loaded.Save(root);
            var averageOnly=LiveCalibrationRecord.Load(root,file);
            Check(averageOnly.Version==3&&averageOnly.EffectiveWindows==null,"v3 stores no synthetic independent windows");
            Check(averageOnly.Average.SequenceEqual(wave)&&averageOnly.PulseCount==3,"last average and actual count preserved");
            Check(new FileInfo(file).Length<oldLength-wave.Length*8*2,"average-only payload removes repeated pulse arrays");
            averageOnly.Save(root);
            Check(LiveCalibrationRecord.Load(root,file).Average.SequenceEqual(wave),"average-only archive reload and resave without cache");
            Mutate(file,bad,b=>b[b.Length-1]^=1,false);
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,bad)),"v3 corruption rejected");
            var higher=LiveCalibrationRecord.Load(root,file);higher.Level="20nC";higher.ChargePc=20000;higher.CoefficientPcPerV=20000;higher.SnrDb=30;
            higher.Save(root);
            Check(LiveCalibrationRecord.BestForProject(root,"A",true,"OFFLINE").ChargePc==20000,"same-phase highest valid charge precedes SNR");
            string cache=Path.Combine(root,"Calibration","Acquisition","fresh"),rawPath=Path.Combine(cache,"capture.bin");Directory.CreateDirectory(cache);
            File.WriteAllBytes(rawPath,new byte[]{1,2,3,4});
            higher.RawFiles=Enumerable.Range(0,3).Select(i=>{
                string name=i==0?"capture.bin":"capture"+i+".bin",p=Path.Combine(cache,name);File.WriteAllBytes(p,new byte[]{1,2,3,4});
                return new CalibrationRawReference{RelativePath="Calibration/Acquisition/fresh/"+name,Sha256=LiveCalibrationRecord.HashFile(p)};
            }).ToArray();
            string higherPath=higher.Save(root);LiveCalibrationRecord.RetireAveragedCaptureCache(root,higherPath);
            Check(!File.Exists(rawPath)&&!Directory.Exists(cache),"only verified current raw cache and empty owned child retired");
            Check(LiveCalibrationRecord.Load(root,higherPath).Average.SequenceEqual(wave),"formal record survives cache retirement");
            Directory.CreateDirectory(cache);File.WriteAllBytes(rawPath,new byte[]{9,9});
            LiveCalibrationRecord.RetireAveragedCaptureCache(root,higherPath);
            Check(File.Exists(rawPath),"changed raw file is not deleted");
            File.AppendAllText(Path.Combine(root,"ITEM.in"),"changed");
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,file)),"project identity still enforced");
            Console.WriteLine("CALIBRATION_SELF_CONTAINED_PASS="+checks+"; HARDWARE_CALLS=0; USER_RECORD_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
