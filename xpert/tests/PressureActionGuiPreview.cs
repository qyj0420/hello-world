using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HVX.Controls;

// Render the production control only. No device, pressure controller or app Main.
internal static class PressureActionGuiPreview
{
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Absolute isolated output required.");
            string output=Path.GetFullPath(args[0]);
            if(File.Exists(output))throw new IOException("Preserve previous preview.");
            using(var canvas=new Bitmap(1160,270,PixelFormat.Format32bppArgb))
            using(var graphics=Graphics.FromImage(canvas))
            using(var labelFont=new Font("Microsoft YaHei UI",12f,FontStyle.Regular,GraphicsUnit.Pixel))
            using(var titleFont=new Font("Microsoft YaHei UI",19f,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var buttonFont=new Font("Microsoft YaHei UI",13f,FontStyle.Bold,GraphicsUnit.Point))
            using(var ink=new SolidBrush(PressureTestTheme.Navy))
            {
                canvas.SetResolution(96,96);
                graphics.Clear(Color.FromArgb(243,248,252));
                graphics.DrawString("加压按钮 · 当前代码渲染",titleFont,ink,24,18);
                string[] labels={"单次加压","连续加压","量程调整后重采","停止完成"};
                string[] details={"13.1 kV","13.1 kV  ·  1/3 次","13.1 kV  ·  重采 1","13.1 kV"};
                for(int i=0;i<4;i++)
                {
                    int x=24+i*284;
                    graphics.DrawString(labels[i],labelFont,ink,x,57);
                    using(var button=new PressureActionButton())
                    using(var rendered=new Bitmap(260,100,PixelFormat.Format32bppArgb))
                    using(var target=Graphics.FromImage(rendered))
                    {
                        rendered.SetResolution(96,96);
                        button.Size=rendered.Size;button.Font=buttonFont;
                        button.Text=(i==3?"已经停止":"开始加压")+"\n"+details[i];
                        button.SetModeBadge(i==1?PressureActionButton.PresentationModes.Sequence:
                            i==2?PressureActionButton.PresentationModes.RangeRetry:PressureActionButton.PresentationModes.Single,labels[i]);
                        typeof(PressureActionButton).GetMethod("OnPaint",BindingFlags.Instance|BindingFlags.NonPublic)
                            .Invoke(button,new object[]{new PaintEventArgs(target,button.ClientRectangle)});
                        graphics.DrawImageUnscaled(rendered,x,82);
                    }
                }
                graphics.DrawString("正文保留操作状态与预设电压；模式标识位于右上角，不占正文位置。",labelFont,ink,24,208);
                graphics.DrawString("纯GUI预览，未连接设备；实际电压与次数由当前轮状态提供。",labelFont,ink,24,234);
                canvas.Save(output,ImageFormat.Png);
            }
            Console.WriteLine("PRESSURE_ACTION_GUI_PREVIEW_PASS;hardware=false;product=false;output="+output);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
