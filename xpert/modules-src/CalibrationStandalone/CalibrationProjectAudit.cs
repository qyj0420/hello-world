using System;
using System.IO;
using System.Text;
using System.Xml;

namespace Xiongxin.Calibration
{
    internal static class CalibrationProjectAudit
    {
        private static readonly object Gate=new object();
        internal static bool TryWrite(string project,string phase,string level,string action,string outcome,string detail,out string error)
        {
            error="";
            try {
                if(String.IsNullOrWhiteSpace(project)||!Directory.Exists(project))throw new IOException("未指定可用项目目录。");
                string root=Path.GetFullPath(project),folder=Path.Combine(root,"Calibration","Logs");
                string path=Path.Combine(folder,"calibration-operations-"+DateTime.Now.ToString("yyyyMMdd")+".xml.log");
                for(var dir=new DirectoryInfo(folder);dir!=null;dir=dir.Parent)
                    if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("日志路径不能包含链接。");
                var text=new StringBuilder();
                using(var xml=XmlWriter.Create(text,new XmlWriterSettings{OmitXmlDeclaration=true,ConformanceLevel=ConformanceLevel.Fragment,Indent=false})) {
                    xml.WriteStartElement("event");xml.WriteAttributeString("utc",DateTime.UtcNow.ToString("o"));xml.WriteAttributeString("local",DateTimeOffset.Now.ToString("o"));
                    xml.WriteAttributeString("phase",phase??"");xml.WriteAttributeString("level",level??"");xml.WriteAttributeString("action",action??"");xml.WriteAttributeString("outcome",outcome??"");
                    xml.WriteElementString("detail",detail??"");xml.WriteEndElement();
                }
                lock(Gate) {
                    Directory.CreateDirectory(folder);FileAttributes? prior=File.Exists(path)?File.GetAttributes(path):(FileAttributes?)null;
                    if(prior.HasValue&&(prior.Value&FileAttributes.ReparsePoint)!=0)throw new IOException("日志文件不能是链接。");
                    try {
                        if(prior.HasValue&&(prior.Value&FileAttributes.ReadOnly)!=0)File.SetAttributes(path,prior.Value&~FileAttributes.ReadOnly);
                        using(var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read)) {
                            byte[] bytes=new UTF8Encoding(false).GetBytes(text+Environment.NewLine);stream.Write(bytes,0,bytes.Length);stream.Flush(true);
                        }
                    }finally{if(prior.HasValue&&File.Exists(path))File.SetAttributes(path,prior.Value);}
                }
                return true;
            }catch(Exception ex){error=ex.Message;return false;}
        }
    }
}
