using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using HVX.Controls;

internal static class CalibrationFormatRoutingSmoke
{
    static int checks;
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static bool Rejects(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(IOException){return true;}}
    [STAThread]static int Main(string[] args)
    {
        try {
            string root=args[0];Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型: = \"一体三芯电缆\"\r\n电缆长度: = \"170\"\r\nALL相采用波速: = \"170\"\r\n");
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n");
            var wave=new double[512];wave[20]=1;wave[220]=.5;
            string hash=Convert.ToBase64String(new byte[32]);
            var record=new LiveCalibrationRecord{Version=3,Phase="ALL",Level="5nC",DeviceSerial="OFFLINE-SOURCE",DeviceModel="Fixture",
                FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,ProjectHash=LiveCalibrationRecord.Identity(root),CreatedUtc=DateTime.UtcNow.ToString("o"),
                LengthM=170,VelocityMPerUs=170,SampleRateHz=100e6,IncidentSeconds=20/100e6,ReflectionSeconds=220/100e6,
                CoefficientPcPerV=5000,ChargePc=5000,SnrDb=40,Correlation=.99,RangeIndex=5,PulseCount=3,
                IncidentPeakIndex=20,ReflectionPeakIndex=220,FirstArrival=true,Average=wave,CoefficientProcessing="HP10K_NO_LP",
                ReflectionStatus="Detected",VelocitySource="Measured",RawFiles=new[]{new CalibrationRawReference{RelativePath="not-required-raw.bin",Sha256=hash}}};
            string source=record.Save(root),originalHash=LiveCalibrationRecord.HashFile(source);
            Check(LiveCalibrationRecord.IsArchiveFile(source),"binary CAL format recognized");
            var candidate=LegacyCalibrationBinding.ReadCandidate(root,source);
            Check(candidate.SourceFormat=="FormalBinary"&&candidate.CoefficientPcPerV==5000,"binary candidate is parsed by formal reader, not text decoder");
            Check(candidate.HistoricalDeviceSerial=="OFFLINE-SOURCE"&&candidate.HistoricalTimingMode=="FirstArrival","original device declaration and timing retained");
            Check(LiveCalibrationRecord.BestForProject(root,"ALL",false,"OTHER")==null,"formal timing/device applicability is not bypassed");
            Check(Rejects(()=>LegacyCalibrationBinding.Confirm(root,candidate,"ALL","CURRENT-PICO","CURRENT-ART",false,false,"review")),"binary parameter reuse still needs explicit operator confirmation");
            var binding=LegacyCalibrationBinding.Confirm(root,candidate,"ALL","CURRENT-PICO","CURRENT-ART",false,true,"Explicitly reviewed applicability; original source is not a new acquisition.");
            var reload=LegacyCalibrationBinding.LoadAndValidate(root,binding.BindingPath,"CURRENT-PICO","CURRENT-ART",false);
            Check(reload.SourceFormat=="FormalBinary"&&reload.HistoricalDeviceSerial=="OFFLINE-SOURCE"&&reload.HistoricalTimingMode=="FirstArrival","confirmation roundtrip preserves original provenance");
            Check(reload.HistoricalDeviceIdentity=="Unknown"&&reload.QualityStatus=="NotReassessed","reuse does not manufacture historical verification");
            Check(LiveCalibrationRecord.HashFile(source)==originalHash,"original binary CAL untouched");
            var legacy=new StringBuilder("Date\t2026/10/11\r\nTime\t06:00\r\nOld path\tfixture\r\nCable length\t170\r\nV (m/usec)\t170\r\nCal.value\t5000\r\nWideband\t0\r\nStart_point\t0.0000002\r\nFinal_point\t0.0000022\r\nSR(N)\t100000000\r\nNI_Calib_factor\t5000\r\nFilterstate\t12\r\nCalib_Attenuation\t0\r\n");
            foreach(double sample in wave)legacy.AppendLine(sample.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            string legacyPath=Path.Combine(root,"B_5nC.cal");File.WriteAllText(legacyPath,legacy.ToString(),Encoding.GetEncoding(936));
            var textCandidate=LegacyCalibrationBinding.ReadCandidate(root,legacyPath);
            Check(textCandidate.SourceFormat=="LegacyText"&&textCandidate.HistoricalDeviceSerial==""&&textCandidate.HistoricalTimingMode=="Unspecified","legacy text remains readable without invented identity/timing");
            var textBinding=LegacyCalibrationBinding.Confirm(root,textCandidate,"B","CURRENT-PICO","CURRENT-ART",true,true,"Reviewed historical text coefficient.");
            Check(LegacyCalibrationBinding.LoadAndValidate(root,textBinding.BindingPath,"CURRENT-PICO","CURRENT-ART",true).CoefficientPcPerV==5000,"historical text confirmation roundtrip preserved");
            using(var page=new CalibrationProgramControl()) {
                var diagnostics=new List<string>();page.CalibrationEntryDiagnosticSink=diagnostics.Add;
                page.ProjectPath=root;
                Check(!page.HasCalibrationData&&page.LastCalibrationError.StartsWith("未找到适用于当前设置")&&page.LastCalibrationError.Length<100&&!page.LastCalibrationError.Contains("未改写原记录")&&!page.LastCalibrationError.Contains("ALL_5nC"),"customer rejection remains concise and actionable without internal preservation or filename details");
                Check(diagnostics.Any(line=>line.Contains("ALL_5nC.cal")&&line.Contains("计时方式")),"exact timing rejection and filename remain explicit in diagnostic sink");
                page.UseFirstArrivalTiming=true;
                Check(page.HasCalibrationData&&String.IsNullOrEmpty(page.LastCalibrationError),"late timing preference reloads matching formal record without manual file reopen");
                Check(page.LoadCalibrationFile()&&page.HasCalibrationData,"matching formal timing loads the original waveform");
                int notices=0;page.ShowCalibrationEntryNoticeForTests=text=>{notices++;Check((text.Contains("不会加压")||text.Contains("未确认"))&&!text.Contains("请按下急停"),"state reminder does not require LOW or claim HV permission");};
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(true);page.NotifyCalibrationPageEntered();
                Check(notices==1&&page.CalibrationEntryEmergencySignalHigh==true&&!page.HasActiveHardwareSession,"HIGH reminder uses actual observation with zero hardware creation");
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(false);page.NotifyCalibrationPageEntered();Check(notices==1,"LOW does not get the HIGH reminder");
                page.ReadCalibrationEntryEmergencyForTests=()=>Task.FromResult<bool?>(null);page.NotifyCalibrationPageEntered();Check(notices==2&&!page.CalibrationEntryEmergencySignalHigh.HasValue,"unknown is visibly not invented as HIGH or LOW");
            }
            record.Level="TDR";record.CoefficientPcPerV=Double.NaN;record.ChargePc=Double.NaN;record.VelocitySource="Configured";
            string tdr=record.Save(root);Check(Rejects(()=>LegacyCalibrationBinding.ReadCandidate(root,tdr)),"TDR never becomes a charge coefficient candidate");
            byte[] corrupt=File.ReadAllBytes(source);corrupt[corrupt.Length-1]^=1;string bad=Path.Combine(root,"A_5nC.cal");File.WriteAllBytes(bad,corrupt);
            Check(Rejects(()=>LegacyCalibrationBinding.ReadCandidate(root,bad)),"invalid formal binary does not fall back to text/legacy reuse");
            Console.WriteLine("CALIBRATION_FORMAT_ROUTING_PASS="+checks+"; PRODUCT_STARTED=0; SDK_CALLS=0; ORIGINAL_DATA_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
