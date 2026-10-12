using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    // Test-only STA message pump. Async UI fixtures must not block an unpumped
    // WindowsFormsSynchronizationContext after an actual inter-round timer.
    internal static class FactoryOfflineAsyncTest
    {
        internal static string Run(Func<Task<string>> test)
        {
            string result=null;Exception error=null;
            var thread=new Thread(()=>
            {
                try
                {
                    using(var context=new WindowsFormsSynchronizationContext())
                    {
                        SynchronizationContext.SetSynchronizationContext(context);
                        context.Post(async unused=>
                        {
                            try {result=await test();}catch(Exception ex){error=ex;}
                            finally {Application.ExitThread();}
                        },null);
                        Application.Run();
                    }
                }
                catch(Exception ex){error=ex;}
            });
            thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
            if(error!=null)throw new InvalidOperationException("Async offline fixture failed",error);
            return result;
        }
    }
}
