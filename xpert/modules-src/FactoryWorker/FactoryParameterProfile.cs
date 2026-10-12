using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // UI configuration only. Never serialize worker results, connection state,
    // AO, output permission, or a per-round acknowledgement.
    public sealed class FactoryParameterValue
    {
        public string Name;
        public decimal Value;
    }
    public sealed class FactoryParameterProfile
    {
        public int Version=1;
        public List<FactoryParameterValue> Numbers=new List<FactoryParameterValue>();
        public int Resolution,Range,Coupling,KnownCharge,CalibrationFrequency;
        public bool Bandwidth,ReferenceCapacitor,OscillationCircuit,RangeReminder;
        public string ArtSerial="",ArtName="",PicoSerial="",ArtLibrary="",PicoLibrary="";
        public bool Continuous,Accumulate=true;
        public int RepeatCount=1,TfScope;
        public int CapturePosition;
        public decimal LocationChargeMaximumPc;
        public bool LocationBoxZoom=true;
        // New profiles show the median trend. An explicitly stored old choice
        // is still restored, not silently reset on upgrade.
        public bool MonitorExtrema=false;
        public bool RepeatPrompts=true;
        public decimal ThresholdPc=1;
        public decimal AcceptanceLoad=200,AcceptanceMaximum=28,AcceptanceKnownCharge=100;
        public string VoltageReferenceId="",PdReferenceId="";
        public FactoryStoredCalibration Calibration;
        internal FactoryParameterProfile Clone()
        {
            var serializer=new XmlSerializer(typeof(FactoryParameterProfile));
            using(var stream=new MemoryStream()) { serializer.Serialize(stream,this);stream.Position=0;return (FactoryParameterProfile)serializer.Deserialize(stream); }
        }
    }
    public sealed class FactoryStoredCalibration
    {
        public string ArtSerial="",PicoSerial="",Origin="";
        public bool Simulation,Bandwidth;
        public int Coupling,Bits;
        public double Rate,Coefficient;
    }
    internal sealed class FactoryParameterStore
    {
        private readonly string directory;
        internal FactoryParameterStore(string directory) { this.directory=Path.GetFullPath(directory); }
        internal int LastMode()
        { string path=Path.Combine(directory,"last-mode.txt");int mode;return File.Exists(path) && Int32.TryParse(File.ReadAllText(path),out mode) && (mode==0 || mode==1)?mode:0; }
        internal void RememberMode(int mode)
        {
            PathFor(mode);Directory.CreateDirectory(directory);string path=Path.Combine(directory,"last-mode.txt"),temp=path+".tmp";
            File.WriteAllText(temp,mode.ToString());if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        internal string PathFor(int mode)
        {
            if(mode!=0 && mode!=1)throw new ArgumentException("Unknown parameter mode");
            return Path.Combine(directory,mode==0?"main-simulation.xml":"main-hardware.xml");
        }
        internal FactoryParameterProfile Read(int mode)
        {
            var recent=ReadFile(PathFor(mode));
            if(recent==null || !HasDevicePair(recent.ArtSerial,recent.PicoSerial))return recent;
            // The per-device file is authoritative; the old filename is only
            // the last-selected snapshot and a migration source for old installs.
            return ReadForDevice(mode,recent.ArtSerial,recent.PicoSerial);
        }
        internal static bool HasDevicePair(string art,string pico)
        { return !String.IsNullOrWhiteSpace(art) && !String.IsNullOrWhiteSpace(pico); }
        internal string PathForDevice(int mode,string art,string pico)
        {
            PathFor(mode);
            if(!HasDevicePair(art,pico))throw new ArgumentException("两卡序列号不完整");
            // Length-prefix both serials: delimiters and path characters in a
            // real serial (e.g. 13000/0059) cannot collide or escape this folder.
            string identity=art.Length+":"+art+pico.Length+":"+pico;
            string digest;using(var hash=SHA256.Create())digest=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-","").ToLowerInvariant();
            return Path.Combine(directory,"devices",mode==0?"simulation":"hardware",digest+".xml");
        }
        internal FactoryParameterProfile ReadForDevice(int mode,string art,string pico)
        {
            string path=PathForDevice(mode,art,pico);
            var profile=ReadFile(path);
            if(profile==null)
            {
                var legacy=ReadFile(PathFor(mode));
                if(legacy!=null && legacy.ArtSerial==art && legacy.PicoSerial==pico)profile=legacy;
            }
            if(profile!=null && (profile.ArtSerial!=art || profile.PicoSerial!=pico))
                throw new InvalidDataException("参数档案与设备序列号不匹配");
            return profile;
        }
        private static FactoryParameterProfile ReadFile(string path)
        {
            if(!File.Exists(path))return null;
            if(new FileInfo(path).Length>131072)throw new InvalidDataException("参数文件过大");
            using(var reader=XmlReader.Create(path,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=131072 }))
            {
                var profile=(FactoryParameterProfile)new XmlSerializer(typeof(FactoryParameterProfile)).Deserialize(reader);
                Validate(profile);return profile;
            }
        }
        internal void Save(int mode,FactoryParameterProfile profile)
        {
            Validate(profile);
            if(profile.Calibration!=null && (profile.Calibration.Simulation!=(mode==0) || profile.Calibration.ArtSerial!=profile.ArtSerial || profile.Calibration.PicoSerial!=profile.PicoSerial))
                throw new InvalidDataException("校准来源与当前序列号/模式不匹配");
            if(HasDevicePair(profile.ArtSerial,profile.PicoSerial))WriteFile(PathForDevice(mode,profile.ArtSerial,profile.PicoSerial),profile);
            WriteFile(PathFor(mode),profile);
        }
        private static void WriteFile(string path,FactoryParameterProfile profile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    using(var writer=XmlWriter.Create(stream,new XmlWriterSettings { Encoding=new UTF8Encoding(false),Indent=true,CloseOutput=false }))
                        new XmlSerializer(typeof(FactoryParameterProfile)).Serialize(writer,profile);
                    stream.Flush(true);
                }
                if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        private static void Validate(FactoryParameterProfile p)
        {
            if(p==null || p.Version!=1 || p.Numbers==null || p.Numbers.Count>100 || p.CapturePosition<-50 || p.CapturePosition>50 || p.LocationChargeMaximumPc<0 || p.LocationChargeMaximumPc>100000000)throw new InvalidDataException("参数文件版本或字段无效");
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in p.Numbers)
                if(item==null || String.IsNullOrWhiteSpace(item.Name) || item.Name.Length>64 || !names.Add(item.Name))
                    throw new InvalidDataException("参数名称无效或重复");
            foreach(var value in new[]{p.ArtSerial,p.ArtName,p.PicoSerial,p.ArtLibrary,p.PicoLibrary,p.VoltageReferenceId,p.PdReferenceId})
                if(value!=null && value.Length>2048)throw new InvalidDataException("参数文本过长");
        }
    }
}
