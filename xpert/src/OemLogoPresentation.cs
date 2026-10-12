using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class OemLogoGeometry
    {
        internal Size OriginalSize, DisplaySize;
        internal Rectangle ContentBounds;
        internal bool Trimmed {get{return OriginalSize!=DisplaySize;}}
        internal double OriginalAspect {get{return OriginalSize.Width/(double)OriginalSize.Height;}}
        internal double DisplayAspect {get{return DisplaySize.Width/(double)DisplaySize.Height;}}
    }
    internal static class OemLogoPresentation
    {
        // Normal home header: 295 wide, 92 high minus12/10 padding and3 accent.
        // Responsive/DPI layouts keep their own fixed viewport; Zoom contains
        // wide/tall artwork there without changing that viewport or distortion.
        internal static readonly Size NormalHeaderViewport=new Size(295,67);
        internal static Bitmap Prepare(Bitmap source,out OemLogoGeometry geometry)
        {
            if(source==null)throw new ArgumentNullException("source");
            int w=source.Width,h=source.Height;byte[] pixels=new byte[checked(w*h*4)];
            var data=source.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try{for(int y=0;y<h;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*w*4,w*4);}
            finally{source.UnlockBits(data);}
            Func<int,int,bool> white=(x,y)=>{int p=(y*w+x)*4;return pixels[p]==255&&pixels[p+1]==255&&pixels[p+2]==255&&pixels[p+3]==255;};
            // White-edge removal requires an EXACT uniform opaque white whole
            // perimeter. Near-white, patterned or content-touching edges do not
            // qualify. Alpha1..255 content is always retained in transparent mode.
            bool whiteBorder=true;
            for(int x=0;x<w&&whiteBorder;x++)whiteBorder=white(x,0)&&white(x,h-1);
            for(int y=0;y<h&&whiteBorder;y++)whiteBorder=white(0,y)&&white(w-1,y);
            int left=w,top=h,right=-1,bottom=-1;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                int p=(y*w+x)*4;bool background=whiteBorder?white(x,y):pixels[p+3]==0;
                if(background)continue;left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
            }
            // A solid opaque white image is ambiguous (may be intentional), so
            // preserve it. An entirely transparent image has no visible logo.
            if(right<0)
            {
                if(!whiteBorder)throw new System.IO.InvalidDataException("所选 Logo 图片没有可见内容，请重新选择。");
                left=0;top=0;right=w-1;bottom=h-1;
            }
            var bounds=Rectangle.FromLTRB(left,top,right+1,bottom+1);
            geometry=new OemLogoGeometry{OriginalSize=source.Size,DisplaySize=bounds.Size,ContentBounds=bounds};
            return source.Clone(bounds,PixelFormat.Format32bppArgb);
        }
        internal static RectangleF Contain(Size image,RectangleF viewport)
        {
            if(image.Width<=0||image.Height<=0||viewport.Width<=0||viewport.Height<=0)return RectangleF.Empty;
            float scale=Math.Min(viewport.Width/image.Width,viewport.Height/image.Height),w=image.Width*scale,h=image.Height*scale;
            return new RectangleF(viewport.Left+(viewport.Width-w)/2,viewport.Top+(viewport.Height-h)/2,w,h);
        }
    }
}
