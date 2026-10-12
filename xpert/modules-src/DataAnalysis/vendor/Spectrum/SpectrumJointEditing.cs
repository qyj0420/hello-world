using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        Action<double> cableJointAction;
        Func<bool> cableJointAllowed;
        ToolStripMenuItem cableJointItem;
        double cableJointContextM = double.NaN;

        internal void ConfigureCableJointAction(Action<double> action, Func<bool> allowed)
        {
            cableJointAction = action; cableJointAllowed = allowed;
            if (cableJointItem != null) return;
            cableJointItem = new ToolStripMenuItem { Name = "AddCableJoint" };
            regionMenu.Items.Add(new ToolStripSeparator()); regionMenu.Items.Add(cableJointItem);
            regionMenu.Opening += (s, e) => UpdateCableJointMenu();
            cableJointItem.Click += (s, e) =>
            {
                double position = cableJointContextM;
                if (mode == 0 && Finite(position) && cableJointAction != null && cableJointAllowed != null && cableJointAllowed())
                    cableJointAction(position);
            };
            UpdateCableJointMenu();
        }
        internal bool TryCableJointPosition(Point location, out double position)
        {
            position = double.NaN;
            Rectangle area = PlotArea(ClientRectangle);
            if (mode != 0 || view == null || view.Is3D || !area.Contains(location)) return false;
            double value = DataX(location.X, area);
            if (!Finite(value) || !Finite(currentLength) || value <= 0 || value >= currentLength) return false;
            position = value; return true;
        }
        void PrepareCableJointContext(Point location)
        {
            double position;
            cableJointContextM = TryCableJointPosition(location, out position) ? position : double.NaN;
            UpdateCableJointMenu();
        }
        void UpdateCableJointMenu()
        {
            if (cableJointItem == null) return;
            cableJointItem.Text = L("Add cable joint…", "添加电缆接头…");
            cableJointItem.Visible = mode == 0;
            cableJointItem.Enabled = mode == 0 && Finite(cableJointContextM) && cableJointAllowed != null && cableJointAllowed();
        }
    }
}
