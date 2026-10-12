using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class LocationFilterBHostSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool yes,string message){checks++;if(!yes)throw new InvalidOperationException(message);}
    static object Get(object o,string key){return o.GetType().GetProperty(key,Flags).GetValue(o,null);}
    static void Set(object o,string key,object value){o.GetType().GetProperty(key,Flags).SetValue(o,value,null);}
    static object Call(object o,string key,params object[] args){return o.GetType().GetMethods(Flags).Single(m=>m.Name==key&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static IEnumerable<Control> Children(Control c){foreach(Control x in c.Controls){yield return x;foreach(var y in Children(x))yield return y;}}
    static void Invalid(Action action,string message){try{action();throw new InvalidOperationException("not rejected: "+message);}catch(TargetInvocationException ex){Check(ex.InnerException is ArgumentException,message);}}
    static bool Same(double[,] a,double[,] b){return a.Cast<double>().Zip(b.Cast<double>(),(x,y)=>x.Equals(y)).All(x=>x);}
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string host=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var analysis=Assembly.LoadFrom(Path.Combine(host,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var pressure=Assembly.LoadFrom(Path.Combine(host,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            var preference=analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true);
            preference.GetField("PathProvider",Flags).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            using(var a=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
            using(var p=(Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureTestControl",true)))
            using(var form=new Form{ClientSize=new Size(1256,720),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            {
                Set(p,"EmbeddedMode",true);form.Controls.Add(p);p.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                Set(a,"AutomaticDisplayRecommendation",false);Set(p,"AutomaticDisplayRecommendation",false);
                double[] x={500,500.1,501,800,997,1000};double[] q={200,5,20,40,200,200};
                var matrix=new double[x.Length,36];var waves=new double[x.Length,4096];
                for(int r=0;r<x.Length;r++){
                    matrix[r,0]=1;matrix[r,1]=r+1;matrix[r,2]=13.4;matrix[r,3]=45;matrix[r,5]=1000;matrix[r,6]=170;matrix[r,7]=125e6;
                    matrix[r,9]=r;matrix[r,10]=100;matrix[r,11]=710;matrix[r,12]=4.88;matrix[r,13]=x[r];matrix[r,15]=matrix[r,16]=q[r];matrix[r,32]=4095;matrix[r,33]=.9;matrix[r,35]=1;
                    for(int i=0;i<4096;i++)waves[r,i]=q[r]*Math.Exp(-Math.Pow((i-100)/14d,2));
                }
                var before=(double[,])matrix.Clone();Check((bool)Call(a,"LoadDataMatrices",matrix,waves),"analysis loads complete rows");Call(p,"SetSpectrumDataMatrix",matrix);
                long seq=(long)Get(p,"PressureRequestSequence");
                Set(a,"LocationFilterAlgorithmId","B");Set(p,"StatisticsLocationFilterAlgorithmId","B");
                foreach(double strength in new[]{0d,5d,6d,20d,40d,55d,90d,100d}){
                    Set(a,"LocationFilterStrength",strength);Set(p,"StatisticsFilterStrength",strength);
                    int[] ar=(int[])Get(a,"FilteredSourceRowIndices"),pr=(int[])Get(p,"StatisticsFilteredSourceRows");
                    Check(ar.SequenceEqual(pr),"B analysis/pressure rows agree at "+strength);
                    Check(ar.Contains(4),"unknown boundary retained at "+strength);
                    Check(ar.Contains(5),"terminal mask behavior retained at "+strength);
                    if(strength>5)Check(ar.SequenceEqual(new[]{0,1,2,4,5}),"independent expected mask at "+strength);
                }
                Check(((string)Get(p,"StatisticsLocationFilteringDiagnosticsJson")).Contains("unknownSourceRows"),"pressure current diagnostic available");
                Check(Children(p).Single(c=>c.Name=="PressureLocationFilterNotice").Text.Length>0,"unknown notice populated");
                var options=Call(p,"GetStatisticsLocationFilterBOptions");Set(options,"ChargeThresholdPc",250d);
                Check((double)Get(Call(p,"GetStatisticsLocationFilterBOptions"),"ChargeThresholdPc")==20d,"returned options cannot mutate active state");
                Call(p,"SetStatisticsLocationFilterBOptions",options);Set(a,"LocationFilterBChargeThresholdPc",250d);
                Check(((int[])Get(p,"StatisticsFilteredSourceRows")).Length==0&&((int[])Get(a,"FilteredSourceRowIndices")).Length==0,"no seeds hides all with explicit zero result");
                Set(options,"ChargeThresholdPc",20d);Call(p,"SetStatisticsLocationFilterBOptions",options);Set(a,"LocationFilterBChargeThresholdPc",20d);
                Set(options,"FromM",1001);Call(p,"SetStatisticsLocationFilterBOptions",options);
                Check(((int[])Get(p,"StatisticsFilteredSourceRows")).Length==0&&((string)Get(p,"StatisticsLocationFilteringDiagnosticsJson")).Contains("Error"),"geometry mismatch fails with explicit diagnostic");
                Set(options,"FromM",0);Call(p,"SetStatisticsLocationFilterBOptions",options);
                Set(options,"FromM",100001);Invalid(()=>Call(p,"SetStatisticsLocationFilterBOptions",options),"out of allowed range rejected atomically");
                Set(options,"FromM",0);Set(options,"Strength",double.NaN);Invalid(()=>Call(p,"SetStatisticsLocationFilterBOptions",options),"nonfinite strength rejected");
                Invalid(()=>Set(p,"StatisticsLocationFilterAlgorithmId","raw-method8"),"raw locator ID cannot select filtering");
                Set(a,"LocationFilterAlgorithmId","A");Set(p,"StatisticsLocationFilterAlgorithmId","A");
                Check(((int[])Get(a,"FilteredSourceRowIndices")).SequenceEqual((int[])Get(p,"StatisticsFilteredSourceRows")),"A unchanged after switch back");
                Set(a,"LocationFilterAlgorithmId","B");Set(p,"StatisticsLocationFilterAlgorithmId","B");
                Check(((int[])Get(a,"FilteredSourceRowIndices")).SequenceEqual((int[])Get(p,"StatisticsFilteredSourceRows")),"switch back does not reuse A score cache");
                Check((long)Get(p,"PressureRequestSequence")==seq,"filter choices do not issue hardware requests");
                Check(Same(matrix,before),"caller matrix unmodified");Check(Same((double[,])Get(a,"ResultParameterMatrix"),before),"analysis acceptance and values unchanged");
                Set(p,"StatisticsFilterEnabled",false);Check(((int[])Get(p,"StatisticsFilteredSourceRows")).Length==matrix.GetLength(0),"off restores source visibility");
                foreach(int language in new[]{0,1,18})foreach(var size in new[]{new Size(1120,416),new Size(1256,720)}){
                    form.ClientSize=size;Set(p,"LanguageIndex",language);Application.DoEvents();
                    var tab=Children(p).OfType<Button>().FirstOrDefault(c=>c.Name=="CompactSpectrumTab");if(tab!=null&&tab.Visible){tab.PerformClick();Application.DoEvents();}
                    var bar=Children(p).Single(c=>c.Name=="PressureStatisticsFilterBar");
                    foreach(Control child in bar.Controls)if(child.Visible)Check(bar.ClientRectangle.Contains(child.Bounds),"visible filter control fits "+language+" "+size+" "+child.Name);
                    if(language==0&&size.Width==1256)using(var bitmap=new Bitmap(p.Width,p.Height)){p.DrawToBitmap(bitmap,new Rectangle(Point.Empty,p.Size));bitmap.Save(Path.Combine(qa,"pressure-filter-b.png"));}
                }
                form.Close();
            }
            Console.WriteLine("LOCATION_FILTER_B_HOST_PASS="+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
