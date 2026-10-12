using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Read-only diagnosis against the very same build's unprotected intermediate.
// No algorithm source, packet gate, saved timing, sample or acceptance is edited.
internal static class AnalysisActualCalibrationPacketsReadonly
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object x,string name){return x.GetType().GetField(name,F).GetValue(x);}
    static void Set(object x,string name,object value){x.GetType().GetField(name,F).SetValue(x,value);}
    static object Call(Type type,string name,params object[] args){return type.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);}
    static string Hash(string p){using(var input=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
    static Dictionary<string,object> Row(params object[] args){var d=new Dictionary<string,object>();for(int i=0;i<args.Length;i+=2)d.Add((string)args[i],args[i+1]);return d;}
    static int Main(string[] args)
    {
        try {
            if(args.Length!=4)throw new ArgumentException("Pass analysis DLL, same-build unprotected intermediate, immutable QA input clone, evidence path.");
            string calPath=Path.Combine(args[2],"ALL_5nC.cal"),before=Hash(calPath);
            var algorithm=Assembly.LoadFrom(args[1]);
            Type engine=algorithm.GetType("HVX.Task17.Location.LocationEngine",true);
            var analysis=Assembly.LoadFrom(args[0]);
            object live=Call(analysis.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true),"Load",args[2],calPath,true);
            double[] raw=(double[])Get(live,"Average"),original=(double[])raw.Clone();
            double fs=(double)Get(live,"SampleRateHz"),ta=(double)Get(live,"IncidentSeconds"),tb=(double)Get(live,"ReflectionSeconds");
            int markedA=(int)Call(engine,"IndexFromTime",ta,fs,raw.Length,0),markedB=(int)Call(engine,"IndexFromTime",tb,fs,raw.Length,raw.Length-1);
            object profile=Call(engine,"A4PrepareMeasurementSignal",raw,fs,markedB-markedA,null);
            object context=Activator.CreateInstance(engine.GetNestedType("A4Context",F),true);
            double center=(double)Get(profile,"DetectionCenter"),sigma=(double)Get(profile,"RawSigma");
            Set(context,"Raw",Get(profile,"DetectionRaw"));Set(context,"Signal",Get(profile,"DetectionRaw"));
            Set(context,"Fs",fs);Set(context,"Center",center);Set(context,"RawCenter",center);Set(context,"Sigma",sigma);
            Set(context,"Threshold",4.5*sigma);Set(context,"RoundTrip",markedB-markedA);Set(context,"RoundTripExact",(double)(markedB-markedA));
            object[] packets=((IEnumerable)Call(engine,"A4Extract",context)).Cast<object>().ToArray();
            Func<int,object> owner=i=>packets.FirstOrDefault(p=>(int)Get(p,"Start")<=i&&(int)Get(p,"End")>=i);
            object a=owner(markedA),b=owner(markedB);
            var evidence=Row("schema","HVX.Actual.CalibrationPacket.Readonly/1","hardwareAccessed",false,"sdkCalled",false,"businessSourceChanged",false,"physicalAccuracyVerified",false,
                "algorithmIntermediateSha256",Hash(args[1]),"analysisDllSha256",Hash(args[0]),"calSha256",before,"calSampleCount",raw.Length,"sampleRateHz",fs,
                "markedIncidentIndex",markedA,"markedReflectionIndex",markedB,"storedIncidentPeakIndex",Get(live,"IncidentPeakIndex"),"storedReflectionPeakIndex",Get(live,"ReflectionPeakIndex"),
                "incidentMarkerVoltage",raw[markedA],"reflectionMarkerVoltage",raw[markedB],"rawSigma",sigma,"rawCenter",center,"threshold",4.5*sigma,
                "incidentMarkerHasPacket",a!=null,"reflectionMarkerHasPacket",b!=null,"samePacket",a!=null&&Object.ReferenceEquals(a,b),
                "packets",packets.Select(p=>Row("id",Get(p,"Id"),"start",Get(p,"Start"),"end",Get(p,"End"),"peak",Get(p,"Peak"),"amplitude",Get(p,"Amplitude"),"clipped",Get(p,"Clipped"),"unresolvedOverlap",Get(p,"UnresolvedOverlap"))).ToArray());
            object parsed=Activator.CreateInstance(engine.GetNestedType("ParsedCalibration",F),true);
            Set(parsed,"Samples",raw);Set(parsed,"SampleRate",fs);Set(parsed,"StartTime",ta);Set(parsed,"FinalTime",tb);
            Set(context,"Calibration",parsed);Set(context,"Attenuation",true);
            object measured=Call(engine,"A4BuildAttenuationScoringReference",context,false);
            evidence["originalScoringReferenceValid"]=Get(measured,"Valid");evidence["originalScoringReferenceReason"]=Get(measured,"Reason");
            // Counterfactual diagnosis only: isolate the later structural gates.
            // This does NOT change the original CAL, actual A/B analysis, measured
            // first-arrival times, pulse acceptance or a production input policy.
            int peakA=(int)Get(live,"IncidentPeakIndex"),peakB=(int)Get(live,"ReflectionPeakIndex");
            Set(parsed,"StartTime",peakA/fs);Set(parsed,"FinalTime",peakB/fs);
            object peakReference=Call(engine,"A4BuildAttenuationScoringReference",context,false);
            evidence["counterfactualStoredPeakDiagnosticOnly"]=Row("originalTimingRetainedInActualAnalysis",true,"referenceValid",Get(peakReference,"Valid"),"referenceReason",Get(peakReference,"Reason"));
            Type eighth=algorithm.GetType("HVX.Task17.Location.EighthPulseMath",true);
            object bReference=Call(eighth,"Calibrate",raw,fs,markedA,markedB,tb-ta);
            object[] bPackets=((IEnumerable)Get(bReference,"ObservedPackets")).Cast<object>().ToArray();
            evidence["methodBOriginalCalibrationDiagnosis"]=Row("referenceValid",Get(bReference,"Valid"),"reason",Get(bReference,"Reason"),
                "packets",bPackets.Select(p=>Row("start",Get(p,"Start"),"end",Get(p,"End"),"peak",Get(p,"Peak"),"valid",Get(p,"Valid"),"reason",Get(p,"Reason"))).ToArray());
            object bPeaks=Call(eighth,"Calibrate",raw,fs,peakA,peakB,tb-ta);
            evidence["methodBCounterfactualStoredPeakDiagnosticOnly"]=Row("referenceValid",Get(bPeaks,"Valid"),"reason",Get(bPeaks,"Reason"));
            if(Hash(calPath)!=before||!original.SequenceEqual(raw))throw new InvalidOperationException("Readonly diagnostic mutated input.");
            evidence["inputBytesAndSamplesUnchanged"]=true;
            File.WriteAllText(args[3],new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue}.Serialize(evidence),new UTF8Encoding(false));
            Console.WriteLine("ACTUAL_CAL_PACKET_DIAGNOSTIC_COMPLETE; INPUT_UNCHANGED=true; HARDWARE=0; REFERENCE="+Get(measured,"Reason"));return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
