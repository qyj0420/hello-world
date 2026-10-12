// Isolated feasibility probe only. Never start the host, workers, SDKs or models.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal static class PdReviewProtectionProbe
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static void Put(object value,string name,object data){value.GetType().GetProperty(name,All).SetValue(value,data,null);}
    static void Record(BinaryWriter writer,object value)
    {
        if(value==null){writer.Write("null");return;}
        var matrix=value as double[,];if(matrix!=null){writer.Write("matrix");writer.Write(matrix.GetLength(0));writer.Write(matrix.GetLength(1));foreach(double v in matrix)writer.Write(Double.IsNaN(v)?Double.NaN:v);return;}
        var indices=value as int[];if(indices!=null){writer.Write("indices");writer.Write(indices.Length);foreach(int v in indices)writer.Write(v);return;}
        if(value is double){writer.Write("number");writer.Write((double)value);return;}
        writer.Write(value.ToString());
    }
    static string Digest(Action<BinaryWriter> emit)
    {
        using(var stream=new MemoryStream()){using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))emit(writer);using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","");}
    }
    static double[,] Matrix(int count)
    {
        var p=new double[count,36];for(int r=0;r<count;r++){
            p[r,0]=1;p[r,1]=r+1;p[r,2]=10;p[r,5]=500;p[r,6]=170;p[r,7]=125e6;p[r,9]=r;p[r,10]=100;p[r,11]=500;p[r,12]=400;
            p[r,13]=r<count-3?100+r%3*.01:300+(r-count+3)*65;p[r,15]=p[r,16]=r<count-3?1000:1;p[r,22]=.5;p[r,28]=10;p[r,32]=2048;p[r,33]=r%2==0?.15:.9;p[r,35]=1;
        }return p;
    }
    static string Filter(Assembly assembly,double strength,int count)
    {
        var type=assembly.GetType("HVX.PD.LocationFiltering.LocationClusterFilterEngine",true);var input=Matrix(count);var original=(double[,])input.Clone();
        object[] args={input,strength,-1d,-1,true,true,null,null,null,null,null,null};
        type.GetMethod("FilterLocationMatrixAdvanced",All).Invoke(Activator.CreateInstance(type),args);
        Check(input.Cast<double>().SequenceEqual(original.Cast<double>()),"filter preserves input");
        Check(args.Skip(6).All(v=>v!=null),"filter fills every output");
        Check(((double[,])args[6]).GetLength(1)==36,"filter matrix contract");
        return Digest(w=>{foreach(var result in args.Skip(6))Record(w,result);});
    }
    static string Capacitor(Assembly assembly,string fixture,bool manual)
    {
        Directory.CreateDirectory(fixture);File.WriteAllText(Path.Combine(fixture,"ITEM.in"),"负载类型 = 电容局放\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(fixture,"CABLE.in"),"0\tALL\r\n");
        string file=Path.Combine(fixture,"A_12.5kV_2026-09-29-10'00'00.loc");
        using(var writer=new StreamWriter(file,false,new UTF8Encoding(true))){
            foreach(string h in new[]{"Cable length\t0","Velocity (m/us)\t0","Delta x\t1E-7","Phase\t10","Voltage\t12.5","F(Hz)\t50","Sample units\tpC*100000","Reserved\t0","Reserved2\t0","Reserved3\t0","Data"})writer.WriteLine(h);
            for(int i=0;i<50000;i++){double v=.01*Math.Sin(i*.71);foreach(int peak in new[]{10000,25000,40000}){double d=(i-peak)/7d;v+=(peak==25000?-150:100)*(Math.Exp(-d*d/2)-.5*Math.Exp(-(d-3)*(d-3)/2));}writer.WriteLine((v*100000).ToString("R",CultureInfo.InvariantCulture));}
        }
        var request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true));Set(request,"ProjectPath",fixture);Set(request,"LocationFilePath",file);Set(request,"LocationDataId",7L);Set(request,"ManualThresholdEnabled",manual);Set(request,"ManualThreshold",20d);Set(request,"AutomaticThresholdMultiplier",100d);
        var result=assembly.GetType("HVX.PdReview.ReviewCapacitorAnalysis",true).GetMethod("Analyze",All).Invoke(null,new[]{request,(object)fixture});
        Check((bool)Field(result,"Success"),"capacitor synthetic result success");var parameters=(double[,])Field(result,"Parameters");
        Check(parameters.GetLength(0)==3,"capacitor has three synthetic pulses");
        for(int r=0;r<3;r++){Check(parameters[r,1]==7&&parameters[r,2]==12.5,"actual charge identity retained");Check(Double.IsNaN(parameters[r,11])&&parameters[r,13]==0,"no fake cable location");}
        return Digest(w=>{foreach(var name in new[]{"Parameters","Raw","Filtered","Equivalent","AppliedThreshold","Status"})Record(w,Field(result,name));});
    }
    static string Api(Assembly assembly)
    {
        var lines=new List<string>();foreach(var type in assembly.GetExportedTypes().Where(t=>t.FullName!="ConfusedByAttribute")){
            lines.Add("TYPE "+type.FullName);
            foreach(var member in type.GetMembers(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)){
                string line=type.FullName+"|"+member.MemberType+"|"+member.Name;var method=member as MethodBase;
                if(method!=null)line+="|"+String.Join(",",method.GetParameters().Select(p=>p.ParameterType.FullName+" "+p.Name+" "+p.IsOut));
                var prop=member as PropertyInfo;if(prop!=null)line+="|"+prop.PropertyType.FullName+" "+prop.CanRead+" "+prop.CanWrite;
                var field=member as FieldInfo;if(field!=null)line+="|"+field.FieldType.FullName;
                lines.Add(line);
            }
        }
        Check(lines.Count>1000,"public/reflection contract enumerated");lines.Sort(StringComparer.Ordinal);return Digest(w=>{foreach(string line in lines)w.Write(line);});
    }
    static string Ui(Assembly assembly,string fixture)
    {
        assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(fixture,"analysis-settings.json")));
        using(var ui=(Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl",true))){
            Put(ui,"EnableAiFeatures",false);Put(ui,"AutomaticDisplayRecommendation",false);Put(ui,"PromptSavedAnalysisOnProjectLoad",false);
            ui.Size=new Size(1000,700);ui.CreateControl();Check(ui.IsHandleCreated,"isolated UI handle created");
            foreach(string name in new[]{"LoadDataMatrices","ForceReloadDataMatrices","ExportAnalysisSessionUtf8","LoadAnalysisSessionUtf8"})Check(ui.GetType().GetMethod(name,All)!=null,"UI reflection method retained: "+name);
            Check(ui.GetType().GetProperty("PdMap").GetValue(ui,null)!=null,"PdMap UI retained");Check(ui.GetType().GetProperty("Spectrum").GetValue(ui,null)!=null,"spectrum UI retained");
            return Digest(w=>{w.Write(ui.GetType().FullName);w.Write(ui.Controls.Count);w.Write(ui.ClientSize.Width);w.Write(ui.ClientSize.Height);});
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            string dll=Path.GetFullPath(args[0]),fixture=Path.GetFullPath(args[1]);if(!fixture.StartsWith(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Synthetic fixture must remain beneath QA output.");
            var assembly=Assembly.LoadFrom(dll);var rows=new List<string>{"API="+Api(assembly)};
            foreach(double strength in new[]{0d,40d,100d})foreach(int count in new[]{0,1,30})rows.Add("FILTER-"+strength+"-"+count+"="+Filter(assembly,strength,count));
            rows.Add("CAPACITOR-MANUAL="+Capacitor(assembly,fixture,true));rows.Add("CAPACITOR-AUTO="+Capacitor(assembly,fixture,false));rows.Add("UI="+Ui(assembly,fixture));
            foreach(string row in rows)Console.WriteLine(row);Console.WriteLine("CHECKS="+checks+"; HARDWARE=False; USER_DATA=False; MODEL_INFERENCE=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
