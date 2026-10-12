using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace HVX.SignalProcessing
{
    public sealed class NoisePulseTemplate
    {
        public string Id,CreatedUtc,FilterId,SourceHash;
        public double SampleRateHz,SourceStartSeconds,SourceEndSeconds;
        public int PeakIndex;
        public double[] Samples;
    }
    public static class NoisePulseTemplates
    {
        public static string WaveformHash(double[] samples,double x0,double dx)
        {
            using(var sha=System.Security.Cryptography.SHA256.Create()) {
                byte[] header=System.Text.Encoding.UTF8.GetBytes(x0.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|"+dx.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                sha.TransformBlock(header,0,header.Length,header,0);
                var chunk=new byte[65536];for(int i=0;i<samples.Length;) {
                    int count=Math.Min(chunk.Length/8,samples.Length-i);Buffer.BlockCopy(samples,i*8,chunk,0,count*8);sha.TransformBlock(chunk,0,count*8,chunk,0);i+=count;
                }
                sha.TransformFinalBlock(new byte[0],0,0);return Convert.ToBase64String(sha.Hash);
            }
        }
        public static double Similarity(NoisePulseTemplate template,double[] candidate,int peak,double sampleRateHz)
        {
            if(template==null||template.Samples==null||template.Samples.Length<16||candidate==null||template.SampleRateHz<=0||sampleRateHz<=0||template.FilterId!=PdLinearHighPass.FilterId)return 0;
            // Resample on physical time about the packet peak, never stretch a pulse
            // to a different width to artificially increase its similarity.
            int count=template.Samples.Length;double sx=0,sy=0,sxx=0,syy=0,sxy=0;
            for(int i=0;i<count;i++) {
                double at=peak+(i-template.PeakIndex)*sampleRateHz/template.SampleRateHz;
                if(at<0||at>candidate.Length-1)return 0;
                int left=(int)at;double y=left==candidate.Length-1?candidate[left]:candidate[left]+(candidate[left+1]-candidate[left])*(at-left),x=template.Samples[i];
                sx+=x;sy+=y;sxx+=x*x;syy+=y*y;sxy+=x*y;
            }
            double xx=sxx-sx*sx/count,yy=syy-sy*sy/count;
            return xx>1e-24&&yy>1e-24?Math.Min(1,Math.Abs((sxy-sx*sy/count)/Math.Sqrt(xx*yy))):0;
        }
        public static bool Matches(NoisePulseTemplate[] templates,double[] candidate,int peak,double sampleRateHz,double tolerancePercent)
        {
            if(Double.IsNaN(tolerancePercent)||tolerancePercent<0||tolerancePercent>10)throw new ArgumentOutOfRangeException("tolerancePercent");
            return templates!=null&&templates.Any(t=>Similarity(t,candidate,peak,sampleRateHz)>=1-tolerancePercent/100);
        }
        public static string StorePath(string root,bool simulation){return Path.Combine(root,"Calibration",simulation?"Simulation":"Live","noise-templates.xml");}
        public static NoisePulseTemplate[] Load(string root,bool simulation)
        {
            string path=StorePath(root,simulation);if(!File.Exists(path))return new NoisePulseTemplate[0];
            if(new FileInfo(path).Length>32*1024*1024)throw new InvalidDataException("干扰模板文件过大。");
            var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32*1024*1024};
            using(var reader=XmlReader.Create(path,settings)) {
                var templates=(NoisePulseTemplate[])new XmlSerializer(typeof(NoisePulseTemplate[])).Deserialize(reader);Validate(templates);return templates;
            }
        }
        public static void Save(string root,bool simulation,NoisePulseTemplate[] templates)
        {
            Validate(templates);string path=StorePath(root,simulation);
            for(var d=new DirectoryInfo(Path.GetDirectoryName(path));d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("不支持链接目录中的模板写入。");
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("模板文件不能是链接。");
            Directory.CreateDirectory(Path.GetDirectoryName(path));string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew))new XmlSerializer(typeof(NoisePulseTemplate[])).Serialize(stream,templates);
                if(File.Exists(path))File.Replace(temporary,path,path+"."+Guid.NewGuid().ToString("N")+".bak");else File.Move(temporary,path);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        private static void Validate(NoisePulseTemplate[] templates)
        {
            if(templates==null||templates.Length>64)throw new InvalidDataException("干扰模板数量超限。");
            foreach(var t in templates)if(t==null||String.IsNullOrWhiteSpace(t.Id)||t.FilterId!=PdLinearHighPass.FilterId||t.Samples==null||t.Samples.Length<16||t.Samples.Length>65536||
                t.PeakIndex<0||t.PeakIndex>=t.Samples.Length||Double.IsNaN(t.SampleRateHz)||t.SampleRateHz<1e5||t.SampleRateHz>1e9||t.Samples.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v)))throw new InvalidDataException("干扰模板参数无效。");
        }
    }
}
