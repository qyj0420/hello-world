using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Xiongxin.ZeroBench;

// Pure synthetic binary compatibility/performance test. No native backend or product entry.
internal static class BenchCaptureBinaryIoSmoke
{
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static byte[] Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return h.ComputeHash(s);}
    static BenchCapture Fixture(double ms)
    {
        var settings=new BenchSettings{CaptureDurationMs=ms,TransientExclusionMs=0,RangeIndex=5,RealHardware=false};
        var c=new BenchCapture{Settings=settings,VoltageRaw=new double[settings.ExpectedArtCount],PdAdc=new short[settings.RequestedPicoCount],
            Simulation=true,ArtSerial="SIM-ART",PicoSerial="SIM-PICO",Runtime="synthetic-only",CapturedUtc=new DateTime(2026,10,9,0,0,0,DateTimeKind.Utc),
            PicoFullScaleV=BenchSettings.Ranges[settings.RangeIndex],HardwareBits=PicoCaptureConfiguration.HardwareBits(settings.ResolutionBits,settings.RangeIndex)};
        for(int i=0;i<c.VoltageRaw.Length;i++)c.VoltageRaw[i]=Math.Sin(i*.031)*9.1;
        c.VoltageRaw[0]=BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));
        c.VoltageRaw[1]=Double.Epsilon;c.VoltageRaw[2]=-Double.Epsilon;
        for(int i=0;i<c.PdAdc.Length;i++)c.PdAdc[i]=unchecked((short)(i*317));
        c.ValidateCapture();return c;
    }
    static void LegacyWrite(BenchCapture c,string path)
    {
        using(var w=new BinaryWriter(File.Create(path)))
        {
            w.Write("XIONGXIN-ZERO-BENCH-1");w.Write(c.Simulation);w.Write(c.CapturedUtc.ToBinary());
            w.Write(c.ArtSerial);w.Write(c.PicoSerial);w.Write(c.Runtime);
            w.Write(c.ArtRateHz);w.Write(c.PicoRateHz);w.Write(c.MaximumAdc);w.Write(c.PicoFullScaleV);w.Write(c.HardwareBits);w.Write(c.Overflow);
            w.Write(c.VoltageRaw.Length);foreach(double v in c.VoltageRaw)w.Write(v);
            w.Write(c.PdAdc.Length);foreach(short v in c.PdAdc)w.Write(v);
        }
    }
    static void Same(BenchCapture a,BenchCapture b)
    {
        Check(a.PdAdc.SequenceEqual(b.PdAdc),"all ADC codes exact");
        Check(a.VoltageRaw.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(b.VoltageRaw.Select(BitConverter.DoubleToInt64Bits)),"all voltage bits including signed zero exact");
        Check(a.CapturedUtc==b.CapturedUtc&&a.Runtime==b.Runtime&&a.PicoRateHz==b.PicoRateHz&&a.ArtRateHz==b.ArtRateHz&&a.PicoFullScaleV==b.PicoFullScaleV,"metadata unchanged");
    }
    static void Reject(string directory,string why)
    {
        bool rejected=false;try{BenchCapture.Load(directory);}catch(InvalidDataException){rejected=true;}catch(EndOfStreamException){rejected=true;}
        Check(rejected,why);
    }
    sealed class FragmentStream : MemoryStream
    {
        internal FragmentStream(byte[] data):base(data){}
        public override int Read(byte[] b,int o,int n){return base.Read(b,o,Math.Min(7,n));}
    }
    static void ChunkChecks()
    {
        var method=typeof(BenchCapture).GetMethod("ReadSamples",BindingFlags.NonPublic|BindingFlags.Static);
        Check(method!=null,"actual chunk reader present");
        foreach(int count in new[]{0,1,32767,32768,32769,70000})
        {
            short[] expected=Enumerable.Range(0,count).Select(i=>unchecked((short)(i*17))).ToArray();
            byte[] bytes;using(var s=new MemoryStream()){using(var w=new BinaryWriter(s)){foreach(short v in expected)w.Write(v);w.Flush();bytes=s.ToArray();}}
            short[] read=new short[count];using(var r=new BinaryReader(new FragmentStream(bytes)))method.Invoke(null,new object[]{r,read,sizeof(short)});
            Check(expected.SequenceEqual(read),"fragmented short reads at boundary "+count);
            if(count>0)
            {
                bool rejected=false;
                try{using(var r=new BinaryReader(new FragmentStream(bytes.Take(bytes.Length-1).ToArray())))method.Invoke(null,new object[]{r,new short[count],sizeof(short)});}
                catch(TargetInvocationException ex){rejected=ex.InnerException is EndOfStreamException;}
                Check(rejected,"fragmented truncated payload rejects "+count);
            }
        }
        var reverse=typeof(BenchCapture).GetMethod("ReverseElements",BindingFlags.NonPublic|BindingFlags.Static);
        foreach(int width in new[]{2,8})
        {
            byte[] bytes=Enumerable.Range(0,width*4).Select(i=>(byte)i).ToArray(),original=(byte[])bytes.Clone();
            reverse.Invoke(null,new object[]{bytes,bytes.Length,width});
            Check(bytes.Select((v,i)=>v==original[(i/width)*width+width-1-i%width]).All(x=>x),"per-element byte order "+width);
            reverse.Invoke(null,new object[]{bytes,bytes.Length,width});Check(bytes.SequenceEqual(original),"byte order reversible "+width);
        }
    }
    static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))throw new Exception("new fixture path required");Directory.CreateDirectory(root);
            foreach(double ms in new[]{1.0,2.0,60.0})
            {
                var c=Fixture(ms);string folder=Path.Combine(root,"ms"+ms);Directory.CreateDirectory(folder);
                string golden=Path.Combine(folder,"legacy.bin"),current=Path.Combine(folder,"capture.bin");
                var timer=Stopwatch.StartNew();LegacyWrite(c,golden);long oldMs=timer.ElapsedMilliseconds;
                timer.Restart();c.Save(folder);long saveMs=timer.ElapsedMilliseconds;
                Check(Hash(golden).SequenceEqual(Hash(current)),"complete binary file equals legacy writer at "+ms+" ms");
                timer.Restart();Same(c,BenchCapture.Load(folder));long loadMs=timer.ElapsedMilliseconds;
                File.Copy(golden,current,true);Same(c,BenchCapture.Load(folder));
                c.Save(folder);Check(Hash(golden).SequenceEqual(Hash(current)),"replacement stays byte compatible");
                Console.WriteLine("DURATION_MS="+ms+";BYTES="+new FileInfo(current).Length+";LEGACY_WRITE_MS="+oldMs+";SAVE_MS="+saveMs+";LOAD_MS="+loadMs);
                // Mutations only affect this test's newly-created synthetic file.
                using(var s=File.Open(current,FileMode.Open,FileAccess.Write)){s.SetLength(s.Length-1);}Reject(folder,"truncated last ADC rejected");
                File.Copy(golden,current,true);using(var s=File.Open(current,FileMode.Append,FileAccess.Write))s.WriteByte(0);Reject(folder,"undeclared tail rejected");
                File.Copy(golden,current,true);using(var s=File.Open(current,FileMode.Open,FileAccess.Write))s.SetLength(110);Reject(folder,"truncated metadata/payload rejected");
                c.ArtFullScaleV=28000.0/11000*1.5;c.Save(folder);
                var ranged=BenchCapture.Load(folder);Same(c,ranged);Check(ranged.ArtFullScaleV==c.ArtFullScaleV,"adaptive ART range preserved");
                using(var reader=new BinaryReader(File.OpenRead(current)))Check(reader.ReadString()=="XIONGXIN-ZERO-BENCH-2","new raw range metadata is versioned");
            }
            ChunkChecks();
            Console.WriteLine("BENCH_BINARY_IO_PASS="+checks+";HARDWARE_CALLS=0;DRIVERS_LOADED=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
