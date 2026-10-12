using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace Xiongxin.OWTS.Desktop
{
    internal partial class MainForm
    {
        string oemRequestedKey, oemLoadedKey;
        bool oemLoadRunning;
        Label oemWarning;
        internal Task OemLoadTask {get;private set;}
        void RequestOemBranding()
        {
            oemRequestedKey=(settings.LogoPath??"")+"\n"+(settings.IconPath??"");
            if(oemLoadRunning||oemLoadedKey==oemRequestedKey)return;
            OemLoadTask=LoadOemBrandingAsync();
        }
        async Task LoadOemBrandingAsync()
        {
            oemLoadRunning=true;
            try {
                while(!IsDisposed&&!Disposing&&oemLoadedKey!=oemRequestedKey) {
                    string key=oemRequestedKey,logo=settings.LogoPath,icon=settings.IconPath;
                    using(var result=await OemBranding.LoadAsync(logo,icon)) {
                        if(IsDisposed||Disposing)return;
                        if(key!=oemRequestedKey)continue;
                        HVX.Presentation.OemUiBranding.Publish(result.Icon,result.Logo);
                        Image previous=brandLogo.Image;brandLogo.Image=AppTheme.LoadLogo();if(previous!=null)previous.Dispose();
                        lastChromeMetricsKey=null;RequestResponsiveChrome();
                        // No modal interruption or repeated warning when navigating.
                        if(oemWarning==null){oemWarning=new Label{Name="OemResourceWarning",AutoSize=false,Dock=DockStyle.Bottom,Height=42,ForeColor=AppTheme.Warning,BackColor=AppTheme.Surface,Padding=new Padding(8,3,8,3)};headerPanel.Controls.Add(oemWarning);oemWarning.BringToFront();}
                        oemWarning.Text=String.IsNullOrEmpty(result.Warning)?"":IsChinese?result.Warning+" 请在设置中的品牌配置重新选择。":"Brand images could not be loaded. Neutral presentation is in use. Choose local files in Brand settings.";
                        oemWarning.Visible=oemWarning.Text.Length>0;
                        oemLoadedKey=key;
                    }
                }
            } catch(Exception error) {
                new Diagnostics().Error("Oem","Could not update all window branding.",error);
                if(!IsDisposed&&!Disposing&&oemWarning!=null){oemWarning.Text=IsChinese?"部分窗口显示未能更新，请关闭后重新打开。":"Some window images could not be updated. Close and reopen those windows.";oemWarning.Visible=true;}
            } finally{oemLoadRunning=false;}
        }
    }
}
