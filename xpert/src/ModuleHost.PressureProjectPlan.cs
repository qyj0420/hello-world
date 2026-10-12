using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        Control pressurePlanContextControl;
        string appliedPressurePlanSignature;

        // Project editing saves ITEM.in. Its read-only fingerprint also detects a
        // changed plan at the same project path when the cached page is reopened.
        internal static string PressureProjectPlanSignature(string projectPath)
        {
            if (String.IsNullOrWhiteSpace(projectPath)) return "NO_PROJECT";
            try
            {
                string itemPath = Directory.Exists(projectPath) ? Path.Combine(projectPath, "ITEM.in") : projectPath;
                itemPath = Path.GetFullPath(itemPath);
                if (!File.Exists(itemPath)) return itemPath + "|MISSING_ITEM";
                using (SHA256 hash = SHA256.Create())
                    return itemPath + "|" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(itemPath)));
            }
            catch (Exception error) { return projectPath + "|UNREADABLE_ITEM|" + error.GetType().Name; }
        }

        void ApplyPressureProjectPlan(Control control, string projectPath, string signature)
        {
            if (ReferenceEquals(control, pressurePlanContextControl) &&
                String.Equals(signature, appliedPressurePlanSignature, StringComparison.Ordinal)) return;
            object result;
            string error;
            bool invoked = LateBound.TryInvoke(control, "LoadProjectPlan", new object[] { projectPath ?? String.Empty }, out result, out error);
            if (!invoked)
            {
                TryInvoke(control, "ClearPressurePlan", new object[0]);
                diagnostics.Warning("PressurePlan", "Could not apply the project pressure plan: " + error);
                return;
            }
            pressurePlanContextControl = control;
            appliedPressurePlanSignature = signature;
            if (!(result is bool) || !(bool)result)
                diagnostics.Warning("PressurePlan", LateBound.ReadString(control, "LastError"));
        }
    }
}
