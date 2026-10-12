using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        internal bool StandalonePressureMode { get { return pressureStandalone; } }

        private void InitializeStandalonePressureChrome()
        {
            if (!pressureStandalone) return;
            standaloneLoadProjectButton = AppTheme.Button("加载项目", false);
            standaloneLoadProjectButton.Name = "StandaloneLoadProject";
            standaloneLoadProjectButton.Click += delegate { ChooseStandalonePressureProject(); };
            headerActions.Controls.Add(standaloneLoadProjectButton);
            standaloneDebugButton=AppTheme.Button("调试信息",false);
            standaloneDebugButton.Name="StandaloneDebug";
            standaloneDebugButton.Click+=delegate {
                if(pressureDebugWindow==null||pressureDebugWindow.IsDisposed)
                    pressureDebugWindow=new PressureDebugForm(new Diagnostics(),()=>
                        "项目："+state.ProjectPath+Environment.NewLine+
                        "连接："+state.Hardware.StatusCode+"　急停："+LateBound.ReadString(activeModule,"EmergencyStopStatusCode")+
                        "　阶段："+LateBound.ReadString(activeModule,"PressureStateCode")+" / "+LateBound.ReadString(activeModule,"PostProcessStateCode")+
                        Environment.NewLine+"当前提示："+LateBound.ReadString(activeModule,"PressureActionFullMessage")+
                        Environment.NewLine+"处理错误："+LateBound.ReadString(activeModule,"LastPostProcessError")+
                        Environment.NewLine+"最近错误："+LateBound.ReadString(activeModule,"LastError"),Icon);
                if(!pressureDebugWindow.Visible)pressureDebugWindow.Show(this);
                pressureDebugWindow.BringToFront();
            };
            headerActions.Controls.Add(standaloneDebugButton);
            ApplyStandalonePressureText();
            ApplyResponsiveChrome();
        }

        private void ApplyStandalonePressureText()
        {
            if (!pressureStandalone) return;
            const string revision="20261008-03";
            if(Text.IndexOf(revision,StringComparison.Ordinal)<0)
                Text+=" · "+(IsChinese?"独立加压内测 ":"Pressure internal test ")+revision;
            if (standaloneLoadProjectButton != null)
                standaloneLoadProjectButton.Text = IsChinese ? "加载项目" : "Load project";
            if(standaloneDebugButton!=null)standaloneDebugButton.Text=IsChinese?"调试信息":"Diagnostics";
            if (activeModule != null) settingsButton.Text = IsChinese ? "设备与设置" : "Device / settings";
            welcomeTitle.Text = IsChinese ? "加压测试" : "Pressure test";
            welcomeSubtitle.Text = IsChinese ? "连接设备并加载项目后进入测试；不会自动开始加压。" :
                "Connect the device and load a project to open the test. Voltage output never starts automatically.";
        }

        private async void ChooseStandalonePressureProject()
        {
            if (!pressureStandalone || !CanLeaveActivePressure() || hardwareRefreshInProgress ||
                hardwareDisconnectInProgress || settingsDialogActive || firstBindingInProgress) return;
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = IsChinese ? "选择含 ITEM.in、CABLE.in 和校准数据的项目文件夹" :
                    "Select the project folder containing ITEM.in, CABLE.in and calibration data";
                dialog.ShowNewFolderButton = false;
                if (state.ProjectLoaded && Directory.Exists(state.ProjectPath)) dialog.SelectedPath = state.ProjectPath;
                if (dialog.ShowDialog(this) == DialogResult.OK) await LoadStandalonePressureProjectAsync(dialog.SelectedPath);
            }
        }

        // Same project loader, calibration matching, input supervisor and worker
        // as the integrated pressure page. No separate voltage-control algorithm.
        internal bool LoadStandalonePressureProject(string path)
        {
            Task<bool> load=LoadStandalonePressureProjectAsync(path);
            return load.Status==TaskStatus.RanToCompletion&&load.Result;
        }
        internal async Task<bool> LoadStandalonePressureProjectAsync(string path)
        {
            if (!pressureStandalone || pressureExitInProgress || !CanLeaveActivePressure() || hardwareRefreshInProgress ||
                hardwareDisconnectInProgress || settingsDialogActive || firstBindingInProgress) return false;
            string validation;
            var candidate = new ApplicationState();
            if (!candidate.TryLoadProject(path, out validation))
            {
                if (!verificationMode) ShowRequirement(validation);
                return false; // Do not discard the current page for an invalid folder.
            }
            if(!await ShowHomeAsync())return false;
            if (activeModule != null || !LoadProject(path, !verificationMode)) return false;
            if (!verificationMode && CanOpenLiveCalibration()) OpenModule(ModuleHost.PressureTest);
            return true;
        }
    }
}
