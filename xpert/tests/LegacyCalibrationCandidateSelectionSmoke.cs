using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HVX.CalibrationStorage;
using Xiongxin.OWTS.Desktop;

internal static class LegacyCalibrationCandidateSelectionSmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static string Geometry(string phase,double length)
    {return "0\t"+phase+"相\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t"+length.ToString("R",CultureInfo.InvariantCulture)+"m\r\n";}
    static string Project(string root,string name,bool unequal=false)
    {
        string p=Path.Combine(root,name);Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"[ITEM]\r\n电缆长度: = \"404.6\"\r\n电缆芯数: = \"三芯\"\r\n",Encoding.GetEncoding(936));
        File.WriteAllText(Path.Combine(p,"CABLE.in"),Geometry("A",404.6)+Geometry("B",unequal?405.6:404.6)+Geometry("C",404.6),Encoding.GetEncoding(936));
        return p;
    }
    static string WriteLegacy(string p,string phase="ALL",int charge=5000,double coefficient=1828.2095631312)
    {
        string path=Path.Combine(p,phase+"_"+(charge/1000).ToString(CultureInfo.InvariantCulture)+"nC.cal");
        var text=new StringBuilder("Date:\t2026/08/26\r\nTime:\t19：35\r\nFile-path:\t旧项目\\ALL_5nC.cal\r\nCable length (m):\t405\r\nV (m/usec):\t169.60\r\nCal.value (pC):\t"+charge+"\r\nWideband:\t0\r\nStart_point（s）:\t0.000000936000\r\nFinal_point（s）:\t0.000005712000\r\nSR（N）:\t 125000004\r\nNI_Calib_factor:\t"+coefficient.ToString("R",CultureInfo.InvariantCulture)+"\r\nFilterstate\t        12\r\nCalib_Attenuation:\t1080.590959\r\n");
        for(int i=0;i<1490;i++)text.AppendLine((i==117?1d:i==714?.5d:Math.Sin(i*.13)*.01).ToString("R",CultureInfo.InvariantCulture));
        File.WriteAllText(path,text.ToString(),Encoding.GetEncoding(936));return path;
    }
    static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            string p=Project(root,"gbk-original"),source=WriteLegacy(p),sourceHash=LiveCalibrationRecord.HashFile(source);
            var candidate=LegacyCalibrationBinding.FindCandidateForProject(p,"A");
            Check(candidate!=null&&candidate.SourceRelativePath=="ALL_5nC.cal","automatically chooses compatible old project parameters without a file-selection dialog");
            Check(candidate.CoefficientPcPerV==1828.2095631312&&candidate.ChargePc==5000,"full legacy coefficient and known injected charge survive");
            Check(candidate.HistoricalLengthM==405&&candidate.HistoricalVelocityMPerUs==169.60,"historical dimensions remain original, not relabelled as current length");
            Check(candidate.WaveformSampleCount==1490&&candidate.RecordedSampleRateHz==125000004&&candidate.SampleRateHz==125000000,"full-width legacy headers and exact historical sample-rate correction are recognised");
            Check(candidate.SourceFormat=="LegacyText"&&candidate.HistoricalTimingMode=="Unspecified"&&candidate.HistoricalDeviceSerial=="","unknown original timing and device remain unknown");
            Check(PressureCalibrationSelection.Find(p,"A",true,"SYNTHETIC-PICO","SYNTHETIC-ART")==null,"candidate selection never silently enables real acquisition");
            Check(!Directory.Exists(Path.Combine(p,"Calibration"))&&LiveCalibrationRecord.HashFile(source)==sourceHash,"choosing candidate creates no binding and never modifies original CAL");
            Check(LegacyCalibrationBinding.FindCandidateForProject(p,"ALL")!=null,"equal-length three-phase project accepts common candidate for an explicit decision");
            Check(LegacyCalibrationBinding.FindCandidateForProject(p,"INVALID")==null,"invalid phase has no candidate");
            var confirmed=LegacyCalibrationBinding.Confirm(p,candidate,"A","SYNTHETIC-PICO","SYNTHETIC-ART",true,true,"Synthetic operator confirmed that the earlier calibration applies to the current devices and wiring.");
            var selected=PressureCalibrationSelection.Find(p,"A",true,"SYNTHETIC-PICO","SYNTHETIC-ART");
            Check(selected!=null&&selected.IsLegacy&&selected.Source==confirmed.BindingPath,"one real explicit decision makes the automatically selected old source reusable");
            Check(confirmed.HistoricalDeviceIdentity=="Unknown"&&confirmed.HistoricalTimingMode=="Unspecified"&&confirmed.HistoricalLengthM==405&&confirmed.CurrentLengthM==404.6,"decision preserves unknown original identity/timing and distinct current length");
            Check(LiveCalibrationRecord.HashFile(source)==sourceHash,"saving a separate applicability decision preserves original old CAL bytes");
            Check(PressureCalibrationSelection.Find(p,"A",false,"SYNTHETIC-PICO","SYNTHETIC-ART")==null,"explicit current timing decision does not follow a later setting change");
            File.WriteAllText(Path.Combine(p,"broken.cal"),"malformed");
            Check(LegacyCalibrationBinding.FindCandidateForProject(p,"A").SourceRelativePath=="ALL_5nC.cal","unreadable unrelated old file cannot hide usable original");
            WriteLegacy(p,"ALL",10000);
            Check(LegacyCalibrationBinding.FindCandidateForProject(p,"A").ChargePc==10000,"highest valid charge wins within the same phase rank");
            WriteLegacy(p,"A",1000);
            Check(LegacyCalibrationBinding.FindCandidateForProject(p,"A").SourceRelativePath=="A_1nC.cal","specific current phase has priority over common candidate");
            string other=Project(root,"other-equal");WriteLegacy(other,"B");
            Check(LegacyCalibrationBinding.FindCandidateForProject(other,"A").SourcePhase=="B","equal-length phases may offer one shared source without rewriting its recorded phase");
            string unequal=Project(root,"unequal",true);WriteLegacy(unequal);
            Check(LegacyCalibrationBinding.FindCandidateForProject(unequal,"A")==null,"unequal phases cannot automatically offer a common source");
            Check(LegacyCalibrationBinding.FindCandidateForProject(unequal,"ALL")==null,"unequal project never offers a single common applicability decision");
            WriteLegacy(unequal,"A");
            Check(LegacyCalibrationBinding.FindCandidateForProject(unequal,"A").SourcePhase=="A","unequal project offers exact phase only");
            string stale=Project(root,"changed-source"),stalePath=WriteLegacy(stale);
            var staleCandidate=LegacyCalibrationBinding.FindCandidateForProject(stale,"A");File.AppendAllText(stalePath,"\r\n1");
            bool rejected=false;try{LegacyCalibrationBinding.Confirm(stale,staleCandidate,"A","SYNTHETIC-PICO","SYNTHETIC-ART",true,true,"Synthetic explicit decision.");}catch(Exception error)when(error is IOException||error is InvalidDataException){rejected=true;}
            Check(rejected&&!Directory.Exists(Path.Combine(stale,"Calibration")),"source changed after selection cannot be confirmed and writes nothing");
            string bounded=Project(root,"bounded");for(int i=0;i<257;i++)File.WriteAllText(Path.Combine(bounded,"invalid-"+i.ToString("D3")+".cal"),"malformed");
            rejected=false;try{LegacyCalibrationBinding.FindCandidateForProject(bounded,"A");}catch(InvalidDataException){rejected=true;}
            Check(rejected&&!Directory.Exists(Path.Combine(bounded,"Calibration")),"candidate enumeration is bounded to 256 files and never writes on overflow");
            if(args.Length>1) {
                string field=Path.GetFullPath(args[1]);
                string[] inputs=new[]{"ITEM.in","CABLE.in","ALL_5nC.cal"}.Select(name=>Path.Combine(field,name)).ToArray();
                string[] before=inputs.Select(LiveCalibrationRecord.HashFile).ToArray();
                var observed=LegacyCalibrationBinding.FindCandidateForProject(field,"A");
                Check(observed!=null&&observed.SourceFormat=="LegacyText","explicitly supplied field project is readable by the actual legacy parser");
                Check(inputs.Select(LiveCalibrationRecord.HashFile).SequenceEqual(before),"field project input bytes are unchanged");
                Console.WriteLine("FIELD_READ_ONLY; SOURCE="+Path.GetFileName(observed.SourceRelativePath)+"; COEFFICIENT="+observed.CoefficientPcPerV.ToString("R",CultureInfo.InvariantCulture)+"; CHARGE="+observed.ChargePc.ToString("R",CultureInfo.InvariantCulture)+"; SAMPLES="+observed.WaveformSampleCount+"; ORIGINAL_TIMING="+observed.HistoricalTimingMode+"; DEVICE_DECLARATION="+(!String.IsNullOrWhiteSpace(observed.HistoricalDeviceSerial)));
            }
            Console.WriteLine("LEGACY_CALIBRATION_CANDIDATE_PASS="+checks+"; PRODUCT_STARTED=0; WORKER_STARTED=0; SDK_CALLS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
