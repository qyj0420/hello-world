using System;
using System.ComponentModel;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // Review state belongs to this control, not to the caller's result matrix.
        // It is deliberately independent of acceptance (column 35) and ROI deletion.
        bool[] reviewVisibility;
        double[] reviewCountRange;
        long reviewRevision;
        bool reviewPreviewDirty;
        Func<int,double,double> reviewPositionProjection;
        internal void SetReviewPositionProjection(Func<int,double,double> projection)
        { SetReviewDisplayGeometry(projection,reviewCableLengthProvider,reviewJointProvider); }
        internal void SetReviewDisplayGeometry(Func<int,double,double> projection,
            Func<int,double> cableLengthProvider,Func<int,double,double[]> jointProvider)
        {
            reviewPositionProjection=projection;reviewCableLengthProvider=cableLengthProvider;
            reviewJointProvider=jointProvider;reviewRevision++;
            InvalidateAutomaticReviewRanges();ClearDataViews();Rebuild(false);
        }

        [Browsable(false)]
        public int[] VisibleSourceRowIndices { get { return (int[])visibleRows.Clone(); } }

        /// <summary>Updates one original source row without resetting camera, zoom, regions or phase selection.</summary>
        public void UpdateReviewRow(int sourceRow, double[] row)
        { UpdateReviewRow(sourceRow,row,false); }

        internal void UpdateReviewRow(int sourceRow, double[] row,bool preview)
        {
            if (sourceRow < 0 || sourceRow >= source.GetLength(0)) throw new ArgumentOutOfRangeException("sourceRow");
            if (row == null || row.Length != source.GetLength(1))
                throw new ArgumentException("The review row must contain exactly the source matrix column count.", "row");
            bool changed = false;
            for (int c = 0; c < row.Length; c++) if (!source[sourceRow, c].Equals(row[c])) { changed = true; break; }
            if (!changed) { if(!preview)FlushReviewPreview();return; }
            bool summaryChanged=!source[sourceRow,0].Equals(row[0])||!source[sourceRow,5].Equals(row[5])||!source[sourceRow,13].Equals(row[13])||!source[sourceRow,15].Equals(row[15])||useCompensatedCharge&&!source[sourceRow,16].Equals(row[16])||!source[sourceRow,35].Equals(row[35]);
            for (int c = 0; c < row.Length; c++) source[sourceRow, c] = row[c];
            reviewRevision++;
            if(summaryChanged)InvalidateSummaryStatistics();
            if(preview)
            {
                reviewPreviewDirty=true;
                // Scatter geometry is row-local. Dragging one cursor must not
                // run all-source aggregation or Task15 on every mouse message.
                dataViews.Clear();recentModes.Clear();DropDrawingCaches();
                if(TryUpdatePreviewScatter(sourceRow))
                { dataViews.Add(mode,view);recentModes.Add(mode);Invalidate();return; }
                Rebuild(false);return;
            }
            reviewPreviewDirty=false;
            InvalidateAutomaticReviewRanges(); ClearDataViews(); Rebuild(false);
            if (SelectedSourceRowIndex >= 0) HighlightSourceRow(SelectedSourceRowIndex);
        }

        internal void FlushReviewPreview()
        {
            if(!reviewPreviewDirty)return;
            reviewPreviewDirty=false;
            InvalidateAutomaticReviewRanges();ClearDataViews();Rebuild(false);
        }

        bool TryUpdatePreviewScatter(int row)
        {
            if(view==null||mode!=0&&mode!=2&&mode!=4&&mode!=5&&mode!=7)return false;
            // The preview changes analysis columns, not identity/phase/acceptance.
            // All eligibility still comes from the original filters and masks.
            int phase=(int)source[row,0];
            bool eligible=!deleted.Contains(row)&&IsReviewRowVisible(row)&&IsRegionStatisticsRow(row)&&(!acceptedOnly||source[row,35]==1)&&
                phase>=1&&phase<=3&&visible[phase-1]&&(phaseFilter<0||phase==phaseFilter);
            if(phase==0)return false; // Preserve the ALL fallback rules via the full adapter.
            double phi=source[row,3];if(Finite(phi))phi=((phi%360)+360)%360;
            double x=mode==4?source[row,24]:mode==2?phi:reviewPositionProjection==null?source[row,13]:reviewPositionProjection(row,source[row,13]);
            double y=mode==4?source[row,23]:mode==2||mode==7?PresentedCharge(row):Math.Abs(PresentedCharge(row));
            double z=mode==5?source[row,2]:mode==7?phi:0;
            eligible&=Finite(x)&&Finite(y)&&Finite(z)&&
                (mode==4?x>0&&y>0:mode==2||x>=0&&(!(currentLength>0)||x<=currentLength));
            int index=view.Points.FindIndex(p=>p.SourceRows.Length==1&&p.SourceRows[0]==row);
            if(eligible)
            {
                if(index>=0){var point=view.Points[index];point.X=x;point.Y=y;point.Z=z;}
                else view.Points.Add(new SpectrumPoint{X=x,Y=y,Z=z,Phase=phase,SourceRows=new[]{row}});
                ExpandAutomaticPreviewRange(x,y);
            }
            else if(index>=0)view.Points.RemoveAt(index);
            if((index>=0)!=eligible)
            {
                visibleRows=view.Points.SelectMany(p=>p.SourceRows).Distinct().OrderBy(r=>r).ToArray();
                displayedPhases=visibleRows.Select(r=>(int)source[r,0]).Distinct().OrderByDescending(p=>p).ToArray();
            }
            return true;
        }

        void ExpandAutomaticPreviewRange(double x,double y)
        {
            var state=State;
            if(!state.HasAutomaticRange||state.X0!=state.AutoX0||state.X1!=state.AutoX1||
                state.Y0!=state.AutoY0||state.Y1!=state.AutoY1)return;
            // A candidate can re-enter the cable after being out of range. Its
            // previous exclusion may have reduced the automatic Q range, so
            // extend just the changed point's envelope without rescanning N rows.
            if(mode==4)
            {
                if(!frequencyTimeAutoRange)return;
                if(x>state.X1)state.X1=FrequencyTimeMaximum(x,1000000);
                if(y>state.Y1)state.Y1=FrequencyTimeMaximum(y,1000);
            }
            else
            {
                double padding=Math.Max(ChargeDisplayScale,Math.Abs(y)*.04);
                double low=Math.Floor((y-padding)/ChargeDisplayScale)*ChargeDisplayScale;
                double high=Math.Ceiling((y+padding)/ChargeDisplayScale)*ChargeDisplayScale;
                if(y<state.Y0&&Finite(low))state.Y0=low;
                if(y>state.Y1&&Finite(high))state.Y1=high;
            }
            state.AutoX0=state.X0;state.AutoX1=state.X1;state.AutoY0=state.Y0;state.AutoY1=state.Y1;
        }

        /// <summary>Sets a source-row inclusion mask. Null restores all rows. This never changes acceptance flags.</summary>
        public void SetReviewVisibility(bool[] mask)
        {
            if (mask != null && mask.Length != source.GetLength(0))
                throw new ArgumentException("The review mask must contain one flag per original source row.", "mask");
            if (mask == null && reviewVisibility == null || mask != null && reviewVisibility != null && mask.SequenceEqual(reviewVisibility)) return;
            reviewVisibility = mask == null ? null : (bool[])mask.Clone();
            reviewRevision++;
            reviewPreviewDirty=false;
            InvalidateAutomaticReviewRanges(); ClearDataViews(); Rebuild(false);
        }

        /// <summary>Task7 count-range semantics, applied only to each phase/bin of N-L. Never rejects source rows.</summary>
        public void SetReviewCountFilter(double[] values)
        {
            double[] next = values != null && values.Length >= 2 && Finite(values[0]) && Finite(values[1]) && values[0] != values[1]
                ? new[] { Math.Min(values[0], values[1]), Math.Max(values[0], values[1]) } : null;
            if (next == null && reviewCountRange == null || next != null && reviewCountRange != null && next.SequenceEqual(reviewCountRange)) return;
            reviewCountRange = next;
            reviewPreviewDirty=false;
            // Count filtering changes only N-L presentation, not Task15's input.
            InvalidateAutomaticReviewRanges(); ClearDataViews(); Rebuild(false);
        }

        /// <summary>Highlights an original source row without raising PdMapSelected or inventing a double-click.</summary>
        public void HighlightSourceRow(int sourceRow)
        {
            if (sourceRow < -1 || sourceRow >= source.GetLength(0)) throw new ArgumentOutOfRangeException("sourceRow");
            ClearHit();
            if (sourceRow >= 0 && visibleRows.Contains(sourceRow))
            {
                SelectedSourceRowIndex = sourceRow;
                SelectedPdMapId = source[sourceRow, 9];
                SelectedRecordId = source[sourceRow, 1];
                SelectedLocId = source[sourceRow, 8];
            }
            Invalidate();
        }

        bool IsReviewRowVisible(int sourceRow)
        { return reviewVisibility == null || sourceRow >= 0 && sourceRow < reviewVisibility.Length && reviewVisibility[sourceRow]; }

        void ClearReviewVisibilityForNewSource()
        { ClearRegionStatisticsForNewSource();ClearRegionDeletionHistory();reviewPreviewDirty=false;if (reviewVisibility != null) { reviewVisibility = null; reviewRevision++; } }

        internal void CancelReviewAcceptance(int[] rows)
        {
            foreach(int row in rows)if(row<0||row>=source.GetLength(0))throw new ArgumentOutOfRangeException("rows");
            foreach(int row in rows){source[row,35]=0;deleted.Remove(row);}
            reviewPreviewDirty=false;reviewRevision++;
            InvalidateAutomaticReviewRanges();ClearDataViews();Rebuild(false);
        }

        void InvalidateAutomaticReviewRanges()
        {
            // An automatic range must follow newly accepted/edited points. A
            // wheel-modified range is intentionally retained, as are all ROIs.
            // Mark inactive modes too: switching back must not revive a stale
            // upper limit from before the row was accepted.
            foreach(var state in states.Values)
                if(state.HasAutomaticRange&&state.X0==state.AutoX0&&state.X1==state.AutoX1&&
                    state.Y0==state.AutoY0&&state.Y1==state.AutoY1)state.ReviewRangeDirty=true;
        }
    }
}
