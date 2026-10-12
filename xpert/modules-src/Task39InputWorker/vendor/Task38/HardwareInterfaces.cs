using System;

namespace HVX.OWTS.Task38
{
    public interface IDeviceIdentity
    {
        string DeviceSerial { get; }
        string IdentityDiagnostic { get; }
    }
    public interface IDeviceConnectionProbe
    {
        string CheckConnection();
    }

    public interface IArtUsb3135Device : IDisposable
    {
        void Initialize(Task38Configuration configuration);
        bool ReadSafetyReady();
        FeedbackSample ReadFeedback();
        void WriteAoVolts(double volts);
        void SetDigitalOutputs(bool hvOn, bool hvIndicatorOn, bool connectionIndicatorOn, bool groundTriggerOn);
        void ForceSourceOff();
        void StopMonitoringInputs();
        void RestartMonitoringInputs();
        void ArmFiniteDividerCapture();
        double[] ReadFiniteDividerCapture(int timeoutMilliseconds);
        void AbortFiniteDividerCapture();
        bool FiniteCaptureArmed { get; }
    }

    public interface IPicoScope5242DBlockDevice : IDisposable
    {
        void Initialize(Task38Configuration configuration);
        void ArmBlockCapture(int rangeIndex);
        PicoBlockCapture ReadBlockCapture(int timeoutMilliseconds);
        void AbortBlockCapture();
        bool BlockCaptureArmed { get; }
    }
}
