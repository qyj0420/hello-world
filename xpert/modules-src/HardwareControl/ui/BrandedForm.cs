using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace HVX.OWTS.Task38.Desktop
{
    internal class BrandedForm : Form
    {
        private readonly Icon brandIcon;
        internal BrandedForm()
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Task38.Brand.ico"))
            using (Icon icon = new Icon(source, 32, 32))
                brandIcon = (Icon)icon.Clone();
            Icon = brandIcon;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && brandIcon != null) brandIcon.Dispose();
        }
    }
}
