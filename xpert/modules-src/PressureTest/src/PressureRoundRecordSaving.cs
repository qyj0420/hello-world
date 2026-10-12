using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        internal Action BeforeDetectedRecordPublishForTests;
        public async Task<string> SaveCurrentPressureRoundAsync(double velocityMPerUs)
        {
            var result=lastPressureCapture;string id=lastPressureAcquisitionId,path=null,dem=null,temporary=null;
            string[] headers=null;bool locationCommitted=false,pairCommitted=false,markerOwned=false,capacitor=false;
            PressureLocationReferenceContext referenceContext=null;
            string marker=null;
            if(result==null)throw new InvalidOperationException("尚无有效加压数据。");
            RunOnUiThread(()=>{
                RequireCurrentRoundSave(result,id);
                capacitor=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(ProjectPath);
                if(capacitor)RequireCapacitorRoundIdentity();
                if(!capacitor)referenceContext=CaptureLocationRawReferenceContext(result,id);
                var time=result.Capture.CapturedUtc.ToLocalTime();
                path=Path.Combine(ProjectPath,SelectedPhaseCode+"_"+cycleTargetPeakKv.ToString("0.###",CultureInfo.InvariantCulture)+"kV_"+
                    time.ToString("yyyy-MM-dd-HH\\'mm\\'ss.fffffff",CultureInfo.InvariantCulture)+(capacitor?".dem":".loc"));
                dem=Path.ChangeExtension(path,".dem");
                marker=HVX.Storage.PressureRecordPublication.MarkerPath(path);
                if(File.Exists(path)||File.Exists(dem))throw new IOException("该轮测试或定位记录已存在，未覆盖原数据。");
                temporary=Path.Combine(ProjectPath,".dem-"+Guid.NewGuid().ToString("N")+".tmp");
                var details=new Dictionary<string,object>{{"locationAcquisitionId",id},{"capturedUtc",result.Capture.CapturedUtc.ToString("O")},
                    {"artRateHz",result.Capture.ArtRateHz},{"picoRateHz",result.Capture.PicoRateHz},{"highPassFilterId",result.HighPassFilterId},
                    {"overRange",lastAcquisitionOverRange},{"overRangeAccepted",overRangeIgnored},{"rawSha256",result.RawSha256},
                    {"simulation",SimulationMode},{"phase",SelectedPhaseCode},{"targetPeakKv",cycleTargetPeakKv},
                    {"calibrationSourceHash",calibrationSourceHash},{"calibrationCoefficientPcPerV",result.Capture.Settings.PdCalibrationPcPerV},
                    {"calibrationAcquiredUtc",calibrationAcquiredUtc.ToString("O")}};
                details.Add("measurementKind",capacitor?"Capacitor":"Cable");
                if(capacitor){
                    // Keep the genuine full-rate ADC source for capacitor
                    // charge/phase statistics. The 80k DEM is a detected view,
                    // not a high-rate location waveform or a fabricated LOC.
                    string rawDirectory=PressureZeroVoltageCapture.CaptureDirectory(ProjectPath,result.Directory);
                    string root=Path.GetFullPath(ProjectPath).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                    details.Add("rawCaptureDirectory",rawDirectory.Substring(root.Length+1));
                    details.Add("rawCaptureFile","capture.bin");details.Add("rawSettingsFile","settings.xml");
                    details.Add("rawSettingsSha256",CapacitorRawSettingsHash(rawDirectory,result));
                    details.Add("rawSampleUnits","Pico ADC counts; ART input volts");
                    details.Add("rawPicoSamples",result.Capture.PdAdc.Length);details.Add("rawArtSamples",result.Capture.VoltageRaw.Length);
                    details.Add("rawPicoFullScaleV",result.Capture.PicoFullScaleV);details.Add("rawMaximumAdc",result.Capture.MaximumAdc);
                    details.Add("rawHasDerivedChargeFilter",false);details.Add("locationApplicable",false);
                    details.Add("zeroVoltageAnalysisOptIn",cycleTargetPeakKv==0&&cycleZeroAnalysisRequested);
                }
                foreach(var quality in CapturePressureQualityMetadata(result,id))details[quality.Key]=quality.Value;
                headers=new[]{"Date:\t"+time.ToString("yyyy/MM/dd",CultureInfo.InvariantCulture),"Time:\t"+time.ToString("HH:mm:ss.fffffff",CultureInfo.InvariantCulture),
                    "Cal.File-path:\t"+(calibrationSource??"").Replace('\t',' '),"Cal.value (pC):\t"+calibrationReferenceChargePc.ToString("R",CultureInfo.InvariantCulture),"Cal.max (mV):\tNaN",
                    "V-scale max (kV):\t"+cycleTargetPeakKv.ToString("R",CultureInfo.InvariantCulture),
                    "PD-scale max(pC):\t"+(result.Capture.PicoFullScaleV*result.Capture.Settings.PdCalibrationPcPerV).ToString("R",CultureInfo.InvariantCulture),
                    "Number of Samples:\t0","Delta-x [s]:\t"+(1/PressureDetectedRecord.RateHz).ToString("R",CultureInfo.InvariantCulture),
                    "Hz:\t"+oscillationFrequencyHz.ToString("R",CultureInfo.InvariantCulture),"C[uF]:\t"+equivalentCapacitanceUf.ToString("R",CultureInfo.InvariantCulture),
                    "D-losses:\t"+dissipationFactorPercent.ToString("R",CultureInfo.InvariantCulture),
                    "Acquisition context:\t"+new JavaScriptSerializer().Serialize(details),
                    "Detection:\t80k absolute peak bins; signed voltage; full-segment high-pass before detection", "DATA-TOTAL:","PD (pC):\tVoltage (kV):"};
            });
            if(headers==null)throw new InvalidOperationException("采集界面已关闭，未保存。");
            try{
                await Task.Run(()=>{
                    using(var stream=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                        markerOwned=true;
                        byte[] bytes=System.Text.Encoding.UTF8.GetBytes((capacitor?"HVX capacitor DEM pending / ":"HVX paired record pending / ")+id);
                        stream.Write(bytes,0,bytes.Length);stream.Flush(true);
                    }
                });
                bool locationPrepared=await PressureRoundPairPreparation.RunAsync(
                    ()=>capacitor?Task.FromResult(true):SaveLocationCycleDataAsync(id,path,result.ChargePc,result.Capture.PicoRateHz,0,velocityMPerUs,result.HighPassFilterId,CancellationToken.None),
                    ()=>Task.Run(()=>{
                        var data=PressureDetectedRecord.Detect(result.VoltageKv,result.Capture.ArtRateHz,result.ChargePc,result.Capture.PicoRateHz,CancellationToken.None);
                        headers[7]="Number of Samples:\t"+data[0].Length.ToString(CultureInfo.InvariantCulture);
                        PressureDetectedRecord.Write(temporary,data,headers,CancellationToken.None);
                    }),
                    saved=>locationCommitted=!capacitor&&saved);
                if(!locationPrepared)throw new InvalidOperationException(LastPostProcessError);
                RunOnUiThread(()=>{
                    RequireCurrentRoundSave(result,id);
                    if(capacitor!=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(ProjectPath))
                        throw new InvalidOperationException("项目负载类型已改变，测试记录未发布。");
                    if(capacitor)RequireCapacitorRoundIdentity();
                    if(BeforeDetectedRecordPublishForTests!=null)BeforeDetectedRecordPublishForTests();
                    File.Move(temporary,dem);
                    File.Delete(marker);markerOwned=false;pairCommitted=true;
                });
                if(!pairCommitted)throw new InvalidOperationException("界面已关闭，测试记录未完整发布。");
                // N-cycle LOC and detected DEM are derived records, not a copy
                // of the unique full-segment ADC. Keep capture.bin and its hash.
                // Optional explicit reference publication still follows pair commit.
                if(!capacitor&&!ManualRiskSaveIsCurrent(result,id)&&referenceContext!=null&&referenceContext.Retain)
                    await Task.Run(()=>RetirePublishedRawCapture(result,path,dem,referenceContext));
                return path;
            }catch{
                // Only hide the newly created LOC owned by this invocation.
                // Retain it for recovery; never remove a prior round or raw capture.
                if(locationCommitted&&!pairCommitted&&File.Exists(path)){
                    string recovery=Path.Combine(Path.GetDirectoryName(path),".incomplete-"+Guid.NewGuid().ToString("N")+".loc.tmp");
                    File.Move(path,recovery);
                }
                // If neither final record exists, failure cleanup is complete.
                // Otherwise retain the marker: do not expose a partial pair.
                if(markerOwned&&!File.Exists(path)&&!File.Exists(dem)){
                    File.Delete(marker);markerOwned=false;
                }
                throw;
            }finally{
                if(temporary!=null&&File.Exists(temporary))File.Delete(temporary);
            }
        }
        void RetirePublishedRawCapture(PressureZeroVoltageResult result,string location,string detected,PressureLocationReferenceContext referenceContext)
        {
            try {
                // Only the just-published pair can retire its own full-rate
                // temporary capture. Keep failures, interrupted rounds and all
                // unknown/historical files; never recursively delete a project.
                string project=Path.GetDirectoryName(location);
                string boundary=Path.GetFullPath(Path.Combine(project,"Acquisition","Pressure"))+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(result.Directory).StartsWith(boundary,StringComparison.OrdinalIgnoreCase))return;
                PressureZeroVoltageCapture.CaptureDirectory(project,result.Directory);
                using(var loc=new StreamReader(new FileStream(location,FileMode.Open,FileAccess.Read,FileShare.Read)))
                using(var dem=new StreamReader(new FileStream(detected,FileMode.Open,FileAccess.Read,FileShare.Read))) {
                    var headers=new string[11];for(int i=0;i<headers.Length;i++)headers[i]=loc.ReadLine();
                    if(Array.Exists(headers,h=>h==null))return;
                    int count=0;string line;long encoded;
                    while((line=loc.ReadLine())!=null){if(!Int64.TryParse(line,NumberStyles.Integer,CultureInfo.InvariantCulture,out encoded))return;if(++count>16000000)return;}
                    if(Xiongxin.OWTS.Data.LocationCycleReference.ParseHeader(headers,count,result.Capture.PicoRateHz)==null)return;
                    var demHeaders=new string[16];for(int i=0;i<16;i++)demHeaders[i]=dem.ReadLine();
                    if(Array.Exists(demHeaders,h=>h==null))return;
                    int expected;if(!Int32.TryParse(demHeaders[7].Substring(demHeaders[7].IndexOf('\t')+1),out expected)||expected<16||expected>4800)return;
                    count=0;
                    while((line=dem.ReadLine())!=null){var cells=line.Split('\t');double pd,v;
                        if(cells.Length!=2||!Double.TryParse(cells[0],NumberStyles.Float,CultureInfo.InvariantCulture,out pd)||
                            !Double.TryParse(cells[1],NumberStyles.Float,CultureInfo.InvariantCulture,out v)||Double.IsNaN(pd)||Double.IsInfinity(pd)||Double.IsNaN(v)||Double.IsInfinity(v))return;
                        if(++count>expected)return;
                    }
                    if(count!=expected||File.Exists(HVX.Storage.PressureRecordPublication.MarkerPath(location)))return;
                    if(referenceContext==null||!Object.ReferenceEquals(referenceContext.Source,result)||
                        !String.Equals(referenceContext.Project,project,StringComparison.OrdinalIgnoreCase)||
                        !LocationRawReferenceOwnerIsCurrent(referenceContext))return;
                    if(referenceContext.Retain){
                        PublishLocationRawReference(referenceContext);
                        return; // Separate reference only: formal LOC/ACK semantics are unchanged.
                    }
                    if(File.Exists(Path.Combine(result.Directory,PressureLocationReferenceRetention.FileName)))return;
                    string raw=Path.Combine(result.Directory,"capture.bin"),hash;
                    using(var stream=File.OpenRead(raw))using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                    if(hash==result.RawSha256)File.Delete(raw);
                }
            }catch(Exception error)when(error is IOException||error is UnauthorizedAccessException||error is InvalidOperationException||error is ArgumentException){
                System.Diagnostics.Trace.TraceWarning("Pressure raw cache retained: "+error.Message);
            }
        }
        void RequireCurrentRoundSave(PressureZeroVoltageResult result,string id)
        {
            bool manualWarning=ManualRiskSaveIsCurrent(result,id);
            if(manualWarning&&(IsBusy||HasActiveHardwareSession||pressureStateCode!="COMPLETE"||pressureEmergencyLatched||finishCurrentRoundRequested||automaticPlanInterrupted||
                !CanRequestVoltageNoLock(requestedTargetPeakVoltageKv)))throw new InvalidOperationException("当前试验状态已改变，未保存测试记录。");
            if(result!=null&&result.Capture!=null&&PressureChargeRangeFloor.MustHoldAtMaximum(result.Quality,result.Capture.Settings.RangeIndex)&&!manualWarning)
                throw new InvalidOperationException("本轮量程质量待复核，数据仅供查看，未保存测试记录。");
            if(operatorPressureStopRequested||id!=cycleAcquisitionId||!Object.ReferenceEquals(result,lastPressureCapture)||
                cyclePhase!=SelectedPhaseCode||!String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)||
                currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview)
                throw new InvalidOperationException("本轮已中断或来源已改变，未保存测试记录。");
        }
        void RequireCapacitorRoundIdentity()
        {
            if(!cycleIdentityBound||!cycleIdentityAccepted||cycleSimulation!=SimulationMode)
                throw new InvalidOperationException("电容采集身份尚未核验或模式已改变，未保存测试记录。");
        }
        static string CapacitorRawSettingsHash(string directory,PressureZeroVoltageResult result)
        {
            // Bind the original settings bytes as well as ADC bytes. A later
            // analysis must not reinterpret saved raw samples with edited
            // calibration, range, rate or trigger parameters.
            using(var stream=new FileStream(Path.Combine(directory,"settings.xml"),FileMode.Open,FileAccess.Read,FileShare.Read)){
                if(stream.Length<=0||stream.Length>65536)throw new InvalidDataException("原始采集参数文件无效，测试记录未保存。");
                string hash;
                using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                stream.Position=0;
                using(var reader=new StreamReader(stream,System.Text.Encoding.UTF8,true,1024,true)){
                    var actual=PressureZeroVoltageCapture.ReadSettings(reader.ReadToEnd());
                    foreach(var field in typeof(Xiongxin.ZeroBench.BenchSettings).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public))
                        if(!Object.Equals(field.GetValue(actual),field.GetValue(result.Capture.Settings)))
                            throw new InvalidDataException("原始采集参数与本轮数据不符，测试记录未保存。");
                }
                return hash;
            }
        }
    }
}
