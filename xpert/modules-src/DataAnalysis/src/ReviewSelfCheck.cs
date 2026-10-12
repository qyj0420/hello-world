using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        [Browsable(false)] public string SelfCheckStatus { get; private set; } = "NotRun";
        /// <summary>Checks presentation against the authoritative review snapshot. Never infers missing scientific values.</summary>
        public bool RunSelfCheck(bool repairDisplayState)
        {
            try
            {
                if(!spectrum.AcceptedOnly)
                { if(!repairDisplayState){SelfCheckStatus="AcceptedViewMismatch";return false;}spectrum.AcceptedOnly=true; }
                var bottom=spectrum.ResultParameterMatrix;
                if(bottom.GetLength(0)!=SourceRowCount||bottom.GetLength(1)!=working.GetLength(1))
                {
                    if(!repairDisplayState){SelfCheckStatus="SpectrumShapeMismatch";return false;}
                    LoadSpectrumDisplayData();ApplyTreeSpectrumVisibility();
                }
                else
                {
                    var mismatchedRows=new List<int>();
                    for(int r=0;r<SourceRowCount;r++)
                    {
                        bool mismatch=false;for(int c=0;c<working.GetLength(1);c++)if(!bottom[r,c].Equals(working[r,c])){mismatch=true;break;}
                        if(mismatch)
                        {
                            if(!repairDisplayState){SelfCheckStatus="SpectrumRowMismatch";return false;}
                            mismatchedRows.Add(r);
                        }
                    }
                    if(mismatchedRows.Count>0)spectrum.RepairReviewRows(working,mismatchedRows.ToArray());
                }
                if(selected>=0&&pdmap.CurrentParameterRowIndex!=selected)
                {
                    if(!repairDisplayState){SelfCheckStatus="WaveformSelectionMismatch";return false;}
                    if(!pdmap.LoadDataMatricesAtRow(working,matching,selected)){SelfCheckStatus="WaveformReloadFailed";return false;}
                }
                if(selected>=0&&!IsReviewRowVisible(selected)&&!HasExplicitFilteredPreview())
                { if(!repairDisplayState){SelfCheckStatus="FilteredSelectionMismatch";return false;}EnsureVisibleSelection(); }
                if(!spectrum.RunSelfCheck(repairDisplayState)){SelfCheckStatus="Spectrum:"+spectrum.SelfCheckStatusCode;return false;}
                SelfCheckStatus="OK";RefreshReviewUi();return true;
            }
            catch(Exception ex){SelfCheckStatus="Failed: "+ex.Message;return false;}
        }
    }
}
