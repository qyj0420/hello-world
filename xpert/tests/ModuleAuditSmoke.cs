using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ModuleAuditSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static OperationAuditQueryResult Read(OperationAuditLog log, AuditLogKind kind)
    { Check(log.Flush(TimeSpan.FromSeconds(2)), "audit queue drained within QA budget"); return log.Query(kind); }
    sealed class RequestArgs:EventArgs{}
    sealed class FakeModule:UserControl
    {
        public string ProjectPath {get;set;}
        public string LastSavedProjectPath {get;set;}
        public string LastSavedProjectOperationCode {get;set;}
        public long ProjectSavedSequence {get;set;}
        public bool SimulationMode {get;set;}
        public bool IsCalibrationSavedValid {get;set;}
        public string OutputFormat {get;set;}
        public string LastGeneratedDocxPath {get;set;}
        public string LastGeneratedPdfPath {get;set;}
        public string DisplayRecommendationJson {get;set;}
        public event EventHandler DisplayRecommendationChanged;
        public void Display(){if(DisplayRecommendationChanged!=null)DisplayRecommendationChanged(this,EventArgs.Empty);}
        public event EventHandler ProjectSaved,PressureRequested,AnalysisArchiveChanged,AnalysisSaved,AnalysisSettingsChanged,ReportGenerated,CalibrationSaved;
        public event EventHandler<RequestArgs> StartRequested;
        public void Save(){ProjectSavedSequence++;if(ProjectSaved!=null)ProjectSaved(this,EventArgs.Empty);}
        public void Pressure(){if(PressureRequested!=null)PressureRequested(this,EventArgs.Empty);}
        public void Calibration(){if(StartRequested!=null)StartRequested(this,new RequestArgs());}
        public void SaveCalibration(){if(CalibrationSaved!=null)CalibrationSaved(this,EventArgs.Empty);}
        public void Archive(){if(AnalysisArchiveChanged!=null)AnalysisArchiveChanged(this,EventArgs.Empty);}
        public void SaveAnalysis(){if(AnalysisSaved!=null)AnalysisSaved(this,EventArgs.Empty);}
        public void Settings(){if(AnalysisSettingsChanged!=null)AnalysisSettingsChanged(this,EventArgs.Empty);}
        public void Report(){if(ReportGenerated!=null)ReportGenerated(this,EventArgs.Empty);}
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string qa=Path.Combine(Path.GetFullPath(args.Length>1?args[1]:args[0]),"module-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var log=new OperationAuditLog(Path.Combine(qa,"Logs"));
            string modules=Path.Combine(Path.GetFullPath(args[0]),"modules");
            if(File.Exists(Path.Combine(modules,"PressureTest","Xiongxin.OWTS.PressureTest.dll")))
            {
                using(var integration=new ModuleHost(new object(),new ApplicationState(),new Diagnostics(qa),modules))
                {
                    Control loaded;string error;Check(integration.TryCreateControl(ModuleHost.PressureTest,out loaded,out error),"production module construction: "+error);
                    using(loaded)
                    {
                        var connected=typeof(ModuleHost).GetField("auditConnectedControls",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(integration);
                        Check((bool)connected.GetType().GetMethod("Contains").Invoke(connected,new object[]{loaded}),"production DLL path connects audit observer");
                    }
                }
            }
            // Logical project identity only; no file is opened at this path.
            // The runner's "password-free" evidence directory is deliberately
            // redacted by audit privacy rules and must not be the identity fixture.
            using(var pressure=new FakeModule{ProjectPath=Path.Combine(Path.GetPathRoot(qa),"HVX-Audit-Fixture","Project")})
            using(var calibration=new FakeModule{ProjectPath=pressure.ProjectPath})
            using(var creation=new FakeModule{ProjectPath=pressure.ProjectPath,LastSavedProjectPath=pressure.ProjectPath})
            using(var analysis=new FakeModule{ProjectPath=pressure.ProjectPath})
            using(var report=new FakeModule{ProjectPath=pressure.ProjectPath})
            {
                var host=new ModuleHost(new object(),new ApplicationState(),new Diagnostics(qa),qa){AuditLog=log};
                host.ConnectModuleAudit(ModuleHost.PressureTest,pressure);host.ConnectModuleAudit(ModuleHost.PressureTest,pressure);
                pressure.Pressure();Check(!Directory.Exists(log.DirectoryPath),"audit disabled by default and no real log IO");
                host.AuditEnabled=true;pressure.Pressure();
                var entries=Read(log,AuditLogKind.Operation).Entries;
                Check(entries.Count==1&&entries[0].Action=="PressureRequested"&&entries[0].Outcome==AuditOutcome.Requested&&entries[0].Module==AuditModule.PressureTest,"request is typed, not success; duplicate connect ignored");
                host.SetAuditSimulationMode(ModuleHost.PressureTest,true);pressure.Pressure();
                Check(Read(log,AuditLogKind.Operation).Entries.Count(x=>x.Outcome==AuditOutcome.Simulation)==1,"explicit simulation marked");
                host.ConnectModuleAudit(ModuleHost.CalibrationTest,calibration);host.SetAuditSimulationMode(ModuleHost.CalibrationTest,true);calibration.Calibration();
                Check(Read(log,AuditLogKind.Operation).Entries.Any(x=>x.Action=="CalibrationRequested"&&x.Outcome==AuditOutcome.Simulation),"typed calibration event safely attached");
                calibration.SaveCalibration();
                Check(!Read(log,AuditLogKind.Operation).Entries.Any(x=>x.Action=="CalibrationSaved"),"invalid saved file does not create calibration save success");
                calibration.IsCalibrationSavedValid=true;calibration.SimulationMode=true;calibration.SaveCalibration();
                Check(Read(log,AuditLogKind.Operation).Entries.Any(x=>x.Action=="CalibrationSaved"&&x.Outcome==AuditOutcome.Simulation),"verified simulated calibration save is not a live success claim");
                host.ConnectModuleAudit(ModuleHost.ProjectCreation,creation);
                host.AuditProjectSave(creation);Check(Read(log,AuditLogKind.Project).Entries.Count==0,"load/zero sequence does not create project audit");
                creation.LastSavedProjectOperationCode="CREATE";creation.Save();host.AuditProjectSave(creation);
                Check(Read(log,AuditLogKind.Project).Entries.Count==1,"event and poll deduplicated");
                creation.LastSavedProjectOperationCode="UPDATE";creation.Save();
                Check(Read(log,AuditLogKind.Project).Entries.Count(x=>x.Action=="ProjectUpdated")==1,"update distinguished from create");
                creation.LastSavedProjectOperationCode="";creation.Save();
                Check(Read(log,AuditLogKind.Project).Entries.Count==2,"unknown old module provenance not guessed");
                creation.LastSavedProjectOperationCode="JOINT_UPDATE";creation.Save();host.AuditProjectSave(creation);
                Check(Read(log,AuditLogKind.Project).Entries.Count(x=>x.Action=="ProjectUpdated")==2,"joint edit remains an audited update, event and poll deduplicated");
                host.ConnectModuleAudit(ModuleHost.DataAnalysis,analysis);int before=Read(log,AuditLogKind.Operation).Entries.Count;analysis.Archive();
                Check(Read(log,AuditLogKind.Operation).Entries.Count==before,"analysis load/change not save");
                analysis.SaveAnalysis();analysis.Settings();
                Check(Read(log,AuditLogKind.Operation).Entries.Any(x=>x.Action=="AnalysisSaved"&&x.Outcome==AuditOutcome.Succeeded),"save success is dedicated event");
                Check(Read(log,AuditLogKind.Operation).Entries.Any(x=>x.Action=="AnalysisSettingsConfirmed"&&x.Outcome==AuditOutcome.Requested),"settings confirmation not presumed persisted");
                analysis.DisplayRecommendationJson="{\"Automatic\":true,\"AppliedFilter\":true,\"AppliedClassification\":false,\"Advice\":{\"Error\":\"do-not-log-private-detail\"}}";
                analysis.Display();analysis.Display();
                var choices=Read(log,AuditLogKind.Operation).Entries;
                Check(choices.Count(x=>x.Action=="DisplayAutomatic")==1&&choices.Any(x=>x.Action=="StatisticsFilterOn")&&choices.Any(x=>x.Action=="ClassificationOff"),"deduplicated display settings retained as whitelisted events");
                Check(choices.Where(x=>x.Action=="DisplayAutomatic").All(x=>x.ProjectPath==analysis.ProjectPath&&x.Module==AuditModule.DataAnalysis),"display events associated with correct project");
                Check(!Directory.GetFiles(log.DirectoryPath,"*.jsonl").Any(p=>File.ReadAllText(p).Contains("do-not-log-private-detail")),"arbitrary recommendation details never copied to audit");
                host.ConnectModuleAudit(ModuleHost.ReportGenerator,report);
                report.OutputFormat="PdfAndWord";report.LastGeneratedDocxPath=Path.Combine(qa,"fixture.docx");File.WriteAllBytes(report.LastGeneratedDocxPath,new byte[]{1});report.Report();
                Check(Read(log,AuditLogKind.Operation).Entries.Any(x=>x.Action=="ReportGenerated"&&x.Outcome==AuditOutcome.Partial),"Word only is partial when both requested");
                report.LastGeneratedPdfPath=Path.Combine(qa,"fixture.pdf");File.WriteAllBytes(report.LastGeneratedPdfPath,new byte[]{2});report.Report();
                Check(ModuleAuditSubscription.ReportOutcome(report)==AuditOutcome.Succeeded,"both required outputs exist");
                report.OutputFormat="PdfOnly";report.LastGeneratedPdfPath="";Check(ModuleAuditSubscription.ReportOutcome(report)==AuditOutcome.Failed,"recovery Word does not satisfy PDF-only");
                report.OutputFormat="Unspecified";Check(ModuleAuditSubscription.ReportOutcome(report)==AuditOutcome.Unknown,"unknown format not assumed success");
                using(var withstand=new WithstandTestControl())
                {
                    host.ConnectModuleAudit(ModuleHost.WithstandTest,withstand);withstand.ConfigureCapabilities(true,false);withstand.SetContext(pressure.ProjectPath,"A",10,60,30);
                    Check(withstand.RequestStart(),"isolated withstand starts demonstration");withstand.RequestStop();withstand.RequestStop();
                    var withstandEvents=Read(log,AuditLogKind.Operation).Entries.Where(x=>x.Module==AuditModule.WithstandTest).ToArray();
                    Check(withstandEvents.Length==2&&withstandEvents.All(x=>x.Outcome==AuditOutcome.Simulation),"only explicit start and active stop logged as simulation");
                    Check(withstandEvents.Any(x=>x.Action=="WithstandRequested")&&withstandEvents.Any(x=>x.Action=="WithstandStopped"),"withstand event semantics retained");
                }
                host.AuditEnabled=false;before=Read(log,AuditLogKind.Operation).Entries.Count;pressure.Pressure();Check(Read(log,AuditLogKind.Operation).Entries.Count==before,"verification disables hooks");
                host.AuditEnabled=true;host.Dispose();pressure.Pressure();analysis.SaveAnalysis();calibration.Calibration();Check(Read(log,AuditLogKind.Operation).Entries.Count==before,"dispose disconnects semantic subscriptions");
            }
            Console.WriteLine("MODULE_AUDIT_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
