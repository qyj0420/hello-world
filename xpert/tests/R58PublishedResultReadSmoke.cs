using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xiongxin.FactoryTest;

internal static class R58PublishedResultReadSmoke
{
    static int checks;
    sealed class SharingFailure:IOException{internal SharingFailure(int code):base("offline injected read failure"){HResult=unchecked((int)0x80070000)|code;}}
    static void Assert(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static string Read(Func<string,string> fake,Func<int,Task> delay=null)
    {return FactoryResidentDeviceClient.ReadPublishedRoundResultAsync("offline-no-file",fake,delay).GetAwaiter().GetResult();}
    static int Main()
    {
        try{
            foreach(int status in new[]{32,33}){
                int calls=0,waits=0;var error=new SharingFailure(status);const string exact="4\r\n";
                string result=Read(p=>{if(++calls<3)throw error;return exact;},ms=>{waits++;Assert(ms>0&&ms<=10,"bounded ten-ms interval");return Task.Delay(ms);});
                Assert(result==exact&&calls==3&&waits==2,"sharing recovery preserves exact published bytes");
            }
            foreach(int status in new[]{32,33}){
                int calls=0;var error=new SharingFailure(status);var watch=Stopwatch.StartNew();
                try{Read(p=>{calls++;throw error;});throw new Exception("Persistent lock accepted");}
                catch(SharingFailure actual){Assert(Object.ReferenceEquals(error,actual),"deadline retains original sharing error");}
                Assert(watch.ElapsedMilliseconds>=90&&watch.ElapsedMilliseconds<1000,"persistent contention has a 100-ms budget");
                Assert(calls>1&&calls<=15,"persistent read has bounded attempts with real delay");
            }
            foreach(int status in new[]{2,3,5,21,995}){
                int calls=0,waits=0;var error=new SharingFailure(status);
                try{Read(p=>{calls++;throw error;},ms=>{waits++;return Task.Delay(ms);});throw new Exception("Other I/O accepted");}
                catch(SharingFailure actual){Assert(Object.ReferenceEquals(error,actual),"nonsharing error remains original");}
                Assert(calls==1&&waits==0,"nonsharing I/O never retries");
            }
            foreach(string value in new[]{"0","1","3","4","5","not-a-result",""})
                Assert(Read(p=>value)==value,"reader does not parse, repair or upgrade result: "+value);
            int unknownCalls=0;var unknown=new InvalidOperationException("offline unexpected failure");
            try{Read(p=>{unknownCalls++;throw unknown;});throw new Exception("Unknown error accepted");}
            catch(InvalidOperationException actual){Assert(Object.ReferenceEquals(unknown,actual)&&unknownCalls==1,"unknown failure not swallowed");}
            Console.WriteLine("R58_PUBLISHED_RESULT_READ_PASS="+checks+"; SDK/HARDWARE/PRODUCT_MAIN=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
