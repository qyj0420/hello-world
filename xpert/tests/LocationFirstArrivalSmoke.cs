using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class LocationFirstArrivalSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Set(object value,string name,object data){var field=value.GetType().GetField(name,All);if(field!=null)field.SetValue(value,data);else value.GetType().GetProperty(name,All).SetValue(value,data,null);}
    static object Call(Type type,object target,string name,params object[] arguments)
    {return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length).Invoke(target,arguments);}
    static object Nested(Type type,string name){return Activator.CreateInstance(type.GetNestedType(name,BindingFlags.NonPublic),true);}
    static Array One(Type element,object value){var array=Array.CreateInstance(element,1);array.SetValue(value,0);return array;}
    sealed class Fixture
    {
        internal Type Engine;
        internal object Context,Incident,Reflection,Pair;
        internal double[] Raw;
        internal Fixture(Assembly assembly,int sign=1)
        {
            Engine=assembly.GetType("HVX.Task17.Location.LocationEngine",true);
            Raw=Enumerable.Repeat(5d,800).ToArray();
            Pulse(100,112,132,100*sign);Pulse(300,320,350,50*sign);
            Context=Nested(Engine,"A4Context");
            Set(Context,"Raw",Raw);Set(Context,"Signal",Raw);Set(Context,"RawCenter",5d);Set(Context,"Center",5d);
            Set(Context,"Sigma",.1d);Set(Context,"Fs",125e6);Set(Context,"Length",300d);Set(Context,"Velocity",170d);
            Set(Context,"Joints",new[]{0d,300d});Set(Context,"Velocities",new[]{170d});Set(Context,"RoundTrip",441);Set(Context,"RoundTripExact",2*300d/170*125);
            Incident=Packet(1,95,145,112);Reflection=Packet(2,295,365,320);
            Pair=Nested(Engine,"A4Pair");Set(Pair,"Incident",Incident);Set(Pair,"Reflection",Reflection);
            Set(Pair,"Confidence",.9d);Set(Pair,"State","accepted");
        }
        void Pulse(int start,int peak,int end,double amplitude)
        {for(int i=start;i<=end;i++)Raw[i]+=amplitude*(i<=peak?(double)(i-start)/(peak-start):(double)(end-i)/(end-peak));}
        object Packet(int id,int start,int end,int peak)
        {var value=Nested(Engine,"A4Pulse");Set(value,"Id",id);Set(value,"Start",start);Set(value,"End",end);Set(value,"Peak",peak);Set(value,"RawPeak",peak);Set(value,"RawAmplitudeIndex",peak);Set(value,"MappingValid",true);return value;}
        internal int[] Timing(bool first)
        {
            object[] args={Context,Pair,first,0,0};
            bool ok=(bool)Call(Engine,null,"A4OutputTiming",args);
            return new[]{ok?1:0,(int)args[3],(int)args[4]};
        }
        internal int[] PhysicalTiming(bool first)
        {
            Set(Context,"FirstArrivalPhysicalTiming",first);
            object[] args={Context,Incident,Reflection,0,0};
            bool ok=(bool)Call(Engine,null,"A4PhysicalTiming",args);
            return new[]{ok?1:0,(int)args[3],(int)args[4]};
        }
        internal double[,] Project(int algorithm,bool first)
        {
            var engine=Activator.CreateInstance(Engine,true);Set(Context,"Algorithm",algorithm);Set(engine,"equivalentContext",Context);
            Set(engine,"StatusJson","{}");Set(engine,"EffectiveInitialPhaseDegrees",0d);Set(engine,"EffectiveOscillationFrequencyHz",50d);
            var identity=Nested(Engine,"A4CanonicalOutputIdentity");Set(identity,"Phase",1d);Set(identity,"Record",42d);
            Set(identity,"Incident",112);Set(identity,"Reflection",320);Set(identity,"Pair",Pair);
            Set(engine,"canonicalMixedRows",One(identity.GetType(),identity));Set(engine,"projectRetainedRawPeaks",new[]{112});
            var packets=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Incident.GetType()));
            packets.Add(Incident);packets.Add(Reflection);Set(engine,"projectRetainedPackets",packets);
            var output=Nested(Engine,"A6OutputPulse");Set(output,"Pulse",Incident);Set(output,"Pair",Pair);
            Set(engine,"a6CurrentOutputPulses",One(output.GetType(),output));
            var rows=new double[1,36];rows[0,0]=1;rows[0,1]=42;rows[0,5]=300;rows[0,6]=170;rows[0,7]=125e6;
            rows[0,10]=112;rows[0,11]=320;rows[0,12]=208;rows[0,13]=0; // deliberately stale peak endpoint
            rows[0,15]=100;rows[0,16]=100;rows[0,17]=1;rows[0,31]=0;rows[0,32]=799;rows[0,33]=.9;rows[0,35]=1;
            object[] arguments={rows,first};Call(Engine,engine,"CompletePeakTimingProjection",arguments);
            return (double[,])arguments[0];
        }
    }
    static string Dll(string path)
    {
        if(File.Exists(path))return Path.GetFullPath(path);
        string flat=Path.Combine(path,"fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
        return File.Exists(flat)?flat:Path.Combine(path,"modules","DataAnalysis","fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
    }
    static double[] Wave(int a,int b,double amplitude)
    {
        var data=new double[4096];
        for(int i=0;i<data.Length;i++)
        {
            double x=(i-a)/10d,y=(i-b)/10d;
            data[i]=amplitude*(Math.Exp(-x*x/2)-.6*Math.Exp(-(x-3)*(x-3)/2))+
                amplitude*.6*(Math.Exp(-y*y/2)-.6*Math.Exp(-(y-3)*(y-3)/2))+.00001*amplitude*Math.Sin(i*.73);
        }
        return data;
    }
    static string ProjectFixture(string qa)
    {
        string root=Path.Combine(qa,"synthetic-first-arrival-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        string header=string.Join("\r\n",new[]{"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1",
            "Start_point (s)\t0.000004","Final_point (s)\t0.000006","SR\t125000000","Calib_Attenuation\t4.436974992",
            "Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"})+"\r\n";
        File.WriteAllText(Path.Combine(root,"ALL_1nC.CAL"),header+string.Join("\r\n",Wave(500,750,.01).Select(x=>x.ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));
        return root;
    }
    static double[,] PublicAnalysis(Assembly assembly,string root,int method,bool first)
    {
        string stem=method==6?"Seventh":method==5?"Sixth":"Project";
        Type type=assembly.GetType("HVX.Task17.Location."+stem+"LocationAnalyzer",true);
        object target=Activator.CreateInstance(type);
        var entry=type.GetMethods(All).Single(m=>m.Name=="Analyze"+stem+"LocationIncremental");
        var values=new System.Collections.Generic.Dictionary<string,object>{
            {"projectPath",root},{"locationFilePath",Path.Combine(root,"A_12.0kV_synthetic.loc")},{"locSampleHz",125e6},{"locData",Wave(500,700,1000)},{"useLocData",true},
            {"previousParameterMatrix",new double[0,36]},{"previousPulseEnvelopeMatrix",new double[0,0]},{"previousFilteredPulseEnvelopeMatrix",new double[0,0]},
            {"previousEquivalentPulseWaveformMatrix",new double[0,0]},{"locationDataId",1L},{"automaticAnalysis",true},{"locationAlgorithm",method},
            {"useFirstArrivalTiming",first},{"useAttenuationReference",true},{"usePulseWidthComparison",true},
            {"thresholdType",-1},{"manualThresholdAmplitude",100d},{"automaticThresholdMultiplier",3d},{"multipleReflectionSuppressionCount",1},
            {"maximumDischargeCount",10},{"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},{"initialPhaseDegrees",0d},
            {"oscillationFrequencyHz",50d},{"applyLinearFilter",false},{"linearFilterCutoffsHz",new[]{10000d,3000000d}},
            {"useEquivalentPulseWindow",false},{"equivalentPulseWindowNs",new double[0]},{"minimumCorrelationPercent",70d},
            {"attenuationErrorPercent",15d},{"pulseWidthErrorPercent",-1d},{"scoreWeights",new double[0]}};
        var parameters=entry.GetParameters();object[] inputs=parameters.Select(p=>p.IsOut?null:values[p.Name]).ToArray();
        bool success=(bool)entry.Invoke(target,inputs);string status=(string)inputs[Array.FindIndex(parameters,p=>p.Name=="statusMessage")];
        Check(success,"public method "+method+" "+(first?"first-arrival":"peak")+" completes: "+status);
        var result=(double[,])inputs[Array.FindIndex(parameters,p=>p.Name=="currentParameterMatrix2DOutput")];
        Console.WriteLine("PUBLIC_METHOD="+method+"; FIRST="+first+"; ROWS="+result.GetLength(0)+"; PAIRED="+Enumerable.Range(0,result.GetLength(0)).Count(r=>!double.IsNaN(result[r,11])));
        Check(result.GetLength(0)>0,"public method "+method+" retains nonempty synthetic pulse output");
        for(int r=0;r<result.GetLength(0);r++)if(!double.IsNaN(result[r,11]))
        {
            Check(result[r,11]-result[r,10]==result[r,12],"public output delay uses published timing pair");
            if(first)Check(Math.Abs(result[r,13]-(170-result[r,12]/125e6*1e6/2*170))<1e-6,"public first-arrival location derives from same delay");
        }
        return result;
    }
    static int Main(string[] args)
    {
        try
        {
            var assembly=Assembly.LoadFile(Path.GetFullPath(Dll(args[0])));
            Check(new Version(System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion)>=new Version(2,10,2,0),"release compiled first-arrival DLL version");
            foreach(int sign in new[]{1,-1})
            {
                var f=new Fixture(assembly,sign);var original=(double[])f.Raw.Clone();
                Check(f.Timing(false).SequenceEqual(new[]{1,112,320}),"peak mode canonical timing unchanged");
                Check(f.Timing(true).SequenceEqual(new[]{1,101,301}),"both edges define first-arrival timing, including negative polarity");
                Check(f.PhysicalTiming(false).SequenceEqual(new[]{1,112,320}),"matching geometry uses canonical peaks in peak mode");
                Check(f.PhysicalTiming(true).SequenceEqual(new[]{1,101,301}),"matching geometry uses the same first-arrival pair as final output");
                Check(f.Raw.SequenceEqual(original),"raw samples remain unchanged");
                for(int method=0;method<=5;method++)
                {
                    double[,] rows=f.Project(method,true);
                    Check(rows[0,10]==101&&rows[0,11]==301&&rows[0,12]==200,"method "+method+" uses same strict edges");
                    Check(Math.Abs(rows[0,13]-164)<1e-8,"method "+method+" replaces stale endpoint with edge-derived distance");
                    Check(rows[0,15]==100&&rows[0,16]==100&&rows[0,35]==1,"waveform charge preserved without acceptance promotion");
                }
            }
            var bad=new Fixture(assembly);for(int i=0;i<=112;i++)bad.Raw[i]=105;
            Check(bad.Timing(false).SequenceEqual(new[]{1,112,320}),"peak mode still available when leading edge truncated");
            Check(bad.Timing(true).SequenceEqual(new[]{0,-1,-1}),"truncated first arrival rejected; no peak substitution");
            Check(bad.PhysicalTiming(true).SequenceEqual(new[]{0,-1,-1}),"matching cannot use peak geometry after an unresolved first arrival");
            bool rejected=false;try{bad.Project(5,true);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException&&e.InnerException.Message.Contains("no peak timing");}
            Check(rejected,"final matrix publication fails closed for unresolved first arrival");
            var weak=new Fixture(assembly);Set(weak.Context,"Sigma",100d);
            Check(weak.Timing(true).SequenceEqual(new[]{0,-1,-1}),"noise floor cannot be labeled as first arrival");
            var invalid=new Fixture(assembly);invalid.Raw[108]=double.NaN;
            Check(invalid.Timing(true).SequenceEqual(new[]{0,-1,-1}),"nonfinite packet rejected");
            if(args.Length>2)
            {
                var baseline=Assembly.LoadFile(Path.GetFullPath(Dll(args[2])));
                // Shipping legacy components may obfuscate private engine names.
                // Their public API is still compared below; only private fixture
                // construction is unavailable in that case, not a passed check.
                if(baseline.GetType("HVX.Task17.Location.LocationEngine",false)==null)
                    Console.WriteLine("BASELINE_PRIVATE_FIXTURE=UNAVAILABLE_OBFUSCATED; PUBLIC_PARITY_REQUIRED");
                else foreach(int sign in new[]{1,-1})
                {
                    Check(new Fixture(baseline,sign).Timing(false).SequenceEqual(new Fixture(assembly,sign).Timing(false)),"actual baseline DLL peak pair parity");
                    for(int method=0;method<4;method++)
                    {
                        var before=new Fixture(baseline,sign).Project(method,false);var after=new Fixture(assembly,sign).Project(method,false);
                        Check(before.Cast<double>().Zip(after.Cast<double>(),(a,b)=>a.Equals(b)).All(equal=>equal),"actual baseline DLL method "+method+" peak matrix parity");
                    }
                }
            }
            if(args.Length>1)
            {
                string qa=Path.GetFullPath(args[1]);if(qa.IndexOf("\\build\\",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidOperationException("QA root must be under build.");
                string project=ProjectFixture(qa);
                Assembly baseline=args.Length>2?Assembly.LoadFile(Path.GetFullPath(Dll(args[2]))):null;
                for(int method=0;method<7;method++)
                {
                    var peak=PublicAnalysis(assembly,project,method,false);
                    var first=PublicAnalysis(assembly,project,method,true);
                    if(baseline!=null)
                    {
                        var before=PublicAnalysis(baseline,project,method,false);
                        Check(before.GetLength(0)==peak.GetLength(0)&&before.Cast<double>().Zip(peak.Cast<double>(),(a,b)=>a.Equals(b)).All(equal=>equal),
                            "public method "+method+" all peak matrix values unchanged from baseline");
                    }
                }
            }
            Console.WriteLine("LOCATION_FIRST_ARRIVAL_PASS="+checks);Console.WriteLine("DLL="+assembly.Location);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
