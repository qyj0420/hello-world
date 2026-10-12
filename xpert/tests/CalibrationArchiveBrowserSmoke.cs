using System;
using System.IO;
using System.Linq;
using System.Text;
using HVX.Controls;
using HVX.CalibrationStorage;
using HVX.SignalProcessing;
using HVX.ReportGenerator;

internal static class CalibrationArchiveBrowserSmoke
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static void Reject(Action action,string name){bool failed=false;try{action();}catch(IOException){failed=true;}catch(InvalidDataException){failed=true;}Check(failed,name);}
    static int Main(string[] args)
    {
        try {
            string root=Path.Combine(Path.GetFullPath(args[0]),"browser-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"电缆长度: = \"180\"\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t末终端\t终端\t180m\r\n",Encoding.UTF8);
            var raw=Enumerable.Range(0,3).Select(i=>{string name="offline-evidence-"+i+".bin";File.WriteAllBytes(Path.Combine(root,name),new byte[]{1,2,(byte)i});return new CalibrationRawReference{RelativePath=name,Sha256=LiveCalibrationRecord.HashFile(Path.Combine(root,name))};}).ToArray();
            var r=new LiveCalibrationRecord{Phase="A",Level="100pC",DeviceSerial="OFFLINE-FIXTURE",DeviceModel="synthetic",FilterId=PdLinearHighPass.FilterId,
                ProjectHash=LiveCalibrationRecord.Identity(root),LengthM=180,VelocityMPerUs=180,SampleRateHz=100e6,IncidentSeconds=100/100e6,ReflectionSeconds=300/100e6,
                CoefficientPcPerV=100/.15,ChargePc=100,AttenuationDb=8,SnrDb=40,Correlation=.99,RangeIndex=5,PulseCount=3,IncidentPeakIndex=100,ReflectionPeakIndex=300,
                Average=Enumerable.Range(0,1000).Select(i=>.15*Math.Exp(-Math.Pow((i-100)/4d,2))+.06*Math.Exp(-Math.Pow((i-300)/4d,2))).ToArray(),RawFiles=raw,CreatedUtc=DateTime.UtcNow.ToString("o")};
            string path=r.Save(root);var project=BrowserReader.LoadProject(root);
            Check(project.Calibrations.Count==1&&project.Calibrations[0].IsValid,"Formal calibration appears in project browser");
            var wave=BrowserReader.LoadWaveform(project.Calibrations[0]);
            Check(wave.Y.SequenceEqual(r.Average)&&wave.SampleRateHz==r.SampleRateHz,"Selected formal calibration reloads waveform with project context");
            Check(Math.Abs(wave.ReflectionSeconds-r.ReflectionSeconds)<1e-15&&wave.CableLengthMetres==180,"Cursor timing and geometry retained");
            var before=File.ReadAllBytes(path);BrowserReader.LoadWaveform(project.Calibrations[0]);Check(before.SequenceEqual(File.ReadAllBytes(path)),"Read/comparison does not modify formal archive");
            File.AppendAllText(Path.Combine(root,raw[0].RelativePath),"changed");Reject(()=>BrowserReader.LoadWaveform(project.Calibrations[0]),"Formal browser verifies evidence on selected waveform load");
            string archive=Path.Combine(root,"Analysis","sample");Directory.CreateDirectory(archive);File.WriteAllText(Path.Combine(archive,"analysis.json"),"{}");
            Reject(()=>AnalysisRecovery.Move(root,root,new[]{root},null),"Project root cannot be deleted");
            Reject(()=>AnalysisRecovery.Move(root,archive,new[]{archive},archive),"Loaded analysis cannot be deleted");
            using(var guard=new FileStream(Path.Combine(root,"Analysis",".save.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Reject(()=>AnalysisRecovery.Move(root,archive,new[]{archive},null),"Analysis save lock prevents concurrent delete");
            string recovered=AnalysisRecovery.Move(root,archive,new[]{archive},null);
            Check(!Directory.Exists(archive)&&File.Exists(Path.Combine(recovered,"analysis.json")),"Delete is recoverable move, not data destruction");
            Check(new DirectoryInfo(Path.GetDirectoryName(recovered)).Name.StartsWith(".deleted_"),"Recovery folder omitted by archive enumerators");
            Console.WriteLine("CALIBRATION_ARCHIVE_BROWSER_PASS="+checks+"; fixtures="+root);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
