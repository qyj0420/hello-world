using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

public sealed class ProtectedAlgorithmRunner:MarshalByRefObject
{
    public string Run(string dll,string root,bool first,int method)
    {
        var assembly=Assembly.LoadFrom(dll);string stem=method==5?"Sixth":"Project";
        var type=assembly.GetType("HVX.Task17.Location."+stem+"LocationAnalyzer",true);
        object target=Activator.CreateInstance(type);
        var call=type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Single(m=>m.Name=="Analyze"+stem+"LocationIncremental");
        var values=new Dictionary<string,object>{
            {"projectPath",root},{"locationFilePath",Path.Combine(root,"A_12.0kV_1.loc")},{"locSampleHz",100000000d},{"locData",new double[0]},{"useLocData",false},
            {"previousParameterMatrix",new double[0,36]},{"previousPulseEnvelopeMatrix",new double[0,0]},{"previousFilteredPulseEnvelopeMatrix",new double[0,0]},{"previousEquivalentPulseWaveformMatrix",new double[0,0]},{"locationDataId",0L},
            {"automaticAnalysis",true},{"useFirstArrivalTiming",first},{"useAttenuationReference",true},{"usePulseWidthComparison",true},
            {"thresholdType",1},{"manualThresholdAmplitude",0d},{"automaticThresholdMultiplier",3d},{"multipleReflectionSuppressionCount",1},{"maximumDischargeCount",100},
            {"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},{"initialPhaseDegrees",0d},{"oscillationFrequencyHz",0d},{"applyLinearFilter",false},{"linearFilterCutoffsHz",new[]{10000d,3000000d}},
            {"useEquivalentPulseWindow",false},{"equivalentPulseWindowNs",new double[0]},
            {"minimumCorrelationPercent",70d},{"attenuationErrorPercent",15d},{"pulseWidthErrorPercent",-1d},{"scoreWeights",new double[0]},{"locationAlgorithm",method}};
        var args=call.GetParameters().Select(p=>p.IsOut?null:values[p.Name]).ToArray();
        bool ok=(bool)call.Invoke(target,args);
        var signature=call.GetParameters();string status=(string)args[Array.FindIndex(signature,p=>p.Name=="statusMessage")];
        if(!ok)throw new Exception("Algorithm failed: "+status);
        int rows=0;
        using(var bytes=new MemoryStream())using(var writer=new BinaryWriter(bytes)){
            for(int i=0;i<signature.Length;i++)if(signature[i].IsOut&&args[i] is double[,]){
                var matrix=(double[,])args[i];rows+=matrix.GetLength(0);writer.Write(matrix.GetLength(0));writer.Write(matrix.GetLength(1));
                foreach(double value in matrix)writer.Write(Double.IsNaN(value)?Double.NaN:value);
            }
            if(rows==0)throw new Exception("No candidate matrices; cannot establish numeric equivalence");
            writer.Flush();using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes.ToArray()))+"|"+status;
        }
    }
}
internal static class AlgorithmProtectionRegression
{
    static string Run(string dll,string project,bool first,int method){
        var domain=AppDomain.CreateDomain("offline-"+Guid.NewGuid(),null,new AppDomainSetup{ApplicationBase=AppDomain.CurrentDomain.BaseDirectory});
        try{return ((ProtectedAlgorithmRunner)domain.CreateInstanceAndUnwrap(typeof(ProtectedAlgorithmRunner).Assembly.FullName,typeof(ProtectedAlgorithmRunner).FullName)).Run(dll,project,first,method);}
        finally{AppDomain.Unload(domain);}
    }
    static double[] Wave(int reflection,double magnitude){return Enumerable.Range(0,4096).Select(i=>{
        double x=(i-500)/10d,y=(i-reflection)/10d;
        return magnitude*(Math.Exp(-x*x/2)-.6*Math.Exp(-(x-3)*(x-3)/2))+.6*magnitude*(Math.Exp(-y*y/2)-.6*Math.Exp(-(y-3)*(y-3)/2))+.00001*magnitude*Math.Sin(i*.73);
    }).ToArray();}
    static void Write(string path,string[] header,double[] wave){File.WriteAllText(path,String.Join("\r\n",header)+"\r\n"+String.Join("\r\n",wave.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));}
    internal static void Fixture(string root,int reflection,double scale){
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        Write(Path.Combine(root,"ALL_1nC.cal"),new[]{"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1","Start_point (s)\t0.000005","Final_point (s)\t0.000007","SR\t100000000","Calib_Attenuation\t4.436974992","Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"},Wave(700,.01));
        Write(Path.Combine(root,"A_12.0kV_1.loc"),new[]{"Cable length\t170","Velocity (m/us)\t170","Delta x\t1E-8","Phase\t0","Voltage\t12.0","Reserved1\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0","Data"},Wave(reflection,scale).Select(v=>v*100000).ToArray());
    }
    static int Main(string[] args){try{
        int checks=0;string qa=Path.GetFullPath(args[2]);Directory.CreateDirectory(qa);
        foreach(int offset in new[]{660,680,720})foreach(bool first in new[]{false,true})foreach(int method in new[]{5,7}){
            string root=Path.Combine(qa,"p"+offset);Fixture(root,offset,1000);
            string plain=Run(args[0],root,first,method),secure=Run(args[1],root,first,method);
            if(plain!=secure){File.WriteAllText(Path.Combine(qa,"before.txt"),plain);File.WriteAllText(Path.Combine(qa,"after.txt"),secure);throw new Exception("Numeric/diagnostic protection regression: "+offset+"/"+first+"/"+method);}
            checks++;Console.WriteLine("EQUAL "+offset+"/"+first+"/"+method);
        }
        Console.WriteLine("PROTECTION_EQUIVALENCE_PASS="+checks+"; HardwareAccessed=False; PhysicalAcceptance=False");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
