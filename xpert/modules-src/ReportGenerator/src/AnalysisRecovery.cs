using System;
using System.IO;
using System.Linq;

namespace HVX.ReportGenerator
{
    internal static class AnalysisRecovery
    {
        internal static string Move(string project,string target,string[] allowed,string active)
        {
            string root=Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string source=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if(!source.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!allowed.Any(p=>String.Equals(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar),source,StringComparison.OrdinalIgnoreCase)))throw new IOException("分析路径不属于当前项目列表。");
            if(!String.IsNullOrEmpty(active)) {
                string current=Path.GetFullPath(active);
                if(current.Equals(source,StringComparison.OrdinalIgnoreCase)||current.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("该分析当前正在报告中使用，请先加载另一条分析后再删除。");
            }
            bool directory=Directory.Exists(source);
            if(directory) {
                if(!File.Exists(Path.Combine(source,"analysis.json"))||File.Exists(Path.Combine(source,"ITEM.in"))||File.Exists(Path.Combine(source,"CABLE.in")))throw new IOException("拒绝移动项目目录或非分析归档目录。");
            }else if(!File.Exists(source)||!source.EndsWith(".data",StringComparison.OrdinalIgnoreCase))throw new IOException("分析文件不存在或格式不受支持。");
            for(var d=new DirectoryInfo(directory?source:Path.GetDirectoryName(source));d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("分析路径含链接目录，未移动。");
            if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0)throw new IOException("分析不能为链接。");
            // Named archives share this lock with the analysis save/delete path.
            string lockPath=Path.Combine(Path.GetDirectoryName(source),".save.lock");
            if(File.Exists(lockPath)&&(File.GetAttributes(lockPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("归档锁不能是链接。");
            using(var guard=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            string recovery=Path.Combine(Path.GetDirectoryName(source),".deleted_"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",System.Globalization.CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery);
            string destination=Path.Combine(recovery,Path.GetFileName(source));
            if(directory)Directory.Move(source,destination);else File.Move(source,destination);
            return destination;
            }
        }
    }
}
