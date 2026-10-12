using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace HVX.Unified.Internal;

internal static class RuntimeBridge
{
	private const string RuntimeResourcePrefix = "Task17.Runtime.";
	private static readonly object RuntimeLoadLock = new object();
	private static readonly Dictionary<string, Assembly> RuntimeAssemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
	private static readonly RuntimeDependency[] RuntimeDependencies = new RuntimeDependency[]
	{
		new RuntimeDependency("4a02e6517789139b5c6b673f.V1.0.5.dll", "BEA0576B78DDBA79AC3470762F70560E362CABA5685D839C4F8F88E1ED977CA5", 376832, "OWTS.Calibration.Onset, Version=1.0.0.0, Culture=neutral, PublicKeyToken=7dc42e362b631bdc"),
		new RuntimeDependency("abf8baa185b4308416932703.V1.0.17.dll", "25E706DCF8F09C600719DE13000CD9186F1880C45109D6758B7CAD661CECFC29", 239616, "OWTS.DAC2X.Engine, Version=5.0.0.0, Culture=neutral, PublicKeyToken=7dc42e362b631bdc"),
		new RuntimeDependency("e0e6f70cda0647b68830372c.V1.0.17.dll", "3B52AF9D65EE01BF3C38153E4EAE667A7701425C9BC56E1BB69279D36994FF9E", 361984, "e0e6f70cda0647b68830372c.V1.0.17, Version=5.0.0.0, Culture=neutral, PublicKeyToken=7dc42e362b631bdc")
	};

	private sealed class RuntimeDependency
	{
		internal readonly string FileName;
		internal readonly string Sha256;
		internal readonly int Length;
		internal readonly string AssemblyIdentity;

		internal RuntimeDependency(string fileName, string sha256, int length, string assemblyIdentity)
		{
			FileName = fileName;
			Sha256 = sha256;
			Length = length;
			AssemblyIdentity = assemblyIdentity;
		}
	}

	internal static object Create(string assemblyFileName, string typeName)
	{
		Assembly runtime = LoadRuntime(assemblyFileName);
		try
		{
			return Activator.CreateInstance(runtime.GetType(typeName, throwOnError: true, ignoreCase: false));
		}
		catch (TargetInvocationException ex)
		{
			if (ex.InnerException != null)
			{
				ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			}
			throw;
		}
	}

	private static Assembly LoadRuntime(string assemblyFileName)
	{
		RuntimeDependency dependency = null;
		foreach (RuntimeDependency candidate in RuntimeDependencies)
		{
			// Exact filenames only: no rooted path, traversal, aliases or case variants.
			if (string.Equals(candidate.FileName, assemblyFileName, StringComparison.Ordinal))
			{
				dependency = candidate;
				break;
			}
		}
		if (dependency == null)
		{
			throw new ArgumentException("Runtime filename is not in the fixed dependency allowlist.", nameof(assemblyFileName));
		}
		lock (RuntimeLoadLock)
		{
			Assembly loaded;
			if (RuntimeAssemblies.TryGetValue(dependency.FileName, out loaded))
			{
				return loaded;
			}
			Assembly owner = typeof(RuntimeBridge).Assembly;
			HashSet<string> resources = new HashSet<string>(StringComparer.Ordinal);
			foreach (string resourceName in owner.GetManifestResourceNames())
			{
				if (resourceName.StartsWith(RuntimeResourcePrefix, StringComparison.OrdinalIgnoreCase))
				{
					resources.Add(resourceName);
				}
			}
			byte[] bytes;
			if (resources.Count != 0)
			{
				// A partial or misspelled embedded set is a broken build, never permission
				// to use a same-name DLL from the host application's probing directories.
				if (resources.Count != RuntimeDependencies.Length)
				{
					throw new InvalidDataException("Embedded runtime resource set must contain exactly the three approved Task17.Runtime.<filename> resources.");
				}
				foreach (RuntimeDependency required in RuntimeDependencies)
				{
					if (!resources.Contains(RuntimeResourcePrefix + required.FileName))
					{
						throw new InvalidDataException("Required embedded runtime resource is missing: " + RuntimeResourcePrefix + required.FileName);
					}
				}
				string resource = RuntimeResourcePrefix + dependency.FileName;
				using (Stream stream = owner.GetManifestResourceStream(resource))
				{
					if (stream == null)
					{
						throw new InvalidDataException("Required embedded runtime resource could not be opened: " + resource);
					}
					bytes = ReadVerifiedRuntime(stream, dependency, resource);
				}
			}
			else
			{
				string path = FindLegacyRuntimePath(dependency.FileName);
				using (FileStream stream = File.OpenRead(path))
				{
					bytes = ReadVerifiedRuntime(stream, dependency, path);
				}
			}
			// The verified bytes, not an assembly already loaded under the same identity,
			// are authoritative. There is no extraction, LoadFrom or ambient probing here.
			loaded = Assembly.Load(bytes);
			if (!string.Equals(loaded.FullName, dependency.AssemblyIdentity, StringComparison.Ordinal))
			{
				throw new InvalidDataException("Verified runtime has an unexpected assembly identity: " + loaded.FullName);
			}
			// PE references in the three pinned files contain only mscorlib,
			// System.Core, System.Numerics and System.Web.Extensions. They do not
			// reference one another. Leave framework resolution to the CLR; no broad
			// AppDomain.AssemblyResolve handler is needed or installed.
			// Publish only successful loads. A failed read/hash/load can be retried.
			RuntimeAssemblies.Add(dependency.FileName, loaded);
			return loaded;
		}
	}

	private static byte[] ReadVerifiedRuntime(Stream stream, RuntimeDependency dependency, string source)
	{
		// Pin the byte count as well as the hash so an untrusted sidecar cannot
		// induce an unbounded allocation before its integrity check.
		byte[] bytes = new byte[dependency.Length];
		int offset = 0;
		while (offset < bytes.Length)
		{
			int count = stream.Read(bytes, offset, bytes.Length - offset);
			if (count == 0)
			{
				break;
			}
			offset += count;
		}
		if (offset != bytes.Length || stream.ReadByte() != -1)
		{
			throw new InvalidDataException("Runtime byte length does not match the pinned dependency: " + source);
		}
		string actualHash;
		using (SHA256 sha = SHA256.Create())
		{
			actualHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
		}
		if (!string.Equals(actualHash, dependency.Sha256, StringComparison.Ordinal))
		{
			throw new InvalidDataException("Runtime SHA-256 mismatch: " + source + "; expected " + dependency.Sha256 + "; actual " + actualHash + ".");
		}
		return bytes;
	}

	private static string FindLegacyRuntimePath(string assemblyFileName)
	{
		string text = Path.GetDirectoryName(typeof(RuntimeBridge).Assembly.Location);
		if (string.IsNullOrEmpty(text))
		{
			text = AppDomain.CurrentDomain.BaseDirectory;
		}
		string path = text;
		if (string.Equals(Path.GetFileName(text), "Support", StringComparison.OrdinalIgnoreCase))
		{
			DirectoryInfo parent = Directory.GetParent(text);
			if (parent != null)
			{
				path = parent.FullName;
			}
		}
		string runtimePath = Path.GetFullPath(Path.Combine(Path.Combine(path, "runtime"), assemblyFileName));
		string adjacentPath = Path.GetFullPath(Path.Combine(text, assemblyFileName));
		if (File.Exists(runtimePath))
		{
			return runtimePath;
		}
		if (File.Exists(adjacentPath))
		{
			return adjacentPath;
		}
		throw new FileNotFoundException("Required runtime DLL was not found. Checked full paths: " + runtimePath + " ; " + adjacentPath, adjacentPath);
	}

	internal static object Invoke(object instance, string methodName, params object[] arguments)
	{
		MethodInfo methodInfo = null;
		MethodInfo[] methods = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
		foreach (MethodInfo methodInfo2 in methods)
		{
			if (methodInfo2.Name == methodName && methodInfo2.GetParameters().Length == arguments.Length)
			{
				methodInfo = methodInfo2;
				break;
			}
		}
		if (methodInfo == null)
		{
			throw new MissingMethodException(instance.GetType().FullName, methodName);
		}
		try
		{
			return methodInfo.Invoke(instance, arguments);
		}
		catch (TargetInvocationException ex)
		{
			if (ex.InnerException != null)
			{
				ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			}
			throw;
		}
	}

	internal static T Get<T>(object instance, string propertyName, T fallback)
	{
		if (instance == null)
		{
			return fallback;
		}
		PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
		if (property == null)
		{
			return fallback;
		}
		object value = property.GetValue(instance, null);
		if (value == null)
		{
			return fallback;
		}
		if (value is T)
		{
			return (T)value;
		}
		return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
	}

	internal static string OneLine(Exception error)
	{
		return (((error == null) ? "Unknown error." : error.Message) ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
	}

	internal static string ToJson(object value)
	{
		StringBuilder stringBuilder = new StringBuilder(1024);
		WriteJson(stringBuilder, value, 0);
		return stringBuilder.ToString();
	}

	private static void WriteJson(StringBuilder output, object value, int depth)
	{
		if (value == null || depth > 6)
		{
			output.Append("null");
			return;
		}
		Type type = value.GetType();
		if (type == typeof(string) || type == typeof(char))
		{
			WriteString(output, Convert.ToString(value, CultureInfo.InvariantCulture));
			return;
		}
		if (type == typeof(bool))
		{
			output.Append(((bool)value) ? "true" : "false");
			return;
		}
		if (type.IsEnum)
		{
			WriteString(output, value.ToString());
			return;
		}
		if (type.IsPrimitive || type == typeof(decimal))
		{
			if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && (double.IsNaN(result) || double.IsInfinity(result)))
			{
				output.Append("null");
			}
			else
			{
				output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
			}
			return;
		}
		if (value is IEnumerable enumerable)
		{
			output.Append('[');
			bool flag = true;
			foreach (object item in enumerable)
			{
				if (!flag)
				{
					output.Append(',');
				}
				WriteJson(output, item, depth + 1);
				flag = false;
			}
			output.Append(']');
			return;
		}
		output.Append('{');
		bool flag2 = true;
		PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
		foreach (PropertyInfo propertyInfo in properties)
		{
			if (propertyInfo.CanRead && propertyInfo.GetIndexParameters().Length == 0)
			{
					object value2 = propertyInfo.GetValue(value, null);
				if (!flag2)
				{
					output.Append(',');
				}
				WriteString(output, propertyInfo.Name);
				output.Append(':');
				WriteJson(output, value2, depth + 1);
				flag2 = false;
			}
		}
		output.Append('}');
	}

	private static void WriteString(StringBuilder output, string value)
	{
		output.Append('"');
		string text = value ?? string.Empty;
		foreach (char c in text)
		{
			switch (c)
			{
			case '"':
				output.Append("\\\"");
				continue;
			case '\\':
				output.Append("\\\\");
				continue;
			case '\b':
				output.Append("\\b");
				continue;
			case '\f':
				output.Append("\\f");
				continue;
			case '\n':
				output.Append("\\n");
				continue;
			case '\r':
				output.Append("\\r");
				continue;
			case '\t':
				output.Append("\\t");
				continue;
			}
			if (c < ' ')
			{
				int num = c;
				output.Append("\\u" + num.ToString("x4", CultureInfo.InvariantCulture));
			}
			else
			{
				output.Append(c);
			}
		}
		output.Append('"');
	}
}
