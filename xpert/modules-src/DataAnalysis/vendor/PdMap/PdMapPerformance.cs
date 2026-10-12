using System;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // All keys below refer only to control-owned snapshots, never caller arrays.
        sealed class FilterCache
        {
            double[] source, settings, result;
            double rate;
            string status;
            internal double[] Apply(double[] values, double fs, double[] parameters, out string message)
            {
                if (!ReferenceEquals(source, values) || !rate.Equals(fs) || !SameValues(settings, parameters))
                {
                    result = PdMapSignalFilter.Apply(values, fs, parameters, out status);
                    source = values; rate = fs; settings = (double[])parameters.Clone();
                }
                message = status;
                return result;
            }
        }
        readonly FilterCache pulseFilterCache = new FilterCache();
        readonly FilterCache calibrationFilterCache = new FilterCache();
        double[] alignmentPulse, alignmentCalibration, alignmentAttenuation;
        int alignmentIncident = -1, alignmentReflection = -1, alignmentPeak = -1;
        double[,] countedMatrix;
        double[] sortedLocations = Array.Empty<double>();
        double[] loadedPulse, loadedCalibration, loadedAttenuation, loadedParameters;
        double[,] builtOverlayMatrix;
        double[] builtPositions, builtCharges;
        int loadedRow = -1;

        void RememberLoadedInputs()
        {
            loadedPulse=pulse; loadedCalibration=calibration; loadedAttenuation=attenuation;
            loadedParameters=originalParameterRow; loadedRow=currentParameterRowIndex;
        }

        static bool SameMatrix(double[,] a, double[,] b)
        {
            if (a == null || b == null) return a == b;
            int rows = a.GetLength(0), columns = a.GetLength(1);
            if (rows != b.GetLength(0) || columns != b.GetLength(1)) return false;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    if (!a[r, c].Equals(b[r, c])) return false;
            return true;
        }

        static bool UpdateMatrixSnapshot(ref double[,] snapshot, double[,] input)
        {
            if (SameMatrix(snapshot, input)) return false;
            snapshot = CloneMatrix(input);
            return true;
        }

        void EnsureLocationIndex()
        {
            if (ReferenceEquals(countedMatrix, positioningMatrix)) return;
            countedMatrix = positioningMatrix;
            if (!IsUsableMatrix(positioningMatrix, 14)) { sortedLocations = Array.Empty<double>(); return; }
            var values = new System.Collections.Generic.List<double>(positioningMatrix.GetLength(0));
            for (int r = 0; r < positioningMatrix.GetLength(0); r++)
                if (IsFinite(positioningMatrix[r, 13])) values.Add(positioningMatrix[r, 13]);
            sortedLocations = values.ToArray(); Array.Sort(sortedLocations);
        }

        int CountLocationsNear(double location, double tolerance)
        {
            EnsureLocationIndex();
            if (!IsFinite(location) || double.IsNaN(tolerance)) return 0;
            int low = 0, high = sortedLocations.Length;
            while (low < high)
            {
                int m = low + (high - low) / 2;
                if (sortedLocations[m] < location && Math.Abs(sortedLocations[m] - location) > tolerance) low = m + 1;
                else high = m;
            }
            int first = low; high = sortedLocations.Length;
            while (low < high)
            {
                int m = low + (high - low) / 2;
                if (sortedLocations[m] > location && Math.Abs(sortedLocations[m] - location) > tolerance) high = m;
                else low = m + 1;
            }
            return low - first;
        }
    }
}
