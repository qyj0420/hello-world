using System;
using System.IO;
using System.Reflection;

// Execute the existing ordinary-spectrum regression against the exact delivered
// DataAnalysis assembly, without a copied DLL or a source-recompiled stand-in.
internal static class OrdinarySpectrumBridgeSmoke
{
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Assembly review = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
                new AssemblyName(eventArgs.Name).Name == review.GetName().Name ? review : null;
            Type suite = Assembly.GetExecutingAssembly().GetType("ReviewBridgeRegression", true);
            return (int)suite.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
