using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.OWTS.Desktop;

// Only the managed input-only fake is instantiated. No native API is loaded.
internal static class R58EnvironmentOwnedReleaseSmoke
{
    static int checks;
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    sealed class Fake : IArtEnvironmentApi
    {
        internal int Creates,Stops,Clears,CreateStatus;internal bool ClearFails=true;
        internal readonly ManualResetEventSlim Entered=new ManualResetEventSlim(),Resume=new ManualResetEventSlim();
        internal bool WaitInRead;
        public int CreateTask(string name,out IntPtr task){Creates++;task=CreateStatus<0?IntPtr.Zero:new IntPtr(123);return CreateStatus;}
        public int CreateAI(IntPtr task,string channels,int terminal,double min,double max,int units){return 0;}
        public int ConfigureClock(IntPtr task,double rate,int edge,int mode,int samples){return 0;}
        public int Start(IntPtr task){return 0;}
        public int Read(IntPtr task,int samples,double timeout,int layout,double[] buffer,out int read)
        { Entered.Set();if(WaitInRead&&!Resume.Wait(3000))throw new Exception("fake timeout");read=samples;for(int i=0;i<buffer.Length;i++)buffer[i]=2.0;return 0; }
        public int Stop(IntPtr task){Check(task==new IntPtr(123),"stop retains original handle");Stops++;return 0;}
        public int Clear(IntPtr task){Check(task==new IntPtr(123),"clear retains original handle");Clears++;return ClearFails?-1:0;}
    }
    static int Main()
    {
        try {
            var fake=new Fake();var reader=new ArtEnvironmentReader(()=>fake,TimeSpan.FromSeconds(2));
            var result=reader.Read("Dev1",CancellationToken.None);
            Check(!result.Success&&fake.Creates==1&&fake.Clears==1,"failed clear invalidates success and retains owner");
            bool rejected=false;try{ArtEnvironmentReader.ReleaseRetainedTaskAsync(CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){rejected=true;}
            Check(rejected&&fake.Creates==1&&fake.Clears==2,"failed handoff cleanup cannot open replacement task");
            fake.ClearFails=false;ArtEnvironmentReader.ReleaseRetainedTaskAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(fake.Creates==1&&fake.Clears==3,"successful handoff clears only retained handle");
            ArtEnvironmentReader.ReleaseRetainedTaskAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(fake.Clears==3,"confirmed cleanup is idempotent");
            fake.WaitInRead=true;fake.Entered.Reset();
            using(var cancel=new CancellationTokenSource()) {
                Task<TemperatureHumidityReading> active=reader.ReadAsync("Dev1",cancel.Token);
                Check(fake.Entered.Wait(3000),"fake reader became active");cancel.Cancel();
                bool busy=false;try{ArtEnvironmentReader.ReleaseRetainedTaskAsync(CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidOperationException){busy=true;}
                Check(busy&&fake.Clears==3,"cancelled but still executing native read cannot be concurrently cleared");
                fake.Resume.Set();result=active.GetAwaiter().GetResult();
                Check(result.ErrorCode=="cancelled"&&fake.Clears==4,"reader owns actual stop/clear after read returns");
            }
            ArtEnvironmentReader.ReleaseRetainedTaskAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(fake.Creates==2&&fake.Clears==4,"post-reader handoff does not create or double-clear tasks");
            int[] codes={-50103,-4850003,-4850005,-4850011,-4850001,-4850002,-4850006,-4850007,-4850004,-4850008,-4850013};
            string[] expected={"busy","busy","busy","timeout","driver_missing","driver_missing","driver_missing","driver_missing","device_unavailable","device_unavailable","cleanup_pending"};
            for(int i=0;i<codes.Length;i++) {
                var blocked=new Fake{CreateStatus=codes[i],ClearFails=false};
                result=new ArtEnvironmentReader(()=>blocked,TimeSpan.FromSeconds(2)).Read("Dev1",CancellationToken.None);
                Check(result.ErrorCode==expected[i]&&!result.Success,"private status classified without fake measurement: "+codes[i]);
                Check(blocked.Creates==1&&blocked.Stops==0&&blocked.Clears==0,"failed create owns no new handle: "+codes[i]);
            }
            Console.WriteLine("R58_ENVIRONMENT_OWNED_RELEASE_PASS="+checks+";sdk=false;hardware=false;network=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
