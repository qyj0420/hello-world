using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class OfflineSimulationBridgeSmoke
{
    private static string releaseRoot;

    [STAThread]
    private static int Main(string[] args)
    {
        releaseRoot = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveModuleAssembly;
        Application.EnableVisualStyles();
        try
        {
            RunModule("CalibrationTest", "Xiongxin.OWTS.CalibrationTest.dll",
                "HVX.Controls.CalibrationProgramControl", "RequestStart", "LastActionSucceeded");
            RunModule("PressureTest", "Xiongxin.OWTS.PressureTest.dll",
                "HVX.Controls.PressureTestControl", "RequestPressure", "LastActionSucceeded");
            Console.WriteLine("PASS OfflineSimulationBridgeSmoke");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RunModule(string moduleId, string assemblyFile, string typeName,
        string requestMethod, string resultProperty)
    {
        string moduleDirectory = Path.Combine(releaseRoot, "modules", moduleId);
        Assembly module = Assembly.LoadFrom(Path.Combine(moduleDirectory, assemblyFile));
        Assembly desktop = Assembly.LoadFrom(Path.Combine(releaseRoot, "Xiongxin.OWTS.Desktop.exe"));
        Type bridgeType = desktop.GetType("Xiongxin.OWTS.Desktop.OfflineSimulationBridge", true);

        using (Form host = new Form { ShowInTaskbar = false, Width = 1280, Height = 720 })
        using (Control control = (Control)Activator.CreateInstance(module.GetType(typeName, true)))
        {
            control.GetType().GetProperty("SimulationMode").SetValue(control, true, null);
            if (moduleId == "PressureTest")
            {
                // No project/U0 is loaded in this isolated fixture. An explicit
                // peak-mode 0 kV request is the safe, defined simulation input.
                control.GetType().GetProperty("CustomVoltageModeCode").SetValue(control, "PEAK", null);
                control.GetType().GetProperty("CustomVoltageValue").SetValue(control, 0d, null);
            }
            control.Dock = DockStyle.Fill;
            host.Controls.Add(control);
            host.Show();
            Pump(100);

            object bridge = Activator.CreateInstance(bridgeType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { true }, null);
            bridgeType.GetMethod("Start").Invoke(bridge, new object[] { control, moduleId });
            object accepted = control.GetType().GetMethod(requestMethod).Invoke(control, new object[0]);
            if (accepted is bool && !(bool)accepted) throw new Exception(moduleId + " request rejected.");
            Pump(1500);

            bool busy = (bool)control.GetType().GetProperty("IsBusy").GetValue(control, null);
            bool succeeded = (bool)control.GetType().GetProperty(resultProperty).GetValue(control, null);
            if (busy || !succeeded) throw new Exception(moduleId + " simulation action did not complete: busy=" + busy +
                "; error=" + control.GetType().GetProperty("LastActionError").GetValue(control, null) +
                "; code=" + control.GetType().GetProperty("LastActionCode").GetValue(control, null));

            if (moduleId == "PressureTest")
            {
                string state = Convert.ToString(control.GetType().GetProperty("PressureStateCode").GetValue(control, null));
                if (state != "COMPLETE") throw new Exception("Pressure simulation state is " + state + ".");
            }

            bridgeType.GetMethod("Stop").Invoke(bridge, new object[0]);
            ((IDisposable)bridge).Dispose();
            host.Hide();
        }
    }

    private static Assembly ResolveModuleAssembly(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        foreach (string moduleDirectory in Directory.GetDirectories(Path.Combine(releaseRoot, "modules")))
        {
            string candidate = Path.Combine(moduleDirectory, file);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }
}
