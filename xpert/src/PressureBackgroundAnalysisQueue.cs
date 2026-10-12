using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // Managed saved-record work only. The caller supplies immutable inputs and
    // performs all UI setup on its captured context; the analysis engine runs
    // the expensive computation on its own worker. This queue owns no device.
    internal sealed class PressureBackgroundAnalysisQueue
    {
        sealed class Item
        {
            internal string Id;
            internal Func<Task> Work;
            internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
        }
        readonly object gate = new object();
        readonly Queue<Item> items = new Queue<Item>();
        readonly Dictionary<string, Task> admitted = new Dictionary<string, Task>(StringComparer.Ordinal);
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        internal readonly string Id=Guid.NewGuid().ToString("N");
        bool draining, active, ended;
        Item currentItem;
        string activeAcquisitionId = "";
        internal int PendingCount { get { lock (gate) return ended ? 0 : items.Count + (active ? 1 : 0); } }
        internal string ActiveAcquisitionId { get { lock (gate) return ended ? "" : activeAcquisitionId; } }
        internal CancellationToken Cancellation {get{return lifetime.Token;}}
        internal bool Contains(string acquisitionId) { lock (gate) return admitted.ContainsKey(acquisitionId ?? ""); }
        internal Task Enqueue(string acquisitionId, Func<Task> work)
        {
            if (String.IsNullOrWhiteSpace(acquisitionId) || work == null) throw new ArgumentException("Missing saved analysis job.");
            Item item; bool start;
            lock (gate)
            {
                if (ended) throw new InvalidOperationException("This pressure analysis trial has ended.");
                Task existing;
                if (admitted.TryGetValue(acquisitionId, out existing)) return existing;
                item = new Item { Id = acquisitionId, Work = work };
                admitted.Add(acquisitionId, item.Completion.Task);
                items.Enqueue(item);
                start = !draining;
                if (start) draining = true;
            }
            if (start) ThreadPool.QueueUserWorkItem(unused=>Drain());
            return item.Completion.Task;
        }
        async void Drain()
        {
            // Queue bookkeeping never relies on a possibly destroyed UI
            // context. Every real view job uses its bound dispatcher below.
            while (true)
            {
                Item item;
                lock (gate)
                {
                    if (ended || items.Count == 0) { draining = active = false; return; }
                    item = items.Dequeue();
                    currentItem = item;
                    active = true;
                    activeAcquisitionId = item.Id;
                }
                try { await item.Work().ConfigureAwait(false); item.Completion.TrySetResult(true); }
                catch (OperationCanceledException) { item.Completion.TrySetCanceled(); }
                catch (Exception error) { item.Completion.TrySetException(error); }
                finally { lock (gate) { active = false; activeAcquisitionId = ""; currentItem=null; } }
            }
        }
        internal void End()
        {
            lock (gate)
            {
                ended = true;
                while (items.Count != 0) items.Dequeue().Completion.TrySetCanceled();
                if(currentItem!=null)currentItem.Completion.TrySetCanceled();
            }
            lifetime.Cancel();
            // Logical completion is canceled immediately; no pure computation
            // is awaited or aborted. Its immutable epoch guard prevents late
            // results from updating the page or a resumed analysis queue.
        }
        // Temporary analysis controls can replace/uninstall the thread's
        // WinForms context. Bind managed callbacks to the actual host page's
        // dispatcher instead of inheriting that temporary control's context.
        internal static AnalysisDispatcherContext CreateDispatcher(Control dispatcher,CancellationToken cancellation)
        {
            return new AnalysisDispatcherContext(dispatcher,cancellation);
        }
        internal sealed class AnalysisDispatcherContext:SynchronizationContext
        {
            readonly Control dispatcher;
            readonly TaskCompletionSource<bool> completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellationRegistration;
            int canceled,started,subscriptionsDisposed;
            internal AnalysisDispatcherContext(Control control,CancellationToken cancellation)
            {
                if(control==null)throw new ArgumentNullException("control");
                dispatcher=control;
                // Created by the save admission callback on the UI thread,
                // before a queued item can encounter a destroyed handle.
                dispatcher.HandleDestroyed+=DispatcherUnavailable;
                dispatcher.Disposed+=DispatcherUnavailable;
                cancellationRegistration=cancellation.Register(Cancel);
                if(Volatile.Read(ref canceled)!=0)cancellationRegistration.Dispose();
                if(dispatcher.IsDisposed||!dispatcher.IsHandleCreated||cancellation.IsCancellationRequested)Cancel();
            }
            internal Task Run(Func<Task> work)
            {
                if(work==null)throw new ArgumentNullException("work");
                if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Analysis dispatcher is single-use.");
                if(Volatile.Read(ref canceled)!=0)return completion.Task;
                Post(async unused=>{
                    try{await work();completion.TrySetResult(true);}
                    catch(OperationCanceledException){completion.TrySetCanceled();}
                    catch(Exception error){completion.TrySetException(error);}
                    finally{DisposeSubscriptions();}
                },null);
                return completion.Task;
            }
            void DispatcherUnavailable(object sender,EventArgs args){Cancel();}
            void Cancel()
            {
                Interlocked.Exchange(ref canceled,1);completion.TrySetCanceled();DisposeSubscriptions();
            }
            void DisposeSubscriptions()
            {
                if(Interlocked.Exchange(ref subscriptionsDisposed,1)!=0)return;
                dispatcher.HandleDestroyed-=DispatcherUnavailable;
                dispatcher.Disposed-=DispatcherUnavailable;
                cancellationRegistration.Dispose();
            }
            public override SynchronizationContext CreateCopy(){return this;}
            void InvokeCallback(SendOrPostCallback callback,object state)
            {
                var previous=Current;SetSynchronizationContext(this);
                try{callback(state);}finally{SetSynchronizationContext(previous);}
            }
            public override void Post(SendOrPostCallback callback,object state)
            {
                if(Volatile.Read(ref canceled)!=0)return;
                try {
                    if(!dispatcher.IsDisposed&&dispatcher.IsHandleCreated){
                        dispatcher.BeginInvoke((Action)(()=>{if(Volatile.Read(ref canceled)==0)InvokeCallback(callback,state);}));return;
                    }
                }catch(InvalidOperationException){}
                // Logical work is canceled. In-flight pure computation may
                // finish, but no callback may create or update UI off-thread.
                Cancel();
            }
            public override void Send(SendOrPostCallback callback,object state)
            {
                if(Volatile.Read(ref canceled)!=0||dispatcher.IsDisposed||!dispatcher.IsHandleCreated){Cancel();throw new OperationCanceledException("Analysis dispatcher is unavailable.");}
                if(!dispatcher.IsDisposed&&dispatcher.IsHandleCreated&&dispatcher.InvokeRequired)
                    dispatcher.Invoke((Action)(()=>InvokeCallback(callback,state)));
                else InvokeCallback(callback,state);
            }
        }
    }
    // Temporary review controls belong to the real host UI thread, not the
    // canceled analysis SynchronizationContext. A late pure engine completion
    // requests cleanup directly on that owner; it never republishes a result.
    internal sealed class PressureAnalysisReviewOwnership:IDisposable
    {
        readonly Control owner,review;
        readonly int uiThread=Thread.CurrentThread.ManagedThreadId;
        CancellationTokenRegistration cancellationRegistration;
        int requested,disposed;
        internal PressureAnalysisReviewOwnership(Control owner,Control review)
        {
            this.owner=owner??throw new ArgumentNullException("owner");
            this.review=review??throw new ArgumentNullException("review");
            owner.Disposed+=OwnerDisposed;owner.HandleCreated+=OwnerHandleCreated;
        }
        internal void Watch(Task computation,CancellationToken cancellation)
        {
            if(computation==null)throw new ArgumentNullException("computation");
            computation.ContinueWith(t=>{var observed=t.Exception;if(cancellation.IsCancellationRequested)RequestDispose();},
                CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            cancellationRegistration=cancellation.Register(()=>{if(computation.IsCompleted)RequestDispose();});
            if(Volatile.Read(ref disposed)!=0)cancellationRegistration.Dispose();
        }
        void OwnerDisposed(object sender,EventArgs args){Dispose();}
        void OwnerHandleCreated(object sender,EventArgs args){if(Volatile.Read(ref requested)!=0)RequestDispose();}
        void RequestDispose()
        {
            Interlocked.Exchange(ref requested,1);if(Volatile.Read(ref disposed)!=0)return;
            if(Thread.CurrentThread.ManagedThreadId==uiThread){Dispose();return;}
            if(owner.IsDisposed||!owner.IsHandleCreated)return; // owner disposal/handle recreation owns the deferred cleanup
            try{owner.BeginInvoke((Action)Dispose);}catch(InvalidOperationException){}
        }
        public void Dispose()
        {
            if(Thread.CurrentThread.ManagedThreadId!=uiThread){RequestDispose();return;}
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            owner.Disposed-=OwnerDisposed;owner.HandleCreated-=OwnerHandleCreated;
            try{review.Dispose();}finally{cancellationRegistration.Dispose();}
        }
    }
}
