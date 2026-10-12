using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Data;

internal static class HybridManagedPhaseOracle
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || !Path.IsPathRooted(args[0])) throw new ArgumentException("One absolute offline input path is required.");
            if (new FileInfo(args[0]).Length > 1024 * 1024) throw new InvalidDataException("Oversized offline request.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 12 };
            var request = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0]));
            if ((string)request["schema"] != "HVX.HybridOfflinePhase/1" || (string)request["mode"] != "cycle") throw new InvalidDataException("Only offline cycle input is supported.");
            var data = (Dictionary<string, object>)request["cycleReference"];
            var times = new List<double>();
            foreach (object value in (IEnumerable)data["crossingSeconds"]) times.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            var reference = new LocationCycleReference {
                SampleRateHz = Convert.ToDouble(data["sampleRateHz"], CultureInfo.InvariantCulture),
                SampleCount = Convert.ToInt32(data["sampleCount"], CultureInfo.InvariantCulture),
                CaptureStartSeconds = Convert.ToDouble(data["captureStartSeconds"], CultureInfo.InvariantCulture),
                FirstCrossingPositive = (bool)data["firstCrossingPositive"], CrossingSeconds = times.ToArray()
            };
            reference.Validate();
            var phases = new List<double>();
            foreach (object value in (IEnumerable)request["sampleIndices"])
            {
                if (phases.Count >= 100000) throw new InvalidDataException("Too many oracle samples.");
                double phase = reference.PhaseAt(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                if (!LocationCycleReference.Finite(phase)) throw new InvalidDataException("Oracle rejected the sample index.");
                phases.Add(phase);
            }
            Console.WriteLine(serializer.Serialize(new Dictionary<string, object> { {"schema", "HVX.ManagedPhaseOracle/1"}, {"phasesDegrees", phases} }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
