using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        bool analyzeZeroVoltageData,cycleZeroAnalysisRequested,currentZeroVoltageAnalysis;
        string zeroAnalysisSource="";
        public bool AnalyzeZeroVoltageData
        {
            get{return analyzeZeroVoltageData;}
            set{RunOnUiThread(delegate{
                if(value==analyzeZeroVoltageData)return;
                if(PressureParametersLocked)throw new InvalidOperationException("请先完成本轮采集及保存，再修改零电压分析设置。");
                // This is a next-acquisition preference, never a reclassification
                // of a reference already displayed or saved.
                analyzeZeroVoltageData=value;
            });}
        }
        [Browsable(false)] public bool CurrentAcquisitionIsZeroVoltageAnalysis{get{return currentZeroVoltageAnalysis;}}
        void ApplyZeroVoltageAnalysisMetrics(double knownPeak=Double.NaN)
        {
            double peak=IsFinite(knownPeak)?knownPeak:0;
            if(!IsFinite(knownPeak)&&lastDischargePc!=null)foreach(double sample in lastDischargePc)if(IsFinite(sample))peak=Math.Max(peak,Math.Abs(sample));
            lock(stateLock){maxDischargePc=peak;equivalentCapacitanceUf=oscillationFrequencyHz=dissipationFactorPercent=Double.NaN;
                metricFrequencyAvailable=metricCapacitanceAvailable=metricTanDeltaAvailable=false;
                rawMeasuredCapacitanceUf=rawMeasuredTanDeltaPercent=Double.NaN;analysisStatus="ZERO_VOLTAGE_ANALYSIS";}
            UpdateMetricsUi();
        }
        bool ZeroLocationSaveIsCurrent(LocationSaveContext value)
        {
            return value!=null&&currentZeroVoltageAnalysis&&!currentAcquisitionReferencePreview&&cycleZeroAnalysisRequested&&
                cycleTargetPeakKv==0&&LocationSaveContextIsCurrent(value)&&!String.IsNullOrWhiteSpace(zeroAnalysisSource);
        }
        Task<bool> SaveZeroVoltageLocationDataAsync(string acquisitionId,string filePath,double[] chargePc,double rate,double start,double velocity,string filterId,CancellationToken cancellation)
        {
            LocationSaveContext context=null;string source="";
            RunOnUiThread(delegate{try{
                cancellation.ThrowIfCancellationRequested();
                if(operatorPressureStopRequested||!IsHandleCreated||!currentZeroVoltageAnalysis||currentAcquisitionReferencePreview||!cycleIdentityAccepted||!cycleIdentityBound||
                    !cycleZeroAnalysisRequested||cycleTargetPeakKv!=0||acquisitionId!=cycleAcquisitionId||cyclePhase!=SelectedPhaseCode||
                    !String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)||cycleSimulation!=SimulationMode||String.IsNullOrWhiteSpace(zeroAnalysisSource))
                    throw new InvalidOperationException("本轮零电压数据的来源或分析选择不完整，未保存。");
                if(chargePc==null||chargePc.Length<16||chargePc.Length>16000000||!IsFinite(rate)||rate<=0||!IsFinite(start)||start<0||!IsFinite(velocity)||velocity<=0||velocity>299.792458)
                    throw new InvalidDataException("本轮数据长度、采样率或波速无效。");
                if(filterId==null||(filterId.Length>0&&filterId!=HVX.SignalProcessing.PdLinearHighPass.FilterId))throw new InvalidDataException("数据处理标记无法识别，未保存。");
                string root=Path.GetFullPath(ProjectPath).TrimEnd(Path.DirectorySeparatorChar),full=Path.GetFullPath(filePath);
                if(!Directory.Exists(root)||!String.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetExtension(full),".loc",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("请保存到当前项目中的新定位文件。");
                Match identity=Regex.Match(Path.GetFileName(full),@"^(A|B|C)_([0-9]+(?:\.[0-9]+)?)kV_",RegexOptions.IgnoreCase);double voltage;
                if(!identity.Success||!String.Equals(identity.Groups[1].Value,cyclePhase,StringComparison.OrdinalIgnoreCase)||!Double.TryParse(identity.Groups[2].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out voltage)||voltage!=0)
                    throw new InvalidDataException("文件名称与本轮相别或零电压来源不一致。");
                if(File.Exists(full))throw new IOException("该定位记录已存在，不会覆盖。");
                source=zeroAnalysisSource;
                context=new LocationSaveContext{Id=acquisitionId,Project=root,Phase=cyclePhase,Path=full,ReceivedUtc=cycleReceivedUtc,Simulation=cycleSimulation,TargetPeak=0,CableLength=cycleCableLength,OriginalWindow=locationCycleWindow};
            }catch(Exception ex){if(ex is OutOfMemoryException)throw;if(acquisitionId==cycleAcquisitionId){lastError=lastPostProcessError=ex.Message;lastActionCode="ZERO_ANALYSIS_SAVE_FAILED";}}});
            if(context==null)return Task.FromResult(false);
            return Task.Run(delegate{
                string temporary=null;
                try{
                    cancellation.ThrowIfCancellationRequested();
                    int savedCount=ZeroVoltageLocationWindow.SampleCount(chargePc.Length,rate);
                    var reference=new Dictionary<string,object>{{"schema","HVX.ZeroVoltageAnalysis/1"},{"analysisOptIn",true},{"targetVoltageKv",0},{"phaseKnown",false},
                        {"sampleRateHz",rate},{"sampleCount",savedCount},{"captureStartSeconds",start},{"locationAcquisitionId",context.Id},{"noiseReferenceSourceId",source},{"simulation",context.Simulation},
                        {"sourceSampleCount",chargePc.Length},{"maximumSavedDurationSeconds",ZeroVoltageLocationWindow.MaximumSeconds},{"savedDurationSeconds",savedCount/rate}};
                    if(!String.IsNullOrEmpty(filterId)){reference.Add("pdAcquisitionFilterId",filterId);reference.Add("pdAcquisitionFilterScope","full-segment-before-zero-voltage-analysis");}
                    var details=new Dictionary<string,object>{{"simulation",context.Simulation},{"locationAcquisitionId",context.Id},{"savedUtc",DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)},
                        {"analyzeZeroVoltageData",true},{"targetVoltageKv",0},{"noiseReferenceSourceId",source},{"phaseKnown",false}};
                    string[] headers={"HVX LOC zero-voltage record / engineering pC x 100000","Received UTC\t"+context.ReceivedUtc,"Phase\tNaN","Voltage (kV)\t0",
                        "Cable-length\t"+context.CableLength.ToString("R",CultureInfo.InvariantCulture),"Velocity (m/us)\t"+velocity.ToString("R",CultureInfo.InvariantCulture),
                        "Delta x [s]\t"+(1/rate).ToString("R",CultureInfo.InvariantCulture),"Charge unit\tpC x 100000","Capture start seconds\t"+start.ToString("R",CultureInfo.InvariantCulture),
                        "Acquisition context\t"+new JavaScriptSerializer().Serialize(details),"HVX_ZeroVoltageReference\t"+new JavaScriptSerializer().Serialize(reference)};
                    temporary=Path.Combine(context.Project,".loc-zero-"+Guid.NewGuid().ToString("N")+".tmp");
                    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(true))){
                        foreach(string header in headers)writer.WriteLine(header);
                        for(int i=0;i<savedCount;i++){
                            if((i&4095)==0)cancellation.ThrowIfCancellationRequested();
                            if(!IsFinite(chargePc[i])||Math.Abs(chargePc[i])>1e100)throw new InvalidDataException("本轮局放数据含无效数值，未加入分析。");
                            writer.WriteLine(EncodeLocationCharge(chargePc[i]));
                        }
                    }
                    bool saved=false;RunOnUiThread(delegate{
                        cancellation.ThrowIfCancellationRequested();
                        if(!ZeroLocationSaveIsCurrent(context)||source!=zeroAnalysisSource)throw new InvalidOperationException("保存期间采集会话已改变，未加入分析。");
                        File.Move(temporary,context.Path);saved=true;lastError=lastPostProcessError="";lastActionCode="ZERO_ANALYSIS_SAVED";
                    });return saved;
                }catch(Exception ex){if(ex is OutOfMemoryException)throw;RunOnUiThread(delegate{if(ZeroLocationSaveIsCurrent(context)){lastError=lastPostProcessError=ex.Message;lastActionCode="ZERO_ANALYSIS_SAVE_FAILED";}});return false;}
                finally{if(temporary!=null&&File.Exists(temporary))File.Delete(temporary);}
            });
        }
    }
}
