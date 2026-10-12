using System;
using System.IO;
using System.IO.Compression;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using HVX.Report.Engine;
static class ReportConverterPipeSmoke
{
 static int Main(string[] args){try{
  var engine=new ReportGenerator();var json=new JavaScriptSerializer();string root=Path.Combine(args[0],"converter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  foreach(string name in new[]{"ok","fail","timeout"}) {
   string input=Path.Combine(root,name+".docx"),output=Path.Combine(root,name+".pdf");
   // Conversion now checks package safety before launching any provider.
   using(var archive=ZipFile.Open(input,ZipArchiveMode.Create))
   using(var writer=new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
    writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
   string response=engine.ConvertDocxToPdf(json.Serialize(new{input_docx_path=input,output_pdf_path=output,soffice_path=args[1],timeout_ms=name=="timeout"?100:10000}));
   var data=json.Deserialize<Dictionary<string,object>>(response);bool ok=(bool)data["ok"];
   if(ok!=(name=="ok")||File.Exists(output)!=(name=="ok")||response.Length>20000)throw new Exception(name+": "+response);
   Console.WriteLine("PIPE_"+name+"_PASS; responseLength="+response.Length);
  }return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
