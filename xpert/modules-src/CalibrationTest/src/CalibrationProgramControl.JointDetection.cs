using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xiongxin.Calibration;
using System.Globalization;
using HVX.ProjectCables;
using HVX.SignalProcessing;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private void DetectAndStoreJoints(int phaseIndex,string level,CalibrationCaptureResult source)
        {
            if (IsCapacitiveLoad) return;
            if(source==null||!source.Reflection.Valid||source.Windows==null||source.Windows.Length<3)return;
            var p=GetPhaseStateNoLock(phaseIndex);MarkerData marker;WaveformData raw;TdrAutoAnalysisResult analysis;
            if(!p.Markers.TryGetValue(level,out marker)||!p.Waveforms.TryGetValue(level,out raw)||!p.Analyses.TryGetValue(level,out analysis))return;
            int incident=analysis.IncidentIndex,end=analysis.ReflectionIndex;
            int radius=Math.Max(4,Math.Min(48,(end-incident)/20));
            if(incident<radius||end-incident<radius*8)return;
            // Capture windows already contain the common 10 kHz high-pass. Use
            // the currently displayed low-pass result, and apply that low-pass
            // once to each high-pass-only pulse for repeatability evidence.
            var samples=ActiveWaveformNoLock(p,level,raw).Samples;
            var windows=p.WideBand?source.Windows.Select(w=>PdLinearLowPass.Apply(w,1/source.SampleRateHz,p.BandwidthCutoffMHz)).ToArray():source.Windows;
            if(end+radius>=samples.Length||windows.Any(w=>w==null||w.Length!=samples.Length))return;
            int baselineCount=Math.Max(1,incident-radius*2);
            double mean=samples.Take(baselineCount).Average();
            double noise=Math.Max(Math.Sqrt(samples.Take(baselineCount).Average(v=>(v-mean)*(v-mean))),1e-12);
            // Scores are waveform evidence, not a probability of physical existence.
            // Require repeated incident-like packets away from incident/terminal.
            var candidates=new List<CalibrationJoint>();
            for(int i=incident+4*radius;i<end-4*radius;i++) {
                if(Math.Abs(samples[i])<6*noise||Math.Abs(samples[i])<Math.Abs(samples[i-1])||Math.Abs(samples[i])<Math.Abs(samples[i+1]))continue;
                double correlation=Math.Abs(PacketCorrelation(samples,incident,i,radius));if(correlation<.60)continue;
                int repeats=0;foreach(var w in windows)if(Math.Abs(PacketCorrelation(w,incident,i,radius))>=.60 && Math.Abs(w[i])>=4*noise)repeats++;
                double score=100*correlation*repeats/windows.Length*Math.Min(1,Math.Abs(samples[i])/(10*noise));
                if(score<50)continue;
                double distance=(i/source.SampleRateHz-marker.StartTimeSeconds)*p.VelocityMPerUs*1e6/2;
                if(distance<=0||distance>=p.CableLengthMeters)continue;
                double tolerance=Math.Max(.01,p.VelocityMPerUs*1e6/source.SampleRateHz*radius);
                if(p.Joints.Any(j=>Math.Abs(j.DistanceMeters-distance)<tolerance))continue;
                int ordinal=1+p.Joints.Count(j=>j.DistanceMeters<distance)+candidates.Count(j=>j.DistanceMeters<distance);
                var joint=new CalibrationJoint(Guid.NewGuid().ToString("N"),CableJointStore.AutomaticJointName(ordinal),distance,
                    "未指定",p.VelocityMPerUs,"未指定",p.VelocityMPerUs);
                joint.AutomaticallyDetected=true;joint.ConfidencePercent=Math.Min(100,score);joint.Confirmed=false;
                candidates.Add(joint);i+=radius*3;
                if(candidates.Count>=32)break;
            }
            // Signal score is evidence for review, never user confirmation.
            // Only the explicit joint-review operation publishes project data.
            PublishCommittedJointMarkers(candidates.Select(j=>new KeyValuePair<int,CalibrationJoint>(phaseIndex,j)));
        }
        private static double PacketCorrelation(double[] samples,int incident,int candidate,int radius)
        {
            if(samples==null||radius<1||incident-radius<0||candidate-radius<0||incident+radius>=samples.Length||candidate+radius>=samples.Length)return 0;
            double a0=0,b0=0;int count=radius*2+1;
            for(int j=-radius;j<=radius;j++){a0+=samples[incident+j]/count;b0+=samples[candidate+j]/count;}
            double ab=0,aa=0,bb=0;for(int j=-radius;j<=radius;j++){double a=samples[incident+j]-a0,b=samples[candidate+j]-b0;ab+=a*b;aa+=a*a;bb+=b*b;}
            return aa>1e-24&&bb>1e-24?ab/Math.Sqrt(aa*bb):0;
        }
        private string[] JointTargetPhases(int phaseIndex,bool ask)
        {
            string selected=PhaseCodeFromIndex(phaseIndex);
            if(SimulationMode)return new[]{selected};
            var snapshot=CableJointStore.Read(ProjectPath);string[] phases=snapshot.PhaseChoices;
            if(phases.Length==0)throw new InvalidOperationException("项目没有可识别的相别结构。");
            // Common three-core and true single-core structures have one target.
            // Repeated A blocks remain A; never infer replacement B/C labels.
            if(phases.Length==1)return phases;
            if(!ask)return phases.Where(s=>s==selected).ToArray();
            using(var form=CreateJointTargetDialog(phases,selected)) {
                var options=(CheckedListBox)form.Controls.Find("CalibrationJointTargetPhases",true)[0];
                if(form.ShowDialog(FindForm())!=DialogResult.OK)return new string[0];
                return options.CheckedItems.Cast<string>().ToArray();
            }
        }
        private Form CreateJointTargetDialog(string[] phases,string selected)
        {
            var form=new CalibrationDialogForm("CalibrationJointTargetDialog",CalibrationText("接头适用相别","Applicable joint phases"),Font,new System.Drawing.Size(480,360));
            var options=new CheckedListBox{Name="CalibrationJointTargetPhases",CheckOnClick=true,IntegralHeight=false,HorizontalScrollbar=true};
            foreach(string phase in phases)options.Items.Add(phase,selected=="ALL"||selected==phase);
            var all=new HVX.Presentation.R2RoundedButton{Name="SelectAllJointPhases",Text=CalibrationText("全选","Select all")};
            all.Click+=delegate{for(int i=0;i<options.Items.Count;i++)options.SetItemChecked(i,true);};
            var ok=new HVX.Presentation.R2RoundedButton{Name="ConfirmJointPhases",Text=CalibrationText("确认所选相别","Confirm phases"),DialogResult=DialogResult.OK};
            var cancel=new HVX.Presentation.R2RoundedButton{Text=CalibrationText("取消","Cancel"),DialogResult=DialogResult.Cancel};
            form.SetContent(new CalibrationChoiceContent(CalibrationText("请明确勾选接头适用的相别。","Select the phases to which this joint applies."),options),cancel,ok,all);
            form.AcceptButton=ok;form.CancelButton=cancel;return form;
        }
    }
}
