using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.SignalProcessing;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        public double NoiseSimilarityTolerancePercent{get;set;}=1;
        private WaveformData noiseViewSource;
        private double noiseX0,noiseX1,noiseY0,noiseY1;
        private bool noiseSelecting;
        private int noiseAnchor,noiseSelectionStart=-1,noiseSelectionEnd=-1,noiseSelectionPeak=-1;
        private NoisePulseTemplate[] noiseTemplates=new NoisePulseTemplate[0];
        private string noiseSourceHash;
        private PointF[] noiseRenderCache;
        private RectangleF noiseRenderPlot;
        private double[] noiseRenderBounds;
        private ContextMenuStrip noiseMenu;
        private ToolTip noiseTooltip;
        private bool noiseTemplateSaving;
        private System.Windows.Forms.Timer noiseBusyTimer;
        private int noiseBusyFrame;
        private void UpdateNoiseBusyFeedback()
        {
            if(IsDisposed||Disposing)return;
            if(noiseTemplateSaving||noiseMatchBusy) {
                if(noiseBusyTimer==null) {
                    noiseBusyTimer=new System.Windows.Forms.Timer{Interval=100};
                    noiseBusyTimer.Tick+=delegate{noiseBusyFrame=(noiseBusyFrame+1)%20;Invalidate();};
                }
                noiseBusyTimer.Start();
            } else if(noiseBusyTimer!=null)noiseBusyTimer.Stop();
            Invalidate();
        }
        private void DrawNoiseBusyFeedback(Graphics g,RectangleF plot)
        {
            var panel=new RectangleF(plot.Left+10,plot.Top+18,Math.Min(540,plot.Width-20),54);
            using(var fill=new SolidBrush(Color.FromArgb(245,255,255,255)))g.FillRectangle(fill,panel);
            using(var border=new Pen(Theme.Border))g.DrawRectangle(border,panel.X,panel.Y,panel.Width,panel.Height);
            ThemeText(g,noiseTemplateSaving?CalibrationText("正在准备干扰模板…","Preparing interference template…"):
                CalibrationText("正在匹配干扰脉冲…","Matching interference pulses…"),
                new RectangleF(panel.Left+10,panel.Top+4,panel.Width-20,28),Theme.Ink,10f);
            var track=new RectangleF(panel.Left+10,panel.Bottom-13,panel.Width-20,5);
            using(var bg=new SolidBrush(Theme.Border))g.FillRectangle(bg,track);
            float width=Math.Max(10,track.Width*.22f),travel=track.Width-width;
            float fraction=noiseBusyFrame<=10?noiseBusyFrame/10f:(20-noiseBusyFrame)/10f;
            using(var bar=new SolidBrush(Theme.Accent))g.FillRectangle(bar,track.Left+travel*fraction,track.Top,width,track.Height);
        }
        private RectangleF NoiseResetBounds(CompactGeometry ui){float width=Math.Min(145,(ui.Center.Width-40)*.2f);return new RectangleF(NoiseThresholdCheckBounds(ui).Left-width-8,ui.Center.Top+53,width,28);}
        private RectangleF LegacyNoiseResetBounds{get{return new RectangleF(1065F,signalQualityPanelTop+29F,140F,28F);}}
        private void ResetNoiseView(){CancelNoiseGesture();noiseViewSource=null;Invalidate();}
        private void DisposeNoiseInteraction()
        {if(noiseMatchCancellation!=null)noiseMatchCancellation.Cancel();if(noiseBusyTimer!=null){noiseBusyTimer.Stop();noiseBusyTimer.Dispose();noiseBusyTimer=null;}if(noiseMenu!=null){noiseMenu.Dispose();noiseMenu=null;}if(noiseTooltip!=null){noiseTooltip.Dispose();noiseTooltip=null;}}
        private void LoadNoiseTemplates()
        {
            CancelNoiseGesture();manualNoiseThresholds.Clear();automaticNoiseThresholds.Clear();
            noiseFilteredWave=null;noiseFilteredSamples=null;
            noiseViewSource=null;noiseSelectionStart=noiseSelectionEnd=noiseSelectionPeak=-1;
            try{noiseTemplates=String.IsNullOrEmpty(ProjectPath)?new NoisePulseTemplate[0]:NoisePulseTemplates.Load(ProjectPath,SimulationMode);}
            catch(Exception ex){noiseTemplates=new NoisePulseTemplate[0];lastCalibrationError=RecordCalibrationCustomerFailure("Noise template load",ex);}
        }
        private void EnsureNoiseView(WaveformData wave)
        {
            if(wave==noiseViewSource)return;if(NoiseGestureActive)CancelNoiseGesture();noiseViewSource=wave;
            noiseSourceHash=NoisePulseTemplates.WaveformHash(wave.Samples,wave.X0Seconds,wave.DxSeconds);
            noiseRenderCache=null;
            noiseX0=wave.X0Seconds;noiseX1=wave.X0Seconds+(wave.Samples.Length-1)*wave.DxSeconds;
            double peak=wave.Samples.Length==0?.2:Math.Max(1e-6,wave.Samples.Max(v=>Math.Abs(v)))*1.08;
            noiseY0=-peak;noiseY1=peak;noiseSelectionStart=noiseSelectionEnd=noiseSelectionPeak=-1;
        }
        private int NoiseSample(float x,WaveformData wave)
        {
            double t=noiseX0+(x-CurrentNoisePlotRectangle.Left)/CurrentNoisePlotRectangle.Width*(noiseX1-noiseX0);
            return Math.Max(0,Math.Min(wave.Samples.Length-1,(int)Math.Round((t-wave.X0Seconds)/wave.DxSeconds)));
        }
        private bool HandleNoiseDown(MouseEventArgs e)
        {
            PointF point;if(UseCompactLayout&&(!compactQualityTab||compactLevelsOpen)||!TryReferencePoint(e.Location,out point)||!CurrentNoisePlotRectangle.Contains(point))return false;
            var wave=GetPhaseStateNoLock(SelectedPhaseIndex).Noise;if(wave==null)return true;EnsureNoiseView(wave);
            if(e.Button==MouseButtons.Left){
                Focus();noiseGesturePhase=SelectedPhaseIndex;noiseGestureStart=noiseGestureEnd=point;
                if(HitNoiseThreshold(point)) {
                    noiseThresholdDragging=true;noiseOriginalThreshold=ManualNoiseThreshold(SelectedPhaseIndex);
                    double signed=NoiseValue(point.Y)<0?-DisplayNoiseThreshold:DisplayNoiseThreshold;
                    noiseThresholdGrabOffset=NoiseValue(point.Y)-signed;Cursor=Cursors.SizeNS;
                } else if(noiseTemplateMode) {
                    noiseSelecting=true;noiseAnchor=NoiseSample(point.X,wave);noiseSelectionStart=noiseSelectionEnd=noiseAnchor;Cursor=Cursors.Cross;
                } else {noiseZooming=true;Cursor=Cursors.Cross;}
                Capture=true;Invalidate();
            }
            else if(e.Button==MouseButtons.Right)ShowNoiseMenu(e.Location,wave);
            return true;
        }
        private bool HandleNoiseMove(MouseEventArgs e)
        {
            if(!NoiseGestureActive||noiseViewSource==null)return false;
            if(IsBusy||(e.Button&MouseButtons.Left)==0||noiseGesturePhase!=SelectedPhaseIndex||noiseViewSource!=GetPhaseStateNoLock(SelectedPhaseIndex).Noise){CancelNoiseGesture();return true;}
            PointF point=NoiseGesturePoint(e.Location);noiseGestureEnd=point;
            if(noiseThresholdDragging) {
                if(Math.Abs(point.Y-noiseGestureStart.Y)>=3)manualNoiseThresholds[noiseGesturePhase]=Math.Max(1e-12,Math.Abs(NoiseValue(point.Y)-noiseThresholdGrabOffset));
                Invalidate();return true;
            }
            if(noiseZooming){Invalidate();return true;}
            int at=NoiseSample(point.X,noiseViewSource);noiseSelectionStart=Math.Min(noiseAnchor,at);noiseSelectionEnd=Math.Max(noiseAnchor,at);Invalidate();return true;
        }
        private bool HandleNoiseUp(MouseEventArgs e)
        {
            if(!NoiseGestureActive)return false;if(e.Button!=MouseButtons.Left)return true;
            HandleNoiseMove(e);
            if(noiseZooming)ApplyNoiseBoxZoom();
            if(noiseSelecting)SelectNoiseEnvelope(noiseViewSource,noiseSelectionStart,noiseSelectionEnd);
            noiseThresholdDragging=noiseZooming=noiseSelecting=false;Capture=false;Cursor=Cursors.Default;Invalidate();return true;
        }
        private void SelectNoiseEnvelope(WaveformData wave,int first,int last)
        {
            int peak=first;for(int i=first+1;i<=last;i++)if(Math.Abs(wave.Samples[i])>Math.Abs(wave.Samples[peak]))peak=i;
            double gate=Math.Max(1e-12,Math.Abs(wave.Samples[peak])*.03);int left=peak,right=peak,quiet=0;
            while(left>0&&peak-left<32760){left--;quiet=Math.Abs(wave.Samples[left])<gate?quiet+1:0;if(quiet>=16)break;}
            quiet=0;while(right<wave.Samples.Length-1&&right-peak<32760){right++;quiet=Math.Abs(wave.Samples[right])<gate?quiet+1:0;if(quiet>=16)break;}
            noiseSelectionStart=left;noiseSelectionEnd=right;noiseSelectionPeak=peak;
        }
        private void ShowNoiseMenu(Point point,WaveformData wave)
        {
            if(IsDisposed||Disposing||wave==null)return;
            PointF reference;TryReferencePoint(point,out reference);
            double at=wave.X0Seconds+NoiseSample(reference.X,wave)*wave.DxSeconds;
            var hit=noiseTemplates.LastOrDefault(t=>t.SourceHash==noiseSourceHash&&(at>=t.SourceStartSeconds&&at<=t.SourceEndSeconds||NoiseMarkerHit(reference,t)));
            // Closed is raised inside ToolStrip's click/close stack. Synchronous
            // disposal there invalidates the menu still dispatching that click.
            // Keep one owner-held menu and dispose only with this control.
            if(noiseMenu==null||noiseMenu.IsDisposed)noiseMenu=new ContextMenuStrip{Name="CalibrationNoiseContextMenu"};
            var menu=noiseMenu;menu.Close();while(menu.Items.Count>0){var item=menu.Items[0];menu.Items.RemoveAt(0);item.Dispose();}
            string contextProject=ProjectPath;bool contextSimulation=SimulationMode;int contextPhase=SelectedPhaseIndex;string contextHash=noiseSourceHash;
            Func<bool> sameContext=()=>!IsDisposed&&!Disposing&&contextProject==ProjectPath&&contextSimulation==SimulationMode&&contextPhase==SelectedPhaseIndex&&wave==GetPhaseStateNoLock(SelectedPhaseIndex).Noise;
            if(hit!=null)menu.Items.Add("删除过滤（保留原始波形）",null,delegate {
                if(!sameContext()||noiseTemplateSaving)return;
                try {var next=noiseTemplates.Where(t=>t.Id!=hit.Id).ToArray();NoisePulseTemplates.Save(ProjectPath,SimulationMode,next);noiseTemplates=next;Invalidate();}
                catch(Exception ex){ShowCalibrationCustomerFailure(FindForm(),"Noise filter removal",ex,"过滤未删除","Filter not deleted");}
            });
            int selectedFirst=noiseSelectionStart,selectedLast=noiseSelectionEnd,selectedPeak=noiseSelectionPeak;
            if(noiseSelectionPeak>=0)menu.Items.Add("添入过滤 · 相似度 ≥ "+(100-NoiseSimilarityTolerancePercent).ToString("0.##")+"%",null,async delegate {
                if(!sameContext()||noiseTemplateSaving)return;noiseTemplateSaving=true;UpdateNoiseBusyFeedback();
                int first=selectedFirst,last=selectedLast,peak=selectedPeak;string project=contextProject;bool simulation=contextSimulation;
                try {
                    var filtered=await NoiseHighPassedSamplesAsync(wave);
                    if(!sameContext())return;
                    noiseFilteredWave=wave;noiseFilteredSamples=filtered;
                    if(first<PdLinearHighPass.GuardSamples(1/wave.DxSeconds)||last>=wave.Samples.Length-PdLinearHighPass.GuardSamples(1/wave.DxSeconds))throw new InvalidOperationException("所选脉冲过于接近记录边界，请选择有完整前后包络的脉冲。");
                    var item=new NoisePulseTemplate{Id=Guid.NewGuid().ToString("N"),CreatedUtc=DateTime.UtcNow.ToString("o"),FilterId=PdLinearHighPass.FilterId,
                        SampleRateHz=1/wave.DxSeconds,PeakIndex=peak-first,Samples=filtered.Skip(first).Take(last-first+1).ToArray(),SourceHash=contextHash,
                        SourceStartSeconds=wave.X0Seconds+first*wave.DxSeconds,SourceEndSeconds=wave.X0Seconds+last*wave.DxSeconds};
                    var next=noiseTemplates.Concat(new[]{item}).ToArray();NoisePulseTemplates.Save(project,simulation,next);noiseTemplates=next;Invalidate();
                }catch(Exception ex){ShowCalibrationCustomerFailure(FindForm(),"Noise filter addition",ex,"过滤未添加","Filter not added");}
                finally{noiseTemplateSaving=false;UpdateNoiseBusyFeedback();}
            });
            if(noiseTemplates.Length>0) {
                var manage=new ToolStripMenuItem("已启用的干扰模板（点击撤销）");menu.Items.Add(manage);
                foreach(var template in noiseTemplates) {var target=template;
                    manage.DropDownItems.Add(target.CreatedUtc+" · "+target.Samples.Length+" 点",null,delegate {
                        if(!sameContext()||noiseTemplateSaving)return;
                        try{var next=noiseTemplates.Where(t=>t.Id!=target.Id).ToArray();NoisePulseTemplates.Save(ProjectPath,SimulationMode,next);noiseTemplates=next;Invalidate();}
                        catch(Exception ex){ShowCalibrationCustomerFailure(FindForm(),"Noise filter removal",ex,"过滤未删除","Filter not deleted");}
                    });
                }
            }
            menu.Items.Add(CalibrationText("拖动放大","Drag to zoom"),null,delegate{if(sameContext()){noiseTemplateMode=false;Invalidate();}});
            menu.Items.Add(CalibrationText("选取干扰脉冲","Select interference pulse"),null,delegate{if(sameContext()){noiseTemplateMode=true;Invalidate();}});
            menu.Items.Add(CalibrationText("恢复自动阈值","Restore automatic threshold"),null,delegate{if(sameContext())RestoreAutomaticNoiseThreshold();});
            menu.Items.Add("恢复原始视图",null,delegate{if(sameContext())ResetNoiseView();});
            menu.Show(this,point);
        }
        private bool HandleNoiseWheel(MouseEventArgs e)
        {
            if(NoiseGestureActive)return true;
            PointF point;if(UseCompactLayout&&(!compactQualityTab||compactLevelsOpen)||!TryReferencePoint(e.Location,out point)||!CurrentNoisePlotRectangle.Contains(point))return false;
            var wave=GetPhaseStateNoLock(SelectedPhaseIndex).Noise;if(wave==null)return true;EnsureNoiseView(wave);
            ZoomNoise(point,e.Delta,(ModifierKeys&Keys.Shift)!=0,wave);
            Invalidate();return true;
        }
        private void ZoomNoise(PointF point,int delta,bool vertical,WaveformData wave)
        {
            double factor=delta>0?.82:1/.82;
            if(vertical){double anchor=noiseY1-(point.Y-CurrentNoisePlotRectangle.Top)/CurrentNoisePlotRectangle.Height*(noiseY1-noiseY0);double span=Math.Max(1e-12,Math.Min(1e12,(noiseY1-noiseY0)*factor));double ratio=(anchor-noiseY0)/(noiseY1-noiseY0);noiseY0=anchor-ratio*span;noiseY1=noiseY0+span;}
            else{double anchor=noiseX0+(point.X-CurrentNoisePlotRectangle.Left)/CurrentNoisePlotRectangle.Width*(noiseX1-noiseX0);noiseX0=Math.Max(wave.X0Seconds,anchor+(noiseX0-anchor)*factor);noiseX1=Math.Min(wave.X0Seconds+(wave.Samples.Length-1)*wave.DxSeconds,anchor+(noiseX1-anchor)*factor);if(noiseX1-noiseX0<wave.DxSeconds*4)noiseX1=Math.Min(wave.X0Seconds+(wave.Samples.Length-1)*wave.DxSeconds,noiseX0+wave.DxSeconds*4);}
        }
        private void DrawInteractiveNoise(Graphics g,WaveformData wave)
        {
            EnsureNoiseView(wave);RectangleF plot=CurrentNoisePlotRectangle;DrawWaveformAxes(g,plot,noiseX0,noiseX1,noiseY0,noiseY1);
            var bounds=new[]{noiseX0,noiseX1,noiseY0,noiseY1};
            if(noiseRenderCache==null||noiseRenderPlot!=plot||!noiseRenderBounds.SequenceEqual(bounds)) {
            int first=NoiseSample(plot.Left,wave),last=NoiseSample(plot.Right,wave),bins=Math.Max(1,(int)plot.Width);var points=new List<PointF>();
            for(int bin=0;bin<bins;bin++) {
                int from=first+(int)((long)(last-first)*bin/bins),to=first+(int)((long)(last-first)*(bin+1)/bins);
                double lo=wave.Samples[from],hi=lo;for(int i=from+1;i<=to;i++){lo=Math.Min(lo,wave.Samples[i]);hi=Math.Max(hi,wave.Samples[i]);}
                float x=plot.Left+bin;points.Add(new PointF(x,plot.Bottom-(float)((lo-noiseY0)/(noiseY1-noiseY0))*plot.Height));points.Add(new PointF(x,plot.Bottom-(float)((hi-noiseY0)/(noiseY1-noiseY0))*plot.Height));
            }
            noiseRenderCache=points.ToArray();noiseRenderPlot=plot;noiseRenderBounds=bounds;
            }
            using(var pen=new Pen(Theme.Accent,1))DrawClippedPolyline(g,plot,pen,noiseRenderCache);
            EnsureNoiseMatchOverlay(wave);
            foreach(var match in noiseMatchResults) {
                DrawNoiseShade(g,plot,wave.X0Seconds+match.First*wave.DxSeconds,wave.X0Seconds+match.Last*wave.DxSeconds,Color.FromArgb(28,195,70,70));
                DrawNoiseMarkers(g,plot,wave.X0Seconds+match.Peak*wave.DxSeconds,Color.FromArgb(190,130,50));
            }
            foreach(var t in noiseTemplates.Where(t=>t.SourceHash==noiseSourceHash)){DrawNoiseShade(g,plot,t.SourceStartSeconds,t.SourceEndSeconds,Color.FromArgb(48,195,70,70));DrawNoiseMarkers(g,plot,t.SourceStartSeconds+t.PeakIndex/t.SampleRateHz,Color.Firebrick);}
            if(noiseSelectionStart>=0)DrawNoiseShade(g,plot,wave.X0Seconds+noiseSelectionStart*wave.DxSeconds,wave.X0Seconds+noiseSelectionEnd*wave.DxSeconds,Color.FromArgb(60,0,163,199));
            DrawNoiseZoomSelection(g,plot);
            if(noiseTemplateSaving||noiseMatchBusy)DrawNoiseBusyFeedback(g,plot);
            else if(noiseTemplates.Length>0)ThemeText(g,noiseMatchStatus,new RectangleF(plot.Left+10,plot.Top+18,Math.Min(650,plot.Width-20),26),Theme.Ink,8.5f);
        }
        private void DrawNoiseShade(Graphics g,RectangleF plot,double from,double to,Color color)
        {
            float left=Math.Max(plot.Left,plot.Left+(float)((from-noiseX0)/(noiseX1-noiseX0))*plot.Width),right=Math.Min(plot.Right,plot.Left+(float)((to-noiseX0)/(noiseX1-noiseX0))*plot.Width);
            if(right<=left)return;using(var brush=new SolidBrush(color))g.FillRectangle(brush,left,plot.Top,Math.Max(2,right-left),plot.Height);
        }
        private void DrawNoiseMarkers(Graphics g,RectangleF plot,double time,Color color)
        {
            if(time<noiseX0||time>noiseX1)return;float x=plot.Left+(float)((time-noiseX0)/(noiseX1-noiseX0))*plot.Width;
            using(var b=new SolidBrush(color)){g.FillPolygon(b,new[]{new PointF(x-6,plot.Top+2),new PointF(x+6,plot.Top+2),new PointF(x,plot.Top+12)});g.FillPolygon(b,new[]{new PointF(x-6,plot.Bottom-2),new PointF(x+6,plot.Bottom-2),new PointF(x,plot.Bottom-12)});}
        }
        private bool NoiseMarkerHit(PointF point,NoisePulseTemplate template)
        {
            var plot=CurrentNoisePlotRectangle;double time=template.SourceStartSeconds+template.PeakIndex/template.SampleRateHz;
            if(time<noiseX0||time>noiseX1)return false;float x=plot.Left+(float)((time-noiseX0)/(noiseX1-noiseX0))*plot.Width;
            return Math.Abs(point.X-x)<=8&&(point.Y>=plot.Top&&point.Y<=plot.Top+16||point.Y<=plot.Bottom&&point.Y>=plot.Bottom-16);
        }
        private void UpdateNoiseHover(Point point)
        {
            if(noiseTooltip==null)noiseTooltip=new ToolTip{InitialDelay=200,ReshowDelay=100,AutoPopDelay=10000};
            PointF reference;TryReferencePoint(point,out reference);
            var hit=noiseTemplates.LastOrDefault(t=>t.SourceHash==noiseSourceHash&&NoiseMarkerHit(reference,t));
            string caption=hit==null?null:"干扰过滤区域："+(hit.SourceStartSeconds*1000).ToString("0.######")+"–"+(hit.SourceEndSeconds*1000).ToString("0.######")+" ms\n右键可删除过滤；原始波形保留。";
            if(UseCompactLayout&&compactQualityTab&&NoiseThresholdCheckBounds(CompactLayout).Contains(reference)||CurrentNoisePlotRectangle.Contains(reference)) {
                if(hit==null)caption=CalibrationText("拖动阈值线调整下一次采集的脉冲筛选幅值；在线外按当前模式拖动放大或选取干扰。滚轮缩放时间，Shift＋滚轮缩放幅值。", "Drag a threshold line to set pulse screening for the next capture. Away from the line, drag to zoom or select interference in the selected mode. Wheel: time; Shift+wheel: amplitude.");
                Cursor=HitNoiseThreshold(reference)?Cursors.SizeNS:Cursors.Cross;
            }
            if(noiseTooltip.GetToolTip(this)!=caption)noiseTooltip.SetToolTip(this,caption);
        }
    }
}
