using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        internal Control[] LoadedProjectControls()
        {
            return new List<Control>(controls.Values).ToArray();
        }

        internal void LogProjectReleaseError(string message, Exception error)
        {
            diagnostics.Error("ProjectRelease", message, error);
        }

        internal bool HasExternalCalibrationProcess
        {
            get { return calibrationAcquisitionProcess != null && !calibrationAcquisitionProcess.HasExited; }
        }

        internal void PauseProjectRequestsForRelease()
        {
            // Do not flush a pending activation while an explicit release is being
            // committed. Normal Back navigation continues to use its old flush.
            if (projectPollTimer != null) projectPollTimer.Stop();
            projectManagerControl = null;
            projectCreationControl = null;
        }

        internal void ReleaseLoadedProjectControls(Control activeControl)
        {
            PauseProjectRequestsForRelease();
            // No disk deletion and no adapter or hardware commands. Only controls
            // already validated as idle are detached; the next entry loads afresh.
            var pending = new HashSet<Control>(LoadedProjectControls());
            if (activeControl != null) pending.Add(activeControl);
            var failures = new List<Exception>();
            foreach (Control control in pending)
            {
                if (control == null || control.IsDisposed) continue;
                try { control.Dispose(); }
                catch (Exception error)
                {
                    diagnostics.Error("ProjectRelease", "A module reported a cleanup error.", error);
                    // A late Disposed handler can throw after the control was
                    // destroyed. Continue detaching all other idle controls. A
                    // still-live control, however, keeps the project open.
                    if (!control.IsDisposed) failures.Add(error);
                }
                if (!control.IsDisposed && failures.Count == 0)
                    failures.Add(new InvalidOperationException("A project module could not be disposed."));
            }
            if (failures.Count > 0) throw new AggregateException("Some project modules are still live; the project context was retained.", failures);
            controls.Clear();
            contextSignatures.Clear();
            appliedProjectPaths.Clear();
            lastLoadRequestSequence = lastProjectSavedSequence = 0;
            warnedLoadRequestSequence = warnedProjectSavedSequence = 0;
        }
    }
}
