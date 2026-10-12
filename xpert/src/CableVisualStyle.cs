using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace HVX.ProjectCables
{
    // Display-only, not a wiring plan, physical dimension or voltage rating.
    // x is the measured cable endpoint; the termination extends outwards.
    internal static class CableVisualStyle
    {
        internal static bool IsSeparable(string type)
        {
            return !string.IsNullOrEmpty(type)&&(type.IndexOf("插拔",StringComparison.OrdinalIgnoreCase)>=0||
                type.IndexOf("separable",StringComparison.OrdinalIgnoreCase)>=0||type.IndexOf("plug-in",StringComparison.OrdinalIgnoreCase)>=0);
        }
        internal static void DrawBody(Graphics g,float left,float right,float y,bool integrated,Color phase,float scale=1)
        {
            if(right<=left||scale<=0)return;
            var body=new RectangleF(left,y-5*scale,right-left,10*scale);
            using(var metal=new LinearGradientBrush(body,Color.FromArgb(247,250,251),Color.FromArgb(99,121,134),LinearGradientMode.Vertical))g.FillRectangle(metal,body);
            using(var edge=new Pen(Color.FromArgb(91,113,126),scale)){g.DrawLine(edge,left,body.Top,right,body.Top);g.DrawLine(edge,left,body.Bottom,right,body.Bottom);}
            using(var shine=new Pen(Color.White,1.3f*scale))g.DrawLine(shine,left,y-2*scale,right,y-2*scale);
            var colors=integrated?new[]{Color.FromArgb(209,154,18),Color.FromArgb(43,154,104),Color.FromArgb(207,77,81)}:new[]{phase};
            for(int p=0;p<colors.Length;p++)using(var core=new Pen(colors[p],1.5f*scale))g.DrawLine(core,left,y+(integrated?-3+p*3:0)*scale,right,y+(integrated?-3+p*3:0)*scale);
        }
        internal static void DrawTermination(Graphics g,float x,float y,int outward,string type,float scale=1)
        {
            var state=g.Save();
            try
            {
                g.TranslateTransform(x,y);g.ScaleTransform((outward<0?-1:1)*scale,scale);
                using(var edge=new Pen(Color.FromArgb(67,80,89),1))
                {
                    if(IsSeparable(type))
                    {
                        // Compact shielded T-shaped assembly, no exposed pin.
                        using(var rubber=new SolidBrush(Color.FromArgb(65,76,83)))
                        using(var face=new SolidBrush(Color.FromArgb(103,118,128)))
                        {
                            var shape=new[]{new PointF(0,-5),new PointF(17,-5),new PointF(17,-14),new PointF(28,-14),new PointF(28,14),new PointF(17,14),new PointF(17,5),new PointF(0,5)};
                            g.FillPolygon(rubber,shape);g.DrawPolygon(edge,shape);
                            g.FillRectangle(face,29,-11,7,22);g.DrawRectangle(edge,29,-11,7,22);
                            g.DrawLine(Pens.Silver,19,-11,19,11);g.DrawLine(edge,5,-6,5,6);
                        }
                    }
                    else
                    {
                        using(var red=new LinearGradientBrush(new RectangleF(0,-10,37,20),Color.FromArgb(243,190,184),Color.FromArgb(182,89,80),LinearGradientMode.Vertical))
                        using(var metal=new SolidBrush(Color.FromArgb(201,211,216)))
                        {
                            g.FillRectangle(red,0,-5,32,10);g.DrawRectangle(edge,0,-5,32,10);
                            for(int ring=0;ring<3;ring++)
                            {
                                float a=7+ring*7;
                                var skirt=new[]{new PointF(a,-10),new PointF(a+6,-5),new PointF(a+6,5),new PointF(a,10)};
                                g.FillPolygon(red,skirt);g.DrawPolygon(edge,skirt);
                            }
                            g.FillRectangle(metal,32,-3,10,6);g.DrawRectangle(edge,32,-3,10,6);
                        }
                    }
                }
            }
            finally{g.Restore(state);}
        }
    }
}
