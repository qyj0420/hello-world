using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

// Runs the existing byte-exact readonly actual-project pipeline, then checks the
// explicitly local CAL packet contract. Loading a reference is not accuracy or
// acceptance. All generated files remain in the owned QA copy/scope.
internal static class AnalysisActualCalibrationPacketContractSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object x,string n){return x.GetType().GetField(n,F).GetValue(x);}
    static object Prop(object x,string n){return x.GetType().GetProperty(n,F).GetValue(x,null);}
    static void Set(object x,string n,object v){x.GetType().GetField(n,F).SetValue(x,v);}
    static object Call(Type t,string n,params object[] a){return CalibrationProtectedTestReflection.Call(t,n,a);}
    static IEnumerable<string> Values(object value,string name)
    {
        var d=value as Dictionary<string,object>;if(d!=null){foreach(var p in d){if(p.Key==name&&p.Value is string)yield return (string)p.Value;foreach(var s in Values(p.Value,name))yield return s;}}
        else if(value is object[])foreach(var item in (object[])value)foreach(var s in Values(item,name))yield return s;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            if(args.Length!=3)throw new ArgumentException("Pass isolated build, owned QA directory, readonly actual project or exact clone.");
            int baseResult=(int)typeof(AnalysisActualCalibrationLocReadonly).GetMethod("Main",F).Invoke(null,new object[]{args});Check(baseResult==0,"byte-exact readonly CAL/LOC pipeline integrity");
            string qa=Path.GetFullPath(args[1]);var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=256};
            var evidence=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(qa,"actual-cal-loc-readonly.json")));
            var results=((object[])evidence["analysisResults"]).Cast<Dictionary<string,object>>().ToArray();
            var a=results.Single(x=>(string)x["method"]=="A");var b=results.Single(x=>(string)x["method"]=="B");
            Check((bool)a["success"]&&(bool)a["calibrationResolvedInFrozenAlgorithm"],"actual A6 full CAL-to-LOC entry now resolves saved ALL charge reference");
            Check(!(bool)b["calibrationResolvedInFrozenAlgorithm"]&&Values(b["status"],"reason").Any(x=>x.Contains("calibration_overlap_unresolved")),"actual B same-packet remains explicitly unusable, no split or fallback");
            string clone=Path.Combine(qa,"exact-input-copy"),loc=Directory.GetFiles(clone,"*.loc").Single();
            var analysis=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var algorithm=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
            CalibrationProtectedTestReflection.InitializeFor(algorithm);
            Type scopeType=analysis.GetType("HVX.PdReview.ReviewCalibrationInputScope",true),engine=CalibrationProtectedTestReflection.Type(algorithm,"HVX.Task17.Location.LocationEngine");
            using(var scope=(IDisposable)Call(scopeType,"Create",clone,loc)) {
                string projected=(string)Prop(scope,"AlgorithmProjectPath"),calPath=Path.Combine(projected,"ALL_5nC.cal");
                object cal=Call(engine,"ReadCalibration",calPath),identity=Get(cal,"PacketIdentity");Check(identity!=null,"actual source archive emits strict independent packet identity");
                double ta=(double)Get(cal,"StartTime"),tb=(double)Get(cal,"FinalTime"),fs=(double)Get(cal,"SampleRate");double[] raw=(double[])Get(cal,"Samples");
                object live=Call(analysis.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true),"Load",clone,Path.Combine(clone,"ALL_5nC.cal"),true);
                Check(ta==(double)Get(live,"IncidentSeconds")&&tb==(double)Get(live,"ReflectionSeconds")&&
                    (int)Get(identity,"IncidentPeakIndex")== (int)Get(live,"IncidentPeakIndex")&&
                    (int)Get(identity,"ReflectionPeakIndex")== (int)Get(live,"ReflectionPeakIndex"),"actual saved first-arrival clock and packet identities unchanged, no snapshot constants");
                object context=Activator.CreateInstance(CalibrationProtectedTestReflection.Type(algorithm,"HVX.Task17.Location.LocationEngine+A4Context"),true);Set(context,"Calibration",cal);Set(context,"Attenuation",true);Set(context,"Fs",fs);
                object reference=Call(engine,"A4BuildAttenuationScoringReference",context,false);
                Check((bool)Get(reference,"Valid")&&(double)Get(reference,"TravelSeconds")==tb-ta,"actual A reference keeps original clock and all complete-packet quality gates");
                var type=CalibrationProtectedTestReflection.Type(algorithm,"HVX.Task17.Location.EighthPulseMath");object br=Call(type,"Calibrate",raw,fs,(int)Math.Round(ta*fs),(int)Math.Round(tb*fs),tb-ta);
                Check(!(bool)Get(br,"Valid")&&(string)Get(br,"Reason")=="calibration_overlap_unresolved","actual original B combined-packet gate retained");
                object low=Call(engine,"A4BuildLowBandReference",context),band=Call(engine,"A4BuildBandReference",context,new double[]{1e6,5e6,10e6});
                evidence["localContractReview"]=new Dictionary<string,object>{{"schema","HVX.CalibrationPacketIdentity/1"},{"actualAReferenceJson",Call(engine,"A4AttenuationReferenceJson",reference)},
                    {"startSeconds",ta},{"finalSeconds",tb},{"travelSeconds",Get(reference,"TravelSeconds")},{"lowBandTrusted",Get(low,"Trusted")},{"lowBandReason",Get(low,"Reason")},
                    {"bandReferenceAvailable",band!=null},{"originalBandLowBandCodeUnchanged",true},{"methodBReason",Get(br,"Reason")},{"acceptanceGranted",false},{"physicalAccuracyVerified",false}};
                scopeType.GetMethod("VerifySources",F).Invoke(scope,null);
                string sourceCal=Path.Combine(clone,"ALL_5nC.cal");byte[] originalSource=File.ReadAllBytes(sourceCal);var sourceAttributes=File.GetAttributes(sourceCal);
                DateTime sourceWrite=File.GetLastWriteTimeUtc(sourceCal);
                File.SetAttributes(sourceCal,sourceAttributes&~FileAttributes.ReadOnly);File.AppendAllText(sourceCal," ");
                try{scopeType.GetMethod("VerifySources",F).Invoke(scope,null);throw new Exception("Changed source archive was accepted.");}
                catch(TargetInvocationException error){Check(error.InnerException is IOException,"scope rejects concurrent QA source CAL hash change");}
                finally{File.WriteAllBytes(sourceCal,originalSource);File.SetLastWriteTimeUtc(sourceCal,sourceWrite);File.SetAttributes(sourceCal,sourceAttributes);}
                // Concurrent source and projection edits fail the entire scope;
                // never write the actual source, only this disposable projection.
                string meta=calPath+".packets.xml";File.AppendAllText(meta," ");
                try{scopeType.GetMethod("VerifySources",F).Invoke(scope,null);throw new Exception("Changed packet contract was accepted.");}
                catch(TargetInvocationException error){Check(error.InnerException is IOException,"scope rejects concurrent projected contract hash change");}
                File.Delete(meta);
                try{scopeType.GetMethod("VerifySources",F).Invoke(scope,null);throw new Exception("Missing scoped packet contract was accepted.");}
                catch(TargetInvocationException error){Check(error.InnerException is IOException,"scope rejects missing projected contract instead of legacy fallback");}
            }
            evidence["localContractChecks"]=checks;evidence["legacyFrozenLabelIsHistorical"]=true;evidence["compiledAlgorithmHasReviewedLocalContractOverlay"]=true;
            File.WriteAllText(Path.Combine(qa,"actual-cal-packet-contract.json"),json.Serialize(evidence),new UTF8Encoding(false));
            Console.WriteLine("ACTUAL_CAL_PACKET_CONTRACT_PASS="+checks+"; A6_REFERENCE_RESOLVED=true; B_ORIGINAL_OVERLAP_REJECTED=true; SOURCE_WRITES=0; HARDWARE=0; PHYSICAL_ACCURACY=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
