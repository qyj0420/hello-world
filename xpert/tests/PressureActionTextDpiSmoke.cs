using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HVX.Controls;

// Standalone raster-only managed regression: only the production button/theme
// are compiled, never PressureTestControl, product Main, workers or SDKs.
internal static class PressureActionTextDpiSmoke
{
    static int checks,cases;
    static void Check(bool condition,string why){++checks;if(!condition)throw new InvalidOperationException(why);}
    static byte[] Pixels(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try {var bytes=new byte[Math.Abs(data.Stride)*bitmap.Height];Marshal.Copy(data.Scan0,bytes,0,bytes.Length);return bytes;}
        finally {bitmap.UnlockBits(data);}
    }
    static Bitmap BitmapAt(int width,int height,int dpi)
    {var image=new Bitmap(width,height,PixelFormat.Format32bppArgb);image.SetResolution(dpi,dpi);return image;}
    static void RasterCase(int dpi,int logicalWidth,int logicalHeight,string title,string detail,PressureActionButton.PresentationModes mode,string output,bool preview)
    {
        ++cases;float scale=dpi/96f;
        using(var button=new PressureActionButton())
        using(var font=new Font("Microsoft YaHei UI",13f,FontStyle.Bold,GraphicsUnit.Point))
        using(var actual=BitmapAt((int)Math.Round(logicalWidth*scale),(int)Math.Round(logicalHeight*scale),dpi))
        using(var reference=BitmapAt(actual.Width,actual.Height,dpi))
        {
            button.Size=actual.Size;button.Font=font;button.Text=title+"\n"+detail;
            string modeText=mode==PressureActionButton.PresentationModes.Single?"单次加压":mode==PressureActionButton.PresentationModes.Sequence?"连续加压":"重复加压";
            button.SetModeBadge(mode,modeText);
            Check(button.PresentationMode==mode&&button.ModeDescription==modeText,"corner mode changes independently of boolean text");
            var toolTip=(ToolTip)button.GetType().GetField("modeToolTip",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(button);
            Check(toolTip.GetToolTip(button)==modeText,"corner mode tooltip retains its own translated explanation");
            var bounds=new Rectangle(7,2,actual.Width-14,actual.Height-6);
            PressureActionButton.TwoLineTextLayout layout;
            using(var graphics=Graphics.FromImage(actual))
            {
                graphics.Clear(Color.White);graphics.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
                layout=button.MeasureTwoLineText(graphics,bounds,new[]{title,detail});
                button.GetType().GetMethod("DrawButtonText",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(button,new object[]{graphics,bounds,PressureTestTheme.Navy});
            }
            Check(layout.TitlePixels>0&&layout.DetailPixels>0,"DPI layout has both actual fonts");
            Check(layout.TitleBounds.Top>bounds.Top&&layout.DetailBounds.Bottom<bounds.Bottom,"top and bottom guard remain inside button");
            Check(layout.TitleBounds.Bottom<layout.DetailBounds.Top,"two lines cannot intersect");
            using(var graphics=Graphics.FromImage(actual))
                Check(layout.TitleBounds.Top>button.ModeBadgeBounds(graphics).Bottom,"corner badge has a separate band above both text lines");
            using(var graphics=Graphics.FromImage(reference))
            using(var titleFont=new Font(font.FontFamily,layout.TitlePixels,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var detailFont=new Font(font.FontFamily,layout.DetailPixels,FontStyle.Regular,GraphicsUnit.Pixel))
            using(var format=PressureActionButton.CreateTextFormat())
            using(var ink=new SolidBrush(PressureTestTheme.Navy))
            {
                graphics.Clear(Color.White);graphics.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
                // Reference permits ink outside either line box. A cropped top,
                // bottom or horizontal overhang therefore differs pixel-for-pixel.
                format.FormatFlags|=StringFormatFlags.NoClip;
                graphics.DrawString(title,titleFont,ink,layout.TitleBounds,format);
                graphics.DrawString(detail,detailFont,ink,layout.DetailBounds,format);
            }
            var a=Pixels(actual);var r=Pixels(reference);int different=0,inkPixels=0;
            for(int i=0;i<a.Length;i+=4)
            {
                if(a[i]!=r[i]||a[i+1]!=r[i+1]||a[i+2]!=r[i+2]||a[i+3]!=r[i+3])++different;
                if(a[i]<245||a[i+1]<245||a[i+2]<245)++inkPixels;
            }
            Check(different==0,"unclipped reference differs at DPI "+dpi+" size "+logicalWidth+"x"+logicalHeight+" title "+title+" pixels "+different);
            Check(inkPixels>20,"both lines paint visible text rather than an empty result");
            if(preview)
            {
                using(var graphics=Graphics.FromImage(actual))
                    button.GetType().GetMethod("OnPaint",BindingFlags.Instance|BindingFlags.NonPublic)
                        .Invoke(button,new object[]{new PaintEventArgs(graphics,button.ClientRectangle)});
                actual.Save(Path.Combine(output,"pressure-action-"+mode+"-"+dpi+"dpi.png"));
                Check(actual.GetPixel(actual.Width/2,actual.Height-8).ToArgb()==Color.FromArgb(220,244,248).ToArgb(),"confirmed pale-cyan face remains");
                Rectangle badge;
                using(var graphics=Graphics.FromImage(actual))badge=Rectangle.Ceiling(button.ModeBadgeBounds(graphics));
                int badgeInk=0;
                for(int y=badge.Top;y<badge.Bottom;y++)for(int x=badge.Left;x<badge.Right;x++)
                {var color=actual.GetPixel(x,y);if(color.R<100&&color.G<120&&color.B<160)++badgeInk;}
                Check(badgeInk>10,"mode symbol is actually painted at the upper right");
            }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Isolated absolute output directory required.");
            var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
            string[] titles={"开始加压","正在加压","加压完成","正在准备 · 点击停止","正在停止 · 等待回执","本轮未完成 · 点击停止","已經停止","Preparing · click to stop","Przygotowanie · kliknij, aby zatrzymać","Vorbereitung · zum Stoppen klicken","Préparation · cliquer pour arrêter"};
            foreach(int dpi in new[]{96,120,144,168,192,240})
                foreach(int width in new[]{220,286,360})
                    foreach(int height in new[]{66,90,116})
                        foreach(string title in titles)
                            foreach(PressureActionButton.PresentationModes mode in Enum.GetValues(typeof(PressureActionButton.PresentationModes)))
                                RasterCase(dpi,width,height,title,mode==PressureActionButton.PresentationModes.Single?"24.6 kV":
                                    mode==PressureActionButton.PresentationModes.Sequence?"24.6 kV  ·  3/3 次":"24.6 kV  ·  重采 1",mode,output,width==286&&height==90&&title=="正在加压");
            Console.WriteLine("PRESSURE_ACTION_TEXT_DPI_PASS="+checks+";cases="+cases+";dpi=96,120,144,168,192,240;hardware=false;product=false;physicalAcceptance=false");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
