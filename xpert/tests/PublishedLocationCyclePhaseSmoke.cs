using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Uses only the published Analyzer APIs. Works after private-name obfuscation.
internal static class PublishedLocationCyclePhaseSmoke
{
    const double Fs=125000000;
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static bool Same(double a,double b){return Double.IsNaN(a)?Double.IsNaN(b):!Double.IsNaN(b)&&Math.Abs(a-b)<1e-8*Math.Max(1,Math.Abs(a));}
    static double[] Wave(bool tail)
    {
        var data=new double[125000];
        for(int i=0;i<data.Length;i++){
            data[i]=.01*Math.Sin(i*.73);
            foreach(int origin in tail?new[]{500,120000}:new[]{500}){
                double a=(i-origin)/10d,b=(i-origin-200)/10d;
                data[i]+=1000*(Math.Exp(-a*a/2)-.6*Math.Exp(-(a-3)*(a-3)/2))+600*(Math.Exp(-b*b/2)-.6*Math.Exp(-(b-3)*(b-3)/2));
            }
        }return data;
    }
    static double Phase(double sample,bool tail)
    {
        double[] crossing={0,600,1800,2800,tail?100000:124999};
        if(sample<0||sample>=125000||sample>crossing[4])return Double.NaN;
        int j=0;while(j<3&&sample>=crossing[j+1])j++;
        return (180+180*(j+(sample-crossing[j])/(crossing[j+1]-crossing[j])))%360;
    }
    static string Header(int version,bool tail)
    {
        if(version==0)return "Reserved\t0";
        return "HVX_CycleReference\t{\"schema\":\"HVX.LocationCycles/"+version+"\",\"sampleRateHz\":125000000,\"sampleCount\":125000,\"captureStartSeconds\":0.125,"+
            (version==2?"\"savedDurationSeconds\":0.001,":"")+"\"firstCrossingPositive\":false,\"crossingSeconds\":["+
            String.Join(",",new double[]{0,600,1800,2800,tail?100000:124999}.Select(v=>(v/Fs).ToString("R",Inv)))+
            "],\"pdAcquisitionFilterId\":\"PD-HP10K-SYMMETRIC-FIR4-v1\",\"pdAcquisitionFilterScope\":\"full-segment-before-cycle-crop\"}";
    }
    static string Fixture(string directory)
    {
        string dir=Path.GetFullPath(Path.Combine(directory,"published-cycle-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        string[] header={"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1","Start_point (s)\t0.000004","Final_point (s)\t0.000006","SR\t125000000","Calib_Attenuation\t4.436974992","Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"};
        var cal=new double[4096];for(int i=0;i<cal.Length;i++){
            double a=(i-500)/10d,b=(i-750)/10d;cal[i]=.01*(Math.Exp(-a*a/2)-.6*Math.Exp(-(a-3)*(a-3)/2))+.006*(Math.Exp(-b*b/2)-.6*Math.Exp(-(b-3)*(b-3)/2))+.0000001*Math.Sin(i*.73);
        }
        File.WriteAllLines(Path.Combine(dir,"ALL_1nC.CAL"),header.Concat(cal.Select(v=>v.ToString("R",Inv))),new UTF8Encoding(true));return dir;
    }
    static string WriteLoc(string dir,int version,bool tail)
    {
        string path=Path.Combine(dir,"A_12.0kV_synthetic.loc");
        string[] header={"HVX LOC cycle record / engineering pC x 100000","Received UTC\t2026-10-08T00:00:00Z","Phase\t180","Target peak kV\t12",
            "Cable-length\t170","Velocity (m/us)\t170","Delta x [s]\t8E-09","Charge unit\tpC x 100000","Capture start seconds\t0.125","Acquisition context\t{}",Header(version,tail)};
        File.WriteAllLines(path,header.Concat(Wave(tail).Select(v=>(v*100000).ToString("R",Inv))),new UTF8Encoding(true));return path;
    }
    sealed class Result {internal bool Ok;internal string Status;internal double[,] Rows;}
    static Result Analyze(Assembly asm,string dir,int method,bool first,bool[] mask=null,bool memory=false,double rate=Fs)
    {
        string kind=method==6?"Seventh":method==5?"Sixth":"Project";
        Type type=asm.GetType("HVX.Task17.Location."+kind+"LocationAnalyzer",true);object instance=Activator.CreateInstance(type);
        if(mask!=null)type.GetProperty("AnalysisPhaseQuadrants",BindingFlags.Public|BindingFlags.Instance).SetValue(instance,mask,null);
        var values=new Dictionary<string,object>{
            {"projectPath",dir},{"locationFilePath",Path.Combine(dir,"A_12.0kV_synthetic.loc")},{"locSampleHz",rate},{"locData",Wave(false)},{"useLocData",memory},
            {"previousParameterMatrix",new double[0,36]},{"previousPulseEnvelopeMatrix",new double[0,0]},{"previousFilteredPulseEnvelopeMatrix",new double[0,0]},{"previousEquivalentPulseWaveformMatrix",new double[0,0]},
            {"locationDataId",1L},{"automaticAnalysis",true},{"locationAlgorithm",method},{"useFirstArrivalTiming",first},{"useAttenuationReference",true},{"usePulseWidthComparison",true},
            {"thresholdType",-1},{"manualThresholdAmplitude",100d},{"automaticThresholdMultiplier",3d},{"multipleReflectionSuppressionCount",1},{"maximumDischargeCount",10},
            {"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},{"initialPhaseDegrees",13d},{"oscillationFrequencyHz",50d},{"applyLinearFilter",false},{"linearFilterCutoffsHz",new[]{10000d,3000000d}},
            {"useEquivalentPulseWindow",false},{"equivalentPulseWindowNs",new double[0]},{"minimumCorrelationPercent",70d},{"attenuationErrorPercent",15d},{"pulseWidthErrorPercent",-1d},{"scoreWeights",new double[0]}};
        MethodInfo api=type.GetMethod("Analyze"+kind+"LocationIncremental",BindingFlags.Public|BindingFlags.Instance);var p=api.GetParameters();object[] input=p.Select(v=>v.IsOut?null:values[v.Name]).ToArray();
        bool ok=(bool)api.Invoke(instance,input);return new Result{Ok=ok,Status=(string)input[Array.FindIndex(p,v=>v.Name=="statusMessage")],Rows=(double[,])input[Array.FindIndex(p,v=>v.Name=="currentParameterMatrix2DOutput")]};
    }
    static void Success(Result r,string name){Check(r.Ok,name+": "+r.Status);}
    static int Main(string[] args)
    {
        try{
            string dll=File.Exists(args[0])?args[0]:Path.Combine(args[0],"modules","DataAnalysis","fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
            var asm=Assembly.LoadFile(Path.GetFullPath(dll));string dir=Fixture(args[1]);
            foreach(int method in new[]{0,5,6,7})foreach(bool first in new[]{false,true}){
                WriteLoc(dir,0,false);Result legacy=Analyze(asm,dir,method,first);Success(legacy,"legacy method"+method);Check(legacy.Rows.GetLength(0)>0,"legacy produces real candidate");
                Result legacyMemory=Analyze(asm,dir,method,first,null,true);Success(legacyMemory,"original memory contract");
                foreach(int version in new[]{1,2}){
                    string file=WriteLoc(dir,version,false);byte[] original=File.ReadAllBytes(file);Result recorded=Analyze(asm,dir,method,first);Success(recorded,"v"+version+" method"+method);
                    Check(recorded.Rows.GetLength(0)==legacy.Rows.GetLength(0),"phase source cannot change raw candidate count");
                    for(int r=0;r<recorded.Rows.GetLength(0);r++){
                        double anchor=Math.Round((legacy.Rows[r,3]-13)*Fs/(360*50));Check(anchor>=0&&anchor<125000,"legacy-derived original phase anchor");
                        Check(Same(recorded.Rows[r,3],Phase(anchor,false)),"public output uses measured crossings; method="+method+" first="+first+" version="+version);
                        for(int c=0;c<36;c++)if(c!=3)Check(Same(recorded.Rows[r,c],legacy.Rows[r,c]),"unrelated numeric output preserved col"+c);
                    }
                    Check(recorded.Status.Contains("recorded_alternating_zero_crossings"),"public diagnostics identify phase source");
                    Check(original.SequenceEqual(File.ReadAllBytes(file)),"public analysis does not rewrite input");
                }
                WriteLoc(dir,2,true);Result tail=Analyze(asm,dir,method,first);Success(tail,"v2 tail");
                Check(Enumerable.Range(0,tail.Rows.GetLength(0)).Any(r=>tail.Rows[r,10]>100000&&Double.IsNaN(tail.Rows[r,3])),"unbracketed retained tail remains unknown, method"+method);
                Result partial=Analyze(asm,dir,method,first,new[]{false,false,false,true});Success(partial,"partial recorded mask");Check(partial.Rows.GetLength(0)>0,"known partial quadrant retained");
                for(int r=0;r<partial.Rows.GetLength(0);r++)Check(partial.Rows[r,3]>=270&&partial.Rows[r,3]<360&&partial.Rows[r,10]<100000,"unknown tail cannot enter selected phase quadrant");
                Result none=Analyze(asm,dir,method,first,new[]{false,false,false,false});Success(none,"none mask");Check(none.Rows.GetLength(0)==0,"none mask excludes all candidates");
                Result bad=Analyze(asm,dir,method,first,null,false,Fs/2);Check(!bad.Ok&&bad.Rows.GetLength(0)==0,"recorded rate override rejected atomically");
                Result memory=Analyze(asm,dir,method,first,null,true);Success(memory,"memory contract");
                Check(memory.Rows.GetLength(0)==legacyMemory.Rows.GetLength(0),"memory ignores recorded header");
                for(int r=0;r<memory.Rows.GetLength(0);r++)for(int c=0;c<36;c++)Check(Same(memory.Rows[r,c],legacyMemory.Rows[r,c]),"memory contract independent of disk header method="+method+" first="+first+" row="+r+" col="+c);
            }
            Console.WriteLine("PUBLISHED_LOCATION_CYCLE_PHASE_PASS="+checks+"; PRIVATE_API=False; HARDWARE=False; QA="+dir);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
