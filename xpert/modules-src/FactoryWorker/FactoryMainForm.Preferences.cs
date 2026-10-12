using System;
using System.IO;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private FactoryParameterStore parameterStore;
        private FactoryParameterProfile parameterDefaults;
        private int parameterMode;
        private bool loadingParameters;
        private bool restoringStartupMode;
        private bool parameterSaveDeferredForEdit,savingParameters;
        private string parameterArtName="";
        private System.Windows.Forms.Timer parameterSaveTimer;
        private readonly Label storedCalibration=new Label { AutoSize=true };

        private void RefreshStoredCalibration()
        { if(values.ContainsKey("pd"))storedCalibration.Text=values["pd"].Value.ToString("0.######")+" pC/V\n"+calibrationOrigin.Text+"\n长期保存，直至手动修改或应用新校准。"; }
        private readonly Label parameterPersistence=new Label { Dock=DockStyle.Top,Height=27,Padding=new Padding(10,5,0,0),Text="参数按两卡序列号自动保存；仿真与实机分别记忆。" };

        private void InitializeParameterPreferences(string testLabelRoot,string testDirectory)
        {
            // All existing UI/replay tests are isolated from the operator's settings.
            if(testLabelRoot!=null && testDirectory==null)return;
            string directory=testDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Xiongxin","FactoryAcceptance","ParameterProfiles");
            parameterStore=new FactoryParameterStore(directory);
            parameterDefaults=SnapshotParameters();
            restoringStartupMode=true;
            try { mode.SelectedIndex=parameterStore.LastMode(); } finally {restoringStartupMode=false;}
            parameterMode=mode.SelectedIndex;
            LoadParameterPreferences(parameterMode);
            parameterSaveTimer=new System.Windows.Forms.Timer { Interval=500 };
            parameterSaveTimer.Tick+=(s,e)=>SaveParametersFromTimer();
            ObserveParameterEditorCompletion(this);
            foreach(var number in values.Values)number.ValueChanged+=(s,e)=>ScheduleParameterSave();
            repeatCount.ValueChanged+=(s,e)=>ScheduleParameterSave();tfThresholdPc.ValueChanged+=(s,e)=>ScheduleParameterSave();
            rawPd.DisplayPreferenceChanged+=(s,e)=>ScheduleParameterSave();
            foreach(var number in new[]{acceptanceLoad,acceptanceMaximum,acceptanceKnownCharge})number.ValueChanged+=(s,e)=>ScheduleParameterSave();
            voltageReferenceId.TextChanged+=(s,e)=>ScheduleParameterSave();pdReferenceId.TextChanged+=(s,e)=>ScheduleParameterSave();
            continuous.CheckedChanged+=(s,e)=>ScheduleParameterSave();tfAccumulate.CheckedChanged+=(s,e)=>ScheduleParameterSave();tfScope.SelectedIndexChanged+=(s,e)=>ScheduleParameterSave();
            foreach(var choice in new[]{resolution,range,coupling,knownCharge,calibrationFrequency,artChoice,picoChoice})
                choice.SelectedIndexChanged+=(s,e)=>ScheduleParameterSave();
            foreach(var check in new[]{bandwidth,referenceCapacitor,oscillationCircuit,automaticRange,monitorExtrema,repeatPrompts})
                check.CheckedChanged+=(s,e)=>ScheduleParameterSave();
            artLibrary.TextChanged+=(s,e)=>ScheduleParameterSave();picoLibrary.TextChanged+=(s,e)=>ScheduleParameterSave();
            FormClosing+=(s,e)=>{if(!e.Cancel && !busy && !scanning)SaveParameterPreferences();};
            Disposed+=(s,e)=>{parameterSaveTimer.Stop();parameterSaveTimer.Dispose();};
        }
        private FactoryParameterProfile SnapshotParameters()
        {
            var art=artChoice.SelectedItem as Xiongxin.ZeroBench.ArtDeviceIdentity;
            var p=new FactoryParameterProfile { Resolution=resolution.SelectedIndex,Range=range.SelectedIndex,Coupling=coupling.SelectedIndex,
                KnownCharge=knownCharge.SelectedIndex,CalibrationFrequency=calibrationFrequency.SelectedIndex,
                Bandwidth=bandwidth.Checked,ReferenceCapacitor=referenceCapacitor.Checked,OscillationCircuit=oscillationCircuit.Checked,
                RangeReminder=automaticRange.Checked,MonitorExtrema=monitorExtrema.Checked,RepeatPrompts=repeatPrompts.Checked,ArtSerial=pinnedArtSerial,ArtName=art==null || art.Serial!=pinnedArtSerial?parameterArtName:art.Name,PicoSerial=pinnedPicoSerial,
                ArtLibrary=artLibrary.Text,PicoLibrary=picoLibrary.Text,Continuous=continuous.Checked,RepeatCount=(int)repeatCount.Value,CapturePosition=capturePosition.Value,
                Accumulate=tfAccumulate.Checked,TfScope=tfScope.SelectedIndex,ThresholdPc=tfThresholdPc.Value,LocationChargeMaximumPc=rawPd.ChargeRulerMaximumPc,LocationBoxZoom=rawPd.BoxZoomEnabled,
                AcceptanceLoad=acceptanceLoad.Value,AcceptanceMaximum=acceptanceMaximum.Value,AcceptanceKnownCharge=acceptanceKnownCharge.Value,
                VoltageReferenceId=voltageReferenceId.Text,PdReferenceId=pdReferenceId.Text };
            if(appliedCalibrationProfile!=null)
            {
                var c=appliedCalibrationProfile.Input;
                p.Calibration=new FactoryStoredCalibration { ArtSerial=c.ArtSerial,PicoSerial=c.PicoSerial,Simulation=appliedCalibrationProfile.Simulation,
                    Coupling=c.PicoCoupling,Bandwidth=c.PicoBandwidth20MHz,Bits=c.ResolutionBits,Rate=c.RequestedPicoRateHz,
                    Coefficient=c.PdCalibrationPcPerV,Origin=calibrationOrigin.Text };
            }
            foreach(var item in values)p.Numbers.Add(new FactoryParameterValue { Name=item.Key,Value=item.Value.Value });
            return p;
        }
        private void ScheduleParameterSave()
        {
            if(parameterStore==null || loadingParameters || applyingDiscovery || savingParameters)return;
            parameterSaveDeferredForEdit=false;
            if(parameterPersistence.Text!="参数已修改，正在自动保存…")parameterPersistence.Text="参数已修改，正在自动保存…";
            parameterSaveTimer.Stop();parameterSaveTimer.Start();
        }
        private void SaveParametersFromTimer()
        {
            parameterSaveTimer.Stop();
            // Reading NumericUpDown.Value commits unfinished edit-box text. A
            // save scheduled by control A must not interrupt typing in control B.
            // Park this timer; Leave/Validated will resume it without polling or
            // rewriting the status label. Explicit saves intentionally bypass it.
            if(ContainsFocusedNumber(this)){parameterSaveDeferredForEdit=true;return;}
            parameterSaveDeferredForEdit=false;
            SaveParameterPreferences();
        }
        private static bool ContainsFocusedNumber(Control control)
        {
            if(!control.ContainsFocus)return false;
            if(control is NumericUpDown)return true;
            foreach(Control child in control.Controls)if(ContainsFocusedNumber(child))return true;
            return false;
        }
        private void ObserveParameterEditorCompletion(Control control)
        {
            var number=control as NumericUpDown;
            if(number!=null)
            {
                EventHandler finished=(s,e)=>{if(parameterSaveDeferredForEdit && !IsDisposed && !Disposing)ScheduleParameterSave();};
                number.Leave+=finished;number.Validated+=finished;
            }
            foreach(Control child in control.Controls)ObserveParameterEditorCompletion(child);
        }
        private bool SaveParameterPreferences()
        {
            if(parameterStore==null || loadingParameters)return true;
            if(parameterSaveTimer!=null)parameterSaveTimer.Stop();
            parameterSaveDeferredForEdit=false;savingParameters=true;
            try
            {
                parameterStore.Save(parameterMode,SnapshotParameters());
                parameterStore.RememberMode(parameterMode);
                parameterPersistence.Text=(parameterMode==0?"仿真":"实机")+"参数已保存 · "+DateTime.Now.ToString("HH:mm:ss")+" · "+ParameterPairLabel();
                return true;
            }
            catch(Exception ex)
            { parameterPersistence.Text="参数未保存："+ex.Message;diagnostics.AppendText("PARAMETER_SAVE_ERROR："+ex.Message+Environment.NewLine);return false; }
            finally {savingParameters=false;}
        }
        private string ParameterPairLabel()
        { return FactoryParameterStore.HasDevicePair(pinnedArtSerial,pinnedPicoSerial)?"ART "+pinnedArtSerial+" / Pico "+pinnedPicoSerial:"尚未绑定两卡序列号"; }

        private bool SwitchSelectedParameterPair()
        {
            if(parameterStore==null || loadingParameters || applyingDiscovery)return true;
            var art=artChoice.SelectedItem as Xiongxin.ZeroBench.ArtDeviceIdentity;
            string pico=picoChoice.SelectedItem as string;
            if(art==null || !FactoryParameterStore.HasDevicePair(art.Serial,pico))return true;
            if(art.Serial==pinnedArtSerial && pico==pinnedPicoSerial) {parameterArtName=art.Name;return true;}
            // Save before changing the bound identity, including any changes
            // still waiting for the debounce timer. Never copy A's values to B.
            if(!SaveParameterPreferences()) {RestoreBoundDeviceSelection();return false;}
            if(parameterSaveTimer!=null)parameterSaveTimer.Stop();
            loadingParameters=true;
            try
            {
                var saved=parameterStore.ReadForDevice(parameterMode,art.Serial,pico);
                var next=saved??parameterDefaults.Clone();
                next.ArtSerial=art.Serial;next.PicoSerial=pico;next.ArtName=art.Name;
                ApplyParameterProfile(next,true);
                latest=null;applyCalibration.Enabled=applyRange.Enabled=export.Enabled=false;
                calibrationAutoAccepted=false;combined.ClearWave();rawPd.ClearWave();calibrationRaw.ClearWave();calibrationAverage.ClearWave();ResetTf(true);ResetStages();
                resultSummary.Text="设备组合已切换；等待本轮采集。";
                parameterPersistence.Text=(saved==null?"新设备组合使用默认参数，不继承其他设备。":"已恢复此设备组合的参数。")+" "+ParameterPairLabel();
            }
            catch(Exception ex)
            {
                RestoreBoundDeviceSelection();
                parameterPersistence.Text="设备参数未切换："+ex.Message;
                diagnostics.AppendText("PARAMETER_DEVICE_LOAD_ERROR："+ex.Message+Environment.NewLine);
                return false;
            }
            finally {loadingParameters=false;}
            ScheduleParameterSave();return true;
        }
        private void RestoreBoundDeviceSelection()
        {
            applyingDiscovery=true;
            try
            {
                artChoice.SelectedIndex=-1;
                for(int i=0;i<artChoice.Items.Count;i++)if(((Xiongxin.ZeroBench.ArtDeviceIdentity)artChoice.Items[i]).Serial==pinnedArtSerial){artChoice.SelectedIndex=i;break;}
                picoChoice.SelectedIndex=picoChoice.Items.IndexOf(pinnedPicoSerial);
            }
            finally {applyingDiscovery=false;}
        }
        private void LoadParameterPreferences(int nextMode)
        {
            if(parameterStore==null)return;
            loadingParameters=true;parameterMode=nextMode;
            try
            {
                var saved=parameterStore.Read(nextMode);
                ApplyParameterProfile(saved??parameterDefaults);
                parameterStore.RememberMode(nextMode);
                parameterPersistence.Text=saved==null?"当前模式尚无历史参数；修改后自动保存。":
                    (nextMode==0?"仿真":"实机")+"参数已恢复；设备序列号待本次读取匹配。";
            }
            catch(Exception ex)
            {
                ApplyParameterProfile(parameterDefaults);
                parameterPersistence.Text="历史参数未载入，使用默认值："+ex.Message;
                diagnostics.AppendText("PARAMETER_LOAD_ERROR："+ex.Message+Environment.NewLine);
            }
            finally { loadingParameters=false; }
        }
        private void ApplyParameterProfile(FactoryParameterProfile p,bool preserveDevices=false)
        {
            // Validate all ranges before changing any control. Out-of-date/invalid
            // values are not silently clamped and presented as restored settings.
            foreach(var item in p.Numbers)
            {
                NumericUpDown number;
                if(values.TryGetValue(item.Name,out number) && (item.Value<number.Minimum || item.Value>number.Maximum))
                    throw new InvalidDataException(item.Name+" 超出界面允许范围");
            }
            var controls=new[]{resolution,range,coupling,knownCharge,calibrationFrequency};
            var indices=new[]{p.Resolution,p.Range,p.Coupling,p.KnownCharge,p.CalibrationFrequency};
            for(int i=0;i<controls.Length;i++)if(indices[i]<0 || indices[i]>=controls[i].Items.Count)throw new InvalidDataException("参数选项无效");
            if(p.LocationChargeMaximumPc<0 || p.LocationChargeMaximumPc>100000000 || p.CapturePosition<-50 || p.CapturePosition>50 || p.RepeatCount<repeatCount.Minimum || p.RepeatCount>repeatCount.Maximum || p.ThresholdPc<tfThresholdPc.Minimum || p.ThresholdPc>tfThresholdPc.Maximum || p.TfScope<0 || p.TfScope>=tfScope.Items.Count)
                throw new InvalidDataException("连续次数或阈值设置无效");
            if(p.Calibration!=null && (!FactorySettings.Positive(p.Calibration.Coefficient) || !FactorySettings.Positive(p.Calibration.Rate) || p.Calibration.Simulation!=(parameterMode==0)))
                throw new InvalidDataException("校准来源与模式不匹配");
            var extraNumbers=new[]{acceptanceLoad,acceptanceMaximum,acceptanceKnownCharge};
            var extraValues=new[]{p.AcceptanceLoad,p.AcceptanceMaximum,p.AcceptanceKnownCharge};
            for(int i=0;i<extraNumbers.Length;i++)if(extraValues[i]<extraNumbers[i].Minimum || extraValues[i]>extraNumbers[i].Maximum)throw new InvalidDataException("核验参数超出界面允许范围");
            if(p.Calibration!=null && (p.Calibration.ArtSerial!=p.ArtSerial || p.Calibration.PicoSerial!=p.PicoSerial))throw new InvalidDataException("校准来源与档案序列号不一致");
            if(p.Calibration!=null)
            {
                var coefficient=p.Numbers.Find(n=>n.Name=="pd");
                if(coefficient==null || Math.Abs(p.Calibration.Coefficient-(double)coefficient.Value)>1e-6)throw new InvalidDataException("校准系数与来源不一致");
            }
            // An older profile can lack newly added settings. Fill those from
            // factory defaults, never from the previously selected device.
            if(parameterDefaults!=null)
                foreach(var item in parameterDefaults.Numbers){NumericUpDown number;if(values.TryGetValue(item.Name,out number))number.Value=item.Value;}
            foreach(var item in p.Numbers){NumericUpDown number;if(values.TryGetValue(item.Name,out number))number.Value=item.Value;}
            for(int i=0;i<controls.Length;i++)controls[i].SelectedIndex=indices[i];
            bandwidth.Checked=p.Bandwidth;referenceCapacitor.Checked=p.ReferenceCapacitor;oscillationCircuit.Checked=p.OscillationCircuit;
            automaticRange.Checked=p.RangeReminder;monitorExtrema.Checked=p.MonitorExtrema;repeatPrompts.Checked=p.RepeatPrompts;artLibrary.Text=p.ArtLibrary;picoLibrary.Text=p.PicoLibrary;
            continuous.Checked=p.Continuous;repeatCount.Value=p.RepeatCount;tfThresholdPc.Value=p.ThresholdPc;tfAccumulate.Checked=p.Accumulate;tfScope.SelectedIndex=p.TfScope;
            capturePosition.Value=p.CapturePosition;
            rawPd.ChargeRulerMaximumPc=p.LocationChargeMaximumPc;rawPd.BoxZoomEnabled=p.LocationBoxZoom;
            for(int i=0;i<extraNumbers.Length;i++)extraNumbers[i].Value=extraValues[i];
            voltageReferenceId.Text=p.VoltageReferenceId??"";pdReferenceId.Text=p.PdReferenceId??"";
            // Independent readings/acknowledgements are evidence, not settings.
            acceptanceReferenceVoltage.Value=0;standardVoltage.Checked=standardPd.Checked=voltage80Percent.Checked=voltageCoverage.Checked=false;
            pinnedArtSerial=p.ArtSerial??"";pinnedPicoSerial=p.PicoSerial??"";
            parameterArtName=p.ArtName??"";
            if(!preserveDevices)
            {
                applyingDiscovery=true;
                try {devices=null;artChoice.Items.Clear();picoChoice.Items.Clear();}
                finally {applyingDiscovery=false;}
            }
            artIdentity.Text=pinnedArtSerial.Length==0?"待读取设备":(preserveDevices?"":"已保存 ")+p.ArtName+" / "+pinnedArtSerial+(preserveDevices?"":" · 待匹配");
            picoIdentity.Text=pinnedPicoSerial.Length==0?"待读取设备":(preserveDevices?"":"已保存 ")+pinnedPicoSerial+(preserveDevices?"":" · 待匹配");
            wiring.Checked=calibrationConnected.Checked=false;
            appliedCalibrationProfile=null;
            calibrationOrigin.Text="已保存的设定系数；不代表本轮已重新校准。";
            if(p.Calibration!=null)
            {
                var c=p.Calibration;
                appliedCalibrationProfile=new FactorySettings { Simulation=c.Simulation };
                var input=appliedCalibrationProfile.Input;
                input.ArtSerial=c.ArtSerial;input.PicoSerial=c.PicoSerial;input.PicoCoupling=c.Coupling;input.PicoBandwidth20MHz=c.Bandwidth;
                input.ResolutionBits=c.Bits;input.RequestedPicoRateHz=c.Rate;input.PdCalibrationPcPerV=c.Coefficient;
                calibrationOrigin.Text=c.Origin;
            }
            RefreshStoredCalibration();
            RefreshRangeLabels();RefreshMode();
        }
    }
}
