using System;
using System.Collections.Generic;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // All SDK clients in this version take this lock, including read-only
    // enumeration: device serial locks alone cannot protect SDK-global calls.
    internal sealed class FactoryDeviceLease : IDisposable
    {
        private const string DeviceMutexName="Global\\Xiongxin.FactoryAcceptance.DeviceSdk.v1";
        private static readonly object processGate=new object();
        private static readonly HashSet<string> processOwned=new HashSet<string>(StringComparer.Ordinal);
        private readonly string name;
        private readonly int ownerThread;
        private Mutex mutex;
        private FactoryDeviceLease(string value,Mutex owned)
        {name=value;mutex=owned;ownerThread=Thread.CurrentThread.ManagedThreadId;}

        internal static FactoryDeviceLease Acquire() {return AcquireNamed(DeviceMutexName);}
        internal static void ReleaseAfterCleanup(IDisposable lease,bool cleanupFailed,Action<string> report)
        {
            if(lease==null)return;
            if(cleanupFailed)
            {
                try
                {
                    if(report!=null)report("设备清理失败，未正常释放SDK独占锁；保留到所有者线程退出，不新增会话，不表示硬件已关断或放电。");
                }
                finally {GC.KeepAlive(lease);}
                return;
            }
            lease.Dispose();
        }
        // Tests use only unique Local\\ test names, never the real device lock.
        internal static FactoryDeviceLease AcquireNamed(string name)
        {
            if(String.IsNullOrWhiteSpace(name))throw new ArgumentException("设备独占锁名称为空。");
            lock(processGate)
            {
                if(!processOwned.Add(name))throw new InvalidOperationException("当前进程已持有设备独占会话，禁止重入打开。");
            }
            Mutex candidate=null;bool acquired=false;
            try
            {
                candidate=new Mutex(false,name);
                try {acquired=candidate.WaitOne(0);}
                catch(AbandonedMutexException ex)
                {
                    acquired=true;
                    throw new InvalidOperationException("上一设备进程异常退出，硬件关断状态未知；本次拒绝打开，不代表已关断或已放电。请核查现场状态后重新操作。",ex);
                }
                if(!acquired)throw new InvalidOperationException("另一设备程序正持有SDK独占会话；未打开或操作硬件，请先正常关闭原设备界面。");
                return new FactoryDeviceLease(name,candidate);
            }
            catch
            {
                try {if(acquired && candidate!=null)candidate.ReleaseMutex();}
                finally {if(candidate!=null)candidate.Dispose();lock(processGate)processOwned.Remove(name);}
                throw;
            }
        }
        public void Dispose()
        {
            if(mutex==null)return;
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)
                throw new InvalidOperationException("设备独占会话必须由创建它的SDK所有者线程释放。");
            var owned=mutex;
            owned.ReleaseMutex();
            mutex=null;
            try {owned.Dispose();}
            finally {lock(processGate)processOwned.Remove(name);}
        }
    }
}
