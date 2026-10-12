using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;
using HVX.SignalProcessing;

namespace HVX.PdReview
{
    // DEM is a detected display, never a pulse waveform. Only the bound original
    // ADC record is used to count pulses or calculate calibrated charge/phase.
    internal sealed class ReviewCapacitorDemInput
    {
        internal ReviewLocationFileData Data;
        internal double[] HighPassed,VoltageKv;
        internal int PhaseCode;
        internal double TargetKv;
        internal string Status;
        internal Func<int,double> PhaseAt;
        sealed class Header
        {
            internal Dictionary<string,object> Context;
            internal string Calibration;
            internal double Voltage,Rate;
            internal int Count;
        }
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        internal static bool IsDem(string path){return System.String.Equals(Path.GetExtension(path),".dem",StringComparison.OrdinalIgnoreCase);}
        internal static ReviewLocationFileData Metadata(string path)
        {
            var h=ReadHeader(path,null);
            return new ReviewLocationFileData{DataPath=path,SampleRateHz=Number(h.Context,"picoRateHz"),VoltageKv=h.Voltage,
                AcquisitionQuality=ReviewAcquisitionQuality.FromContext(h.Context),
                ZeroVoltageAnalysisOptIn=Boolean(h.Context,"zeroVoltageAnalysisOptIn")};
        }
        internal static bool ZeroOptIn(string path)
        {
            try{var metadata=Metadata(path);return metadata.VoltageKv==0&&metadata.ZeroVoltageAnalysisOptIn;}
            catch(Exception e)when(e is IOException||e is ArgumentException||e is InvalidOperationException||e is FormatException||e is OverflowException){return false;}
        }
        internal static ReviewLocationFileData Display(string path,string root)
        {
            if(!HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root))throw new InvalidDataException("DEM 电容统计需要明确的电容项目。");
            OwnPath(root,path);double[] charge;var h=ReadHeader(path,out charge);
            return new ReviewLocationFileData{DataPath=path,ChargeSamples=charge,SampleRateHz=h.Rate,VoltageKv=h.Voltage,
                AcquisitionQuality=ReviewAcquisitionQuality.FromContext(h.Context),
                ZeroVoltageAnalysisOptIn=Boolean(h.Context,"zeroVoltageAnalysisOptIn")};
        }
        static Header ReadHeader(string path,out double[] charge)
        {var values=new List<double>();var h=ReadHeader(path,values);charge=values.ToArray();return h;}
        static Header ReadHeader(string path,List<double> display)
        {
            try{return ReadHeaderCore(path,display);}
            catch(Exception e)when(e is ArgumentException||e is FormatException||e is OverflowException||e is InvalidOperationException){throw new InvalidDataException("DEM 来源表头无效。",e);}
        }
        static Header ReadHeaderCore(string path,List<double> display)
        {
            HVX.Storage.PressureRecordPublication.RequireReadable(path);
            if(new FileInfo(path).Length>2*1024*1024)throw new InvalidDataException("DEM 记录超过单段大小限制。");
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var reader=new StreamReader(stream,new UTF8Encoding(false,true),true)){
                var lines=new string[16];for(int i=0;i<16;i++)if((lines[i]=ReviewAcquisitionQuality.ReadHeaderLine(reader))==null)throw new InvalidDataException("DEM 表头不完整。");
                var h=new Header{Calibration=Cell(lines[2],"Cal.File-path:"),Voltage=Parse(Cell(lines[5],"V-scale max (kV):")),
                    Count=Int(Parse(Cell(lines[7],"Number of Samples:"))),Rate=1/Parse(Cell(lines[8],"Delta-x [s]:"))};
                h.Context=ReviewAcquisitionQuality.ParseContext(Cell(lines[12],"Acquisition context:"));
                ReviewAcquisitionQuality.FromContext(h.Context);
                if(h.Context==null||Text(h.Context,"measurementKind")!="Capacitor"||Boolean(h.Context,"locationApplicable")||Boolean(h.Context,"rawHasDerivedChargeFilter")||
                    Text(h.Context,"rawSampleUnits")!="Pico ADC counts; ART input volts"||Text(h.Context,"highPassFilterId")!=PdLinearHighPass.FilterId||
                    h.Count<16||h.Count>4800||Math.Abs(h.Rate-80000)>.0001||h.Voltage<0)throw new InvalidDataException("DEM 电容来源或单位无效；不能作为定位波形。");
                Guid id;if(!Guid.TryParse(Text(h.Context,"locationAcquisitionId"),out id)||id==Guid.Empty)throw new InvalidDataException("DEM 采集身份无效。");
                var name=Regex.Match(Path.GetFileName(path),@"^(ALL|A|B|C)_([-+]?[0-9]+(?:\.[0-9]+)?)kV_",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                if(!name.Success||name.Groups[1].Value.ToUpperInvariant()!=Text(h.Context,"phase")||Math.Abs(Parse(name.Groups[2].Value)-h.Voltage)>.000501||
                    Math.Abs(Number(h.Context,"targetPeakKv")-h.Voltage)>1e-9)throw new InvalidDataException("DEM 相别、电压与采集身份不一致。");
                int count=0;string line;
                while((line=reader.ReadLine())!=null){if(line.Length>256)throw new InvalidDataException("DEM 数据行过长。");var cells=line.Split('\t');
                    if(cells.Length!=2||Parse(cells[0])<0)throw new InvalidDataException("DEM 检波电量无效。");Parse(cells[1]);
                    if(++count>h.Count)throw new InvalidDataException("DEM 数据数量超过声明。");if(display!=null)display.Add(Parse(cells[0]));}
                if(count!=h.Count)throw new InvalidDataException("DEM 数据数量与声明不一致。");return h;
            }
        }
        internal static ReviewCapacitorDemInput Read(string path,string root,bool allowZero)
        {return ReadCore(path,root,allowZero,true);}
        internal static void Validate(string path,string root,bool allowZero)
        {ReadCore(path,root,allowZero,false);}
        static ReviewCapacitorDemInput ReadCore(string path,string root,bool allowZero,bool deriveStatistics)
        {
            if(!IsDem(path)||!HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root))throw new InvalidDataException("电容统计需要电容项目及正式 DEM 记录。");
            OwnPath(root,path);var h=ReadHeader(path,null);var c=h.Context;
            if(h.Voltage==0&&(!allowZero||!Boolean(c,"zeroVoltageAnalysisOptIn")))throw new InvalidDataException("零电压记录未获准分析，仅作为噪声参考。");
            if(Boolean(c,"overRange")&&!Boolean(c,"overRangeAccepted"))throw new InvalidDataException("采集质量未确认，不能进入统计。");
            string relative=Text(c,"rawCaptureDirectory");
            if(Path.IsPathRooted(relative)||relative.Split('\\','/').Any(p=>p==".."||p=="."||p.Length==0))throw new InvalidDataException("原始来源路径无效。");
            string dir=OwnPath(root,Path.Combine(root,relative));string boundary=Path.GetFullPath(Path.Combine(root,"Acquisition","Pressure"))+Path.DirectorySeparatorChar;
            if(!dir.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||Text(c,"rawCaptureFile")!="capture.bin"||Text(c,"rawSettingsFile")!="settings.xml")
                throw new InvalidDataException("原始来源不属于当前项目加压记录。");
            string raw=OwnPath(root,Path.Combine(dir,"capture.bin")),settings=OwnPath(root,Path.Combine(dir,"settings.xml"));
            using(var input=new FileStream(raw,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var config=new FileStream(settings,FileMode.Open,FileAccess.Read,FileShare.Read)){
                if(input.Length<64||input.Length>64L*1024*1024||config.Length<1||config.Length>65536)throw new InvalidDataException("原始记录大小无效。");
                VerifyHash(input,Text(c,"rawSha256"));VerifyHash(config,Text(c,"rawSettingsSha256"));
                var xml=XmlReader.Create(config,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
                Dictionary<string,string> s;
                using(xml){var doc=XDocument.Load(xml);if(doc.Root==null||doc.Root.Name.LocalName!="BenchSettings")throw new InvalidDataException("原始参数类型错误。");
                    s=new Dictionary<string,string>(StringComparer.Ordinal);foreach(var field in doc.Root.Elements()){if(s.ContainsKey(field.Name.LocalName)||field.HasElements)throw new InvalidDataException("重复或嵌套的原始参数。");s.Add(field.Name.LocalName,field.Value);}}
                input.Position=0;
                using(var reader=new BinaryReader(input,Encoding.UTF8,true)){
                    string magic=BoundedString(reader);if(magic!="XIONGXIN-ZERO-BENCH-1"&&magic!="XIONGXIN-ZERO-BENCH-2")throw new InvalidDataException("原始 ADC 格式不支持。");
                    bool simulation=reader.ReadBoolean();var captured=DateTime.FromBinary(reader.ReadInt64());string art=BoundedString(reader),pico=BoundedString(reader),runtime=BoundedString(reader);
                    double artRate=reader.ReadDouble(),picoRate=reader.ReadDouble();int maximum=reader.ReadInt32();double scale=reader.ReadDouble();int bits=reader.ReadInt32();bool overflow=reader.ReadBoolean();
                    double artScale=magic.EndsWith("-2",StringComparison.Ordinal)?reader.ReadDouble():10;
                    double duration=Value(s,"CaptureDurationMs"),requested=Value(s,"RequestedPicoRateHz"),coefficient=Value(s,"PdCalibrationPcPerV");
                    int pre=Int(Value(s,"PreTriggerPercent")),range=Int(Value(s,"RangeIndex"));double[] ranges={.01,.02,.05,.1,.2,.5,1,2,5,10,20};
                    DateTime declared;if(!DateTime.TryParse(Text(c,"capturedUtc"),Inv,DateTimeStyles.RoundtripKind,out declared)||captured.Kind!=DateTimeKind.Utc||captured!=declared.ToUniversalTime()||
                        simulation!=Boolean(c,"simulation")||simulation==Bool(s,"RealHardware")||pico!=String(s,"PicoSerial")||art!=String(s,"ArtSerial")||System.String.IsNullOrWhiteSpace(runtime)||
                        duration<=0||duration>60||pre<0||pre>100||range<0||range>10||scale!=ranges[range]||maximum<=0||maximum>32767||!(artScale>0)||artScale>10||
                        bits!=HardwareBits(Int(Value(s,"ResolutionBits")),range)||Math.Abs(artRate-80000)>.5||picoRate<6000000||picoRate>1e9||Math.Abs(picoRate-requested)>requested*1e-9||
                        artRate!=Number(c,"artRateHz")||picoRate!=Number(c,"picoRateHz")||scale!=Number(c,"rawPicoFullScaleV")||maximum!=Number(c,"rawMaximumAdc")||
                        !Bool(s,"PicoChannelAEnabled")||Value(s,"PicoAnalogOffsetVolts")!=0||(Value(s,"PicoCoupling")!=0&&Value(s,"PicoCoupling")!=1)||
                        // BenchSettings describes the input-only ADC session,
                        // even for a nonzero factory round. The actual pressure
                        // target belongs to the bound DEM acquisition context.
                        coefficient<=0||coefficient!=Number(c,"calibrationCoefficientPcPerV")||Value(s,"TargetVoltageV")!=0||overflow&&!Boolean(c,"overRangeAccepted"))
                        throw new InvalidDataException("原始 ADC 与采集参数、时间、设备或质量来源不一致。");
                    int vn=reader.ReadInt32(),expectedV=Int(Math.Round(artRate*duration/1000));if(vn!=expectedV||vn!=Number(c,"rawArtSamples")||vn<16||vn>4800)throw new InvalidDataException("ART 原始数量无效。");
                    var voltage=new double[vn];double divider=Value(s,"DividerVPerV");if(!(divider>0))throw new InvalidDataException("分压比无效。");
                    for(int i=0;i<vn;i++){double v=reader.ReadDouble();if(!Finite(v)||Math.Abs(v)>artScale*1.001)throw new InvalidDataException("ART 原始采样值无效。");voltage[i]=v*divider/1000;}
                    int pn=reader.ReadInt32(),expectedP=Int(Math.Round(picoRate*duration/1000));if(pn!=expectedP||pn!=Number(c,"rawPicoSamples")||pn<16||pn>16000000||input.Length-input.Position!=2L*pn)
                        throw new InvalidDataException("Pico 原始数量或尾部数据无效。");
                    var adc=new short[pn];var histogram=new int[65536];for(int i=0;i<pn;i++){short v=reader.ReadInt16();if(Math.Abs((int)v)>maximum)throw new InvalidDataException("原始 ADC 超出实际量化范围。");adc[i]=v;histogram[v+32768]++;}
                    if(h.Count!=Math.Min((int)Math.Floor(Math.Min(vn/artRate,pn/picoRate)*80000+1e-8),(int)Math.Floor((vn-1)*80000/artRate+1e-8)+1))throw new InvalidDataException("DEM 与原始共同时长不一致。");
                    string cal=OwnPath(root,Path.IsPathRooted(h.Calibration)?h.Calibration:Path.Combine(root,h.Calibration));
                    using(var guard=new FileStream(cal,FileMode.Open,FileAccess.Read,FileShare.Read)){
                        using(var sha=SHA256.Create())if(Convert.ToBase64String(sha.ComputeHash(guard))!=Text(c,"calibrationSourceHash"))throw new InvalidDataException("电容校准来源已改变。");
                        var record=HVX.CalibrationStorage.LiveCalibrationRecord.Load(root,cal,true);
                        if(!record.IsCapacitor||record.IsTdr||record.DeviceSerial!=pico||record.CoefficientPcPerV!=coefficient||record.Phase!="ALL"&&record.Phase!=Text(c,"phase")||
                            record.FilterId!=PdLinearHighPass.FilterId)throw new InvalidDataException("电容校准类型、相别、设备或系数不匹配。");
                    }
                    // Session restore verifies the complete bound sources but
                    // never reruns pulse extraction, FIR or a location algorithm.
                    if(!deriveStatistics)return null;
                    int total=0,median=0;for(int i=0;i<histogram.Length;i++){total+=histogram[i];if(total>pn/2){median=i-32768;break;}}
                    double chargeScale=scale/maximum*coefficient;var charge=new double[pn];for(int i=0;i<pn;i++)charge[i]=(adc[i]-median)*chargeScale;
                    var result=new ReviewCapacitorDemInput{Data=new ReviewLocationFileData{DataPath=path,ChargeSamples=charge,SampleRateHz=picoRate,VoltageKv=h.Voltage,
                        AcquisitionQuality=ReviewAcquisitionQuality.FromContext(c),
                        ZeroVoltageAnalysisOptIn=Boolean(c,"zeroVoltageAnalysisOptIn")},HighPassed=PdLinearHighPass.Apply(charge,picoRate),VoltageKv=voltage,TargetKv=h.Voltage,
                        PhaseCode=Text(c,"phase")=="A"?1:Text(c,"phase")=="B"?2:Text(c,"phase")=="C"?3:0,PhaseAt=i=>Double.NaN};
                    bool shared=!simulation&&h.Voltage>0&&runtime.IndexOf("ART PFI0 / Pico EXT 共同物理检测上升沿",StringComparison.Ordinal)>=0&&
                        runtime.IndexOf("NO_COMMON_TRIGGER",StringComparison.Ordinal)<0&&runtime.IndexOf("FREE_RUN",StringComparison.Ordinal)<0&&String(s,"ArtTrigger")=="/"+String(s,"ArtDevice")+"/PFI0"&&!Bool(s,"FallingTrigger")&&Value(s,"ExternalTriggerVolts")>0&&Value(s,"ExternalTriggerVolts")<5&&
                        (long)vn*pre%100==0&&(long)pn*pre%100==0&&Math.Abs(((long)vn*pre/100)/artRate-((long)pn*pre/100)/picoRate)<1e-12;
                    Xiongxin.OWTS.Data.OscillationCycleWindow window=null;
                    if(shared)for(int n=1;n<=100;n++){var w=Xiongxin.OWTS.Data.OscillationCycleWindow.Find(voltage,artRate,n,h.Voltage);if(!w.Success)break;window=w;}
                    if(window!=null){var crossings=window.CrossingSeconds;bool positive=window.FirstCrossingPositive;double end=Math.Min((vn-1)/artRate,(pn-1)/picoRate);
                        result.PhaseAt=i=>MeasuredPhase(i/picoRate,crossings,positive,end);}
                    result.Status="source=DEM-bound-original-ADC; coefficient=captured-Capacitor-CAL-once; raw-filter=none; analysis-filter="+PdLinearHighPass.FilterId+
                        "; location-algorithm=not-called; phase="+(window==null?"unavailable-no-verified-common-trigger/crossings":"measured-ART-crossings-only")+
                        "; source-quality="+(Boolean(c,"overRange")||overflow?"operator-acknowledged-warning":"published-no-overrange-warning");return result;
                }
            }
        }
        static double MeasuredPhase(double t,double[] times,bool positive,double end)
        {if(t<times[0]||t>times[times.Length-1]||t>end)return Double.NaN;int lo=0,hi=times.Length-1;while(hi-lo>1){int m=(hi+lo)/2;if(times[m]<=t)lo=m;else hi=m;}return ((positive?0:180)+180*(lo+(t-times[lo])/(times[hi]-times[lo])))%360;}
        static string OwnPath(string root,string path)
        {
            root=Path.GetFullPath(root).TrimEnd('\\','/');path=Path.GetFullPath(path);
            if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("记录来源越出当前项目。");
            string current=path;while(!System.String.IsNullOrEmpty(current)){if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("记录来源不允许链接路径。");current=Path.GetDirectoryName(current);}return path;
        }
        static void VerifyHash(FileStream stream,string expected)
        {if(!Regex.IsMatch(expected??"",@"\A[0-9A-F]{64}\z"))throw new InvalidDataException("原始来源哈希格式错误。");using(var sha=SHA256.Create())if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=expected)throw new InvalidDataException("原始来源哈希不匹配。");stream.Position=0;}
        static string BoundedString(BinaryReader reader)
        {int n=0,shift=0;for(int i=0;i<5;i++){byte b=reader.ReadByte();if(i==4&&b>7)throw new InvalidDataException("原始字符串长度越界。");n|=(b&127)<<shift;if((b&128)==0){if(n<0||n>4096)throw new InvalidDataException("原始字符串过长。");var bytes=reader.ReadBytes(n);if(bytes.Length!=n)throw new EndOfStreamException();return new UTF8Encoding(false,true).GetString(bytes);}shift+=7;}throw new InvalidDataException("原始字符串长度错误。");}
        static int HardwareBits(int bits,int range){if(bits!=8&&bits!=12&&bits!=14&&bits!=15&&bits!=16)throw new InvalidDataException("分辨率无效。");return bits==12&&range==0?11:bits>=14&&range==0?bits-2:bits>=14&&range==1?bits-1:bits;}
        static string Cell(string line,string key){if(!line.StartsWith(key+"\t",StringComparison.Ordinal))throw new InvalidDataException("DEM 表头类型错误。");return line.Substring(key.Length+1).Trim();}
        static string Text(Dictionary<string,object> values,string key){object value;if(!values.TryGetValue(key,out value)||!(value is string))throw new InvalidDataException("缺少来源字段："+key);return (string)value;}
        static bool Boolean(Dictionary<string,object> values,string key){object value;if(!values.TryGetValue(key,out value)||!(value is bool))throw new InvalidDataException("缺少来源标记："+key);return (bool)value;}
        static double Number(Dictionary<string,object> values,string key){object value;if(!values.TryGetValue(key,out value)||!(value is int||value is long||value is double||value is decimal))throw new InvalidDataException("缺少来源数值："+key);return Parse(Convert.ToString(value,Inv));}
        static string String(Dictionary<string,string> values,string key){string value;if(!values.TryGetValue(key,out value))throw new InvalidDataException("缺少原始参数："+key);return value;}
        static double Value(Dictionary<string,string> values,string key){return Parse(String(values,key));}
        static bool Bool(Dictionary<string,string> values,string key){bool result;if(!System.Boolean.TryParse(String(values,key),out result))throw new InvalidDataException("原始布尔参数无效。");return result;}
        static double Parse(string text){double v;if(!Double.TryParse(text,NumberStyles.Float,Inv,out v)||!Finite(v))throw new InvalidDataException("记录数值无效。");return v;}
        static int Int(double v){if(!Finite(v)||v<0||v>Int32.MaxValue||v!=Math.Truncate(v))throw new InvalidDataException("记录整数无效。");return (int)v;}
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
    }
}
