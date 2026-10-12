using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        public string PressureAnalysisCacheScope { get; set; }
        public int PressureAnalysisCacheHits { get; private set; }
        ReviewAnalysisRequest CreateFileAnalysisRequest(string path,long id,bool automaticAcceptance,int phase=0)
        {
            var threshold=ThresholdFor(path).Copy();var filters=pdmap.FilterParameters;
            return new ReviewAnalysisRequest{EndpointPolicy=EndpointPolicy,ProjectPath=projectPath,LocationFilePath=path,LocationDataId=id,AlgorithmIndex=AnalysisAlgorithmIndex,AHistoryPhase=phase,AnalyzeZeroVoltageData=AnalyzeZeroVoltageData,
                MaxPulses=AnalysisMaximumPulseCount,MultipleReflectionSuppressionCount=AnalysisMultipleReflectionSuppressionCount,
                AutomaticAcceptance=automaticAcceptance,ManualThresholdEnabled=threshold.Manual,ManualThreshold=threshold.Value,UseFirstArrivalTiming=!ReviewEAnalysis.IsMethod(AnalysisAlgorithmIndex)&&AnalysisUseFirstArrivalTiming,
                AutomaticThresholdMultiplier=AnalysisAutomaticThresholdMultiplier,AttenuationTolerancePercent=AnalysisAttenuationTolerancePercent,CorrelationErrorPercent=AnalysisCorrelationErrorPercent,
                NoiseSimilarityTolerancePercent=NoiseSimilarityTolerancePercent,ConnectingCableLengthM=capacitorProject?0:ConnectingCableLengthM,
                LocationSuspiciousMinimumPercent=LocationSuspiciousMinimumPercent,LocationManualAcceptanceMinimumPercent=LocationManualAcceptanceMinimumPercent,LocationAutomaticAcceptanceMinimumPercent=LocationAutomaticAcceptanceMinimumPercent,
                PhaseQuadrants=AnalysisPhaseFilterEnabled?AnalysisQuadrants:new[]{true,true,true,true},ApplyLinearFilter=!ReviewEAnalysis.IsMethod(AnalysisAlgorithmIndex)&&WaveformFilterMode==1,LinearFilterCutoffsHz=new[]{filters[1],filters[2]}};
        }
        // Captures the same per-file options and catalog IDs as the interactive
        // analysis page. Reuse only identical per-file inputs/options within a
        // trial; changed settings and history-assisted requests still recompute.
        public Task<double[,]> AnalyzeSavedPressureFilesAsync(string[] files)
        { return AnalyzeSavedPressureFilesWithProgressAsync(files,null); }
        public Task<double[,]> AnalyzeSavedPressureFilesWithProgressAsync(string[] files,Action<int,int> progress)
        {
            if(files==null||files.Length==0)throw new ArgumentException("没有已保存的有效轮次。");
            if(InvokeRequired)throw new InvalidOperationException("Capture analysis settings on the UI thread.");
            var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
            var requests=new List<ReviewAnalysisRequest>();
            var selected=new SortedDictionary<int,string>();
            foreach(string path in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)){
                int index=Array.FindIndex(paths,p=>String.Equals(Path.GetFullPath(p),path,StringComparison.OrdinalIgnoreCase));
                if(index<0||!CanAnalyzeRecord(path,catalog[index,2]))throw new InvalidDataException("本轮定位文件未进入当前项目有效记录目录，或未允许分析零电压记录。");
                selected.Add(index,paths[index]);
            }
            foreach(var entry in selected)requests.Add(CreateFileAnalysisRequest(entry.Value,(long)catalog[entry.Key,1],true,(int)catalog[entry.Key,0]));
            PrepareAHistoryBatch(requests);
            string cacheScope=PressureAnalysisCacheScope;
            PressureAnalysisCacheHits=0;
            return Task.Run(()=>{
                var results=new List<double[,]>();int hits=0;
                if(progress!=null)progress(0,requests.Count);
                foreach(var request in requests){bool hit;results.Add(PressureAnalysisCache.Analyze(cacheScope,request,ReviewAnalysisEngine.Analyze,out hit));if(hit)hits++;
                    if(progress!=null)progress(results.Count,requests.Count);}
                PressureAnalysisCacheHits=hits;
                int count=results.Sum(r=>r.GetLength(0)),columns=Math.Max(36,results.Max(r=>r.GetLength(1)));
                var matrix=new double[count,columns];int offset=0;
                foreach(var result in results){for(int r=0;r<result.GetLength(0);r++)for(int c=0;c<columns;c++)matrix[offset+r,c]=c<result.GetLength(1)?result[r,c]:double.NaN;offset+=result.GetLength(0);}
                for(int r=0;r<count;r++)matrix[r,9]=r;
                return matrix;
            });
        }
    }
}
