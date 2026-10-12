using System;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        ToolTip locationFilterBTip;
        void InitializeLocationFilterSelection()
        {
            // B is the sole filter; use the existing shared strength slider.
            locationFilterBTip=new ToolTip{AutoPopDelay=15000};
            Disposed+=(s,e)=>locationFilterBTip.Dispose();
        }
        void UpdateLocationFilterBTip(int unknown)
        {
            if(locationFilterBTip==null)return;
            string detail=locationFilterError.Length>0?L("Filter B calculation failed: ","过滤B计算失败：")+locationFilterError:
                L("ClusterStrength density multiplier; unknown boundary candidates remain visible for review. Unknown rows: ","ClusterStrength密度倍率；边界未知候选保留显示、待复核。未知行数：")+unknown;
            if(statusLabel!=null)locationFilterBTip.SetToolTip(statusLabel,detail);
        }
        void SyncLocationFilterSelection() { }
    }
}
