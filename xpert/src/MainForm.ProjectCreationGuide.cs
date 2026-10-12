using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        bool projectGuideOpen;
        void QueueProjectCreationGuide(string id,Control control)
        {
            if(verificationMode||projectGuideOpen||state.ProjectLoaded||!settings.ShowProjectCreationGuide||id!=ModuleHost.ProjectCreation)return;
            BeginInvoke(new Action(delegate
            {
                if(IsDisposed||Disposing||activeModule!=control||state.ProjectLoaded||!settings.ShowProjectCreationGuide||projectGuideOpen)return;
                var capture=control.GetType().GetMethod("CreateProjectGuidePages",BindingFlags.Public|BindingFlags.Instance);
                if(capture==null)return;
                projectGuideOpen=true;
                try
                {
                    using(var dialog=new ProjectCreationGuideDialog((Image[])capture.Invoke(control,null),AppLanguages.IndexOf(settings.LanguageCode),Font))
                    {
                        dialog.ShowDialog(this);
                        if(dialog.DoNotShowAgain)
                        {
                            SaveProjectGuidePreference(AppPaths.SettingsFile,false);
                            settings.ShowProjectCreationGuide=false;
                        }
                    }
                }
                catch(Exception error)
                {
                    new Diagnostics().Error("ProjectCreationGuide", "The project guide or its reminder preference could not be completed.", error);
                    MessageBox.Show(this, Localize(IsChinese
                        ? "操作引导未能完成。可继续创建项目；如需引导或调整提醒，请重新打开新建项目或常规设置。"
                        : "The project guide could not be completed. You can continue creating the project. To retry the guide or change reminders, reopen New project or General settings."),
                        Localize(IsChinese?"操作引导":"Project guide"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally {projectGuideOpen=false;}
            }));
        }
        internal static void SaveProjectGuidePreference(string path,bool enabled)
        {
            bool absent=!File.Exists(path);
            string digest=absent?null:AppSettings.FileDigest(path);
            AppSettings latest;string message;
            if(!AppSettings.TryLoad(path,out latest,out message))throw new InvalidDataException(message);
            latest.ShowProjectCreationGuide=enabled;
            if(!latest.TrySaveIfUnchanged(path,digest,absent))
                throw new IOException("Configuration changed while saving the guide preference; reopen General settings to retry.");
        }
    }
}
