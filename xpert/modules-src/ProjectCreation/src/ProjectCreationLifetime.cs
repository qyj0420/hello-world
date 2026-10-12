using System;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        // Commands retain their synchronous UI-thread contract. Polling never
        // uses this path; the host must not block its UI thread on a worker.
        bool InvokeCommandSafely(Func<bool> command)
        {
            try { return (bool)Invoke(command); }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { if (IsDisposed || Disposing) return false; throw; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                // Components and hidden editors are not owned by Controls.
                if (validationProvider != null)
                {
                    validationProvider.Dispose();
                    validationProvider = null;
                }
                DisposeReferenceMenus();
                if (cableGrid != null && cableGrid.ContextMenuStrip != null)
                {
                    ContextMenuStrip menu = cableGrid.ContextMenuStrip;
                    cableGrid.ContextMenuStrip = null;
                    menu.Dispose();
                }
                if (languageCombo != null) languageCombo.Dispose();
                if (editJointButton != null) editJointButton.Dispose();
                if (deleteJointButton != null) deleteJointButton.Dispose();
                ProjectSaved = null;
                ProjectLoaded = null;
                ProjectCleared = null;
                ProjectManagerRequested = null;
                ExitRequested = null;
                HardwareAcquisitionRequested = null;
                EnvironmentDetailsExpandedChanged = null;
                LanguageChanged = null;
                ValidationChanged = null;
                WeatherAcquisitionRequested = null;
                WeatherUpdated = null;
                WeatherRefreshFailed = null;
            }
            base.Dispose(disposing);
        }
    }
}
