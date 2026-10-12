using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class AnalysisPositionSettingsSmoke
{
    static int checks;
    static void Check(bool value,string text){++checks;if(!value)throw new InvalidOperationException(text);}
    static object Get(object value,string name){return value.GetType().GetProperty(name).GetValue(value,null);}
    static void Set(object value,string name,object data){value.GetType().GetProperty(name).SetValue(value,data,null);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Invoke(value,args);}
    static IEnumerable<Control> All(Control value){foreach(Control child in value.Controls){yield return child;foreach(var item in All(child))yield return item;}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string dll=Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll");if(!File.Exists(dll))dll=Path.Combine(root,"HVX.PdReview.V1.0.dll");
            var assembly=Assembly.LoadFrom(dll);
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(output,"analysis-settings-test.json")));
            using(var form=new Form{ClientSize=new Size(1256,740),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var review=(Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                form.Controls.Add(review);review.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                object pdmap=Get(review,"PdMap"),spectrum=Get(review,"Spectrum");Set(spectrum,"AutomaticConclusions",false);
                Check((bool)Get(pdmap,"ApproximateCablePositions")&&(bool)Get(pdmap,"ShowCalibrationCurve")&&(bool)Get(pdmap,"ShowDampingCurve"),"three requested defaults enabled");
                Call(pdmap,"SetConnectors",new double[]{200});
                var matrix=new double[2,36];var wave=new double[2,2048];
                for(int r=0;r<2;r++)
                {
                    matrix[r,0]=1;matrix[r,1]=r+1;matrix[r,2]=13.4;matrix[r,3]=45;matrix[r,5]=414;matrix[r,6]=170;matrix[r,7]=125000000;matrix[r,9]=r;
                    matrix[r,10]=100;matrix[r,11]=500;matrix[r,12]=400;matrix[r,13]=r==0?300:201;matrix[r,14]=609;matrix[r,15]=180;matrix[r,16]=180;
                    matrix[r,23]=100;matrix[r,24]=100000;matrix[r,26]=0;matrix[r,32]=2048;matrix[r,33]=.9;matrix[r,35]=1;
                    for(int k=0;k<2048;k++)wave[r,k]=Math.Exp(-Math.Pow((k-100)/8.0,2))+.3*Math.Exp(-Math.Pow((k-500)/10.0,2));
                }
                Check((bool)Call(review,"LoadDataMatrices",matrix,wave),"synthetic pair rows load");
                var source=(double[,])Get(review,"ResultParameterMatrix");var effective=(double[,])Get(review,"EffectiveResultParameterMatrix");
                Check(source[1,13]==201&&effective[1,13]==200,"effective accessory coordinate without overwriting raw row");
                Check(((double[,])Get(review,"OriginalParameterMatrix"))[1,13]==201,"immutable original position retained");
                var input=(double[,])Call(spectrum,"ConclusionInput",7);Check(input[1,13]==201,"automatic chart conclusion uses raw evidence, not accessory display projection");
                var evalArgs=new object[]{null,null,0,0};var eval=(double[,])Call(review,"CreateEvaluationInput",evalArgs);
                Check(eval.GetLength(0)==2&&eval[1,13]==201&&eval[1,10]==100&&eval[1,11]==500,"manual conclusion uses same raw position and timing as automatic evidence");
                var points=(double[,])Call(spectrum,"GetDisplayedPoints");Check(Enumerable.Range(0,points.GetLength(0)).Any(i=>points[i,0]==200),"statistical plot retains requested accessory projection independently of evidence");
                Set(review,"ReversePositionDisplay",true);input=(double[,])Call(spectrum,"ConclusionInput",7);Check(input[1,13]==201,"display reversal does not reverse canonical conclusion evidence");
                points=(double[,])Call(spectrum,"GetDisplayedPoints");Check(Enumerable.Range(0,points.GetLength(0)).Any(i=>points[i,0]==214),"reversed chart still shows the projected position from the selected origin");
                Check(((double[,])Get(review,"EffectiveResultParameterMatrix"))[1,13]==200,"canonical output is independent of display reversal");
                Set(review,"ReversePositionDisplay",false);
                double clusterTolerance=(double)Get(pdmap,"PositionTolerancePercent");
                Set(review,"JointApproximationErrorPercent",0.1d);
                Check((double)Get(pdmap,"PositionTolerancePercent")==clusterTolerance,"joint tolerance does not change same-location tolerance");
                Check(((double[,])Get(review,"EffectiveResultParameterMatrix"))[1,13]==201,"outside narrow joint band stays at measured position");
                Set(review,"JointApproximationErrorPercent",1d);
                Check(((double[,])Get(review,"EffectiveResultParameterMatrix"))[1,13]==200,"restoring joint band reprojects display only");
                Check(((double[,])Call(spectrum,"ConclusionInput",7))[1,13]==201&&((double[,])Get(review,"OriginalParameterMatrix"))[1,13]==201&&matrix[1,13]==201,"tolerance/reversal changes never mutate raw input or original matrix");
                Set(review,"AiLearningMode",true);Check(!(bool)Get(pdmap,"ApproximateCablePositions"),"learning disables automatic joint snap");
                Set(pdmap,"ApproximateCablePositions",true);Check(!(bool)Get(pdmap,"ApproximateCablePositions"),"cannot force auto snap while learning");
                Check(((double[,])Get(review,"EffectiveResultParameterMatrix"))[1,13]==201,"learning retains manually selected raw position");
                Set(review,"AiLearningMode",false);Check((bool)Get(pdmap,"ApproximateCablePositions"),"normal snap choice restored after learning");
                int events=0;EventHandler handler=(s,e)=>events++;review.GetType().GetEvent("AnalysisSettingsChanged").AddEventHandler(review,handler);
                int protectedAlgorithm=(int)Get(review,"AnalysisAlgorithmIndex");
                Check(protectedAlgorithm==5||protectedAlgorithm==7,"configured location choice uses an active A/B method");
                Call(review,"SetAnalysisSettings",15d,30d,3d,30,false,new[]{true,true,true,true});Check(events==0,"programmatic configuration does not emit persistence event");
                RunDialog(review,false,19m);Check(events==0&&(double)Get(review,"AnalysisAttenuationTolerancePercent")==15,"cancel does not commit or notify");
                RunDialog(review,true,19m);Check(events==1&&(double)Get(review,"AnalysisAttenuationTolerancePercent")==19,"OK commits and notifies once");
                Check(File.Exists(Path.Combine(output,"analysis-settings-test.json")),"standalone preference save retained");
                Check((int)Get(review,"AnalysisAlgorithmIndex")==protectedAlgorithm,"protected algorithm unchanged without unlock");
                Check((bool)Call(review,"ClearAllAnalysis"),"clear analysis succeeds");
                Check((double)Get(review,"AnalysisAttenuationTolerancePercent")==19&&events==1,"clear data retains saved settings and does not emit settings edit");
                form.Close();
            }
            Console.WriteLine("ANALYSIS_POSITION_SETTINGS_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void RunDialog(Control review,bool apply,decimal draft)
    {
        Exception failure=null;
        using(var timer=new Timer{Interval=50})
        {
            timer.Tick+=(s,e)=>
            {
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="ReviewAnalysisSettingsDialog");if(dialog==null)return;
                timer.Stop();try{All(dialog).OfType<NumericUpDown>().Single(c=>c.Name=="AnalysisAttenuationTolerance").Value=draft;
                    ((Button)All(dialog).Single(c=>c.Name==(apply?"ApplyAnalysisSettings":"CancelAnalysisSettings"))).PerformClick();}
                catch(Exception ex){failure=ex;dialog.DialogResult=DialogResult.Cancel;}
            };
            timer.Start();bool result=(bool)Call(review,"ShowAnalysisSettingsDialog");if(failure!=null)throw failure;Check(result==apply,"dialog confirmation result");
        }
    }
}
