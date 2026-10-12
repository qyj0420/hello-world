using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private string projectPath = String.Empty, savedCalibrationFingerprint = String.Empty, lastCalibrationError = String.Empty;
        private string savedCalibrationStateFingerprint = String.Empty, simulationDataOrigin = String.Empty;
        private bool simulationMode;
        private bool capacitiveLoad;
        [Browsable(false)] public bool IsCapacitiveLoad { get { return capacitiveLoad; } }
        private long calibrationSavedSequence;
        public event EventHandler CalibrationSaved;
        [Browsable(false)] public long CalibrationSavedSequence { get { return System.Threading.Interlocked.Read(ref calibrationSavedSequence); } }
        private const int CalibrationArchiveLimit = 96 * 1024 * 1024;
        private const int CalibrationTotalSampleLimit = 8 * 1024 * 1024;
        public string ProjectPath
        {
            get { return projectPath; }
            set
            {
                string next = String.IsNullOrWhiteSpace(value) ? String.Empty : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(next, projectPath, StringComparison.OrdinalIgnoreCase)) return;
                bool nextCapacitor = HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(next);
                RequireCalibrationScopeSwitch(); ClearCalibrationMemory(); projectPath = next; capacitiveLoad = nextCapacitor;
                if (capacitiveLoad) SetSelectedLevel("5nC");
                LoadNoiseTemplates();
                if (!simulationMode) ApplyProjectGeometryDefaults(next);
                if (!simulationMode) LoadLiveCalibrations();
                if (simulationMode && File.Exists(CalibrationFilePath)) LoadCalibrationFile();
                RefreshPersistedJointProjection();
            }
        }
        public bool SimulationMode
        {
            get { return simulationMode; }
            set
            {
                if (value == simulationMode) return;
                RequireCalibrationScopeSwitch(); ClearCalibrationMemory(); simulationMode = value;
                LoadNoiseTemplates();
                if (value && File.Exists(CalibrationFilePath)) LoadCalibrationFile();
                if (!value) { ApplyProjectGeometryDefaults(projectPath); LoadLiveCalibrations(); }
                RefreshPersistedJointProjection();
            }
        }
        [Browsable(false)] public string LastCalibrationError { get { return lastCalibrationError; } }
        [Browsable(false)] public string SimulationDataOrigin { get { lock (stateLock) return simulationDataOrigin; } }
        public bool SetSimulationDataOrigin(string value)
        {
            if (!simulationMode || IsDisposed || Disposing || InvokeRequired || value == null ||
                value.Length > 4096 || !value.StartsWith("SIM:", StringComparison.Ordinal)) return false;
            foreach (char c in value) if (Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') return false;
            lock (stateLock) simulationDataOrigin = value;
            Invalidate(); return true;
        }
        [Browsable(false)] public string CalibrationFilePath
        { get { return String.IsNullOrEmpty(projectPath) ? String.Empty : simulationMode ? Path.Combine(projectPath, "Calibration", "Simulation", "calibration.hvcsim") : HVX.CalibrationStorage.LiveCalibrationRecord.RecordPath(projectPath,SelectedPhaseCode,SelectedLevelCode); } }
        [Browsable(false)] public bool HasCalibrationData
        { get { lock (stateLock) foreach (PhaseState phase in CalibrationPhases()) if (phase.Waveforms.Count > 0) return true; return false; } }
        [Browsable(false)] public bool HasUnsavedCalibrationChanges
        {
            get
            {
                if(!simulationMode)return HasLiveChanges();
                if (!HasCalibrationData && String.IsNullOrEmpty(savedCalibrationFingerprint) && String.IsNullOrEmpty(SimulationDataOrigin)) return false;
                try { return !IsCalibrationSavedValid || !String.Equals(savedCalibrationStateFingerprint, CalibrationHash(CreateCalibrationPayload()), StringComparison.Ordinal); }
                catch { return true; }
            }
        }
        [Browsable(false)] public bool IsCalibrationSavedValid
        {
            get
            {
                if(!simulationMode)return IsLiveSavedValid();
                if (!simulationMode || String.IsNullOrEmpty(savedCalibrationFingerprint) || !File.Exists(CalibrationFilePath)) return false;
                try
                {
                    byte[] payload = ReadArchiveEnvelope(CalibrationFilePath); ArchiveSnapshot snapshot = ReadCalibrationPayload(payload);
                    return snapshot.FirstArrival == UseFirstArrivalTiming && snapshot.ProjectIdentity == CalibrationProjectIdentity()
                        && String.Equals(CalibrationHash(payload), savedCalibrationFingerprint, StringComparison.Ordinal);
                }
                catch { return false; }
            }
        }
        private string CalibrationText(string chinese, string english)
        { return Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex, chinese, english); }
        private void RequireCalibrationScopeSwitch()
        {
            if(HasActiveHardwareSession)throw new InvalidOperationException(CalibrationText("请先释放校准设备，再切换项目或试验模式。", "Release the calibration devices before changing project or test mode."));
            if (!HasUnsavedCalibrationChanges) return;
            lastCalibrationError = CalibrationText("校准数据尚未保存。请先保存，或明确放弃更改后切换项目/试验模式。", "Calibration changes are not saved. Save or explicitly discard them before changing project or test mode.");
            throw new InvalidOperationException(lastCalibrationError);
        }
        public void DiscardCalibrationChanges()
        {
            ClearCalibrationMemory();
            if (simulationMode && File.Exists(CalibrationFilePath)) LoadCalibrationFile();
            if (!simulationMode) {ApplyProjectGeometryDefaults(ProjectPath);LoadLiveCalibrations();}
            RefreshPersistedJointProjection();
        }
        // Host-only source changes must not turn clearing a saved display into a
        // new edit, reload the old source, or delete the saved archive.
        public bool ClearSimulationDisplay()
        {
            if (IsDisposed || Disposing || InvokeRequired) return false;
            lock (actionLock)
            {
                if (!simulationMode || actionClosed || actionBusy || actionPending || HasUnsavedCalibrationChanges)
                    return false;
                ClearCalibrationMemory();
                return true;
            }
        }
        private void ClearCalibrationMemory()
        {
            ClearPersistedJointProjection();
            lock (stateLock) foreach (PhaseState phase in CalibrationPhases())
            {
                phase.Waveforms.Clear(); phase.FilteredWaveforms.Clear(); phase.FilterSources.Clear(); phase.FilterCutoffs.Clear(); phase.Analyses.Clear(); phase.Markers.Clear();
                phase.VelocityAdoptions.Clear(); phase.VelocityHistory.Clear(); phase.OriginalObservations.Clear(); phase.Joints.Clear(); phase.Noise = null;
                phase.ReflectionCandidates.Clear(); phase.ManualBandwidthPanelVisible = false;
                Array.Clear(phase.PublishedLevels, 0, phase.PublishedLevels.Length);
            }
            savedCalibrationFingerprint = savedCalibrationStateFingerprint = simulationDataOrigin = String.Empty;
            savedLevelSignatures.Clear();
            liveCaptures.Clear();liveWaveSources.Clear();liveSavedFiles.Clear();
            liveLevelParameters.Clear();Array.Clear(persistedCableLengths,0,persistedCableLengths.Length);
            reflectionSelections.Clear();pendingReflectionBadgeHit=System.Drawing.RectangleF.Empty;
            signalToNoiseDb=peakVoltageV=noiseVoltageV=Double.NaN;
            lastCalibrationError = String.Empty; ResetViewInternal(); Invalidate();
        }
        public bool SaveCalibrationFile()
        {
            lastCalibrationError = String.Empty;
            if (!simulationMode) return SaveAllLiveCalibrations();
            string temporary = null;
            try
            {
                if (String.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath)) throw new InvalidOperationException(CalibrationText("请先加载有效项目。", "Load a valid project first."));
                byte[] payload = CreateCalibrationPayload();
                string fingerprint = CalibrationHash(payload), path = CalibrationFilePath;
                string directory = Path.GetDirectoryName(path); Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, ".calibration-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                { writer.Write(Encoding.ASCII.GetBytes("HVCSIM01")); writer.Write(payload.Length); writer.Write(Convert.FromBase64String(fingerprint)); writer.Write(payload); writer.Flush(); stream.Flush(true); }
                ReadCalibrationPayload(ReadArchiveEnvelope(temporary));
                if (File.Exists(path)) File.Replace(temporary, path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".bak");
                else File.Move(temporary, path);
                temporary = null;
                byte[] verify = ReadArchiveEnvelope(path); ReadCalibrationPayload(verify);
                if (!String.Equals(fingerprint, CalibrationHash(verify), StringComparison.Ordinal)) throw new InvalidDataException("Calibration read-back verification failed.");
                savedCalibrationFingerprint = fingerprint;
                savedCalibrationStateFingerprint = fingerprint;
                RememberSavedLevels();
                System.Threading.Interlocked.Increment(ref calibrationSavedSequence);
                Invalidate(); EventHandler handler = CalibrationSaved;
                if (handler != null) try { handler(this, EventArgs.Empty); } catch (Exception listenerError) { System.Diagnostics.Debug.WriteLine("CalibrationSaved listener failed: " + listenerError.GetType().Name); }
                return true;
            }
            catch (Exception ex) { lastCalibrationError=RecordCalibrationCustomerFailure("Simulation calibration save",ex); return false; }
            finally { if (temporary != null && File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
        }
        public bool LoadCalibrationFile()
        {
            lastCalibrationError = String.Empty;
            if (!simulationMode) return LoadLiveCalibrations();
            try
            {
                if (HasUnsavedCalibrationChanges) throw new InvalidOperationException(CalibrationText("请先保存或放弃当前校准更改。", "Save or discard current calibration changes first."));
                byte[] payload = ReadArchiveEnvelope(CalibrationFilePath); ArchiveSnapshot snapshot = ReadCalibrationPayload(payload);
                if (snapshot.ProjectIdentity != CalibrationProjectIdentity()) throw new InvalidDataException(CalibrationText("项目资料已改变或档案属于另一项目，请重新核对校准。", "Project information changed or this archive belongs to a different project. Review calibration again."));
                if (snapshot.FirstArrival != UseFirstArrivalTiming) throw new InvalidDataException(CalibrationText("校准档案计时模式不同，请使用匹配模式或重新校准。", "The calibration timing mode does not match. Use the matching mode or calibrate again."));
                lock (stateLock)
                {
                    int index = 0;
                    reflectionSelections.Clear();
                    foreach (PhaseState target in CalibrationPhases())
                    {
                        CopyCalibrationPhase(snapshot.Phases[index], target);
                        foreach (string level in snapshot.PeakSelectionLevels[index])
                        {
                            var a=target.Analyses[level]; var raw=target.Waveforms[level];
                            reflectionSelections[LiveKey(index-1,level)]=new ReflectionSelectionState {Source=raw,
                                Index=a.ReflectionIndex,IncidentPeak=a.IncidentIndex,ReflectionPeak=a.ReflectionIndex,
                                Confirmed=true,NoticeShown=true,PeakAnchored=true,FirstArrival=snapshot.FirstArrival};
                        }
                        index++;
                    }
                    simulationDataOrigin = snapshot.SimulationDataOrigin;
                }
                savedCalibrationFingerprint = CalibrationHash(payload);
                // Legacy v1/v2 archives lack peak-selection provenance (and v1
                // lacks origin). Preserve their disk bytes, but compare edits
                // against the current canonical state; do not relabel old data.
                savedCalibrationStateFingerprint = CalibrationHash(CreateCalibrationPayload());
                RememberSavedLevels();
                ResetViewInternal(); Invalidate(); return true;
            }
            catch (Exception ex) { lastCalibrationError=RecordCalibrationCustomerFailure("Simulation calibration load",ex); return false; }
        }
        public string GetCalibrationReadiness(string phaseCode, bool simulation, bool firstArrival)
        {
            int phaseIndex;
            if (String.Equals(phaseCode, "ALL", StringComparison.OrdinalIgnoreCase)) phaseIndex = -1;
            else if (String.Equals(phaseCode, "A", StringComparison.OrdinalIgnoreCase) || phaseCode == "L1") phaseIndex = 0;
            else if (String.Equals(phaseCode, "B", StringComparison.OrdinalIgnoreCase) || phaseCode == "L2") phaseIndex = 1;
            else if (String.Equals(phaseCode, "C", StringComparison.OrdinalIgnoreCase) || phaseCode == "L3") phaseIndex = 2;
            else return CalibrationText("请选择明确的校准相别。", "Select a specific calibration phase.");
            bool separate;
            try {
                separate=HVX.CalibrationStorage.LiveCalibrationRecord.RequiresSeparatePhases(ProjectPath);
                if (!simulation && HVX.CalibrationStorage.LiveCalibrationRecord.BestForProject(ProjectPath,PhaseCodeFromIndex(phaseIndex),firstArrival)!=null)return String.Empty;
            } catch(InvalidDataException) { return CalibrationText("项目长度信息无效，请核对项目资料。", "Project lengths are invalid. Check project information."); }
            catch(IOException) { return CalibrationText("无法读取有效的项目长度信息，请核对项目资料。", "Valid project lengths could not be read. Check project information."); }
            string missing=separate
                ?CalibrationText("三相电缆长度不一致，请分别完成 A、B、C 相校准；当前相尚无适用的已保存校准。", "Cable lengths differ. Calibrate A, B and C separately; this phase has no applicable saved calibration.")
                :CalibrationText("完成任一相或 ALL 的有效校准并保存即可；请核对当前项目及计时模式。", "Save a valid calibration for any phase or ALL. Check the current project and timing mode.");
            if(!simulation || separate&&phaseIndex==-1)return missing;
            if (!SimulationMode || firstArrival != UseFirstArrivalTiming) return CalibrationText("校准的试验模式或计时模式不匹配。", "Calibration test mode or timing mode does not match.");
            if (HasUnsavedCalibrationChanges || !IsCalibrationSavedValid) return CalibrationText("请先保存有效校准；校准文件缺失、损坏或有未保存更改。", "Save valid calibration first; the file is missing, damaged, or has unsaved changes.");
            bool velocityReview=false;
            foreach(int candidate in separate?new[]{phaseIndex}:new[]{phaseIndex,-1,0,1,2}.Distinct())
            {
                if(RequiresPhaseVelocityReview(candidate)){velocityReview=true;continue;}
                lock (stateLock) {
                PhaseState phase = GetPhaseStateNoLock(candidate);
                foreach (string level in phase.Waveforms.Keys)
                {
                    if (level == "TDR") continue;
                    MarkerData marker; TdrAutoAnalysisResult analysis;
                    if (phase.Markers.TryGetValue(level, out marker) && marker.DistanceMarkerSeconds > marker.StartTimeSeconds && phase.Analyses.TryGetValue(level, out analysis)
                        && analysis.SearchStatus == TdrSearchStatus.Ok && analysis.IncidentMagnitude > 0 && analysis.ReflectionMagnitude > 0) return String.Empty;
                }
                }
            }
            return velocityReview?CalibrationText("校准各档位测得波速不一致，请核对并确认采用波速。", "Measured velocities differ between calibration levels. Review and confirm the adopted velocity."):missing;
        }
        private bool CalibrationFailure(string zh, string en) { lastCalibrationError = CalibrationText(zh, en); return false; }
        private string CalibrationProjectIdentity()
        {
            using (var buffer = new MemoryStream()) using (var writer = new BinaryWriter(buffer, Encoding.UTF8))
            {
                foreach (string name in new[] { "ITEM.in", "CABLE.in" })
                {
                    string path = Path.Combine(projectPath, name); var info = new FileInfo(path);
                    if (!info.Exists || info.Length > 4 * 1024 * 1024) throw new InvalidDataException(CalibrationText("项目资料文件缺失或超限。", "Project information is missing or exceeds the size limit."));
                    byte[] bytes = File.ReadAllBytes(path); writer.Write(name); writer.Write(bytes.Length); writer.Write(bytes);
                }
                writer.Flush(); return CalibrationHash(buffer.ToArray());
            }
        }
        private static string CalibrationHash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }
        private static byte[] ReadArchiveEnvelope(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < 44 || stream.Length > CalibrationArchiveLimit + 44) throw new InvalidDataException("Invalid calibration archive size.");
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "HVCSIM01") throw new InvalidDataException("Not a simulation calibration archive.");
                int length = reader.ReadInt32(); if (length < 1 || length > CalibrationArchiveLimit || stream.Length != length + 44L) throw new InvalidDataException("Truncated or oversized calibration archive.");
                byte[] checksum = reader.ReadBytes(32), payload = reader.ReadBytes(length);
                if (!String.Equals(Convert.ToBase64String(checksum), CalibrationHash(payload), StringComparison.Ordinal)) throw new InvalidDataException("Calibration integrity check failed.");
                return payload;
            }
        }
        private byte[] CreateCalibrationPayload()
        {
            string identity = CalibrationProjectIdentity();
            using (var buffer = new MemoryStream()) using (var writer = new BinaryWriter(buffer, Encoding.UTF8))
            {
                writer.Write(3); writer.Write(true); writer.Write(UseFirstArrivalTiming); writer.Write(identity);
                lock (stateLock)
                {
                    foreach (PhaseState phase in CalibrationPhases()) WriteCalibrationPhase(writer, phase);
                    writer.Write(simulationDataOrigin);
                }
                writer.Flush(); if (buffer.Length > CalibrationArchiveLimit) throw new InvalidDataException("Calibration archive size limit exceeded.");
                byte[] result = buffer.ToArray(); ReadCalibrationPayload(result); return result;
            }
        }
        private void WriteCalibrationPhase(BinaryWriter w, PhaseState p)
        {
            w.Write(p.CableLengthMeters); w.Write(p.VelocityMPerUs); w.Write(p.ShortCable); w.Write(p.WideBand); w.Write(p.BandwidthCutoffMHz);
            w.Write(p.Waveforms.Count);
            foreach (string level in LevelCodes)
            {
                WaveformData wave; if (!p.Waveforms.TryGetValue(level, out wave)) continue;
                w.Write(level); WriteCalibrationWave(w, wave);
                MarkerData m; bool hasMarker = p.Markers.TryGetValue(level, out m); w.Write(hasMarker);
                if (hasMarker) { w.Write(m.StartTimeSeconds); w.Write(m.DistanceMarkerSeconds); w.Write(m.VelocityMarkerSeconds); w.Write(m.DistanceMeters); }
                TdrAutoAnalysisResult a; p.Analyses.TryGetValue(level, out a);
                w.Write(a == null ? -1 : a.IncidentIndex); w.Write(a == null ? -1 : a.ReflectionIndex);
                w.Write(a == null ? -1 : a.IncidentFirstArrivalIndex); w.Write(a == null ? -1 : a.ReflectionFirstArrivalIndex);
                var selection=ReflectionSelectionNoLock(p,level,false);
                w.Write(selection!=null&&selection.Confirmed&&selection.PeakAnchored);
                double adopted; w.Write(p.VelocityAdoptions.TryGetValue(level, out adopted) ? adopted : 0);
                CalibrationTimingObservation original; bool hasOriginal = p.OriginalObservations.TryGetValue(level, out original); w.Write(hasOriginal);
                if (hasOriginal) { w.Write(original.Length); w.Write(original.PeakIncident); w.Write(original.PeakReflection); w.Write(original.ArrivalIncident); w.Write(original.ArrivalReflection); }
            }
            w.Write(p.Noise != null); if (p.Noise != null) WriteCalibrationWave(w, p.Noise);
            w.Write(p.Joints.Count); foreach (CalibrationJoint joint in p.Joints)
            { w.Write(joint.Id); w.Write(joint.Name); w.Write(joint.DistanceMeters); w.Write(joint.MaterialBefore); w.Write(joint.VelocityBeforeMPerUs); w.Write(joint.MaterialAfter); w.Write(joint.VelocityAfterMPerUs); }
            w.Write(p.VelocityHistory.Count); foreach (CalibrationVelocityAdoption h in p.VelocityHistory)
            { w.Write(h.UtcTicks); w.Write(h.FirstArrival); w.Write(h.Level); w.Write(h.MeasuredVelocity); w.Write(h.AdoptedVelocity); w.Write(h.IncidentSeconds); w.Write(h.ReflectionSeconds); }
        }
        private static void WriteCalibrationWave(BinaryWriter w, WaveformData wave)
        { w.Write(wave.X0Seconds); w.Write(wave.DxSeconds); w.Write(wave.Samples.Length); foreach (double value in wave.Samples) w.Write(value); }
        private sealed class ArchiveSnapshot {
            internal bool FirstArrival; internal string ProjectIdentity, SimulationDataOrigin;
            internal readonly PhaseState[] Phases = new PhaseState[4];
            internal readonly List<string>[] PeakSelectionLevels = new List<string>[4];
        }
        private ArchiveSnapshot ReadCalibrationPayload(byte[] payload)
        {
            using (var buffer = new MemoryStream(payload, false)) using (var r = new BinaryReader(buffer, Encoding.UTF8))
            {
                int version = r.ReadInt32();
                if ((version != 1 && version != 2 && version != 3) || !r.ReadBoolean()) throw new InvalidDataException("Unsupported or non-simulation calibration archive.");
                var snapshot = new ArchiveSnapshot { FirstArrival = r.ReadBoolean(), ProjectIdentity = ReadCalibrationText(r, 128) }; int totalSamples = 0;
                for (int phase = 0; phase < 4; phase++) {
                    snapshot.PeakSelectionLevels[phase]=new List<string>();
                    snapshot.Phases[phase] = ReadCalibrationPhase(r, snapshot.FirstArrival, version>=3,
                        snapshot.PeakSelectionLevels[phase], ref totalSamples);
                }
                snapshot.SimulationDataOrigin = version == 1 ? "SIM: legacy source not recorded" : ReadCalibrationText(r, 4096);
                if (snapshot.SimulationDataOrigin.Length > 0 && !snapshot.SimulationDataOrigin.StartsWith("SIM:", StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid simulation provenance.");
                if (buffer.Position != buffer.Length) throw new InvalidDataException("Unexpected trailing calibration data."); return snapshot;
            }
        }
        private PhaseState ReadCalibrationPhase(BinaryReader r, bool firstArrival, bool hasPeakMetadata,
            List<string> peakSelectionLevels, ref int totalSamples)
        {
            var p = new PhaseState { CableLengthMeters = ReadCalibrationPositive(r), VelocityMPerUs = ReadCalibrationPositive(r), ShortCable = r.ReadBoolean(), WideBand = r.ReadBoolean(), BandwidthCutoffMHz = ReadCalibrationPositive(r) };
            if (p.BandwidthCutoffMHz > 60) throw new InvalidDataException("Invalid calibration bandwidth.");
            int count = ReadCalibrationCount(r, 16);
            for (int j = 0; j < count; j++)
            {
                string level = NormalizeLevelCode(ReadCalibrationText(r, 32)); if (p.Waveforms.ContainsKey(level)) throw new InvalidDataException("Duplicate calibration level.");
                WaveformData wave = ReadCalibrationWave(r, ref totalSamples); p.Waveforms.Add(level, wave);
                MarkerData marker = null; if (r.ReadBoolean())
                {
                    marker = new MarkerData(ReadCalibrationFinite(r), ReadCalibrationFinite(r), ReadCalibrationFinite(r), ReadCalibrationFinite(r));
                    if (marker.StartTimeSeconds < wave.X0Seconds || marker.DistanceMarkerSeconds <= marker.StartTimeSeconds || marker.DistanceMarkerSeconds > wave.X0Seconds + (wave.Samples.Length - 1) * wave.DxSeconds || marker.DistanceMeters < 0) throw new InvalidDataException("Invalid calibration cursors.");
                }
                int peakI = r.ReadInt32(), peakR = r.ReadInt32(), arrivalI = r.ReadInt32(), arrivalR = r.ReadInt32();
                foreach (int index in new[] { peakI, peakR, arrivalI, arrivalR }) if (index < -1 || index >= wave.Samples.Length) throw new InvalidDataException("Invalid calibration sample index.");
                bool peakAnchored=hasPeakMetadata&&r.ReadBoolean();
                if(peakAnchored) {
                    if(peakI<1||peakR<=peakI||peakR>=wave.Samples.Length-1)
                        throw new InvalidDataException("Invalid selected calibration peaks.");
                    if(marker!=null) {
                        int i=firstArrival?arrivalI:peakI, end=firstArrival?arrivalR:peakR;
                        if(i<0||end<=i||firstArrival&&(i>=peakI||end<=peakI||end>=peakR)||
                            Math.Abs(marker.StartTimeSeconds-(wave.X0Seconds+i*wave.DxSeconds))>wave.DxSeconds*1e-6||
                            Math.Abs(marker.DistanceMarkerSeconds-(wave.X0Seconds+end*wave.DxSeconds))>wave.DxSeconds*1e-6)
                            throw new InvalidDataException("Selected calibration timing does not match its peak evidence.");
                    } else if(!firstArrival)throw new InvalidDataException("Selected peak timing is incomplete.");
                    peakSelectionLevels.Add(level);
                }
                double adopted = ReadCalibrationFinite(r); if (adopted < 0) throw new InvalidDataException("Invalid adopted velocity."); if (adopted > 0) p.VelocityAdoptions[level] = adopted;
                if (r.ReadBoolean())
                {
                    var original = new CalibrationTimingObservation { Length = ReadCalibrationPositive(r), PeakIncident = r.ReadInt32(), PeakReflection = r.ReadInt32(), ArrivalIncident = r.ReadInt32(), ArrivalReflection = r.ReadInt32() };
                    foreach (int index in new[] { original.PeakIncident, original.PeakReflection, original.ArrivalIncident, original.ArrivalReflection }) if (index < -1 || index >= wave.Samples.Length) throw new InvalidDataException("Invalid original calibration index.");
                    p.OriginalObservations[level] = original;
                }
                RebuildLevelDerivedNoLock(p, level);
                if (adopted > 0) p.VelocityAdoptions[level] = adopted;
                if (peakI >= 0 && peakR > peakI)
                {
                    TdrAutoAnalysisResult previous = p.Analyses[level]; WaveformData active = ActiveWaveformNoLock(p, level, wave);
                    TdrAutoAnalysisResult analysis = BuildManualResult(active, p, peakI, peakR, previous);
                    analysis.NoiseSigma = previous.NoiseSigma; analysis.IncidentFirstArrivalIndex = arrivalI; analysis.ReflectionFirstArrivalIndex = arrivalR; p.Analyses[level] = analysis;
                }
                if (marker != null)
                {
                    if (peakI < 0 || peakR <= peakI || (firstArrival && (arrivalI < 0 || arrivalR <= arrivalI))) throw new InvalidDataException("Calibration timing evidence is incomplete.");
                    p.Markers[level] = marker;
                }
                else p.Markers.Remove(level);
                PublishLevelNoLock(p, level);
            }
            if (r.ReadBoolean()) p.Noise = ReadCalibrationWave(r, ref totalSamples);
            int joints = ReadCalibrationCount(r, 1000);
            for (int j = 0; j < joints; j++) p.Joints.Add(new CalibrationJoint(ReadCalibrationText(r, 128), ReadCalibrationText(r, 4096), ReadCalibrationFinite(r), ReadCalibrationText(r, 4096), ReadCalibrationPositive(r), ReadCalibrationText(r, 4096), ReadCalibrationPositive(r)));
            int history = ReadCalibrationCount(r, 10000);
            for (int j = 0; j < history; j++)
            {
                var h = new CalibrationVelocityAdoption { UtcTicks = r.ReadInt64(), FirstArrival = r.ReadBoolean(), Level = NormalizeLevelCode(ReadCalibrationText(r, 32)), MeasuredVelocity = ReadCalibrationPositive(r), AdoptedVelocity = ReadCalibrationPositive(r), IncidentSeconds = ReadCalibrationFinite(r), ReflectionSeconds = ReadCalibrationFinite(r) };
                if (h.UtcTicks < DateTime.MinValue.Ticks || h.UtcTicks > DateTime.MaxValue.Ticks || h.ReflectionSeconds <= h.IncidentSeconds) throw new InvalidDataException("Invalid calibration adoption history."); p.VelocityHistory.Add(h);
            }
            return p;
        }
        private static WaveformData ReadCalibrationWave(BinaryReader r, ref int total)
        {
            double x0 = ReadCalibrationFinite(r), dx = ReadCalibrationPositive(r); int length = ReadCalibrationCount(r, 2 * 1024 * 1024);
            if (length < 16 || !IsFinite(x0 + (length - 1) * dx)) throw new InvalidDataException("Invalid calibration sample duration.");
            total += length; if (total > CalibrationTotalSampleLimit) throw new InvalidDataException("Calibration sample budget exceeded.");
            if (r.BaseStream.Length - r.BaseStream.Position < 8L * length) throw new InvalidDataException("Truncated calibration waveform.");
            var samples = new double[length]; for (int i = 0; i < length; i++) samples[i] = ReadCalibrationFinite(r); return new WaveformData(samples, x0, dx);
        }
        private static int ReadCalibrationCount(BinaryReader r, int max) { int v = r.ReadInt32(); if (v < 0 || v > max) throw new InvalidDataException("Calibration item limit exceeded."); return v; }
        private static double ReadCalibrationFinite(BinaryReader r) { double v = r.ReadDouble(); if (!IsFinite(v)) throw new InvalidDataException("Non-finite calibration parameter."); return v; }
        private static double ReadCalibrationPositive(BinaryReader r) { double v = ReadCalibrationFinite(r); if (v <= 0) throw new InvalidDataException("Calibration parameter must be positive."); return v; }
        private static string ReadCalibrationText(BinaryReader r, int max)
        {
            // BinaryReader.ReadString allocates from its prefix; reject oversized prefix before allocation.
            int length = 0, shift = 0;
            for (int i = 0; i < 5; i++) { byte b = r.ReadByte(); if (i == 4 && (b & 0xf0) != 0) throw new InvalidDataException("Invalid string length."); length |= (b & 127) << shift; if ((b & 128) == 0) { if (length < 0 || length > max * 4 || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Calibration text limit exceeded."); string value = new UTF8Encoding(false, true).GetString(r.ReadBytes(length)); if (value.Length > max) throw new InvalidDataException("Calibration text limit exceeded."); return value; } shift += 7; }
            throw new InvalidDataException("Invalid string length.");
        }
        private static void CopyCalibrationPhase(PhaseState source, PhaseState target)
        {
            target.CableLengthMeters = source.CableLengthMeters; target.VelocityMPerUs = source.VelocityMPerUs; target.ShortCable = source.ShortCable; target.WideBand = source.WideBand; target.BandwidthCutoffMHz = source.BandwidthCutoffMHz; target.Noise = source.Noise;
            target.Waveforms.Clear(); foreach (var p in source.Waveforms) target.Waveforms[p.Key] = p.Value;
            target.FilterSources.Clear(); target.FilterCutoffs.Clear();
            target.FilteredWaveforms.Clear(); foreach (var p in source.FilteredWaveforms) target.FilteredWaveforms[p.Key] = p.Value;
            target.Analyses.Clear(); foreach (var p in source.Analyses) target.Analyses[p.Key] = p.Value;
            target.Markers.Clear(); foreach (var p in source.Markers) target.Markers[p.Key] = p.Value;
            target.VelocityAdoptions.Clear(); foreach (var p in source.VelocityAdoptions) target.VelocityAdoptions[p.Key] = p.Value;
            target.OriginalObservations.Clear(); foreach (var p in source.OriginalObservations) target.OriginalObservations[p.Key] = p.Value;
            target.VelocityHistory.Clear(); target.VelocityHistory.AddRange(source.VelocityHistory);
            target.Joints.Clear(); target.Joints.AddRange(source.Joints); Array.Copy(source.PublishedLevels, target.PublishedLevels, source.PublishedLevels.Length);
        }
    }
}
