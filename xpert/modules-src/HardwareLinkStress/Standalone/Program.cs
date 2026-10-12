using System;
using System.Windows.Forms;

namespace HVX.HardwareLinkStress.Standalone
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var window = new StressStandaloneForm(HostRegistration.CreateVerifiedHost()))
            {
                // Offline build smoke check: creates the branded window without
                // showing it or contacting any hardware.
                if (args.Length == 1 && String.Equals(args[0], "--self-test", StringComparison.Ordinal))
                {
                    window.CreateControl();
                    return window.Icon != null && window.Controls.Count > 0 ? 0 : 2;
                }
                Application.Run(window);
            }
            return 0;
        }
    }
}
