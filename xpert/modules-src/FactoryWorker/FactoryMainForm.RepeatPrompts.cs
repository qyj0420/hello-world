using System;
using System.Drawing;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Display preference only, never an acknowledgement or output permission.
    internal static class FactoryOptionalPromptPolicy
    {
        internal static bool CanSuppressRange(FactoryResult round,LocationRangeResult q)
        {
            if(round==null || round.Kind!=FactoryRunKind.Pressure || round.Capture==null || q==null ||
                !q.WindowValid || q.RecommendedIndex<0 || q.RecommendedIndex>=BenchSettings.Ranges.Length ||
                q.LocationClippedSamples>0 || q.AtMaximumAndClipped || q.MaximumRangeInsufficient ||
                FactoryTransientRangePolicy.BlocksForOverflow(q,round.Capture.Settings.RangeIndex) ||
                FactoryTransientRangePolicy.BlocksDownrange(q))return false;
            var context=FactoryRangeReview.CreateMeasurementContext(round,q);
            return !RangeDecisionPolicy.IsMaximumRangeRisk(context) && !RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context);
        }
        internal static bool NeedsRangePrompt(FactoryResult round,LocationRangeResult q,bool repeat)
        {
            // Keep the independent, previously authorized silent range policy.
            // Invalid windows and mandatory warnings can never be hidden.
            return !q.WindowValid || FactoryNextRangeReview.NeedsPrompt(round,q,false) ||
                FactoryNextRangeReview.NeedsPrompt(round,q,round.Settings.Input.AutoRange) && (repeat || !CanSuppressRange(round,q));
        }
    }
    internal sealed class FactoryOptionalNoticeDialog : Form
    {
        internal readonly CheckBox DoNotShow=new CheckBox {Text="不再显示可选提示（可在主界面恢复）",AutoSize=true};
        internal FactoryOptionalNoticeDialog(Icon icon,string title,string message)
        {
            Icon=icon;Text=title;Font=new Font("Microsoft YaHei UI",10);ClientSize=new Size(560,235);
            StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=3};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text=message},0,0);layout.Controls.Add(DoNotShow,0,1);
            var close=new Button {Text="知道了",Width=92,Height=32,Anchor=AnchorStyles.Right,DialogResult=DialogResult.OK};
            layout.Controls.Add(close,0,2);Controls.Add(layout);AcceptButton=CancelButton=close;
        }
    }
    internal sealed partial class FactoryMainForm
    {
        private readonly CheckBox repeatPrompts=new CheckBox {Text="重复普通提示",Checked=true,AutoSize=true,Margin=new Padding(7,5,0,0)};
        private void ShowOptionalNotice(IWin32Window owner,string title,string message)
        {
            status.Text=message;
            diagnostics.AppendText("OPTIONAL_NOTICE "+DateTime.Now.ToString("O")+" ["+title+"] "+message+Environment.NewLine);
            if(!repeatPrompts.Checked || smokeMode)return;
            using(var dialog=new FactoryOptionalNoticeDialog(Icon,title,message))
            {dialog.ShowDialog(owner);if(dialog.DoNotShow.Checked)repeatPrompts.Checked=false;}
        }
    }
}
