using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        sealed class PendingCableRefresh
        {
            internal string ModuleId, ProjectPath;
            internal bool WaitingForBusy;
        }
        readonly Dictionary<Control,ModuleAuditSubscription> cableSubscriptions = new Dictionary<Control,ModuleAuditSubscription>();
        readonly Dictionary<Control, string> cableControls = new Dictionary<Control, string>();
        readonly Dictionary<Control, PendingCableRefresh> pendingCableRefresh = new Dictionary<Control, PendingCableRefresh>();
        System.Windows.Forms.Timer cableRefreshTimer;
        readonly int cableUiThread = Thread.CurrentThread.ManagedThreadId;
        Control cableDispatcher;
        readonly List<Action> deferredCableNotifications=new List<Action>();
        bool refreshingCableStructure;
        Control refreshingCableControl;

        internal void ConnectCableStructure(string moduleId, Control control)
        {
            if (disposed || control == null || control.IsDisposed || cableControls.ContainsKey(control)) return;
            if (Thread.CurrentThread.ManagedThreadId != cableUiThread)
                throw new InvalidOperationException("Cable modules must be registered on the host UI thread.");
            if (cableDispatcher == null)
            {
                // A stable host-owned dispatcher also works before module HWNDs
                // exist. InvokeRequired on an uncreated control can return false
                // on a worker thread and must not be used as the ownership test.
                cableDispatcher = new Control();
                IntPtr handle = cableDispatcher.Handle;
            }
            cableControls.Add(control, moduleId);
            control.Disposed += CableControlDisposed;
            var subscription = ModuleAuditSubscription.TryCreate(control, "CableStructureChanged", () =>
            {
                if (disposed || control.IsDisposed || control==refreshingCableControl) return;
                // These module contracts expose their loaded project as a field-
                // backed string. Capture the commit's project before posting; a
                // later page switch must not turn it into a commit for another project.
                string committedPath = ReadCableProject(control);
                if (committedPath == null) return;
                DispatchCableRefresh(() => CableStructureCommitted(moduleId, control, committedPath));
            });
            if (subscription != null) cableSubscriptions.Add(control,subscription);
        }

        void DispatchCableRefresh(Action action)
        {
            if (disposed) return;
            if (Thread.CurrentThread.ManagedThreadId == cableUiThread)
            {if(refreshingCableStructure)deferredCableNotifications.Add(action);else action();return;}
            try
            {
                Control dispatcher = cableDispatcher;
                if (dispatcher != null && !dispatcher.IsDisposed)
                    dispatcher.BeginInvoke(new Action(() => { if (!disposed) DispatchCableRefresh(action); }));
            }
            catch (InvalidOperationException) { /* Host disposal invalidated the queued notification. */ }
        }

        void CableControlDisposed(object sender, EventArgs args)
        { var control=sender as Control;if(control!=null)DispatchCableRefresh(()=>RemoveCableControl(control)); }
        void RemoveCableControl(Control control)
        {
            ModuleAuditSubscription subscription;
            if(cableSubscriptions.TryGetValue(control,out subscription)){subscription.Dispose();cableSubscriptions.Remove(control);}
            control.Disposed-=CableControlDisposed;cableControls.Remove(control);pendingCableRefresh.Remove(control);
            UpdateCableRefreshTimer();
        }

        void CableStructureCommitted(string moduleId, Control source, string path)
        {
            if (disposed || source.IsDisposed || source==refreshingCableControl || !cableControls.ContainsKey(source)) return;
            foreach (var item in new List<KeyValuePair<Control,string>>(cableControls))
            {
                Control target = item.Key;
                if (target == source || target.IsDisposed || !SameCableProject(path, ReadCableProject(target))) continue;
                MethodInfo refresh = target.GetType().GetMethod("RefreshCableStructure", Type.EmptyTypes);
                if (refresh == null || refresh.ReturnType != typeof(bool)) continue;
                pendingCableRefresh[target] = new PendingCableRefresh { ModuleId = item.Value, ProjectPath = path };
                RefreshPendingCableStructure(target);
            }
            UpdateCableRefreshTimer();
        }

        internal void RefreshPendingCableStructure(Control target)
        {
            if (disposed || target == null) return;
            if (Thread.CurrentThread.ManagedThreadId != cableUiThread)
            { DispatchCableRefresh(() => RefreshPendingCableStructure(target)); return; }
            PendingCableRefresh pending;
            if (!pendingCableRefresh.TryGetValue(target, out pending)) return;
            if (target.IsDisposed || !SameCableProject(pending.ProjectPath, ReadCableProject(target)))
            { pendingCableRefresh.Remove(target); UpdateCableRefreshTimer(); return; }
            if (target==refreshingCableControl) return;
            if(refreshingCableStructure){DispatchCableRefresh(()=>RefreshPendingCableStructure(target));return;}
            pending.WaitingForBusy = LateBound.ReadBoolean(target, false, "IsBusy") ||
                LateBound.ReadBoolean(target, false, "IsAnalysisRunning") ||
                LateBound.ReadBoolean(target, false, "HasActiveHardwareSession");
            if (pending.WaitingForBusy) { UpdateCableRefreshTimer(); return; }
            try
            {
                // A refresh updates project metadata only. Never force a full
                // ProjectPath reload, discard unsaved fields or restart acquisition.
                refreshingCableStructure = true;
                refreshingCableControl = target;
                MethodInfo refresh = target.GetType().GetMethod("RefreshCableStructure", Type.EmptyTypes);
                PendingCableRefresh current;
                if (refresh != null && refresh.ReturnType==typeof(bool) && (bool)refresh.Invoke(target, null) &&
                    pendingCableRefresh.TryGetValue(target,out current) && Object.ReferenceEquals(current,pending)) pendingCableRefresh.Remove(target);
                // false while idle means a conflict. Retain it for next activation,
                // but do not repeatedly reload or overwrite the operator's edits.
            }
            catch (Exception error) { diagnostics.Error("CableStructure", "Metadata refresh deferred: " + pending.ModuleId, error); }
            finally { refreshingCableStructure = false;refreshingCableControl=null; }
            var deferred=deferredCableNotifications.ToArray();deferredCableNotifications.Clear();
            foreach(Action notification in deferred)if(!disposed)notification();
            UpdateCableRefreshTimer();
        }

        void UpdateCableRefreshTimer()
        {
            bool busy = false;
            foreach (var pair in pendingCableRefresh) if (!pair.Key.IsDisposed && pair.Value.WaitingForBusy) { busy = true; break; }
            if (!busy) { if (cableRefreshTimer != null) cableRefreshTimer.Stop(); return; }
            if (cableRefreshTimer == null)
            {
                cableRefreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
                cableRefreshTimer.Tick += delegate
                {
                    foreach (var pair in new List<KeyValuePair<Control,PendingCableRefresh>>(pendingCableRefresh))
                        if (pair.Value.WaitingForBusy) RefreshPendingCableStructure(pair.Key);
                    UpdateCableRefreshTimer();
                };
            }
            cableRefreshTimer.Start();
        }
        static string CanonicalCableProject(string path)
        { try { if(String.IsNullOrWhiteSpace(path))return null;string full=Path.GetFullPath(path);return full.Length==Path.GetPathRoot(full).Length?full:full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch { return null; } }
        static string ReadCableProject(Control control)
        {
            // Unlike audit metadata, sync may never fall back to the host's
            // previous project when a page reports that it has been unloaded.
            try
            {
                PropertyInfo property=control.GetType().GetProperty("ProjectPath")??control.GetType().GetProperty("CurrentProjectPath");
                return property==null||!property.CanRead?null:CanonicalCableProject(Convert.ToString(property.GetValue(control,null)));
            }
            catch { return null; }
        }
        static bool SameCableProject(string left, string right)
        { return left != null && String.Equals(left, CanonicalCableProject(right), StringComparison.OrdinalIgnoreCase); }
        void DisposeCableStructureSync()
        {
            if (cableRefreshTimer != null) { cableRefreshTimer.Stop(); cableRefreshTimer.Dispose(); cableRefreshTimer = null; }
            foreach (var subscription in cableSubscriptions.Values) subscription.Dispose();
            foreach (Control control in cableControls.Keys)control.Disposed-=CableControlDisposed;
            cableSubscriptions.Clear(); cableControls.Clear(); pendingCableRefresh.Clear();
            deferredCableNotifications.Clear();
            if(cableDispatcher!=null){cableDispatcher.Dispose();cableDispatcher=null;}
        }
    }
}
