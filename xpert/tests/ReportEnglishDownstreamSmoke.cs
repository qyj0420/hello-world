using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

// Pure reflection into owned QA assemblies; no UI, export, worker or SDK.
internal static class ReportEnglishDownstreamSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static object Call(object target,string name,params object[] args){Type t=target as Type??target.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}
    static readonly string[,] Keys={
        {"TestingOrganization","所属单位"},{"TestPersonnel","测试人员"},{"TestLocation","测试地点"},
        {"Date","日期"},{"Time","时间"},{"StartName","始端名称"},{"EndName","终端名称"},
        {"StartType","始端类型"},{"EndType","终端类型"},{"CableLength","电缆长度"},{"InstallationDate","铺设时间"},
        {"PhaseVoltage","相电压"},{"CoreCount","电缆芯数"},{"Remarks","备注"},
        {"A1","A相测试前绝缘电阻"},{"A2","A相测试后绝缘电阻"},{"B1","B相测试前绝缘电阻"},{"B2","B相测试后绝缘电阻"},
        {"C1","C相测试前绝缘电阻"},{"C2","C相测试后绝缘电阻"},{"LineName","线路名称"},{"CableModel","电缆型号"},
        {"StartDispatch","调度号1"},{"EndDispatch","调度号2"},{"Temperature","温度"},{"Weather","天气"},{"Humidity","湿度"},
        {"TestMode","加压模式"},{"PressureMultipliers","加压倍数"},{"PressureRepetitions","加压次数"}
    };
    static void Engine(Type engine,string qa,Encoding encoding,string name)
    {
        var aliases=(Dictionary<string,string>)Call(engine,"ItemFieldAliases");
        for(int i=0;i<Keys.GetLength(0);i++)Check(aliases[Keys[i,0]]==aliases[Keys[i,1]],"engine exact English/legacy mapping "+Keys[i,0]);
        string file=Path.Combine(qa,name+".in");var text=new StringBuilder("[ITEM]\r\n");
        for(int i=0;i<Keys.GetLength(0);i++)text.Append(Keys[i,0]).Append(": = \"用户值").Append(i).Append("\"\r\n");
        File.WriteAllText(file,text.ToString(),encoding);byte[] before=File.ReadAllBytes(file);
        var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);Call(engine,"ReadItemFile",file,fields);
        for(int i=0;i<Keys.GetLength(0);i++)Check(fields[aliases[Keys[i,0]]]=="用户值"+i,"engine reads persisted English value "+name+" "+Keys[i,0]);
        Check(File.ReadAllBytes(file).SequenceEqual(before),"engine reader does not migrate raw ITEM "+name);
    }
    static void Grid(Assembly module,string culture)
    {
        Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo(culture);
        Type dataType=module.GetType("HVX.ReportGenerator.StateGridReportData",true),browserType=module.GetType("HVX.Controls.BrowserProject",true);
        object data=Activator.CreateInstance(dataType,true),browser=Activator.CreateInstance(browserType),project=Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ProjectReportData",true),true);
        project.GetType().GetProperty("CableLengthM",All).SetValue(project,999d,null);
        Set(data,"Browser",browser);Set(data,"Project",project);
        var joints=(IDictionary)Field(browser,"JointPositionsByPhase");joints["A"]=new[]{80d};
        var values=(IDictionary)Field(data,"Values");values["CableLengthA"]="170";
        var matrix=new double[1,36];matrix[0,0]=1;matrix[0,35]=1;matrix[0,13]=170;matrix[0,15]=matrix[0,16]=77;Set(data,"Matrix",matrix);
        Check(((string)Call(data,"Discharge","A",2)).Contains("170 m"),"StateGrid English per-phase length fallback "+culture);
        values.Remove("CableLengthA");values["A相电缆长度"]="170";
        Check(((string)Call(data,"Discharge","A",2)).Contains("170 m"),"StateGrid legacy per-phase fallback "+culture);
        values.Remove("A相电缆长度");
        Check(((string)Call(data,"Discharge","A",2)).Contains("本相结构/线长不足"),"separate phase does not borrow project maximum "+culture);
        values["CableLengthA"]="170";matrix[0,5]=160;
        Check(!((string)Call(data,"Discharge","A",2)).Contains("170 m"),"matrix measured phase length takes precedence "+culture);
        matrix[0,5]=0;matrix[0,13]=80;
        Check(((string)Call(data,"Discharge","A",0)).Contains("80 m"),"joint classification remains actual phase topology "+culture);
        Check(!((string)Call(data,"Discharge","B",0)).Contains("80 m"),"no phase borrowing from A to B "+culture);
        matrix[0,13]=170;values.Remove("CableLengthA");joints.Remove("A");joints["ALL"]=new[]{80d};
        project.GetType().GetProperty("CableLengthM",All).SetValue(project,170d,null);
        Check(((string)Call(data,"Discharge","A",2)).Contains("170 m"),"common geometry retains explicit common-length fallback "+culture);
    }
    static int Main(string[] args)
    {
        var previous=Thread.CurrentThread.CurrentCulture;
        try{
            string qa=Path.GetFullPath(args[1]);
            var engine=Assembly.LoadFrom(Path.Combine(args[0],"engine","HVX.Report.Engine.dll")).GetType("HVX.Report.Engine.ReportGenerator",true);
            Engine(engine,qa,new UTF8Encoding(true,true),"english-utf8");
            Engine(engine,qa,Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),"english-gbk");
            Engine(engine,qa,new UnicodeEncoding(false,true,true),"english-utf16");
            var module=Assembly.LoadFrom(Path.Combine(args[0],"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            foreach(string culture in new[]{"en-US","de-DE","fr-FR","zh-CN"})Grid(module,culture);
            Console.WriteLine("REPORT_ENGLISH_DOWNSTREAM_PASS="+checks+";ProductEntry=false;Controls=false;Hardware=false;UserProjectWritten=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error is TargetInvocationException?((TargetInvocationException)error).InnerException:error);return 1;}
        finally{Thread.CurrentThread.CurrentCulture=previous;}
    }
}
