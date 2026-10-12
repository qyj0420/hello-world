using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    internal enum PressureManualRangeChoice { Dismiss, Adjust, Ignore, Save }

    public sealed class PressureManualRangeDecisionRecord
    {
        public string Schema="HVX.Pressure.ManualOverRange/1",AcquisitionId,Choice,RawSha256,QualitySha256,OriginalQualitySha256;
        public int CapturedRangeIndex;
        public long RequestSequence;
        public DateTime RecordedUtc;
        public bool DataValid=false,StartsHardware=false,AutomaticallyChangesRange=false;
        public LocationRangeResult Quality,OriginalQuality;
    }

    public sealed partial class PressureTestControl
    {
        internal Func<bool,PressureManualRangeChoice> ManualPressureRangeChoiceForTests;
        string manualRiskAcquisition="",manualExplicitSaveAcquisition="",manualRiskQualityHash="",manualRiskOriginalHash="";
        PressureZeroVoltageResult manualRiskSource;
        long manualRiskRequest;
        int manualHigherRangeRequired=-1;
        string manualHigherRangeProject="",manualHigherRangePhase="",manualHigherRangeAcquisition="";

        [Browsable(false)] public bool CurrentRoundExplicitSave
        { get { return cycleAcquisitionId.Length>0&&manualExplicitSaveAcquisition==cycleAcquisitionId; } }
        [Browsable(false)] public bool CurrentRoundManualOverRangeWarning
        { get { return ManualRiskSaveIsCurrent(lastPressureCapture,lastPressureAcquisitionId); } }
        string ManualOverRangeWarningText()
        {return PressureUiText("超量程数据已保存；峰值及定位未验证。","Overrange data saved; peak and location are unverified.");}

        bool ManualRiskSaveIsCurrent(PressureZeroVoltageResult source,string id)
        {
            return source!=null&&source.Capture!=null&&source.Capture.Settings!=null&&
                Object.ReferenceEquals(source,manualRiskSource)&&Object.ReferenceEquals(source,lastPressureCapture)&&
                id.Length>0&&id==cycleAcquisitionId&&id==manualRiskAcquisition&&id==lastPressureAcquisitionId&&
                manualRiskRequest==pressureRequestSequence&&lastAcquisitionOverRange&&
                cycleProject==ProjectPath&&cyclePhase==SelectedPhaseCode&&cycleSimulation==SimulationMode&&
                source.Capture.Settings.AutoRange==false&&manualRiskQualityHash==PressureOverflowUpscale.QualityHash(source.Quality)&&
                manualRiskOriginalHash==PressureOverflowUpscale.QualityHash(source.OriginalRangeQuality??source.Quality);
        }
        void ResetManualRoundSave()
        {
            manualRiskAcquisition=manualExplicitSaveAcquisition=manualRiskQualityHash=manualRiskOriginalHash="";
            manualRiskSource=null;manualRiskRequest=0;
        }
        bool AdoptCurrentManualOverRangeForSave()
        {
            var source=lastPressureCapture;string id=lastPressureAcquisitionId;
            if(automaticRange||source==null||source.Capture==null||source.Capture.Settings==null||source.Capture.Settings.AutoRange||source.Quality==null||
                id.Length==0||id!=cycleAcquisitionId||!lastAcquisitionOverRange||IsBusy||HasActiveHardwareSession||
                pressureStateCode!="COMPLETE"||operatorPressureStopRequested||finishCurrentRoundRequested||automaticPlanInterrupted||pressureEmergencyLatched||
                !CanRequestVoltageNoLock(requestedTargetPeakVoltageKv)||cyclePhase!=SelectedPhaseCode||cycleProject!=ProjectPath)return false;
            manualRiskAcquisition=id;manualRiskSource=source;manualRiskRequest=pressureRequestSequence;
            manualRiskQualityHash=PressureOverflowUpscale.QualityHash(source.Quality);
            manualRiskOriginalHash=PressureOverflowUpscale.QualityHash(source.OriginalRangeQuality??source.Quality);
            manualExplicitSaveAcquisition=id;overRangeIgnored=true;return true;
        }
        bool AllowExplicitManualRangeRestart()
        {
            if(manualHigherRangeRequired<0)return true;
            if(manualHigherRangeProject!=ProjectPath||manualHigherRangePhase!=SelectedPhaseCode||manualHigherRangeAcquisition!=cycleAcquisitionId){manualHigherRangeRequired=-1;return true;}
            // The pending manual choice must not require an unavailable manual
            // selector in automatic mode. A fresh click still passes the normal
            // request/interlock path; retain this requirement if the operator
            // returns to manual mode before a new acquisition actually starts.
            if(automaticRange)return true;
            if(!automaticRange&&SelectedPicoRangeCode>manualHigherRangeRequired){manualHigherRangeRequired=-1;return true;}
            lastPostProcessError=PressureUiText("请调高量程，再点击加压。","Select a higher range, then press Start.");
            ShowPressureRangeReview(lastPressureCapture==null?null:lastPressureCapture.RangeReview,lastPostProcessError);
            return false;
        }
        void RefreshManualRangeRestartForAutomaticMode()
        {
            if(manualHigherRangeRequired<0||!automaticRange)return;
            lastPostProcessError=String.Empty;
            ShowPressureRangeReview(lastPressureCapture==null?null:lastPressureCapture.RangeReview,
                PressureUiText("本段未保存。请重新开始加压。","This segment was not saved. Press Start for a new round."));
        }
        async Task<string> ReviewManualPressureOverRangeAsync(PressureZeroVoltageResult source,string id,long request,string context)
        {
            int range=source.Capture==null||source.Capture.Settings==null?-1:source.Capture.Settings.RangeIndex;
            if(range<0||range>=BenchSettings.Ranges.Length||source.Capture.Settings.AutoRange||range!=SelectedPicoRangeCode)
                throw new InvalidOperationException("本轮手动量程身份无法确认。");
            var qualityHash=PressureOverflowUpscale.QualityHash(source.Quality);
            var originalHash=PressureOverflowUpscale.QualityHash(source.OriginalRangeQuality??source.Quality);
            bool canAdjust=false;var ranges=GetExactDischargeRangesPc();
            for(int i=range+1;i<ranges.Length;i++)if(IsFinite(ranges[i])&&ranges[i]>=100&&ranges[i]>ranges[range]){canAdjust=true;break;}
            PressureManualRangeChoice choice;
            if(ManualPressureRangeChoiceForTests!=null)choice=ManualPressureRangeChoiceForTests(canAdjust);
            else using(var dialog=new PressureManualRangeDialog(canAdjust,FindForm()==null?null:FindForm().Icon,
                ()=>CurrentPressureQualityDecision(id,request,context)&&!automaticRange&&Object.ReferenceEquals(source,lastPressureCapture),PressureUiText)) {
                dialog.ShowDialog(FindForm());choice=dialog.Choice;
            }
            if(!CurrentPressureQualityDecision(id,request,context)||automaticRange||!Object.ReferenceEquals(source,lastPressureCapture))return "Stale";
            if(choice==PressureManualRangeChoice.Adjust&&!canAdjust)choice=PressureManualRangeChoice.Dismiss;
            var record=new PressureManualRangeDecisionRecord{AcquisitionId=id,RequestSequence=request,CapturedRangeIndex=range,
                Choice=choice.ToString(),RawSha256=source.RawSha256,QualitySha256=qualityHash,OriginalQualitySha256=originalHash,
                Quality=PressureOverflowUpscale.Copy(source.Quality),OriginalQuality=PressureOverflowUpscale.Copy(source.OriginalRangeQuality??source.Quality),RecordedUtc=DateTime.UtcNow};
            string file=Path.Combine(source.Directory,"pressure-manual-range-"+Guid.NewGuid().ToString("N")+".xml");
            await Task.Run(()=>{using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))new XmlSerializer(typeof(PressureManualRangeDecisionRecord)).Serialize(stream,record);});
            if(!CurrentPressureQualityDecision(id,request,context)||automaticRange||!Object.ReferenceEquals(source,lastPressureCapture)||
                qualityHash!=PressureOverflowUpscale.QualityHash(source.Quality)||originalHash!=PressureOverflowUpscale.QualityHash(source.OriginalRangeQuality??source.Quality))return "Stale";
            reviewedRangeRetry=null;continuousPressureDisplayLock=false;
            if(choice==PressureManualRangeChoice.Save){
                manualRiskAcquisition=id;manualRiskSource=source;manualRiskRequest=request;
                manualRiskQualityHash=qualityHash;manualRiskOriginalHash=originalHash;manualExplicitSaveAcquisition=id;
                lock(stateLock){overRangeIgnored=true;dataSaved=dataAnalyzed=deleteEnabled=false;lastActionCode="MANUAL_OVERRANGE_SAVE_REQUESTED";
                    SetPostProcessStateNoLock("SAVE_REQUESTED");saveRequestSequence++;}
                ShowPressureRangeReview(source.RangeReview,PressureUiText("正在保存超量程数据；质量标记保留，不再次加压。","Saving overrange data with quality flags; no new pressure round."));
                UpdatePostProcessUi();Raise(PostProcessStateChanged);Raise(SaveRequested);return "ManualSaved";
            }
            lock(stateLock){overRangeIgnored=false;dataSaved=dataAnalyzed=deleteEnabled=false;
                SetPostProcessStateNoLock("AWAITING_MANUAL_SAVE");lastActionCode="MANUAL_OVERRANGE_"+choice.ToString().ToUpperInvariant();}
            if(choice==PressureManualRangeChoice.Adjust){
                manualHigherRangeRequired=range;manualHigherRangeProject=ProjectPath;manualHigherRangePhase=SelectedPhaseCode;manualHigherRangeAcquisition=id;
                ShowPressureRangeReview(source.RangeReview,PressureUiText("本段未保存。请调高量程，再点击加压。","This segment was not saved. Select a higher range, then press Start."));
            }else ShowPressureRangeReview(source.RangeReview,PressureUiText("超量程，数据可能无效，请调高量程。","Overrange: data may be invalid. Select a higher range."));
            UpdatePostProcessUi();Raise(PostProcessStateChanged);
            return choice==PressureManualRangeChoice.Adjust?"ManualAdjust":choice==PressureManualRangeChoice.Ignore?"ManualIgnored":"ManualDismissed";
        }

        Dictionary<string,object> CapturePressureQualityMetadata(PressureZeroVoltageResult source,string id)
        {
            var details=new Dictionary<string,object>();
            if(!ManualRiskSaveIsCurrent(source,id))return details;
            var q=source.Quality;var original=source.OriginalRangeQuality??q;
            details["manualOverRangeSaved"]=true;details["overRange"]=true;details["overRangeAccepted"]=true;
            details["peakValidity"]="clipped-or-uncertain";details["locationValidity"]="not-validated";
            details["capturedRangeIndex"]=source.Capture.Settings.RangeIndex;details["rawSha256"]=source.RawSha256;
            details["rawCaptureDirectory"]=Path.GetFullPath(source.Directory).Substring(Path.GetFullPath(ProjectPath).TrimEnd(Path.DirectorySeparatorChar).Length+1);
            details["driverOverflow"]=source.Capture.Overflow;details["locationClippedSamples"]=q.LocationClippedSamples;
            details["fullClippedSamples"]=original.FullClippedSamples;details["originalDriverOverflow"]=original.DriverOverflow;
            details["qualitySha256"]=manualRiskQualityHash;details["originalQualitySha256"]=manualRiskOriginalHash;
            details["qualityXml"]=PressureOverflowUpscale.Xml(q);details["originalQualityXml"]=PressureOverflowUpscale.Xml(original);
            return details;
        }
        public void ReportManualOverRangeAnalysisComplete(string acquisitionId)
        {
            RunOnUiThread(()=>{
                if(acquisitionId!=cycleAcquisitionId||!dataSaved||!CurrentRoundManualOverRangeWarning||IsBusy||HasActiveHardwareSession||
                    pressureStateCode!="COMPLETE"||postProcessStateCode!="COMPLETE")return;
                dataAnalyzed=true;maxDischargePc=Double.NaN;locationConfidencePercent=Double.NaN;
                analysisStatus="OVERRANGE_SAVED_NOT_VALIDATED";spectrumDashboardView.ClearData();spectrumDashboardView.SetEndpointReviewAvailable(false);
                ShowPressureRangeReview(lastPressureCapture.RangeReview,ManualOverRangeWarningText());UpdateMetricsUi();UpdatePostProcessUi();
            });
        }
    }

    internal sealed class PressureManualRangeDialog:Form
    {
        internal PressureManualRangeChoice Choice {get;private set;}
        internal PressureManualRangeDialog(bool canAdjust,Icon owner,Func<bool> current,Func<string,string,string> text)
        {
            Text=text("量程提示","Range warning");Font=new Font("Microsoft YaHei UI",10);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(560,165);MinimumSize=new Size(480,200);StartPosition=FormStartPosition.CenterParent;MinimizeBox=MaximizeBox=false;
            var owned=owner==null?HVX.Presentation.OemUiBranding.LoadIcon():(Icon)owner.Clone();if(owned!=null)Icon=owned;
            var label=new Label{Dock=DockStyle.Fill,AutoSize=false,Padding=new Padding(18),Text=text("超量程，数据可能无效，请调高量程。","Overrange: data may be invalid. Select a higher range.")};
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,WrapContents=true,Padding=new Padding(12)};
            Action<string,PressureManualRangeChoice,bool> add=(caption,value,enabled)=>{var b=new Button{AutoSize=true,Text=caption,Enabled=enabled};
                b.Click+=(s,e)=>{if(!current()){Close();return;}Choice=value;Close();};buttons.Controls.Add(b);};
            add(text("调整量程重新加压","Adjust range and restart"),PressureManualRangeChoice.Adjust,canAdjust);
            add(text("忽略","Ignore"),PressureManualRangeChoice.Ignore,true);add(text("保存","Save"),PressureManualRangeChoice.Save,true);
            Controls.Add(label);Controls.Add(buttons);
            var timer=new Timer{Interval=50};timer.Tick+=(s,e)=>{if(!current())Close();};Shown+=(s,e)=>timer.Start();
            Disposed+=(s,e)=>{timer.Dispose();if(owned!=null)owned.Dispose();};
        }
    }
}
