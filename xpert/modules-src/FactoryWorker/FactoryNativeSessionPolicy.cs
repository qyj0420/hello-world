using System;

namespace Xiongxin.FactoryTest
{
    // Validation is separate from native operations so identity and per-round
    // immutability can be checked offline without loading either device SDK.
    internal static class FactoryNativeSessionPolicy
    {
        internal static FactorySettings PrepareOpen(FactorySettings requested)
        {
            if (requested == null || requested.Input == null) throw new ArgumentNullException("requested");
            if (requested.Simulation) throw new ArgumentException("仿真请求不能打开真实设备会话。");
            var copy = requested.Clone();
            copy.PressureFeedbackContext=false; // Opening an idle session is not a pressure run.
            if(copy.ManualZeroInputWithoutEmergency && (copy.TargetKv!=0 || copy.ZeroSynchronizedOptical ||
                copy.AutomaticBatch || copy.BatchStartConfirmed || copy.BatchContinuation))
                throw new InvalidOperationException("人工0 kV输入会话不允许输出或自动批次。");
            copy.Input.RealHardware = true;
            copy.Input.Validate();
            NativeFactoryBackend.ValidatePhysicalContract(copy.Input);
            return copy;
        }

        internal static FactorySettings PrepareRound(FactorySettings requested, bool calibration)
        {
            if (requested == null || requested.Input == null) throw new ArgumentNullException("requested");
            if (requested.Simulation) throw new ArgumentException("仿真请求不能使用真实设备会话。");
            if (!calibration && requested.TargetKv == 0 && !requested.ZeroSynchronizedOptical)
                throw new InvalidOperationException("0 kV 必须走独立只读自由采集分支，禁止初始化 AO/DO 或 HV/光输出。");
            var copy = requested.Clone();
            copy.PressureFeedbackContext=!calibration&&!copy.ManualZeroInputWithoutEmergency;
            if(copy.AutomaticBatch && (calibration || copy.BatchPermit==null || !copy.BatchPermit.Matches(copy)))
                throw new InvalidOperationException("自动批次执行缺少当前设备服务的运行时授权，不能使用保存的声明放行。");
            copy.Validate(calibration ? FactoryRunKind.Calibration : FactoryRunKind.Pressure);
            copy.Input.ValidateNewCaptureDuration();
            NativeFactoryBackend.ValidatePhysicalContract(copy.Input);
            return copy;
        }

        internal static FactorySettings PrepareQueuedRound(FactorySettings requested,bool calibration)
        {
            if(requested==null || requested.Input==null)throw new ArgumentNullException("requested");
            if(!calibration && requested.TargetKv==0 && !requested.ZeroSynchronizedOptical)
                return PrepareZeroInputRound(requested);
            if(!requested.AutomaticBatch)return PrepareRound(requested,calibration);
            if(requested.Simulation || calibration || requested.TargetKv<=0 || requested.ZeroSynchronizedOptical)
                throw new InvalidOperationException("排队的自动批次只接受实机非零加压。");
            var copy=requested.Clone(); copy.BatchPermit=null;
            copy.PressureFeedbackContext=true;
            copy.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            copy.Input.ValidateNewCaptureDuration(); NativeFactoryBackend.ValidatePhysicalContract(copy.Input);
            return copy;
        }

        internal static FactorySettings PrepareZeroInputRound(FactorySettings requested)
        {
            if(requested==null || requested.Input==null)throw new ArgumentNullException("requested");
            if(requested.Simulation || requested.TargetKv!=0 || requested.ZeroSynchronizedOptical ||
                requested.AutomaticBatch || requested.BatchContinuation)
                throw new InvalidOperationException("常驻只读输入仅接受手动0 kV自由采集；不授予自动加压或光触发许可。");
            var copy=requested.Clone();copy.BatchPermit=null;
            copy.PressureFeedbackContext=!copy.ManualZeroInputWithoutEmergency;
            copy.Validate(FactoryRunKind.Pressure);copy.Input.ValidateNewCaptureDuration();
            NativeFactoryBackend.ValidatePhysicalContract(copy.Input);
            return copy;
        }

        internal static bool SameDeviceSession(FactorySettings opened, FactorySettings next)
        {
            if (opened == null || next == null || opened.Input == null || next.Input == null ||
                opened.Simulation || next.Simulation) return false;
            // Input-only and supervised HV sessions cannot share a capability.
            // The resident owner must successfully close the old mode first.
            if(opened.ManualZeroInputWithoutEmergency!=next.ManualZeroInputWithoutEmergency)return false;
            var a = opened.Input; var b = next.Input;
            return Exact(a.ArtDevice,b.ArtDevice) && Exact(a.ArtSerial,b.ArtSerial) && Exact(a.PicoSerial,b.PicoSerial) &&
                Runtime(a.ArtLibraryDirectory,b.ArtLibraryDirectory) && Runtime(a.PicoLibraryDirectory,b.PicoLibraryDirectory);
        }
        private static bool Exact(string a,string b)
        { return String.Equals((a ?? "").Trim(),(b ?? "").Trim(),StringComparison.Ordinal); }
        private static bool Runtime(string a,string b)
        { return String.Equals((a ?? "").Trim().TrimEnd('\\','/'),(b ?? "").Trim().TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase); }
    }
}
