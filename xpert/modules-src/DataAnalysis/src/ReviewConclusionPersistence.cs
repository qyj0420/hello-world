using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)]
        internal sealed class ArchiveConclusionRegion
        { public int PhaseMask {get;set;} public double PositionM {get;set;} public int ActionCode {get;set;} }
        [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)]
        internal sealed class ArchiveConclusion
        {
            public string Schema {get;set;} public string AnalysisId {get;set;} public string GeneratedUtc {get;set;}
            public string Api {get;set;} public string SummaryText {get;set;}
            public string ParametersSha256 {get;set;} public string SessionSha256 {get;set;}
            public ArchiveConclusionRegion[] Regions {get;set;}
            public ArchiveConclusionRegion[][] PhaseViews {get;set;}
            public int[] SourceRowIndices {get;set;}
            public double[][] Joints {get;set;}
            public double OperatingVoltageKv {get;set;}
        }
        ArchiveConclusion rememberedConclusion;
        string rememberedConclusionContext,rememberedConclusionParameters;
        double[,] rememberedConclusionSource;
        LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry rememberedConclusionGeometry;
        static bool IsEvidenceV3(ArchiveConclusion c) { return c!=null&&(c.Api??"").StartsWith("hvx-conclusion-dotnet-3.",StringComparison.Ordinal); }
        static ArchiveConclusionRegion[] ConclusionRegions(double[,] matrix)
        {
            return Enumerable.Range(0,matrix.GetLength(0)).Select(r=>new ArchiveConclusionRegion{PhaseMask=(int)matrix[r,0],PositionM=matrix[r,1],ActionCode=(int)matrix[r,8]}).ToArray();
        }
        void RememberSavedConclusion(ArchiveConclusion snapshot)
        {
            rememberedConclusion=snapshot;rememberedConclusionContext=EvaluationContextKey();rememberedConclusionParameters=AnalysisParameterStamp();
            rememberedConclusionSource=working;rememberedConclusionGeometry=pdmap.GetReviewConnectorGeometry();
        }
        ArchiveConclusion CurrentConclusionSnapshot()
        {
            ArchiveConclusion value=null;var current=LatestEvaluationResult;
            if(current!=null)
            {
                var matrix=current.LocationMatrix;
                if(matrix!=null&&matrix.GetLength(0)>0&&matrix.GetLength(0)<=100000&&matrix.GetLength(1)>=9)
                {
                    var rows=new ArchiveConclusionRegion[matrix.GetLength(0)];
                    for(int r=0;r<rows.Length;r++)
                    {
                        double phase=matrix[r,0],position=matrix[r,1],code=matrix[r,8];
                        if(!Finite(phase)||phase<1||phase>7||phase!=Math.Truncate(phase)||!Finite(position)||position<0||!Finite(code)||code<0||code>3||code!=Math.Truncate(code))throw new InvalidDataException("Task15 conclusion contains invalid structured regions.");
                        rows[r]=new ArchiveConclusionRegion{PhaseMask=(int)phase,PositionM=position,ActionCode=(int)code};
                    }
                    value=new ArchiveConclusion{Schema="HVX.AnalysisConclusion.1",GeneratedUtc=current.CreatedUtc,Api=current.Api,SummaryText=current.SummaryText,Regions=rows,
                        PhaseViews=Enumerable.Range(1,7).Select(mask=>ConclusionRegions(current.GetVisibleLocationMatrix(mask))).ToArray(),
                        SourceRowIndices=current.SourceRowIndices,Joints=current.JointArrays,OperatingVoltageKv=current.OperatingVoltageKv};
                }
            }
            else if(rememberedConclusion!=null&&rememberedConclusionParameters==AnalysisParameterStamp()&&EvaluationContextMatches(rememberedConclusionContext,rememberedConclusionSource,rememberedConclusionGeometry))
                value=ArchiveJson().Deserialize<ArchiveConclusion>(ArchiveJson().Serialize(rememberedConclusion));
            return value;
        }
        ArchiveConclusion ConclusionForSave(string id,ArchiveFile[] files)
        {
            var value=CurrentConclusionSnapshot();if(value==null)return null;
            value.AnalysisId=id;value.ParametersSha256=files.Single(f=>f.Path=="parameters.bin").Sha256;value.SessionSha256=files.Single(f=>f.Path=="session.json").Sha256;
            return value;
        }
        static void ValidateSavedConclusion(ArchiveManifest manifest)
        {
            var c=manifest.Conclusion;if(c==null)return;DateTime generated,saved;
            if(c.Schema!="HVX.AnalysisConclusion.1"||c.AnalysisId!=manifest.Id||String.IsNullOrWhiteSpace(c.SummaryText)||
                !DateTime.TryParse(c.GeneratedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out generated)||
                (manifest.SavedUtc!=null&&DateTime.TryParse(manifest.SavedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out saved)&&generated.ToUniversalTime()>saved.ToUniversalTime())||c.Regions==null||c.Regions.Length==0||c.Regions.Length>100000||
                c.Regions.Any(r=>r==null||r.PhaseMask<1||r.PhaseMask>7||!Finite(r.PositionM)||r.PositionM<0||r.ActionCode<0||r.ActionCode>3)||
                !manifest.Files.Any(f=>f.Path=="parameters.bin"&&String.Equals(f.Sha256,c.ParametersSha256,StringComparison.OrdinalIgnoreCase))||
                !manifest.Files.Any(f=>f.Path=="session.json"&&String.Equals(f.Sha256,c.SessionSha256,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Saved conclusion does not match this analysis snapshot.");
            if(IsEvidenceV3(c)&&(c.PhaseViews==null||c.PhaseViews.Length!=7||c.PhaseViews.Where((v,i)=>v==null||v.Length>100000||v.Any(r=>r==null||r.PhaseMask<1||r.PhaseMask>7||(r.PhaseMask&(i+1))!=r.PhaseMask||!Finite(r.PositionM)||r.PositionM<0||r.ActionCode<1||r.ActionCode>3)).Any()||
                c.SourceRowIndices==null||c.SourceRowIndices.Length==0||c.SourceRowIndices.Length>200000||c.SourceRowIndices.Any(r=>r<0)||c.SourceRowIndices.Distinct().Count()!=c.SourceRowIndices.Length||
                c.Joints==null||c.Joints.Length!=3||c.Joints.Any(j=>j==null||j.Length>10000||j.Any(x=>!Finite(x)||x<0))||!Finite(c.OperatingVoltageKv)||c.OperatingVoltageKv<0))
                throw new InvalidDataException("Saved Evidence V3 conclusion context is invalid.");
        }
    }
}
