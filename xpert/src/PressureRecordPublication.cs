using System;
using System.IO;

namespace HVX.Storage
{
    // A short-lived write-intent marker, not an extra saved dataset. It is
    // removed only after both files have been published. An interrupted write
    // remains visibly invalid after process restart; readers never guess success.
    internal static class PressureRecordPublication
    {
        internal static string MarkerPath(string recordPath)
        { return Path.ChangeExtension(Path.GetFullPath(recordPath), ".record.pending"); }

        internal static void RequireReadable(string recordPath)
        {
            string extension=Path.GetExtension(recordPath);
            if (!String.Equals(extension,".loc",StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(extension,".dem",StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(MarkerPath(recordPath)))
                throw new InvalidDataException("本轮测试与定位数据尚未完整保存，不能作为有效记录读取。请保留文件并检查保存日志。");
        }
    }
}
