using System;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private void ProjectExitRequested()
        {
            if(InvokeRequired){BeginInvoke(new Action(ProjectExitRequested));return;}
            var creator=activeModule;
            if(creator==null||creator.Tag as string!=ModuleHost.ProjectCreation)return;
            // Defer to let the control finish raising its event before detaching it.
            BeginInvoke(new Action(()=>{
                if(activeModule!=creator)return;
                TryReleaseProjectContext(true);
            }));
        }
    }
}
