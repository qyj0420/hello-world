using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PressureCumulativeAnalysisSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Field(object o,string name){return o.GetType().GetField(name,F).GetValue(o);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,F).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,F).SetValue(o,value,null);}
    static object Call(object o,string name,params object[] args){var m=o.GetType().GetMethods(F).SingleOrDefault(x=>x.Name==name&&x.GetParameters().Length==args.Length);if(m==null)throw new MissingMethodException(o.GetType().FullName,name+"/"+args.Length);return m.Invoke(o,args);}
    static double[] Wave(int peak,int reflected,double amplitude){return Enumerable.Range(0,4096).Select(i=>{
        double x=(i-peak)/10d,y=(i-reflected)/10d;
        return amplitude*(Math.Exp(-x*x/2)-.6*Math.Exp(-(x-3)*(x-3)/2))+
            amplitude*.6*(Math.Exp(-y*y/2)-.6*Math.Exp(-(y-3)*(y-3)/2))+.00001*amplitude*Math.Sin(i*.73);}).ToArray();}
    static void Write(string path,string[] header,double[] values){File.WriteAllText(path,String.Join("\r\n",header)+"\r\n"+String.Join("\r\n",values.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));}
    static string Fixture(string qa){string p=Path.Combine(qa,"p");Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",Encoding.UTF8);
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",Encoding.UTF8);
        Write(Path.Combine(p,"ALL_1nC.cal"),new[]{"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1","Start_point (s)\t0.000004","Final_point (s)\t0.000006","SR\t125000000","Calib_Attenuation\t4.436974992","Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"},Wave(500,750,.01));
        for(int n=0;n<3;n++)Write(Path.Combine(p,"A_12.0kV_"+n+".loc"),new[]{"Cable length\t170","Velocity (m/us)\t170","Delta x\t8E-9","Phase\t0","Voltage\t12.0","Reserved1\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0","Data"},Wave(500,700+n*5,1000+n*100).Select(v=>v*100000).ToArray());
        return p;}
    static void Equal(double[,] left,double[,] right,string label){Check(left.GetLength(0)==right.GetLength(0)&&left.GetLength(1)==right.GetLength(1),label+" shape");
        for(int r=0;r<left.GetLength(0);r++)for(int c=0;c<left.GetLength(1);c++)Check(left[r,c].Equals(right[r,c]),label+" cell "+r+","+c);}
    [STAThread]static int Main(string[] args){try{
        string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string project=Fixture(qa);
        var original=Directory.GetFiles(project).ToDictionary(p=>p,File.ReadAllBytes);
        var exe=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));Func<string,object> create=n=>Activator.CreateInstance(exe.GetType("Xiongxin.OWTS.Desktop."+n,true),true);
        var settings=create("AppSettings");var options=Get(settings,"TaskMinus3");Set(settings,"NoiseSimilarityTolerancePercent",3d);
        Set(options,"AutomaticThresholdMultiplier",4d);Set(options,"MaximumPulseCount",10);Set(options,"CorrelationErrorPercent",22d);
        Set(options,"PhaseFilterEnabled",false);Set(options,"Quadrant90To180",false);
        var owner=Activator.CreateInstance(exe.GetType("Xiongxin.OWTS.Desktop.ModuleHost",true),new[]{settings,create("ApplicationState"),create("Diagnostics"),(object)Path.Combine(args[0],"modules")});
        var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));var type=asm.GetType("HVX.PdReview.PdReviewControl",true);
        asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
        asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
        var engine=asm.GetType("HVX.PdReview.ReviewAnalysisEngine",true);var resultType=asm.GetType("HVX.PdReview.ReviewAnalysisResult",true);
        foreach(int algorithm in new[]{5,7})foreach(bool timing in new[]{false,true}){
            Set(options,"AlgorithmIndex",algorithm);Set(options,"UseFirstArrivalTiming",timing);
            object snapshot=Call(settings,"Clone");Set(options,"AutomaticThresholdMultiplier",999d);Set(settings,"NoiseSimilarityTolerancePercent",1d);
            using(var pressure=(Control)Activator.CreateInstance(type))using(var interactive=(Control)Activator.CreateInstance(type)){
                Call(owner,"ApplyPressureAnalysisSettings",pressure,snapshot);
                var q=(bool[])exe.GetType("Xiongxin.OWTS.Desktop.ModuleHost").GetMethods(F).Single(m=>m.Name=="ReadAnalysisQuadrants"&&m.GetParameters().Length==1).Invoke(null,new[]{snapshot});
                Call(owner,"ApplyDataAnalysisSettings",interactive,(string)Get(pressure,"ModelLibraryPath"),q,snapshot);
                Set(interactive,"NoiseSimilarityTolerancePercent",Get(snapshot,"NoiseSimilarityTolerancePercent"));
                foreach(string name in new[]{"AnalysisAlgorithmIndex","AnalysisUseFirstArrivalTiming","AnalysisAutomaticThresholdMultiplier","AnalysisCorrelationErrorPercent","AnalysisAttenuationTolerancePercent","AnalysisMaximumPulseCount","NoiseSimilarityTolerancePercent","ModelLibraryPath"})
                    Check(Get(pressure,name).Equals(Get(interactive,name)),"same snapshot reaches both consumers: "+name);
                Check((double)Get(pressure,"AnalysisAutomaticThresholdMultiplier")==4d,"later host edits cannot alter captured settings");
                Check((double)Get(pressure,"NoiseSimilarityTolerancePercent")==3d,"noise tolerance comes from SAME snapshot");
                foreach(var view in new[]{pressure,interactive}){Set(view,"PromptSavedAnalysisOnProjectLoad",false);Set(view,"ProjectPath",project);}
                var tree=Field(interactive,"analysisTree");var paths=(string[])Call(tree,"GetDataPaths");var catalog=(double[,])Call(tree,"GetDataCatalog");
                Check(paths.Length==3,"three actual synthetic files discovered");
                // Include only this trial's two files; scramble order and casing,
                // repeat a filename. Historical third file must not be included.
                string[] selection={paths[1].ToUpperInvariant(),paths[0],paths[1]};
                var progress=new List<string>();
                Action<int,int> report=(done,total)=>progress.Add(done+"/"+total);
                var actual=((Task<double[,]>)Call(pressure,"AnalyzeSavedPressureFilesWithProgressAsync",selection,report)).GetAwaiter().GetResult();
                Check(progress.SequenceEqual(new[]{"0/2","1/2","2/2"}),"progress counts unique selected records in processing order");
                Array results=Array.CreateInstance(resultType,2);
                for(int i=0;i<2;i++){
                    var request=Call(interactive,"CreateFileAnalysisRequest",paths[i],(long)catalog[i,1],true,(int)catalog[i,0]);
                    var result=engine.GetMethod("Analyze",F).Invoke(null,new[]{request});Check((bool)Field(result,"Success"),"interactive per-file engine succeeds");results.SetValue(result,i);
                }
                object[] merge={results,null,null};Call(interactive,"MergeAnalysisResults",merge);var expected=(double[,])merge[1];
                Check(expected.GetLength(0)>0,"nonempty independent per-file reference");Equal(actual,expected,"algorithm "+algorithm+" firstArrival="+timing);
                Check(Enumerable.Range(0,actual.GetLength(0)).All(r=>actual[r,1]!=catalog[2,1]),"earlier trial's file excluded");
            }
            Set(options,"AutomaticThresholdMultiplier",4d);Set(settings,"NoiseSimilarityTolerancePercent",3d);
        }
        foreach(var pair in original)Check(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),"analysis preserves source: "+Path.GetFileName(pair.Key));
        ((IDisposable)owner).Dispose();Console.WriteLine("PRESSURE_CUMULATIVE_ANALYSIS_PASS="+checks+"; HardwareAccessed=false; independent real engine A/B paths");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
