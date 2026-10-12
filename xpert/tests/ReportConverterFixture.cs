using System;
using System.IO;
using System.Threading;
// Isolated converter substitute. No device, Office or network access.
static class ReportConverterFixture
{
 static int Main(string[] args){
  string input=args[args.Length-1];
  if(Path.GetFileName(input).StartsWith("timeout")){Thread.Sleep(5000);return 1;}
  string chunk=new string('x',4096);for(int i=0;i<512;i++){Console.Out.Write(chunk);Console.Error.Write(chunk);}
  if(Path.GetFileName(input).StartsWith("fail"))return 17;
  int option=Array.IndexOf(args,"--outdir");
  File.WriteAllText(Path.Combine(args[option+1],Path.GetFileNameWithoutExtension(input)+".pdf"),"%PDF-1.4\n% synthetic envelope only; not rendered content\n%%EOF\n");return 0;
 }
}
