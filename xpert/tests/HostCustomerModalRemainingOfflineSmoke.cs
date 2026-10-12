using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xiongxin.OWTS.Desktop;

// Source-bound presentation checks plus actual Diagnostics logging only.
// No forms, product entry point, profiles, project data, workers or devices.
internal static class HostCustomerModalRemainingOfflineSmoke
{
    static int checks;
    static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    static string Source(string root, string name)
    { return File.ReadAllText(Path.Combine(root, "src", name)); }
    static void Contains(string source, string text, string reason)
    { Check(source.Contains(text), reason); }
    static void Presentation(string source, string name)
    {
        foreach (Match call in Regex.Matches(source, @"(?:MessageBox\.Show|DeviceConnectionNotice)\s*\([^;]+;", RegexOptions.Singleline))
        {
            Check(!Regex.IsMatch(call.Value, @"\b(?:error|exception|actual|managerError)\s*\.\s*(?:Message|ToString)"),
                name + " customer call excludes raw exceptions");
        }
        foreach (Match literal in Regex.Matches(source, "\"([^\"\\\\]|\\\\.)*\""))
        {
            string text = literal.Value;
            if (!(text.Contains("contact support") || text.Contains("请联系维护人员") ||
                  text.Contains("Device settings were saved, but") || text.Contains("The project guide could not be completed."))) continue;
            Check(text.Length < 220 && !text.Contains("System.") && !text.Contains("SHA256") && !text.Contains("\\\\"),
                name + " new customer message is compact and excludes internal payloads");
        }
    }
    static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args[0]);
            AppPaths.TestDiagnosticsDirectory=Path.GetFullPath(args[1]);
            Directory.CreateDirectory(AppPaths.TestDiagnosticsDirectory);
            string competition=Source(root,"CompetitionDefectHostForm.cs");
            string guide=Source(root,"MainForm.ProjectCreationGuide.cs");
            string devices=Source(root,"MainForm.DetectionDeviceSelection.cs");
            Presentation(competition,"CompetitionDefectHostForm");
            Presentation(guide,"ProjectCreationGuide");
            Presentation(devices,"DetectionDeviceSelection");
            Contains(competition,"Error(\"CompetitionDefectPreferencesSave\"", "settings failure retains a diagnostic boundary");
            Contains(competition,"Error(\"CompetitionDefectPreferencesRead\", preferencesWarning)", "preferences warning retained in diagnostics");
            Contains(competition,"Error(\"CompetitionDefect\", \"Recognition module could not load the selected project.\", actual)", "recognition failure preserves the actual exception");
            Check(!competition.Contains("+ actual.Message") && !competition.Contains("+ preferencesWarning;"), "ordinary recognition status/header excludes raw failure text");
            Contains(competition,"ClearRecognition(); appliedSignature = null;", "module failure still clears unusable recognition state");
            Contains(guide,"Error(\"ProjectCreationGuide\"", "guide failure preserves diagnostics");
            Contains(guide,"finally {projectGuideOpen=false;}", "guide still clears its busy state on failure");
            Contains(guide,"SaveProjectGuidePreference(AppPaths.SettingsFile,false);", "guide reminder persists only through the existing save boundary");
            Contains(devices,"log.Error(\"ManagedDeviceManagerOpen\"", "read-only device dialog failure remains logged");
            Contains(devices,"log.Error(\"DeviceManagerOpen\",detail,error)", "retained vendor manager failure keeps detailed evidence");
            Contains(devices,"log.Error(\"DetectionDeviceSelection\"", "discovery/selection failure remains logged");
            Contains(devices,"log.Error(\"DetectionDeviceSelectionUi\"", "post-commit interface failure remains logged");
            Contains(devices,"Device settings were saved, but the interface did not refresh.", "post-commit failure does not falsely claim settings were rolled back");
            Contains(devices,"catch(OperationCanceledException) { }", "explicit cancellation behavior remains separate");
            Contains(devices,"if(connectionAttempted && !committed && !IsDisposed)", "uncommitted attempted connection still records unknown selection state");
            Contains(devices,"hardwareManuallyDisconnected=true; state.SetHardware(new HardwareStatusSnapshot", "unverified selection still pauses automatic connection");
            Contains(devices,"StatusCode=\"SelectionUnverified\"", "identity failure remains unverified");
            Contains(devices,"committed=true; settings=next;", "successful configuration commit remains before interface refresh");
            Contains(devices,"if(!SelectionCanProceed()) throw", "existing operation-idle gate remains fail closed");
            Contains(devices,"if(!live.CanSelect(target,DateTime.UtcNow) || target.PersistentBound!=selected.PersistentBound)", "freshness and binding checks remain required");
            Contains(devices,"HardwareDeviceDiscovery.SavedSerialsMatch(basis,target,exact)", "exact device identity check remains required");
            Contains(devices,"if(!await TryDisconnectCurrentDevice()) return false;", "unconfirmed disconnect never proceeds to a new connection");
            Contains(devices,"if(connected) CompleteDeviceConnectionSetup();", "setup completion still requires successful connection");

            string diagnostic="D:\\internal\\private-source.dat; implementation detail retained only in diagnostics";
            var log=new Diagnostics(AppPaths.TestDiagnosticsDirectory);
            log.Error("SyntheticHostCustomerBoundary","A synthetic operation failed.",new TargetInvocationException(new IOException(diagnostic,new InvalidDataException("synthetic inner validation failure"))));
            string recorded=File.ReadAllText(log.CurrentLogPath);
            Check(recorded.Contains(diagnostic), "actual Diagnostics retains full synthetic detail");
            Check(recorded.Contains("synthetic inner validation failure") && recorded.Contains("System.IO.IOException"), "actual Diagnostics retains nested exception type and details");
            log.Error("SyntheticHostCustomerBoundary","diagnostic-only warning");
            Check(File.ReadAllText(log.CurrentLogPath).Contains("diagnostic-only warning"), "string diagnostic overload remains available");
            Console.WriteLine("HOST_CUSTOMER_MODAL_REMAINING_PASS="+checks+";SourceChecks=true;DiagnosticsRuntime=true;ProductEntry=false;WindowsShown=false;Worker=false;SDK=false;Hardware=false;ProfilesRead=false;UserProjectsWritten=false");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

namespace Xiongxin.OWTS.Desktop
{
    // Test-only path provider: actual Diagnostics is compiled unchanged, but
    // nothing can resolve the user's live profile or its settings.
    internal static class AppPaths
    {
        internal static string TestDiagnosticsDirectory;
        internal static string DiagnosticsDirectory { get { return TestDiagnosticsDirectory; } }
    }
}
