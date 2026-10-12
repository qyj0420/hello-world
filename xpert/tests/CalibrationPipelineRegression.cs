using System;
using System.IO;
using System.Linq;
using System.Text;
using HVX.SignalProcessing;
using HVX.CalibrationStorage;
using HVX.Controls;
using Xiongxin.Calibration;
using HVX.OWTS.Task38;

internal static class CalibrationPipelineRegression
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static void Reject(Action action,string name){bool failed=false;try{action();}catch{failed=true;}Check(failed,name);}
    static double Gain(double frequency){double fs=1000000;var wave=Enumerable.Range(0,30000).Select(i=>Math.Sin(2*Math.PI*frequency*i/fs)).ToArray();var filtered=PdLinearHighPass.Apply(wave,fs);double a=0,b=0;for(int i=1000;i<29000;i++){a+=wave[i]*wave[i];b+=filtered[i]*filtered[i];}return Math.Sqrt(b/a);}
    static int Main(string[] args)
    {
        try{Run(args);return 0;}catch(Exception error){Console.Error.WriteLine("FAIL at check "+checks+": "+error.GetType().FullName+": "+error.Message);return 1;}
    }
    static void Run(string[] args)
    {
        string root=Path.Combine(Path.GetFullPath(args[0]),"pipeline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var flow=new CalibrationAcquisitionFlow();
        Reject(()=>flow.RequireCapture(false),"No capture before explicit start");flow.Begin();flow.RequireCapture(true);
        Reject(()=>flow.RequireCapture(false),"Main acquisition blocked during self-check");
        flow.RangeAndTriggerReady();flow.RequireCapture(false);
        Reject(()=>flow.RequireCapture(true),"Self-check cannot restart after range/trigger ready");
        flow.StopForReview();Check(flow.Stage==CalibrationAcquisitionStage.Review,"Sampling stops before quality/persistence callbacks");
        Reject(()=>flow.RequireCapture(false),"No acquisition during save/review");Reject(()=>flow.Begin(),"No new acquisition during review");
        flow.Finish();Reject(()=>flow.RequireCapture(false),"Completion cannot implicitly restart sampling");
        flow.Begin();flow.RequireCapture(true);flow.Finish();Check(flow.Stage==CalibrationAcquisitionStage.Finished,"Only explicit start permits next acquisition; cancellation/failure ends it");
        Check(PdLinearHighPass.Apply(Enumerable.Repeat(3d,10000).ToArray(),1e6).All(v=>Math.Abs(v)<1e-10),"DC removal");
        Check(Gain(1000)<.015,"Sub-1kHz rejection");Check(Math.Abs(Gain(10000)-1/Math.Sqrt(2))<.015,"10kHz -3dB corner");Check(Gain(100000)>.995,"PD pass band");
        var impulse=new double[10001];impulse[5000]=1;var output=PdLinearHighPass.Apply(impulse,125e6);
        Check(Array.IndexOf(output,output.Max())==5000,"No peak timing shift");for(int i=0;i<4000;i++)Check(Math.Abs(output[5000-i]-output[5000+i])<1e-12,"Symmetric FIR");
        Reject(()=>PdLinearHighPass.Apply(new[]{double.NaN},1e6),"NaN rejected");Reject(()=>PdLinearHighPass.Apply(new[]{0d},40000),"Insufficient sample rate rejected");
        var pre=Enumerable.Range(0,10000).Select(i=>.0001*Math.Sin(i)).ToArray();pre[100]=.12;
        var plan=AcquisitionPlanning.Plan(pre,4,false);Check(plan.RangeIndex==4 && plan.ThresholdV>.01&&plan.ThresholdV<.1,"Range hysteresis and trigger");
        Check(AcquisitionPlanning.Plan(pre,4,true).RangeIndex==5,"Clipping raises range");
        var w=new CalibrationWindowSettings{CableLengthM=180,VelocityMPerUs=170,SampleRateHz=125e6};
        Check(w.PostPeakSamples==checked((int)Math.Ceiling(10*180d/170*125)+256),"Five line lengths at actual sample rate");
        var blocks=new double[5][];
        for(int b=0;b<5;b++){blocks[b]=new double[100000];for(int i=0;i<100000;i++)blocks[b][i]=.15*Math.Exp(-Math.Pow((i-30000)/5d,2))+.04*Math.Exp(-Math.Pow((i-30265)/6d,2));}
        w.ThresholdV=.02;
        var extracted=CalibrationWindowing.FromFilteredTriggeredBlocks(blocks,w,PdLinearHighPass.GuardSamples(125e6));
        Check(extracted.Windows.Length==5,"Triggered windows extracted without padding");
        extracted.Windows[4]=extracted.Windows[4].Select(v=>-v).ToArray();var clean=AcquisitionPlanning.RejectOutliers(extracted);
        Check(clean.Windows.Length==4 && clean.RejectedCount==1,"Inverted foreign pulse excluded before averaging");
        var tdr=TdrCorrelation.Match(clean.Average,400,125e6,180,170);
        Check(tdr.Valid&&Math.Abs(tdr.DistanceM-180)<.5,"Reflection uses correlation and actual timebase (one sample = 0.68 m)");
        Check(TdrCorrelation.Match(clean.Average,400,125e6,250,170,.15).Valid==false,"Narrow calibration gate rejects wrong length");
        var lowpassCase=new double[2400];
        for(int i=0;i<lowpassCase.Length;i++)lowpassCase[i]=Math.Exp(-Math.Pow((i-400)/2d,2))+.035*Math.Exp(-Math.Pow((i-1000)/24d,2));
        var auto=ReflectionBandwidth.Select(lowpassCase,125e6,400,1000);
        Check(auto.Sufficient&&auto.CutoffMHz>=1&&auto.CutoffMHz<=60&&auto.Ratio>=.1,"Automatic bandwidth raises relative dispersed reflection above 10 percent");
        Check(lowpassCase[400]>.99&&lowpassCase[1000]<.036,"Automatic bandwidth preserves raw samples");
        var weakSameShape=new double[2400];for(int i=0;i<weakSameShape.Length;i++)weakSameShape[i]=Math.Exp(-Math.Pow((i-400)/4d,2))+.03*Math.Exp(-Math.Pow((i-1000)/4d,2));
        var weak=ReflectionBandwidth.Select(weakSameShape,125e6,400,1000);
        Check(!weak.Sufficient&&weak.CutoffMHz==7&&weak.Ratio<.1,"Excess attenuation stops at 7MHz without manufacturing a passing ratio");
        var alreadyGood=ReflectionBandwidth.Select(clean.Average,125e6,400,665);
        Check(alreadyGood.Sufficient&&alreadyGood.CutoffMHz==0,"No unnecessary lowpass when ratio already passes");
        var lpImpulse=PdLinearLowPass.Apply(impulse,1/125e6,5);
        Check(Array.IndexOf(lpImpulse,lpImpulse.Max())==5000,"Lowpass peak has no time shift");
        Reject(()=>PdLinearLowPass.Apply(impulse,1/125e6,.9),"Automatic/manual lowpass floor enforced");
        var segment=new double[120000];for(int p=20000;p<=70000;p+=10000)for(int i=-30;i<=30;i++)segment[p+i]=.15*Math.Exp(-Math.Pow(i/4d,2));
        foreach(int p in new[]{25000,65000})for(int i=-30;i<=30;i++)segment[p+i]=.10*Math.Exp(-Math.Pow(i/4d,2));
        var segmentSettings=new CalibrationWindowSettings{CableLengthM=180,VelocityMPerUs=170,SampleRateHz=1e6,ThresholdV=.02};
        var selectedSegment=CalibrationWindowing.FromFilteredSegment(segment,segmentSettings,PdLinearHighPass.GuardSamples(1e6));
        Check(selectedSegment.Windows.Length==6&&selectedSegment.RejectedCount==2,"Segment extraction rejects competing 25Hz pulse train and keeps 100Hz calibrator");
        foreach(int count in new[]{0,1,2}) {
            var shortSegment=new double[60000];
            for(int pulse=0;pulse<count;pulse++)for(int i=-30;i<=30;i++)shortSegment[10000+pulse*10000+i]=.15*Math.Exp(-Math.Pow(i/4d,2));
            var subset=CalibrationWindowing.FromFilteredSegment(shortSegment,segmentSettings,PdLinearHighPass.GuardSamples(1e6));
            Check(subset.Windows.Length==count,"single segment retains actual 0/1/2 complete windows without requesting N");
            if(count>0)Check(subset.Message.Contains("周期证据不足"),"few pulses are not represented as confirmed periodicity");
        }
        var boundedPulse=new[]{1d,.5d};
        var bounded=AcquisitionPlanning.SelectConsistentPulses(new CalibrationWindowResult{Windows=Enumerable.Repeat(boundedPulse,4096).ToArray()});
        Check(bounded.Windows.Length==4096&&Math.Abs(bounded.Average[0]-1)<1e-12,"4096-window budget averages all windows without N truncation");
        Reject(()=>AcquisitionPlanning.SelectConsistentPulses(new CalibrationWindowResult{Windows=Enumerable.Repeat(boundedPulse,4097).ToArray()}),"4097 candidate windows rejected before expensive averaging");
        var templateWave=Enumerable.Range(0,101).Select(i=>Math.Exp(-Math.Pow((i-50)/7d,2))-.5*Math.Exp(-Math.Pow((i-63)/5d,2))).ToArray();
        var template=new NoisePulseTemplate{Id="fixture",FilterId=PdLinearHighPass.FilterId,SampleRateHz=125e6,PeakIndex=50,Samples=templateWave};
        var comparison=new double[302];for(int i=0;i<templateWave.Length;i++)comparison[100+i]=-2*templateWave[i]+.03;
        Check(NoisePulseTemplates.Matches(new[]{template},comparison,150,125e6,1),"Noise similarity removes scaled inverted matching packet");
        Check(!NoisePulseTemplates.Matches(new[]{template},new double[302],150,125e6,1),"Flat signal not a matching interference template");
        Check(NoisePulseTemplates.WaveformHash(comparison,0,1/125e6)!=NoisePulseTemplates.WaveformHash(comparison,0,1/100e6),"Template source identity includes timebase");
        string item="电缆长度: = \"180\"\r\nA相电缆长度: = \"180\"\r\nB相电缆长度: = \"210\"\r\nC相电缆长度: = \"220\"\r\n";
        string cable=Block("A",180)+Block("A",180)+Block("A",180);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),item,Encoding.GetEncoding(936));File.WriteAllText(Path.Combine(root,"CABLE.in"),cable,Encoding.GetEncoding(936));
        CalibrationProjectStore.AddJoints(root,new[]{"A"},new[]{80d});
        string text=Encoding.GetEncoding(936).GetString(File.ReadAllBytes(Path.Combine(root,"CABLE.in")));
        Check(text.Split('\n').Count(l=>l.StartsWith("0\tA"))==3&&!text.Contains("0\tB"),"Repeated A never renamed");
        Check(text.Split('\n').Count(l=>l.Contains("校准识别"))==3,"Joint added to all actual selected A blocks");
        Check(text.Split('\n').Count(l=>l.Contains("接头 1\t校准识别\t80m"))==3,"Repeated real blocks each start automatic joint order from one");
        string named=Path.Combine(root,"numbering");Directory.CreateDirectory(named);
        File.WriteAllText(Path.Combine(named,"CABLE.in"),Block("A",180),Encoding.GetEncoding(936));
        CalibrationProjectStore.AddJoints(named,new[]{"A"},new[]{120.123d,30.456d,80.789d});
        var namedLines=Encoding.GetEncoding(936).GetString(File.ReadAllBytes(Path.Combine(named,"CABLE.in")));
        Check(namedLines.Contains("接头 1\t校准识别\t30.456m")&&namedLines.Contains("接头 2\t校准识别\t80.789m")&&namedLines.Contains("接头 3\t校准识别\t120.123m"),"Numbering follows distance without losing measured precision");
        File.WriteAllText(Path.Combine(named,"CABLE.in"),namedLines.Replace("接头 2\t","用户命名\t"),Encoding.GetEncoding(936));
        CalibrationProjectStore.RemoveGeneratedJoints(named,new[]{"A"},new[]{30.456d});
        namedLines=Encoding.GetEncoding(936).GetString(File.ReadAllBytes(Path.Combine(named,"CABLE.in")));
        Check(namedLines.Contains("用户命名\t校准识别\t80.789m")&&namedLines.Contains("接头 2\t校准识别\t120.123m"),"Removing automatic joint renumbers remaining automatic names but preserves manual name");
        byte[] before=File.ReadAllBytes(Path.Combine(root,"CABLE.in"));CalibrationProjectStore.AddJoints(root,new[]{"A"},new[]{80d});Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),"Idempotent joint addition");
        Reject(()=>CalibrationProjectStore.SaveLength(root,"A",70,170),"Terminal cannot precede joint");
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),"Failed length write leaves project intact");
        CalibrationProjectStore.SaveLength(root,"A",190,171);Check(ProjectDefaults.Load(root,"A").LengthM==190&&ProjectDefaults.Load(root,"A").VelocityMPerUs==171,"Per-phase saved geometry reloaded");
        CalibrationProjectStore.RemoveGeneratedJoints(root,new[]{"A"},new[]{80d});Check(!Encoding.GetEncoding(936).GetString(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))).Contains("校准识别"),"Unconfirm removes only generated joints");
        CalibrationProjectStore.AddJoints(root,new[]{"A"},new[]{80d});var beforeReview=File.ReadAllBytes(Path.Combine(root,"CABLE.in"));
        Reject(()=>CalibrationProjectStore.ReviewJoints(root,new[]{"A"},new[]{double.NaN},new[]{80d}),"Invalid joint review rejected atomically");
        Check(beforeReview.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),"Atomic review did not remove old joints before failed addition");
        Check(Directory.GetFiles(root,"*.bak").Length>=3,"Recoverable project backups");
        string deep=Path.Combine(root,new String('d',Math.Max(1,208-root.Length-1)));Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep,"CABLE.in"),Block("A",180),Encoding.GetEncoding(936));
        byte[] deepOriginal=File.ReadAllBytes(Path.Combine(deep,"CABLE.in"));
        CalibrationProjectStore.AddJoints(deep,new[]{"A"},new[]{80d});
        Check(Directory.GetFiles(deep,"*.bak").Any(b=>File.ReadAllBytes(b).SequenceEqual(deepOriginal)),"Deep project preserves exact original in short unique backup");
        string excessive=Path.Combine(root,new String('e',Math.Max(1,225-root.Length-1)));Directory.CreateDirectory(excessive);
        File.WriteAllText(Path.Combine(excessive,"CABLE.in"),Block("A",180),Encoding.GetEncoding(936));
        byte[] excessiveOriginal=File.ReadAllBytes(Path.Combine(excessive,"CABLE.in"));
        Reject(()=>CalibrationProjectStore.AddJoints(excessive,new[]{"A"},new[]{80d}),"Unrepresentable backup path rejected before write");
        Check(File.ReadAllBytes(Path.Combine(excessive,"CABLE.in")).SequenceEqual(excessiveOriginal)&&Directory.GetFiles(excessive).Length==1,"Overlong path leaves data and directory unchanged");
        string previousDirectory=Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory=root;
            CalibrationProjectStore.AddJoints(Path.GetFileName(deep)+Path.DirectorySeparatorChar,new[]{"A"},new[]{90d});
            Check(Encoding.GetEncoding(936).GetString(File.ReadAllBytes(Path.Combine(deep,"CABLE.in"))).Contains("接头 2\t校准识别\t90m"),"Relative deep path and trailing separator normalize safely; automatic joints use position order");
            Reject(()=>CalibrationProjectStore.AddJoints(Path.GetFileName(excessive),new[]{"A"},new[]{80d}),"Relative path cannot bypass absolute backup length preflight");
            Check(File.ReadAllBytes(Path.Combine(excessive,"CABLE.in")).SequenceEqual(excessiveOriginal)&&Directory.GetFiles(excessive).Length==1,"Relative overlong path rejected before lock/temp creation");
        } finally {Environment.CurrentDirectory=previousDirectory;}
        VerifyMetadataCommitSafety(root);
        var record=new LiveCalibrationRecord{Phase="A",Level="5nC",DeviceSerial="TEST-OFFLINE",DeviceModel="test",FilterId=PdLinearHighPass.FilterId,
            ProjectHash=LiveCalibrationRecord.Identity(root),LengthM=190,VelocityMPerUs=2*190/(265/125e6*1e6),SampleRateHz=125e6,IncidentSeconds=400/125e6,ReflectionSeconds=665/125e6,
            CoefficientPcPerV=5000/.15,ChargePc=5000,AttenuationDb=10,SnrDb=40,Correlation=.99,RangeIndex=4,PulseCount=4,IncidentPeakIndex=400,ReflectionPeakIndex=665,Average=clean.Average,CreatedUtc=DateTime.UtcNow.ToString("o")};
        record.RawFiles=Enumerable.Range(0,3).Select(i=>{string rel="raw"+i+".bin";File.WriteAllBytes(Path.Combine(root,rel),new byte[]{(byte)i,1,2});return new CalibrationRawReference{RelativePath=rel,Sha256=LiveCalibrationRecord.HashFile(Path.Combine(root,rel))};}).ToArray();
        string path=record.Save(root);Check(LiveCalibrationRecord.Load(root,path).Average.SequenceEqual(record.Average),"Formal archive roundtrip");
        Check(LiveCalibrationRecord.Best(root,"A",false)!=null&&LiveCalibrationRecord.Best(root,"B",false)==null,"Phase isolation");
        Check(LiveCalibrationRecord.Best(root,"A",true)==null,"Timing-mode isolation");
        string savedHash=LiveCalibrationRecord.HashFile(path);int priorBackupCount=Directory.GetFiles(Path.GetDirectoryName(path),"*.bak").Length;
        record.Save(root);Check(Directory.GetFiles(Path.GetDirectoryName(path),"*.bak").Length==priorBackupCount&&LiveCalibrationRecord.HashFile(path)==savedHash,"Unchanged resave preserves exact archive without accumulating redundant full backups");
        record.AcquisitionMode="Segment";record.PulseCount=4096;record.Save(root);
        var largeSegment=LiveCalibrationRecord.Load(root,path);
        Check(largeSegment.PulseCount==4096&&largeSegment.AcquisitionMode=="Segment"&&largeSegment.RawFiles.Length==3&&largeSegment.RawFiles.All(f=>f.Sha256==LiveCalibrationRecord.HashFile(Path.Combine(root,f.RelativePath))),"segment count and original raw hashes persist and reload at bounded maximum");
        Check(LiveCalibrationRecord.Best(root,"A",false).PulseCount==4096,"consumer discovery accepts applicable bounded segment record");
        record.PulseCount=4097;Reject(()=>record.Validate(root,true),"segment archive rejects count above4096");
        record.AcquisitionMode="Pulse";record.PulseCount=257;Reject(()=>record.Validate(root,true),"triggered archive preserves256 count upper limit");
        File.AppendAllText(Path.Combine(root,"raw0.bin"),"changed");Reject(()=>LiveCalibrationRecord.Load(root,path),"Raw tamper detection");
        Reject(()=>LiveCalibrationRecord.ResolveRaw(root,"..\\outside.bin"),"Archive path traversal rejected");
        Console.WriteLine("CALIBRATION_PIPELINE_PASS="+checks+"; fixtures="+root);
    }
    static void VerifyMetadataCommitSafety(string root)
    {
        string transaction=Path.Combine(root,"tx");Directory.CreateDirectory(transaction);
        var paths=Enumerable.Range(0,4).Select(i=>Path.Combine(transaction,i+".in")).ToArray();
        Action reset=()=>{for(int i=0;i<paths.Length;i++)File.WriteAllText(paths[i],"original "+i,new UTF8Encoding(false));};
        Func<CalibrationProjectStore.TextFile[]> prepare=()=>paths.Select(path=>{var file=CalibrationProjectStore.TextFile.Read(path);file.Lines[0]="updated "+Path.GetFileName(path);return file;}).ToArray();
        reset();var files=prepare();
        try {
            CalibrationProjectStore.BeforePublishForTests=(index,path)=>{if(index==1)throw new IOException("second publish failed");};
            Reject(()=>CalibrationProjectStore.Commit(transaction,files),"Second metadata publish failure propagated");
            Check(paths.Select((path,i)=>File.ReadAllText(path)=="original "+i).All(v=>v),"Earlier metadata restored after second publish failure");
            Check(Directory.GetFiles(transaction,"*.c-*.bak").Any(path=>File.ReadAllBytes(path).SequenceEqual(files[0].Bytes)),"Rollback keeps exact original commit backup");
        } finally {CalibrationProjectStore.BeforePublishForTests=null;}
        reset();files=prepare();string failure="";
        try {
            CalibrationProjectStore.BeforePublishForTests=(index,path)=>{
                if(index==3){File.WriteAllText(paths[1],"newer external contents",new UTF8Encoding(false));File.WriteAllText(paths[2],"another external change",new UTF8Encoding(false));throw new IOException("fourth publish failed");}
            };
            try{CalibrationProjectStore.Commit(transaction,files);}catch(IOException error){failure=error.Message;}
            Check(failure.Length>0&&failure.Contains("原始备份"),"Recovery conflict reports retained original backup");
            Check(File.ReadAllText(paths[1])=="newer external contents","Rollback never overwrites concurrently edited metadata");
            Check(File.ReadAllText(paths[2])=="another external change","Each independent rollback conflict preserves external content");
            Check(File.ReadAllText(paths[0])=="original 0"&&File.ReadAllText(paths[3])=="original 3","Multiple rollback conflicts do not skip recovery of other files");
            string backup=Directory.GetFiles(transaction,"1.in.c-*.bak").Single(path=>File.ReadAllBytes(path).SequenceEqual(files[1].Bytes));
            Check(failure.Contains(backup),"Recovery failure exposes existing exact backup path");
            string secondBackup=Directory.GetFiles(transaction,"2.in.c-*.bak").Single(path=>File.ReadAllBytes(path).SequenceEqual(files[2].Bytes));
            Check(failure.Contains(secondBackup),"Aggregate recovery error includes every conflicted file backup");
            Check(Directory.GetFiles(transaction,"*.tmp").Length==0,"Owned staging/rollback temporary files cleaned after conflict");
        } finally {CalibrationProjectStore.BeforePublishForTests=null;}
        string sibling=transaction+"-sibling";Directory.CreateDirectory(sibling);string outside=Path.Combine(sibling,"outside.in");File.WriteAllText(outside,"outside untouched");
        var wrong=CalibrationProjectStore.TextFile.Read(outside);wrong.Lines[0]="must not write";
        Reject(()=>CalibrationProjectStore.Commit(transaction,new[]{wrong}),"Absolute prefix sibling is not an authorized project child");
        Check(File.ReadAllText(outside)=="outside untouched","Rejected sibling target unchanged");
    }
    static string Block(string phase,double length){return "0\t"+phase+"\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t末终端\t终端\t"+length+"m\r\n";}
}
