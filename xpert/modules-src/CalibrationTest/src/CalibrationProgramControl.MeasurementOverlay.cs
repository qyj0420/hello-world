using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Xiongxin.Calibration;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private RectangleF measurementInformationHit;
        private string measurementInformationText = String.Empty;
        private static string MeasurementValue(double value,string unit)
        { return (IsFinite(value) ? value.ToString(unit=="m"?"0.0":"0.###",CultureInfo.InvariantCulture) : "待确认")+" "+unit; }
        private string SignalToNoiseMetricCaption(double value)
        { return IsFinite(value) ? value.ToString("0.0",CultureInfo.InvariantCulture)+" dB" : CalibrationText("未能估计", "Not estimated"); }

        private string DisplayDecayExplanation(string status)
        {
            switch(status) {
                case "OppositePolarity": return CalibrationText("入射与反射峰极性相反，不绘制跨极性的指数阻尼曲线；原始波形与计时保留。", "Incident and reflection peaks have opposite polarity; no signed exponential decay is drawn. Original waveform and timing are retained.");
                case "NonDecaying": return CalibrationText("反射幅值不小于入射或有效拟合点不足，不能形成衰减曲线；不补造阻尼。", "Reflection is not smaller than the incident peak, or there are too few fitting points. No decay curve is fabricated.");
                case "UnknownPacket": return CalibrationText("所选到达时刻附近未能配对真实、已分辨的同极性脉冲峰；请手动核对反射及显示带宽。", "Resolved same-polarity pulse peaks could not be paired near the selected arrivals. Review the reflection and display bandwidth manually.");
                default: return CalibrationText("尚无可靠反射配对；可手动选择反射，也可仅保存入射电量系数，阻尼保持待确认。", "No reliable reflection pair. Select the reflection manually or save the incident charge coefficient only; decay remains pending.");
            }
        }

        private string[] MeasurementInformationLines(int phaseIndex,string level,PhaseState phase,WaveformData waveform,MarkerData marker,TdrAutoAnalysisResult analysis)
        {
            bool capacitor=IsCapacitiveLoad, paired=!capacitor&&marker!=null&&analysis!=null&&analysis.SearchStatus==TdrSearchStatus.Ok;
            double charge,coefficient=Double.NaN;
            if(TryChargePc(level,out charge)&&analysis!=null&&analysis.IncidentMagnitude>0)coefficient=charge/analysis.IncidentMagnitude;
            string saved; bool formal=savedLevelSignatures.TryGetValue(LiveKey(phaseIndex,level),out saved)&&saved==LevelSignature(phaseIndex,level);
            CalibrationCaptureResult capture; liveCaptures.TryGetValue(LiveKey(phaseIndex,level),out capture);
            if(capture!=null&&capture.Assessment!=null)coefficient=capture.Assessment.CoefficientPcPerV;
            ReflectionCandidateEvidence candidate; phase.ReflectionCandidates.TryGetValue(level,out candidate);
            var lines=new List<string>();
            lines.Add(level=="TDR"?"测距结果":"校准系数："+MeasurementValue(coefficient,"pC/V")+(formal?"（已保存）":"（待验证）"));
            lines.Add("校准质量分（采集）："+MeasurementScore(IncidentQualityEngineeringScore(capture)));
            lines.Add("反射可信分："+(capacitor?"不适用（电容）":paired?MeasurementScore(analysis.DisplayReflectionScore):candidate!=null?MeasurementScore(candidate.Score)+" · 低可信 / 待确认":"未检出"));
            lines.Add("信号质量参考（0–100），不代表测量准确率或合格结论");
            double incident=marker!=null?marker.StartTimeSeconds:analysis==null?Double.NaN:analysis.IncidentTimeSeconds;
            if(!capacitor) {
                lines.Add("时间差 Δt："+MeasurementValue(paired?(marker.DistanceMarkerSeconds-marker.StartTimeSeconds)*1e6:Double.NaN,"μs"));
                lines.Add("采用波速："+MeasurementValue(phase.VelocityMPerUs,"m/μs"));
                if(level!="TDR") {
                    var decay=paired?DisplayDecayNoLock(phase,level,waveform,marker,analysis):null;
                    lines.Add(CalibrationText("显示衰减系数：", "Display attenuation: ")+(decay==null?CalibrationText("待确认", "Pending"):decay.DecayConstantPerMicrosecond.ToString("0.###",CultureInfo.InvariantCulture))+" μs⁻¹");
                    if(decay==null)lines.Add(DisplayDecayExplanation(paired?DisplayDecayStatus:"Unavailable"));
                }
                lines.Add(level=="TDR" ? "测距长度："+MeasurementValue(paired?marker.DistanceMeters:Double.NaN,"m") : "电缆长度："+MeasurementValue(phase.CableLengthMeters,"m"));
                lines.Add("反射位置："+MeasurementValue(paired?marker.DistanceMarkerSeconds*1e6:Double.NaN,"μs"));
            } else lines.Add("电容校准：仅评估入射，不进行反射定位");
            lines.Add("入射位置："+MeasurementValue(incident*1e6,"μs"));
            if(!String.IsNullOrWhiteSpace(LastJointCommitWarning))lines.Add(LastJointCommitWarning);
            lines.Add((capture!=null&&capture.CoefficientProcessing=="HP10K_NO_LP"?"电量系数：10 kHz 高通，不施加低通；":"历史电量系数滤波口径待核对；")+ActualBandwidthText(phase)+"（定位显示）。");
            if(candidate!=null)lines.Add("反射候选："+MeasurementValue(candidate.Seconds*1e6,"μs")+"；"+candidate.Reason);
            if(capture!=null) {
                if(capture.QualityOverrideForReview)lines.Insert(0,"待复核：已按操作员选择继续计算，未通过校准质量检查。");
                if(capture.Assessment!=null) {
                    var q=capture.Assessment;
                    lines.Add("信噪比："+(IsFinite(q.SnrDb)?SignalToNoiseMetricCaption(q.SnrDb):"未能估计 / 待复测"));
                    if(q.CircuitSuspected) {
                        lines.Add("质量："+q.Quality+"；相对指定回路参考偏差 "+MeasurementValue(q.ReferenceDeviationFactor,"倍"));
                        lines.Add("指定参考预期峰值："+MeasurementValue(q.ExpectedPeakV,"V")+"；实测入射："+MeasurementValue(q.IncidentPeakV,"V"));
                        lines.Add(IsFinite(q.CrossLevelDeviationFactor)?"实际跨档对比偏差："+MeasurementValue(q.CrossLevelDeviationFactor,"倍"):"未进行实际跨档比较；请核对校准源、接线及回路参考适用性。");
                    } else lines.Add("质量："+q.Quality+"；"+q.Message);
                    lines.Add("质量分依据：SNR / 30 dB、脉冲数 / 8、重复峰值离散（≤10%满分）三项取最弱证据；保存失败最高59，回路异常最高49。");
                }
                if(!capacitor&&!paired)lines.Add("反射待确认："+(capture.Reflection==null?"缺少反射评估。":capture.Reflection.Message));
                if(!capacitor&&capture.Bandwidth!=null&&!capture.Bandwidth.Sufficient)lines.Add(capture.Bandwidth.Stable==true?
                    capture.Bandwidth.CutoffMHz>0?CalibrationText("已自动调整显示，请核对波形。", "Display adjusted automatically. Please review the waveform."):
                    CalibrationText("请核对波形。", "Please review the waveform."):
                    CalibrationText("请核对波形，必要时手动调整显示滤波。", "Please review the waveform and adjust the display filter manually if needed."));
            } else if(!capacitor&&!paired)lines.Add("反射待确认："+(analysis==null?"尚无反射评估。":analysis.Message));
            if(!capacitor)lines.Add("反射分依据：入射信噪35% + 反射信噪35% + 传播时差20% + 衰减拟合10%；候选另受形状相关约束，最高79。");
            if(!capacitor) {string timing=GetTimingStatus(phaseIndex,level);if(!String.IsNullOrEmpty(timing))lines.Insert(0,timing);}
            return lines.ToArray();
        }

        private void DrawMeasurementInformation(Graphics g,RectangleF plot,int phaseIndex,string level,PhaseState phase,WaveformData waveform,MarkerData marker,TdrAutoAnalysisResult analysis)
        {
            string[] lines=MeasurementInformationLines(phaseIndex,level,phase,waveform,marker,analysis);
            measurementInformationText=String.Join(Environment.NewLine,lines);
            bool small=plot.Width<450||plot.Height<235;
            double x0,x1,y0,y1;GetWaveformView(waveform,out x0,out x1,out y0,out y1);
            // This card is passive. The toolbar owns the sole assessment action,
            // so clicks/double-clicks in the waveform keep their normal meaning.
            int visible=small?4:(IsCapacitiveLoad?5:8);
            float width=Math.Min(plot.Width-16,Math.Min(420,Math.Max(180,plot.Width*.42f)));
            var heights=new List<float>();
            float height=34, maximumHeight=Math.Max(34,plot.Height-16);
            using(var font=ThemeFont(9.5f,FontStyle.Regular))
            using(var format=new StringFormat { FormatFlags=StringFormatFlags.LineLimit })
                for(int i=0;i<visible&&i<lines.Length;i++) {
                    float row=Math.Max(23,(float)Math.Ceiling(g.MeasureString(BuiltIn(lines[i]),font,(int)(width-14),format).Height)+3);
                    if(height+row>maximumHeight)break;
                    heights.Add(row);height+=row;
                }
            var box=new RectangleF(plot.Right-width-8,plot.Top+8,width,height);
            if(marker!=null&&x1>x0) box=PlaceMeasurementInformation(box,plot,marker,analysis,x0,x1,y0,y1);
            using(var fill=new SolidBrush(Color.FromArgb(235,255,255,255)))g.FillRectangle(fill,box);
            using(var pen=new Pen(Theme.Border))g.DrawRectangle(pen,box.X,box.Y,box.Width,box.Height);
            using(var font=ThemeFont(9.5f,FontStyle.Regular))
            using(var ink=new SolidBrush(Theme.Ink))
            using(var format=new StringFormat { FormatFlags=StringFormatFlags.LineLimit }) {
                float top=box.Top+4;
                for(int i=0;i<heights.Count;i++) {
                    g.DrawString(BuiltIn(lines[i]),font,ink,new RectangleF(box.Left+7,top,box.Width-14,heights[i]),format);
                    top+=heights[i];
                }
            }
            ThemeText(g,CalibrationText("详细依据见上方「校准评估」", "Details: Assessment above"),new RectangleF(box.Left+7,box.Bottom-27,box.Width-14,24),Theme.Muted,9f);
            measurementInformationHit=box;
            if(EmbeddedMode)measurementInformationHit.Offset(0,-40);
        }

        private static RectangleF PlaceMeasurementInformation(RectangleF box,RectangleF plot,MarkerData marker,TdrAutoAnalysisResult analysis,double x0,double x1,double y0,double y1)
        {
            var protectedAreas=new List<RectangleF>();
            float ix=plot.Left+(float)((marker.StartTimeSeconds-x0)/(x1-x0))*plot.Width;
            float rx=plot.Left+(float)((marker.DistanceMarkerSeconds-x0)/(x1-x0))*plot.Width;
            float vx=plot.Left+(float)((marker.VelocityMarkerSeconds-x0)/(x1-x0))*plot.Width;
            Func<float,float,float,float,RectangleF> badge=(x,y,w,h)=>new RectangleF(Math.Max(plot.Left+4,Math.Min(plot.Right-w-4,x)),Math.Max(plot.Top+4,Math.Min(plot.Bottom-h-4,y)),w,h);
            protectedAreas.Add(badge(ix+10,plot.Top+12,112,60));
            protectedAreas.Add(badge(rx-118,plot.Top+132,120,60));
            protectedAreas.Add(badge(vx+10,plot.Top+12,128,60));
            if(analysis!=null&&y1>y0) {
                foreach(var peak in new[]{new PointF(ix,plot.Bottom-(float)((analysis.IncidentValue-y0)/(y1-y0))*plot.Height),new PointF(rx,plot.Bottom-(float)((analysis.ReflectionValue-y0)/(y1-y0))*plot.Height)})
                    if(plot.Contains(peak))protectedAreas.Add(new RectangleF(peak.X-14,peak.Y-22,28,44));
            }
            var choices=new[]{box,new RectangleF(plot.Left+8,box.Top,box.Width,box.Height),
                new RectangleF(plot.Right-box.Width-8,plot.Bottom-box.Height-8,box.Width,box.Height),
                new RectangleF(plot.Left+8,plot.Bottom-box.Height-8,box.Width,box.Height)};
            // Prefer the original upper-right position when it does not obscure
            // incident/reflection badges or their actual peaks. On short pages,
            // choose the corner with the least evidence occlusion.
            return choices.OrderBy(r=>protectedAreas.Sum(p=>{var overlap=RectangleF.Intersect(r,p);return overlap.Width*overlap.Height;})).First();
        }
        private void ShowMeasurementAssessment()
        {
            // Refresh from current state instead of opening the last rendered
            // phase/level's text after a fast selection or a background capture.
            lock(stateLock) {
                int phaseIndex=selectedPhaseIndex;string level=selectedLevelCode;
                var phase=GetPhaseStateNoLock(phaseIndex);WaveformData wave=null;
                if(phase.WideBand)phase.FilteredWaveforms.TryGetValue(level,out wave);
                if(wave==null)phase.Waveforms.TryGetValue(level,out wave);
                if(wave==null)return;
                MarkerData marker;TdrAutoAnalysisResult analysis;
                phase.Markers.TryGetValue(level,out marker);phase.Analyses.TryGetValue(level,out analysis);
                measurementInformationText=String.Join(Environment.NewLine,MeasurementInformationLines(phaseIndex,level,phase,wave,marker,analysis));
            }
            using(var dialog=CreateMeasurementInformationDialog())dialog.ShowDialog(FindForm());
        }

        // A recognised incident remains useful even if a cable reflection is absent.
        // This result deliberately never claims a paired cable analysis succeeded.
        private TdrAutoAnalysisResult IncidentOnlyAnalysis(WaveformData wave,CalibrationCaptureResult capture)
        {
            var result=new TdrAutoAnalysisResult { SearchStatus=TdrSearchStatus.ReflectionNotFound,
                Message=IsCapacitiveLoad?"电容校准仅使用入射信号。":capture.Reflection==null?"反射尚未确认。":capture.Reflection.Message };
            var quality=capture.Assessment;
            if(quality==null||quality.IncidentPeakIndex<0||quality.IncidentPeakIndex>=wave.Samples.Length)return result;
            int index=quality.IncidentPeakIndex; double baseline=quality.BaselineV;
            if(!IsFinite(baseline))return result;
            result.IncidentIndex=index;result.IncidentTimeSeconds=wave.X0Seconds+index*wave.DxSeconds;
            result.IncidentValue=wave.Samples[index];result.Baseline=baseline;
            result.IncidentMagnitude=Math.Abs(wave.Samples[index]-baseline);result.NoiseSigma=quality.NoiseRmsV;
            if(UseFirstArrivalTiming) {
                double[] raw=capture.Average??wave.Samples;
                result.IncidentFirstArrivalIndex=CalibrationArrivalMath.Find(raw,index,0,Math.Min(raw.Length-1,index+128),256,baseline,quality.NoiseRmsV);
                result.IncidentTimeSeconds=result.IncidentFirstArrivalIndex<0?Double.NaN:wave.X0Seconds+result.IncidentFirstArrivalIndex*wave.DxSeconds;
            }
            return result;
        }

        private int compactLevelOffset;
        private RectangleF CompactLevelListBounds(CompactGeometry ui)
        {
            float bottom=(IsCapacitiveLoad?ui.Save.Top:ui.ConfirmJoints.Top)-8;
            return new RectangleF(ui.Level.Left,ui.AllLevels.Bottom+4,ui.Level.Width,Math.Max(0,bottom-ui.AllLevels.Bottom-4));
        }
        private int CompactLevelRows(RectangleF area) { return Math.Max(0,(int)((area.Height-24)/28)); }
        private void DrawCompactLevelList(Graphics g,CompactGeometry ui,string selected)
        {
            RectangleF area=CompactLevelListBounds(ui);int rows=CompactLevelRows(area);if(rows==0)return;
            string[] values=GetVisibleLevelCodes();compactLevelOffset=Math.Max(0,Math.Min(compactLevelOffset,Math.Max(0,values.Length-rows)));
            ThemeBox(g,area,Theme.Surface,Theme.Border);
            for(int i=0;i<rows&&compactLevelOffset+i<values.Length;i++) {
                string code=values[compactLevelOffset+i];
                RectangleF row=new RectangleF(area.Left+2,area.Top+2+i*28,area.Width-18,27);
                DrawCompactButton(g,row,"",code==selected,fontSize:9f);
                string state=GetLevelSaveState(SelectedPhaseIndex,code);
                Color lamp=state=="Saved"?Color.FromArgb(35,138,103):state=="Empty"?Theme.Muted:Color.FromArgb(191,131,24);
                ThemeText(g,LevelLabels[Array.IndexOf(LevelCodes,code)],new RectangleF(row.Left+5,row.Top,70,row.Height),code==selected?Color.White:Theme.Ink,8.5f);
                using(var brush=new SolidBrush(lamp))g.FillEllipse(brush,row.Left+80,row.Top+9,9,9);
                ThemeText(g,LevelSaveStateCaption(state),new RectangleF(row.Left+94,row.Top,row.Width-96,row.Height),code==selected?Color.White:Theme.Ink,8f);
            }
            if(values.Length>rows) {
                float track=Math.Max(1,rows*28),thumb=track*rows/values.Length;
                using(var brush=new SolidBrush(Theme.Border))g.FillRectangle(brush,area.Right-12,area.Top+2,8,track);
                using(var brush=new SolidBrush(Theme.Accent))g.FillRectangle(brush,area.Right-12,area.Top+2+(track-thumb)*compactLevelOffset/(values.Length-rows),8,thumb);
            }
            ThemeText(g,values.Length>rows?CalibrationText("滚轮查看更多档位","Scroll for more levels"):CalibrationText("点击档位切换","Select a level"),new RectangleF(area.Left+3,area.Bottom-22,area.Width-6,20),Theme.Muted,8f,center:true);
        }
        private bool HandleCompactLevelWheel(MouseEventArgs e)
        {
            if(!UseCompactLayout||!CompactLevelListBounds(CompactLayout).Contains(e.Location))return false;
            int rows=CompactLevelRows(CompactLevelListBounds(CompactLayout));
            compactLevelOffset=Math.Max(0,Math.Min(Math.Max(0,GetVisibleLevelCodes().Length-rows),compactLevelOffset+(e.Delta>0?-1:1)));
            Invalidate();return true;
        }
        private bool HandleCompactLevelClick(PointF point,CompactGeometry ui)
        {
            RectangleF area=CompactLevelListBounds(ui);if(!area.Contains(point))return false;
            int rows=CompactLevelRows(area);string[] values=GetVisibleLevelCodes();
            if(point.Y>=area.Bottom-24||point.X>=area.Right-16) {
                compactLevelOffset=Math.Max(0,Math.Min(Math.Max(0,values.Length-rows),compactLevelOffset+(point.Y<area.Top+area.Height/2||point.Y>=area.Bottom-24&&point.X<area.Left+area.Width/2?-1:1)));
                Invalidate();return true;
            }
            int row=(int)((point.Y-area.Top-2)/28),at=compactLevelOffset+row;
            if(row>=0&&row<rows&&at<values.Length)ExecuteParameterAction("LEVEL",delegate{SetSelectedLevel(values[at]);});
            return true;
        }
    }
}
