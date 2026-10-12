using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class LocationCyclePhaseSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    const double Fs=125000000;
    static int checks;
    static Type engine,reference;
    static void Check(bool good,string message){checks++;if(!good)throw new Exception(message);}
    static void Near(double actual,double expected,string message){Check(!double.IsNaN(actual)&&Math.Abs(actual-expected)<1e-8,message+": "+actual+" vs "+expected);}
    static object Call(Type t,object target,string name,params object[] args){return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
    static object Get(object obj,string name){return obj.GetType().GetField(name,All).GetValue(obj);}
    static void Set(object obj,string name,object value){obj.GetType().GetField(name,All).SetValue(obj,value);}
    static object Context(){return Activator.CreateInstance(engine.GetNestedType("A4Context",BindingFlags.NonPublic),true);}
    static void Invalid(Action action,string message)
    {
        bool rejected=false;
        try{action();}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}catch(InvalidDataException){rejected=true;}
        Check(rejected,message);
    }
    static string Header(bool positive=true,int count=4096,double rate=Fs)
    {
        return "HVX_CycleReference\t{\"schema\":\"HVX.LocationCycles/1\",\"sampleRateHz\":"+rate.ToString("R",CultureInfo.InvariantCulture)+
            ",\"sampleCount\":"+count+",\"captureStartSeconds\":0.125,\"firstCrossingPositive\":"+(positive?"true":"false")+
            ",\"crossingSeconds\":["+String.Join(",",new[]{0d,600/Fs,1800/Fs,2800/Fs,4095/Fs}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)))+"]}";
    }
    static double Phase(double sample,bool positive)
    {
        double[] boundaries={0,600,1800,2800,4095};int j=0;while(j<3&&sample>=boundaries[j+1])j++;
        return ((positive?0:180)+180*(j+(sample-boundaries[j])/(boundaries[j+1]-boundaries[j])))%360;
    }
    static double[] Wave()
    {
        var samples=new double[4096];for(int i=0;i<samples.Length;i++)
        {
            double a=(i-500)/10d,b=(i-700)/10d;
            samples[i]=1000*(Math.Exp(-a*a/2)-.6*Math.Exp(-(a-3)*(a-3)/2))+
                600*(Math.Exp(-b*b/2)-.6*Math.Exp(-(b-3)*(b-3)/2))+.01*Math.Sin(i*.73);
        }
        return samples;
    }
    static string WriteFixture(string qa)
    {
        string dir=Path.GetFullPath(Path.Combine(qa,"cycle-phase-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        string[] header={"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1","Start_point (s)\t0.000004",
            "Final_point (s)\t0.000006","SR\t125000000","Calib_Attenuation\t4.436974992","Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"};
        var cal=new double[4096];for(int i=0;i<cal.Length;i++)
        {double a=(i-500)/10d,b=(i-750)/10d;cal[i]=.01*(Math.Exp(-a*a/2)-.6*Math.Exp(-(a-3)*(a-3)/2))+.006*(Math.Exp(-b*b/2)-.6*Math.Exp(-(b-3)*(b-3)/2))+.0000001*Math.Sin(i*.73);}
        File.WriteAllLines(Path.Combine(dir,"ALL_1nC.CAL"),header.Concat(cal.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));
        return dir;
    }
    static string WriteLoc(string dir,string cycle)
    {
        string path=Path.Combine(dir,"A_12.0kV_synthetic.loc");
        string[] header={"Cable-length\t170","Velocity\t170","Delta x\t125000000","Phase\t18","Reserved\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0",cycle??"Reserved6\t0","Data"};
        File.WriteAllLines(path,header.Concat(Wave().Select(v=>(v*100000).ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));return path;
    }
    sealed class Result { internal bool Ok; internal string Status; internal double[,] Rows; }
    static Result Analyze(Assembly asm,string dir,int algorithm,bool memory,bool first,bool[] quadrants=null,double rate=Fs,bool manual=false)
    {
        string kind=algorithm==6?"Seventh":algorithm==5?"Sixth":"Project";
        Type t=asm.GetType("HVX.Task17.Location."+kind+"LocationAnalyzer",true);object target=Activator.CreateInstance(t);
        if(quadrants!=null)t.GetProperty("AnalysisPhaseQuadrants").SetValue(target,quadrants,null);
        var input=new Dictionary<string,object>{
            {"projectPath",dir},{"locationFilePath",Path.Combine(dir,"A_12.0kV_synthetic.loc")},{"locSampleHz",rate},{"locData",Wave()},{"useLocData",memory},
            {"previousParameterMatrix",new double[0,36]},{"previousPulseEnvelopeMatrix",new double[0,0]},{"previousFilteredPulseEnvelopeMatrix",new double[0,0]},
            {"previousEquivalentPulseWaveformMatrix",new double[0,0]},{"locationDataId",1L},{"automaticAnalysis",!manual},{"locationAlgorithm",algorithm},
            {"useFirstArrivalTiming",first},{"useAttenuationReference",true},{"usePulseWidthComparison",true},
            {"thresholdType",-1},{"manualThresholdAmplitude",100d},{"automaticThresholdMultiplier",3d},{"multipleReflectionSuppressionCount",1},
            {"maximumDischargeCount",10},{"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},{"initialPhaseDegrees",13d},
            {"oscillationFrequencyHz",50d},{"applyLinearFilter",false},{"linearFilterCutoffsHz",new[]{10000d,3000000d}},
            {"useEquivalentPulseWindow",false},{"equivalentPulseWindowNs",new double[0]},{"minimumCorrelationPercent",70d},
            {"attenuationErrorPercent",15d},{"pulseWidthErrorPercent",-1d},{"scoreWeights",new double[0]}};
        MethodInfo method=t.GetMethod("Analyze"+kind+"LocationIncremental");ParameterInfo[] p=method.GetParameters();
        object[] args=p.Select(v=>v.IsOut?null:input[v.Name]).ToArray();
        bool ok=(bool)method.Invoke(target,args);
        return new Result{Ok=ok,Status=(string)args[Array.FindIndex(p,v=>v.Name=="statusMessage")],Rows=(double[,])args[Array.FindIndex(p,v=>v.Name=="currentParameterMatrix2DOutput")]};
    }
    static void ValidateRows(Result result,bool positive,string name)
    {
        Check(result.Ok,name+" succeeds: "+result.Status);Check(result.Rows.GetLength(0)>0,name+" has actual outputs");
        for(int i=0;i<result.Rows.GetLength(0);i++)Near(result.Rows[i,3],Phase(result.Rows[i,10],positive),name+" actual per-cycle phase row "+i);
        Check(result.Status.Contains("recorded_alternating_zero_crossings"),name+" diagnostics identify recorded source");
    }
    static int Main(string[] args)
    {
        try
        {
            string dll=File.Exists(args[0])?args[0]:Path.Combine(args[0],"fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
            if(!File.Exists(dll))dll=Path.Combine(args[0],"modules","DataAnalysis","fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
            Assembly asm=Assembly.LoadFile(Path.GetFullPath(dll));engine=asm.GetType("HVX.Task17.Location.LocationEngine",true);reference=asm.GetType("Xiongxin.OWTS.Data.LocationCycleReference",true);
            object positive=Call(reference,null,"ParseHeader",new[]{Header()},4096,Fs),negative=Call(reference,null,"ParseHeader",new[]{Header(false)},4096,Fs);
            foreach(double sample in new[]{0d,300,599,600,1200,1799,1800,2300,2800,3400,4095})
            {Near((double)Call(reference,positive,"PhaseAt",sample),Phase(sample,true),"variable half cycles positive");Near((double)Call(reference,negative,"PhaseAt",sample),Phase(sample,false),"negative start anchors at 180 degrees");}
            Check(Call(reference,null,"ParseHeader",new[]{"Delta x\t125000000"},4096,Fs)==null,"legacy reference stays absent");
            Invalid(()=>Call(reference,null,"ParseHeader",new[]{Header()},4095,Fs),"count mismatch rejected");
            Invalid(()=>Call(reference,null,"ParseHeader",new[]{Header()},4096,Fs/2),"sample rate mismatch rejected");
            Invalid(()=>Call(reference,null,"ParseHeader",new[]{Header(),Header()},4096,Fs),"duplicate reference rejected");
            object owner=Activator.CreateInstance(engine,true),context=Context();Set(context,"Fs",Fs);
            Set(owner,"AnalysisPhaseQuadrants",new[]{false,false,false,true});
            Call(engine,owner,"A4ConfigureAnalysisPhase",context,13d,50d,negative);
            Check((bool)Call(engine,null,"A4AnalysisPhaseEligible",context,500),"gate uses variable reference not caller uniform phase");
            Check(!(bool)Call(engine,null,"A4AnalysisPhaseEligible",context,700),"adjacent quadrant rejected on same curve");
            object review=Call(engine,null,"A4ReviewContext",context);
            Check(Object.ReferenceEquals(Get(review,"CycleReference"),negative),"manual review carries the same immutable reference");
            Near((double)Call(engine,null,"A4PhaseAt",review,500d),330,"manual review phase reference");
            Invalid(()=>Call(engine,null,"A4ValidateCycleSampleRate",negative,Fs/2),"resolved override rate cannot relabel phase");
            string dir=WriteFixture(args[1]);string path=WriteLoc(dir,Header(false));byte[] original=File.ReadAllBytes(path);
            for(int method=0;method<=6;method++)
            {
                foreach(bool first in new[]{false,true})ValidateRows(Analyze(asm,dir,method,false,first),false,"file method "+method+" first="+first);
                ValidateRows(Analyze(asm,dir,method,false,false,null,Fs,true),false,"manual method "+method);
                Result selected=Analyze(asm,dir,method,false,false,new[]{false,false,false,true});
                ValidateRows(selected,false,"partial recorded quadrant method "+method);
                for(int r=0;r<selected.Rows.GetLength(0);r++)Check(selected.Rows[r,3]>=270&&selected.Rows[r,3]<360,"partial quadrant gate is applied before publication method "+method);
                Result none=Analyze(asm,dir,method,false,false,new[]{false,false,false,false});
                Check(none.Ok&&none.Rows.GetLength(0)==0,"method "+method+" all quadrants off excludes upstream analysis");
                Result bad=Analyze(asm,dir,method,false,false,null,Fs/2);Check(!bad.Ok&&bad.Rows.GetLength(0)==0,"method "+method+" caller rate mismatch rejected without rows");
            }
            Check(original.SequenceEqual(File.ReadAllBytes(path)),"analysis leaves LOC byte-for-byte unchanged");
            WriteLoc(dir,Header(true));ValidateRows(Analyze(asm,dir,6,false,false),true,"positive starting crossing file");
            WriteLoc(dir,Header(false,4095));Result malformed=Analyze(asm,dir,6,false,false);Check(!malformed.Ok&&malformed.Rows.GetLength(0)==0,"invalid recorded count fails before phase output");
            Result memory=Analyze(asm,dir,6,true,false);Check(memory.Ok&&memory.Rows.GetLength(0)>0,"memory ignores even malformed disk header");
            for(int r=0;r<memory.Rows.GetLength(0);r++)Near(memory.Rows[r,3],(13+360*50*memory.Rows[r,10]/Fs)%360,"memory retains caller uniform contract");
            WriteLoc(dir,null);Result legacy=Analyze(asm,dir,6,false,false);Check(legacy.Ok&&legacy.Rows.GetLength(0)>0,"legacy file retained");
            for(int r=0;r<legacy.Rows.GetLength(0);r++)Near(legacy.Rows[r,3],(13+360*50*legacy.Rows[r,10]/Fs)%360,"legacy phase unchanged");
            Check(legacy.Status.Contains("legacy_uniform_frequency"),"legacy diagnostics explicit");
            Console.WriteLine("PASS "+checks+" LocationCyclePhaseSmoke\nQA="+dir);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
