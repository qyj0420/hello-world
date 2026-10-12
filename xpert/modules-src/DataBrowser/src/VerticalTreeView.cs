using System.Windows.Forms;
namespace HVX.Controls
{
    // Keep long project filenames discoverable by tooltip while retaining
    // vertical navigation without a distracting horizontal tree scrollbar.
    internal sealed class VerticalTreeView : TreeView
    {
        private const int TvsNoHorizontalScroll = 0x8000;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.Style |= TvsNoHorizontalScroll; return parameters; }
        }
    }
}
