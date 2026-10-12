using System;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // Managed snapshots/queues only. This type owns no native handle and never
    // reads DI, queries Pico/ART, writes outputs, or resets a fault latch.
    internal sealed class FactoryEmergencySnapshotPublisher : IDisposable
    {
        private readonly object gate=new object();
        private readonly ManualResetEvent quit=new ManualResetEvent(false);
        private readonly Action publish;
        private readonly Action<Exception> failed;
        private readonly int periodMs;
        private readonly string name;
        private Thread thread;
        private bool started,disposed;
        private Exception failure;
        internal Exception Failure {get{lock(gate)return failure;}}
        internal FactoryEmergencySnapshotPublisher(int periodMs,string name,Action publish,Action<Exception> failed)
        {
            if(periodMs<=0 || publish==null || failed==null)throw new ArgumentException("Managed publication requires a bounded interval and callbacks.");
            this.periodMs=periodMs;this.name=name;this.publish=publish;this.failed=failed;
        }
        internal void Start()
        {
            lock(gate)
            {
                if(started||disposed)throw new InvalidOperationException("Managed publication cannot be started twice or reused.");
                started=true;thread=new Thread(Loop){IsBackground=true,Name=name};thread.Start();
            }
        }
        private void Loop()
        {
            try
            {
                while(!quit.WaitOne(0))
                {
                    publish();
                    if(quit.WaitOne(periodMs))break;
                }
            }
            catch(Exception error)
            {
                lock(gate)failure=error;
                quit.Set();
                try{failed(error);}catch(Exception notificationError){lock(gate)failure=new AggregateException(error,notificationError);}
            }
        }
        internal bool Stop(int timeoutMs)
        {
            Thread owned;
            lock(gate){if(disposed)return true;quit.Set();owned=thread;}
            return owned==null||owned.Join(Math.Max(0,timeoutMs));
        }
        public void Dispose()
        {
            if(!Stop(1000))throw new InvalidOperationException("Managed publication has not exited; retain its ownership and callbacks.");
            lock(gate){if(disposed)return;disposed=true;quit.Dispose();}
        }
    }
}
