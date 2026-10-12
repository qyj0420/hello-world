using System;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>
    /// Creates Task6 first-zero evaluator: trio present → Reflective（失败仍软降级）；否则 MissingDll。
    /// </summary>
    public static class FirstZeroPhaseEvaluatorFactory
    {
        public static IFirstZeroPhaseEvaluator Create(string runtimeDirectory = null)
        {
            Task6RuntimeProbe.ProbeResult probe = Task6RuntimeProbe.Probe(runtimeDirectory);
            if (!probe.TrioPresent)
            {
                return new MissingDllFirstZeroPhaseEvaluator(
                    runtimeDirectory,
                    string.IsNullOrEmpty(probe.Detail)
                        ? "Task6 trio missing; StatusCode=MissingDll."
                        : probe.Detail);
            }

            // Reflective path: GetDllStatus/Evaluate will soft-fail on load errors (e.g. Linux box).
            return new ReflectiveFirstZeroPhaseEvaluator(runtimeDirectory);
        }
    }
}
