using System;
using System.Collections.Generic;
using System.ComponentModel;
using Xiongxin.OWTS.Noise;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        readonly NoiseReferenceStore noiseReferences=new NoiseReferenceStore();
        ZeroVoltageNoiseReference currentNoiseReference;
        NoiseReferencePanel noiseReferencePanel;
        string noiseReferenceState="None";
        [Browsable(false)] public string NoiseReferenceStateCode{get{return noiseReferenceState;}}
        void InitializeNoiseReferenceUi(){noiseReferencePanel=new NoiseReferencePanel{Font=Font};charts.Panel2.Controls.Add(noiseReferencePanel);noiseReferencePanel.BringToFront();}
        public bool RegisterZeroVoltageNoiseReference(double[] samples,IDictionary<string,object> metadata)
        {
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(()=>RegisterZeroVoltageNoiseReference(samples,metadata)));
            try
            {
                var context=NoiseAcquisitionContext.Read(metadata);if(!SameAnalysisProjectPath(context.Project,projectPath))throw new ArgumentException("project_mismatch");
                var reference=new ZeroVoltageNoiseReference(samples,context);noiseReferences.Add(reference);currentNoiseReference=reference;noiseReferenceState=reference.State;
                analysisTree.SetZeroVoltageReferenceStatus(context.Source,reference.State);RefreshNoiseReferenceUi();return true;
            }
            catch(ArgumentException){noiseReferenceState="Pending";currentNoiseReference=null;RefreshNoiseReferenceUi();return false;}
            catch(NotSupportedException){noiseReferenceState="Pending";currentNoiseReference=null;RefreshNoiseReferenceUi();return false;}
        }
        public bool SetNoiseReferenceComparisonContext(IDictionary<string,object> metadata)
        {
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(()=>SetNoiseReferenceComparisonContext(metadata)));
            try
            {
                var context=NoiseAcquisitionContext.Read(metadata);if(!SameAnalysisProjectPath(context.Project,projectPath))throw new ArgumentException("project_mismatch");
                currentNoiseReference=noiseReferences.Find(context);noiseReferenceState=currentNoiseReference==null?"None":currentNoiseReference.State;RefreshNoiseReferenceUi();return currentNoiseReference!=null;
            }
            catch(ArgumentException){currentNoiseReference=null;noiseReferenceState="None";RefreshNoiseReferenceUi();return false;}
        }
        public IDictionary<string,object> GetNoiseReferenceSummary(){return currentNoiseReference==null?new Dictionary<string,object>{{"state",noiseReferenceState},{"automaticFilteringAllowed",false}}:currentNoiseReference.Summary();}
        public double[] GetNoiseReferenceSamples(){return currentNoiseReference==null?new double[0]:currentNoiseReference.CopySamples();}
        void RefreshNoiseReferenceUi(){if(noiseReferencePanel!=null){if(currentNoiseReference==null&&noiseReferenceState=="Pending")noiseReferencePanel.SetPending(languageIndex);else noiseReferencePanel.SetReference(currentNoiseReference,languageIndex);}}
        void ResetNoiseReferences(){noiseReferences.Clear();currentNoiseReference=null;noiseReferenceState="None";RefreshNoiseReferenceUi();}
    }
}
