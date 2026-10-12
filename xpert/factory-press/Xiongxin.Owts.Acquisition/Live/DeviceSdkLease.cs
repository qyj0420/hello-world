using System;
using System.Collections.Generic;
using System.Threading;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 进程间 SDK 独占租约（精简自 FactoryDeviceLease）。
    /// 枚举/诊断/Live/出厂加压共用同一 Mutex；AbandonedMutex → 拒绝打开（关断状态未知）。
    /// 清理失败时不释放锁，避免下一进程误以为设备空闲。
    /// </summary>
    public sealed class DeviceSdkLease : IDisposable
    {
        public const string DefaultMutexName = @"Global\Xiongxin.Owts.DeviceSdk.v1";

        static readonly object ProcessGate = new object();
        static readonly HashSet<string> ProcessOwned = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>干净 Close 后允许下一次 WaitOne 接管 Abandoned（同进程跨线程 Release 场景）。</summary>
        static int s_allowNextAbandoned;

        readonly string _name;
        readonly int _ownerThread;
        Mutex _mutex;

        DeviceSdkLease(string name, Mutex owned)
        {
            _name = name;
            _mutex = owned;
            _ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        public string Name { get { return _name; } }

        public static DeviceSdkLease Acquire()
        {
            return AcquireNamed(DefaultMutexName);
        }

        /// <summary>测试可用 Local\\… 名；生产勿传空。</summary>
        public static DeviceSdkLease AcquireNamed(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("设备独占锁名称为空。", "name");

            lock (ProcessGate)
            {
                if (!ProcessOwned.Add(name))
                    throw new InvalidOperationException("当前进程已持有设备 SDK 独占会话，禁止重入打开。");
            }

            Mutex candidate = null;
            bool acquired = false;
            try
            {
                candidate = new Mutex(false, name);
                try
                {
                    acquired = candidate.WaitOne(0);
                }
                catch (AbandonedMutexException ex)
                {
                    acquired = true;
                    // 干净 Release 跨线程后句柄 Dispose → 内核 Abandoned；同进程再 Connect 需接管。
                    if (Interlocked.Exchange(ref s_allowNextAbandoned, 0) == 1)
                    {
                        System.Console.WriteLine("[DeviceSdkLease] AbandonedMutex 已接管（上次干净关闭） name=" + name);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "上一设备进程异常退出，硬件关断状态未知；本次拒绝打开，不代表已关断或已放电。请核查现场状态后重新操作。",
                            ex);
                    }
                }

                if (!acquired)
                    throw new InvalidOperationException(
                        "另一设备程序正持有 SDK 独占会话；未打开或操作硬件，请先正常关闭原设备界面。");

                return new DeviceSdkLease(name, candidate);
            }
            catch
            {
                try
                {
                    if (acquired && candidate != null)
                        candidate.ReleaseMutex();
                }
                finally
                {
                    if (candidate != null)
                        candidate.Dispose();
                    lock (ProcessGate)
                        ProcessOwned.Remove(name);
                }
                throw;
            }
        }

        /// <summary>清理失败时保留锁（GC.KeepAlive），不表示硬件已关断。</summary>
        public static void ReleaseAfterCleanup(IDisposable lease, bool cleanupFailed, Action<string> report)
        {
            if (lease == null) return;
            if (cleanupFailed)
            {
                try
                {
                    if (report != null)
                        report("设备清理失败，未正常释放 SDK 独占锁；保留到所有者线程退出，不新增会话，不表示硬件已关断或放电。");
                }
                finally
                {
                    GC.KeepAlive(lease);
                }
                return;
            }
            // 标记：随后 Dispose 若跨线程仅能弃柄，下次 Acquire 可接管 Abandoned
            Interlocked.Exchange(ref s_allowNextAbandoned, 1);
            try { lease.Dispose(); }
            catch (Exception ex)
            {
                // 仍须清 ProcessOwned（Dispose 内 finally 已做）；记录即可
                if (report != null)
                    report("SDK 独占锁释放异常（已尽量清除进程名表）：" + ex.Message);
            }
        }

        public void Dispose()
        {
            // 始终从 ProcessOwned 移除：Release/Connect 常跨线程（UI vs Task.Run），
            // 旧实现同线程才能 ReleaseMutex，跨线程抛错导致本进程 ProcessOwned 残留 →
            //「当前进程已持有设备 SDK 独占会话，禁止重入打开。」
            Mutex owned = Interlocked.Exchange(ref _mutex, null);
            if (owned == null) return;
            try
            {
                if (Thread.CurrentThread.ManagedThreadId == _ownerThread)
                {
                    try { owned.ReleaseMutex(); }
                    catch (ApplicationException) { /* 已释放 */ }
                }
                else
                {
                    // 跨线程无法合法 ReleaseMutex；Dispose 句柄后内核对象会在无等待者时
                    // 以 Abandoned 形式可被同进程再次 WaitOne。Acquire 对 Abandoned 仍拒绝
                    // （关断未知）；同进程重入靠 ProcessOwned 清除 + 下方尽力释放。
                    try { owned.ReleaseMutex(); }
                    catch
                    {
                        // 忽略：非所有者线程；随后 Dispose 句柄
                    }
                }
            }
            finally
            {
                try { owned.Dispose(); } catch { }
                lock (ProcessGate)
                    ProcessOwned.Remove(_name);
            }
        }

        /// <summary>测试/Release：强制清除进程内名表（不碰内核 Mutex）。</summary>
        public static void ClearProcessOwnedForTests(string name = null)
        {
            lock (ProcessGate)
            {
                if (string.IsNullOrEmpty(name))
                    ProcessOwned.Clear();
                else
                    ProcessOwned.Remove(name);
            }
        }

        /// <summary>操作员复位清理门后：允许下一次 Acquire 接管 Abandoned。</summary>
        public static void ArmAllowAbandonedAfterCleanClose()
        {
            Interlocked.Exchange(ref s_allowNextAbandoned, 1);
        }
    }
}
