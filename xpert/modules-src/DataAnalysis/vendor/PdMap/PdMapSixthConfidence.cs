using System;
using System.Globalization;
using System.ComponentModel;
using HVX.SignalEvidence;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        double[] sixthReferenceWave;
        int sixthReferenceIncident=-1,sixthReferenceReflection=-1;
        double sixthReferenceRate=double.NaN;
        SixthCalibrationReference sixthReference;
        string rawDampingReferenceReason="calibration_reference_unavailable";
        [Browsable(false)] public string CalibrationQualityDetails
        {
            get
            {
                SixthCalibrationReference reference;
                bool recognized=TryRawDampingReference(out reference,false);
                var quality=reference==null?new CalibrationQualityResult():reference.Quality;
                double effective,propagated;
                CalibrationQualityMath.ResolveTolerance(confidenceAttenuationTolerancePercent,calibrationToleranceAdaptationLimitPercent,
                    quality.RelativeUncertaintyPercent,1,out effective,out propagated);
                return string.Format(CultureInfo.InvariantCulture,
                    "recognized={0};reason={1};retention={2:R};incidentSnr={3:R};reflectionSnr={4:R};relativeUncertaintyPercent={5:R};requestedTolerancePercent={6:R};adaptationLimitPercent={7:R};qualityIsProbability=false;oneRoundTripEffectiveTolerancePercent={8:R}",
                    recognized,rawDampingReferenceReason,quality.Retention,quality.IncidentSnr,quality.ReflectionSnr,
                    quality.RelativeUncertaintyPercent,confidenceAttenuationTolerancePercent,calibrationToleranceAdaptationLimitPercent,effective);
            }
        }

        SixthCalibrationReference CachedRawCalibrationReference(double[] wave,int markerI,int markerR,double rate)
        {
            if(sixthReference==null||!ReferenceEquals(wave,sixthReferenceWave)||markerI!=sixthReferenceIncident||
                markerR!=sixthReferenceReflection||!rate.Equals(sixthReferenceRate))
            {
                sixthReference=SixthConfidenceMath.CalibrationFromMarkers(wave,markerI,markerR,rate);
                sixthReferenceWave=wave;sixthReferenceIncident=markerI;sixthReferenceReflection=markerR;sixthReferenceRate=rate;
            }
            return sixthReference;
        }

        // A display filter may alter peak ratios. It cannot qualify a raw CAL
        // which failed the same physical reference checks used by scoring.
        bool TryRawDampingReference(out SixthCalibrationReference reference,bool requireDecreasingDisplay=true)
        {
            reference=null;rawDampingReferenceReason="calibration_reference_unavailable";
            if(ProjectCalibrationEnabled)
            {
                var c=selectedProjectCalibration;
                if(c==null||!c.Valid)return false;
                reference=CachedRawCalibrationReference(c.Wave,c.Incident,c.Reflection,c.Rate);
            }
            else
            {
                if(!HasCalibrationWaveform||!IsFinite(dtNs)||dtNs<=0)return false;
                // During initial loading the current matrix row is installed
                // before CaptureOriginalSnapshot; never reuse the prior row.
                double samples=matrixDriven&&positioningMatrix!=null&&currentParameterRowIndex>=0&&
                    currentParameterRowIndex<positioningMatrix.GetLength(0)&&positioningMatrix.GetLength(1)>14?
                    positioningMatrix[currentParameterRowIndex,14]:initialReflection-(double)initialIncident;
                if(!IsFinite(samples)||samples<2)return false;
                if(ConnectingCableLengthM>0)samples+=2*ConnectingCableLengthM/150*1000/dtNs;
                double marker=calibrationPeak+Math.Round(samples);
                if(!IsFinite(marker)||marker<0||marker>=calibration.Length)return false;
                reference=CachedRawCalibrationReference(calibration,calibrationPeak,(int)marker,1e9/dtNs);
            }
            rawDampingReferenceReason=reference.Reason;
            if(requireDecreasingDisplay&&reference.Available&&reference.Retention>=1)
            {rawDampingReferenceReason="NonDecreasingRawCalibration";return false;}
            return reference.Available;
        }
        // Method 6 has an independent, tolerance-first scoring contract.
        // Keep the historical and method-7 paths unchanged.
        void UpdateSixthManualConfidence()
        {
            int delay=reflection-incident;
            if(!reflectionSelected)
            {confidenceStatusCode="ReflectionNotSelected";return;}
            if(!IsFinite(dtNs)||dtNs<=0||originalParameterRow.Length<=7||
                !IsFinite(originalParameterRow[7])||originalParameterRow[7]<=0||
                !IsFinite(originalParameterRow[5])||originalParameterRow[5]<=0||
                !velocityConfigured||!IsFinite(cableLengthM)||cableLengthM<=0||!IsFinite(currentLocationM))
            {confidenceStatusCode="GeometryUnavailable";return;}
            if(delay<=0){confidence=0;confidenceStatusCode="NonpositiveReflectionDelay";return;}
            bool outside=!BoundaryConfidenceMath.WithinTolerance(currentLocationM,cableLengthM);
            if((long)incident+n2>=reflection-(long)n1)
            {confidence=0;confidenceStatusCode=outside?"OutsideCableRange":"OverlappingPulseWindows";return;}
            double ampA,ampB,rho;
            if(!TryConfidenceWindows(out ampA,out ampB,out rho))
            {
                if(outside){confidence=0;confidenceStatusCode="OutsideCableRange";}
                else confidenceStatusCode="PulseEvidenceUnavailable";
                return;
            }
            // Correlation is a measured quantity, not a geometry validity flag.
            // An out-of-range location must not turn a measured rho into zero.
            correlation=100*rho;
            if(outside){confidence=0;confidenceStatusCode="OutsideCableRange";return;}
            if(rho<=0){confidence=0;confidenceStatusCode="OppositeOrUncorrelatedPulse";return;}
            double sigma=Safe(originalParameterRow,28,double.NaN),threshold=Safe(originalParameterRow,4,double.NaN);
            if(!IsFinite(sigma)||sigma<=0){confidenceStatusCode="NoiseEvidenceUnavailable";return;}
            if(!IsFinite(threshold)||threshold<0){confidenceStatusCode="IncidentThresholdUnavailable";return;}
            if(ampA<threshold){confidence=0;confidenceStatusCode="IncidentBelowOutputThreshold";return;}
            if(ampA/sigma<4.5||ampB/sigma<4.5)
            {confidence=0;confidenceStatusCode="PulseSignalToNoiseBelowFloor";return;}
            double retention,travelNs;string basis,referenceError;
            if(!TrySixthManualReference(out retention,out travelNs,out basis,out referenceError))
            {confidenceStatusCode=referenceError;confidenceDetails="math="+SixthConfidenceMath.Version+";reference="+basis;return;}
            var att=SixthConfidenceMath.Attenuation(ampA,ampB,retention,delay*dtNs*1e-9,
                travelNs*1e-9,confidenceAttenuationTolerancePercent,true,sixthReference.Quality.RelativeUncertaintyPercent,calibrationToleranceAdaptationLimitPercent);
            if(!att.Available){confidenceStatusCode="AttenuationEvidenceUnavailable";confidenceDetails=att.Reason;return;}
            // Same declared default contributions as the sixth method. The
            // manual window does not invent automatic width/lobe evidence.
            double score=SixthConfidenceMath.WeightedScore(new[]{rho,att.Score,double.NaN,double.NaN},
                new[]{50.0,35.0,10.0,5.0},new[]{true,true,false,true});
            score=Math.Min(score,SixthConfidenceMath.AttenuationCeiling(true,true,att.BasePassed,att.Score));
            confidence=100*score;
            confidenceStatusCode=att.Passed?"ManualEvidenceMeasured":"AttenuationConflict";
            confidenceDetails=string.Format(CultureInfo.InvariantCulture,
                "math={0};domain=raw-baseline-corrected;reference={1};calRetention={2:R};calTravelNs={3:R};observedRatio={4:R};expectedRatio={5:R};relativeErrorPercent={6:R};tolerancePercent={7:R};attenuationScore={8:R};outsideToleranceCannotAutoAccept=true;scoreIsProbability=false;automaticPacketGuardsAvailable=false;rawSigma={9:R};effectiveTolerancePercent={10:R};calibrationUncertaintyPercent={11:R};propagatedUncertaintyPercent={12:R};adaptationLimitPercent={13:R};baseTolerancePassed={14}",
                SixthConfidenceMath.Version,basis,retention,travelNs,att.ObservedRatio,att.ExpectedRatio,
                att.ErrorPercent,confidenceAttenuationTolerancePercent,att.Score,sigma,att.EffectiveTolerancePercent,
                sixthReference.Quality.RelativeUncertaintyPercent,att.PropagatedUncertaintyPercent,att.AdaptationLimitPercent,att.BasePassed);
        }

        bool TrySixthManualReference(out double retention,out double travelNs,out string basis,out string error)
        {
            retention=travelNs=double.NaN;basis="unavailable";error="AttenuationReferenceUnavailable";
            double[] wave;int markerI,markerR;double rate;
            if(ProjectCalibrationEnabled)
            {
                basis="project_CAL_raw_display_peak_ratio";
                if(selectedProjectCalibration==null||!selectedProjectCalibration.Valid)return false;
                wave=selectedProjectCalibration.Wave;markerI=selectedProjectCalibration.Incident;
                markerR=selectedProjectCalibration.Reflection;rate=selectedProjectCalibration.Rate;
            }
            else
            {
                // A supplied decay curve alone cannot prove the physical CAL
                // reflection amplitude. Check the actual CAL samples at the
                // fixed calibration round-trip interval, never the PD cursor.
                basis="legacy_CAL_peaks_at_fixed_calibration_geometry";
                if(!HasCalibrationWaveform||!IsFinite(dtNs)||dtNs<=0)return false;
                double samples=Safe(originalParameterRow,14,double.NaN);
                if(!IsFinite(samples)||samples<2)return false;
                if(ConnectingCableLengthM>0)samples+=2*ConnectingCableLengthM/150*1000/dtNs;
                double marker=calibrationPeak+Math.Round(samples);
                if(!IsFinite(marker)||marker<0||marker>=calibration.Length)return false;
                wave=calibration;markerI=calibrationPeak;markerR=(int)marker;rate=1e9/dtNs;
            }
            // CAL sorting and quiet-window estimation happen only when the
            // calibration context changes, not for every reflection drag.
            CachedRawCalibrationReference(wave,markerI,markerR,rate);
            if(!sixthReference.Available)
            {
                error="CalibrationReferenceUnavailable";
                basis+="/"+sixthReference.Reason;return false;
            }
            retention=sixthReference.Retention;travelNs=sixthReference.TravelSeconds*1e9;
            return true;
        }
    }
}
