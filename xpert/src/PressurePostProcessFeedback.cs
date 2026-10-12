using System;
using System.IO;
using System.Reflection;

namespace Xiongxin.OWTS.Desktop
{
    internal static class PressurePostProcessFeedback
    {
        internal static string Describe(Exception error,bool saving,string language)
        {
            while((error is TargetInvocationException||error is AggregateException)&&error.InnerException!=null)error=error.InnerException;
            string zh,en;
            if(error is UnauthorizedAccessException){
                zh="无法访问本次试验的数据目录。请检查目录权限；原始采集记录保留。";
                en="The trial data folder cannot be accessed. Check its permissions; original acquisition records are retained.";
            }else if(error is FileNotFoundException||error is DirectoryNotFoundException||error is FileLoadException||error is BadImageFormatException||error is MissingMemberException){
                zh="所需数据或程序组件缺失，处理未完成。请核对项目文件，必要时修复安装；原始采集记录保留。";
                en="Required data or software components are missing. Check the project files and repair the installation if needed; original acquisition records are retained.";
            }else if(error is IOException&&!(error is InvalidDataException)){
                zh="数据读取或保存失败。请检查磁盘空间、数据目录及文件占用后重试；原始采集记录保留。";
                en="Data could not be read or saved. Check disk space, the data folder and file access, then retry; original acquisition records are retained.";
            }else{
                zh=saving?"本轮保存未完成。请核对当前项目和有效采集轮次后重试；原始采集记录保留。":"本次累计分析未完成。请核对本次试验记录及分析参数后重新分析；已保存记录保留。";
                en=saving?"This round was not saved. Check the current project and valid acquisition round, then retry; original acquisition records are retained.":"Cumulative analysis did not complete. Check the trial records and analysis settings, then rerun analysis; saved records are retained.";
            }
            return AppLanguages.IsChinese(language)?AppLanguages.Display(language,zh):en;
        }
    }
}
