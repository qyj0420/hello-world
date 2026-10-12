using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class ReportPhaseAndMarkerSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static int Main(string[] args)
    {
        try{
            Directory.CreateDirectory(args[1]);
            Check(File.ReadAllBytes(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll")).SequenceEqual(File.ReadAllBytes(Path.Combine(args[0],"modules","PressureTest","HVX.PdReview.V1.0.dll"))),"Pressure uses the same deployed conclusion implementation");
            foreach(string module in new[]{"DataAnalysis","ReportGenerator"}){
                string name=module=="DataAnalysis"?"HVX.PdReview.V1.0.dll":"Xiongxin.OWTS."+module+".dll";
                var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules",module,name));var type=asm.GetType("HVX.Location.LocatorEngine",true);var engine=Activator.CreateInstance(type);
                var p=new double[36,36];for(int r=0;r<36;r++){p[r,0]=1+r/12;p[r,1]=r%3+1;p[r,2]=12;p[r,3]=30+r;p[r,9]=r;p[r,13]=80+(r%12)*.01;p[r,15]=200+r;p[r,16]=200+r;p[r,33]=.95;p[r,35]=1;}
                var method=type.GetMethod("AnalyzeWithStandardProfilePulseEnergyAndAlgorithmMode");
                object[] input={p,new double[0,0],new[]{0d,80d,414.8d},new[]{0d,80d,414.8d},new[]{0d,80d,414.8d},2d,.1d,8.7d,0d,0,0,true,1,0,""};
                var en=method.Invoke(engine,input);Check((bool)Get(en,"Ok"),module+" English conclusion runs: "+Get(en,"Error"));
                string text=(string)Get(en,"SummaryText")+String.Join("\n",(string[])Get(en,"DiagnosticArray"));
                Check(new[]{"L1","L2","L3"}.All(text.Contains)&&!new[]{"A phase","B phase","C phase","L0"}.Any(text.Contains),module+" all English conclusion phases match interface");
                input[11]=false;var zh=method.Invoke(engine,input);Check((bool)Get(zh,"Ok")&&((string)Get(zh,"SummaryText")).Contains("相"),module+" Chinese phases retained");
                foreach(string matrix in new[]{"LocationMatrix","PhaseAssessmentMatrix","SourceDecisionMatrix"})Check(((double[])Get(en,matrix)).SequenceEqual((double[])Get(zh,matrix)),module+" language does not change "+matrix);
                File.WriteAllText(Path.Combine(args[1],module+"-english.txt"),text);
                if(module!="ReportGenerator")continue;
                var renderer=asm.GetType("HVX.ReportGenerator.LocationChartRenderer",true);
                using(var image=new Bitmap(180,60))using(var g=Graphics.FromImage(image)){
                    g.Clear(Color.White);for(int phase=1;phase<=3;phase++)renderer.GetMethod("DrawMarker",F).Invoke(null,new object[]{g,phase,30f+(phase-1)*60,30f,15f});
                    Check(image.GetPixel(39,30).ToArgb()==Color.White.ToArgb(),"A circle interior is full white, not a thick gold annulus");
                    Check(image.GetPixel(45,30).R<240&&image.GetPixel(45,30).G<200,"A has a visible darker yellow edge");
                    Check(image.GetPixel(99,30).ToArgb()==Color.White.ToArgb(),"B remains hollow");image.Save(Path.Combine(args[1],"marker-comparison.png"));
                }
                var phaseType=asm.GetType("HVX.ReportGenerator.ReportPhase");var interval=asm.GetType("HVX.ReportGenerator.VoltageInterval").GetMethod("AllResults").Invoke(null,null);
                renderer.GetMethod("RenderChargeLocation",new[]{typeof(double[,]),interval.GetType(),phaseType,typeof(double),typeof(double),typeof(int),typeof(string),typeof(string)})
                    .Invoke(null,new object[]{p,interval,Enum.ToObject(phaseType,0),414.8,8.7,1,"Q-L",Path.Combine(args[1],"report-thin-markers.png")});
            }
            Console.WriteLine("REPORT_PHASE_MARKER_PASS="+checks);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
