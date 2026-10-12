using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop.Tests
{
    internal static class PressureColdStartSmoke
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Pass the modules directory.");
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "HVX.PdReview.V1.0"))
                    throw new InvalidOperationException("Smoke test must start without a preloaded PdReview assembly.");
                string directory = Path.GetFullPath(Path.Combine(args[0], "PressureTest"));
                string path = Path.Combine(directory, "Xiongxin.OWTS.PressureTest.dll");
                Assembly pressure = Assembly.LoadFrom(path);
                Type type = pressure.GetType("HVX.Controls.PressureTestControl", true);
                using (Control control = (Control)Activator.CreateInstance(type))
                {
                    control.Size = new System.Drawing.Size(1065, 416);
                    if (control.Size != new System.Drawing.Size(1065, 416))
                        throw new InvalidOperationException("Pressure control does not fit the small embedded viewport.");
                    Assembly review = AppDomain.CurrentDomain.GetAssemblies()
                        .Single(a => a.GetName().Name == "HVX.PdReview.V1.0");
                    if (!String.Equals(Path.GetFullPath(review.Location),
                        Path.Combine(directory, "HVX.PdReview.V1.0.dll"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Pressure control resolved its spectrum from another module.");
                    long requests = (long)type.GetProperty("PressureRequestSequence").GetValue(control, null);
                    if (requests != 0) throw new InvalidOperationException("Control creation unexpectedly requested pressure.");
                }
                Console.WriteLine("PASS pressure-cold-start: adjacent spectrum loaded; 1065x416 accepted; no pressure request.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
