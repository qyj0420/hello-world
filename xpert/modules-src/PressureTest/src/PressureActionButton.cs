using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.Controls
{
    // Input/presentation only. No pressure state, permission, timer, task or SDK.
    // A new independent click, keyboard activation or direct Stop is never held.
    internal sealed class PressureActionButton : PressureTestButton
    {
        internal enum PresentationModes { Single, Sequence, RangeRetry }
        const double TailWindowMilliseconds=250;
        bool pointerDown, doubleClickTail, nativeDoubleClick, firstMouseClick, mouseUpDispatch;
        Point pointerPoint, firstPoint;
        double firstClickMilliseconds;
        int dispatchDepth;
        readonly ToolTip modeToolTip=new ToolTip { ShowAlways=true };
        PresentationModes presentationMode;
        string modeDescription=String.Empty;
        internal PresentationModes PresentationMode { get { return presentationMode; } }
        internal string ModeDescription { get { return modeDescription; } }
        internal void SetModeBadge(PresentationModes mode,string description)
        {
            description=description??String.Empty;
            if(modeDescription!=description){modeDescription=description;modeToolTip.SetToolTip(this,description);}
            if(presentationMode==mode)return;
            presentationMode=mode;Invalidate();
        }
        internal Func<double> NowMillisecondsForTests;
        internal bool SubmissionBusy {get{return dispatchDepth>0;}}
        protected override bool CompactPressureAppearance { get { return true; } }
        internal struct TwoLineTextLayout
        {
            internal float TitlePixels, DetailPixels;
            internal RectangleF TitleBounds, DetailBounds;
        }
        internal static StringFormat CreateTextFormat()
        {
            var format=(StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment=StringAlignment.Center;
            format.LineAlignment=StringAlignment.Near;
            format.FormatFlags=StringFormatFlags.NoWrap|StringFormatFlags.MeasureTrailingSpaces;
            format.Trimming=StringTrimming.None;
            return format;
        }
        internal TwoLineTextLayout MeasureTwoLineText(Graphics graphics,Rectangle bounds,string[] lines)
        {
            // GDI TextRenderer uses the device HDC's font metrics; a bitmap or
            // per-monitor Graphics can have a different DPI. Mixing that with
            // point-size / Graphics.DpiY sizing clipped CJK ascent/descent in
            // the exact-height rectangles. Use one GDI+ pixel-font pipeline,
            // including explicit breathing room around both line boxes.
            float scale=Math.Max(.5f,graphics.DpiY/96f);
            float guard=Math.Max(2f,2f*scale),gap=Math.Max(3f,3f*scale);
            float pixels=Math.Max(.5f,Font.SizeInPoints*graphics.DpiY/72f);
            // The mode badge has its own top band. It never shares a text line
            // or reduces just one side of a long translated status caption.
            float header=(float)Math.Ceiling(ModeBadgeBounds(graphics).Bottom+3*scale-bounds.Top);
            var content=new RectangleF(bounds.X+guard,bounds.Y+header+guard,
                Math.Max(1f,bounds.Width-2*guard),Math.Max(1f,bounds.Height-header-2*guard));
            using(var format=CreateTextFormat())
            {
                for(int attempt=0;attempt<96;attempt++)
                {
                    using(var title=new Font(Font.FontFamily,pixels,FontStyle.Bold,GraphicsUnit.Pixel))
                    using(var detail=new Font(Font.FontFamily,pixels*.83f,FontStyle.Regular,GraphicsUnit.Pixel))
                    {
                        float titleHeight=(float)Math.Ceiling(title.GetHeight(graphics))+2*guard;
                        float detailHeight=(float)Math.Ceiling(detail.GetHeight(graphics))+2*guard;
                        float height=titleHeight+detailHeight+gap;
                        bool fits=graphics.MeasureString(lines[0],title,Int32.MaxValue,format).Width<=content.Width&&
                            graphics.MeasureString(lines[1],detail,Int32.MaxValue,format).Width<=content.Width&&height<=content.Height;
                        if(fits||pixels<=.5f)
                        {
                            float y=content.Y+Math.Max(0,(content.Height-height)/2);
                            return new TwoLineTextLayout { TitlePixels=pixels,DetailPixels=pixels*.83f,
                                TitleBounds=new RectangleF(content.X,y,content.Width,titleHeight),
                                DetailBounds=new RectangleF(content.X,y+titleHeight+gap,content.Width,detailHeight) };
                        }
                    }
                    pixels=Math.Max(.5f,pixels*.94f);
                }
            }
            return new TwoLineTextLayout();
        }
        internal RectangleF ModeBadgeBounds(Graphics graphics)
        {
            float scale=Math.Max(.5f,graphics.DpiY/96f),side=18*scale;
            return new RectangleF(Math.Max(2*scale,Width-25*scale),5*scale,side,side);
        }
        void DrawModeBadge(Graphics graphics)
        {
            var bounds=ModeBadgeBounds(graphics);
            if(bounds.Right>=Width||bounds.Bottom>=Height)return;
            float scale=Math.Max(.5f,graphics.DpiY/96f);
            Color ink=!Enabled?PressureTestTheme.DisabledInk:Danger?PressureTestTheme.Danger:PressureTestTheme.Navy;
            using(var pen=new Pen(ink,1.6f*scale))
            using(var brush=new SolidBrush(ink))
            {
                pen.StartCap=pen.EndCap=System.Drawing.Drawing2D.LineCap.Round;
                if(presentationMode==PresentationModes.Single)
                    graphics.FillPolygon(brush,new[]{new PointF(bounds.X+4*scale,bounds.Y+3*scale),
                        new PointF(bounds.X+4*scale,bounds.Bottom-3*scale),new PointF(bounds.Right-3*scale,bounds.Y+9*scale)});
                else if(presentationMode==PresentationModes.Sequence)
                {
                    using(var path=new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        float x=bounds.X,y=bounds.Y;
                        path.AddBezier(x+9*scale,y+9*scale,x-1*scale,y-5*scale,x-1*scale,y+23*scale,x+9*scale,y+9*scale);
                        path.AddBezier(x+9*scale,y+9*scale,x+19*scale,y-5*scale,x+19*scale,y+23*scale,x+9*scale,y+9*scale);
                        graphics.DrawPath(pen,path);
                    }
                }
                else
                {
                    var arc=RectangleF.Inflate(bounds,-2*scale,-2*scale);
                    graphics.DrawArc(pen,arc,-60,285);
                    double angle=225*Math.PI/180;
                    var tip=new PointF(arc.X+arc.Width/2+(float)Math.Cos(angle)*arc.Width/2,
                        arc.Y+arc.Height/2+(float)Math.Sin(angle)*arc.Height/2);
                    var tangent=new PointF(-(float)Math.Sin(angle),(float)Math.Cos(angle));
                    var normal=new PointF(-tangent.Y,tangent.X);
                    graphics.FillPolygon(brush,new[]{tip,
                        new PointF(tip.X-tangent.X*5*scale+normal.X*3*scale,tip.Y-tangent.Y*5*scale+normal.Y*3*scale),
                        new PointF(tip.X-tangent.X*5*scale-normal.X*3*scale,tip.Y-tangent.Y*5*scale-normal.Y*3*scale)});
                }
            }
        }
        protected override void DrawButtonText(Graphics graphics,Rectangle bounds,Color ink)
        {
            string[] lines=(Text??String.Empty).Replace("\r",String.Empty).Split('\n');
            if(lines.Length!=2){base.DrawButtonText(graphics,bounds,ink);return;}
            var layout=MeasureTwoLineText(graphics,bounds,lines);
            if(layout.TitlePixels<=0)return;
            using(var title=new Font(Font.FontFamily,layout.TitlePixels,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var detail=new Font(Font.FontFamily,layout.DetailPixels,FontStyle.Regular,GraphicsUnit.Pixel))
            using(var format=CreateTextFormat())
            using(var brush=new SolidBrush(ink))
            {
                var saved=graphics.Save();
                try
                {
                    graphics.SetClip(bounds,System.Drawing.Drawing2D.CombineMode.Intersect);
                    graphics.DrawString(lines[0],title,brush,layout.TitleBounds,format);
                    graphics.DrawString(lines[1],detail,brush,layout.DetailBounds,format);
                }
                finally{graphics.Restore(saved);}
            }
        }
        double NowMilliseconds()
        {return NowMillisecondsForTests==null?Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency:NowMillisecondsForTests();}

        protected override void WndProc(ref Message message)
        {
            if(message.Msg==0x201||message.Msg==0x203)
            {
                nativeDoubleClick=message.Msg==0x203;
                pointerDown=true;doubleClickTail=nativeDoubleClick;
                long position=message.LParam.ToInt64();
                pointerPoint=new Point(unchecked((short)(position&0xffff)),unchecked((short)((position>>16)&0xffff)));
            }
            bool previousDispatch=mouseUpDispatch;
            if(message.Msg==0x202)mouseUpDispatch=true;
            try{base.WndProc(ref message);}
            finally{mouseUpDispatch=previousDispatch;}
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            pointerDown=e.Button==MouseButtons.Left;
            doubleClickTail=pointerDown&&(nativeDoubleClick||e.Clicks>1);
            pointerPoint=e.Location;nativeDoubleClick=false;
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            try{base.OnMouseUp(e);}
            finally{pointerDown=doubleClickTail=nativeDoubleClick=false;}
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Keyboard input is an explicit action, not a mouse tail.
            pointerDown=doubleClickTail=nativeDoubleClick=false;
            base.OnKeyDown(e);
        }
        protected override void OnClick(EventArgs e)
        {
            double now=NowMilliseconds(),elapsed=now-firstClickMilliseconds;
            Size rectangle=SystemInformation.DoubleClickSize;
            bool samePoint=Math.Abs(pointerPoint.X-firstPoint.X)<=Math.Max(1,rectangle.Width/2)&&
                Math.Abs(pointerPoint.Y-firstPoint.Y)<=Math.Max(1,rectangle.Height/2);
            if(mouseUpDispatch&&pointerDown&&doubleClickTail&&firstMouseClick&&samePoint&&elapsed>=0&&elapsed<=TailWindowMilliseconds)
            {
                // Exactly one tail from the same native double-click. Do not
                // apply a time-only debounce to real stop/cancel operations.
                firstMouseClick=false;doubleClickTail=false;Invalidate();return;
            }
            if(mouseUpDispatch)
            {
                firstMouseClick=pointerDown&&!doubleClickTail;
                if(firstMouseClick){firstPoint=pointerPoint;firstClickMilliseconds=now;}
            }
            dispatchDepth++;
            try
            {
                // Paint a short submission indication immediately. No disabling
                // and no message pumping: nested explicit Stop stays callable.
                Invalidate();if(IsHandleCreated&&Visible&&!IsDisposed)Update();
                base.OnClick(e);
            }
            finally{dispatchDepth--;if(!IsDisposed)Invalidate();}
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawModeBadge(e.Graphics);
            if(!SubmissionBusy||Width<36||Height<24)return;
            float scale=Math.Max(.5f,e.Graphics.DpiX/96f),dot=3*scale;
            using(var brush=new SolidBrush(Danger?PressureTestTheme.Danger:PressureTestTheme.Navy))
                for(int i=0;i<3;i++)e.Graphics.FillEllipse(brush,Width-(22-i*5)*scale,Height-10*scale,dot,dot);
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing)modeToolTip.Dispose();
            base.Dispose(disposing);
        }
    }
}
