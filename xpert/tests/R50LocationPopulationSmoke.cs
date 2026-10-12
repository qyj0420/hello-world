using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Synthetic source-row/region fixtures. Never calls the analysis worker or SDK.
internal static class R50LocationPopulationSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,Flags).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,Flags).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static double[,] Data()
    {
        var m=new double[85,36];
        for(int r=0;r<85;r++){
            m[r,0]=1;m[r,1]=1;m[r,2]=24.6;m[r,3]=r*3;m[r,5]=166.2;m[r,13]=Double.NaN;
            m[r,15]=m[r,16]=50+r;m[r,23]=50;m[r,24]=6000000;m[r,35]=1;
        }
        return m;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            var dll=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            using(var c=(Control)Activator.CreateInstance(dll.GetType("HVX.UnifiedSpectrum.UnifiedSpectrumControl",true))){
                Set(c,"AutomaticConclusions",false);Set(c,"AcceptedOnly",true);Set(c,"LanguageIndex",0);
                var m=Data();var original=(double[,])m.Clone();Call(c,"LoadDataMatrix",m);
                Set(c,"DisplayModeIndex",4);Call(c,"AddSelectionRegion",4000000d,8000000d,30d,120d,false);
                Check((bool)Call(c,"EnterRegionStatistics",Enumerable.Range(0,81).ToArray()),"F-T region selects 81 actual row identities");
                Set(c,"DisplayModeIndex",2);
                Check(((int[])Get(c,"VisibleSourceRowIndices")).SequenceEqual(Enumerable.Range(0,81)),"Q-PHI draws exact 81 selected rows");
                Check((int)Get(c,"SummaryTotalDischargeCount")==81,"common discharge population 81");
                Set(c,"DisplayModeIndex",0);
                Check(((int[])Get(c,"VisibleSourceRowIndices")).Length==0,"Q-L never fabricates position for missing L");
                Check((int)Get(c,"SummaryValidLocationCount")==0&&(int)Get(c,"SummaryMissingLocationCount")==81,"Q-L explicitly reports 0/81 locations");
                Check(((string)Get(c,"EmptyStateText")).Contains("未定位：81"),"empty Q-L explains missing location, not lost data");
                Check(((string)Get(c,"SummaryTitleSuffix")).Contains("有效定位：0/81"),"title distinguishes pulses from locations");
                var row=Enumerable.Range(0,36).Select(k=>m[1,k]).ToArray();row[13]=80;Call(c,"UpdateReviewRow",1,row);
                Check((int)Get(c,"SummaryValidLocationCount")==1&&((int[])Get(c,"VisibleSourceRowIndices")).SequenceEqual(new[]{1}),"review location updates count and source identity");
                row[13]=200;Call(c,"UpdateReviewRow",1,row);
                Check((int)Get(c,"SummaryOutOfRangeLocationCount")==1&&((string)Get(c,"EmptyStateText")).Contains("超出电缆：1"),"out-of-cable locations distinguished");
                Set(c,"LanguageIndex",1);
                Check(((string)Get(c,"EmptyStateText")).Contains("Missing location: 80")&&((string)Get(c,"EmptyStateText")).Contains("outside cable: 1"),"English same evidence");
                Set(c,"DisplayModeIndex",2);
                Check(!((string)Get(c,"SummaryTitleSuffix")).Contains("valid locations"),"mode-dependent suffix cache invalidated without changing population");
                Check((int)Get(c,"SummaryTotalDischargeCount")==81,"region population remains consistent after modes/review");
                var displayed=(double[,])Get(c,"ResultParameterMatrix");
                Check(displayed.GetLength(0)==85&&Double.IsNaN(displayed[2,13]),"entire original matrix retained");
                Check(m.Cast<double>().Zip(original.Cast<double>(),(a,b)=>a.Equals(b)).All(x=>x),"caller raw matrix unchanged");
            }
            Console.WriteLine("R50_LOCATION_POPULATION_PASS="+checks+";Synthetic=True;Hardware=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
