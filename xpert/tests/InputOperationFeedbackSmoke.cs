using System;
using System.IO;
using System.Reflection;
using System.Xml;
using Xiongxin.OWTS.Desktop;

internal static class InputOperationFeedbackSmoke
{
    static int checks;
    static void Check(bool result, string name) { checks++; if (!result) throw new Exception(name); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Exception[] errors = {
                new InvalidDataException("Task39 settings must be bounded, explicit XML."),
                new XmlException("Expected Task39 BenchSettings."),
                new FileNotFoundException("internal.dll", "secret-path.dll"),
                new UnauthorizedAccessException("internal path"),
                new IOException("locked-engineering-path"),
                new MissingMethodException("No Task39 input consumer."),
                new OperationCanceledException("internal request id"),
                new Exception("private-configuration-secret"),
                new TargetInvocationException(new InvalidDataException("Task39")),
                new AggregateException(new IOException("private-configuration-secret"))
            };
            foreach (Exception error in errors)
                foreach (string language in new[] { "zh-CN", "zh-TW", "en", "fr" })
                {
                    string message = InputOperationFeedback.Describe(error, language);
                    Check(!String.IsNullOrWhiteSpace(message), "nonempty action guidance");
                    Check(!message.Contains("Task39") && !message.Contains("BenchSettings") &&
                        !message.Contains("secret") && !message.Contains("engineering") &&
                        !message.Contains("internal") && !message.Contains(".dll"), "no raw technical detail");
                    bool chinese = AppLanguages.IsChinese(language);
                    Check(chinese ? message.IndexOf("采集", StringComparison.Ordinal) >= 0 || message.IndexOf("採集", StringComparison.Ordinal) >= 0 :
                        message.IndexOf("acquisition", StringComparison.OrdinalIgnoreCase) >= 0, "current language or English fallback");
                }
            var release=new Xiongxin.FactoryTest.FactorySessionReleaseUnconfirmedException("private-diagnostic-path");
            Check(InputOperationFeedback.Describe(release,"en").Contains("not been confirmed as ended"),"unconfirmed device end is not misreported as disk failure");
            Check(!InputOperationFeedback.Describe(release,"zh-CN").Contains("磁盘"),"no misleading disk-space advice for retained session");
            Check(InputOperationFeedback.Describe(new AggregateException(new IOException("file"),release),"en").Contains("not been confirmed as ended"),"session failure retained across aggregate branches");
            Check(!InputOperationFeedback.Describe(release,"en").Contains("resource")&&!InputOperationFeedback.Describe(release,"zh-CN").Contains("会话"),"customer guidance omits internal ownership terminology");
            Check(!InputOperationFeedback.Describe(release,"en").Contains("private"),"session diagnosis does not expose raw details");
            Check(InputOperationFeedback.Describe(new TargetInvocationException(new OperationCanceledException()), "en").Contains("cancelled"), "wrapped cancellation retained");
            Check(InputOperationFeedback.Describe(new UnauthorizedAccessException(), "en").Contains("permissions"), "access error action");
            Check(InputOperationFeedback.Describe(new MissingMethodException(), "en").Contains("repair"), "missing component action");
            var signal=new TargetInvocationException(new HVX.Controls.PressureNoiseInitializationException("PRIVATE-SIGNAL-DETAIL"));
            Check(InputOperationFeedback.Describe(signal,"en").StartsWith("Signal check failed."),"initialization quality is not mislabeled CAL/settings validation");
            Check(!InputOperationFeedback.Describe(signal,"zh-CN").Contains("PRIVATE")&&!InputOperationFeedback.Describe(signal,"en").Contains("calibration"),"signal guidance hides detail and does not demand unrelated recalibration");
            Console.WriteLine("INPUT_OPERATION_FEEDBACK_PASS=" + checks);
            Console.WriteLine("HARDWARE_ACCESSED=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

namespace Xiongxin.FactoryTest
{
    // Type-only managed fixture; no worker or SDK assembly is loaded.
    internal sealed class FactorySessionReleaseUnconfirmedException:IOException
    { internal FactorySessionReleaseUnconfirmedException(string detail):base(detail){} }
}

namespace HVX.Controls
{
    // Type-only fixture, no product/worker entry or device access.
    internal sealed class PressureNoiseInitializationException:Exception
    { internal PressureNoiseInitializationException(string detail):base(detail){} }
}
