using System;
using System.Collections.Generic;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Only the resident owner thread calls these operations. P0.4 keeps its
    // existing single DI reader, in idle/read-only mode (LOW is valid input).
    internal sealed partial class NativeFactoryBackend
    {
        private bool zeroInputRound, retainedZeroAi;
        internal bool InputStopHealthy
        {
            get {
                if(!CanReuseSession||zeroInputRound||finite.Started||finiteArmed||pico.Armed||hvOn||hvCommands.MayBeOn||opticalOn||lastAo!=0)return false;
                if(settings.ManualZeroInputWithoutEmergency)return true;
                if(emergencyMonitor==null)return false;
                string detail;string state=emergencyMonitor.ConnectionState(out detail);
                return state=="HIGH"||state=="LOW";
            }
        }
        private long zeroInputGeneration;
        private int zeroAiCount;

        long IFactoryResidentZeroInputSession.BeginZeroInput(FactorySettings requested)
        {
            var next=FactoryNativeSessionPolicy.PrepareZeroInputRound(requested);
            EnsureInitialized();
            if(roundActive || initializing)throw new InvalidOperationException("上一轮尚未停止，不能开始0 kV输入轮次。");
            if(faulted || !identitiesVerified || !FactoryNativeSessionPolicy.SameDeviceSession(sessionIdentity,next))
                throw new InvalidOperationException("当前设备身份或故障状态不允许复用0 kV输入会话。");
            if(hvOn || hvCommands.MayBeOn || opticalOn || lastAo!=0 || finiteArmed || pico.Armed ||
                monitor.Handle!=IntPtr.Zero || (finite.Handle!=IntPtr.Zero && !retainedZeroAi))
                throw new InvalidOperationException("上一轮输出或采集资源仍被持有；0 kV不能接管或关闭输出。");
            // Validate a real fresh input without converting LOW into a fault
            // or calling EnterActiveReadMode (reserved for output permission).
            RequireZeroInputReadiness();
            ++zeroInputGeneration;zeroInputRound=roundActive=true;
            settings=next;calibration=false;ioTiming=new FactoryIoTimingDiagnostics();
            try
            {
                VerifyArtIdentity();pico.Configure(next.Input,true);
                if(retainedZeroAi && zeroAiCount!=next.Input.ExpectedArtCount)ReleaseRetainedZeroAi();
                if(finite.Handle==IntPtr.Zero)
                {
                    // Mark ownership before configuration so every failure is
                    // retained for cleanup; no output method is called here.
                    retainedZeroAi=true;zeroAiCount=next.Input.ExpectedArtCount;ConfigureFinite(true);
                }
                RequireZeroInputReadiness();
                return zeroInputGeneration;
            }
            catch(Exception error)
            {
                faulted=true;var failures=new List<Exception>{error};
                Try(()=>EndZeroInputRound(zeroInputGeneration),failures);
                throw new AggregateException("常驻0 kV输入准备失败；未调用输出，故障及资源所有权保留。",failures);
            }
        }

        private bool RequireZeroEmergency()
        {
            if(faulted || emergencyMonitor==null)throw new InvalidOperationException("0 kV输入急停读取不可用；禁止启动新采集。");
            bool value=emergencyMonitor.ReadReleased(); // bounded by existing 500 ms fault policy
            if(faulted)throw new InvalidOperationException("0 kV输入DI故障已锁存。");
            return value; // LOW is only a manual input permission, never an output permit
        }
        private void RequireZeroInputReadiness()
        {
            if(faulted)throw new InvalidOperationException("0 kV输入会话故障已锁存。");
            if(!settings.ManualZeroInputWithoutEmergency)RequireZeroEmergency();
        }
        private void EnsureZeroRound(long generation)
        {
            EnsureInitialized();
            if(generation!=zeroInputGeneration || !zeroInputRound || !roundActive)
                throw new InvalidOperationException("0 kV旧轮次或已结束回调不能操作当前设备会话。");
        }
        bool IFactoryResidentZeroInputSession.ReadZeroEmergency(long generation)
        {EnsureZeroRound(generation);return RequireZeroEmergency();}
        void IFactoryResidentZeroInputSession.ArmZeroInput(long generation)
        {
            EnsureZeroRound(generation);RequireZeroInputReadiness();
            if(!retainedZeroAi || finite.Handle==IntPtr.Zero || !finite.Configured || finite.Started || finiteArmed || pico.Armed)
                throw new InvalidOperationException("0 kV有限输入任务未就绪或已布置。");
            // Existing Pico Arm enforces the original cross-round cooldown.
            // Neither Configure nor this path opens a connection or resets it.
            try
            {
                pico.Arm();RequireZeroInputReadiness();
                finiteArmed=true;StartTask(finite);RequireZeroInputReadiness();
            }
            catch {faulted=true;throw;}
        }
        bool IFactoryResidentZeroInputSession.ZeroArtComplete(long generation)
        {EnsureZeroRound(generation);CheckZeroReadFault();return ArtComplete;}
        bool IFactoryResidentZeroInputSession.ZeroPicoComplete(long generation)
        {EnsureZeroRound(generation);CheckZeroReadFault();return PicoComplete;}
        BenchCapture IFactoryResidentZeroInputSession.ReadZeroCapture(long generation)
        {EnsureZeroRound(generation);CheckZeroReadFault();return ReadCapture();}
        private void CheckZeroReadFault()
        {
            if(faulted)throw new InvalidOperationException("0 kV输入会话故障已锁存。");
            if(settings.ManualZeroInputWithoutEmergency)return;
            string detail;if(emergencyMonitor.ConnectionState(out detail)=="ERROR")
                throw new InvalidOperationException(detail);
        }
        void IFactoryResidentZeroInputSession.EndZeroInput(long generation)
        {EndZeroInputRound(generation);}
        private void EndZeroInputRound(long generation)
        {
            if(generation!=zeroInputGeneration)throw new InvalidOperationException("旧0 kV轮次不能停止新轮次。");
            if(!zeroInputRound || !roundActive)return;
            FactoryZeroInputCleanup.Stop(()=>
            {
                if(finite.Started)
                {
                    Check(InputOnlyArtNative.StopTask(finite.Handle),"停止0 kV AI4有限任务，保留已配置句柄");
                    finite.Started=false;
                }
                finiteArmed=false;
            },pico.StopCapture,()=>{zeroInputRound=false;roundActive=false;},()=>faulted=true);
            // No DI stop/reset, no Close, no EnableIdle mode reset, no AO/DO.
        }
        private void ReleaseRetainedZeroAi()
        {
            if(!retainedZeroAi)return;
            if(finiteArmed || finite.Started)throw new InvalidOperationException("0 kV AI4仍在运行，不能重新配置。");
            ClearTask(finite); // only actual mode/length change or session Close clears it
            retainedZeroAi=false;zeroAiCount=0;
        }
    }
}
