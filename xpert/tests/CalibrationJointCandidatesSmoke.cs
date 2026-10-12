using System;
using System.IO;
using System.Linq;
using System.Threading;
using HVX.Controls;

internal static class CalibrationJointCandidatesSmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static void Reject(Action action,string text){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,text);}
    static CalibrationTraceGeometry Geometry(double origin=0.02){return new CalibrationTraceGeometry{Kind="Cable",Phase="A",IncidentSeconds=origin+10e-6,LengthM=1700,VelocityMPerUs=170,SampleIntervalSeconds=20e-9};}
    static void Pulse(double[] y,int center,double amplitude){for(int i=Math.Max(0,center-20);i<Math.Min(y.Length,center+21);i++)y[i]+=amplitude*Math.Exp(-Math.Pow((i-center)/3.0,2));}
    static int Main()
    {
        try
        {
            var g=Geometry();double[] x=Enumerable.Range(0,3001).Select(i=>.02+i*20e-9).ToArray(),y=x.Select((v,i)=>.001*Math.Sin(i*.72)).ToArray();
            Pulse(y,500,1);Pulse(y,900,.15);Pulse(y,1500,.3);Pulse(y,2500,.25);
            var original=(double[])y.Clone();var candidates=CalibrationJointCandidates.Find(x,y,g,CancellationToken.None);
            Check(candidates.Length==1,"one interior reflected candidate; terminal and later multiple excluded");
            Check(Math.Abs(candidates[0].DistanceM-680)<2,"absolute seconds converted to distance without ms/us confusion");
            Check(candidates[0].Score<=95&&candidates[0].Correlation>.95&&candidates[0].Reason.Contains("不是概率"),"bounded evidence score with explicit warning");
            Check(y.SequenceEqual(original),"no source waveform mutation");
            var aligned=CalibrationTraceGeometry.Compatible(new[]{g,Geometry(.021)},true);
            Check(aligned.IncidentSeconds==0&&Math.Abs(aligned.DistanceAt(8e-6)-680)<1e-9,"alignment changes origin, not length or distance units");
            Reject(()=>CalibrationTraceGeometry.Compatible(new[]{g,Geometry(.021)},false),"unaligned differing incident rejected");
            var other=Geometry();other.Phase="B";Reject(()=>CalibrationTraceGeometry.Compatible(new[]{g,other},true),"cross-phase mean rejected");
            other=Geometry();other.LengthM=1701;Reject(()=>CalibrationTraceGeometry.Compatible(new[]{g,other},true),"different length mean rejected");
            other=Geometry();other.VelocityMPerUs=169;Reject(()=>CalibrationTraceGeometry.Compatible(new[]{g,other},true),"different velocity mean rejected");
            other=Geometry();other.Kind="Capacitor";Reject(()=>CalibrationTraceGeometry.Compatible(new[]{g,other},true),"mixed load mean rejected");
            Reject(()=>CalibrationJointCandidates.Find(x,y,other,CancellationToken.None),"capacitor joint search refused");
            double[] normalized=y.Select(v=>v/7).ToArray();var normalizedCandidates=CalibrationJointCandidates.Find(x,normalized,g,CancellationToken.None);
            Check(normalizedCandidates.Length==1&&normalizedCandidates[0].Seconds==candidates[0].Seconds,"normalization does not move candidates");
            double[] relative=x.Select(v=>v-g.IncidentSeconds).ToArray();var relativeCandidates=CalibrationJointCandidates.Find(relative,y,aligned,CancellationToken.None);
            Check(Math.Abs(relativeCandidates.Single().DistanceM-candidates.Single().DistanceM)<1e-7,"aligned time coordinate gives same distance");
            double[] reversed=x.Select((v,i)=>.001*Math.Sin(i*.72)).ToArray();Pulse(reversed,500,1);Pulse(reversed,900,-.15);Pulse(reversed,1500,.3);
            Check(CalibrationJointCandidates.Find(x,reversed,g,CancellationToken.None).Length==1,"opposite polarity real reflection retained for review");
            double[] noJoint=x.Select((v,i)=>.001*Math.Sin(i*.72)).ToArray();Pulse(noJoint,500,1);Pulse(noJoint,1500,.3);Pulse(noJoint,2500,.2);
            Check(CalibrationJointCandidates.Find(x,noJoint,g,CancellationToken.None).Length==0,"noise and multiple reflection do not create an interior joint");
            double[] noEnd=x.Select((v,i)=>.001*Math.Sin(i*.72)).ToArray();Pulse(noEnd,500,1);Pulse(noEnd,900,.15);
            Reject(()=>CalibrationJointCandidates.Find(x,noEnd,g,CancellationToken.None),"absent terminal rejects instead of fabricating reference");
            Reject(()=>CalibrationJointCandidates.Find(x,new double[x.Length],g,CancellationToken.None),"flat waveform cannot infer incident");
            var nan=(double[])y.Clone();nan[800]=Double.NaN;Reject(()=>CalibrationJointCandidates.Find(x,nan,g,CancellationToken.None),"NaN rejected");
            var early=Geometry();early.IncidentSeconds=x[2];Reject(()=>CalibrationJointCandidates.Find(x,y,early,CancellationToken.None),"no preincident envelope rejected");
            Reject(()=>CalibrationJointCandidates.Find(x.Take(1000).ToArray(),y.Take(1000).ToArray(),g,CancellationToken.None),"truncated terminal rejected");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{CalibrationJointCandidates.Find(x,y,g,cancel.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancellation honored");}
            Console.WriteLine("CALIBRATION_JOINT_CANDIDATES_PASS="+checks);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
