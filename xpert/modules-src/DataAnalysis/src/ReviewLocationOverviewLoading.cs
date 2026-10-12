using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        readonly SemaphoreSlim overviewReaderGate=new SemaphoreSlim(1,1);
        CancellationTokenSource overviewLoadCancellation;
        Task<bool> overviewLoadTask;
        int overviewLoadGeneration;
        bool overviewLoading;
        string overviewLoadingPath="";
        Panel overviewLoadingView;
        Label overviewLoadingText;
        Control overviewOptions;
        [Browsable(false)] public bool IsLocationOverviewLoading {get{return overviewLoading;}}

        void InitializeLocationOverviewLoading(Control options)
        {
            overviewOptions=options;
            overviewLoadingView=new Panel{Name="ReviewLocationLoading",Dock=DockStyle.Fill,BackColor=Color.White,Visible=false};
            overviewLoadingText=new Label{Name="ReviewLocationLoadingText",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=ReviewTheme.Ink};
            var progress=new ProgressBar{Name="ReviewLocationLoadingProgress",Dock=DockStyle.Bottom,Height=6,Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=30};
            overviewLoadingView.Controls.Add(overviewLoadingText);overviewLoadingView.Controls.Add(progress);overviewHost.Controls.Add(overviewLoadingView);
            UpdateLocationOverviewLoadingText();
        }
        string LocationOverviewLoadingMessage {get{return L("Loading waveform…","正在读取波形…");}}
        void UpdateLocationOverviewLoadingText()
        {if(overviewLoadingText!=null)overviewLoadingText.Text=LocationOverviewLoadingMessage;}
        void CancelLocationOverviewLoad()
        {
            bool wasLoading=overviewLoading;
            overviewLoadGeneration++;
            var cancellation=overviewLoadCancellation;overviewLoadCancellation=null;
            if(cancellation!=null){cancellation.Cancel();cancellation.Dispose();}
            overviewLoadTask=null;overviewLoadingPath="";overviewLoading=false;
            if(overviewHost!=null&&!overviewHost.IsDisposed)overviewHost.UseWaitCursor=false;
            if(overviewLoadingView!=null&&!overviewLoadingView.IsDisposed)overviewLoadingView.Visible=false;
            if(locationOverview!=null&&!locationOverview.IsDisposed)locationOverview.Enabled=true;
            if(overviewOptions!=null&&!overviewOptions.IsDisposed)overviewOptions.Enabled=true;
            if(wasLoading&&!IsDisposed&&!Disposing&&String.Equals(operationStatus,LocationOverviewLoadingMessage,StringComparison.Ordinal))SetReviewOperationStatus("");
        }
        Task<bool> BeginLocationOverviewLoad(string path)
        {
            if(IsDisposed||Disposing||analysisBusy||String.IsNullOrEmpty(path))return Task.FromResult(false);
            if(overviewLoading&&String.Equals(path,overviewLoadingPath,StringComparison.OrdinalIgnoreCase)&&overviewLoadTask!=null)return overviewLoadTask;
            if(!overviewLoading&&String.Equals(path,overviewPath,StringComparison.OrdinalIgnoreCase)&&locationOverview.SampleCount>0)
                return Task.FromResult(ShowLocationOverview(path));
            CancelLocationOverviewLoad();
            string root;
            try{root=File.Exists(projectPath)?Path.GetDirectoryName(Path.GetFullPath(projectPath)):projectPath;}
            catch(Exception ex){if(IsFatalHostException(ex))throw;LastError=ex.ToString();SetReviewOperationStatus(L("Unable to load the waveform. Select the file again to retry.","无法读取波形，请重新选择此文件重试。"));return Task.FromResult(false);}
            int generation=overviewLoadGeneration;string project=projectPath;
            overviewLoadCancellation=new CancellationTokenSource();var token=overviewLoadCancellation.Token;
            overviewLoading=true;overviewLoadingPath=path;overviewPath=path;locationOverview.ClearData();locationOverviewMode=true;LastError="";
            UpdateLocationOverviewLoadingText();overviewLoadingView.Visible=true;overviewHost.UseWaitCursor=true;
            RefreshReviewUi();overviewLoadingView.BringToFront();SetReviewOperationStatus(LocationOverviewLoadingMessage);
            overviewLoadTask=LoadLocationOverviewAsync(path,root,project,generation,token);return overviewLoadTask;
        }
        bool IsCurrentOverviewLoad(int generation,string project,CancellationToken token)
        {return !IsDisposed&&!Disposing&&generation==overviewLoadGeneration&&String.Equals(project,projectPath,StringComparison.OrdinalIgnoreCase)&&!token.IsCancellationRequested;}
        async Task<bool> LoadLocationOverviewAsync(string path,string root,string project,int generation,CancellationToken token)
        {
            try
            {
                // Yield immediately so the loading state can paint even when
                // the reader gate is free. Only one full LOC preview is read at
                // a time; superseded queued requests cancel without allocation.
                await Task.Yield();
                await overviewReaderGate.WaitAsync(token);
                ReviewLocationFileData data;
                try{
                    data=await Task.Run(()=>{
                        token.ThrowIfCancellationRequested();
                        var result=ReviewCapacitorDemInput.IsDem(path)?ReviewCapacitorDemInput.Display(path,root):ReviewLocationFileReader.ReadCancellable(path,root,token);
                        token.ThrowIfCancellationRequested();return result;
                    },token);
                }finally{overviewReaderGate.Release();}
                if(!IsCurrentOverviewLoad(generation,project,token))return false;
                locationOverview.ApplyFileData(data);overviewPath=path;RefreshOverviewMeasurementGeometry(path);
                SyncThresholdUi();RefreshReviewUi();SetReviewOperationStatus(L("Waveform loaded.","波形已加载。"));return true;
            }
            catch(OperationCanceledException){if(IsCurrentOverviewLoad(generation,project,CancellationToken.None))SetReviewOperationStatus(L("Loading cancelled.","已取消读取。"));return false;}
            catch(Exception ex){
                if(IsFatalHostException(ex))throw;
                if(IsCurrentOverviewLoad(generation,project,token)){
                    locationOverview.ClearData();LastError=ex.ToString();
                    SetReviewOperationStatus(L("Unable to load the waveform. Select the file again to retry.","无法读取波形，请重新选择此文件重试。"));
                }
                return false;
            }
            finally{
                if(!IsDisposed&&!Disposing&&generation==overviewLoadGeneration){
                    overviewLoading=false;overviewLoadingPath="";overviewLoadTask=null;
                    var cancellation=overviewLoadCancellation;overviewLoadCancellation=null;if(cancellation!=null)cancellation.Dispose();
                    overviewHost.UseWaitCursor=false;overviewLoadingView.Visible=false;SyncThresholdUi();RefreshReviewUi();
                }
            }
        }
    }
}
