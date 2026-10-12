using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Collections.Generic;

static class AutomaticDisplayRecommendationSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int n;static void Check(bool ok,string why){n++;if(!ok)throw new Exception(why);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object v){o.GetType().GetProperty(name,All).SetValue(o,v,null);}
    static object Call(object o,string name,params object[] a){return o.GetType().GetMethod(name,All).Invoke(o,a);}
    static void Pump(Func<bool> done){var watch=Stopwatch.StartNew();while(!done()&&watch.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(10);}Check(done(),"background advice settled");}
    static IEnumerable<Control> Children(Control c){foreach(Control v in c.Controls){yield return v;foreach(var w in Children(v))yield return w;}}
    static double[,] Data(int count){var a=new double[count,36];for(int r=0;r<count;r++){
        a[r,0]=1;a[r,1]=r+1;a[r,2]=10;a[r,5]=500;a[r,6]=170;a[r,7]=125000000;a[r,9]=r;
        a[r,10]=100;a[r,11]=500;a[r,12]=400;a[r,13]=r<count-3?100+r%3*.01:300+(r-(count-3))*65;
        a[r,15]=a[r,16]=r<count-3?1000:1;a[r,22]=.5;a[r,28]=10;a[r,31]=0;a[r,32]=2048;a[r,33]=r%2==0?.15:.9;a[r,35]=1;
    }return a;}
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();string build=args[0];
        var analysis=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
        var pressure=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        Type policy=analysis.GetType("HVX.PdReview.ReviewDisplayRecommendation",true);
        var matrix=Data(30);var original=(double[,])matrix.Clone();
        Func<double[,],double,object> evaluate=(m,strength)=>policy.GetMethod("Evaluate",new[]{typeof(double[,]),typeof(bool[]),typeof(double),typeof(double),typeof(double)}).Invoke(null,new object[]{m,Enumerable.Repeat(true,m.GetLength(0)).ToArray(),strength,30d,60d});
        var recommended=evaluate(matrix,40);
        Check((bool)Get(recommended,"Filter")&&(bool)Get(recommended,"Classification"),"stable repeated cluster and weak scatter recommends both");
        Check(!(bool)Get(evaluate(Data(1),100),"Filter"),"singleton never auto filtered");
        Check(!(bool)Get(evaluate(matrix,0),"Filter"),"no-effect threshold stays off");
        var unavailable=Data(4);for(int i=0;i<4;i++){unavailable[i,2]=Double.NaN;unavailable[i,33]=Double.NaN;}
        var none=evaluate(unavailable,40);Check(!(bool)Get(none,"Filter")&&!(bool)Get(none,"Classification"),"missing evidence stays off");
        using(var form=new Form{ShowInTaskbar=false,Location=new System.Drawing.Point(-25000,-25000),StartPosition=FormStartPosition.Manual,Width=1256,Height=820})
        using(var review=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
        using(var press=(Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureTestControl",true))){
            review.Dock=DockStyle.Fill;form.Controls.Add(review);form.Show();
            Set(review,"EnableAiFeatures",false);Set(Get(review,"Spectrum"),"AutomaticConclusions",false);
            Check((bool)Call(review,"LoadDataMatrices",matrix,new double[30,2048]),"analysis matrix loaded");
            Pump(()=>((string)Get(review,"DisplayRecommendationJson")).Contains("MixedStable"));
            Check((bool)Get(review,"LocationFilterEnabled")&&(bool)Get(Get(review,"Spectrum"),"ConfidenceClassificationEnabled"),"analysis applies recommendation");
            var rows=(int[])Get(review,"FilteredSourceRowIndices");
            Set(review,"LocationFilterEnabled",false);Check(!(bool)Get(review,"AutomaticDisplayRecommendation"),"manual filter overrides automatic");
            Call(Get(review,"Spectrum"),"PerformClassificationCheckboxClick");Check(!(bool)Get(review,"AutomaticDisplayRecommendation"),"manual classification remains manual");
            form.Controls.Remove(review);press.Dock=DockStyle.Fill;form.Controls.Add(press);Application.DoEvents();
            Set(press,"ActionHandshakeEnabled",true);long sequence=(long)Get(press,"ActionSequence");
            Call(press,"SetSpectrumDataMatrix",matrix);
            Pump(()=>((string)Get(press,"DisplayRecommendationJson")).Contains("MixedStable"));
            Check((bool)Get(press,"StatisticsFilterEnabled"),"pressure applies same auto recommendation");
            Check(!(bool)Get(press,"ActionPending")&&(long)Get(press,"ActionSequence")==sequence,"automatic display never occupies pressure action mailbox");
            Check(rows.SequenceEqual((int[])Get(press,"StatisticsFilteredSourceRows")),"pressure and analysis identical filtered source rows");
            Set(press,"StatisticsFilterEnabled",false);Call(press,"SetSpectrumDataMatrix",Data(31));
            Pump(()=>((string)Get(press,"DisplayRecommendationJson")).Contains("MixedStable"));
            Check(!(bool)Get(press,"StatisticsFilterEnabled")&&!(bool)Get(press,"AutomaticDisplayRecommendation"),"new round respects manual override");
            Set(press,"StatisticsFilterEnabled",true);
            var dashboard=press.GetType().GetField("spectrumDashboardView",All).GetValue(press);
            var dashboardSpectrum=dashboard.GetType().GetField("Review",All).GetValue(dashboard);
            Set(dashboardSpectrum,"ConfidenceClassificationEnabled",true);
            // The preceding explicit manual filter choice may legitimately
            // publish its own local action; ACK it before measuring auto work.
            if((bool)Get(press,"ActionPending")) {
                Check((string)Get(press,"ActionCode")=="STATISTICS_FILTER","only explicit filter choice is pending before empty-data check");
                Check((bool)Call(press,"CompleteAction",(long)Get(press,"ActionSequence"),true,""),"test host acknowledges local filter choice");
            }
            long emptySequence=(long)Get(press,"ActionSequence");
            Set(press,"AutomaticDisplayRecommendation",true);Call(press,"SetSpectrumDataMatrix",new double[0,36]);
            var emptyWait=Stopwatch.StartNew();while(emptyWait.ElapsedMilliseconds<350){Application.DoEvents();Thread.Sleep(5);}
            string emptyAdvice=(string)Get(press,"DisplayRecommendationJson");
            Check(emptyAdvice.Contains("\"Advice\":null")&&!emptyAdvice.Contains("MixedStable")&&!emptyAdvice.Contains("WouldHideAll"),"clearing data cancels queued recommendation instead of recommending from empty data");
            Check((bool)Get(press,"StatisticsFilterEnabled")&&(bool)Get(dashboardSpectrum,"ConfidenceClassificationEnabled"),"empty data preserves both filter and classification choices");
            Check(((int[])Get(press,"StatisticsFilteredSourceRows")).Length==0&&!(bool)Get(press,"ActionPending")&&(long)Get(press,"ActionSequence")==emptySequence,"empty advice has no rows or hardware mailbox action");
            var bar=Children(press).Single(c=>c.Name=="PressureStatisticsFilterBar");foreach(Control c in bar.Controls)Check(bar.ClientRectangle.Contains(c.Bounds),"advice controls contained: "+c.Name);
            form.Close();
        }
        Check(matrix.Cast<double>().Zip(original.Cast<double>(),(a,b)=>a.Equals(b)).All(v=>v),"no input or acceptance mutations");
        // Embedded hosts may run without an ambient synchronization context.
        // A completed background recommendation must still touch UI only on
        // the owning thread, and disposal must invalidate a queued completion.
        Type workerType=analysis.GetType("HVX.PdReview.ReviewRecommendationWorker",true);
        var helper=typeof(AutomaticDisplayRecommendationSmoke).GetMethod("WorkerThreadContract",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(policy);
        helper.Invoke(null,new object[]{workerType,recommended});
        Console.WriteLine("AUTOMATIC_DISPLAY_RECOMMENDATION_PASS="+n+"; HARDWARE=False; USER_PROJECTS=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
    static void WorkerThreadContract<T>(Type workerType,object result)
    {
        using(var owner=new Control())using(var gate=new ManualResetEventSlim(false)){
            owner.CreateControl();int uiThread=Thread.CurrentThread.ManagedThreadId,appliedThread=0;
            Func<Func<T>> capture=()=>()=>{gate.Wait();return (T)result;};
            Action<T> apply=value=>appliedThread=Thread.CurrentThread.ManagedThreadId;
            using(var worker=(IDisposable)Activator.CreateInstance(workerType,new object[]{owner,capture,apply})){
                var previous=SynchronizationContext.Current;
                try{SynchronizationContext.SetSynchronizationContext(null);Call(worker,"Request");Call(worker,"Start");}
                finally{SynchronizationContext.SetSynchronizationContext(previous);}
                gate.Set();Pump(()=>appliedThread!=0);Check(appliedThread==uiThread,"worker completion marshals to owner without synchronization context");
                gate.Reset();appliedThread=0;Call(worker,"Request");Call(worker,"Start");worker.Dispose();gate.Set();
                var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<200){Application.DoEvents();Thread.Sleep(2);}
                Check(appliedThread==0,"disposed worker cannot apply queued completion");
            }
        }
    }
}
