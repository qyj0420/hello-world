using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // Explicit read-only inspection only. Do not add full MAP matrices to
        // the normal pressure batch cache or write another copy to the project.
        public async Task<bool> ShowPressureEndpointReviewAsync(string path,Func<bool> stillCurrent)
        {return await PreparePressureEndpointReviewAsync(path,stillCurrent)&&ShowEndpointIdentificationDialog();}
        async Task<bool> PreparePressureEndpointReviewAsync(string path,Func<bool> stillCurrent)
        {
            if(InvokeRequired)throw new InvalidOperationException("Start endpoint review on the UI thread.");
            if(stillCurrent==null||!stillCurrent())return false;
            if(SourceRowCount!=0)throw new InvalidOperationException("请在独立只读窗口中进行本轮近远端识别。");
            string full=Path.GetFullPath(path);var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
            int index=Array.FindIndex(paths,p=>String.Equals(Path.GetFullPath(p),full,StringComparison.OrdinalIgnoreCase));
            if(index<0||!CanAnalyzeRecord(full,catalog[index,2]))throw new InvalidDataException("本轮定位记录不可用于近远端识别。");
            var request=CreateFileAnalysisRequest(full,(long)catalog[index,1],true,(int)catalog[index,0]);
            var integrityRequest=CreateFileAnalysisRequest(full,(long)catalog[index,1],true,(int)catalog[index,0]);
            PrepareAHistoryBatch(new List<ReviewAnalysisRequest>{request});
            var result=await Task.Run(()=>{
                string stamp=PressureAnalysisCache.Key("endpoint-inspection",integrityRequest);
                if(stamp==null)throw new InvalidDataException("无法核验近远端识别的数据或算法来源。");
                var extracted=ReviewAnalysisEngine.Analyze(request);
                if(stamp!=PressureAnalysisCache.Key("endpoint-inspection",integrityRequest))throw new InvalidDataException("识别期间项目或定位数据发生变化，请重新读取。");
                return extracted;
            });
            if(IsDisposed||!stillCurrent())return false;
            if(!result.Success)throw new InvalidDataException(result.Status);
            if(result.Parameters.GetLength(0)==0){AnalysisStatus=L("No extracted MAPs for endpoint review.","本轮未提取到可供近远端识别的 MAP。");return false;}
            double[,] parameters,waves;MergeAnalysisResults(new[]{result},out parameters,out waves);
            if(!LoadDataMatrices(parameters,waves))throw new InvalidDataException(LastError);
            if(!ShowLocationOverview(full))throw new InvalidDataException(LastError);
            if(!stillCurrent())return false;
            return true;
        }
    }
}
