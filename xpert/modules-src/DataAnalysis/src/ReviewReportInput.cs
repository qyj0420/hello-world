using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // Freeze only the final input, not an algorithm result. This context is
        // included inside the hashed session.json, alongside the full raw matrix.
        string CreateReportInputContext(double[,] parameters)
        {
            RefreshApproximationProjection();
            int[] conclusionRows, endpointRows; int accepted, invalid;
            var input=CreateEvaluationInput(out conclusionRows,out endpointRows,out accepted,out invalid);
            var geometry=pdmap.GetReviewConnectorGeometry();
            double[][] joints;
            try { joints=input.GetLength(0)==0?new[]{new double[0],new double[0],new double[0]}:EvaluationJoints(input,geometry); }
            catch(ArgumentException ex)
            {
                // Incompatible historic geometry blocks a new conclusion, not
                // preservation of the user's review work and original samples.
                return ArchiveJson().Serialize(new Dictionary<string,object>{{"Schema","HVX.ReportInput.1"},
                    {"MatrixSha256",EffectiveSelectionHash(parameters)},{"Available",false},{"UnavailableReason",ex.Message},
                    {"AcquisitionQualities",SerializeAcquisitionQualities()}});
            }
            string mask=CreateEffectiveSelectionMask();
            var rows=new List<int>();var raw=new List<double>();var projected=new List<double>();
            for(int r=0;r<parameters.GetLength(0);r++)
            {
                if(mask[r]!='1'||!Finite(parameters[r,13]))continue;
                double position=ProjectCanonicalAccessoryLocation(r,parameters[r,13]);
                if(!Finite(position))throw new InvalidOperationException("Final report position is invalid.");
                rows.Add(r);raw.Add(parameters[r,13]);projected.Add(position);
            }
            if(rows.Count>200000||conclusionRows.Length>200000)throw new InvalidOperationException("Report input exceeds the saved snapshot budget.");
            var context=new Dictionary<string,object>{
                {"Schema","HVX.ReportInput.1"},{"MatrixSha256",EffectiveSelectionHash(parameters)},{"Available",true},
                {"SnapEnabled",pdmap.ApproximateCablePositions&&!AiLearningMode},
                {"EndpointTolerancePercent",pdmap.PositionTolerancePercent},{"JointTolerancePercent",pdmap.JointApproximationErrorPercent},
                {"ProjectionRows",rows.ToArray()},{"RawPositionsM",raw.ToArray()},{"PositionsM",projected.ToArray()},
                {"ConclusionRows",conclusionRows},{"Joints",joints},{"OperatingVoltageKv",spectrum.OperatingVoltageKv},
                {"AcceptedRows",accepted},{"InvalidAcceptedRows",invalid},
                {"AcquisitionReferenceOnlyRows",Enumerable.Range(0,SourceRowCount).Where(IsAcquisitionReferenceOnly).ToArray()},
                {"AcquisitionQualities",SerializeAcquisitionQualities()},
                {"Scope","Saved effective selection; accepted valid rows only; no conclusion calculated during review"}};
            var catalog=analysisTree.GetDataCatalog();
            context["SelectedRecordCount"]=Enumerable.Range(0,catalog.GetLength(0)).Count(r=>catalog[r,5]==1);
            context["PendingSelectedRecordCount"]=Enumerable.Range(0,catalog.GetLength(0)).Count(r=>catalog[r,5]==1&&catalog[r,4]!=1);
            return ArchiveJson().Serialize(context);
        }
    }
}
