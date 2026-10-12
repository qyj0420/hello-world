using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Reflection-only synthetic extracted events. No SDK, worker or product Main.
internal static class PressureCalibrationChargeReviewSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly module;static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name,F).GetValue(owner,null);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(owner,args);}
    static object Evaluate(double[,] matrix,int range=5,double phase=1,double fileId=42)
    {return module.GetType("HVX.PdReview.PressureCalibrationChargeReviewPolicy",true).GetMethod("Evaluate",F).Invoke(null,new object[]{matrix,phase,fileId,range});}
    static void Throws(Action action,Type expected,string why)
    {try{action();Check(false,why);}catch(TargetInvocationException error){Check(expected.IsInstanceOfType(error.InnerException),why);}}
    static double[,] Matrix(int count)
    {
        var matrix=new double[count,36];
        for(int row=0;row<count;row++){
            matrix[row,0]=1;matrix[row,1]=42;matrix[row,2]=0;matrix[row,7]=125e6;
            matrix[row,9]=row;matrix[row,10]=100+row*100;matrix[row,15]=200001;
            matrix[row,31]=0;matrix[row,32]=1000;matrix[row,35]=1;
        }
        return matrix;
    }
    static int Count(double[,] matrix,int range=5){return (int)Get(Evaluate(matrix,range),"IndependentPulseCount");}
    static void Invalid(int column,double value,string why)
    {var matrix=Matrix(3);matrix[2,column]=value;Check(Count(matrix)==2,why);}

    [STAThread]static int Main(string[] args)
    {
        try{
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var policy=module.GetType("HVX.PdReview.PressureCalibrationChargeReviewPolicy",true);
            Check(policy.GetMethod("Evaluate",F)!=null,"pure review interface exists");
            var result=Evaluate(Matrix(2));Check((int)Get(result,"IndependentPulseCount")==2&&!(bool)Get(result,"ShouldRemind"),"two pulses do not trigger");
            result=Evaluate(Matrix(3));Check((int)Get(result,"IndependentPulseCount")==3&&(bool)Get(result,"ShouldRemind"),"three independent pulses trigger");
            Check((string)Get(result,"Schema")=="HVX.Pressure.CalibrationChargeReview/V1"&&(double)Get(result,"ThresholdPc")==200000&&(int)Get(result,"RequiredPulseCount")==3,"explicit pC threshold and schema");
            result=Evaluate(Matrix(3),10);Check((bool)Get(result,"AtMaximumRange")&&!(bool)Get(result,"ShouldRemind")&&(int)Get(result,"IndependentPulseCount")==3,"maximum 20 V range suppresses reminder without changing evidence");
            for(int range=0;range<10;range++)Check((bool)Get(Evaluate(Matrix(3),range),"ShouldRemind"),"nonmaximum range "+range);
            var duplicated=Matrix(6);for(int row=3;row<6;row++){duplicated[row,10]=duplicated[row-3,10];duplicated[row,9]=900+row;}
            Check(Count(duplicated)==3,"duplicate original incident IDs do not count as independent pulses");
            for(int row=0;row<6;row++)duplicated[row,10]=100;
            Check(Count(duplicated)==1,"six cumulative MAP IDs do not create six physical incidents");
            Invalid(0,2,"same file ID in another phase is excluded");Invalid(1,43,"another saved record is excluded");
            Invalid(15,200000,"exactly 200 nC is not over 200 nC");Invalid(15,199999,"below threshold is excluded");
            Invalid(15,-300000,"signed negative value cannot satisfy positive threshold");Invalid(15,Double.NaN,"NaN charge cannot count");Invalid(15,Double.PositiveInfinity,"infinite charge cannot count");
            Invalid(7,0,"missing sample rate cannot count");Invalid(7,-1,"negative sample rate cannot count");Invalid(7,Double.NaN,"invalid sample rate cannot count");
            Invalid(10,-1,"negative incident cannot count");Invalid(10,100.5,"fractional incident cannot count");Invalid(10,Double.NaN,"NaN incident cannot count");Invalid(10,Double.PositiveInfinity,"infinite incident cannot count");Invalid(10,1001,"incident beyond valid waveform cannot count");
            Invalid(31,301,"incident before window cannot count");Invalid(31,-1,"negative window start cannot count");Invalid(31,1.5,"fractional window start cannot count");Invalid(32,299,"incident after window cannot count");Invalid(32,Double.NaN,"invalid window cannot count");Invalid(32,(double)Int32.MaxValue+1,"unrepresentable window cannot count");
            Invalid(35,Double.NaN,"corrupt classification cannot supply evidence");
            var pending=Matrix(3);for(int row=0;row<3;row++)pending[row,35]=0;
            Check(Count(pending)==3,"pending classification is not confused with invalid measurement");
            Check(Count(Matrix(0))==0,"no extracted pulse means no reminder");
            var unchanged=Matrix(6);unchanged[0,15]=Double.NaN;var before=(double[,])unchanged.Clone();
            int once=Count(unchanged),twice=Count(unchanged);
            Check(once==twice&&once==5,"repeated review does not accumulate event count");
            Check(unchanged.Cast<double>().SequenceEqual(before.Cast<double>()),"review is read only including NaN");
            Throws(()=>Evaluate(null),typeof(ArgumentException),"missing matrix rejected");Throws(()=>Evaluate(new double[3,35]),typeof(ArgumentException),"invalid matrix schema rejected");
            Throws(()=>Evaluate(Matrix(3),-1),typeof(ArgumentOutOfRangeException),"unknown range rejected");Throws(()=>Evaluate(Matrix(3),11),typeof(ArgumentOutOfRangeException),"range outside contract rejected");
            Throws(()=>Evaluate(Matrix(3),5,0),typeof(ArgumentOutOfRangeException),"invalid phase rejected");Throws(()=>Evaluate(Matrix(3),5,1,Double.NaN),typeof(ArgumentOutOfRangeException),"invalid file identity rejected");

            module.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            Application.EnableVisualStyles();using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.PdReview.PdReviewControl",true))){
                var handle=control.Handle;var tree=Get(control,"AnalysisTree");
                string current=Path.Combine(qa,"A_current.loc"),other=Path.Combine(qa,"A_previous.loc"),phaseB=Path.Combine(qa,"B_current.loc");
                Check((bool)Call(tree,"LoadRecords",new[]{other,phaseB,current},new[]{1,2,1},new[]{1d,1d,1d},new[]{1,1,2},new[]{43d,42d,42d}),"synthetic record catalog loaded");
                var matrix=Matrix(5);matrix[3,1]=43;matrix[4,0]=2;
                var snapshot=(double[,])matrix.Clone();result=Call(control,"GetPressureCalibrationChargeReview",matrix,current.ToUpperInvariant(),5);
                Check((int)Get(result,"IndependentPulseCount")==3&&(bool)Get(result,"ShouldRemind"),"host resolves case-insensitive saved record and current-file-only events");
                Check((double)Get(result,"Phase")==1&&(double)Get(result,"FileId")==42,"catalog identity returned");
                Check(matrix.Cast<double>().SequenceEqual(snapshot.Cast<double>()),"host API preserves cumulative result");
                result=Call(control,"GetPressureCalibrationChargeReview",matrix,phaseB,5);
                Check((int)Get(result,"IndependentPulseCount")==1&&!(bool)Get(result,"ShouldRemind"),"phase-local duplicate file ID is not conflated");
                result=Call(control,"GetPressureCalibrationChargeReview",matrix,current,10);Check(!(bool)Get(result,"ShouldRemind"),"host maximum actual range is exempt");
                Throws(()=>Call(control,"GetPressureCalibrationChargeReview",matrix,Path.Combine(qa,"missing.loc"),5),typeof(InvalidDataException),"missing current-round catalog record is not guessed");
                Throws(()=>Call(control,"GetPressureCalibrationChargeReview",matrix,"",5),typeof(ArgumentException),"empty current record is rejected");
            }
            Console.WriteLine("PRESSURE_CALIBRATION_CHARGE_REVIEW_PASS="+checks+"; HardwareAccessed=false; Input=synthetic; ProductMainStarted=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
