using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HVX.ProjectModel
{
    /// <summary>
    /// Irreversible deletion of project calibration / test / location data files.
    /// Does not touch ITEM/CABLE, pressure safety, or location E+ mathematics.
    /// </summary>
    public static class ProjectDataFileDeletion
    {
        public static bool IsDeletableDataFile(string path)
        {
            if(String.IsNullOrWhiteSpace(path))return false;
            string ext=Path.GetExtension(path);
            return String.Equals(ext,".cal",StringComparison.OrdinalIgnoreCase)
                ||String.Equals(ext,".dem",StringComparison.OrdinalIgnoreCase)
                ||String.Equals(ext,".loc",StringComparison.OrdinalIgnoreCase)
                ||String.Equals(ext,".hvcal",StringComparison.OrdinalIgnoreCase);
        }
        public static string NormalizeProjectRoot(string projectRoot)
        {
            if(String.IsNullOrWhiteSpace(projectRoot))throw new ArgumentException("项目路径不能为空。","projectRoot");
            string root=Path.GetFullPath(projectRoot.Trim());
            if(File.Exists(root)&&String.Equals(Path.GetFileName(root),"ITEM.in",StringComparison.OrdinalIgnoreCase))
                root=Path.GetDirectoryName(root);
            if(!Directory.Exists(root))throw new DirectoryNotFoundException("项目路径不存在："+root);
            return root;
        }
        public static void EnsureUnderProject(string projectRoot,string path)
        {
            string root=NormalizeProjectRoot(projectRoot);
            string full=Path.GetFullPath(path);
            string prefix=root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&
                !String.Equals(full,root,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只能删除当前项目内的数据文件。");
            if(!IsDeletableDataFile(full))throw new InvalidOperationException("仅允许删除校准、测试或定位数据文件。");
            if(!File.Exists(full))throw new FileNotFoundException("数据文件不存在。",full);
            if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("不支持删除链接文件，请删除原始数据文件。");
        }
        public static void Delete(string projectRoot,IEnumerable<string> paths)
        {
            if(paths==null)throw new ArgumentNullException("paths");
            string root=NormalizeProjectRoot(projectRoot);
            var unique=paths.Where(p=>!String.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if(unique.Length==0)throw new ArgumentException("未选择可删除的数据文件。");
            foreach(string path in unique)EnsureUnderProject(root,path);
            foreach(string path in unique)File.Delete(path);
        }
        public static string ConfirmMessage(int count,bool english)
        {
            if(count<=0)return english?"No deletable data files are selected.":"未选择可删除的数据文件。";
            if(english)
                return "Permanently delete "+count+" selected data file(s)?\n\nDeletion cannot be undone.\nCalibration, test, and location source files will be removed from the project folder.";
            return "确认删除所选的 "+count+" 个数据文件？\n\n删除不可恢复。\n将从项目目录中永久移除所选的校准数据、测试数据或定位数据文件。";
        }
        public static string ConfirmCaption(bool english)
        {return english?"Delete data files":"删除数据文件";}
    }
}
