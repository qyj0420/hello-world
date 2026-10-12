using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.OWTS.Task38
{
    // Independent commissioning archive, NOT a legacy DEM/LOC project record.
    // The checksum detects damage; it is not a signature or encryption.
    public static class Task38RawCaptureStore
    {
        static readonly byte[] Magic=Encoding.ASCII.GetBytes("HXRAW001");
        const long MaximumBytes=512L*1024*1024;
        static void RejectLink(string path)
        {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Archive links are not supported.");}
        static string Target(string directory,string recordId)
        {
            if(!Regex.IsMatch(recordId??"",@"^(HV|SIM)-[A-Za-z0-9-]{1,100}$"))throw new ArgumentException("Invalid record identifier.");
            string root=Path.GetFullPath(directory);Directory.CreateDirectory(root);RejectLink(root);
            return Path.Combine(root,recordId+".hxraw");
        }
        static bool Positive(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>0;}
        public static string SaveNew(string directory,Task38RunRecord record,Task38CycleResult result)
        {
            if(record==null||result==null||record.RecordId!=result.RecordId)throw new ArgumentException("Capture identity mismatch.");
            if(!result.Success||result.PicoHardwareOverflowDiagnosticOnly||result.UsbDividerRawVolts==null||result.PicoRawAdc==null||
                result.UsbDividerRawVolts.Length==0||result.PicoRawAdc.Length==0||
                !Positive(result.UsbSampleRateHz)||!Positive(result.PicoSampleRateHz)||!Positive(result.PicoFullScaleVolts)||result.PicoMaximumAdc<=0)
                throw new InvalidDataException("Capture quality or scaling metadata is incomplete; not automatically saved.");
            long bytes=8L*result.UsbDividerRawVolts.Length+2L*result.PicoRawAdc.Length;
            if(bytes>MaximumBytes-4096)throw new InvalidDataException("Capture exceeds archive limit.");
            foreach(double value in result.UsbDividerRawVolts)
                if(Double.IsNaN(value)||Double.IsInfinity(value))throw new InvalidDataException("Nonfinite voltage sample.");
            string target=Target(directory,record.RecordId);
            if(File.Exists(target))throw new IOException("An existing capture must not be overwritten.");
            string pending=target+"."+Guid.NewGuid().ToString("N")+".pending";
            try
            {
                using(var file=new FileStream(pending,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                {
                    using(var writer=new BinaryWriter(file,Encoding.UTF8,true))
                    {
                        writer.Write(Magic);writer.Write(record.RecordId);writer.Write(record.Simulation);
                        writer.Write(record.FingerprintSha256??"");writer.Write(result.StartedUtc.ToBinary());writer.Write(result.CompletedUtc.ToBinary());
                        writer.Write(result.IsBackgroundCapture);writer.Write(result.UsbSampleRateHz);writer.Write(result.PicoSampleRateHz);
                        writer.Write(result.PicoSampleIntervalNanoseconds);writer.Write(result.PicoFullScaleVolts);writer.Write(result.PicoMaximumAdc);
                        writer.Write(result.PicoResolutionModeBits);writer.Write(result.PicoRangeHardwareBits);writer.Write(result.PicoRangeIndexUsed);
                        writer.Write(result.UsbDividerRawVolts.Length);writer.Write(result.PicoRawAdc.Length);
                        foreach(double v in result.UsbDividerRawVolts)writer.Write(v);
                        foreach(short v in result.PicoRawAdc)writer.Write(v);
                        writer.Flush();
                        file.Position=0;byte[] checksum;using(var sha=SHA256.Create())checksum=sha.ComputeHash(file);
                        file.Position=file.Length;writer.Write(checksum);writer.Flush();file.Flush(true);
                    }
                }
                Verify(pending);
                File.Move(pending,target);return target;
            }
            finally{if(File.Exists(pending))File.Delete(pending);}
        }
        public static void Verify(string path)
        {
            RejectLink(path);
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length<64||file.Length>MaximumBytes)throw new InvalidDataException("Invalid archive length.");
                for(int i=0;i<Magic.Length;i++)if(file.ReadByte()!=Magic[i])throw new InvalidDataException("Invalid archive version.");
                file.Position=0;long remaining=file.Length-32;byte[] buffer=new byte[65536];
                using(var sha=SHA256.Create())
                {
                    while(remaining>0){int count=file.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(count<=0)throw new EndOfStreamException();sha.TransformBlock(buffer,0,count,buffer,0);remaining-=count;}
                    sha.TransformFinalBlock(new byte[0],0,0);
                    int difference=0;foreach(byte value in sha.Hash){int stored=file.ReadByte();difference|=stored<0?1:value^stored;}
                    if(difference!=0)throw new InvalidDataException("Raw capture checksum mismatch.");
                }
            }
        }
        public static string MoveToRecovery(string directory,string recordId)
        {
            string source=Target(directory,recordId);Verify(source);
            string recovery=Path.Combine(Path.GetFullPath(directory),"recovery");
            Directory.CreateDirectory(recovery);RejectLink(recovery);
            string destination=Path.Combine(recovery,recordId+"-"+Guid.NewGuid().ToString("N")+".hxraw");
            File.Move(source,destination);return destination;
        }
    }
}
