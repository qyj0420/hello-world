using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class StatisticsEmptyResultSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Pump(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(5);}Check(done(),"bounded advice completion");}
    static bool Equal(double[,] a,double[,] b){return a.GetLength(0)==b.GetLength(0)&&a.GetLength(1)==b.GetLength(1)&&a.Cast<double>().Zip(b.Cast<double>(),(x,y)=>x.Equals(y)).All(v=>v);}
    static double[,] Data()
    {
        var m=new double[48,36];for(int r=0;r<48;r++){
            m[r,0]=1;m[r,1]=1;m[r,2]=8.7;m[r,3]=30;m[r,5]=1000;m[r,6]=170;m[r,7]=125000000;
            m[r,9]=r;m[r,10]=100+r;m[r,11]=500+r;m[r,12]=400;m[r,13]=50+18*r;
            m[r,15]=m[r,16]=100;m[r,22]=.5;m[r,28]=10;m[r,32]=2048;m[r,33]=.9;m[r,35]=1;
        }return m;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]);
            var analysis=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var pressure=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            var matrix=Data();var original=(double[,])matrix.Clone();
            var policy=analysis.GetType("HVX.PdReview.ReviewDisplayRecommendation",true);
            var advice=policy.GetMethod("Evaluate",new[]{typeof(double[,]),typeof(bool[]),typeof(double),typeof(double),typeof(double)}).Invoke(null,new object[]{matrix,Enumerable.Repeat(true,48).ToArray(),40d,30d,60d});
            Check((string)Get(advice,"ReasonCode")=="WouldHideAll"&&(int)Get(advice,"RetainedRows")==0&&!(bool)Get(advice,"Filter"),"48 scattered pulses in one acquisition legitimately have no stable repeated-record cluster at strength40");
            foreach(bool pressureView in new[]{true,false})
            using(var form=new Form{ClientSize=new Size(1280,850),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=(Control)Activator.CreateInstance(pressureView?pressure.GetType("HVX.Controls.PressureTestControl",true):analysis.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                if(pressureView){Set(control,"EmbeddedMode",true);Set(control,"SimulationMode",true);Set(control,"FrequencyReminderEnabled",false);}
                else Set(control,"EnableAiFeatures",false);
                Set(control,"AutomaticDisplayRecommendation",false);control.Dock=DockStyle.Fill;form.Controls.Add(control);form.Show();Application.DoEvents();
                object spectrum=pressureView?Field(Field(control,"spectrumDashboardView"),"Review"):Get(control,"Spectrum");
                Set(spectrum,"AutomaticConclusions",false);Set(spectrum,"LanguageIndex",0);
                string filter=pressureView?"StatisticsFilterEnabled":"LocationFilterEnabled";
                string strength=pressureView?"StatisticsFilterStrength":"LocationFilterStrength";
                string rows=pressureView?"StatisticsFilteredSourceRows":"FilteredSourceRowIndices";
                Action load=()=>{if(pressureView)Call(control,"SetSpectrumDataMatrix",matrix);else Check((bool)Call(control,"LoadDataMatrices",matrix,new double[48,2048]),"review load succeeds");};
                Set(control,filter,false);load();Check(((int[])Get(control,rows)).Length==48,"unfiltered source has48 "+pressureView);
                Set(control,strength,40d);Set(control,filter,true);
                Check(!(bool)Get(control,"AutomaticDisplayRecommendation")&&((int[])Get(control,rows)).Length==0,"manual filter keeps explicit zero and exits automatic "+pressureView);
                Check((string)Get(spectrum,"EmptyStateText")=="当前筛选无可见脉冲","filtered empty is not a physical no-discharge verdict "+pressureView);
                Check(((double[,])Get(spectrum,"ResultParameterMatrix")).Cast<double>().Count()==48*36,"empty display retains all source cells "+pressureView);
                Set(control,"AutomaticDisplayRecommendation",true);
                Pump(()=>((string)Get(control,"DisplayRecommendationJson")).Contains("WouldHideAll")&&!(bool)Get(control,filter));
                Check(((int[])Get(control,rows)).Length==48,"explicit automatic mode restores rows by avoiding hide-all recommendation "+pressureView);
                // Queue a fresh recommendation, then explicitly take manual
                // ownership before the asynchronous reply can be applied.
                Set(control,"AutomaticDisplayRecommendation",true);Set(control,filter,true);
                Check(!(bool)Get(control,"AutomaticDisplayRecommendation"),"manual click exits pending automatic mode "+pressureView);
                var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<350){Application.DoEvents();Thread.Sleep(5);}
                Check((bool)Get(control,filter)&&((int[])Get(control,rows)).Length==0,"stale recommendation cannot overwrite manual choice "+pressureView);
                load();Pump(()=>((string)Get(control,"DisplayRecommendationJson")).Contains("WouldHideAll"));
                Check(!(bool)Get(control,"AutomaticDisplayRecommendation")&&(bool)Get(control,filter)&&((int[])Get(control,rows)).Length==0,"reload retains manual state without stale cache reset "+pressureView);
                Set(control,filter,false);
                Check(((int[])Get(control,rows)).SequenceEqual(Enumerable.Range(0,48)),"turning filter off restores exact source identities "+pressureView);
                Check(Equal((double[,])Get(spectrum,"ResultParameterMatrix"),original),"display switching never modifies source values or acceptance "+pressureView);
                Set(spectrum,"LanguageIndex",1);Set(control,filter,true);
                Check((string)Get(spectrum,"EmptyStateText")=="No visible pulses in the current selection","English empty state remains neutral "+pressureView);
                if(pressureView)Check((long)Get(control,"PressureRequestSequence")==0,"display-only choices do not pressure");
                form.Close();
            }
            Check(Equal(matrix,original),"caller matrix unchanged");
            Console.WriteLine("STATISTICS_EMPTY_RESULT_PASS="+checks+"; HARDWARE=False; USER_PROJECTS=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
