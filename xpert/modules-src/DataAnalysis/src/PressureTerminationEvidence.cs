using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HVX.SignalEvidence;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // Read-only safety-review export. Classification is supplied by the
        // same Task15 Evidence V3 engine; no new endpoint distance threshold.
        // Output: phase, component (2=start/3=end), physical position, pC,
        // cumulative source row. Only newly saved record rows are returned.
        public Task<double[,]> GetPressureTerminationEvidenceAsync(double[,] matrix,string savedRecord)
        {
            if(InvokeRequired)throw new InvalidOperationException("Read project geometry on the UI thread.");
            if(matrix==null||matrix.GetLength(1)<36)throw new ArgumentException("Invalid location matrix.");
            if(capacitorProject||matrix.GetLength(0)==0)return Task.FromResult(new double[0,5]);
            var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
            int file=Array.FindIndex(paths,p=>String.Equals(Path.GetFullPath(p),Path.GetFullPath(savedRecord),StringComparison.OrdinalIgnoreCase));
            if(file<0)throw new InvalidDataException("本轮记录尚未进入项目分析目录。");
            double fileId=catalog[file,1];
            var geometry=pdmap.GetReviewConnectorGeometry();double operatingVoltage=spectrum.OperatingVoltageKv;bool useEnglish=english;
            return Task.Run(()=>{
            var sourceRows=new List<int>();
            for(int r=0;r<matrix.GetLength(0);r++)
            {
                double phase=matrix[r,0],length=matrix[r,5],position=matrix[r,13];
                if(matrix[r,35]!=1||!Finite(phase)||phase<1||phase>3||phase!=Math.Truncate(phase)||
                    !Finite(matrix[r,1])||!Finite(matrix[r,2])||matrix[r,2]<=.05||!Finite(matrix[r,3])||
                    !Finite(matrix[r,15])||matrix[r,15]<=0||!Finite(matrix[r,23])||matrix[r,23]<0||
                    !Finite(matrix[r,24])||matrix[r,24]<0||!EvaluationPairIsValid(matrix,r)||
                    !BoundaryConfidenceMath.WithinTolerance(position,length))continue;
                sourceRows.Add(r);
            }
            if(sourceRows.Count==0)return new double[0,5];
            var input=new double[sourceRows.Count,matrix.GetLength(1)];
            for(int r=0;r<sourceRows.Count;r++){
                for(int c=0;c<input.GetLength(1);c++)input[r,c]=matrix[sourceRows[r],c];
                input[r,22]=Double.NaN; // amplitude ratio is not probability
            }
            var joints=EvaluationJoints(input,geometry);
            var result=HVX.Location.EmbeddedConclusions.Analyze(input,joints[0],joints[1],joints[2],operatingVoltage,useEnglish);
            if(result==null||!result.Ok)throw new InvalidDataException("端部分析证据暂不可用，后续轮次未继续。");
            var rows=new List<double[]>();
            for(int r=0;r<sourceRows.Count;r++){
                int originalRow=sourceRows[r];
                if(matrix[originalRow,1]!=fileId||matrix[originalRow,15]<=10000)continue;
                int decision=r*4;
                if(result.SourceDecisionMatrix[decision+1]!=1||result.SourceDecisionMatrix[decision+3]!=0)continue;
                double group=result.SourceDecisionMatrix[decision+2],phase=matrix[originalRow,0];
                for(int k=0;k<result.PhaseAssessmentRows;k++){
                    int at=k*18;var a=result.PhaseAssessmentMatrix;
                    if(a[at]!=group||a[at+1]!=phase||a[at+9]<=0||(a[at+12]!=2&&a[at+12]!=3))continue;
                    rows.Add(new[]{phase,a[at+12],matrix[originalRow,13],matrix[originalRow,15],(double)originalRow});break;
                }
            }
            var output=new double[rows.Count,5];
            for(int r=0;r<rows.Count;r++)for(int c=0;c<5;c++)output[r,c]=rows[r][c];
            return output;
            });
        }
    }
}
