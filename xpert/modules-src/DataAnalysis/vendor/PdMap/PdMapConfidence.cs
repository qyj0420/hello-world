using System;
using System.ComponentModel;
using System.Globalization;
using HVX.SignalEvidence;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        double confidenceAttenuationTolerancePercent=15;
        double calibrationToleranceAdaptationLimitPercent=CalibrationQualityMath.DefaultAdaptationLimitPercent;
        string confidenceStatusCode="InputMatrix",confidenceDetails="";
        [Category("Analysis"),DefaultValue(15.0)]
        [Description("Reflection-amplitude tolerance relative to the CAL prediction, in percent. Match Task17's explicit attenuationErrorPercent input.")]
        public double ConfidenceAttenuationTolerancePercent
        {
            get{return confidenceAttenuationTolerancePercent;}
            set
            {
                if(!IsFinite(value)||value<0||value>100)throw new ArgumentOutOfRangeException("value");
                confidenceAttenuationTolerancePercent=value;
            }
        }
        [Category("Analysis"),DefaultValue(CalibrationQualityMath.DefaultAdaptationLimitPercent)]
        [Description("Engineering upper limit for calibration-uncertainty tolerance adaptation; never lowers the requested tolerance or increases the evidence score.")]
        public double CalibrationToleranceAdaptationLimitPercent
        {
            get{return calibrationToleranceAdaptationLimitPercent;}
            set{if(!IsFinite(value)||value<0||value>100)throw new ArgumentOutOfRangeException("value");calibrationToleranceAdaptationLimitPercent=value;}
        }
        [Browsable(false)] public string ConfidenceStatusCode {get{return confidenceStatusCode;}}
        [Browsable(false)] public string ConfidenceDetails {get{return confidenceDetails;}}
        [Browsable(false)] public string ConfidenceMathVersion {get{return reflectionSelected&&reflection==incident?CoincidentConfidenceMath.Version:originalParameterRow.Length>26&&originalParameterRow[26]==6?SeventhPipelineMath.Version:SixthConfidenceMath.Version;}}
        public void ReevaluateConfidence()
        {
            if(!matrixDriven)return;
            UpdateDynamicAnalysis();Invalidate();NotifyReviewAnalysisEdited("Confidence");
        }

        void UpdateConfidenceEvidence()
        {
            confidence=double.NaN;correlation=double.NaN;confidenceDetails="";coincidentIndependentCount=0;
            if(!reflectionSelected){confidenceStatusCode="ReflectionNotSelected";return;}
            if(!ReflectionWithinWaveform){confidenceStatusCode="ReflectionOutsideWaveform";return;}
            if(reflection==incident){UpdateCoincidentConfidence();return;}
            if(reflection<incident)
            {
                double a,b,rho;if(TryConfidenceWindows(out a,out b,out rho))correlation=rho*100;
                confidenceStatusCode="ReflectionBeforeIncidentReviewOnly";return;
            }
            // Methods 1-6 use the same raw-CAL, tolerance-first evidence kernel.
            if(originalParameterRow.Length<=26||originalParameterRow[26]!=6)
            {UpdateSixthManualConfidence();return;}
            if(!IsFinite(dtNs)||dtNs<=0||originalParameterRow.Length<=7||!IsFinite(originalParameterRow[7])||originalParameterRow[7]<=0||
                !IsFinite(originalParameterRow[5])||originalParameterRow[5]<=0||!velocityConfigured||!IsFinite(cableLengthM)||cableLengthM<=0)
            {confidenceStatusCode="GeometryUnavailable";return;}
            if(reflection<incident){confidence=0;confidenceStatusCode="NonpositiveReflectionDelay";return;}
            if(!IsFinite(currentLocationM)){confidenceStatusCode="GeometryUnavailable";return;}
            if(!BoundaryConfidenceMath.WithinTolerance(currentLocationM,cableLengthM))
            {confidence=0;confidenceStatusCode="OutsideCableRange";return;}
            UpdateSeventhManualConfidence();
        }
        // Used by the display-only attenuation curve alignment; scoring uses
        // the original CAL pair through the shared tolerance-first kernel.
        bool TryConfidenceDecaySlope(out double slope)
        {
            slope=double.NaN;
            if(ProjectCalibrationEnabled)
            {
                if(selectedProjectCalibration==null||!IsFinite(projectDecaySlopePerNs)||projectDecaySlopePerNs>=0)return false;
                slope=projectDecaySlopePerNs;return true;
            }
            if(attenuation==null||attenuation.Length<3)return false;
            double meanX=0,meanY=0,sxx=0,sxy=0;int count=0;
            for(int i=0;i<attenuation.Length;i++)
            {
                double a=Math.Abs(attenuation[i]);if(!IsFinite(a))return false;if(a==0)continue;
                double y=Math.Log(a),dx=i-meanX,dy=y-meanY;count++;
                meanX+=dx/count;meanY+=dy/count;sxx+=dx*(i-meanX);sxy+=dx*(y-meanY);
            }
            if(count<3||!IsFinite(sxx)||sxx<=0||!IsFinite(sxy))return false;
            slope=sxy/sxx/dtNs;return IsFinite(slope)&&slope<0;
        }
        bool TryConfidenceWindows(out double ampA,out double ampB,out double rho)
        {
            ampA=ampB=rho=double.NaN;
            long countLong=(long)n1+n2+1;
            if(countLong<3||countLong>pulse.Length||incident-n1<0||reflection-n1<0||
                (long)incident+n2>=pulse.Length||(long)reflection+n2>=pulse.Length)return false;
            int count=(int)countLong;
            double meanA=0,meanB=0;
            for(int k=-n1;k<=n2;k++)
            {double a=pulse[incident+k],b=pulse[reflection+k];if(!IsFinite(a)||!IsFinite(b))return false;meanA+=a/count;meanB+=b/count;}
            double aa=0,bb=0,ab=0;
            // Endpoint mean removes a constant background without letting a
            // positive/negative packet cancel its peak amplitude.
            double baselineA=pulse[incident-n1]/2+pulse[incident+n2]/2;
            double baselineB=pulse[reflection-n1]/2+pulse[reflection+n2]/2;
            ampA=ampB=0;
            for(int k=-n1;k<=n2;k++)
            {
                double a=pulse[incident+k]-meanA,b=pulse[reflection+k]-meanB;
                aa+=a*a;bb+=b*b;ab+=a*b;
                ampA=Math.Max(ampA,Math.Abs(pulse[incident+k]-baselineA));
                ampB=Math.Max(ampB,Math.Abs(pulse[reflection+k]-baselineB));
            }
            if(!IsFinite(aa)||!IsFinite(bb)||!IsFinite(ab)||aa<=0||bb<=0||ampA<=0||ampB<=0)return false;
            rho=ab/Math.Sqrt(aa)/Math.Sqrt(bb);
            if(!IsFinite(rho))return false;rho=Math.Max(-1,Math.Min(1,rho));return true;
        }
    }
}
