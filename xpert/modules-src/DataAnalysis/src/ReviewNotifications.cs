using System;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        int reviewNotificationDepth;

        // Property/state updates from a host handler are allowed, but must not
        // synchronously publish another host notification and recurse forever.
        // Sequence properties still describe the latest committed state.
        void PublishReviewNotification(EventHandler handlers)
        {
            if(handlers==null||reviewNotificationDepth!=0)return;
            reviewNotificationDepth++;
            try
            {
                foreach(EventHandler handler in handlers.GetInvocationList())
                {
                    try { handler(this,EventArgs.Empty); }
                    catch(Exception ex)
                    {
                        if(IsFatalHostException(ex))throw;
                        HostEventErrorCount++;
                        LastError=(L("Host event handler failed: ","宿主事件处理失败："))+ex.Message;
                    }
                }
            }
            finally { reviewNotificationDepth--; }
            RefreshReviewUi();
        }

        static bool IsFatalHostException(Exception exception)
        {
            return exception is OutOfMemoryException || exception is StackOverflowException ||
                exception is AccessViolationException || exception is System.Threading.ThreadAbortException;
        }
    }
}
