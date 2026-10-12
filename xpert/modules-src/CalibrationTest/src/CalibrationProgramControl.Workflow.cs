using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using System.Linq;
using Xiongxin.Calibration;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        public string AcquisitionMode { get; set; } = "Pulse";
        public bool IsDistanceMode { get { return SelectedLevelCode == "TDR"; } }
        public string CalibrationWaveformCaption { get { return IsDistanceMode ? CalibrationText("测距波形", "Ranging waveform") :
            LevelLabels[Array.IndexOf(LevelCodes, SelectedLevelCode)] + CalibrationText(" 档位校准波形", " calibration"); } }
        public string CalibrationSaveCaption
        {
            get {
                if (IsDistanceMode) return CalibrationText("保存电缆长度", "Save cable length");
                if (SelectedCalibrationSaved) return CalibrationText("删除校准", "Delete calibration");
                string previous;
                return savedLevelSignatures.TryGetValue(SelectedPhaseIndex + ":" + SelectedLevelCode, out previous)
                    ? CalibrationText("重新保存", "Save again") : CalibrationText("保存校准", "Save calibration");
            }
        }
        private string MetricCaption(double value, string format, string unit)
        { return IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) + " " + unit : CalibrationText("未采集", "Not acquired"); }

        public bool SaveMeasuredCableLength()
        {
            bool geometrySaved=false;
            try {
                if(IsBusy)return CalibrationFailure("采集尚未结束，请停止后保存。","Stop acquisition before saving.");
                if(SimulationMode) return CalibrationFailure("仿真测距不改写实测项目长度。", "Simulation does not change project geometry.");
                PhaseState phase=GetPhaseStateNoLock(SelectedPhaseIndex); MarkerData marker;
                CalibrationCaptureResult source;WaveformData original;
                string key=LiveKey(SelectedPhaseIndex,"TDR");
                if(!liveCaptures.TryGetValue(key,out source)||!liveWaveSources.TryGetValue(key,out original)||
                    !phase.Waveforms.ContainsKey("TDR")||!Object.ReferenceEquals(original,phase.Waveforms["TDR"])||
                    (!(source.Reflection?.Valid??false)&&ReflectionSelectionNoLock(phase,"TDR",false)?.Confirmed!=true))
                    throw new InvalidOperationException("请先完成实机测距并核对反射，不把仿真或无来源波形写入项目。");
                if(!phase.Markers.TryGetValue("TDR",out marker)||marker.DistanceMarkerSeconds<=marker.StartTimeSeconds)throw new InvalidOperationException("请先确认入射和反射游标。");
                var tdrArchive=PrepareTdrArchive(source,phase,marker);
                double measured=(marker.DistanceMarkerSeconds-marker.StartTimeSeconds)*phase.VelocityMPerUs*1e6/2;
                CalibrationProjectStore.SaveLength(ProjectPath,SelectedPhaseCode,measured,phase.VelocityMPerUs);
                geometrySaved=true;
                phase.CableLengthMeters=measured;
                for(int index=-1;index<3;index++) {
                    var defaults=Xiongxin.Calibration.ProjectDefaults.Load(ProjectPath,PhaseCodeFromIndex(index));
                    if(!defaults.CapacitiveLoad)persistedCableLengths[index+1]=defaults.LengthM;
                }
                phase.Markers["TDR"]=new MarkerData(marker.StartTimeSeconds,marker.DistanceMarkerSeconds,marker.DistanceMarkerSeconds,measured);
                RememberLiveLevelParameters(SelectedPhaseIndex,"TDR");
                MarkLiveProjectChanged();
                LastJointCommitWarning="";
                try { DetectAndStoreJoints(SelectedPhaseIndex,"TDR",source); }
                catch(Exception jointError) {
                    WriteCalibrationDiagnostic("Cable length saved; joint detection was not updated: "+jointError);
                    LastJointCommitWarning=CalibrationText("电缆长度已保存，请重新核对接头。","Cable length saved. Review the joints again.");
                }
                tdrArchive.ProjectHash=HVX.CalibrationStorage.LiveCalibrationRecord.Identity(ProjectPath);
                string tdrFile=tdrArchive.Save(ProjectPath);
                liveSavedFiles[tdrFile]=HVX.CalibrationStorage.LiveCalibrationRecord.HashFile(tdrFile);
                source.SavedRecordPath=tdrFile;
                HVX.CalibrationStorage.LiveCalibrationRecord.RetireAveragedCaptureCache(ProjectPath,tdrFile);
                savedLevelSignatures[key]=LevelSignature(SelectedPhaseIndex,"TDR");
                lastCalibrationError=String.Empty;Invalidate();return true;
            }catch(Exception ex){
                RecordCalibrationCustomerFailure("Cable length/ranging save",ex);
                lastCalibrationError=geometrySaved?CalibrationText("电缆长度已保存，测距波形尚未保存，请再次保存。","Cable length saved; the ranging waveform was not saved. Save again."):CalibrationCustomerFailureCaption;
                return false;
            }
        }

        private void ShowJointConfirmation()
        {
            if (jointConfirmationOpen || IsBusy) return;
            if (IsCapacitiveLoad)
            {
                MessageBox.Show(FindForm(), CalibrationText("电容负载不进行电缆接头定位。", "Joint location does not apply to capacitive loads."),
                    CalibrationText("确认接头", "Review joints"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            jointConfirmationOpen = true;
            bool originalJointVisibility=JointMarkersVisible;
            JointMarkersVisible=true;
            try
            {
            int originalPhase=SelectedPhaseIndex;string originalLevel=SelectedLevelCode,originalProject=ProjectPath;bool originalSimulation=SimulationMode;
            string expectedCableHash=null,expectedItemHash=null;
            if(!originalSimulation) {
                try { var snapshot=HVX.ProjectCables.CableJointStore.Read(originalProject); expectedCableHash=HVX.ProjectCables.CableJointStore.Hash(snapshot.Original); expectedItemHash=snapshot.ItemHash; }
                catch(Exception ex) { ShowCalibrationCustomerFailure(FindForm(),"Joint load",ex,"接头未载入","Joints not loaded");return; }
            }
            var selected = GetPhaseStateNoLock(originalPhase).Joints.ToArray();
            using (var form = CreateJointConfirmationDialog(selected))
            {
                var list = (CheckedListBox)form.Controls.Find("CalibrationJointCandidates", true)[0];
                var save = (Button)form.Controls.Find("ConfirmCalibrationJoints", true)[0];
                save.Click += delegate {
                    try {
                        RequireJointReviewContext(originalProject,originalPhase,originalLevel,originalSimulation);
                        foreach (int i in Enumerable.Range(0, selected.Length).Where(i => selected[i].AutomaticallyDetected && !selected[i].Confirmed && list.GetItemChecked(i))) {
                            if (!ReviewJointCandidate(form, selected[i], originalProject, originalPhase, originalLevel, originalSimulation, ref expectedCableHash, expectedItemHash)) return;
                        }
                        Invalidate(); form.DialogResult=DialogResult.OK;
                    }catch(Exception ex){ShowCalibrationCustomerFailure(form,"Joint review/write",ex,"接头未写入","Joint not saved");}
                };
                form.ShowDialog(FindForm());
            }
            }
            finally { JointMarkersVisible=originalJointVisibility; jointConfirmationOpen = false; }
        }
        private void RequireJointReviewContext(string project,int phase,string level,bool simulation)
        {
            if(IsDisposed||Disposing||IsBusy||IsCapacitiveLoad||project!=ProjectPath||phase!=SelectedPhaseIndex||level!=SelectedLevelCode||simulation!=SimulationMode)
                throw new InvalidOperationException(CalibrationText("项目、相别、档位或采集状态已变化，请重新打开接头窗口。", "Project, phase, level or acquisition state changed. Reopen the joint dialog."));
        }
        private Form CreateJointConfirmationDialog(CalibrationJoint[] selected)
        {
            var form = new CalibrationDialogForm("CalibrationJointConfirmation", CalibrationText("确认接头", "Review joints"), Font, new Size(640, 460));
            var list = new CheckedListBox { Name = "CalibrationJointCandidates", CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
            foreach (var joint in selected) list.Items.Add(joint.Name + "  " + joint.DistanceMeters.ToString("0.0") + " m  " + joint.ConfidencePercent.ToString("0.0") + "%" +
                (joint.AutomaticallyDetected ? "" : CalibrationText("（手动已添加）", " (manually added)")), joint.Confirmed);
            list.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { if (!selected[e.Index].AutomaticallyDetected || selected[e.Index].Confirmed) e.NewValue = CheckState.Checked; };
            string hint = selected.Length == 0 ? CalibrationText("尚无接头候选。采集并确认反射波后识别，或在波形上双击添加。", "No joint candidates yet. Acquire and confirm the reflection, or double-click the waveform to add a joint.") :
                CalibrationText("勾选待核对的候选，再逐一核对位置、附件类型及前后绝缘并确认。未选或取消的候选保留，已有正式接头不在这里修改。ALL 不代表独立三相全选，适用相别须明确选择。", "Select candidates to review, then confirm each position, attachment type and before/after insulation. Unselected or cancelled candidates remain pending; existing joints are not edited here. ALL does not select three independent phases; choose the applicable phases explicitly.");
            var save = new HVX.Presentation.R2RoundedButton { Name = "ConfirmCalibrationJoints", Text = CalibrationText("核对所选接头", "Review selected joints"), Enabled = selected.Any(j => j.AutomaticallyDetected && !j.Confirmed), BackColor = Theme.Accent, ForeColor = Color.White };
            var cancel = new HVX.Presentation.R2RoundedButton { Name = "CancelCalibrationJoints", Text = CalibrationText("取消", "Cancel"), DialogResult = DialogResult.Cancel };
            form.SetContent(new CalibrationChoiceContent(hint, list), cancel, save); form.CancelButton = cancel;
            return form;
        }
    }
}
