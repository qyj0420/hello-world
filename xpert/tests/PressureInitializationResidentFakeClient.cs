using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // TEST-ONLY replacement of the process/native boundary. The production
    // resident gate, bridge request adapters, Program routing and policy run
    // unchanged. No Process, native SDK or real worker is started here.
    internal sealed class FactoryResidentDeviceClient
    {
        internal static string Root;
        internal static int Opens,Closes,Rounds,InputOpens,NormalOpens;
        internal static bool FailClose;
        internal static TaskCompletionSource<bool> HoldClose;
        readonly FactorySettings identity;
        bool started;
        internal string ProgressPath {get;private set;}
        internal long OpenProgressDelivered {get{return 0;}}
        internal bool Alive {get{return started;}}
        internal FactoryResidentDeviceClient(FactorySettings settings)
        {
            identity=FactoryNativeSessionPolicy.PrepareOpen(settings);
            ProgressPath=Path.Combine(Root,"client-"+Guid.NewGuid().ToString("N"),"progress.log");
            Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath));
        }
        internal bool Matches(FactorySettings settings){return FactoryNativeSessionPolicy.SameDeviceSession(identity,settings);}
        internal void Start(){started=true;Opens++;if(identity.ManualZeroInputWithoutEmergency)InputOpens++;else NormalOpens++;}
        internal Task WaitReady(Action<string,string> progress,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(progress!=null)progress("LINK_ART_HIGH",DateTime.UtcNow.ToString("O"));return Task.CompletedTask;}
        internal void ThrowIfExited(){if(!started)throw new IOException("fake process is not active");}
        internal void RevokeBatch(){}
        internal void RequestClose(){}
        internal async Task Close(){Closes++;if(HoldClose!=null)await HoldClose.Task.ConfigureAwait(false);if(FailClose)throw new IOException("injected cleanup failure");started=false;}
        internal Task RunRound(FactorySettings settings,FactoryRunKind kind,string directory,Action<string,string> progress,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();ThrowIfExited();
            var request=FactoryNativeSessionPolicy.PrepareQueuedRound(settings,false);
            if(kind!=FactoryRunKind.Pressure||!request.ManualZeroInputWithoutEmergency||request.TargetKv!=0||
                request.PressureFeedbackContext||request.ZeroSynchronizedOptical||request.AutomaticBatch||!Matches(request))
                throw new Exception("test refuses every output-capable request");
            Rounds++;
            var capture=new BenchCapture{Settings=request.Input,VoltageRaw=new double[request.Input.ExpectedArtCount],PdAdc=new short[request.Input.RequestedPicoCount],
                PicoRateHz=request.Input.RequestedPicoRateHz,PicoFullScaleV=BenchSettings.Ranges[request.Input.RangeIndex],
                HardwareBits=PicoCaptureConfiguration.HardwareBits(request.Input.ResolutionBits,request.Input.RangeIndex),
                CapturedUtc=DateTime.UtcNow,ArtSerial=request.Input.ArtSerial,PicoSerial=request.Input.PicoSerial,Runtime="OFFLINE SYNTHETIC BOUNDARY — NO DEVICE"};
            capture.Save(directory);
            var result=new FactoryResult{Settings=request,Kind=kind,ReadOnlyAcquisition=true,Completed=true,Capture=capture,DirectoryPath=directory};
            using(var file=File.Create(Path.Combine(directory,"result.xml")))new XmlSerializer(typeof(FactoryResult)).Serialize(file,result);
            return Task.CompletedTask;
        }
        internal static FactoryEmergencyRoundStoppedException ReadEmergencyRoundStop(FactorySessionRequest request,string directory,string sessionId,int parent,int worker,bool alive,int code)
        {throw new InvalidOperationException("unrelated emergency fixture must not run with the initialization fake boundary");}
    }
}
