using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Xiongxin.OWTS.Desktop
{
    /// <summary>
    /// Best-effort, non-throwing diagnostics for integration boundaries.  A logging
    /// failure must never prevent the desktop shell or an analysis module from opening.
    /// </summary>
    internal sealed class Diagnostics
    {
        private static readonly object FileGate = new object();
        private readonly string directory;

        public Diagnostics()
            : this(AppPaths.DiagnosticsDirectory)
        {
        }

        public Diagnostics(string diagnosticsDirectory)
        {
            directory = String.IsNullOrWhiteSpace(diagnosticsDirectory)
                ? AppPaths.DiagnosticsDirectory
                : diagnosticsDirectory;
        }

        public string CurrentLogPath
        {
            get
            {
                return Path.Combine(directory, "desktop-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            }
        }

        public void Info(string source, string message)
        {
            Write("INFO", source, message, null);
        }

        public void Warning(string source, string message)
        {
            Write("WARN", source, message, null);
        }

        public void Error(string source, string message)
        {
            Write("ERROR", source, message, null);
        }

        public void Error(string source, string message, Exception error)
        {
            Write("ERROR", source, message, error);
        }

        private void Write(string level, string source, string message, Exception error)
        {
            try
            {
                StringBuilder line = new StringBuilder(256);
                line.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                line.Append(" [").Append(level ?? "INFO").Append("] ");
                line.Append(String.IsNullOrWhiteSpace(source) ? "Desktop" : source.Trim());
                line.Append(" - ").Append(message ?? String.Empty);
                if (error != null)
                {
                    Exception root = error is TargetInvocationException && error.InnerException != null
                        ? error.InnerException
                        : error;
                    // Keep inner exceptions (including asynchronous cleanup
                    // AggregateException details), not just the generic wrapper.
                    line.Append(" | ").Append(root.ToString());
                }
                line.AppendLine();

                lock (FileGate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(CurrentLogPath, line.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Diagnostics are deliberately non-fatal.
            }
        }
    }

    /// <summary>
    /// Small late-bound adapter used at plug-in boundaries.  It intentionally accepts
    /// only properties/fields/methods on the supplied object and never evaluates names.
    /// </summary>
    internal static class LateBound
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;

        public static object Read(object target, params string[] candidatePaths)
        {
            if (target == null || candidatePaths == null) return null;
            for (int i = 0; i < candidatePaths.Length; i++)
            {
                object value;
                if (TryReadPath(target, candidatePaths[i], out value)) return value;
            }
            return null;
        }

        public static string ReadString(object target, params string[] candidatePaths)
        {
            object value = Read(target, candidatePaths);
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
        }

        public static bool ReadBoolean(object target, bool fallback, params string[] candidatePaths)
        {
            object value = Read(target, candidatePaths);
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return Boolean.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static bool TrySet(object target, string memberName, object value, out string error)
        {
            error = String.Empty;
            if (target == null || String.IsNullOrWhiteSpace(memberName)) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(memberName, InstanceMembers);
                if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0) return false;
                object converted;
                if (!TryConvert(value, property.PropertyType, out converted))
                {
                    error = "Cannot convert value for " + property.Name + ".";
                    return false;
                }
                property.SetValue(target, converted, null);
                return true;
            }
            catch (Exception ex)
            {
                error = RootMessage(ex);
                return false;
            }
        }

        public static bool TryInvoke(object target, string methodName, object[] arguments, out object result, out string error)
        {
            result = null;
            error = String.Empty;
            if (target == null || String.IsNullOrWhiteSpace(methodName)) return false;
            object[] supplied = arguments ?? new object[0];
            try
            {
                MethodInfo[] methods = target.GetType().GetMethods(InstanceMembers);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!String.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase) || method.IsGenericMethodDefinition) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != supplied.Length) continue;
                    object[] converted = new object[supplied.Length];
                    bool compatible = true;
                    for (int j = 0; j < supplied.Length; j++)
                    {
                        if (!TryConvert(supplied[j], parameters[j].ParameterType, out converted[j]))
                        {
                            compatible = false;
                            break;
                        }
                    }
                    if (!compatible) continue;
                    result = method.Invoke(target, converted);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                error = RootMessage(ex);
                return false;
            }
        }

        private static bool TryReadPath(object target, string path, out object value)
        {
            value = target;
            if (target == null || String.IsNullOrWhiteSpace(path)) return false;
            string[] segments = path.Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                if (value == null || String.IsNullOrWhiteSpace(segments[i])) return false;
                Type type = value.GetType();
                PropertyInfo property = type.GetProperty(segments[i], InstanceMembers);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(value, null);
                    continue;
                }
                FieldInfo field = type.GetField(segments[i], InstanceMembers);
                if (field == null) return false;
                value = field.GetValue(value);
            }
            return true;
        }

        private static bool TryConvert(object value, Type destinationType, out object converted)
        {
            converted = null;
            if (destinationType == null) return false;
            Type nullable = Nullable.GetUnderlyingType(destinationType);
            Type effective = nullable ?? destinationType;
            if (value == null)
            {
                if (destinationType.IsValueType && nullable == null) return false;
                return true;
            }
            if (effective.IsInstanceOfType(value))
            {
                converted = value;
                return true;
            }
            try
            {
                if (effective.IsEnum)
                {
                    converted = value is string
                        ? Enum.Parse(effective, (string)value, true)
                        : Enum.ToObject(effective, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    return true;
                }
                if (effective == typeof(string))
                {
                    converted = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return true;
                }
                TypeConverter converter = TypeDescriptor.GetConverter(effective);
                if (converter != null && converter.CanConvertFrom(value.GetType()))
                {
                    converted = converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                    return true;
                }
                converted = Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        private static string RootMessage(Exception error)
        {
            Exception root = error;
            while (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
            return root.Message;
        }
    }
}
