using System;
using System.Reflection;

namespace HVX.Task17.Location;

// The file version is the single source for facade and inner-engine diagnostics.
// AssemblyVersion remains frozen for LabVIEW binding compatibility.
internal static class Task17BuildInfo
{
    internal static readonly string Version=((AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
        typeof(Task17BuildInfo).Assembly,typeof(AssemblyFileVersionAttribute))).Version;
}
