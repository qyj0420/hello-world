using System;
using System.Globalization;
using System.Text;
using HVX.PdReview;
using HVX.SignalEvidence;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        internal RawEndpointPolicy ManufacturerEndpointPolicy=RawEndpointPolicy.Parse("2;1.5;OR;OR");
        // Explicit-click service only. Do not call a confidence update, select a
        // different row, snap a position, accept a MAP or write any input matrix.
        public EndpointIdentificationResult IdentifyEndpoint()
        {
            var e = new EndpointIdentificationResult.Builder {
                SourceRowIndex=currentParameterRowIndex, PdMapId=currentPdMapId,
                PolicyVersion=RawEndpointMath.Version + "; review-gates-2026.09.24-v2",
                SamplingRateHz=Safe(originalParameterRow,7,double.NaN), CableLengthM=cableLengthM,
                SampleLocationM=CalculatedLocation(),
                EndpointToleranceM=cableLengthM*Math.Max(acceptedPositionErrorPercent,BoundaryConfidenceMath.TolerancePercent)/100
            };
            var detail = new StringBuilder();
            if(!matrixDriven||currentParameterRowIndex<0||originalParameterRow.Length<36)
                return EndpointResult(e,detail,"Indeterminate","source_provenance_missing",L("A source MAP with original sampling metadata is required.","需要带原始采样信息的 MAP 数据。"));
            double phase=Safe(originalParameterRow,0,double.NaN);
            if(!IsFinite(phase)||phase<0||phase>3||phase!=Math.Round(phase))
                return EndpointResult(e,detail,"Indeterminate","physical_phase_unknown",L("The physical phase is not known.","无法确认物理相别。"));
            e.PhysicalPhase=phase==0?"ALL":phase==1?"A":phase==2?"B":"C";
            if(!IsFinite(e.SamplingRateHz)||e.SamplingRateHz<6000000||!IsFinite(dtNs)||dtNs<=0||
                Math.Abs(e.SamplingRateHz*dtNs/1e9-1)>1e-9)
                return EndpointResult(e,detail,"Indeterminate","sampling_rate_unreliable",L("Original sampling rate is missing, inconsistent, or below the measurement bandwidth requirement.","原始采样率缺失、不一致或不足以支持测量带宽。"));
            if(!reflectionSelected||reflection<=incident||incident<0||reflection>=pulse.Length)
                return EndpointResult(e,detail,"Indeterminate","reflection_not_available",L("No valid independently selected reflection is available.","没有有效的已选反射波，不能判定近远端。"));
            double roundTrip;
            if(!IsFinite(cableLengthM)||cableLengthM<=0||!IsFinite(e.EndpointToleranceM)||e.EndpointToleranceM<0||
                (!MixedCableCalculation&&!velocityConfigured)||!TrySampleDeltaForPosition(mirrorLocation?cableLengthM:0,out roundTrip)||
                !IsFinite(roundTrip)||roundTrip<=0||!IsFinite(e.SampleLocationM))
                return EndpointResult(e,detail,"Indeterminate","geometry_unreliable",L("Reliable cable geometry and propagation speed are required.","需要可靠的电缆长度、波速及传播模型。"));
            bool atZero=Math.Abs(e.SampleLocationM)<=e.EndpointToleranceM;
            bool atLength=Math.Abs(e.SampleLocationM-cableLengthM)<=e.EndpointToleranceM;
            if(!atZero&&!atLength)
                return EndpointResult(e,detail,"NotApplicable","outside_endpoint_error_band",L("The measured position is outside both endpoint error bands.","采样定位不在两端的当前定位误差范围内，不适用端点识别。"));
            if(atZero&&atLength)
                return EndpointResult(e,detail,"Indeterminate","endpoint_error_bands_overlap",L("The configured endpoint error bands overlap.","当前误差范围使两端区间重叠，不能区分。"));
            double start=Safe(originalParameterRow,31,double.NaN),end=Safe(originalParameterRow,32,double.NaN);
            if(!IsSampleIndex(start)||!IsSampleIndex(end)||end<start||end-start+1!=pulse.Length||
                roundTrip>int.MaxValue-1||incident+(long)Math.Ceiling(roundTrip)>=pulse.Length)
                return EndpointResult(e,detail,"Indeterminate","raw_physical_window_incomplete",L("The original crop does not cover the complete cable propagation window.","原始波形截取不完整，未覆盖完整电缆传播范围。"));
            foreach(double value in pulse)if(!IsFinite(value))
                return EndpointResult(e,detail,"Indeterminate","raw_sample_gap",L("An acquired sample is missing; gaps are not filled for identification.","原始采样存在缺口，不补零进行端点判断。"));
            var cal=selectedProjectCalibration;
            if(!ProjectCalibrationEnabled||cal==null||!cal.Valid||cal.Wave==null)
                return EndpointResult(e,detail,"Indeterminate","comparable_project_calibration_missing",L("A traceable same-phase or shared ALL project CAL is required; legacy aligned curves do not prove provenance.","需要可追溯的同相或项目共用 ALL 校准；旧式对齐曲线不能证明来源。"));
            e.CalibrationPhase=cal.Phase; e.CalibrationSelection=projectSelectionStatus; e.CalibrationSamplingRateHz=cal.Rate; e.CalibrationSourcePath=cal.Path??"";
            if(cal.Phase!=e.PhysicalPhase&&cal.Phase!="ALL")
                return EndpointResult(e,detail,"Indeterminate","calibration_phase_not_comparable",L("A different physical phase is not a comparable endpoint reference.","其他明确相别的回退校准不作为可比端点判据。"));
            if(cal.Phase=="ALL") detail.AppendLine(L("Reference: project-shared ALL calibration (not represented as a same-phase CAL).","参考来源：项目共用 ALL 校准（不宣称为当前相专用 CAL）。"));
            double targetLength=CalibrationSelectionTotalLength(cableLengthM);
            if(!IsFinite(cal.Length)||cal.Length<=0||Math.Abs(cal.Length-targetLength)>Math.Max(1e-6,e.EndpointToleranceM)||
                !IsFinite(cal.Rate)||cal.Rate<6000000)
                return EndpointResult(e,detail,"Indeterminate","calibration_length_or_rate_not_comparable",L("CAL length or sampling rate is not comparable to this MAP.","CAL 电缆长度或采样率与当前 MAP 不具备可比性。"));
            // Reuse the same immutable raw reference cache, without changing the
            // display-only damping reason or invoking any confidence setter.
            var reference=CachedRawCalibrationReference(cal.Wave,cal.Incident,cal.Reflection,cal.Rate);
            if(reference==null||!reference.Available||!reference.NoiseReliable||!reference.IndependentCompletePackets||!reference.Unclipped)
            {
                detail.AppendLine("CAL: "+(reference==null?"unavailable":reference.Reason));
                return EndpointResult(e,detail,"Indeterminate","calibration_raw_quality_unreliable",L("CAL does not provide complete, independent, unclipped raw packets and reliable background.","CAL 缺少可靠背景、完整独立或未饱和的原始波包。"));
            }
            double expectedTravel=roundTrip/e.SamplingRateHz+2*connectingCableLengthM/150e6;
            double timingTolerance=Math.Max(2/e.SamplingRateHz,2/cal.Rate)+roundTrip/e.SamplingRateHz*acceptedPositionErrorPercent/100;
            if(Math.Abs(reference.TravelSeconds-expectedTravel)>timingTolerance)
                return EndpointResult(e,detail,"Indeterminate","calibration_travel_geometry_conflict",L("CAL travel time conflicts with the current cable/lead geometry.","CAL 传播时间与当前电缆或连接线几何矛盾。"));
            double noise=Safe(originalParameterRow,28,double.NaN);
            if(!IsFinite(noise)||noise<0)
                return EndpointResult(e,detail,"Indeterminate","raw_background_noise_unavailable",L("Original background-noise evidence is missing.","缺少原始背景噪声证据。"));
            var cbands=SeventhFixedBand(cal.Wave,cal.Rate,true); var bands=SeventhFixedBand(pulse,e.SamplingRateHz,false);
            int mid=incident+(reflection-incident)/2,cmid=reference.IncidentPeak+(reference.ReflectionPeak-reference.IncidentPeak)/2;
            int a0,a1,b0,b1,c0,c1,d0,d1;
            var a=MeasureManualSeventh(bands,incident,0,mid,noise,out a0,out a1);
            var b=MeasureManualSeventh(bands,reflection,mid+1,pulse.Length-1,noise,out b0,out b1);
            var ca=MeasureManualSeventh(cbands,reference.IncidentPeak,0,cmid,reference.NoiseSigma,out c0,out c1);
            var cb=MeasureManualSeventh(cbands,reference.ReflectionPeak,cmid+1,cal.Wave.Length-1,reference.NoiseSigma,out d0,out d1);
            EndpointRawMetrics(e,a.RawShape,b.RawShape,ca.RawShape);
            detail.AppendLine("PD guards: I["+a0+","+a1+"]; R["+b0+","+b1+"] / CAL guards: I["+c0+","+c1+"]; R["+d0+","+d1+"]");
            if(!a.Valid||!b.Valid||!ca.Valid||!cb.Valid||a1>=b0||c1>=d0)
            {
                detail.AppendLine("PD I="+a.Reason+"; PD R="+b.Reason+"; CAL I="+ca.Reason+"; CAL R="+cb.Reason);
                return EndpointResult(e,detail,"Indeterminate","independent_guarded_packets_unavailable",L("Both PD and CAL need independent complete packets with real quiet guards; boundaries were not expanded across neighbouring waves.","PD 与 CAL 均需带真实静背景的完整独立波包；不跨邻波扩大边界。"));
            }
            if(!EndpointUnclipped(pulse,a.MainPeak,bands.Baseline)||!EndpointUnclipped(pulse,b.MainPeak,bands.Baseline))
                return EndpointResult(e,detail,"Indeterminate","raw_peak_clipped_or_flat",L("A PD main peak is flat/clipped; raw shape evidence is unreliable.","PD 主峰存在平顶或饱和，原始形态证据不可靠。"));
            if(a.Sign!=b.Sign||ca.Sign!=cb.Sign)
                return EndpointResult(e,detail,"Indeterminate","pair_polarity_conflict",L("Incident/reflection principal-lobe polarities conflict.","入射与反射主瓣极性矛盾，不能强行判定近远端。"));
            if(!a.RawShape.Valid||!b.RawShape.Valid||!ca.RawShape.Valid||!cb.RawShape.Valid||
                !IsFinite(e.IncidentFwhmNs)||!IsFinite(e.CalibrationFwhmNs)||e.CalibrationFwhmNs<=0)
                return EndpointResult(e,detail,"Indeterminate","raw_shape_comparison_incomplete",L("Raw half-height crossings or following opposite-polarity lobes are incomplete.","原始半高宽交点或后续反向叶峰不足，无法完成形态比较。"));
            if(b.RawShape.Split&&(ca.RawShape.Split||cb.RawShape.Split))
                return EndpointResult(e,detail,"Indeterminate","split_already_present_in_calibration",L("CAL already contains a split principal lobe; the reflection split is not independent endpoint evidence.","CAL 已存在主瓣双峰，不能把反射双峰单独解释为远端证据。"));
            var decision=RawEndpointMath.Decide(a.RawShape,b.RawShape,ca.RawShape,ManufacturerEndpointPolicy);
            e.FarEngineeringScore=decision.FarScore;
            if(decision.Direction=="Indeterminate"&&(!IsFinite(e.IncidentMainOppositeRatio)||
                !IsFinite(e.CalibrationMainOppositeRatio)||e.CalibrationMainOppositeRatio<=0))
                return EndpointResult(e,detail,"Indeterminate","raw_shape_comparison_incomplete",
                    L("The following opposite-polarity lobe is incomplete; neither a width nor split rule resolved the endpoint.","后续反向叶峰不完整，脉宽及双峰证据也不足以判定端点。"));
            return EndpointResult(e,detail,decision.Direction,decision.Reason,decision.Direction=="Near"?
                L("Near-end morphology tendency (engineering policy; not an automatic acceptance).","近端形态倾向（工程判据，不代表自动接受）。"):
                decision.Direction=="Far"?L("Far-end morphology tendency (engineering policy; not an automatic acceptance).","远端形态倾向（工程判据，不代表自动接受）。"):
                L("Raw morphology falls in the indeterminate engineering band.","原始形态落在工程判据的不确定区间。"));
        }

        static void EndpointRawMetrics(EndpointIdentificationResult.Builder e,RawEndpointShape a,RawEndpointShape b,RawEndpointShape c)
        {
            if(a!=null&&a.Valid) {e.IncidentFwhmNs=a.FwhmNs;e.IncidentTemporalMainAmplitude=a.Main;e.IncidentFollowingOppositeAmplitude=a.Opposite;e.IncidentMainOppositeRatio=a.Ratio;}
            if(c!=null&&c.Valid) {e.CalibrationFwhmNs=c.FwhmNs;e.CalibrationMainOppositeRatio=c.Ratio;}
            e.WidthMultiple=e.CalibrationFwhmNs>0?e.IncidentFwhmNs/e.CalibrationFwhmNs:double.NaN;
            e.RatioRelativeToCalibration=e.CalibrationMainOppositeRatio>0?e.IncidentMainOppositeRatio/e.CalibrationMainOppositeRatio:double.NaN;
            e.ReflectionSplitEvidenceAvailable=b!=null&&b.Valid;
            e.ReflectionPrincipalLobeSplit=e.ReflectionSplitEvidenceAvailable&&b.Split;
        }
        static bool EndpointUnclipped(double[] values,int peak,double baseline)
        {
            if(peak<=0||peak>=values.Length-1)return false;
            double epsilon=Math.Max(1e-15,Math.Abs(values[peak]-baseline)*1e-12);int count=1;
            for(int i=peak-1;i>=0&&count<3&&Math.Abs(values[i]-values[peak])<=epsilon;i--)count++;
            for(int i=peak+1;i<values.Length&&count<3&&Math.Abs(values[i]-values[peak])<=epsilon;i++)count++;
            return count<3;
        }
        static string EndpointNumber(double n) { return IsFinite(n)?n.ToString("G6",CultureInfo.InvariantCulture):"—"; }
        EndpointIdentificationResult EndpointResult(EndpointIdentificationResult.Builder e,StringBuilder detail,string direction,string reason,string summary)
        {
            e.Direction=direction;e.Reason=reason;e.Summary=summary;
            var text=new StringBuilder(summary).AppendLine().AppendLine(reason);
            text.AppendLine(L("Raw, unfiltered evidence; baseline removal only. No position/confidence/acceptance changes.","证据来自原始未滤波波形，仅去除背景基线；不修改位置、确信度或接受状态。"));
            text.AppendLine("Phase="+e.PhysicalPhase+"; CAL="+e.CalibrationPhase+"; "+e.CalibrationSelection);
            text.AppendLine("CAL source: "+e.CalibrationSourcePath);
            text.AppendLine("Fs="+EndpointNumber(e.SamplingRateHz)+" Hz; CAL Fs="+EndpointNumber(e.CalibrationSamplingRateHz)+" Hz");
            text.AppendLine("L="+EndpointNumber(e.CableLengthM)+" m; x="+EndpointNumber(e.SampleLocationM)+" m; "+L("endpoint error","端点误差")+"=±"+EndpointNumber(e.EndpointToleranceM)+" m");
            text.AppendLine(L("Incident FWHM / CAL FWHM / multiple: ","入射半高宽 / CAL 半高宽 / 倍数：")+EndpointNumber(e.IncidentFwhmNs)+" ns / "+EndpointNumber(e.CalibrationFwhmNs)+" ns / "+EndpointNumber(e.WidthMultiple));
            text.AppendLine(L("Reflection principal-lobe split: ","反射主瓣双峰：")+(e.ReflectionSplitEvidenceAvailable?(e.ReflectionPrincipalLobeSplit?L("yes","是"):L("no","否")):"—"));
            text.AppendLine(L("Temporal main / following opposite amplitude: ","时序主叶峰 / 后续反向叶峰幅值：")+EndpointNumber(e.IncidentTemporalMainAmplitude)+" / "+EndpointNumber(e.IncidentFollowingOppositeAmplitude));
            text.AppendLine(L("Incident ratio / CAL ratio / relative ratio: ","入射主反叶峰比 / CAL 比值 / 相对比值：")+EndpointNumber(e.IncidentMainOppositeRatio)+" / "+EndpointNumber(e.CalibrationMainOppositeRatio)+" / "+EndpointNumber(e.RatioRelativeToCalibration));
            text.AppendLine(L("Engineering policy: width ≥2× CAL, ratio ≤CAL/1.5, or independent reflection split; 0.30/0.70 bands are NOT probabilities.","工程规则：半高宽≥CAL 2倍、主反叶峰比≤CAL/1.5，或独立反射主瓣双峰；0.30/0.70区间不是概率。"));
            text.AppendLine(L("Far-end engineering score: ","远端工程评分：")+EndpointNumber(e.FarEngineeringScore));
            text.Append(detail).AppendLine("Policy: "+e.PolicyVersion); e.EvidenceText=text.ToString();
            return new EndpointIdentificationResult(e);
        }
    }
}
