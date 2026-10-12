using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PressureTerminationEvidenceSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static double phase,oldId,newId;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static double[,] Evidence(Control c,double[,] p,string file){var task=(Task<double[,]>)Call(c,"GetPressureTerminationEvidenceAsync",p,file);var started=DateTime.UtcNow;while(!task.IsCompleted&&(DateTime.UtcNow-started).TotalSeconds<15){Application.DoEvents();Thread.Sleep(2);}Check(task.IsCompleted,"bounded Task15 export");return task.GetAwaiter().GetResult();}
    static double[,] Matrix(double position){var m=new double[48,36];for(int r=0;r<48;r++){
        m[r,0]=phase;m[r,1]=r<24?oldId:newId;m[r,2]=13.4;m[r,3]=25+r%24*4;m[r,5]=400;m[r,6]=170;m[r,7]=125e6;
        m[r,9]=r;m[r,10]=100;m[r,11]=710;m[r,12]=610;m[r,13]=position+r%6*.006;m[r,15]=11000+r*17;m[r,16]=11000;
        m[r,20]=100;m[r,21]=.92;m[r,23]=1;m[r,24]=1;m[r,32]=1023;m[r,33]=.91;m[r,35]=1;
    }return m;}
    [STAThread]static int Main(string[] args){try{
        string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]),project=Path.Combine(qa,"p");Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度\t400\r\n",Encoding.UTF8);
        File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t高压插拔头\t0\r\n1\t终端\t高压插拔头\t400\r\n",Encoding.UTF8);
        string old=Path.Combine(project,"A_13.40kV_1.loc"),latest=Path.Combine(project,"A_13.40kV_2.loc");File.WriteAllBytes(old,new byte[256]);File.WriteAllBytes(latest,new byte[256]);
        var module=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
        module.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
        Application.EnableVisualStyles();using(var review=(Control)Activator.CreateInstance(module.GetType("HVX.PdReview.PdReviewControl",true))){
            Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);var handle=review.Handle;
            var tree=Get(review,"AnalysisTree");var paths=(string[])Call(tree,"GetDataPaths");var catalog=(double[,])Call(tree,"GetDataCatalog");
            int a=Array.FindIndex(paths,p=>String.Equals(p,old,StringComparison.OrdinalIgnoreCase)),b=Array.FindIndex(paths,p=>String.Equals(p,latest,StringComparison.OrdinalIgnoreCase));
            Check(a>=0&&b>=0,"both saved records available");phase=catalog[b,0];oldId=catalog[a,1];newId=catalog[b,1];
            Set(Get(review,"Spectrum"),"OperatingVoltageKv",8.7d);
            var input=Matrix(.02);var before=(double[,])input.Clone();var endpoints=Evidence(review,input,latest);
            if(endpoints.GetLength(0)!=24){
                var debug=module.GetType("HVX.Location.EmbeddedConclusions",true).GetMethod("Analyze",F).Invoke(null,new object[]{input,new[]{0d,400d},new double[0],new double[0],8.7d,false,0,0,1d,.1d});
                Console.WriteLine("EXPORTED="+endpoints.GetLength(0)+" PHASE="+phase+" FILES="+oldId+","+newId);
                Console.WriteLine("PHASE_MATRIX="+String.Join(",",(double[])Get(debug,"PhaseAssessmentMatrix")));
            }
            Check(endpoints.GetLength(0)==24,"only latest record endpoint evidence after cumulative Task15 analysis");
            Check(Enumerable.Range(0,24).All(r=>endpoints[r,0]==phase&&endpoints[r,1]==2&&endpoints[r,4]>=24),"start component and exact source row retained");
            Check(input.Cast<double>().SequenceEqual(before.Cast<double>()),"source matrix not mutated");
            Check(Evidence(review,Matrix(200),latest).GetLength(0)==0,"cable body is not manufactured into endpoint evidence");
            input=Matrix(399.3);endpoints=Evidence(review,input,latest);Check(endpoints.GetLength(0)==24&&endpoints[0,1]==3,"remote termination supplied by existing attribution");
            input=Matrix(0);for(int r=0;r<48;r++)input[r,13]=0;Check(Evidence(review,input,latest).GetLength(0)==0,"exact snapped boundary pileup is not accepted terminal attribution");
            input=Matrix(.02);for(int r=0;r<48;r++)input[r,35]=0;Check(Evidence(review,input,latest).GetLength(0)==0,"unaccepted candidates do not trigger");
            input=Matrix(.02);for(int r=0;r<48;r++)input[r,11]=Double.NaN;Check(Evidence(review,input,latest).GetLength(0)==0,"cached position without valid pulse pair does not trigger");
            input=Matrix(.02);for(int r=0;r<48;r++)input[r,15]=10000;Check(Evidence(review,input,latest).GetLength(0)==0,"exact threshold not greater than10nC");
        }
        Console.WriteLine("PRESSURE_TERMINATION_EVIDENCE_PASS="+checks+"; HardwareAccessed=false; Input=synthetic");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
