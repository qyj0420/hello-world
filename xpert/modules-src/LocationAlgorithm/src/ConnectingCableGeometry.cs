using System;
using System.Collections.Generic;

namespace HVX.Geometry
{
    /// <summary>Resolved DUT-only geometry. Input arrays and exposed arrays never share storage.</summary>
#if CONNECTING_CABLE_GEOMETRY_PUBLIC
    public sealed class ConnectingCableGeometryResult
#else
    internal sealed class ConnectingCableGeometryResult
#endif
    {
        private readonly double[] joints;
        private readonly double[] velocities;
        public double TotalLengthM { get; private set; }
        public double ConnectingCableLengthM { get; private set; }
        public double DutLengthM { get; private set; }
        public double ConnectingCableRoundTripUs { get; private set; }
        public double CalibrationDutRoundTripUs { get; private set; }
        public double GeometryDutRoundTripUs { get; private set; }
        public double CalibrationResidualUs { get; private set; }
        public bool CompensationEnabled { get; private set; }
        public bool HasCalibrationReference { get; private set; }
        public bool UsedCalibrationForUnknownVelocity { get; private set; }
        public bool GeometryCalibrationConflict { get; private set; }
        public bool CanUseCalibrationGeometry { get { return HasCalibrationReference && !GeometryCalibrationConflict; } }
        public double[] DutJointsM { get { return (double[])joints.Clone(); } }
        public double[] DutSegmentVelocitiesMPerUs { get { return (double[])velocities.Clone(); } }

        internal ConnectingCableGeometryResult(double total, double lead, double leadTime,
            double calibrationTime, double geometryTime, bool hasCalibration, bool solved,
            bool conflict, double[] boundaries, double[] speeds)
        {
            TotalLengthM = total; ConnectingCableLengthM = lead; DutLengthM = total - lead;
            CompensationEnabled = lead > 0; ConnectingCableRoundTripUs = leadTime;
            CalibrationDutRoundTripUs = calibrationTime; GeometryDutRoundTripUs = geometryTime;
            HasCalibrationReference = hasCalibration; UsedCalibrationForUnknownVelocity = solved;
            GeometryCalibrationConflict = conflict;
            CalibrationResidualUs = hasCalibration ? geometryTime - calibrationTime : double.NaN;
            joints = (double[])boundaries.Clone(); velocities = (double[])speeds.Clone();
        }

        /// <summary>
        /// Maps the measured incident/reflection delay to the DUT coordinate, 0 near and L far.
        /// The shared connecting-cable path cancels in this measured delay: do not subtract it.
        /// Signed out-of-range coordinates remain signed; callers decide acceptance separately.
        /// </summary>
        public double PositionFromMeasuredDelayUs(double measuredDelayUs)
        {
            if (!ConnectingCableGeometry.Finite(measuredDelayUs))
                throw new ArgumentOutOfRangeException("measuredDelayUs", "Measured delay must be finite.");
            double remaining = measuredDelayUs * 0.5;
            if (remaining < 0) return FinitePosition(DutLengthM - remaining * velocities[velocities.Length - 1]);
            for (int i = velocities.Length - 1; i >= 0; i--)
            {
                double time = (joints[i + 1] - joints[i]) / velocities[i];
                if (remaining <= time) return FinitePosition(joints[i + 1] - remaining * velocities[i]);
                remaining -= time;
            }
            return FinitePosition(-remaining * velocities[0]);
        }

        private static double FinitePosition(double position)
        {
            if (!ConnectingCableGeometry.Finite(position))
                throw new ArgumentOutOfRangeException("measuredDelayUs", "Measured delay produces an unrepresentable position.");
            return position;
        }
    }

    /// <summary>
    /// Pure geometry only: never changes pulse indices, measured delay, charge, amplitude or attenuation.
    /// CABLE coordinates contain the connecting lead, beginning at the device (0) and ending at the far end.
    /// </summary>
#if CONNECTING_CABLE_GEOMETRY_PUBLIC
    public static class ConnectingCableGeometry
#else
    internal static class ConnectingCableGeometry
#endif
    {
        public const double ConnectingCableVelocityMPerUs = 150.0;
        public const double MaximumPhysicalVelocityMPerUs = 299.792458;
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        /// <param name="calibrationAtDevice">Positive lead normally uses device injection. DUT-start calibration is supported for explicit internal validation only.</param>
        /// <param name="calibrationRoundTripUs">Marker-to-marker CAL delay in microseconds; NaN means unavailable.</param>
        /// <param name="explicitVelocityMask">True means an independently specified material/segment velocity; false means a header fallback that CAL may replace. Null means every segment is unknown.</param>
        /// <param name="calibrationTimingToleranceUs">Explicit marker/sample-resolution tolerance, not an amplitude or width tolerance.</param>
        public static ConnectingCableGeometryResult Build(double totalLengthM, double connectingCableLengthM,
            bool calibrationAtDevice, double calibrationRoundTripUs, double[] totalJointsM,
            double[] segmentVelocitiesMPerUs, bool[] explicitVelocityMask, double calibrationTimingToleranceUs)
        {
            if (!Finite(totalLengthM) || totalLengthM <= 0)
                throw new ArgumentOutOfRangeException("totalLengthM", "Total length must be finite and positive.");
            if (!Finite(connectingCableLengthM) || connectingCableLengthM < 0 || connectingCableLengthM >= totalLengthM)
                throw new ArgumentOutOfRangeException("connectingCableLengthM", "Lead length must be finite, nonnegative and smaller than total length.");
            if (totalJointsM == null || totalJointsM.Length < 2 || segmentVelocitiesMPerUs == null ||
                segmentVelocitiesMPerUs.Length != totalJointsM.Length - 1)
                throw new ArgumentException("Ordered boundary and velocity arrays must describe the same segments.");
            if (explicitVelocityMask != null && explicitVelocityMask.Length != segmentVelocitiesMPerUs.Length)
                throw new ArgumentException("Explicit velocity mask must match segment count.");
            if (totalJointsM[0] != 0 || totalJointsM[totalJointsM.Length - 1] != totalLengthM)
                throw new ArgumentException("Boundary array must begin at device coordinate 0 and end at total length.");
            for (int i = 0; i < totalJointsM.Length; i++)
                if (!Finite(totalJointsM[i]) || (i > 0 && totalJointsM[i] <= totalJointsM[i - 1]))
                    throw new ArgumentException("Segment boundaries must be finite and strictly increasing.");

            // Zero is a genuine opt-out. CAL, its injection position and its tolerance do not enter this path.
            if (connectingCableLengthM == 0)
            {
                double oldTime = RoundTrip(totalJointsM, segmentVelocitiesMPerUs);
                return new ConnectingCableGeometryResult(totalLengthM, 0, 0, double.NaN, oldTime,
                    false, false, false, totalJointsM, segmentVelocitiesMPerUs);
            }
            if (!Finite(calibrationTimingToleranceUs) || calibrationTimingToleranceUs < 0)
                throw new ArgumentOutOfRangeException("calibrationTimingToleranceUs", "CAL timing tolerance must be finite and nonnegative.");
            bool hasCal = !double.IsNaN(calibrationRoundTripUs);
            if (hasCal && (!Finite(calibrationRoundTripUs) || calibrationRoundTripUs <= 0))
                throw new ArgumentOutOfRangeException("calibrationRoundTripUs", "CAL delay must be positive, finite or unavailable (NaN).");
            double leadTime = 2 * connectingCableLengthM / ConnectingCableVelocityMPerUs;
            if (!Finite(leadTime)) throw new ArgumentOutOfRangeException("connectingCableLengthM", "Lead travel time overflow.");
            double calDutTime = hasCal ? calibrationRoundTripUs - (calibrationAtDevice ? leadTime : 0) : double.NaN;
            if (hasCal && (!Finite(calDutTime) || calDutTime <= 0))
                throw new ArgumentOutOfRangeException("calibrationRoundTripUs", "CAL delay does not contain a positive DUT travel time after lead compensation.");

            var boundaries = new List<double>(); var speeds = new List<double>(); var known = new List<bool>();
            boundaries.Add(0);
            double knownOneWay = 0, unknownLength = 0;
            for (int i = 0; i < segmentVelocitiesMPerUs.Length; i++)
            {
                double start = Math.Max(totalJointsM[i], connectingCableLengthM), end = totalJointsM[i + 1];
                if (end <= start) continue; // A segment wholly inside the lead is not a DUT material segment.
                bool explicitSpeed = explicitVelocityMask != null && explicitVelocityMask[i];
                double speed = segmentVelocitiesMPerUs[i];
                if (explicitSpeed && (!Finite(speed) || speed <= 0 || speed > MaximumPhysicalVelocityMPerUs))
                    throw new ArgumentException("Explicit DUT segment velocities must be finite, positive and no faster than vacuum light speed.");
                boundaries.Add(end - connectingCableLengthM); speeds.Add(speed); known.Add(explicitSpeed);
                if (explicitSpeed) knownOneWay += (end - start) / speed;
                else unknownLength += end - start;
            }
            if (speeds.Count == 0 || !Finite(knownOneWay) || !Finite(unknownLength))
                throw new ArgumentException("DUT segment travel time is invalid or overflowed.");
            bool solved = unknownLength > 0;
            if (solved)
            {
                if (!hasCal) throw new ArgumentException("CAL travel time is required to resolve unknown DUT velocities; stale header velocity is not substituted.");
                double remainingTime = calDutTime * 0.5 - knownOneWay;
                if (!Finite(remainingTime) || remainingTime <= 0)
                    throw new ArgumentException("Known DUT material travel time exhausts CAL DUT delay; unknown velocity cannot be resolved.");
                double derivedVelocity = unknownLength / remainingTime;
                if (!Finite(derivedVelocity) || derivedVelocity <= 0 || derivedVelocity > MaximumPhysicalVelocityMPerUs)
                    throw new ArgumentException("CAL-derived DUT velocity must be finite, positive and no faster than vacuum light speed; check CAL markers and lead length.");
                for (int i = 0; i < speeds.Count; i++) if (!known[i]) speeds[i] = derivedVelocity;
            }
            double[] outputJoints = boundaries.ToArray(), outputVelocities = speeds.ToArray();
            double geometryTime = RoundTrip(outputJoints, outputVelocities);
            // Include only floating-point summation uncertainty, not an implicit engineering tolerance.
            double roundoff = 32 * 2.2204460492503131e-16 * Math.Max(1, Math.Max(geometryTime, hasCal ? calDutTime : 0)) * speeds.Count;
            bool conflict = hasCal && Math.Abs(geometryTime - calDutTime) > calibrationTimingToleranceUs + roundoff;
            return new ConnectingCableGeometryResult(totalLengthM, connectingCableLengthM, leadTime,
                calDutTime, geometryTime, hasCal, solved, conflict, outputJoints, outputVelocities);
        }

        private static double RoundTrip(double[] boundaries, double[] speeds)
        {
            double time = 0;
            for (int i = 0; i < speeds.Length; i++)
            {
                if (!Finite(speeds[i]) || speeds[i] <= 0) throw new ArgumentException("Segment velocities must be finite and positive.");
                time += 2 * (boundaries[i + 1] - boundaries[i]) / speeds[i];
            }
            if (!Finite(time) || time <= 0) throw new ArgumentException("DUT round-trip travel time is invalid or overflowed.");
            return time;
        }
    }
}
