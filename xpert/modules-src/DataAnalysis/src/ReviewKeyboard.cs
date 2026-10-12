using System;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        ToolTip reviewKeyboardHints;

        protected override bool ProcessCmdKey(ref Message message,Keys keyData)
        {
            if(rangeDialogBusy||locationOverviewMode||analysisTree!=null&&analysisTree.ContainsFocus)return base.ProcessCmdKey(ref message,keyData);
            bool command=keyData==Keys.Enter||keyData==Keys.Left||keyData==Keys.Right||keyData==Keys.Down||keyData==Keys.Up;
            if(!command||!ContainsFocus||ReviewEditorHasFocus(this))return base.ProcessCmdKey(ref message,keyData);

            if(keyData==Keys.Enter){if(CanAccept)AcceptCurrent();}
            else if(keyData==Keys.Left)PreviousPdMap();
            else if(keyData==Keys.Right)NextPdMap();
            else if(keyData==Keys.Up){if(CanDelete)DeleteCurrent();}
            else if(CurrentAccepted)NextPdMap();
            else if(CanAccept)
            {
                int sourceRow=selected;var sourceMatrix=working;
                // A synchronous host callback may replace data, cancel acceptance
                // or change selection. Do not advance another pulse in that case.
                if(AcceptCurrent()&&ReferenceEquals(sourceMatrix,working)&&selected==sourceRow&&CurrentAccepted)NextPdMap();
            }
            return true;
        }

        static bool ReviewEditorHasFocus(Control root)
        {
            if(root is TextBoxBase||root is UpDownBase||root is ComboBox||root is TrackBar)return root.ContainsFocus;
            foreach(Control child in root.Controls)
                if(child.ContainsFocus&&ReviewEditorHasFocus(child))return true;
            return false;
        }

        void InitializeReviewKeyboardHints()
        {
            reviewKeyboardHints=new ToolTip { AutoPopDelay=6000,InitialDelay=500,ReshowDelay=100 };
            Disposed+=(s,e)=>reviewKeyboardHints.Dispose();
        }

        void UpdateReviewKeyboardHints()
        {
            if(reviewKeyboardHints==null)return;
            string previous=L("Previous PDmap (Left arrow)","上一个 PDmap（←）");
            string next=L("Next PDmap (Right arrow)","下一个 PDmap（→）");
            string accept=L("Accept (Enter); accept and next (Down arrow)","接受（Enter）；接受并下一个（↓）");
            string remove=L("Remove from accepted (Up arrow); source data is retained","取消接受（↑），保留原始数据");
            reviewKeyboardHints.SetToolTip(previousButton,previous);previousButton.AccessibleDescription=previous;
            reviewKeyboardHints.SetToolTip(nextButton,next);nextButton.AccessibleDescription=next;
            reviewKeyboardHints.SetToolTip(acceptButton,accept);acceptButton.AccessibleDescription=accept;
            reviewKeyboardHints.SetToolTip(deleteButton,remove);deleteButton.AccessibleDescription=remove;
        }
    }
}
