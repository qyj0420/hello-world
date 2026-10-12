using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Unprotected compatibility-only entry; never adds exceptions to name protection.
internal static class LocationLegacyRecordedPhaseSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool v,string s){checks++;if(!v)throw new Exception(s);}
    static object Helper(string n,params object[] args){return typeof(PublishedLocationCyclePhaseSmoke).GetMethod(n,All).Invoke(null,args);}
    static double[,] Analyze(Assembly asm,string dir,int method)
    {
        Type t=asm.GetType("HVX.Task17.Location.LocationEngine",true);object engine=Activator.CreateInstance(t,true);
        var values=new Dictionary<string,object>{
            {"calibrationFilePath",Path.Combine(dir,"ALL_1nC.CAL")},{"locationFilePath",Path.Combine(dir,"A_12.0kV_synthetic.loc")},
            {"testFilePath",""},{"cableFilePath",Path.Combine(dir,"CABLE.in")},{"projectFilePath",Path.Combine(dir,"ITEM.in")},
            {"previousParameterMatrix",new double[0,36]},{"previousPulseEnvelopeMatrix",new double[0,0]},
            {"locationDataId",1L},{"automaticAnalysis",true},{"locationAlgorithm",method},{"sampleRateHz",125000000d},
            {"thresholdType",-1},{"manualThresholdAmplitude",100d},{"automaticThresholdMultiplier",3d},{"multipleReflectionSuppressionCount",1},
            {"maximumDischargeCount",10},{"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},
            {"initialPhaseDegrees",13d},{"oscillationFrequencyHz",50d}};
        var api=t.GetMethod("AnalyzeLocationFileIncrementalWithPhaseReference",All);var p=api.GetParameters();
        object[] args=p.Select(v=>v.IsOut?null:values[v.Name]).ToArray();bool ok=(bool)api.Invoke(engine,args);
        Check(ok,"legacy entry success: "+t.GetProperty("StatusMessage").GetValue(engine,null));
        return (double[,])args[Array.FindIndex(p,v=>v.Name=="parameterMatrix2DOutput")];
    }
    static int Main(string[] args)
    {
        try{
            var asm=Assembly.LoadFile(Path.GetFullPath(args[0]));string dir=(string)Helper("Fixture",args[1]);
            foreach(int method in new[]{0,1,2,3,4}){
                Helper("WriteLoc",dir,0,false);var old=Analyze(asm,dir,method);Check(old.GetLength(0)>0,"legacy actual output");
                for(int r=0;r<old.GetLength(0);r++)Check(Math.Abs(old[r,3]-(13+360*50*old[r,10]/125000000d))<1e-9,"legacy phase unchanged");
                foreach(int version in new[]{1,2}){
                    Helper("WriteLoc",dir,version,false);var recorded=Analyze(asm,dir,method);Check(recorded.GetLength(0)==old.GetLength(0),"legacy phase does not alter row count");
                    for(int r=0;r<recorded.GetLength(0);r++)Check(Math.Abs(recorded[r,3]-(double)Helper("Phase",recorded[r,10],false))<1e-9,"legacy consumer recorded v"+version+" method"+method);
                }
            }
            Console.WriteLine("LOCATION_LEGACY_RECORDED_PHASE_PASS="+checks+"; HARDWARE=False; QA="+dir);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
