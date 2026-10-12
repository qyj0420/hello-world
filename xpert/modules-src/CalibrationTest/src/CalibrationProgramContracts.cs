using System;

namespace HVX.Controls
{
    public sealed class CalibrationRequestEventArgs : EventArgs
    {
        private readonly string[] levelCodes;
        internal CalibrationRequestEventArgs(long sequence, string actionCode, int phaseIndex,
            string levelCode, double cableLengthMeters, double velocityMPerUs,
            bool shortCable, bool bandwidthEnhancement, double bandwidthCutoffMHz,
            int incidentPeakIndex, int reflectionPeakIndex, string analysisStatusCode,
            bool allLevels, string[] levelCodes)
        {
            Sequence = sequence;
            ActionCode = actionCode;
            PhaseIndex = phaseIndex;
            PhaseCode = phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
            LevelCode = levelCode;
            CableLengthMeters = cableLengthMeters;
            VelocityMPerUs = velocityMPerUs;
            ShortCable = shortCable;
            BandwidthEnhancement = bandwidthEnhancement;
            BandwidthCutoffMHz = bandwidthCutoffMHz;
            IncidentPeakIndex = incidentPeakIndex;
            ReflectionPeakIndex = reflectionPeakIndex;
            AnalysisStatusCode = analysisStatusCode ?? String.Empty;
            AllLevels = allLevels;
            this.levelCodes = levelCodes == null ? new string[0] : (string[])levelCodes.Clone();
        }

        public long Sequence { get; private set; }
        public string ActionCode { get; private set; }
        public int PhaseIndex { get; private set; }
        public string PhaseCode { get; private set; }
        public string LevelCode { get; private set; }
        public double CableLengthMeters { get; private set; }
        public double VelocityMPerUs { get; private set; }
        public bool ShortCable { get; private set; }
        public bool BandwidthEnhancement { get; private set; }
        public double BandwidthCutoffMHz { get; private set; }
        public int IncidentPeakIndex { get; private set; }
        public int ReflectionPeakIndex { get; private set; }
        public string AnalysisStatusCode { get; private set; }
        public bool AllLevels { get; private set; }
        public string[] LevelCodes { get { return (string[])levelCodes.Clone(); } }
    }

    public sealed class CalibrationSelectionEventArgs : EventArgs
    {
        internal CalibrationSelectionEventArgs(long sequence, int phaseIndex, string levelCode)
        {
            Sequence = sequence;
            PhaseIndex = phaseIndex;
            PhaseCode = phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
            LevelCode = levelCode;
        }

        public long Sequence { get; private set; }
        public int PhaseIndex { get; private set; }
        public string PhaseCode { get; private set; }
        public string LevelCode { get; private set; }
    }

    public sealed class CalibrationJoint
    {
        internal CalibrationJoint(string id, string name, double distanceMeters,
            string materialBefore, double velocityBeforeMPerUs, string materialAfter, double velocityAfterMPerUs)
        {
            Id = id;
            Name = name;
            DistanceMeters = distanceMeters;
            MaterialBefore = materialBefore;
            VelocityBeforeMPerUs = velocityBeforeMPerUs;
            MaterialAfter = materialAfter;
            VelocityAfterMPerUs = velocityAfterMPerUs;
        }

        public string Id { get; private set; }
        public double ConfidencePercent { get; internal set; } = 100;
        public bool Confirmed { get; internal set; } = true;
        public bool AutomaticallyDetected { get; internal set; }
        public string Name { get; private set; }
        public double DistanceMeters { get; private set; }
        public string MaterialBefore { get; private set; }
        public double VelocityBeforeMPerUs { get; private set; }
        public string MaterialAfter { get; private set; }
        public double VelocityAfterMPerUs { get; private set; }
    }

    public sealed class CalibrationJointEventArgs : EventArgs
    {
        internal CalibrationJointEventArgs(long sequence, int phaseIndex, string levelCode, CalibrationJoint joint)
        {
            Sequence = sequence;
            PhaseIndex = phaseIndex;
            PhaseCode = phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
            LevelCode = levelCode;
            Joint = joint;
        }

        public long Sequence { get; private set; }
        public int PhaseIndex { get; private set; }
        public string PhaseCode { get; private set; }
        public string LevelCode { get; private set; }
        public CalibrationJoint Joint { get; private set; }
    }
}
