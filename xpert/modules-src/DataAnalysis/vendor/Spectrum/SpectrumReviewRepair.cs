using System;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // The parent owns the authoritative matrix. Repair all mismatched rows
        // as one display transaction, retaining camera, manual zoom and ROIs.
        internal void RepairReviewRows(double[,] authoritative,int[] rows)
        {
            if(authoritative==null||authoritative.GetLength(0)!=source.GetLength(0)||
                authoritative.GetLength(1)!=source.GetLength(1))
                throw new ArgumentException("Repair matrix shape must match the current source.","authoritative");
            if(rows==null)throw new ArgumentNullException("rows");
            foreach(int row in rows)if(row<0||row>=source.GetLength(0))throw new ArgumentOutOfRangeException("rows");
            bool changed=false;
            foreach(int row in rows)
                for(int column=0;column<source.GetLength(1);column++)
                    if(!source[row,column].Equals(authoritative[row,column]))
                    {source[row,column]=authoritative[row,column];changed=true;}
            if(!changed)return;
            int highlighted=SelectedSourceRowIndex;
            reviewPreviewDirty=false;reviewRevision++;
            InvalidateSummaryStatistics();InvalidateAutomaticReviewRanges();ClearDataViews();Rebuild(false);
            if(highlighted>=0)HighlightSourceRow(highlighted);
        }
    }
}
