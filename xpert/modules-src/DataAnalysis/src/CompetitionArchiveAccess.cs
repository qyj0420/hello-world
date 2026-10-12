using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HVX.PdReview
{
    // Reuse the same bounded/hash-verified archive contract without constructing
    // the editable review control or loading the large waveform matrix.
    public sealed partial class PdReviewControl
    {
        internal sealed class CompetitionDisplayData
        {
            internal double[,] Parameters,DisplayParameters;
        }
        internal static SavedAnalysisInfo[] CompetitionArchives(string project)
        {
            string root=ArchiveRoot(project);if(!Directory.Exists(root))return new SavedAnalysisInfo[0];
            var result=new List<Tuple<SavedAnalysisInfo,DateTime>>();
            foreach(string directory in Directory.GetDirectories(root))
            {
                string id=Path.GetFileName(directory);if(id.StartsWith(".",StringComparison.Ordinal))continue;
                try{ArchiveChild(root,id);var manifest=ReadArchiveManifest(directory,id,false);DateTime saved;
                    if(!DateTime.TryParse(manifest.SavedUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out saved))saved=DateTime.Parse(manifest.CreatedUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind);
                    result.Add(Tuple.Create(ArchiveInfo(manifest,directory),saved.ToUniversalTime()));}
                catch(Exception ex){if(IsFatalHostException(ex))throw;}
            }
            return result.OrderByDescending(x=>x.Item2).Select(x=>x.Item1).ToArray();
        }
        internal static double[,] CompetitionParameters(string project,string id,Func<bool> current)
        {return CompetitionLoadData(project,id,current,false).Parameters;}
        internal static CompetitionDisplayData CompetitionParametersForDisplay(string project,string id,Func<bool> current)
        {return CompetitionLoadData(project,id,current,true);}
        static CompetitionDisplayData CompetitionLoadData(string project,string id,Func<bool> current,bool prepareDisplay)
        {
            if(String.IsNullOrWhiteSpace(id)||Path.GetFileName(id)!=id||id.StartsWith(".",StringComparison.Ordinal))throw new ArgumentException("Invalid analysis identifier.");
            if(!current())throw new OperationCanceledException();
            string directory=ArchiveChild(ArchiveRoot(project),id);
            var manifest=ReadArchiveManifest(directory,id,true);
            if(!current())throw new OperationCanceledException();
            string path=ArchiveChild(directory,"parameters.bin");var matrix=ReadMatrix(path,10000000);
            if(matrix.GetLength(1)<36||matrix.GetLength(0)!=manifest.SourceRowCount)throw new InvalidDataException("Archived analysis matrix dimensions do not match the manifest.");
            if(!String.Equals(SessionHash(path),manifest.Files.Single(x=>x.Path=="parameters.bin").Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Analysis changed while loading; retry.");
            if(!current())throw new OperationCanceledException();
            var result=new CompetitionDisplayData{Parameters=matrix,DisplayParameters=matrix};
            if(prepareDisplay&&Enumerable.Range(0,matrix.GetLength(0)).Any(row=>matrix[row,26]==8||matrix[row,26]==9)) {
                string sessionPath=ArchiveChild(directory,"session.json"),wavePath=ArchiveChild(directory,"pdmap.bin");
                byte[] metadata=ReadLimited(sessionPath,ReviewAnalysisMetadataBudget.SessionMetadataBytes);
                var payload=ArchiveJson().DeserializeObject(ArchiveEncoding.GetString(metadata)) as Dictionary<string,object>;
                if(payload==null||!Object.Equals(payload["schema"],"HVX.AnalysisSession.Utf8")||!Object.Equals(payload["version"],1))throw new InvalidDataException("Invalid analysis session metadata.");
                string[,] files=SessionTable(payload["files"],11),settings=SessionTable(payload["settings"],2);
                var paths=Enumerable.Range(0,files.GetLength(0)).Select(file=>SessionResolve(SessionRoot(project),files[file,0])).ToArray();
                var catalog=new double[files.GetLength(0),2];for(int file=0;file<files.GetLength(0);file++){catalog[file,0]=SessionRead(files[file,1]);catalog[file,1]=SessionRead(files[file,2]);}
                string encoded=null;for(int row=0;row<settings.GetLength(0);row++)if(settings[row,0]=="AlgorithmDiagnostics")encoded=settings[row,1];
                var diagnostics=ParseAlgorithmDiagnostics(encoded,SessionRoot(project),paths,directory,null);
                var waves=ReadMatrix(wavePath,IntPtr.Size==4?8000000:32000000);
                foreach(string member in new[]{"session.json","pdmap.bin"})if(!String.Equals(SessionHash(ArchiveChild(directory,member)),manifest.Files.Single(file=>file.Path==member).Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Analysis changed while loading; retry.");
                if(!current())throw new OperationCanceledException();
                result.DisplayParameters=ReviewEAnalysis.CreateTimeFrequencyDisplay(matrix,waves,TimeFrequencyStatuses(SessionRoot(project),paths,catalog,diagnostics));
            }
            if(!current())throw new OperationCanceledException();return result;
        }
        internal static string[,] CompetitionLearningLabels {get{return (string[,])dischargeLabels.Clone();}}
        internal static void AddCompetitionLearning(string root,RecognitionSnapshot input,string keys,string manual,string operatorName,ReviewTask12Runtime runtime,Func<bool> current,object commitGate)
        {
            if(input.Rows.Length<3||input.Phases.Distinct().Count()!=1)throw new ArgumentException(input.English?"Select at least three samples of one physical phase.":"请仅显示一个物理相别，并至少选择三个样本。");
            var selection=ResolveLearningLabelSelection(keys,manual);
            string model=ReviewTask12Runtime.ResolveModelPath(input.ModelPath),sha=ReviewTask12Runtime.HashFile(model);
            ReviewLearningTransactions.Add(root,input,model,sha,selection.CanonicalLabelKeys,operatorName,
                selection.ManualDischargeType,selection.Task12Compatible,selection.TaxonomyStatus,selection.Task12Compatible?runtime:null,current,commitGate);
        }
    }
}
