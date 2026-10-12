using System;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>
    /// 缺 DLL 实现：Evaluate 恒 MissingDll、FirstZero=NaN；不抛死。
    /// </summary>
    public sealed class MissingDllFirstZeroPhaseEvaluator : IFirstZeroPhaseEvaluator
    {
        readonly string _configuredDirectory;
        readonly string _detail;

        public MissingDllFirstZeroPhaseEvaluator(string configuredDirectory = null, string detail = null)
        {
            _configuredDirectory = configuredDirectory ?? string.Empty;
            _detail = string.IsNullOrWhiteSpace(detail)
                ? "Task6 first-zero DLL not provisioned; StatusCode=MissingDll. Heuristic Task6RangeAdvisor remains available."
                : detail;
        }

        public Task6DllStatus GetDllStatus()
        {
            var probe = Task6RuntimeProbe.Probe(string.IsNullOrWhiteSpace(_configuredDirectory) ? null : _configuredDirectory);
            return new Task6DllStatus
            {
                ConfiguredDirectory = !string.IsNullOrEmpty(_configuredDirectory)
                    ? _configuredDirectory
                    : (probe.Directory ?? string.Empty),
                PathConfigured = !string.IsNullOrWhiteSpace(_configuredDirectory)
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Task6RuntimeProbe.EnvRuntimeDir)),
                TrioPresent = probe.TrioPresent,
                EngineLoaded = false,
                EngineDllPath = probe.EngineDllPath ?? string.Empty,
                Detail = probe.TrioPresent
                    ? ("Trio on disk but MissingDll evaluator selected: " + probe.Detail)
                    : (string.IsNullOrEmpty(probe.Detail) ? _detail : probe.Detail)
            };
        }

        public FirstZeroPhaseEvaluationResult Evaluate(FirstZeroPhaseEvaluationRequest request)
        {
            if (request != null && request.IsBackgroundCapture)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = false,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusBackgroundSkipped,
                    Diagnostic = "Background capture: first-zero phase evaluation skipped.",
                    FirstZeroTimeSeconds = double.NaN
                };
            }
            Task6DllStatus st = GetDllStatus();
            return FirstZeroPhaseEvaluationResult.MissingDll(
                "MissingDll: " + (string.IsNullOrEmpty(st.Detail) ? _detail : st.Detail));
        }

        public void Dispose() { }
    }
}
