using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    // Detailed exceptions belong in diagnostics, never in operator notifications.
    internal static class InputOperationFeedback
    {
        static bool IsSessionFailure(Exception error)
        {
            if(error==null)return false;
            string name=error.GetType().FullName;
            if(name=="Xiongxin.FactoryTest.FactorySessionReleaseUnconfirmedException" ||
               name=="Xiongxin.FactoryTest.FactorySessionFaultException" ||
               name=="Xiongxin.FactoryTest.FactoryResidentRoundIncompleteException")return true;
            var aggregate=error as AggregateException;
            if(aggregate!=null)foreach(var inner in aggregate.InnerExceptions)if(IsSessionFailure(inner))return true;
            return IsSessionFailure(error.InnerException);
        }
        internal static string Describe(Exception error, string language)
        {
            bool sessionFailure=IsSessionFailure(error);
            while ((error is TargetInvocationException || error is AggregateException) && error.InnerException != null)
                error = error.InnerException;
            string zh, en;
            if(sessionFailure)
            {
                zh="设备操作尚未确认结束，采集未启动。请按现场规程确认设备状态；如仍无法继续，请联系维护人员。不会自动恢复加压。";
                en="The equipment operation has not been confirmed as ended; acquisition was not started. Verify the equipment under site procedures. If you still cannot continue, contact support. Voltage will not resume automatically.";
            }
            else if (error != null && error.GetType().FullName=="HVX.Controls.PressureNoiseInitializationException")
            {
                zh="信号检查未通过。请检查接线和输入信号后重新进入加压测试；若仍失败，请联系维护人员。";
                en="Signal check failed. Check the wiring and input signal, then reopen Voltage test. If it still fails, contact support.";
            }
            else if (error is OperationCanceledException)
            {
                zh = "采集已取消。请核对当前项目、相别和设备后重新开始。";
                en = "Acquisition was cancelled. Check the current project, phase and device before starting again.";
            }
            else if (error is FileNotFoundException || error is DirectoryNotFoundException ||
                     error is MissingMemberException || error is BadImageFormatException || error is FileLoadException)
            {
                zh = "采集所需文件缺失或无法加载。请检查采集设置文件，或修复安装后重试。";
                en = "A required acquisition file is missing or could not be loaded. Check the acquisition settings file or repair the installation, then retry.";
            }
            else if (error is UnauthorizedAccessException)
            {
                zh = "无法访问采集文件。请检查文件和数据目录的访问权限后重试。";
                en = "The acquisition files cannot be accessed. Check the permissions for the files and data folder, then retry.";
            }
            else if (error is InvalidDataException || error is XmlException || error is ArgumentException || error is CryptographicException)
            {
                zh = "采集设置或校准记录未通过检查。请核对设备设置，并选择与当前项目、相别和设备匹配的有效校准。";
                en = "The acquisition settings or calibration record failed validation. Check the device settings and select a valid calibration matching the current project, phase and device.";
            }
            else if (error is IOException)
            {
                zh = "采集文件无法读取或保存。请检查数据目录、磁盘空间及文件占用后重试。";
                en = "The acquisition files could not be read or saved. Check the data folder, free disk space and whether another program is using the files, then retry.";
            }
            else
            {
                zh = "采集未能完成。请核对设备连接、采集设置和校准记录；如仍失败，请联系维护人员查看软件日志。";
                en = "Acquisition could not be completed. Check the device connection, acquisition settings and calibration record. If the problem persists, contact support to review the software log.";
            }
            return AppLanguages.IsChinese(language) ? AppLanguages.Display(language, zh) : en;
        }
    }
}
