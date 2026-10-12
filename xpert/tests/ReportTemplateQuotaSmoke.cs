using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

internal static class ReportTemplateQuotaSmoke
{
    static int checks;
    static object engine;
    static Type engineType, optional;
    static string output;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    const string Begin = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>";
    const string End = "</w:body></w:document>";
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static string Make(string name, Action<ZipArchive> build)
    { string path=Path.Combine(output,name+".docx");using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))build(zip);return path; }
    static void Text(ZipArchive zip,string name,string content)
    { using(var w=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false)))w.Write(content); }
    static Dictionary<string,object> Reply(string method,params object[] args)
    {return Json.Deserialize<Dictionary<string,object>>((string)engineType.GetMethod(method).Invoke(engine,args));}
    static void Rejected(string path,string cause)
    {
        byte[] before=File.ReadAllBytes(path);
        var reply=Reply("AnalyzeTemplate",path);
        Check(!(bool)reply["ok"],cause+" rejected by actual AnalyzeTemplate");
        Check(File.ReadAllBytes(path).SequenceEqual(before),cause+" retained template unchanged");
        var temporary=new List<string>();bool failed=false;
        try { optional.GetMethod("CreateTemplateCopy",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{path,temporary}); }
        catch(TargetInvocationException ex) { failed=ex.InnerException is InvalidDataException||ex.InnerException is System.Xml.XmlException; }
        Check(failed,cause+" rejected by optional-appendix input");
        foreach(string file in temporary) if(File.Exists(file))File.Delete(file);
    }
    [STAThread] static int Main(string[] args)
    {try{
        output=Path.Combine(args[1],"quota-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        engineType=Assembly.LoadFrom(Path.Combine(args[0],"engine","HVX.Report.Engine.dll")).GetType("HVX.Report.Engine.ReportGenerator",true);engine=Activator.CreateInstance(engineType);
        optional=Assembly.LoadFrom(Path.Combine(args[0],"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll")).GetType("HVX.ReportGenerator.ReportOptionalAppendix",true);
        string big=Make("compressed-8MiB",zip=>{using(var w=new StreamWriter(zip.CreateEntry("word/document.xml").Open())){w.Write(Begin+"<w:p><w:r><w:t>");string block=new string('x',8192);for(int i=0;i<1024;i++)w.Write(block);w.Write("</w:t></w:r></w:p>"+End);}});
        Check(new FileInfo(big).Length<16000,"original high-compression reproduction retained");Rejected(big,"8 MiB XML");
        string nodes=Make("many-nodes",zip=>Text(zip,"word/document.xml",Begin+string.Concat(Enumerable.Repeat("<w:p/>",200001))+End));Rejected(nodes,"node count");
        string deep=Make("deep",zip=>Text(zip,"word/document.xml",Begin+string.Concat(Enumerable.Repeat("<w:p>",130))+string.Concat(Enumerable.Repeat("</w:p>",130))+End));Rejected(deep,"depth");
        string dtd=Make("dtd",zip=>Text(zip,"word/document.xml","<!DOCTYPE w:document [<!ENTITY x 'unsafe'>]>"+Begin+"<w:p>&x;</w:p>"+End));Rejected(dtd,"DTD");
        string duplicate=Make("duplicate",zip=>{Text(zip,"word/document.xml",Begin+End);Text(zip,"word/document.xml",Begin+End);});Rejected(duplicate,"duplicate part");
        string total=Make("xml-total",zip=>{for(int i=0;i<6;i++)Text(zip,i==0?"word/document.xml":"word/header"+i+".xml",Begin+"<w:p><w:r><w:t>"+new string('x',3*1024*1024)+"</w:t></w:r></w:p>"+End);});Rejected(total,"cumulative XML");
        string entries=Make("entry-count",zip=>{for(int i=0;i<2049;i++)Text(zip,"part"+i,"a");Text(zip,"word/document.xml",Begin+End);});Rejected(entries,"entry count");
        string truncated=Path.Combine(output,"truncated.docx");File.WriteAllBytes(truncated,File.ReadAllBytes(big).Take(20).ToArray());Rejected(truncated,"invalid ZIP");
        string normal=Make("normal-large",zip=>Text(zip,"word/document.xml",Begin+"<w:p><w:r><w:t>"+new string('x',1024*1024)+"</w:t></w:r></w:p>"+End));
        Check((bool)Reply("AnalyzeTemplate",normal)["ok"],"normal 1 MiB XML still accepted");
        string existing=Path.Combine(output,"existing.docx");byte[] retained=Encoding.UTF8.GetBytes("preserve existing report");File.WriteAllBytes(existing,retained);
        var request=new Dictionary<string,object>{{"template_path",big},{"output_docx_path",existing},{"overwrite",true},{"values",new Dictionary<string,object>()}};
        Check(!(bool)Reply("GenerateReport",Json.Serialize(request))["ok"],"generation rejects oversized source");
        Check(File.ReadAllBytes(existing).SequenceEqual(retained),"rejection preserves existing report despite overwrite=true");
        Check(Directory.GetFiles(output,".hvx_report_*").Length==0,"no partial report on quota rejection");
        Console.WriteLine("REPORT_TEMPLATE_QUOTA_PASS="+checks);Console.WriteLine("EVIDENCE="+output);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
