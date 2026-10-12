using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class ReportFinalInputSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
    static void Check(bool v,string label){checks++;if(!v)throw new Exception(label);}
    static object P(object o,string name){return o.GetType().GetProperty(name,B).GetValue(o,null);}
    static void S(object o,string name,object v){o.GetType().GetProperty(name,B).SetValue(o,v,null);}
    static object F(object o,string name){return o.GetType().GetField(name,B).GetValue(o);}
    static void SF(object o,string name,object v){o.GetType().GetField(name,B).SetValue(o,v);}
    static object Call(object o,string name,params object[] args){var t=o as Type??o.GetType();return t.GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static string Hash(string path){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
    static void Write(string path,object value){File.WriteAllText(path,json.Serialize(value),new UTF8Encoding(false));}
    static double[,] Matrix()
    {
        var m=new double[8,36];for(int r=0;r<8;r++)
        {
            m[r,0]=r==6?2:1;m[r,1]=r+1;m[r,2]=9+r;m[r,3]=r;m[r,5]=200;m[r,6]=170;m[r,7]=250;
            m[r,9]=r;m[r,10]=10;m[r,11]=20;m[r,12]=10;m[r,13]=79.5+r*.1;
            m[r,15]=100+r*20;m[r,16]=m[r,15];m[r,22]=.8;m[r,23]=.1;m[r,24]=.9;m[r,26]=5;m[r,33]=.9;m[r,35]=1;
        }
        m[5,13]=80.45;m[6,13]=120.4;m[7,35]=0;return m;
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            string qa=Path.GetFullPath(args[0]),test=Path.Combine(qa,"fixture-"+Guid.NewGuid().ToString("N")),root=Path.Combine(test,"project"),archive=Path.Combine(root,"Analysis report","final");Directory.CreateDirectory(archive);
            var analysis=Assembly.LoadFrom(Path.Combine(qa,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var report=Assembly.LoadFrom(Path.Combine(qa,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            var matrix=Matrix();var raw=(double[,])matrix.Clone();string context,offContext,mask,matrixHash;
            using(var review=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                SF(review,"working",matrix);SF(review,"original",(double[,])matrix.Clone());
                SF(review,"reviewMask",new[]{true,true,true,true,true,true,false,true});SF(review,"removed",new bool[8]);
                SF(review,"exactManualPositionRows",new[]{false,false,false,false,false,true,false,false});
                var pdmap=P(review,"PdMap");Call(pdmap,"SetConnectors",new double[]{80,120});
                var spectrum=P(review,"Spectrum");S(spectrum,"OperatingVoltageKv",8.7d);
                context=(string)Call(review,"CreateReportInputContext",matrix);
                S(pdmap,"ApproximateCablePositions",false);offContext=(string)Call(review,"CreateReportInputContext",matrix);S(pdmap,"ApproximateCablePositions",true);
                mask=(string)Call(review,"CreateEffectiveSelectionMask");matrixHash=(string)Call(review,"EffectiveSelectionHash",matrix);
                var ctx=json.Deserialize<Dictionary<string,object>>(context);
                Check(context.Contains("HVX.ReportInput.1"),"new context serialized");
                Check((int)P(review,"EvaluationAnalysisCount")==0,"context export does not run conclusion engine");
                Check(!(bool)P(spectrum,"ShowConclusionAnnotations"),"analysis plot conclusion markers hidden");
                Check(!(bool)Call(review,"RequestEvaluation"),"analysis-stage conclusion request rejected");
                Check((string)P(review,"EvaluationStatusCode")=="ReportOnly","report-only status explicit");
                for(int i=0;i<20;i++){Call(review,"QueueAutomaticEvaluation");Call(review,"PublishEvaluationMarkers");}
                Check(!(bool)P(review,"AutomaticEvaluationPending")&&(int)P(review,"EvaluationAnalysisCount")==0,"repeated refresh cannot enqueue computation");
                Check(!(bool)P(spectrum,"ShowConclusionAnnotations"),"repeated publication remains hidden");
                Call(review,"ShowEvaluationResult");Check(!(bool)((Control)F(review,"evaluationPopup")).Visible,"analysis conclusion popup stays hidden");
                Check(matrix.Cast<double>().SequenceEqual(raw.Cast<double>()),"snapshot preserves original matrix values");
                matrix[1,5]=201;string incompatible=(string)Call(review,"CreateReportInputContext",matrix);
                Check(incompatible.Contains("\"Available\":false"),"inconsistent historic geometry does not prevent saving original analysis");matrix[1,5]=200;
            }
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"电缆长度 = 200\r\n相电压 = 8.7\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0\r\n1\t接头1\t冷缩\t80\r\n1\t终端\t冷缩\t200\r\n",Encoding.UTF8);
            string parameters=Path.Combine(archive,"parameters.bin"),session=Path.Combine(archive,"session.json"),manifestPath=Path.Combine(archive,"analysis.json");
            using(var w=new BinaryWriter(File.Create(parameters))){w.Write(Encoding.ASCII.GetBytes("HVXMAT01"));w.Write(8);w.Write(36);foreach(double x in matrix)w.Write(x);}
            var settings=new Dictionary<string,string>{{"EffectiveSelectionVersion","1"},{"EffectiveSelectionMask",mask},{"EffectiveSelectionMatrixSha256",matrixHash},{"LocationFilterEnabled","0"},{"LocationFilterStrength","40"},{"ReportInputContext",context}};
            Action save=()=>{
                Write(session,new{schema="HVX.AnalysisSession.Utf8",version=1,settings=settings.Select(k=>new[]{k.Key,k.Value}).ToArray()});
                Write(manifestPath,new{Schema="HVX.NamedAnalysis",Version=1,Id="final",Name="final",CreatedUtc=DateTime.UtcNow.ToString("o"),SavedUtc=DateTime.UtcNow.ToString("o"),Charts=new object[0],Files=new[]{parameters,session}.Select(p=>new{Path=Path.GetFileName(p),Sha256=Hash(p),Bytes=new FileInfo(p).Length}).ToArray()});};
            var reader=report.GetType("HVX.ReportGenerator.AnalysisPackageReader",true);save();object package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasEffectiveSelectionSnapshot"),"file-bound effective selection validates");
            Check((bool)P(package,"HasReportInputSnapshot"),"saved report input validates: "+P(package,"ReportInputStatus"));
            Check(!(bool)P(package,"HasSavedConclusionSnapshot"),"report input does not masquerade as precomputed conclusion");
            var statistics=(double[,])P(package,"StatisticsMatrix");var source=(double[,])P(package,"LocationMatrix");var input=(double[,])F(package,"ConclusionInput");
            Check(statistics.GetLength(0)==7&&input.GetLength(0)==6,"stats and accepted conclusion scopes are distinct");
            Check(source[0,13]==79.5&&statistics[0,13]==80&&input[0,13]==80,"plot and conclusion both use snapped coordinate with raw retained");
            Check(statistics[5,13]==80.45&&input[5,13]==80.45,"exact manual selection preserved without joint snapping");
            Check(input.Cast<double>().Count(Double.IsNaN)==6,"probability field not forged from amplitude ratio");
            Check((bool)P(package,"ReportJointApproximationEnabled")&&(double)P(package,"ReportJointTolerancePercent")==1,"switch and actual tolerance inherited");
            var joints=(double[][])F(package,"ConclusionJoints");Check(joints[0].SequenceEqual(new[]{0d,80d,120d,200d})&&joints[1].Length==0,"phase geometry is saved rather than borrowed");
            string fileHash=Hash(parameters),sessionHash=Hash(session),manifestHash=Hash(manifestPath);
            using(var control=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl",true)))
            {
                S(control,"PromptAnalysisOnLoad",false);
                Check((bool)Call(control,"LoadAnalysis",archive),"report loads without analysis-stage conclusion: "+P(control,"LastError"));
                Check(((string)P(control,"ConclusionText")).Contains("请点击"),"loading report does not calculate a conclusion");
                var button=(Button)F(control,"aiButton");typeof(Button).GetMethod("OnClick",B).Invoke(button,new object[]{EventArgs.Empty});
                object[] invocation={null,null,0d};Check((bool)CallWithArgs(control,"TryGetConclusionInput",invocation),"report obtains final context");
                Check(((double[,])invocation[0])[0,13]==80,"actual report generation receives projected coordinate");
                string text=(string)P(control,"ConclusionText");Check(!String.IsNullOrWhiteSpace(text)&&!text.Contains("缺少可验证")&&!text.Contains("请点击")&&!text.Contains("尚未生成结论"),"explicit report conclusion succeeds without old snapshot");
                Check(text.Contains("接头"),"actual report conclusion uses inherited joint component");
            }
            Check(Hash(parameters)==fileHash&&Hash(session)==sessionHash&&Hash(manifestPath)==manifestHash,"report is read-only to saved inputs");
            settings["ReportInputContext"]=offContext;save();package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasReportInputSnapshot")&&!(bool)P(package,"ReportJointApproximationEnabled"),"disabled approximation choice is preserved");
            Check(((double[,])P(package,"StatisticsMatrix"))[0,13]==79.5&&((double[,])F(package,"ConclusionInput"))[0,13]==79.5,"disabled approximation keeps raw positions in both consumers");
            var partial=json.Deserialize<Dictionary<string,object>>(context);partial["SelectedRecordCount"]=2;partial["PendingSelectedRecordCount"]=1;settings["ReportInputContext"]=json.Serialize(partial);save();
            using(var control=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl",true)))
            {
                S(control,"PromptAnalysisOnLoad",false);Check((bool)Call(control,"LoadAnalysis",archive),"partial saved input remains loadable");
                typeof(Button).GetMethod("OnClick",B).Invoke((Button)F(control,"aiButton"),new object[]{EventArgs.Empty});
                Check(((string)P(control,"ConclusionText")).Contains("1 条已选记录尚未分析"),"partial analysis cannot masquerade as complete-project conclusion");
            }
            var rounding=json.Deserialize<Dictionary<string,object>>(context);
            rounding["RawPositionsM"]=new[]{79.5+2.8421709430404007e-14,79.6,79.7,79.8,79.9,80.45,80.2};
            settings["ReportInputContext"]=json.Serialize(rounding);save();package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasReportInputSnapshot"),"JSON sub-ULP-scale position rounding is accepted");
            rounding["RawPositionsM"]=new[]{79.500001,79.6,79.7,79.8,79.9,80.45,80.2};
            settings["ReportInputContext"]=json.Serialize(rounding);save();package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasReportInputSnapshot"),"changed source coordinate remains rejected");
            var invalid=json.Deserialize<Dictionary<string,object>>(context);invalid["PositionsM"]=new[]{180d,80,80,80,80,80,80};settings["ReportInputContext"]=json.Serialize(invalid);save();package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasReportInputSnapshot"),"out-of-band projected position rejected");
            using(var control=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl",true))){S(control,"PromptAnalysisOnLoad",false);Check(!(bool)Call(control,"LoadAnalysis",archive),"bad new context cannot silently fall back to raw positions");}
            invalid=json.Deserialize<Dictionary<string,object>>(context);invalid["ConclusionRows"]=new[]{0,1};settings["ReportInputContext"]=json.Serialize(invalid);save();package=Call(reader,"Read",archive);Check(!(bool)P(package,"HasReportInputSnapshot"),"truncated conclusion input rejected");
            invalid=json.Deserialize<Dictionary<string,object>>(context);invalid["Joints"]=new[]{new[]{0d,80,199},new double[0],new double[0]};settings["ReportInputContext"]=json.Serialize(invalid);save();package=Call(reader,"Read",archive);Check(!(bool)P(package,"HasReportInputSnapshot"),"mismatched phase length rejected");
            settings.Remove("ReportInputContext");save();package=Call(reader,"Read",archive);Check(!(bool)P(package,"HasReportInputSnapshot")&&((string)P(package,"ReportInputStatus")).Contains("Legacy"),"legacy input explicitly unknown, not guessed");
            settings["ReportInputContext"]=context;save();File.AppendAllText(session," ");package=Call(reader,"Read",archive);Check(!(bool)P(package,"HasEffectiveSelectionSnapshot"),"changed session bytes fail source binding before projection");
            Console.WriteLine("REPORT_FINAL_INPUT_PASS="+checks+"; hardware=false; product_main=false; user_project_writes=false");Console.WriteLine("EVIDENCE="+test);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e is TargetInvocationException?((TargetInvocationException)e).InnerException:e);return 1;}
    }
    static object CallWithArgs(object o,string name,object[] args){return o.GetType().GetMethod(name,B).Invoke(o,args);}
}
