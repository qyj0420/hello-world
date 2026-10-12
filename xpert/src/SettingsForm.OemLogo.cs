using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal partial class SettingsForm
    {
        Label oemLogoInfo;
        OemLogoGeometry oemLogoGeometry;
        Timer oemLogoPreviewTimer;
        int oemLogoPreviewGeneration;
        bool oemLogoPreviewFailed;
        void AddOemLogoInfo(TableLayoutPanel grid)
        {
            oemLogoInfo=CreateNote("");oemLogoInfo.Name="OemLogoDimensions";
            grid.SizeChanged+=delegate{oemLogoInfo.MaximumSize=new Size(Math.Max(160,grid.ClientSize.Width-20),0);};
            AddWideRow(grid,oemLogoInfo);
            oemLogoPreviewTimer=new Timer{Interval=250};
            oemLogoPreviewTimer.Tick+=async delegate
            {
                oemLogoPreviewTimer.Stop();int generation=++oemLogoPreviewGeneration;string path=logoPath.Text;
                if(String.IsNullOrWhiteSpace(path)){oemLogoGeometry=null;oemLogoPreviewFailed=false;RefreshOemLogoInfo();return;}
                using(var result=await OemBranding.LoadAsync(path,null))
                {
                    if(IsDisposed||Disposing||generation!=oemLogoPreviewGeneration||logoPath.Text!=path)return;
                    oemLogoGeometry=result.LogoGeometry;oemLogoPreviewFailed=result.Warning.Length>0;RefreshOemLogoInfo();
                }
            };
            logoPath.TextChanged+=delegate{++oemLogoPreviewGeneration;oemLogoPreviewTimer.Stop();oemLogoPreviewTimer.Start();};
            Disposed+=delegate{++oemLogoPreviewGeneration;oemLogoPreviewTimer.Stop();oemLogoPreviewTimer.Dispose();};
            RefreshOemLogoInfo();
        }
        void RefreshOemLogoInfo()
        {
            if(oemLogoInfo==null||oemLogoInfo.IsDisposed)return;
            string text=SettingsText("支持 PNG 透明图片。建议使用横版 Logo（长宽比约 4:1～5:1）；自动去除空白边缘并等比例显示。留空时不占用标识位置。",
                "Transparent PNG is supported. A landscape logo with an aspect ratio around 4:1–5:1 is recommended. Blank edges are trimmed and the image fits proportionally. A blank path leaves no logo space.");
            if(oemLogoGeometry!=null)
                text=String.Format(CultureInfo.CurrentCulture,SettingsText("原图 {0}×{1}（{2:0.##}:1）；显示 {3}×{4}（{5:0.##}:1）。","Original {0}×{1} ({2:0.##}:1); display {3}×{4} ({5:0.##}:1)."),
                    oemLogoGeometry.OriginalSize.Width,oemLogoGeometry.OriginalSize.Height,oemLogoGeometry.OriginalAspect,
                    oemLogoGeometry.DisplaySize.Width,oemLogoGeometry.DisplaySize.Height,oemLogoGeometry.DisplayAspect)+" "+text;
            else if(oemLogoPreviewFailed)text=SettingsText("图片暂无法读取，请检查格式、尺寸和文件路径。原设置未改变。","The image could not be read. Check its format, dimensions and file path. Previous settings are unchanged.")+" "+text;
            if(oemLogoInfo.Text!=text)oemLogoInfo.Text=text;
        }
    }
}
