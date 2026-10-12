using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // Only semantic, whitelisted events: never observe arbitrary control changes,
    // passwords, native command payloads, raw waveforms or user conclusions.
    internal sealed partial class ModuleHost
    {
        internal bool AuditEnabled { get; set; }
        internal OperationAuditLog AuditLog { get; set; }
        readonly List<ModuleAuditSubscription> moduleAuditSubscriptions=new List<ModuleAuditSubscription>();
        readonly HashSet<Control> auditConnectedControls=new HashSet<Control>();
        readonly Dictionary<Control,long> auditedProjectSaves=new Dictionary<Control,long>();
        readonly Dictionary<string,bool> auditSimulationModes=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<Control,string> auditedDisplayChoices=new Dictionary<Control,string>();

        internal void SetAuditSimulationMode(string moduleId,bool simulation)
        { if(!String.IsNullOrEmpty(moduleId))auditSimulationModes[moduleId]=simulation; }

        internal void ConnectModuleAudit(string moduleId,Control control)
        {
            if(control==null||!auditConnectedControls.Add(control))return;
            if(moduleId==ProjectCreation)
            {
                auditedProjectSaves[control]=ReadInt64(control,"ProjectSavedSequence");
                SubscribeAudit(control,"ProjectSaved",()=>AuditProjectSave(control));
            }
            else if(moduleId==PressureTest)
                SubscribeAudit(control,"PressureRequested",()=>AuditRequest(moduleId,control,OperationAuditAction.PressureRequested));
            else if(moduleId==CalibrationTest)
            {
                SubscribeAudit(control,"StartRequested",()=>AuditRequest(moduleId,control,OperationAuditAction.CalibrationRequested));
                SubscribeAudit(control,"CalibrationSaved",()=>
                {
                    if(!LateBound.ReadBoolean(control,false,"IsCalibrationSavedValid"))return;
                    AuditOperation(OperationAuditAction.CalibrationSaved,
                        LateBound.ReadBoolean(control,false,"SimulationMode")?AuditOutcome.Simulation:AuditOutcome.Succeeded,
                        AuditProjectPath(moduleId,control),AuditModule.CalibrationTest);
                });
            }
            else if(moduleId==DataAnalysis)
            {
                SubscribeAudit(control,"AnalysisSettingsChanged",()=>AuditOperation(OperationAuditAction.AnalysisSettingsConfirmed,AuditOutcome.Requested,AuditProjectPath(moduleId,control),AuditModule.DataAnalysis));
                SubscribeAudit(control,"AnalysisSaved",()=>AuditOperation(OperationAuditAction.AnalysisSaved,AuditOutcome.Succeeded,AuditProjectPath(moduleId,control),AuditModule.DataAnalysis));
            }
            else if(moduleId==ReportGenerator)
                SubscribeAudit(control,"ReportGenerated",()=>AuditOperation(OperationAuditAction.ReportGenerated,ModuleAuditSubscription.ReportOutcome(control),AuditProjectPath(moduleId,control),AuditModule.ReportGenerator));
            else if(moduleId==WithstandTest)
            {
                SubscribeAudit(control,"StartRequested",()=>AuditOperation(OperationAuditAction.WithstandRequested,AuditOutcome.Simulation,AuditProjectPath(moduleId,control),AuditModule.WithstandTest));
                SubscribeAudit(control,"StopRequested",()=>AuditOperation(OperationAuditAction.WithstandStopped,AuditOutcome.Simulation,AuditProjectPath(moduleId,control),AuditModule.WithstandTest));
            }
            if(moduleId==DataAnalysis||moduleId==PressureTest)
                SubscribeAudit(control,"DisplayRecommendationChanged",()=>AuditDisplayChoices(moduleId,control));
        }
        void AuditDisplayChoices(string moduleId,Control control)
        {
            // Decode only these three booleans; never log arbitrary JSON, errors,
            // waveforms or model paths through the operation audit channel.
            string json=LateBound.ReadString(control,"DisplayRecommendationJson");if(json.Length>8192)return;
            var data=new JavaScriptSerializer{MaxJsonLength=8192,RecursionLimit=8}.DeserializeObject(json) as Dictionary<string,object>;
            object automatic,filter,classification;
            if(data==null||!data.TryGetValue("Automatic",out automatic)||!(automatic is bool)||
                !data.TryGetValue("AppliedFilter",out filter)||!(filter is bool)||
                !data.TryGetValue("AppliedClassification",out classification)||!(classification is bool))return;
            string project=AuditProjectPath(moduleId,control),prior;
            string key=project+"|"+automatic+"|"+filter+"|"+classification;
            if(auditedDisplayChoices.TryGetValue(control,out prior)&&prior==key)return;
            auditedDisplayChoices[control]=key;
            var module=moduleId==DataAnalysis?AuditModule.DataAnalysis:AuditModule.PressureTest;
            AuditOperation((bool)automatic?OperationAuditAction.DisplayAutomatic:OperationAuditAction.DisplayManual,AuditOutcome.Succeeded,project,module);
            AuditOperation((bool)filter?OperationAuditAction.StatisticsFilterOn:OperationAuditAction.StatisticsFilterOff,AuditOutcome.Succeeded,project,module);
            AuditOperation((bool)classification?OperationAuditAction.ClassificationOn:OperationAuditAction.ClassificationOff,AuditOutcome.Succeeded,project,module);
        }
        void SubscribeAudit(Control control,string eventName,Action action)
        {
            var subscription=ModuleAuditSubscription.TryCreate(control,eventName,()=>
            { if(disposed||!AuditEnabled||control.IsDisposed)return;try{action();}catch{} });
            if(subscription!=null)moduleAuditSubscriptions.Add(subscription);
        }
        void AuditOperation(OperationAuditAction action,AuditOutcome outcome,string projectPath,AuditModule module)
        { if(!disposed&&AuditEnabled)try{(AuditLog??OperationAuditLog.Default).EnqueueOperation(action,outcome,projectPath,module);}catch{} }
        void AuditRequest(string moduleId,Control control,OperationAuditAction action)
        {
            bool simulation;
            if(!auditSimulationModes.TryGetValue(moduleId,out simulation))
                simulation=LateBound.ReadBoolean(control,false,"SimulationMode");
            AuditOperation(action,simulation?AuditOutcome.Simulation:AuditOutcome.Requested,AuditProjectPath(moduleId,control),moduleId==PressureTest?AuditModule.PressureTest:AuditModule.CalibrationTest);
        }
        string AuditProjectPath(string moduleId,Control control)
        {
            string path=LateBound.ReadString(control,"ProjectPath","CurrentProjectPath");
            if(String.IsNullOrWhiteSpace(path))appliedProjectPaths.TryGetValue(moduleId,out path);
            return path;
        }
        internal void AuditProjectSave(Control control)
        {
            if(disposed||!AuditEnabled||control==null||control.IsDisposed)return;
            try
            {
                long sequence=ReadInt64(control,"ProjectSavedSequence"),prior;
                if(sequence<=0||(auditedProjectSaves.TryGetValue(control,out prior)&&sequence<=prior))return;
                // Advance even for unsupported old modules. Missing provenance must
                // not be guessed from a load, an existing directory or current UI mode.
                auditedProjectSaves[control]=sequence;
                string operation=LateBound.ReadString(control,"LastSavedProjectOperationCode");
                string path=LateBound.ReadString(control,"LastSavedProjectPath");
                if(String.IsNullOrWhiteSpace(path)||(operation!="CREATE"&&operation!="UPDATE"&&operation!="JOINT_UPDATE"))return;
                (AuditLog??OperationAuditLog.Default).EnqueueProject(operation=="CREATE"?ProjectAuditAction.ProjectCreated:ProjectAuditAction.ProjectUpdated,AuditOutcome.Succeeded,path);
            }
            catch{}
        }
        void DisposeModuleAudit()
        {
            foreach(var subscription in moduleAuditSubscriptions)subscription.Dispose();
            moduleAuditSubscriptions.Clear();auditConnectedControls.Clear();auditedProjectSaves.Clear();auditSimulationModes.Clear();auditedDisplayChoices.Clear();
        }
    }

    internal sealed class ModuleAuditSubscription : IDisposable
    {
        object source;EventInfo sourceEvent;Delegate handler;
        internal static ModuleAuditSubscription TryCreate(object source,string eventName,Action callback)
        {
            try
            {
                var item=source.GetType().GetEvent(eventName,BindingFlags.Public|BindingFlags.Instance);
                if(item==null)return null;
                MethodInfo invoke=item.EventHandlerType.GetMethod("Invoke");var args=invoke.GetParameters();
                if(invoke.ReturnType!=typeof(void)||args.Length!=2||!typeof(EventArgs).IsAssignableFrom(args[1].ParameterType))return null;
                var parameters=new[]{Expression.Parameter(args[0].ParameterType,"sender"),Expression.Parameter(args[1].ParameterType,"args")};
                Delegate handler=Expression.Lambda(item.EventHandlerType,Expression.Invoke(Expression.Constant(callback)),parameters).Compile();
                item.AddEventHandler(source,handler);return new ModuleAuditSubscription{source=source,sourceEvent=item,handler=handler};
            }
            catch{return null;}
        }
        internal static AuditOutcome ReportOutcome(object control)
        {
            // ReportGenerated can also mean only the Word half of a dual-format
            // report exists. Inspect the requested formats, not a generic success flag.
            try
            {
                var property=control.GetType().GetProperty("OutputFormat");
                string format=property==null?"":Convert.ToString(property.GetValue(control,null));
                if(format!="PdfOnly"&&format!="WordOnly"&&format!="PdfAndWord")return AuditOutcome.Unknown;
                bool word=File.Exists(LateBound.ReadString(control,"LastGeneratedDocxPath"));
                bool pdf=File.Exists(LateBound.ReadString(control,"LastGeneratedPdfPath"));
                bool requireWord=format!="PdfOnly",requirePdf=format!="WordOnly";
                if((!requireWord||word)&&(!requirePdf||pdf))return AuditOutcome.Succeeded;
                return (requireWord&&word)||(requirePdf&&pdf)?AuditOutcome.Partial:AuditOutcome.Failed;
            }
            catch{return AuditOutcome.Unknown;}
        }
        public void Dispose()
        {
            if(source==null)return;try{sourceEvent.RemoveEventHandler(source,handler);}catch{}
            source=null;sourceEvent=null;handler=null;
        }
    }
}
