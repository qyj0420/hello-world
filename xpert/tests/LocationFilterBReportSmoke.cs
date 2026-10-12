using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Private offline QA. Uses an isolated project and the real named archive/report reader.
internal static class LocationFilterBReportSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    static void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException(name); }
    static object Get(object value, string name)
    { var p=value.GetType().GetProperty(name,All); return p!=null?p.GetValue(value,null):value.GetType().GetField(name,All).GetValue(value); }
    static void Set(object value, string name, object data) { value.GetType().GetProperty(name,All).SetValue(value,data,null); }
    static object Call(object value, string name, params object[] args)
    { var t=value as Type??value.GetType(); return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value is Type?null:value,args); }
    static bool Same(double[,] a, double[,] b)
    { return a.GetLength(0)==b.GetLength(0)&&a.GetLength(1)==b.GetLength(1)&&a.Cast<double>().Zip(b.Cast<double>(),(x,y)=>x.Equals(y)).All(x=>x); }
    static string Hash(string path)
    { using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-",""); }
    static Dictionary<string,string> Settings(string archive)
    {
        var data=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(archive,"session.json")));
        return ((System.Collections.IEnumerable)data["settings"]).Cast<System.Collections.IList>().ToDictionary(row=>(string)row[0],row=>(string)row[1]);
    }
    static string Save(Control review, object reader, string project, string name)
    {
        Check((bool)Call(review,"SaveNamedAnalysis",name),"real named archive saves: "+Get(review,"LastError"));
        string id=(string)Get(review,"LastSavedAnalysisId");
        string[] paths=(string[])Call(reader,"EnumeratePackagePaths",project);
        string path=paths.Single(p=>Path.GetFileName(p)==id);
        Check(File.Exists(Path.Combine(path,"analysis.json")),"reader discovers newly saved archive"); return path;
    }
    static void Verify(object package, double[,] raw, int[] expected)
    {
        Check((bool)Get(package,"HasEffectiveSelectionSnapshot"),"report validates saved effective mask");
        Check((bool)Get(package,"HasReportInputSnapshot"),"report validates saved input context: "+Get(package,"ReportInputStatus"));
        Check(Same((double[,])Get(package,"LocationMatrix"),raw),"report retains six original rows and every original value");
        double[,] selected=(double[,])Get(package,"StatisticsMatrix");
        Check(selected.GetLength(0)==expected.Length,"report statistics exact saved cardinality");
        for(int r=0;r<expected.Length;r++)for(int c=0;c<raw.GetLength(1);c++)
            Check(selected[r,c].Equals(raw[expected[r],c]),"report row/value matches saved mask: "+r+","+c);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string host=Path.GetFullPath(args[0]), qa=Path.Combine(Path.GetFullPath(args[1]),"b-report-"+Guid.NewGuid().ToString("N").Substring(0,8));
            string project=Path.Combine(qa,"project"); Directory.CreateDirectory(project);
            var analysis=Assembly.LoadFrom(Path.Combine(host,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var report=Assembly.LoadFrom(Path.Combine(host,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            var readerType=report.GetType("HVX.ReportGenerator.AnalysisPackageReader",true);
            object reader=Activator.CreateInstance(readerType);
            analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"1000\"\r\n相电压: = \"8.7\"\r\n",new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t1000m\r\n",new UTF8Encoding(false));
            string loc=Path.Combine(project,"A_13.40kV_1.loc"); File.WriteAllBytes(loc,new byte[256]);
            using(var review=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false); Set(review,"LanguageIndex",0); Set(review,"ProjectPath",project);
                Set(review,"AutomaticDisplayRecommendation",false); Set(Get(review,"PdMap"),"ApproximateCablePositions",false);
                double[,] catalog=(double[,])Call(Get(review,"AnalysisTree"),"GetDataCatalog");
                Check(catalog.GetLength(0)==1&&catalog[0,0]==1&&catalog[0,2]==13.4,"actual isolated LOC catalog identifies phase and voltage");
                double[] x={500,500.1,501,800,997,1000}, q={200,5,20,40,200,200};
                double[,] matrix=new double[6,36],waves=new double[6,4096];
                for(int r=0;r<6;r++)
                {
                    matrix[r,0]=catalog[0,0]; matrix[r,1]=catalog[0,1]; matrix[r,2]=catalog[0,2]; matrix[r,3]=45;
                    matrix[r,5]=1000; matrix[r,6]=170; matrix[r,7]=125e6; matrix[r,9]=r; matrix[r,10]=100;
                    matrix[r,11]=710; matrix[r,12]=4.88; matrix[r,13]=x[r]; matrix[r,15]=matrix[r,16]=q[r];
                    matrix[r,32]=4095; matrix[r,33]=.9; matrix[r,35]=1;
                    for(int i=0;i<4096;i++)waves[r,i]=q[r]*Math.Exp(-Math.Pow((i-100)/14d,2));
                }
                double[,] raw=(double[,])matrix.Clone();
                Check((bool)Call(review,"LoadDataMatrices",matrix,waves),"complete mapped source matrices load: "+Get(review,"LastError"));
                Set(review,"LocationFilterAlgorithmId","B"); Set(review,"LocationFilterStrength",40d);
                Set(review,"LocationFilterBChargeThresholdPc",20d); Set(review,"LocationFilterEnabled",true);
                int[] expected={0,1,2,4,5};
                Check(((int[])Get(review,"FilteredSourceRowIndices")).SequenceEqual(expected),"B yields independent expected five-row set");
                string archive=Save(review,reader,project,"B expected five rows");
                var settings=Settings(archive);
                Check(settings["EffectiveSelectionMask"]=="111011","real archive saves exact six-row-aligned mask");
                Check(settings["LocationFilterAlgorithmId"]=="B"&&settings["LocationFilterBVersion"]=="1.0.0","B identity and version persisted");
                Check(settings["LocationFilterStrength"]=="40"&&settings["LocationFilterBChargeThresholdPc"]=="20"&&
                    settings["LocationFilterBMinimumClusterSize"]=="3"&&settings["LocationFilterBFromM"]=="0"&&
                    settings.ContainsKey("LocationFilterBToM")&&settings.ContainsKey("LocationFilterBVoltageLimitKv"),"complete B parameter snapshot persisted");
                string diagnostic=settings.Single(k=>k.Key.Contains("Diagnostics")&&k.Value.Contains("unknownSourceRows")).Value;
                var diag=json.Deserialize<Dictionary<string,object>>(diagnostic);
                Check(Convert.ToInt32(diag["unknownRows"])==1&&((System.Collections.IEnumerable)diag["unknownSourceRows"]).Cast<object>().Select(Convert.ToInt32).SequenceEqual(new[]{4}),"saved diagnostic preserves unknown row 4 without confirmed support");
                Verify(Call(readerType,"Read",archive),raw,expected);
                var hashes=Directory.GetFiles(archive).ToDictionary(p=>p,Hash);
                Set(review,"LocationFilterAlgorithmId","A"); Set(review,"LocationFilterStrength",100d); Set(review,"LocationFilterBChargeThresholdPc",250d);
                Verify(Call(readerType,"Read",archive),raw,expected);
                Check(Settings(archive)["LocationFilterAlgorithmId"]=="B"&&Settings(archive)["LocationFilterBChargeThresholdPc"]=="20","active A and threshold edits cannot overwrite saved B settings");
                Check(hashes.All(p=>Hash(p.Key)==p.Value),"active edits and report reads leave saved archive bytes unchanged");
                Set(review,"LocationFilterAlgorithmId","B"); Set(review,"LocationFilterStrength",40d);
                Check(((int[])Get(review,"FilteredSourceRowIndices")).Length==0,"250 pC seed threshold produces truly empty selection");
                string empty=Save(review,reader,project,"B empty result");
                Check(Settings(empty)["EffectiveSelectionMask"]=="000000","empty mask persisted without raw fallback");
                Verify(Call(readerType,"Read",empty),raw,new int[0]);
                Set(review,"LocationFilterBChargeThresholdPc",20d); Set(review,"LocationFilterEnabled",false);
                Verify(Call(readerType,"Read",empty),raw,new int[0]);
                Check(Same(matrix,raw)&&Same((double[,])Get(review,"ResultParameterMatrix"),raw),"export and report preserve caller and accepted source matrices");
            }
            Console.WriteLine("LOCATION_FILTER_B_REPORT_PASS="+checks+"; hardware=false; product_main=false; user_project_writes=false");
            Console.WriteLine("EVIDENCE="+qa); return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e is TargetInvocationException?((TargetInvocationException)e).InnerException:e); return 1; }
    }
}
