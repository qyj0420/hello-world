// Task15 Evidence V3.0.1 - source-only 100/101 integration, 2026-10-07.
// Do not combine with old Task15 source classes in the same assembly.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System;

// SOURCE: AnalysisResult.cs
namespace HVX.Location
{
    /// <summary>
    /// LabVIEW-facing result. All matrices are row-major flattened arrays.
    /// Use Reshape Array in LabVIEW with the supplied Rows/Columns values.
    /// </summary>
    public sealed partial class AnalysisResult
    {
        public bool Ok { get; internal set; }
        public string Error { get; internal set; }
        public string ApiVersion { get; internal set; }
        public string ModelVersion { get; internal set; }
        public int TotalPulses { get; internal set; }
        public int ValidPulses { get; internal set; }
        public int InputRows { get; internal set; }
        public int AcceptedRows { get; internal set; }
        public int RejectedIndex35Rows { get; internal set; }
        public double OperatingPhaseVoltageKv { get; internal set; }
        public bool EnglishOutput { get; internal set; }
        public string OutputLanguage { get; internal set; }
        public string RecommendationBasis { get; internal set; }
        public int ConclusionAudienceType { get; internal set; }
        public string ConclusionAudience { get; internal set; }
        public int AlgorithmMode { get; internal set; }
        public string AlgorithmId { get; internal set; }
        public string OriginalSummaryText { get; internal set; }

        public double[] FilteredScatter { get; internal set; }
        public int FilteredScatterRows { get; internal set; }
        public int FilteredScatterColumns { get { return 3; } }

        public double[] CandidateMatrix { get; internal set; }
        public int CandidateRows { get; internal set; }
        public int CandidateColumns { get { return 15; } }

        public double[] LocationMatrix { get; internal set; }
        public int LocationRows { get; internal set; }
        public int LocationColumns { get { return 11; } }

        internal int[][][] LocationPointRows = new int[0][][];
        // Exact candidate-to-place membership for subset bounds.
        internal int[] CandidateLocationRows = new int[0];

        // One row per merged location: mask, center, minimum detected kV, U0,
        // minimum detected/U0, count at minimum detected voltage, maximum
        // per-phase application count, ascending Q-U support, at-U0 evidence flag.
        // Minimum detected voltage is NOT a verified PDIV; PDEV is unestablished.
        public double[] VoltageAssessmentMatrix { get; internal set; }
        public int VoltageAssessmentRows { get; internal set; }
        public int VoltageAssessmentColumns { get { return 9; } }

        public string[] StatusArray { get; internal set; }
        public string SummaryText { get; internal set; }

        // Company-side continuous-learning data. One row per internal candidate.
        internal double[] LearningFeatureMatrix { get; set; }
        internal int LearningFeatureRows { get; set; }
        internal int LearningFeatureColumns { get { return 16; } }
        internal string[] CandidateIds { get; set; }

        // Final multi-mode conclusion diagnostics. FactorMatrix and LocationMatrix
        // preserve the stable runtime's location-row order; none of these arrays
        // are severity-sorted.
        public double[] FactorMatrix { get; internal set; }
        public int FactorRows { get; internal set; }
        public int FactorColumns { get; internal set; }
        public string[] FactorColumnNames { get; internal set; }
        public double[] MarkerMatrix { get; internal set; }
        public int MarkerRows { get; internal set; }
        public int MarkerColumns { get { return 4; } }
        public string[] ActionColors { get; internal set; }
        public string[] DefectTypeArray { get; internal set; }
        public double[] DefectProbabilityArray { get; internal set; }
        public string[] DefectStatusArray { get; internal set; }
        public string[] DiagnosticArray { get; internal set; }

        // Legacy compatibility: Task17 column18 is an auxiliary index, not joules.
        // Waveform row alignment is validated, but this statistical engine does
        // not calculate waveform energy or use that auxiliary index for risk.
        public bool PulseEnergyUsed { get; internal set; }
        public int PulseEnergyRows { get; internal set; }
        public double[] PulseEnergyMatrix { get; internal set; }
        public int PulseEnergyColumns { get; internal set; }
        public string[] PulseEnergyColumnNames { get; internal set; }
        public string PulseEnergyStatus { get; internal set; }

        // Row map columns: input row, accepted row (-1 if rejected), PDmap ID,
        // index-35 acceptance state, phase code, pulse-energy row (-1 if unused).
        public double[] InputToAcceptedRowMap { get; internal set; }
        public int InputToAcceptedRowMapRows { get; internal set; }
        public int InputToAcceptedRowMapColumns { get { return 6; } }
        public string[] InputToAcceptedRowMapColumnNames { get; internal set; }

        internal AnalysisResult()
        {
            Ok = false;
            Error = string.Empty;
            ApiVersion = LocatorEngine.ApiVersion;
            ModelVersion = "none";
            OutputLanguage = "zh-CN";
            RecommendationBasis = "DL/T 1576-2016; DL/T 417-2019; Q/GDW 11838-2018 (配电电缆场景适用时)";
            ConclusionAudienceType = 1;
            ConclusionAudience = "professional-technical";
            AlgorithmMode = 0;
            AlgorithmId = "HVX-STABLE-15";
            OriginalSummaryText = string.Empty;
            FilteredScatter = new double[0];
            CandidateMatrix = new double[0];
            LocationMatrix = new double[0];
            VoltageAssessmentMatrix = new double[0];
            StatusArray = new string[0];
            SummaryText = string.Empty;
            LearningFeatureMatrix = new double[0];
            CandidateIds = new string[0];
            FactorMatrix = new double[0];
            FactorColumnNames = new string[0];
            MarkerMatrix = new double[0];
            ActionColors = new string[0];
            DefectTypeArray = new string[0];
            DefectProbabilityArray = new double[0];
            DefectStatusArray = new string[0];
            DiagnosticArray = new string[0];
            PulseEnergyMatrix = new double[0];
            PulseEnergyColumnNames = new string[0];
            PulseEnergyStatus = "DISABLED|legacy-api";
            InputToAcceptedRowMap = new double[0];
            InputToAcceptedRowMapColumnNames = new[] {
                "input_row_index", "accepted_row_index", "pdmap_id",
                "accepted_index35", "phase_code", "pulse_energy_row_index" };
        }
    }


}


// SOURCE: EmbeddedConclusions.cs
namespace HVX.Location {
 /// <summary>Source-only integration for task 100. No DLL discovery or Python.</summary>
 public static class EmbeddedConclusions {
  public static AnalysisResult Analyze(double[,] data,double[] jointsA,double[] jointsB,double[] jointsC,
   double operatingPhaseVoltageKv,bool englishOutput,int audience=0,int mode=0,
   double locationErrorPercent=1,double cursorStepM=.1) {
   return new LocatorEngine().AnalyzeWithStandardProfilePulseEnergyAndAlgorithmMode(data,null,
    jointsA,jointsB,jointsC,locationErrorPercent,cursorStepM,operatingPhaseVoltageKv,0,0,0,
    englishOutput,audience,mode,"");
  }
  // ReportGenerator's existing enum is 1=brief, 2=professional; core uses 0/1.
  // serviceYears is accepted for host compatibility but is not a risk feature.
  public static AnalysisResult AnalyzeForReport(double[,] data,double[] jointsA,double[] jointsB,double[] jointsC,
   double operatingPhaseVoltageKv,double serviceYears,bool englishOutput,int reportAudience) {
   if(reportAudience!=1&&reportAudience!=2)throw new ArgumentOutOfRangeException("reportAudience","Expected 1=brief or 2=professional.");
   return Analyze(data,jointsA,jointsB,jointsC,operatingPhaseVoltageKv,englishOutput,reportAudience-1);
  }
  /// <summary>Positive-action positions for the selected phases only. Does not
  /// re-score or promote hidden-phase evidence. Full-phase center is the original
  /// median; subset center is the pulse-count weighted phase-center estimate.</summary>
  public static double[,] GetVisibleLocationMatrix(AnalysisResult result,int visibleMask) {
   if(result==null||!result.Ok)throw new ArgumentException("A successful result is required.");
   if(visibleMask<1||visibleMask>7)throw new ArgumentOutOfRangeException("visibleMask");
   var output=new List<double[]>();
   for(int i=0;i<result.LocationRows;i++) {
    var phases=Enumerable.Range(0,result.PhaseAssessmentRows).Where(k=>
     result.PhaseAssessmentMatrix[k*18]==i&&result.PhaseAssessmentMatrix[k*18+9]>0&&
     (visibleMask&(1<<((int)result.PhaseAssessmentMatrix[k*18+1]-1)))!=0).ToList();
    if(phases.Count==0)continue;
    var row=new double[11];Array.Copy(result.LocationMatrix,i*11,row,0,11);
    int mask=phases.Sum(k=>1<<((int)result.PhaseAssessmentMatrix[k*18+1]-1));
    double count=phases.Sum(k=>result.PhaseAssessmentMatrix[k*18+2]);
    row[0]=mask;row[4]=count;row[5]=phases.Max(k=>result.PhaseAssessmentMatrix[k*18+6]);
    row[6]=phases.Sum(k=>result.PhaseAssessmentMatrix[k*18+7]*result.PhaseAssessmentMatrix[k*18+2])/count;
    row[7]=phases.Max(k=>result.PhaseAssessmentMatrix[k*18+8]);row[8]=phases.Max(k=>result.PhaseAssessmentMatrix[k*18+9]);
    row[9]=row[10]=phases.Count;
    if(mask!=(int)result.LocationMatrix[i*11]) {
     row[1]=phases.Sum(k=>result.PhaseAssessmentMatrix[k*18+4]*result.PhaseAssessmentMatrix[k*18+2])/count;
     var candidates=Enumerable.Range(0,result.CandidateRows).Where(k=>
      result.CandidateLocationRows[k]==i&&
      (mask&(1<<(int)result.CandidateMatrix[k*15]))!=0&&result.CandidateMatrix[k*15+8]>0).ToList();
     if(candidates.Count>0){row[2]=candidates.Min(k=>result.CandidateMatrix[k*15+2]);row[3]=candidates.Max(k=>result.CandidateMatrix[k*15+3]);}
    }output.Add(row);
   }
   var matrix=new double[output.Count,11];for(int r=0;r<output.Count;r++)for(int c=0;c<11;c++)matrix[r,c]=output[r][c];return matrix;
  }
 }
}


// SOURCE: EvidenceCore.cs
namespace HVX.Location {
 // Pure per-call statistical engine. Location/asset metadata never contributes
 // to evidence score. No field probability is claimed without calibration.
 internal static class EvidenceCore {
  internal static readonly string[] PhaseColumns = {"location_row","phase_code","pulse_count","application_count",
   "center_m","min_detected_kv","max_q_pc","mean_q_pc","evidence_score","action_code","background_q95_pc",
   "service_voltage_pulses","component_code","nearest_component_m","feature_coherence","ascending_qu_score",
   "regular_phase_spacing_hint","matching_confidence_mean"};
  static readonly string[] FactorColumns = {"phase_mask","center_m","low_m","high_m","pulse_count","application_groups",
   "max_q_pc","mean_q_pc","unused_charge_energy","min_detected_kv","min_detected_over_u0","ascending_qu_score",
   "unused_defect_score","count_support","unused_energy_score","unused_standard_score","service_evidence",
   "unused_jump_score","unused_energy_growth","unused_phase_broadening","column_shape","evidence_score",
   "evidence_score_copy","action_code","component_code","defect_probability","same_core_action","location_row_index",
   "task17_energy_present","task17_energy_sum","task17_energy_mean"};
  sealed class Pulse { internal int Row,Accepted,Phase; internal double X,Q,U,Angle,App,Map,Confidence,Width,Band,Energy; }
  sealed class Region {
   internal List<Pulse> P; internal int Phase,Apps,Action,Component,ServiceCount,Group;
   internal double X,Low,High,MaxQ,MeanQ,Score,Coherence,Trend,Noise,Joint,MinU,Match,MatchCoverage,Shape,Bandwidth,Enrichment;
   internal bool Floating,AmbiguousOrder,BoundaryPileup;
  }
  sealed class Place { internal List<Region> Regions=new List<Region>(); internal List<Pulse> P; internal int Mask,Action; internal double X,Low,High,Score; }
  static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
  static double Clamp(double x) { return Math.Max(0,Math.Min(1,x)); }
  static double Tol(double x,double percent,double step) { return Math.Max(step, Math.Abs(x)*percent/100); }
  static string F(double x,string format="0.0") { return x.ToString(format,CultureInfo.InvariantCulture); }
  static double Quantile(IEnumerable<double> source,double p) {
   var a=source.OrderBy(x=>x).ToArray(); if(a.Length==0)return 0;
   double k=(a.Length-1)*p; int i=(int)k; return a[i]+(a[Math.Min(i+1,a.Length-1)]-a[i])*(k-i);
  }
  static void Require(bool b,string message) { if(!b)throw new ArgumentException(message); }
  internal static AnalysisResult Run(double[,] data,double[,] waves,double[][] joints,double percent,double step,
   double u0,double years,int body,int jointType,bool en,int audience,int mode,string modelPath) {
   Require(Finite(percent)&&percent>=0&&percent<=10,"locationErrorPercent must be finite in [0,10].");
   Require(Finite(step)&&step>0&&step<=100,"cursorStepM must be finite in (0,100].");
   Require(Finite(u0)&&(u0==0||u0>=.001&&u0<=1000),"operatingPhaseVoltageKv must be 0 (unknown) or finite in [0.001,1000] kV.");
   Require(Finite(years)&&years>=0,"cableServiceYears must be finite and nonnegative (metadata only).");
   Require(body>=0&&jointType>=0,"Material profile codes must be nonnegative (metadata only).");
   Require(audience==0||audience==1,"conclusionAudienceType must be 0 or 1.");
   Require(mode>=0&&mode<=2,"algorithmMode must be 0, 1 or 2.");
   int n=data==null?0:data.GetLength(0), cols=data==null?36:data.GetLength(1);
   Require(n<=200000&&cols<=256,"Matrix limit exceeded: at most 200000 rows and 256 columns.");
   Require(n==0||cols>=36,"locationDataMatrix requires N rows and at least 36 columns.");
   var result=new AnalysisResult { Ok=true, ModelVersion="evidence-v3.0.1-not-calibrated",InputRows=n,TotalPulses=n,
    OperatingPhaseVoltageKv=u0,EnglishOutput=en,OutputLanguage=en?"en-US":"zh-CN",ConclusionAudienceType=audience,
    ConclusionAudience=audience==0?"field-simple":"professional-technical",AlgorithmMode=mode,AlgorithmId="HVX-EVIDENCE-15-V3",
    RecommendationBasis=en?"IEEE Std 400.4 (DAC); engineering support, not a standards compliance verdict":"DL/T 1576-2016、DL/T 417-2019；适用GB及国网标准由项目确认；本算法不作合规判定",
    FactorColumns=FactorColumns.Length,FactorColumnNames=(string[])FactorColumns.Clone(),PhaseAssessmentColumnNames=(string[])PhaseColumns.Clone(),
    SourceDecisionMatrix=new double[n*4],SourceDecisionRows=n,InputToAcceptedRowMap=new double[n*6],InputToAcceptedRowMapRows=n,
    PhaseAssessmentMatrix=new double[0],PulseEnergyColumns=8,
    PulseEnergyColumnNames=new[]{"accepted_row_index","pdmap_id","voltage_kv","baseline_unused","peak_unused","integral_unused","task17_energy_index","sample_count_unused"} };
   var pulses=new List<Pulse>(); var background=new List<double>[] {new List<double>(),new List<double>(),new List<double>()};
   var unique=new HashSet<string>(StringComparer.Ordinal);var backgroundKeys=new HashSet<string>(StringComparer.Ordinal); int accepted=0;
   var appVolt=new Dictionary<string,List<double>>();var validatedPhases=new bool[3];
   for(int i=0;i<n;i++) {
    bool accept=data[i,35]==1; int k=i*6, d=i*4;
    result.InputToAcceptedRowMap[k]=i; result.InputToAcceptedRowMap[k+1]=accept?accepted:-1;
    result.InputToAcceptedRowMap[k+2]=Finite(data[i,9])?data[i,9]:-1;
    result.InputToAcceptedRowMap[k+3]=accept?1:0;result.InputToAcceptedRowMap[k+4]=Finite(data[i,0])?data[i,0]:0;
    result.InputToAcceptedRowMap[k+5]=-1;
    result.SourceDecisionMatrix[d]=i;result.SourceDecisionMatrix[d+1]=-1;result.SourceDecisionMatrix[d+2]=-1;
    int phase=(data[i,0]==1||data[i,0]==2||data[i,0]==3)?(int)data[i,0]:0;
    double raw=data[i,15],q=Math.Abs(raw),u=data[i,2];
    // Only recorded 0 kV samples establish background. Phase-separated, same
    // charge channel, no assumption that a missing background means zero noise.
    if(phase>0&&u==0&&Finite(q)&&q>0&&q<=1e12) {
     string bk=string.Join("|",new[]{(double)phase,data[i,1],data[i,9],data[i,3],q}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
     if(backgroundKeys.Add(bk))background[phase-1].Add(q);
    }
    if(!accept){result.SourceDecisionMatrix[d+3]=1;continue;}
    int ai=accepted++;
    if(phase==0||!Finite(q)||q<=0||q>1e12||!Finite(u)||u<0||u>1000||
     !Finite(data[i,13])||!Finite(data[i,3])||Math.Abs(data[i,3])>1e9) {result.SourceDecisionMatrix[d+3]=2;continue;}
    if(u==0) {result.SourceDecisionMatrix[d+3]=3;continue;}
    if(!validatedPhases[phase-1]){ValidateJoints(joints[phase-1],step);validatedPhases[phase-1]=true;}
    double x=data[i,13],length=joints[phase-1][joints[phase-1].Length-1];
    if(x<0||x>length) {result.SourceDecisionMatrix[d+3]=2;continue;}
    var p=new Pulse {Row=i,Accepted=ai,Phase=phase,X=x,Q=q,U=u,Angle=(data[i,3]%360+360)%360,
     App=Finite(data[i,1])&&data[i,1]>=0&&data[i,1]<=9007199254740991d&&data[i,1]==Math.Truncate(data[i,1])?data[i,1]:-1,
     Map=Finite(data[i,9])?data[i,9]:-1,Confidence=Finite(data[i,33])&&data[i,33]>=0&&data[i,33]<=1?data[i,33]:-1,
     Width=Finite(data[i,23])&&data[i,23]>0?data[i,23]:-1,Band=Finite(data[i,24])&&data[i,24]>0?data[i,24]:-1,
     Energy=Finite(data[i,18])&&data[i,18]>0&&data[i,18]<1e100?data[i,18]:-1 };
    // A repeated identical row is not an independent new pulse. Identity includes
    // phase/application/PDmap, position, charge and phase angle; row order is irrelevant.
    string fingerprint=string.Join("|",new[]{(double)p.Phase,p.App,p.Map,p.X,p.Q,p.U,p.Angle,p.Width,p.Band,p.Confidence}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
    if(!unique.Add(fingerprint)) {result.DuplicateRows++;result.SourceDecisionMatrix[d+3]=4;continue;}
    pulses.Add(p);result.SourceDecisionMatrix[d+1]=1;result.SourceDecisionMatrix[d+3]=5;
    if(p.App>=0) {string key=AppKey(p); if(!appVolt.ContainsKey(key))appVolt[key]=new List<double>();appVolt[key].Add(u);}
   }
   result.AcceptedRows=accepted;result.RejectedIndex35Rows=n-accepted;result.ValidPulses=pulses.Count;
   int wr=waves==null?0:waves.GetLength(0), wc=waves==null?0:waves.GetLength(1);
   Require(wr==0||wc>0,"A nonempty pulseEnvelopeMatrix cannot have zero columns.");
   Require(wr==0||wc==0||wr==n||wr==accepted,"pulseEnvelopeMatrix rows must equal input rows or accepted rows.");
   Require((long)wr*wc<=100000000,"pulseEnvelopeMatrix limit exceeded.");
   bool hasWave=wr>0&&wc>0;
   var energies=new List<double>();
   if(hasWave)foreach(var p in pulses.Where(p=>p.Energy>0)) {
    energies.AddRange(new[]{(double)p.Accepted,p.Map,p.U,0,0,0,p.Energy,0});result.InputToAcceptedRowMap[p.Row*6+5]=energies.Count/8-1;
   }
   result.PulseEnergyUsed=energies.Count>0;result.PulseEnergyMatrix=energies.ToArray();result.PulseEnergyRows=energies.Count/8;
   result.PulseEnergyStatus=hasWave?"TASK17_COLUMN18_AUXILIARY_ONLY_NOT_JOULES_NOT_RISK_WEIGHT":"DISABLED|statistics-only";
   var ambiguous=new HashSet<string>(appVolt.Where(kv=>kv.Value.Max()-kv.Value.Min()>Math.Max(.1,kv.Value.Max()*.02)).Select(kv=>kv.Key));
   var regions=new List<Region>();
   for(int phase=1;phase<=3;phase++) {
    var sorted=pulses.Where(p=>p.Phase==phase).OrderBy(p=>p.X).ThenBy(p=>p.App).ThenBy(p=>p.Q).ThenBy(p=>p.Angle).ThenBy(p=>p.Map).ToList();
    double noise=background[phase-1].Count>=5?Quantile(background[phase-1],.95):-1;
    if(sorted.Count==0)continue;
    double length=joints[phase-1].Last();
    var columns=DensityColumns(sorted,length,percent,step);
    // Independently resolved narrow columns are competing sources, not diffuse
    // background for one another. This morphology-only exclusion is decided
    // before scoring; it never depends on an action or a defect classification.
    var compactRows=new HashSet<int>(columns.Where(m=>m.Count>=3&&
     m.Last().X-m.First().X<=Bandwidth(Quantile(m.Select(p=>p.X),.5),length,percent,step))
     .SelectMany(m=>m.Select(p=>p.Row)));
    var spatialBackground=sorted.Where(p=>!compactRows.Contains(p.Row)).ToList();
    foreach(var members in columns) {
     var r=Assess(members,noise,u0,percent,step,mode,ambiguous,length,spatialBackground);Attribute(r,joints[phase-1],percent,step);regions.Add(r);
    }
   }
   var places=Merge(regions,percent,step);
   Fill(result,places,regions,u0,hasWave,en,audience);
   result.AssessmentState=pulses.Count==0?"INSUFFICIENT_DATA":(places.Any(g=>g.Action>=2)?"ATTENTION_IDENTIFIED":
    (places.Any(g=>g.Action==1)?"FOLLOW_UP_EVIDENCE":"INCONCLUSIVE_NO_SUPPORTED_LOCATION"));
   result.SummaryText=Summary(result,places,u0,en,audience);result.OriginalSummaryText=result.SummaryText;
   if(!string.IsNullOrWhiteSpace(modelPath)&&places.Count>0) {
    // Model annotations never change evidence, actions or exact row memberships.
    string summary=result.SummaryText;
    MultiFactorCore.AttachDefectLabels(result,data,places.Select(g=>g.P.Select(p=>p.Row).ToArray()).ToArray(),modelPath,en);
    result.SummaryText=summary; // Detailed labels remain in typed arrays/diagnostics.
   }
   return result;
  }
  static string AppKey(Pulse p) { return p.Phase+":"+p.App.ToString("R",CultureInfo.InvariantCulture); }
  static void ValidateJoints(double[] j,double step) {
   Require(j!=null&&j.Length>=2&&j.Length<=10000,"Each populated phase needs a joint array [0,...,length].");
   Require(j[0]==0,"The first joint-array element must be 0.");
   for(int k=0;k<j.Length;k++)Require(Finite(j[k])&&j[k]>=0&&j[k]<=1000000&&(k==0||j[k]>j[k-1]),"Joint positions must be finite, strictly increasing and within 1000000 m.");
   Require(j[j.Length-1]>=step,"Cable length must be at least cursorStepM.");
  }
  // Statistics bandwidth is distinct from joint attribution error. The 0.5% L
  // floor is an explicit engineering grouping resolution, not meter accuracy.
  static double Bandwidth(double x,double length,double percent,double step) {return Math.Max(Tol(x,percent,step),length*.005);}
  static List<List<Pulse>> DensityColumns(List<Pulse> p,double length,double percent,double step) {
   int n=p.Count,l=0,r=0;var left=new int[n];var right=new int[n];var taken=new bool[n];
   for(int i=0;i<n;i++) {double h=Bandwidth(p[i].X,length,percent,step);
    while(l<n&&p[l].X<p[i].X-h)l++;while(r<n&&p[r].X<=p[i].X+h)r++;left[i]=l;right[i]=r;}
   var output=new List<List<Pulse>>();
   foreach(int seed in Enumerable.Range(0,n).OrderByDescending(i=>right[i]-left[i]).ThenBy(i=>p[i].X)) {
    if(taken[seed])continue;
    double center=p[(left[seed]+right[seed]-1)/2].X,h=Bandwidth(center,length,percent,step);
    int lo=LowerBound(p,center-2*h),hi=LowerBound(p,center+2*h+1e-9);var group=new List<Pulse>();
    for(int j=lo;j<hi;j++)if(!taken[j]){taken[j]=true;group.Add(p[j]);}
    // A broad grouping floor must not erase two narrow peaks separated by an
    // empty spatial valley. Split only when both sides have at least 3 records.
    int start=0;
    for(int j=1;j<group.Count;j++)if(j-start>=3&&group.Count-j>=3&&
     group[j].X-group[j-1].X>Bandwidth((group[j].X+group[j-1].X)/2,length,percent,step)) {
     output.Add(group.GetRange(start,j-start));start=j;
    }
    if(group.Count>start)output.Add(group.GetRange(start,group.Count-start));
   }return output;
  }
  static int LowerBound(List<Pulse> p,double x) {int l=0,r=p.Count;while(l<r){int m=l+(r-l)/2;if(p[m].X<x)l=m+1;else r=m;}return l;}
  static int UpperBound(List<Pulse> p,double x) {int l=0,r=p.Count;while(l<r){int m=l+(r-l)/2;if(p[m].X<=x)l=m+1;else r=m;}return l;}
  // Adjacent same-phase energized sidebands measure spatial contrast. A remote
  // concentrated source is not local spatial background. The one-record floor
  // is an engineering regularizer, not field calibration. Measured 0-kV charge
  // background remains an independent evidence gate.
  static double LocalEnrichment(Region r,List<Pulse> sorted,double length) {
   double h=r.Bandwidth,left=Math.Max(0,r.Low-2*h),right=Math.Min(length,r.High+2*h);
   double sideWidth=(r.Low-left)+(right-r.High);
   double coreWidth=Math.Min(length,r.High+h)-Math.Max(0,r.Low-h);
   if(sideWidth<=0||coreWidth<=0)return 0;
   int sideCount=LowerBound(sorted,r.Low)-LowerBound(sorted,left)+
    UpperBound(sorted,right)-UpperBound(sorted,r.High);
   // Keep the dimensionless width ratio together to avoid intermediate
   // reciprocal overflow for positive subnormal-length boundary inputs.
   return ((double)r.P.Count/Math.Max(1,sideCount))*(sideWidth/coreWidth);
  }
  static Region Assess(List<Pulse> p,double noise,double u0,double percent,double step,int mode,HashSet<string> ambiguous,double length,List<Pulse> sorted) {
   var r=new Region {P=p,Phase=p[0].Phase,Low=p.Min(a=>a.X),High=p.Max(a=>a.X),X=Quantile(p.Select(a=>a.X),.5),
    MaxQ=p.Max(a=>a.Q),MeanQ=p.Average(a=>a.Q),MinU=p.Min(a=>a.U),Noise=noise,Joint=-1};
   var apps=p.Where(a=>a.App>=0&&!ambiguous.Contains(AppKey(a))).GroupBy(a=>a.App).OrderBy(g=>g.Key).ToList();
   r.Apps=apps.Count;r.AmbiguousOrder=p.Any(a=>a.App<0||ambiguous.Contains(AppKey(a)));
   r.Match=p.Any(a=>a.Confidence>=0)?p.Where(a=>a.Confidence>=0).Average(a=>a.Confidence):-1;
   r.MatchCoverage=(double)p.Count(a=>a.Confidence>=0)/p.Count;
   r.Coherence=Coherence(p); r.Trend=Trend(apps);r.Floating=RegularSpacing(apps);
   double count=Clamp(Math.Log(1+p.Count)/Math.Log(25)),repeat=Clamp((r.Apps-1)/3.0);
   r.Bandwidth=Bandwidth(r.X,length,percent,step);
   r.Shape=(double)p.Count(a=>Math.Abs(a.X-r.X)<=r.Bandwidth)/p.Count;
   r.Enrichment=LocalEnrichment(r,sorted,length);
   double ampSpread=Quantile(p.Select(a=>Math.Log(1+a.Q)),.9)-Quantile(p.Select(a=>Math.Log(1+a.Q)),.1);
   double morphology=.75*r.Shape+.25*Clamp(ampSpread/1.5);
   double signal=noise>0?Clamp((Quantile(p.Select(a=>a.Q),.5)/noise-1)/3):0;
   r.Score=Clamp(.25*count+.25*repeat+.20*morphology+.10*Math.Max(0,r.Match)*r.MatchCoverage+.15*signal+
    (mode==0?0:.03*r.Coherence+.02*r.Trend));
   r.ServiceCount=u0>0?p.Count(a=>a.U<=u0+1e-6&&(noise<0||a.Q>1.5*noise)):0;
   bool aboveNoise=noise<0||Quantile(p.Select(a=>a.Q),.5)>1.5*noise;
   bool spatial=r.Shape>=.60&&r.Enrichment>=3;
   r.Action=r.Score>=.65&&p.Count>=5&&r.Apps>=2&&spatial&&aboveNoise?2:
    (r.Score>=.40&&p.Count>=2&&aboveNoise&&r.Enrichment>=2?1:0);
   if(p.Count>=3&&r.Apps>=3&&r.Match>=.8&&r.MatchCoverage>=.8&&spatial&&r.ServiceCount>=2&&aboveNoise)r.Action=Math.Max(2,r.Action);
   if(r.Action==2&&r.ServiceCount>=5&&r.Apps>=3&&noise>0&&signal>=.5&&r.Score>=.80)r.Action=3;
   if(r.Match>=0&&r.Match<.3)r.Action=0;
   if(!aboveNoise)r.Action=0;
   if(r.Apps<2)r.Action=Math.Min(1,r.Action);
   // Exact boundary coordinates may be upstream clipping/snapping. Retain every
   // record for review but do not infer a terminal defect from that artificial
   // vertical column alone. This is an abstention, not noise classification.
   r.BoundaryPileup=r.Low==r.High&&(r.X==0||r.X==length);
   if(r.BoundaryPileup) {r.Action=0;r.Score=Math.Min(r.Score,.39);}
   return r;
  }
  static double Coherence(List<Pulse> p) {
   var a=p.Where(v=>v.Width>0&&v.Band>0).ToList();if(a.Count<3)return 0;
   var w=a.Select(v=>Math.Log(v.Width)).ToArray();var b=a.Select(v=>Math.Log(v.Band)).ToArray();
   return (double)a.Count/p.Count*Clamp(1-(Quantile(w,.9)-Quantile(w,.1)+Quantile(b,.9)-Quantile(b,.1))/2);
  }
  static double Trend(List<IGrouping<double,Pulse>> apps) {
   if(apps.Count<3)return 0; var rising=new List<Tuple<double,double>>();double previous=-1,best=0;
   foreach(var g in apps) {double u=Quantile(g.Select(p=>p.U),.5),q=Quantile(g.Select(p=>Math.Log(1+p.Q)),.5);
    if(u<previous-.1){best=Math.Max(best,RisingCorrelation(rising));rising.Clear();}
    if(rising.Count==0||u>previous+.1)rising.Add(Tuple.Create(u,q));previous=u;}
   return Math.Max(best,RisingCorrelation(rising));
  }
  static double RisingCorrelation(List<Tuple<double,double>> rising) {
   if(rising.Count<3)return 0;
   double ax=rising.Average(p=>p.Item1),ay=rising.Average(p=>p.Item2),xy=0,xx=0,yy=0;
   foreach(var p in rising) {double x=p.Item1-ax,y=p.Item2-ay;xy+=x*y;xx+=x*x;yy+=y*y;}
   return xx>0&&yy>0?Clamp(xy/Math.Sqrt(xx*yy)):0;
  }
  static bool RegularSpacing(List<IGrouping<double,Pulse>> apps) {
   foreach(var app in apps) {
    var p=app.OrderBy(a=>a.Angle).ToList();if(p.Count<5)continue;
    // Include the wrap-around gap; a short equally spaced arc is not evidence
    // of a periodic full-cycle pattern. The hint never raises the action code.
    var gaps=p.Skip(1).Select((a,i)=>a.Angle-p[i].Angle).Concat(new[]{360-p.Last().Angle+p[0].Angle}).ToArray();double mean=gaps.Average();
    double q=p.Average(a=>a.Q);
    if(mean>5&&gaps.Max()<180&&Math.Sqrt(gaps.Average(x=>(x-mean)*(x-mean)))/mean<.15&&
     Math.Sqrt(p.Average(a=>(a.Q/q-1)*(a.Q/q-1)))<.15)return true;
   }return false;
  }
  static void Attribute(Region r,double[] joints,double percent,double step) {
   double t=Tol(r.X,percent,step);var near=joints.Select((v,i)=>new{v,i}).Where(a=>Math.Abs(a.v-r.X)<=t+1e-9).OrderBy(a=>Math.Abs(a.v-r.X)).ToArray();
   r.Component=0;if(near.Length==0)return;
   if(near.Length>1){r.Component=4;return;}
   r.Joint=near[0].v;r.Component=near[0].i==0?2:(near[0].i==joints.Length-1?3:1);
  }
  static List<Place> Merge(List<Region> regions,double percent,double step) {
   var groups=new List<Place>();
   foreach(var r in regions.OrderBy(r=>r.X).ThenBy(r=>r.Phase)) {
    var g=groups.Where(v=>!v.Regions.Any(a=>a.Phase==r.Phase||(a.Action==0)!=(r.Action==0)||
     Math.Abs(a.X-r.X)>a.Bandwidth+r.Bandwidth)).OrderBy(v=>Math.Abs(v.Regions.Average(a=>a.X)-r.X)).FirstOrDefault();
    if(g==null) {
     g=new Place();groups.Add(g);
    }g.Regions.Add(r);
   }
   for(int i=0;i<groups.Count;i++) {
    var g=groups[i];g.P=g.Regions.SelectMany(r=>r.P).OrderBy(p=>p.Phase).ThenBy(p=>p.X).ThenBy(p=>p.App).ThenBy(p=>p.Q).ThenBy(p=>p.Angle).ToList();
    g.Mask=g.Regions.Sum(r=>1<<(r.Phase-1));g.X=Quantile(g.P.Select(p=>p.X),.5);g.Low=g.P.Min(p=>p.X);g.High=g.P.Max(p=>p.X);
    g.Action=g.Regions.Max(r=>r.Action);g.Score=g.Regions.Max(r=>r.Score);foreach(var r in g.Regions)r.Group=i;
   }return groups;
  }
  static void Fill(AnalysisResult o,List<Place> places,List<Region> regions,double u0,bool hasWave,bool en,int audience) {
   var loc=new List<double>();var cand=new List<double>();var scatter=new List<double>();var factors=new List<double>();var voltage=new List<double>();
   var markers=new List<double>();var phase=new List<double>();var diagnostics=new List<string>();var status=new List<string>();
   var candidatePlaces=new List<int>();
   for(int i=0;i<places.Count;i++) {
    var g=places[i];var p=g.P;double minU=p.Min(a=>a.U),energy=hasWave?p.Where(a=>a.Energy>0).Sum(a=>a.Energy):0;
    loc.AddRange(new[]{(double)g.Mask,g.X,g.Low,g.High,p.Count,p.Max(a=>a.Q),p.Average(a=>a.Q),g.Score,g.Action,g.Regions.Count,g.Regions.Count});
    voltage.AddRange(new[]{(double)g.Mask,g.X,minU,u0,u0>0?minU/u0:0,p.Count(a=>a.U==minU),g.Regions.Max(r=>r.Apps),g.Regions.Max(r=>r.Trend),g.Regions.Any(r=>r.ServiceCount>0)?1d:0d});
    double component=g.Regions.Select(r=>r.Component).Distinct().Count()==1?g.Regions[0].Component:4;
    factors.AddRange(new[]{(double)g.Mask,g.X,g.Low,g.High,p.Count,g.Regions.Max(r=>r.Apps),p.Max(a=>a.Q),p.Average(a=>a.Q),0,minU,u0>0?minU/u0:0,
     g.Regions.Max(r=>r.Trend),0,Clamp(Math.Log(1+p.Count)/Math.Log(25)),0,0,g.Regions.Any(r=>r.ServiceCount>=2)?1d:0,0,0,0,g.Regions.Max(r=>r.Shape),
     g.Score,g.Score,g.Action,component,0,g.Action,i,energy>0?1d:0,energy,energy>0?energy/p.Count(a=>a.Energy>0):0});
    if(g.Action>0)markers.AddRange(new[]{(double)g.Mask,g.X,g.Action,g.Score});
    foreach(var r in g.Regions) {
     phase.AddRange(new[]{(double)i,r.Phase,r.P.Count,r.Apps,r.X,r.MinU,r.MaxQ,r.MeanQ,r.Score,r.Action,r.Noise,r.ServiceCount,r.Component,r.Joint,r.Coherence,r.Trend,r.Floating?1d:0,r.Match});
     cand.AddRange(new[]{r.Phase-1d,r.X,r.Low,r.High,r.P.Count,r.MaxQ,r.MeanQ,r.Score,r.Action,r.Joint,r.Joint>=0?Math.Abs(r.Joint-r.X):-1,r.Apps,r.Coherence,r.Shape,(double)(cand.Count/15)});
      candidatePlaces.Add(i);
     foreach(var a in r.P) {
      o.SourceDecisionMatrix[a.Row*4+2]=i;o.SourceDecisionMatrix[a.Row*4+3]=r.BoundaryPileup?6:(r.Action>0?0:5);
      if(r.Action>0)scatter.AddRange(new[]{a.Phase-1d,a.X,a.Q});
     }
    }
    diagnostics.Add(Describe(g,u0,en,1));status.Add(Action(g.Action,en));
   }
   o.LocationMatrix=loc.ToArray();o.LocationRows=places.Count;o.CandidateMatrix=cand.ToArray();o.CandidateRows=regions.Count;
   o.CandidateLocationRows=candidatePlaces.ToArray();
   o.FilteredScatter=scatter.ToArray();o.FilteredScatterRows=scatter.Count/3;o.FactorMatrix=factors.ToArray();o.FactorRows=places.Count;
   o.VoltageAssessmentMatrix=voltage.ToArray();o.VoltageAssessmentRows=places.Count;o.MarkerMatrix=markers.ToArray();o.MarkerRows=markers.Count/4;
   o.PhaseAssessmentMatrix=phase.ToArray();o.PhaseAssessmentRows=phase.Count/18;o.DiagnosticArray=diagnostics.ToArray();o.StatusArray=status.ToArray();
   o.ActionColors=places.Select(g=>g.Action==3?"#D93025":g.Action==2?"#F57C00":g.Action==1?"#2B6CB0":"").ToArray();
   o.DefectTypeArray=places.Select(g=>"").ToArray();o.DefectProbabilityArray=new double[places.Count];
   o.DefectStatusArray=places.Select(g=>"DISABLED|empty-defect-model-path").ToArray();
  }
  static string Action(int a,bool en) {return en?new[]{"Insufficient evidence","Planned review","Attention","Priority engineering review"}[a]:new[]{"证据不足","计划跟踪","重点关注","优先核查"}[a];}
  static string Part(Region r,bool en) {
   if(r.Component==4)return en?"component attribution ambiguous":"部位归属存在重叠，待核对";
   if(r.Component==0)return en?"cable body":"电缆本体";
   if(r.Component==2)return en?"near the starting termination":"始端终端附近";
   if(r.Component==3)return en?"near the remote termination":"末端终端附近";
   return en?"near the joint at "+F(r.Joint)+" m":F(r.Joint)+" m接头附近";
  }
  static string Describe(Place g,double u0,bool en,int audience) {
   var sb=new StringBuilder();
   foreach(var r in g.Regions) {
    if(sb.Length>0)sb.Append(en?"; ":"；"); string ph=PhaseName(r.Phase,en);
    if(en)sb.Append(ph).Append(" phase near ").Append(F(r.X)).Append(" m (").Append(Part(r,true)).Append("): ").Append(Action(r.Action,true))
     .Append(", ").Append(r.P.Count).Append(" pulses / ").Append(r.Apps).Append(" identified applications, max/mean ").Append(F(r.MaxQ,"0")).Append("/").Append(F(r.MeanQ,"0")).Append(" pC");
    else sb.Append(ph).Append("相约").Append(F(r.X)).Append(" m（").Append(Part(r,false)).Append("）：").Append(Action(r.Action,false))
     .Append("，").Append(r.P.Count).Append("个脉冲/").Append(r.Apps).Append("组可识别加压，最大/平均放电量").Append(F(r.MaxQ,"0")).Append("/").Append(F(r.MeanQ,"0")).Append(" pC");
    if(audience==1)sb.Append(en?", evidence score ":"，证据评分").Append(F(r.Score*100,"0")).Append("/100");
    if(u0>0&&r.ServiceCount>0)sb.Append(en?", ":"，").Append(r.ServiceCount).Append(en?" pulses recorded at or below service phase voltage (DAC evidence, not proof of continuous in-service PD)":"个记录位于运行相电压及以下，需关注运行电压下放电风险（不等同于证实运行中持续放电）");
    else if(u0>0)sb.Append(en?", no qualifying records at or below U0 in supplied data; coverage not established":"，所供数据未检出运行相电压及以下有效记录，不能据此判定该电压下无放电");
    else sb.Append(en?", service phase voltage not provided":"，未提供运行相电压，暂不作运行电压风险判断");
    if(audience==1) {
     sb.Append(en?", minimum detected voltage ":"，最低检出记录电压").Append(F(r.MinU)).Append(en?" kV (PDIV/PDEV not established)":" kV（非经完整试验确认的PDIV，PDEV未知）");
     if(r.Trend>.6)sb.Append(en?", ascending Q-U support":"，升压记录存在放电量增强趋势，仅作辅助");
     if(r.Floating)sb.Append(en?", regular within-application phase spacing is an auxiliary hint only":"，同次加压相位间隔及幅值较一致，仅作悬浮类特征提示");
     if(r.AmbiguousOrder)sb.Append(en?", application ordering evidence incomplete":"，加压标识或电压次序证据不完整");
    }
    if(r.Noise<0)sb.Append(en?", background comparison unavailable":"，缺少足量0 kV背景记录，抗干扰证据不足");
    if(r.BoundaryPileup)sb.Append(en?", fixed endpoint coordinates require a clipping/snapping check; terminal PD is not excluded":"，端点坐标完全固定，需核查是否被吸附或截断，不能据此排除真实终端放电");
   }
   return sb.ToString();
  }
  static string Summary(AnalysisResult o,List<Place> places,double u0,bool en,int audience) {
   var s=new StringBuilder();int attention=places.Count(g=>g.Action>=2),track=places.Count(g=>g.Action==1);
   s.Append(en?"HV-Xpert analyzed ":"本次测试HV-Xpert系统为您分析").Append(o.InputRows)
    .Append(en?" input pulse records; ":"个输入脉冲记录，去除未接受、无效、背景及重复记录后保留").Append(o.ValidPulses)
    .Append(en?" unique usable energized records remain. ":"个有效加压脉冲，");
   if(o.ValidPulses==0) {s.Append(en?"Insufficient evidence; equipment condition cannot be determined.":"数据不足，暂不能判断电缆局部放电状况。");return s.ToString();}
   s.Append(en?"Main concentration sites: ":"检出").Append(attention).Append(en?"; weaker sites for planned review: ":"处主要集中位置，另有").Append(track).Append(en?".":"处列入计划跟踪。");
   if(places.Any(g=>g.Regions.Select(r=>r.Action).Distinct().Count()>1))
    s.Append(en?" Site counts use the highest action at each location; phase-specific recommendations follow.":"位置数量按该处最高关注等级统计，各相处理建议见下文。");
   var selected=places.Where(g=>g.Action>0).OrderByDescending(g=>g.Action).ThenByDescending(g=>g.Score).ThenBy(g=>g.X).ToList();
   int cap=audience==0?4:6;
   for(int i=0;i<Math.Min(cap,selected.Count);i++)s.Append("\r\n").Append(i+1).Append(". ").Append(Compact(selected[i],u0,en,audience)).Append(en?".":"。");
   if(selected.Count>cap)s.Append("\r\n").Append(en?"Additional locations are listed in DiagnosticArray and the location table.":"其余位置请查看定位明细及DiagnosticArray。");
   if(selected.Count==0)s.Append(en?" No location has sufficient combined evidence. This is not a healthy-cable verdict.":"尚无位置达到综合证据要求，不等同于电缆无局部放电。");
   int boundary=places.SelectMany(g=>g.Regions).Where(r=>r.BoundaryPileup).Sum(r=>r.P.Count);
   int weak=places.SelectMany(g=>g.Regions).Where(r=>r.Action==0&&!r.BoundaryPileup).Sum(r=>r.P.Count);
   if(boundary+weak>0)s.Append("\r\n").Append(en?"Other records: ":"其余记录：").Append(boundary).Append(en?" fixed-endpoint records and ":"个端点定值记录、").Append(weak)
    .Append(en?" weak/isolated records retained for review, not identified as noise or additional confirmed defects.":"个弱证据或离散记录保留待核查，不直接认定为干扰或新增缺陷。");
   bool missingNoise=selected.Any(g=>g.Regions.Any(r=>r.Noise<0));
   if(missingNoise)s.Append("\r\n").Append(en?"Background separation is unverified: insufficient 0 kV records.":"缺少足量0 kV背景记录，尚不能充分排除干扰。");
   if(u0<=0)s.Append("\r\n").Append(en?"Service phase voltage not provided; service-voltage risk is not assessed.":"未提供运行相电压，暂不作运行电压风险判断。");
   else if(!selected.Any(g=>g.Regions.Any(r=>r.ServiceCount>0)))s.Append("\r\n").Append(en?"No qualifying records at/below U0 in supplied data; absence of PD at U0 is not established.":"所供数据未检出运行相电压及以下有效记录；试验覆盖尚未确认，不能据此认定该电压下无放电。");
   s.Append("\r\n").Append(en?"Review existing calibration and test records first; target inspection to site conditions instead of mandatory standalone retesting. Automated engineering reference only.":"建议先核查已有校准和测试记录，再按现场条件安排针对性检查，不一律要求单独复测。本结果仅供参考，不作为单独停送电依据。");
   return s.ToString();
  }
  static string Compact(Place g,double u0,bool en,int audience) {
   string phases=string.Join(en?"/":"、",g.Regions.OrderBy(r=>r.Phase).Select(r=>PhaseName(r.Phase,en)).ToArray());
   bool mixedActions=g.Regions.Select(r=>r.Action).Distinct().Count()>1;
   var s=new StringBuilder();s.Append(en?"Near ":"约").Append(F(g.X)).Append(en?" m, ":" m，");
   if(mixedActions)s.Append(string.Join(en?"; ":"；",g.Regions.OrderBy(r=>r.Phase).Select(r=>
    PhaseName(r.Phase,en)+(en?": ":"相：")+Action(r.Action,en)).ToArray()));
   else s.Append(phases).Append(en?"; ":"相：").Append(Action(g.Action,en));
   s.Append(en?"; ":"。共").Append(g.P.Count).Append(en?" pulses, max/mean ":"个脉冲，最大/平均放电量")
    .Append(F(g.P.Max(p=>p.Q),"0")).Append("/").Append(F(g.P.Average(p=>p.Q),"0")).Append(" pC");
   if(g.Regions.All(r=>r.Component==0))s.Append(en?"; cable body under the configured joint tolerance":"；按当前接头误差设置归入电缆本体");
   else foreach(var r in g.Regions.OrderBy(r=>r.Phase))s.Append(en?"; ":"；").Append(PhaseName(r.Phase,en)).Append(en?": ":"相").Append(Part(r,en));
   int atService=g.Regions.Sum(r=>r.ServiceCount);
   if(atService>0)s.Append(en?"; ":"；").Append(atService).Append(en?" records at/below U0 warrant attention (not proof of continuous service PD)":"个记录位于运行相电压及以下，应关注运行电压下的放电风险");
   if(audience==1) {
    s.Append(mixedActions?(en?"; highest phase evidence score ":"；逐相最高证据评分"):(en?"; evidence score ":"；证据评分")).Append(F(g.Score*100,"0")).Append("/100")
     .Append(en?", minimum detected voltage ":"，最低检出记录电压").Append(F(g.P.Min(p=>p.U))).Append(en?" kV, not a verified PDIV":" kV，不作为已确认的PDIV");
    s.Append(en?"; phase-specific details in DiagnosticArray":"，逐相证据见定位明细");
   }return s.ToString();
  }
  static string PhaseName(int phase,bool english) {return english?"L"+phase.ToString(CultureInfo.InvariantCulture):((char)('A'+phase-1)).ToString();}
 }
}


// SOURCE: OptionalClassifier.cs
namespace HVX.Location {
    // Optional, explicitly configured type annotation only. Empty model path
    // makes no file access or external load. Use trusted company Task12 files.
    // Old charge thresholds, age/material scoring and PDIV inference removed.
    internal static class MultiFactorCore {
        internal static void AttachDefectLabels(AnalysisResult result, double[,] matrix,
            int[][] locationInputRows, string defectModelPath, bool english)
        {
            int rows = result.LocationRows;
            result.DefectTypeArray = new string[rows];
            result.DefectProbabilityArray = new double[rows];
            result.DefectStatusArray = new string[rows];
            string[] diagnostics = new string[rows];
            bool enabled = !string.IsNullOrWhiteSpace(defectModelPath);
            // Empty path never constructs or loads an external model.
            Task12Bridge classifier = enabled && Enumerable.Range(0, rows).Any(r =>
                Cell(result.LocationMatrix, r, result.LocationColumns, 8) > 0.0)
                ? new Task12Bridge(defectModelPath) : null;
            try
            {
            List<Pulse> pulses = ReadPulses(matrix);
            StringBuilder labels = new StringBuilder();
            for (int row = 0; row < rows; row++)
            {
                result.DefectTypeArray[row] = string.Empty;
                diagnostics[row] = result.DiagnosticArray != null && row < result.DiagnosticArray.Length
                    ? result.DiagnosticArray[row] : string.Empty;
                bool needsLabel = Cell(result.LocationMatrix, row, result.LocationColumns, 8) > 0.0;
                if (!enabled || !needsLabel)
                {
                    result.DefectStatusArray[row] = !enabled
                        ? "DISABLED|empty-defect-model-path" : "SKIPPED|no-action-location";
                    continue;
                }
                if (locationInputRows == null || row >= locationInputRows.Length)
                {
                    result.DefectStatusArray[row] = "UNAVAILABLE|missing-location-member-map";
                    AppendRuntimeMessage(result, result.DefectStatusArray[row]);
                    continue;
                }
                HashSet<int> selected = new HashSet<int>(locationInputRows[row]);
                List<Pulse> members = pulses.Where(p => selected.Contains(p.RowIndex)).ToList();
                List<string> phaseTypes = new List<string>();
                List<string> phaseStatuses = new List<string>();
                // A merged location can contain several phases, but each PRPD
                // classification must use only the pulses of one physical phase.
                foreach (IGrouping<int, Pulse> phase in members.GroupBy(p => p.Phase).OrderBy(g => g.Key))
                {
                    string phaseName = PhaseText(1 << (phase.Key - 1), english);
                    Task12Result prediction = classifier.Classify(
                        phase.Select(p => p.PhaseDegree).ToArray(), phase.Select(p => p.Charge).ToArray());
                    bool accepted = prediction.Available && !prediction.Rejected;
                    phaseStatuses.Add(phaseName + ": " + prediction.Status);
                    string detail = accepted
                        ? prediction.Type + " " + (prediction.Probability * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                        : prediction.Status;
                    string text = (english ? "Defect label: " : "缺陷类型：") + detail;
                    diagnostics[row] = (diagnostics[row] + " " + phaseName + ": " + text).Trim();
                    if (accepted)
                    {
                        phaseTypes.Add(phaseName + (english ? ": " : "：") + detail);
                        result.DefectProbabilityArray[row] = Math.Max(result.DefectProbabilityArray[row], prediction.Probability);
                        labels.AppendLine().Append(phaseName).Append(english ? " near " : "约")
                            .Append(Cell(result.LocationMatrix, row, result.LocationColumns, 1).ToString("0.0", CultureInfo.InvariantCulture))
                            .Append(" m ").Append(text);
                    }
                    else
                        AppendRuntimeMessage(result, (english ? "[Defect recognition] " : "[缺陷识别] ") + phaseName + ": " + prediction.Status);
                }
                result.DefectTypeArray[row] = string.Join(english ? "; " : "；", phaseTypes.ToArray());
                result.DefectStatusArray[row] = string.Join(english ? "; " : "；", phaseStatuses.ToArray());
                if (result.FactorColumns > 25 && result.FactorRows > row)
                    result.FactorMatrix[row * result.FactorColumns + 25] = result.DefectProbabilityArray[row];
            }
            result.DiagnosticArray = diagnostics;
            if (labels.Length > 0) result.SummaryText += labels.ToString();
            }
            finally
            {
                if (classifier != null) classifier.Dispose();
            }
        }

        internal static void AppendRuntimeMessage(AnalysisResult result, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            message = LocatorEngine.SingleLine(message).Trim();
            string current = result.Error ?? string.Empty;
            if (current.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Contains(message)) return;
            result.Error = current.Length == 0 ? message : current + Environment.NewLine + message;
        }

        private static string PhaseText(int mask, bool english)
        {
            List<string> names = new List<string>();
            if ((mask & 1) != 0) names.Add(english?"L1":"A"); if ((mask & 2) != 0) names.Add(english?"L2":"B"); if ((mask & 4) != 0) names.Add(english?"L3":"C");
            string joined = string.Join("/", names.ToArray());
            return english ? joined : joined + "相";
        }

        private static double Cell(double[] values, int row, int columns, int column)
        {
            int index = row * columns + column;
            return values != null && column < columns && index >= 0 && index < values.Length ? values[index] : 0.0;
        }

        private static double Phase(double value) { value %= 360.0; return value < 0.0 ? value + 360.0 : value; }
        private static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static double[] Safe(double[] values) { return values ?? new double[0]; }


        private sealed class Pulse {
            internal int RowIndex, Phase;
            internal double PhaseDegree, Charge;
        }
        private static List<Pulse> ReadPulses(double[,] matrix) {
            var output = new List<Pulse>();
            if(matrix==null||matrix.GetLength(1)<36)return output;
            for(int r=0;r<matrix.GetLength(0);r++) {
                double phase=matrix[r,0],q=Math.Abs(matrix[r,15]);
                if((phase!=1&&phase!=2&&phase!=3)||matrix[r,35]!=1||
                   !Finite(q)||q<=0||!Finite(matrix[r,3]))continue;
                output.Add(new Pulse {RowIndex=r,Phase=(int)phase,PhaseDegree=Phase(matrix[r,3]),Charge=q});
            }
            return output;
        }
        private sealed class Task12Result
        {
            internal bool Available, Rejected;
            internal string Type = string.Empty, Status = string.Empty;
            internal double Probability;
        }

        // Legacy method signatures remain for source compatibility. A model
        // path is data, never authority to discover or execute adjacent DLLs.
        // The host's separately managed classifier is outside this bridge.
        private sealed class Task12Bridge
        {
            private readonly string loadStatus;

            internal Task12Bridge(string modelPath = null)
            {
                loadStatus = string.IsNullOrWhiteSpace(modelPath)
                    ? "DISABLED|empty-defect-model-path"
                    : "DISABLED|legacy-external-classifier-loading-disabled; use the host-managed classifier separately.";
            }

            internal Task12Result Classify(double[] phase, double[] charge)
            {
                return new Task12Result { Available = false, Rejected = false,
                    Status = loadStatus, Type = string.Empty, Probability = 0.0 };
            }

            internal void Dispose() { }
        }
    }
}


// SOURCE: PublicApi.cs
namespace HVX.Location {
 public sealed partial class AnalysisResult {
  public bool IsCalibratedProbability { get { return false; } }
  public string ScoreMeaning { get { return "UNCALIBRATED_ENGINEERING_EVIDENCE_NOT_FAILURE_PROBABILITY"; } }
  public string AssessmentState { get; internal set; }
  public int DuplicateRows { get; internal set; }
  public double[] SourceDecisionMatrix { get; internal set; }
  public int SourceDecisionRows { get; internal set; }
  public int SourceDecisionColumns { get { return 4; } }
  public double[] PhaseAssessmentMatrix { get; internal set; }
  public int PhaseAssessmentRows { get; internal set; }
  public int PhaseAssessmentColumns { get { return 18; } }
  public string[] PhaseAssessmentColumnNames { get; internal set; }
 }
 public sealed class LocatorEngine {
  internal static string SingleLine(string text) { return (text??"").Replace('\r',' ').Replace('\n',' '); }
  internal static double ReadLocationCharge(double[,] matrix,int row,int columns) {
   return Math.Abs(matrix[row,15]);
  }
  public const string ApiVersion = "hvx-conclusion-dotnet-3.0.1-evidence";
  public string Health() { return "OK|" + ApiVersion + "|statistical-evidence|not-field-calibrated"; }
  public AnalysisResult AnalyzeWithStandardProfilePulseEnergyAndAlgorithmMode(
   double[,] locationDataMatrix, double[,] pulseEnvelopeMatrix,
   double[] jointsA, double[] jointsB, double[] jointsC,
   double locationErrorPercent, double cursorStepM, double operatingPhaseVoltageKv,
   double cableServiceYears, int cableBodyType, int intermediateJointType,
   bool englishOutput, int conclusionAudienceType, int algorithmMode, string defectModelPath) {
   try {
    return EvidenceCore.Run(locationDataMatrix,pulseEnvelopeMatrix,new[]{jointsA,jointsB,jointsC},
     locationErrorPercent,cursorStepM,operatingPhaseVoltageKv,cableServiceYears,cableBodyType,
     intermediateJointType,englishOutput,conclusionAudienceType,algorithmMode,defectModelPath);
   } catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException)) {
    return new AnalysisResult { Ok=false, Error=ex.Message.Replace('\r',' ').Replace('\n',' '),
     AssessmentState="INPUT_OR_EXECUTION_ERROR", SummaryText=englishOutput?
      "Analysis failed. No equipment-condition conclusion is available.":"分析未完成，暂不能形成电缆状态结论。",
     EnglishOutput=englishOutput, OutputLanguage=englishOutput?"en-US":"zh-CN",
     SourceDecisionMatrix=new double[0], PhaseAssessmentMatrix=new double[0], PhaseAssessmentColumnNames=(string[])EvidenceCore.PhaseColumns.Clone() };
   }
  }
 }
}
