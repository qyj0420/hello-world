using System;
using System.IO;
using System.Reflection;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>
    /// Optional reflection host for Task6 WavePreprocessEngine.AnalyzeWithLinearity (29 args).
    /// DLL path: ctor / OWTS_TASK6_RUNTIME_DIR / AppBase[/Task6].
    /// Load or invoke failure → soft Status（不抛死）；不伪造 FirstZeroTimeSeconds。
    /// </summary>
    public sealed class ReflectiveFirstZeroPhaseEvaluator : IFirstZeroPhaseEvaluator
    {
        readonly string _explicitDirectory;
        readonly object _gate = new object();
        MethodInfo _analyze;
        object _engine;
        string _loadedDirectory = string.Empty;
        string _loadError = string.Empty;
        bool _loadAttempted;

        public ReflectiveFirstZeroPhaseEvaluator(string runtimeDirectory = null)
        {
            _explicitDirectory = string.IsNullOrWhiteSpace(runtimeDirectory)
                ? string.Empty
                : runtimeDirectory.Trim().Trim('"');
        }

        public Task6DllStatus GetDllStatus()
        {
            Task6RuntimeProbe.ProbeResult probe = Task6RuntimeProbe.Probe(
                string.IsNullOrWhiteSpace(_explicitDirectory) ? null : _explicitDirectory);
            var status = new Task6DllStatus
            {
                ConfiguredDirectory = !string.IsNullOrEmpty(_explicitDirectory)
                    ? _explicitDirectory
                    : (probe.Directory ?? string.Empty),
                PathConfigured = !string.IsNullOrWhiteSpace(_explicitDirectory)
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Task6RuntimeProbe.EnvRuntimeDir))
                    || probe.TrioPresent,
                TrioPresent = probe.TrioPresent,
                EngineDllPath = probe.EngineDllPath ?? string.Empty
            };

            if (!probe.TrioPresent)
            {
                status.EngineLoaded = false;
                status.Detail = string.IsNullOrEmpty(probe.Detail)
                    ? "Task6 trio missing."
                    : probe.Detail;
                return status;
            }

            try
            {
                EnsureEngine(probe);
            }
            catch (Exception ex)
            {
                status.EngineLoaded = false;
                status.Detail = "Load failed: " + OneLine(ex)
                    + (string.IsNullOrEmpty(_loadError) ? string.Empty : "; " + _loadError);
                return status;
            }

            status.EngineLoaded = _engine != null && _analyze != null;
            if (!string.IsNullOrEmpty(_loadError) && !status.EngineLoaded)
                status.Detail = _loadError;
            else if (status.EngineLoaded)
                status.Detail = "Engine loaded from " + _loadedDirectory
                    + "; type=" + Task6RuntimeProbe.EngineTypeName
                    + "; method=" + Task6RuntimeProbe.AnalyzeMethodName;
            else
                status.Detail = string.IsNullOrEmpty(_loadError) ? probe.Detail : _loadError;
            return status;
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

            Task6RuntimeProbe.ProbeResult probe = Task6RuntimeProbe.Probe(
                string.IsNullOrWhiteSpace(_explicitDirectory) ? null : _explicitDirectory);
            if (!probe.TrioPresent)
                return FirstZeroPhaseEvaluationResult.MissingDll(
                    string.IsNullOrEmpty(probe.Detail) ? "Task6 trio missing." : probe.Detail);

            if (request == null)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = false,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusInvalidInput,
                    Diagnostic = "Null evaluation request.",
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            if (request.PdRawVolts == null || request.PdRawVolts.Length == 0)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = false,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusInvalidInput,
                    Diagnostic = "PdRawVolts empty; cannot evaluate first-zero phase.",
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            try
            {
                EnsureEngine(probe);
            }
            catch (Exception ex)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = false,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusDllLoadFailed,
                    Diagnostic = "DllLoadFailed: " + OneLine(ex)
                        + (string.IsNullOrEmpty(_loadError) ? string.Empty : "; " + _loadError),
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            if (_engine == null || _analyze == null)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = false,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusDllLoadFailed,
                    Diagnostic = string.IsNullOrEmpty(_loadError) ? "Engine not loaded." : _loadError,
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            double[] ranges = request.PdRangeValuesVolts != null && request.PdRangeValuesVolts.Length > 0
                ? request.PdRangeValuesVolts
                : Task6RangeAdvisor.Pico5242DFullScaleVolts;
            double dividerKvPerV = (request.DividerRatioVPerV > 0 ? request.DividerRatioVPerV : 11000.0) / 1000.0;
            double acDividerKvPerV = (request.AcDividerRatioVPerV > 0 ? request.AcDividerRatioVPerV : 11000.0) / 1000.0;
            double vCal = request.VoltageCalibration > 0 ? request.VoltageCalibration : 1.0;
            double pdCal = request.PdCalibrationPcPerVolt > 0 ? request.PdCalibrationPcPerVolt : 1.0;
            double L = request.InductanceH > 0 ? request.InductanceH : 1.0;
            double dcr = request.InductorDcrOhm >= 0 && !double.IsNaN(request.InductorDcrOhm) ? request.InductorDcrOhm : 0.0;
            double targetKv = request.TargetVoltageV / 1000.0;

            // Mirror Task38Task6Analyzer argument layout (29 parameters; last two are out-results).
            object[] args = new object[]
            {
                request.VoltageRawVolts ?? new double[0],
                request.VoltageSampleRateHz,
                request.PdRawVolts,
                request.PdSampleRateHz,
                request.VoltageSampleRateHz,
                request.PretriggerMicroseconds,
                request.TransientSkipMicroseconds,
                dividerKvPerV,
                acDividerKvPerV,
                vCal,
                pdCal,
                false,
                new double[0, 2],
                L,
                dcr,
                0.0,
                0.0,
                ranges,
                request.CurrentPdRangeIndex,
                0.0,
                0.0,
                0,
                0,
                0,
                3,
                new double[] { 20.0, 5000.0 },
                targetKv,
                null,
                null
            };

            try
            {
                lock (_gate)
                {
                    _analyze.Invoke(_engine, args);
                }
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = true,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationFailed,
                    Diagnostic = "AnalyzeWithLinearity failed: " + OneLine(inner),
                    ErrorMessage = OneLine(inner),
                    FirstZeroTimeSeconds = double.NaN
                };
            }
            catch (Exception ex)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = true,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationFailed,
                    Diagnostic = "Invoke failed: " + OneLine(ex),
                    ErrorMessage = OneLine(ex),
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            object pd = args[27];
            if (pd == null)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = true,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationFailed,
                    Diagnostic = "Task6 returned null PD result object.",
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            try
            {
                var result = new FirstZeroPhaseEvaluationResult
                {
                    UsedDll = true,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationOk,
                    Task6Success = Get<bool>(pd, "Success"),
                    Task6Status = Get<int>(pd, "Status"),
                    ErrorMessage = Get<string>(pd, "ErrorMessage") ?? string.Empty,
                    QualityFlags = Get<int>(pd, "QualityFlags"),
                    PdOverRange = Get<bool>(pd, "PdOverRange"),
                    MultipleClippedPulses = Get<bool>(pd, "MultipleClippedPulses"),
                    RangeSuitable = Get<bool>(pd, "RangeSuitable"),
                    RangeAdjustmentDirection = Get<int>(pd, "RangeAdjustmentDirection"),
                    BestRangeIndex = Get<int>(pd, "BestRangeIndex"),
                    PdMaximumAbsolute = Get<double>(pd, "PdMaximumAbsolute"),
                    FirstZeroTimeSeconds = Get<double>(pd, "FirstZeroTimeSeconds")
                };
                // Never leave a non-finite "fake" zero when Success says no oscillation.
                if (!result.Task6Success
                    || double.IsNaN(result.FirstZeroTimeSeconds)
                    || double.IsInfinity(result.FirstZeroTimeSeconds)
                    || result.FirstZeroTimeSeconds < 0.0)
                {
                    result.FirstZeroTimeSeconds = double.NaN;
                }
                result.Success = result.Task6Success
                    && !double.IsNaN(result.FirstZeroTimeSeconds);
                result.Diagnostic = result.Success
                    ? ("EvaluationOk firstZero_s=" + result.FirstZeroTimeSeconds.ToString("G6")
                        + " bestRange=" + result.BestRangeIndex
                        + " suitable=" + result.RangeSuitable)
                    : ("DLL returned Task6Success=" + result.Task6Success
                        + " status=" + result.Task6Status
                        + " err=" + result.ErrorMessage
                        + " (no forged first-zero)");
                if (!result.Success && result.StatusCode == FirstZeroPhaseEvaluationResult.StatusEvaluationOk)
                    result.StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationFailed;
                return result;
            }
            catch (Exception ex)
            {
                return new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = true,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationFailed,
                    Diagnostic = "Read PD result failed: " + OneLine(ex),
                    FirstZeroTimeSeconds = double.NaN
                };
            }
        }

        void EnsureEngine(Task6RuntimeProbe.ProbeResult probe)
        {
            lock (_gate)
            {
                if (_engine != null && _analyze != null) return;
                if (_loadAttempted && _engine == null)
                    throw new InvalidOperationException(string.IsNullOrEmpty(_loadError) ? "Previous load failed." : _loadError);

                _loadAttempted = true;
                _loadError = string.Empty;
                try
                {
                    Assembly.LoadFrom(probe.DependencyDllPath);
                    Assembly engineAsm = Assembly.LoadFrom(probe.EngineDllPath);
                    if (!File.Exists(probe.NativeDllPath))
                        throw new FileNotFoundException("Task6 native dependency missing.", probe.NativeDllPath);

                    Type t = engineAsm.GetType(Task6RuntimeProbe.EngineTypeName, throwOnError: true);
                    _engine = Activator.CreateInstance(t);
                    _analyze = t.GetMethod(Task6RuntimeProbe.AnalyzeMethodName);
                    if (_analyze == null || _analyze.GetParameters().Length != Task6RuntimeProbe.AnalyzeParameterCount)
                        throw new InvalidOperationException(
                            "Task6 AnalyzeWithLinearity interface mismatch (expected "
                            + Task6RuntimeProbe.AnalyzeParameterCount + " parameters).");
                    _loadedDirectory = probe.Directory;
                }
                catch (Exception ex)
                {
                    _engine = null;
                    _analyze = null;
                    _loadError = OneLine(ex);
                    throw;
                }
            }
        }

        static T Get<T>(object o, string name)
        {
            PropertyInfo p = o.GetType().GetProperty(name);
            if (p == null) throw new MissingMemberException(o.GetType().FullName, name);
            object v = p.GetValue(o, null);
            if (v == null) return default(T);
            return (T)v;
        }

        static string OneLine(Exception ex)
        {
            if (ex == null) return string.Empty;
            string m = ex.GetType().Name + ": " + (ex.Message ?? string.Empty);
            return m.Replace('\r', ' ').Replace('\n', ' ');
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _engine = null;
                _analyze = null;
            }
        }
    }
}
