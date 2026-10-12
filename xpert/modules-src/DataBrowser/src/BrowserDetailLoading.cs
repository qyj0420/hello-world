using System.Drawing;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        readonly TableLayoutPanel detailLoadingView=new TableLayoutPanel();
        readonly Label detailLoadingText=new Label();
        readonly ProgressBar detailLoadingProgress=new ProgressBar();
        int detailLoadingSelection=-1,detailLoadingProject=-1;

        void InitializeDetailLoadingView()
        {
            detailLoadingView.Name="WaveformLoadingView";
            detailLoadingView.Dock=DockStyle.Fill;
            detailLoadingView.BackColor=BrowserTheme.Surface;
            detailLoadingView.ColumnCount=3;detailLoadingView.RowCount=4;
            detailLoadingView.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            detailLoadingView.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,300));
            detailLoadingView.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            detailLoadingView.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            detailLoadingView.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailLoadingView.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
            detailLoadingView.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            detailLoadingText.Name="WaveformLoadingText";
            detailLoadingText.Dock=DockStyle.Fill;detailLoadingText.AutoSize=true;
            detailLoadingText.MinimumSize=new Size(0,44);
            detailLoadingText.TextAlign=ContentAlignment.MiddleCenter;
            detailLoadingText.ForeColor=BrowserTheme.Ink;
            detailLoadingProgress.Name="WaveformLoadingProgress";
            detailLoadingProgress.Dock=DockStyle.Fill;
            detailLoadingProgress.Style=ProgressBarStyle.Marquee;
            detailLoadingProgress.MarqueeAnimationSpeed=0;
            detailLoadingProgress.Margin=new Padding(30,2,30,2);
            detailLoadingView.Controls.Add(detailLoadingText,1,1);
            detailLoadingView.Controls.Add(detailLoadingProgress,1,2);
            detailLoadingView.Visible=false;
        }

        void ApplyDetailLoadingLanguage()
        {
            string message=T("正在读取波形…","Loading waveform…");
            detailLoadingText.Text=message;
            detailLoadingView.AccessibleName=message;
            detailLoadingProgress.AccessibleName=message;
            if(detailLoadingSelection==selectionGeneration&&detailLoadingProject==projectGeneration&&loadingWave)
            {information.Text=message;SetStatus(message);}
        }

        void ShowDetailLoading(int selection,int projectId)
        {
            detailLoadingSelection=selection;detailLoadingProject=projectId;
            ApplyDetailLoadingLanguage();
            detail.Controls.Clear();detail.Controls.Add(detailLoadingView);
            detailLoadingView.Visible=true;
            detailLoadingProgress.MarqueeAnimationSpeed=30;
            detail.UseWaitCursor=true;
        }

        void HideDetailLoading(int selection,int projectId)
        {
            // A stale completion must never end the next file's loading feedback.
            if(selection!=selectionGeneration||projectId!=projectGeneration||
                selection!=detailLoadingSelection||projectId!=detailLoadingProject)return;
            HideDetailLoading();
        }

        void HideDetailLoading()
        {
            detailLoadingSelection=detailLoadingProject=-1;
            detail.UseWaitCursor=false;
            if(detailLoadingView.IsDisposed)return;
            detailLoadingProgress.MarqueeAnimationSpeed=0;
            detailLoadingView.Visible=false;
            if(detailLoadingView.Parent==detail)detail.Controls.Remove(detailLoadingView);
        }

        void DisposeDetailLoadingView()
        {HideDetailLoading();if(!detailLoadingView.IsDisposed)detailLoadingView.Dispose();}
    }
}
