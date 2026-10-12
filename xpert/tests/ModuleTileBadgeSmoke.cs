using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ModuleTileBadgeSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Directory.CreateDirectory(args[0]);
            foreach(bool compact in new[]{false,true})
            foreach(string text in new[]{"内嵌采集","內嵌採集","LIVE","SIM","HV DAC","实机","仿真"})
            foreach(int width in compact?new[]{176,250,400}:new[]{220,252,420})
            using(var tile=new ModuleTile("CalibrationTest","pulse"))
            {
                tile.CompactMode=compact;tile.Size=new Size(width,compact?72:186);
                tile.ModeBadge=text;tile.Title="校准测试";tile.Description="TDR 测距与多档局放校准";
                tile.CreateControl();Rectangle badge=tile.GetModeBadgeBounds();
                using(var font=AppTheme.Font(compact?7.1f:8.2f,FontStyle.Bold))
                {
                    Size measured=TextRenderer.MeasureText(text,font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
                    Check(badge.Width>=measured.Width+(compact?12:20),"Full text plus padding: "+text);
                    Check(badge.Height>=measured.Height,"Full single-line height");
                }
                Rectangle title,description,footer;tile.GetContentBounds(out title,out description,out footer);
                Check(tile.ClientRectangle.Contains(badge),"Badge stays inside card");
                Check(!badge.IntersectsWith(title),"Badge does not overlap title");
                Check(!badge.IntersectsWith(footer),"Badge does not overlap footer");
                if(text=="HV DAC"&&width==(compact?176:252))
                    using(var bitmap=new Bitmap(tile.Width,tile.Height)){tile.DrawToBitmap(bitmap,tile.ClientRectangle);bitmap.Save(Path.Combine(args[0],compact?"badge-compact.png":"badge-card.png"));}
                int longWidth=badge.Width;tile.ModeBadge="SIM";
                if(text=="内嵌采集")Check(longWidth>tile.GetModeBadgeBounds().Width,"Badge grows with actual label width");
            }
            Console.WriteLine("MODULE_TILE_BADGE_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
