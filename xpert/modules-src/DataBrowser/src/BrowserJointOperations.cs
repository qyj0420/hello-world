using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HVX.ProjectCables;

namespace HVX.Controls
{
    // Control.OnEnabledChanged synchronously calls Update for a user-painted
    // parent. Defer only that parent's repaint; base still changes native input
    // states and propagates every child/event. This does not remove cold-frame
    // stalls elsewhere in the message loop.
    internal sealed class DeferredBrowserActionsPanel : System.Windows.Forms.FlowLayoutPanel
    {
        protected override void OnEnabledChanged(EventArgs e)
        {
            bool userPaint=GetStyle(System.Windows.Forms.ControlStyles.UserPaint);
            try {
                if(userPaint)SetStyle(System.Windows.Forms.ControlStyles.UserPaint,false);
                base.OnEnabledChanged(e);
            } finally {
                SetStyle(System.Windows.Forms.ControlStyles.UserPaint,userPaint);
                if(!IsDisposed&&!Disposing)Invalidate(false);
            }
        }
    }

    public sealed partial class ProjectDataBrowserControl
    {
        // Capture control state once on the UI thread. Workers see file paths,
        // immutable geometry/source hashes and cancellation, never a Control.
        sealed class JointEvidenceContext
        {
            internal string Root;
            internal int ProjectGeneration, SelectionGeneration;
            internal bool English;
            internal CalibrationTraceGeometry Geometry;
            internal Dictionary<string,string> Sources;
        }
        JointEvidenceContext CaptureJointEvidence()
        {
            if(InvokeRequired)throw new InvalidOperationException("Start joint editing on the UI thread.");
            EnsureJointUiContext();
            if(project==null||IsBusy||editing)throw new InvalidOperationException(T("请先完成当前操作。","Finish the current operation first."));
            bool mean=SelectedNodeKind=="cal-comparison";
            if(mean&&!CanUseComparisonGeometry())throw new InvalidOperationException("Recalculate the current mean before editing joints.");
            return new JointEvidenceContext {Root=projectPath,ProjectGeneration=projectGeneration,SelectionGeneration=selectionGeneration,English=english,
                Geometry=mean?comparisonGeometry:null,Sources=mean?new Dictionary<string,string>(comparisonMeanSourceHashes,StringComparer.OrdinalIgnoreCase):null};
        }
        static void EnsureJointUiContext()
        {
            // Interop/console hosts can call a control on its UI thread without
            // an ambient context. Install the same dispatcher used by WinForms
            // before awaiting; never resume control access on a pool thread.
            if(SynchronizationContext.Current==null||SynchronizationContext.Current.GetType()==typeof(SynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
        }
        bool JointEvidenceIsCurrent(JointEvidenceContext context)
        {
            return !IsDisposed&&!Disposing&&context.ProjectGeneration==projectGeneration&&context.SelectionGeneration==selectionGeneration&&
                String.Equals(context.Root,projectPath,StringComparison.OrdinalIgnoreCase)&&
                (context.Geometry==null||ReferenceEquals(context.Geometry,comparisonGeometry));
        }
        static CableJointSnapshot ReadJointEvidence(JointEvidenceContext context,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(context.Sources!=null)ValidateComparisonMeanSourcesCore(context.Root,context.Sources,token,context.English);
            var snapshot=CableJointStore.Read(context.Root);
            if(context.Geometry!=null)ComparisonProjectBlocks(snapshot,context.Geometry);
            token.ThrowIfCancellationRequested();return snapshot;
        }
        internal async Task<CableJointSnapshot> PrepareJointEditorSnapshotAsync(CancellationToken cancellation)
        {
            var context=CaptureJointEvidence();
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,projectCancellation.Token,waveCancellation.Token))
            {
                loadingWave=true;findComparisonJointsButton.Enabled=false;
                SetStatus(T("正在校验接头编辑所需来源…","Validating sources for joint editing…"));
                try {
                    var snapshot=await Task.Run(()=>ReadJointEvidence(context,linked.Token),linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if(!JointEvidenceIsCurrent(context))throw new OperationCanceledException("Project or selection changed.");
                    return snapshot;
                } finally {
                    if(JointEvidenceIsCurrent(context)){loadingWave=false;findComparisonJointsButton.Enabled=CanUseComparisonGeometry();}
                }
            }
        }
        internal async Task CommitJointDialogAsync(CableJointPlan plan,string operation,string root,int generation,int selection,CancellationToken cancellation)
        {
            var context=CaptureJointEvidence();
            if(generation!=context.ProjectGeneration||selection!=context.SelectionGeneration||!String.Equals(root,context.Root,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(T("项目或记录已改变，请重新打开接头编辑。","Project or selection changed; reopen the joint editor."));
            if(plan==null||!String.Equals(Path.GetDirectoryName(plan.CablePath),Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Joint plan does not belong to the current project.");
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,projectCancellation.Token,waveCancellation.Token))
            {
                LastChangedFileCount=0;LastBackupDirectory="";saving=true;
                bool treeEnabled=tree.Enabled,actionsEnabled=actions.Enabled;tree.Enabled=actions.Enabled=false;
                SetStatus(T("正在校验来源并备份保存接头…","Validating sources, backing up and saving joint…"));
                try {
                    // Full evidence validation is deliberately retained at the
                    // confirmation boundary. It can read large formal/raw files.
                    var committed=await Task.Run(()=> {
                        ReadJointEvidence(context,linked.Token);
                        linked.Token.ThrowIfCancellationRequested();
                        // Commit point: the atomic CABLE transaction owns failure/
                        // rollback from here. A later Cancel must not claim no write.
                        return CableJointStore.CommitWithResult(plan);
                    },linked.Token);
                    CableJointSnapshot refreshed=null;string refreshFailure="";
                    try {refreshed=await Task.Run(()=>CableJointStore.Read(root));}
                    catch(Exception ex){refreshFailure=ex.Message;}
                    if(!IsDisposed&&!Disposing)FinishJointDialogCommit(plan,operation,root,committed,refreshed,refreshFailure);
                } finally {
                    if(!IsDisposed&&!Disposing){saving=false;tree.Enabled=treeEnabled;actions.Enabled=actionsEnabled;findComparisonJointsButton.Enabled=CanUseComparisonGeometry();}
                }
            }
        }
    }
}
