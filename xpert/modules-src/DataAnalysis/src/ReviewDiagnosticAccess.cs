using System;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool AuthorizeDiagnostics()
        {
            RecordOperation("DiagnosticAccess", "Opened");
            return !IsDisposed && !Disposing;
        }
        string CurrentDiagnosticContext()
        {
            return OperationContext()+Environment.NewLine+"ControlPath="+AnalysisControlAssemblyPath+Environment.NewLine+
                "AlgorithmPath="+ExpectedAnalysisAlgorithmPath+Environment.NewLine+
                "Incident="+pdmap.CurrentIncidentIndex+"; Reflection="+pdmap.CurrentReflectionIndex+"; dtNs="+pdmap.CurrentSampleIntervalNs+Environment.NewLine+
                "CorrelationPercent="+pdmap.CurrentCorrelationPercent+"; ConfidencePercent="+pdmap.CurrentConfidencePercent+Environment.NewLine+
                "ConfidenceStatus="+pdmap.ConfidenceStatusCode+Environment.NewLine+pdmap.ConfidenceDetails+Environment.NewLine+
                "OperationLog="+operationJournal.FilePath+Environment.NewLine+"LogError="+operationJournal.Error;
        }
    }
}
