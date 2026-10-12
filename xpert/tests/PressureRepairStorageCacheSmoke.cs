using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureRepairStorageCacheSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks,calls;static Type resultType;static Action duringAnalysis;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static object Result(object request){calls++;if(duringAnalysis!=null)duringAnalysis();var r=Activator.CreateInstance(resultType,true);resultType.GetField("Success",Hidden).SetValue(r,true);var m=new double[1,36];m[0,15]=42;resultType.GetField("Parameters",Hidden).SetValue(r,m);return r;}
    [STAThread]static int Main(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);
            var pressure=Assembly.LoadFrom(Path.Combine(root,"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
            var encode=pressure.GetType("HVX.Controls.PressureTestControl").GetMethod("EncodeLocationCharge",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(double value in new[]{0d,-0d,.000005,-.000005,1218.812345678,-13982.779784123,1e8}){
                string text=(string)encode.Invoke(null,new object[]{value});long integer;
                Check(long.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out integer),"integer encoding");
                Check(Math.Abs(integer/100000d-value)<=.0000051,"quantization bound");
            }
            foreach(double value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,double.MaxValue,1e20}){
                bool failed=false;try{encode.Invoke(null,new object[]{value});}catch(TargetInvocationException){failed=true;}Check(failed,"invalid values rejected");
            }
            var qType=pressure.GetType("Xiongxin.ZeroBench.LocationRangeResult");
            var cType=pressure.GetType("Xiongxin.ZeroBench.RangeDecisionContext");
            var dialogType=pressure.GetType("Xiongxin.ZeroBench.PressureRangeDecisionDialog");
            var details=dialogType.GetMethod("BuildDetails",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(string state in new[]{"small","large","invalid","ambiguous","unchanged","clipped"}){
                var q=Activator.CreateInstance(qType);int current=4,next=state=="small"?5:state=="large"?3:4;
                qType.GetField("Change").SetValue(q,next!=current);qType.GetField("RecommendedIndex").SetValue(q,next);
                qType.GetField("WindowValid").SetValue(q,state!="invalid");qType.GetField("DriverOverflowUncertain").SetValue(q,state=="ambiguous");
                qType.GetField("LocationClippedSamples").SetValue(q,state=="clipped"?1:0);
                var context=Activator.CreateInstance(cType,new object[]{q,current,next,6000d,0,false,true,false});
                string text=(string)details.Invoke(null,new[]{context,(object)"SENSITIVE_DIAGNOSTIC"});
                Check(text==(state=="small"||state=="clipped"?"量程过小":state=="large"?"量程过大":state=="unchanged"?"当前量程无需调整":"量程暂无法判断，请检查采集数据。"),"simple range verdict "+state);
                using(var dialog=(Form)Activator.CreateInstance(dialogType,Hidden,null,new object[]{context,null,true},CultureInfo.InvariantCulture)){
                    if(state=="invalid"){
                        var retry=dialog.Controls.Find("RangeDecisionActions",true)[0].Controls.OfType<Button>().First();
                        Check(!retry.Enabled,"invalid window still blocks retry");
                    }
                }
            }
            var review=Assembly.LoadFrom(Path.Combine(root,"modules/DataAnalysis/HVX.PdReview.V1.0.dll"));
            var requestType=review.GetType("HVX.PdReview.ReviewAnalysisRequest");resultType=review.GetType("HVX.PdReview.ReviewAnalysisResult");
            var cache=review.GetType("HVX.PdReview.PressureAnalysisCache");var key=cache.GetMethod("Key",BindingFlags.Static|BindingFlags.NonPublic);var run=cache.GetMethod("Analyze",BindingFlags.Static|BindingFlags.NonPublic);
            string fixture=Path.Combine(root,"qa-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string loc=Path.Combine(fixture,"A_10kV_test.loc");File.WriteAllText(loc,"123456");File.WriteAllText(Path.Combine(fixture,"ITEM.in"),"project");File.WriteAllText(Path.Combine(fixture,"CABLE.in"),"cable");
            var request=Activator.CreateInstance(requestType,true);requestType.GetField("ProjectPath",Hidden).SetValue(request,fixture);requestType.GetField("LocationFilePath",Hidden).SetValue(request,loc);
            string scope=Guid.NewGuid().ToString("N");string original=(string)key.Invoke(null,new[]{(object)scope,request});Check(original!=null,"cache has complete dependencies");
            var p=Expression.Parameter(requestType,"r");var funcType=typeof(Func<,>).MakeGenericType(requestType,resultType);
            var callback=Expression.Lambda(funcType,Expression.Convert(Expression.Call(typeof(PressureRepairStorageCacheSmoke).GetMethod("Result",BindingFlags.Static|BindingFlags.NonPublic),Expression.Convert(p,typeof(object))),resultType),p).Compile();
            Func<string,double[,]> analyze=s=>{object[] a={s,request,callback,false};var m=(double[,])run.Invoke(null,a);return m;};
            var first=analyze(scope);first[0,15]=999;var second=analyze(scope);Check(calls==1&&second[0,15]==42,"cache clone and hit");
            second[0,15]=888;Check(analyze(scope)[0,15]==42,"caller cannot mutate cache");
            string pending=Path.ChangeExtension(loc,".record.pending");File.WriteAllText(pending,"partial pair fixture");
            int unchanged=calls;bool pendingBlocked=false;
            try{analyze(scope);}catch(TargetInvocationException e){pendingBlocked=e.InnerException is InvalidDataException;}
            Check(pendingBlocked&&calls==unchanged,"pending publication cannot use cache hit or invoke analysis");
            File.Delete(pending);
            foreach(var field in requestType.GetFields(Hidden)){
                if(field.Name=="ProjectPath"||field.Name=="LocationFilePath"||field.Name=="AHistoryBatch")continue;
                object old=field.GetValue(request),changed=null;
                if(field.FieldType==typeof(string))changed=(string)old+"x";
                else if(field.FieldType==typeof(double))changed=(double)old+.125;
                else if(field.FieldType==typeof(int))changed=(int)old+1;
                else if(field.FieldType==typeof(long))changed=(long)old+1;
                else if(field.FieldType==typeof(bool))changed=!(bool)old;
                else if(field.FieldType==typeof(bool[])){var a=(bool[])((bool[])old).Clone();a[0]=!a[0];changed=a;}
                else if(field.FieldType==typeof(double[])){var a=(double[])((double[])old).Clone();a[0]++;changed=a;}
                else throw new Exception("test missing option "+field.Name);
                field.SetValue(request,changed);Check((string)key.Invoke(null,new[]{(object)scope,request})!=original,"invalidates option "+field.Name);field.SetValue(request,old);
            }
            DateTime stamp=File.GetLastWriteTimeUtc(loc);File.WriteAllText(loc,"123457");File.SetLastWriteTimeUtc(loc,stamp);analyze(scope);Check(calls==2,"same size/date changed body invalidates");
            File.WriteAllText(Path.Combine(fixture,"CABLE.in"),"cable2");analyze(scope);Check(calls==3,"geometry invalidates");
            analyze(scope+"new");Check(calls==4,"trial isolation");analyze(null);analyze(null);Check(calls==6,"no scope no caching");
            duringAnalysis=()=>File.WriteAllText(Path.Combine(fixture,"ITEM.in"),Guid.NewGuid().ToString());analyze("unstable");analyze("unstable");Check(calls==8,"changing metadata not cached");duringAnalysis=null;
            analyze("dem-dependency");int before=calls;
            string dem=Path.ChangeExtension(loc,".dem");File.WriteAllText(dem,"Hz\t400");
            analyze("dem-dependency");Check(calls==before+1,"adding companion DEM invalidates cached analysis");
            stamp=File.GetLastWriteTimeUtc(dem);File.WriteAllText(dem,"Hz\t401");File.SetLastWriteTimeUtc(dem,stamp);
            analyze("dem-dependency");Check(calls==before+2,"same-size DEM frequency change invalidates analysis");
            File.Delete(dem);analyze("dem-dependency");Check(calls==before+2,"removed DEM selects the earlier exact absent-DEM input, not stale present-DEM result");
            // Same production reader consumes encoded LOC values and both voltage aliases.
            var reader=review.GetType("HVX.PdReview.ReviewLocationFileReader").GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(string header in new[]{"Target peak kV","Voltage (kV)"}){
                var lines=new[]{"LOC","Date\t2026-10-08","Phase\t0",header+"\t6.152","Cable-length\t171.74","Velocity (m/us)\t175.4","Delta x [s]\t0.000000008","Charge unit\tpC x 100000","Reserved","Reserved","Reserved"}.Concat(Enumerable.Repeat((string)encode.Invoke(null,new object[]{12.345678}),16)).ToArray();
                File.WriteAllLines(loc,lines);var data=reader.Invoke(null,new object[]{loc,fixture});
                Check((double)data.GetType().GetField("VoltageKv",Hidden).GetValue(data)==6.152,"voltage header "+header);
                Check(Math.Abs(((double[])data.GetType().GetField("ChargeSamples",Hidden).GetValue(data))[0]-12.345678)<.0000051,"reader charge round trip");
                File.WriteAllText(pending,"interrupted pair");pendingBlocked=false;
                try{reader.Invoke(null,new object[]{loc,fixture});}catch(TargetInvocationException e){pendingBlocked=e.InnerException is InvalidDataException;}
                Check(pendingBlocked,"direct LOC reader rejects interrupted pair "+header);File.Delete(pending);
            }
            Console.WriteLine("PASS "+checks+" checks; no hardware; fixtures: "+fixture);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
