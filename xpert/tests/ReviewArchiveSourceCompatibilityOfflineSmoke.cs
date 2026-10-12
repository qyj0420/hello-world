using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Pure review-control/archival regression. The original project is read only;
// synthetic writes, preferences and journals stay in the owned QA directory.
// No product entry point, algorithm worker, SDK or device is executed.
internal static class ReviewArchiveSourceCompatibilityOfflineSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Type review;static Assembly module;static int checks;static string qa;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object target,string name,params object[] args)
    {
        Type t=target as Type??target.GetType();
        try{return t.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}
        catch(TargetInvocationException ex){throw ex.InnerException;}
    }
    static object Get(object target,string name){return target.GetType().GetField(name,F).GetValue(target);}
    static object Prop(object target,string name){return target.GetType().GetProperty(name,F).GetValue(target,null);}
    static void Put(object target,string name,object value){target.GetType().GetProperty(name,F).SetValue(target,value,null);}
    static Control ControlFor(string root)
    {
        var c=(Control)Activator.CreateInstance(review);
        Put(c,"PromptSavedAnalysisOnProjectLoad",false);Put(c,"EnableAiFeatures",false);Put(c,"ProjectPath",root);return c;
    }
    static string Hash(string path)
    {
        using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");
    }
    static Dictionary<string,string> Snapshot(string root,string archive)
    {
        return Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(archive,"*",SearchOption.AllDirectories))
            .ToDictionary(p=>p,Hash,StringComparer.OrdinalIgnoreCase);
    }
    static void Same(double[,] a,double[,] b,string name)
    {
        Check(a.GetLength(0)==b.GetLength(0)&&a.GetLength(1)==b.GetLength(1),name+" dimensions");
        for(int r=0;r<a.GetLength(0);r++)for(int c=0;c<a.GetLength(1);c++)
            if(BitConverter.DoubleToInt64Bits(a[r,c])!=BitConverter.DoubleToInt64Bits(b[r,c]))
                throw new Exception(name+" changed at "+r+","+c+": "+a[r,c]+" -> "+b[r,c]);
        checks++;
    }
    static string Project(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"Load type = Three-core\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t415m\r\n");return root;
    }
    static string Loc(string root,int suffix,string context=null)
    {
        string path=Path.Combine(root,"A_12.5kV_"+suffix+".loc");
        using(var writer=new StreamWriter(path,false,new UTF8Encoding(true)))
        {
            foreach(string line in new[]{"Cable length\t415","Velocity (m/us)\t170","Delta x\t8E-9","Phase\tA","Voltage\t12.5","F(Hz)\t500","Sample units\tpC*100000","Reserved\t0","Reserved2\t0",context==null?"Reserved3\t0":"Acquisition context\t"+context,"Data"})writer.WriteLine(line);
            for(int i=0;i<4096;i++)writer.WriteLine(i==100?"10000000":i==300?"5000000":"0");
        }
        return path;
    }
    static double[,] Parameters(int count=1)
    {
        var p=new double[count,36];for(int r=0;r<count;r++)
        {
            p[r,0]=1;p[r,1]=7+r;p[r,2]=12.5;p[r,3]=90;p[r,5]=415;p[r,6]=170;p[r,7]=125e6;p[r,8]=7+r;p[r,9]=r;
            p[r,10]=100;p[r,11]=300;p[r,12]=200;p[r,13]=136;p[r,15]=p[r,16]=100;p[r,21]=.9;p[r,23]=20;p[r,24]=1e6;
            p[r,26]=9;p[r,31]=0;p[r,32]=4095;p[r,33]=.9;p[r,34]=1;p[r,35]=1;
        }return p;
    }
    static double[,] Waves(int count=1)
    {var w=new double[count*3,4096];for(int r=0;r<w.GetLength(0);r++){w[r,100]=100+r;w[r,300]=50+r;}return w;}
    static object[] Export(Control c)
    {
        object[] args={null,null,null,null};Check((bool)Call(c,"ExportAnalysisSession",args),"fixture session export: "+Prop(c,"LastError"));return args;
    }
    static void PrepareFixture(Control c,string[] paths,int rows)
    {
        Check((bool)Call(c,"SetProjectLocationIds",paths,Enumerable.Range(7,paths.Length).Select(i=>(double)i).ToArray()),"fixture file IDs");
        Check((bool)Call(c,"ForceReloadDataMatrices",Parameters(rows),Waves(rows)),"fixture matrix: "+Prop(c,"LastError"));
        Check((bool)Call(Get(c,"analysisTree"),"MarkAnalyzed",new object[]{paths.Take(rows).ToArray()}),"fixture analyzed states");
    }
    static void EmptyAndCorruptSources()
    {
        string root=Project(Path.Combine(qa,"empty-unselected"));string valid=Loc(root,1),empty=Path.Combine(root,"A_12.5kV_2.loc"),truncated=Path.Combine(root,"A_12.5kV_3.loc");
        File.WriteAllBytes(empty,new byte[0]);File.WriteAllText(truncated,"partial header\r\n");
        using(var c=ControlFor(root))
        {
            PrepareFixture(c,new[]{valid,empty,truncated},1);var args=Export(c);
            var plan=Call(c,"PrepareSession",root,args[0],args[1],args[2],args[3]);
            Check((int)Get(plan,"Missing")==1&&(int)Get(plan,"Changed")==1,"unselected empty and damaged sources classified separately");
            Same((double[,])args[0],(double[,])Get(plan,"Parameters"),"valid fixture parameter rows");Same((double[,])args[1],(double[,])Get(plan,"Waves"),"valid fixture waves");
            Check((bool)Call(c,"SaveNamedAnalysis","empty unselected regression"),"save fixture named analysis: "+Prop(c,"LastError"));string id=(string)Prop(c,"LastSavedAnalysisId");
            string archive=Path.Combine(root,"Analysis report",id);var before=Snapshot(root,archive);
            Check((bool)Call(c,"LoadSavedAnalysis",id),"named archive loads despite unused empty source: "+Prop(c,"LastError"));
            Check((int)Prop(c,"SourceRowCount")==1&&((string)Prop(c,"AnalysisArchiveStatus")).Contains("2"),"partial status and valid retained row");
            Check(before.All(p=>File.Exists(p.Key)&&Hash(p.Key)==p.Value),"fixture load preserves every archive/source byte");
            // Archive-member integrity stays fail closed, independent of source exclusion.
            string session=Path.Combine(archive,"session.json");File.AppendAllText(session," ");
            Check(!(bool)Call(c,"LoadSavedAnalysis",id)&&(int)Prop(c,"SourceRowCount")==1,"tampered archive rejected without replacing loaded data");
        }
        string damagedRoot=Project(Path.Combine(qa,"analyzed-source-unavailable"));var paths=Enumerable.Range(1,3).Select(i=>Loc(damagedRoot,i)).ToArray();
        using(var c=ControlFor(damagedRoot))
        {
            PrepareFixture(c,paths,3);var args=Export(c);var files=(string[,])args[2];
            File.WriteAllBytes(paths[1],new byte[0]);File.WriteAllText(paths[2],"truncated\r\n");
            for(int i=0;i<files.GetLength(0);i++)files[i,6]=Hash(Path.Combine(damagedRoot,files[i,0]));
            var plan=Call(c,"PrepareSession",damagedRoot,args[0],args[1],files,args[3]);
            var kept=(double[,])Get(plan,"Parameters");var waves=(double[,])Get(plan,"Waves");
            Check(kept.GetLength(0)==1&&kept[0,1]==7&&waves.GetLength(0)==3,"empty/damaged analyzed rows and their 3N waves excluded together");
            Check(((string[])Get(plan,"Analyzed")).SequenceEqual(new[]{paths[0]}),"unavailable source never marked analyzed");
            // A later source change excludes its rows; it cannot masquerade as valid old analysis.
            File.AppendAllText(paths[0],"0\r\n");plan=Call(c,"PrepareSession",damagedRoot,args[0],args[1],files,args[3]);
            Check(((double[,])Get(plan,"Parameters")).GetLength(0)==0&&(int)Get(plan,"Changed")==2,"changed source fingerprint remains excluded");
        }
    }
    static void ActualArchive(string root,string id,bool baseline)
    {
        string archive=Path.Combine(root,"Analysis report",id);var before=Snapshot(root,archive);
        var p=(double[,])Call(review,"ReadMatrix",Path.Combine(archive,"parameters.bin"),10000000L);
        var w=(double[,])Call(review,"ReadMatrix",Path.Combine(archive,"pdmap.bin"),32000000L);
        Check(p.GetLength(0)==644,"actual 023 fixture has 644 saved rows");
        using(var c=ControlFor(root))
        {
            if(baseline)
            {
                Check(!(bool)Call(c,"LoadSavedAnalysis",id),"frozen r58 reproduces load failure");
                Check(((string)Prop(c,"LastError")).Contains("LOC header requires 11 bounded lines"),"frozen r58 failure matches user diagnostic");
                Console.WriteLine("BASELINE_023_REPRODUCED="+Prop(c,"LastError"));
            }
            else
            {
                var json=new JavaScriptSerializer{MaxJsonLength=64*1024*1024,RecursionLimit=16};
                var payload=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(archive,"session.json"),new UTF8Encoding(false,true)));
                var plan=Call(c,"PrepareSession",root,p,w,Call(review,"SessionTable",payload["files"],11),Call(review,"SessionTable",payload["settings"],2));
                Same(p,(double[,])Get(plan,"Parameters"),"actual 023 prepared parameters");Same(w,(double[,])Get(plan,"Waves"),"actual 023 prepared 3N waves");
                Check((int)Get(plan,"Missing")==6&&(int)Get(plan,"Changed")==0,"actual 023 six empty placeholders excluded, no changed valid source");
                Check((bool)Call(c,"LoadSavedAnalysis",id),"actual 023 full named-archive load: "+Prop(c,"LastError"));
                Check((int)Prop(c,"SourceRowCount")==644,"actual 023 full reload retains all 644 valid result rows");
                Same(p,(double[,])Prop(c,"ResultParameterMatrix"),"actual 023 full reload parameter values");
                Console.WriteLine("HOTFIX_023_LOADED="+Prop(c,"AnalysisSessionStatus")+"; "+Prop(c,"AnalysisArchiveStatus"));
            }
        }
        Check(before.All(p0=>File.Exists(p0.Key)&&Hash(p0.Key)==p0.Value),"actual project/archive source hashes unchanged");
        Check(Snapshot(root,archive).Count==before.Count,"actual project/archive no new or removed files");
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding=Encoding.UTF8;qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.GetFullPath(args[0]));review=module.GetType("HVX.PdReview.PdReviewControl",true);
            module.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(qa,"isolated-preferences.json")));
            module.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(qa,"isolated-journals")));
            bool baseline=args.Length>4&&args[4]=="baseline";
            if(!baseline)EmptyAndCorruptSources();ActualArchive(Path.GetFullPath(args[2]),args[3],baseline);
            Console.WriteLine("ARCHIVE_SOURCE_COMPATIBILITY_PASS="+checks+";ProductEntry=false;Algorithm=false;Worker=false;SDK=false;OriginalProjectWrites=false");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
