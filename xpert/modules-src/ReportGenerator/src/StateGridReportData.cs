using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HVX.Controls;

namespace HVX.ReportGenerator
{
    internal sealed class StateGridFigure
    {
        internal string Title, Phase, Note, SourcePath, ImagePath;
        internal BrowserRecord Record;
        internal bool Reference;
        internal double Actual=double.NaN,NoisePeak=double.NaN;
    }

    internal sealed class StateGridReportData
    {
        sealed class Measurement {internal BrowserRecord Record;internal double Peak,Noise;}
        internal const string Missing="未采集 / 缺数据";
        internal ProjectReportData Project;
        internal BrowserProject Browser;
        internal readonly List<StateGridFigure> Figures=new List<StateGridFigure>();
        internal readonly Dictionary<string,string> Values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Trace=new List<string>();
        internal string Conclusion;
        internal double[,] Matrix;
        internal static bool Finite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}
        internal static string N(double v){return Finite(v)?v.ToString("0.###",CultureInfo.InvariantCulture):Missing;}
        internal string Value(params string[] keys)
        {foreach(string key in keys){string value;if(Values.TryGetValue(key,out value)&&!String.IsNullOrWhiteSpace(value)&&value!="—")return value.Trim();}return Missing;}

        internal static StateGridReportData Read(string root,ProjectReportData project,double[,] matrix,string conclusion)
        {
            if(HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root))throw new NotSupportedException("电容项目不适用电缆局部放电振荡波检测附录 M，已跳过国网格式附加报告。");
            var data=new StateGridReportData{Project=project??ProjectReportDataReader.Read(root),Browser=BrowserReader.LoadProject(root),Matrix=matrix,Conclusion=String.IsNullOrWhiteSpace(conclusion)?"未形成结论，待专业人员复核。":conclusion};
            foreach(var pair in data.Project.Values)data.Values[pair.Key]=pair.Value;
            foreach(var pair in data.Browser.Metadata)data.Values[pair.Key]=pair.Value;
            data.SelectFigures();return data;
        }

        internal static double ChargePc(BrowserRecord record)
        {
            if(record==null)return double.NaN;
            if(HVX.CalibrationStorage.LiveCalibrationRecord.IsArchiveFile(record.Path)) {
                string root;if(record.Metadata.TryGetValue("LiveProjectPath",out root))
                    try{return HVX.CalibrationStorage.LiveCalibrationRecord.Load(root,record.Path,true).ChargePc;}catch{return double.NaN;}
            }
            var match=Regex.Match(Path.GetFileNameWithoutExtension(record.Path),@"_(\d+(?:\.\d+)?)\s*(pC|nC)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            double value;return match.Success&&double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out value)?value*(match.Groups[2].Value.Equals("nC",StringComparison.OrdinalIgnoreCase)?1000:1):double.NaN;
        }

        internal static StateGridFigure SelectCalibration(IEnumerable<BrowserRecord> source,string phase,double target)
        {
            // Never borrow another phase. ALL is retained as ALL, not relabelled A/B/C.
            var candidates=source.Where(r=>r.IsValid&&r.Kind=="cal"&&r.MeasurementKind!="Capacitor"&&r.Phase==phase)
                .Select(r=>new{Record=r,Charge=ChargePc(r)}).Where(x=>Finite(x.Charge)&&x.Charge>0)
                .OrderBy(x=>Math.Abs(x.Charge-target)).ThenByDescending(x=>x.Record.Timestamp).ThenBy(x=>x.Record.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            var item=candidates.FirstOrDefault();
            var result=new StateGridFigure{Title=N(target)+" pC 标准脉冲校准",Phase=phase,Note=Missing};
            if(item==null)return result;
            result.Record=item.Record;result.SourcePath=item.Record.Path;result.Actual=item.Charge;result.Reference=Math.Abs(item.Charge-target)>1e-6;
            result.Note=(result.Reference?"参考替代：缺少精确校准档位；":"精确档位：")+phase+"，实际 "+N(item.Charge)+" pC；来源 "+Path.GetFileName(item.Record.Path);
            return result;
        }

        internal static StateGridFigure SelectMeasurement(IEnumerable<BrowserRecord> source,string phase,double targetPeak,bool noise)
        {return SelectMeasurementCached(ReadMeasurements(source),phase,targetPeak,noise);}

        static List<Measurement> ReadMeasurements(IEnumerable<BrowserRecord> source)
        {
            var candidates=new List<Measurement>();
            foreach(var record in source.Where(r=>r.IsValid&&r.Kind=="dem")) {
                try {
                    var wave=BrowserReader.LoadWaveform(record);
                    int voltageCount=0;double peak=0;
                    foreach(double value in wave.VoltageKv)if(Finite(value)){voltageCount++;peak=Math.Max(peak,Math.Abs(value));}
                    // These are sampled kV values, not RMS labels or the V-scale limit.
                    if(voltageCount>=16)candidates.Add(new Measurement{Record=record,Peak=peak,Noise=wave.Y.Where(Finite).Select(Math.Abs).DefaultIfEmpty(double.NaN).Max()});
                }catch(IOException){}catch(InvalidDataException){}catch(ArgumentException){}
            }
            return candidates;
        }

        static StateGridFigure SelectMeasurementCached(IEnumerable<Measurement> candidates,string phase,double targetPeak,bool noise)
        {
            var result=new StateGridFigure{Title=noise?"背景噪声检测":(Finite(targetPeak)?N(targetPeak)+" kV 目标峰值":"加压检测"),Phase=phase,Note=Missing};
            if(!Finite(targetPeak)||targetPeak<0){result.Note="缺少可靠 U0，无法确定目标电压档位。";return result;}
            var ranked=candidates.Where(x=>x.Record.Phase==phase).OrderBy(x=>Math.Abs(x.Peak-targetPeak)).ThenByDescending(x=>x.Record.Timestamp).ThenBy(x=>x.Record.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            if(ranked.Length==0)return result;
            var chosen=ranked[0];result.Record=chosen.Record;result.SourcePath=chosen.Record.Path;result.Actual=chosen.Peak;result.NoisePeak=chosen.Noise;
            // Only numeric precision allowance, not an unstated standards tolerance.
            result.Reference=Math.Abs(chosen.Peak-targetPeak)>Math.Max(.001,targetPeak*1e-6);
            result.Note=(result.Reference?"参考替代：无精确目标记录；":"精确记录：")+phase+" 相，实测峰值 "+N(chosen.Peak)+" kV；来源 "+Path.GetFileName(chosen.Record.Path);
            if(noise&&result.Reference)result.Note+="；非零电压试验仅作参考，不等同零压背景噪声。";
            return result;
        }

        void SelectFigures()
        {
            // Per record retain only scalar summaries; do not hold full DEM
            // arrays or reread each record for all three requested voltage slots.
            var measurements=ReadMeasurements(Browser.Tests);
            string[] phases=Browser.Calibrations.Where(r=>r.IsValid&&r.MeasurementKind!="Capacitor").Select(r=>r.Phase).Where(p=>new[]{"ALL","A","B","C"}.Contains(p)).Distinct().OrderBy(p=>p=="ALL"?0:p=="A"?1:p=="B"?2:3).ToArray();
            if(phases.Length==0)phases=new[]{"ALL"};
            foreach(double charge in new[]{500d,100000d})foreach(string phase in phases)Figures.Add(SelectCalibration(Browser.Calibrations,phase,charge));
            foreach(string phase in new[]{"A","B","C"}) {
                var noise=SelectMeasurementCached(measurements,phase,0,true);Figures.Add(noise);
                string value=Missing;
                if(noise.Record!=null) {
                    if(Finite(noise.NoisePeak))value=(noise.Reference?"参考替代 ":"")+N(noise.NoisePeak)+" pC（绝对峰值）";
                }
                Values["noise."+phase]=value;
            }
            foreach(double multiple in new[]{1d,1.4d})foreach(string phase in new[]{"A","B","C"}) {
                // Matches the software's U0 mode: U0 is phase RMS, DAC target is sqrt(2)*multiple*U0.
                double target=Project.DeviceU0Kv>0?Math.Sqrt(2)*multiple*Project.DeviceU0Kv:double.NaN;
                var figure=SelectMeasurementCached(measurements,phase,target,false);figure.Title=multiple.ToString("0.0",CultureInfo.InvariantCulture)+"U0 时测试波形";Figures.Add(figure);
                Values["peak."+multiple.ToString("0.0",CultureInfo.InvariantCulture)+"."+phase]=figure.Record==null?Missing:(figure.Reference?"参考替代 ":"")+N(figure.Actual);
            }
            Trace.Add("电压口径：U0 取项目额定相电压（有效值）。软件 U0 档目标峰值 = √2 × 倍数 × U0。表内峰值取实际 DEM 电压采样的绝对最大值，不以文件名、RMS 或量程上限冒充实测峰值。");
            Trace.Add("参考替代只选同项目、同相别、同类数据，按实际量或实测峰值距离排序；不会改写成目标值，也不作为符合标准或合格的自动判据。");
            Trace.Add("报告为 Q/GDW 11262—2024 附录 M 表 M.1 格式输出，不代表已验证满足标准全文。主管、审核和试验人员文字不构成签名。");
        }

        internal string Discharge(string phase,int kind)
        {
            if(Matrix==null||Matrix.GetLength(0)==0||Matrix.GetLength(1)<36)return "缺少定位分析";
            int p="ABC".IndexOf(phase,StringComparison.Ordinal)+1;double[] joints;
            bool knownStructure=Browser.JointPositionsByPhase.TryGetValue(phase,out joints)||Browser.JointPositionsByPhase.TryGetValue("ALL",out joints);
            if(!knownStructure)joints=new double[0];
            var matches=new List<string>();int phaseRows=0,unknownRows=0;
            for(int r=0;r<Matrix.GetLength(0);r++) {
                if(Matrix[r,0]!=p||Matrix[r,35]!=1)continue;
                double position=Matrix[r,13],charge=Finite(Matrix[r,16])?Matrix[r,16]:Matrix[r,15];
                double length=Matrix[r,5];
                if(!Finite(length)||length<=0) {
                    if(!double.TryParse(Value("CableLength"+phase,phase+"相电缆长度"),NumberStyles.Float,CultureInfo.InvariantCulture,out length)||length<=0)
                        length=Browser.JointPositionsByPhase.ContainsKey("ALL")?Project.CableLengthM:double.NaN;
                }
                if(!Finite(position)||!Finite(charge)||position<0)continue;
                if(!Finite(length)||length<=0||!knownStructure){unknownRows++;continue;}
                if(position>length)continue;
                phaseRows++;bool terminal=Math.Abs(position)<.001||Math.Abs(position-length)<.001;
                bool joint=joints.Any(j=>Math.Abs(position-j)<.001);
                if(kind==0&&joint||kind==1&&!joint&&!terminal||kind==2&&terminal)matches.Add(N(position)+" m / "+N(Math.Abs(charge))+" pC");
            }
            if(phaseRows==0)return unknownRows>0?"本相结构/线长不足，无法归属部位":"未有已接受定位事件";
            if(matches.Count==0)return "无对应已接受事件（非合格判定）";
            return String.Join("；",matches.Distinct().Take(8))+ (matches.Count>8?"；其余见位置分布图":"")+"（按记录位置匹配，待复核）";
        }
    }
}
