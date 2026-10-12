using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Xiongxin.FactoryTest;

// Compiled with the worker closure but enters this test Main only. No worker,
// SDK, product Main, ports or hardware are started.
internal static class R58RequestRejectionSmoke
{
    static int checks;
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static int Main()
    {
        try {
            string session=Guid.NewGuid().ToString("N"),request=Guid.NewGuid().ToString("N");
            int prepared=0;
            Check(UnifiedPressureBridge.PrepareIdentifiedRequest(session,request,()=>++prepared)==1&&prepared==1,"valid pure preparation passes once");
            foreach(Exception cause in new Exception[]{new ArgumentException("invalid action"),new InvalidDataException("stale settings"),new InvalidOperationException("old request"),new System.Xml.XmlException("invalid XML"),new OperationCanceledException("cancelled")}) {
                bool rejected=false;
                try{UnifiedPressureBridge.PrepareIdentifiedRequest<int>(session,request,()=>{throw cause;});}
                catch(FactoryRequestRejectedException receipt){
                    rejected=receipt.NativeRequestNotSubmitted&&receipt.HostSessionId==session&&receipt.HostRequestId==request&&Object.ReferenceEquals(receipt.InnerException,cause);
                    var worker=typeof(UnifiedPressureBridge).Assembly;
                    Check(Xiongxin.OWTS.Desktop.ModuleHost.IsVerifiedRequestRejection(receipt,worker,session,request),"host verifies this exact request");
                    Check(!Xiongxin.OWTS.Desktop.ModuleHost.IsVerifiedRequestRejection(receipt,worker,session,Guid.NewGuid().ToString("N")),"host rejects old request receipt");
                    Check(!Xiongxin.OWTS.Desktop.ModuleHost.IsVerifiedRequestRejection(receipt,worker,Guid.NewGuid().ToString("N"),request),"host rejects old session receipt");
                    Check(!Xiongxin.OWTS.Desktop.ModuleHost.IsVerifiedRequestRejection(receipt,typeof(string).Assembly,session,request),"host rejects foreign assembly receipt");
                    Check(Xiongxin.OWTS.Desktop.ModuleHost.IsVerifiedRequestRejection(new TargetInvocationException(receipt),worker,session,request),"only reflection envelope is unwrapped");
                }
                Check(rejected,"pure validation rejection carries exact request/session and original cause: "+cause.GetType().Name);
            }
            var unknown=new IOException("unknown IO failure");bool original=false;
            try{UnifiedPressureBridge.PrepareIdentifiedRequest<int>(session,request,()=>{throw unknown;});}catch(IOException error){original=Object.ReferenceEquals(error,unknown);}
            Check(original,"unknown failure not reclassified as a harmless rejection");
            Check(!Xiongxin.OWTS.Desktop.ModuleHost.IsVerifiedRequestRejection(new Exception("RequestRejected native not submitted"),typeof(UnifiedPressureBridge).Assembly,session,request),"exception text is not trusted evidence");
            int calls=0;bool badIdentity=false;
            try{UnifiedPressureBridge.PrepareIdentifiedRequest("",request,()=>++calls);}catch(ArgumentException){badIdentity=true;}
            Check(badIdentity&&calls==0,"invalid request identity cannot fabricate a trusted receipt");
            Check(typeof(FactoryRequestRejectedException).GetConstructors(BindingFlags.Public|BindingFlags.Instance).Length==0,"receipt cannot be constructed by callers");
            bool rejectedXml=false;
            try{UnifiedPressureBridge.CaptureIdentifiedAsync("bad XML","bad XML",1,"unused-offline",session,request,false,false,"",1,1,true,false,null,CancellationToken.None).GetAwaiter().GetResult();}
            catch(FactoryRequestRejectedException receipt){rejectedXml=receipt.HostSessionId==session&&receipt.HostRequestId==request;}
            Check(rejectedXml,"actual identified capture rejects malformed settings before worker dispatch");
            Console.WriteLine("R58_REQUEST_REJECTION_PASS="+checks+";sdk=false;hardware=false;worker=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
