using System.ComponentModel;
namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        bool capacitorStatisticsMode;
        [Browsable(false)] public bool CapacitorStatisticsMode {
            get {return capacitorStatisticsMode;}
            set {if(value==capacitorStatisticsMode)return;capacitorStatisticsMode=value;ClearCursorSelection();Invalidate();}
        }
    }
}
