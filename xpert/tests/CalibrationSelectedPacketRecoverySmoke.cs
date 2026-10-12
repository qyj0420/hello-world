using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HVX.Controls;
using HVX.OWTS.Task38;
using HVX.SignalProcessing;
using Xiongxin.Calibration;
using Xiongxin.Scope;

// Managed, detached controls and raw readers only. No ProjectPath, save, Main,
// worker, SDK or device command. Actual replay is optional and strictly readonly.
internal static class CalibrationSelectedPacketRecoverySmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object o,string method,params object[] args)
    {return o.GetType().GetMethods(B).Single(m=>m.Name==method&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static object Get(object o,string name)
    {var f=o.GetType().GetField(name,B);return f!=null?f.GetValue(o):o.GetType().GetProperty(name,B).GetValue(o,null);}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s));}
    static void Pulse(double[] wave,int peak,int width,double amplitude)
    {for(int i=Math.Max(0,peak-width);i<=Math.Min(wave.Length-1,peak+width);i++)wave[i]+=amplitude*Math.Max(0,1-Math.Abs(i-peak)/(double)width);}
    static double[] Synthetic(double reflectionSign)
    {
        var wave=Enumerable.Range(0,2000).Select(i=>1e-6*Math.Sin(.73*i)).ToArray();
        Pulse(wave,400,8,2);Pulse(wave,390,5,-.35);Pulse(wave,412,8,-.7);
        Pulse(wave,620,8,-.8);Pulse(wave,632,8,.28);Pulse(wave,610,5,.14);
        Pulse(wave,658,10,.26*reflectionSign);return wave;
    }
    static void StorePair(CalibrationProgramControl c,object phase,string level,int incident,int reflection)
    {
        var raw=((IDictionary)Get(phase,"Waveforms"))[level];
        var result=Call(c,"BuildManualResult",raw,phase,incident,reflection,null);
        Call(c,"StoreAnalysisNoLock",phase,level,raw,result);
    }
    static void VerifyWave(double[] wave,double length,double velocity,int incident,int reflection,string tag)
    {
        var original=(double[])wave.Clone();
        foreach(string level in new[]{"TDR","5nC"})using(var c=new CalibrationProgramControl()) {
            c.SelectedLevelCode=level;c.UseFirstArrivalTiming=true;c.SetPhaseParameters(-1,length,velocity,false,false);
            c.SetPhaseWaveform(-1,level,wave,0,8e-9);var phase=Call(c,"GetPhaseStateNoLock",-1);
            StorePair(c,phase,level,incident,reflection);
            if(level!="TDR") {
                var anchors=c.GetDisplayDecayAnchors(-1,level);
                Check(anchors.Length==4&&anchors[1]>0&&anchors[3]>0,tag+": positive selected pair is not replaced by negative precursor");
            }
            Check(c.SetPhasePacketPeaks(-1,level,incident,reflection),tag+": explicit actual peak pair accepted");
            Check(c.GetIncidentPeakIndex(-1,level)==incident&&c.GetReflectionPeakIndex(-1,level)==reflection,tag+": selected peak identity unchanged");
            int i=c.GetIncidentFirstArrivalIndex(-1,level),r=c.GetReflectionFirstArrivalIndex(-1,level);
            Check(i>=0&&i<incident&&r>incident&&r<reflection,tag+": selected raw packet arrivals supported");
            Check(((IDictionary)Get(phase,"Markers")).Contains(level)&&String.IsNullOrEmpty(c.GetTimingStatus(-1,level)),tag+": valid propagation marker restored");
            Check(Math.Abs(c.GetMeasuredVelocityMPerUs(-1,level)-2*length/((r-i)*.008))<1e-8,tag+": velocity uses actual arrival difference");
            var before=c.GetDisplayDecayAnchors(-1,level);
            if(level!="TDR")Check(before.Length==4&&c.DisplayDecayStatus=="Ready",tag+": explicit peak pair remains display-ready");
            c.UseFirstArrivalTiming=false;
            if(level!="TDR")Check(c.GetDisplayDecayAnchors(-1,level).SequenceEqual(before),tag+": display amplitudes/polarity independent of timing mode");
            Check(c.GetIncidentPeakIndex(-1,level)==incident&&c.GetReflectionPeakIndex(-1,level)==reflection,tag+": timing switch cannot relocate selected peaks");
            Check(wave.SequenceEqual(original),tag+": original samples unchanged");
            Console.WriteLine(tag+" level="+level+" peaks="+incident+","+reflection+" arrivals="+i+","+r+" decay="+c.DisplayDecayStatus);
        }
    }
    static void SyntheticBoundaries()
    {
        var wave=Synthetic(1);VerifyWave(wave,166.2,160,400,658,"synthetic-bipolar");
        foreach(double sign in new[]{1d,-1d}) {
            var y=Enumerable.Range(0,2048).Select(i=>1e-6*Math.Sin(i)).ToArray();
            Pulse(y,400,8,1);Pulse(y,625,8,-.6);Pulse(y,653,9,sign*.25);
            var match=TdrCorrelation.Match(y,400,125e6,172.04,170,.15);
            Check(match.Valid&&match.ReflectionIndex==653,"rough prior ranks a real endpoint over earlier stronger ringing for either polarity");
            Check(Math.Sign(y[match.ReflectionIndex])==(int)sign,"automatic matching retains actual signed polarity");
            Check(Math.Abs(match.DistanceM-(653-400)*.68)<1e-10,"distance uses measured indices rather than forcing project length");
        }
        var empty=new double[2000];Pulse(empty,400,8,1);
        Check(!TdrCorrelation.Match(empty,400,125e6,166.2,160).Valid,"prior never creates absent reflection");
        var truncated=new double[2000];Pulse(truncated,4,3,1);Pulse(truncated,260,8,.3);
        Check(TdrCorrelation.Match(truncated,4,125e6,166.2,160).IncidentWindowTruncated,"truncated incident stays rejected");
        var invalid=(double[])wave.Clone();invalid[650]=Double.NaN;
        Check(CalibrationArrivalMath.FindSelectedPacket(invalid,658,401,1999,258,0,1e-5)==-1,"nonfinite selected packet does not use local fallback");
        Check(CalibrationArrivalMath.LocalReflectionArrival(null,658,650,258,0,1e-5)==-1,"preexisting proposed onset cannot bypass source validation");
        var sustained=Enumerable.Repeat(.1,2000).ToArray();sustained[658]=.2;
        Check(CalibrationArrivalMath.FindSelectedPacket(sustained,658,401,1999,258,0,1e-5)==-1,"unsupported sustained floor never yields crop-edge onset");
        var spikes=new double[2000];spikes[400]=1;spikes[658]=.3;
        Check(CalibrationArrivalMath.FindSelectedPacket(spikes,658,401,1999,258,0,1e-5)==-1,"one-sample peak never fabricated as arrival");
        using(var c=new CalibrationProgramControl()) {
            c.UseFirstArrivalTiming=true;c.SetPhaseWaveform(-1,"5nC",spikes,0,8e-9);
            Check(c.SetPhasePacketPeaks(-1,"5nC",400,658),"unsupported peaks can remain editing evidence");
            Check(c.GetDisplayDecayAnchors(-1,"5nC").Length==0&&c.DisplayDecayStatus=="Unavailable"&&!String.IsNullOrEmpty(c.GetTimingStatus(-1,"5nC")),"unknown onset still blocks propagation and clears stale display status");
        }
        using(var c=new CalibrationProgramControl()) {
            var inverted=Synthetic(-1);c.UseFirstArrivalTiming=false;c.SetPhaseWaveform(-1,"5nC",inverted,0,8e-9);
            var phase=Call(c,"GetPhaseStateNoLock",-1);StorePair(c,phase,"5nC",400,658);
            Check(c.GetDisplayDecayAnchors(-1,"5nC").Length==0&&c.DisplayDecayStatus=="OppositePolarity","genuine opposite selected peaks remain opposite; no absolute-value decay");
        }
    }
    static double Number(string text,string pattern)
    {return Double.Parse(Regex.Match(text,pattern).Groups[1].Value,CultureInfo.InvariantCulture);}
    static void Actual(string project)
    {
        foreach(string name in new[]{"20261011_155254_5c8d4f","20261011_155522_ce89df","20261011_160013_a2575f"}) {
            string folder=Path.Combine(project,"Calibration","Acquisition",name),reviewPath=Path.Combine(folder,"calibration-review.txt");
            if(!File.Exists(reviewPath)){Console.WriteLine("ACTUAL_REPLAY_NOT_AVAILABLE="+name);continue;}
            var files=Directory.GetFiles(folder,"capture_*.xxs").OrderBy(p=>p,StringComparer.Ordinal).ToArray();
            var paths=files.Concat(new[]{reviewPath}).ToArray();var hashes=paths.ToDictionary(p=>p,Hash);
            Check(files.Length==5,"actual five retained raw pulse blocks");string review=File.ReadAllText(reviewPath);
            double length=Number(review,@"长度：([0-9.Ee+\-]+) m"),velocity=Number(review,@"波速：([0-9.Ee+\-]+) m"),
                threshold=Number(review,@"脉冲筛选阈值：±([0-9.Ee+\-]+) V");
            var captures=files.Select(Wire.Load).ToArray();double fs=1e9/captures[0].Info.IntervalNs;
            var settings=new CalibrationWindowSettings{CableLengthM=length,VelocityMPerUs=velocity,SampleRateHz=fs,
                ThresholdV=threshold,FullScaleV=Settings.Ranges[captures[0].Info.Config.A.Range]};
            var filtered=captures.Select(c=>PdLinearHighPass.Apply(AcquisitionPlanning.Volts(c),fs)).ToArray();
            var extracted=CalibrationWindowing.FromFilteredTriggeredBlocks(filtered,settings,PdLinearHighPass.GuardSamples(fs));
            var average=AcquisitionPlanning.ContinueForReview(AcquisitionPlanning.SelectConsistentPulses(extracted));
            Check(average.Windows.Length==5&&fs==125e6,"actual replay follows current DC/HP/window/mean chain");
            bool tdr=name.Contains("155254");var wave=average.Average;
            var match=tdr?TdrCorrelation.Match(wave,400,fs,length,velocity):TdrCorrelation.MatchTerminal(wave,400,fs,length,velocity,.15);
            Check(match.Valid&&match.IncidentIndex==400&&Math.Abs(match.ReflectionIndex-(tdr?658:657))<=1,"actual automatic matching retains supported terminal rather than earlier negative ringing");
            VerifyWave(wave,length,velocity,400,tdr?658:657,name);
            Check(paths.All(p=>Hash(p)==hashes[p]),"all actual raw and review bytes unchanged after replay");
            Console.WriteLine("ACTUAL_USER_BYTES_UNCHANGED=True; FOLDER="+name+"; AutoPair="+match.IncidentIndex+","+match.ReflectionIndex+"; Corr="+match.Correlation.ToString("R"));
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            SyntheticBoundaries();
            string project=args.Length>1?args[1]:@"D:\OWTSData\软件测试-软件\2026-10-07_13-23-28";
            if(Directory.Exists(project))Actual(project);else Console.WriteLine("ACTUAL_REPLAY_NOT_AVAILABLE=project");
            Console.WriteLine("CALIBRATION_SELECTED_PACKET_RECOVERY_PASS="+checks+"; SDK_CALLS=0; PRODUCT_STARTED=0; ACTUAL_WORKER_STARTED=0; ORIGINAL_DATA_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
