using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.PdReview
{
    // One rounded waveform surface, with four seamless native checkable regions.
    // Native CheckBox semantics preserve keyboard and assistive-technology state.
    internal sealed class ReviewPhaseSelector:Control
    {
        readonly CheckBox[] quadrants=new CheckBox[4];
        internal CheckBox[] Quadrants{get{return (CheckBox[])quadrants.Clone();}}

        internal ReviewPhaseSelector()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            TabStop=false;BackColor=Color.White;Size=new Size(552,125);
            for(int i=0;i<4;i++)
            {
                var region=new ReviewPhaseQuadrant(i){Name="AnalysisQuadrant"+(i+1),TabIndex=i,Checked=true,
                    Text=new[]{"I","II","III","IV"}[i],AccessibleName=(i*90)+"°–"+((i+1)*90)+"°",
                    AccessibleRole=AccessibleRole.CheckButton};
                quadrants[i]=region;Controls.Add(region);
            }
            LayoutRegions();
        }
        int Inset{get{return Math.Max(24,Font.Height+8);}}
        int LabelHeight{get{return Math.Max(24,Font.Height+7);}}
        internal RectangleF WaveFrame{get{return new RectangleF(Inset-.5f,.5f,Math.Max(4,ClientSize.Width-2*Inset)+1,Math.Max(1,ClientSize.Height-LabelHeight-1));}}
        internal GraphicsPath WaveOutline()
        {return ReviewTheme.Rounded(WaveFrame,Math.Max(7,Font.Height*.65f));}
        void LayoutRegions()
        {
            if(quadrants[3]==null)return;
            int left=Inset,width=Math.Max(4,ClientSize.Width-2*left),height=Math.Max(1,ClientSize.Height-LabelHeight-2);
            for(int i=0;i<4;i++)
            {
                int x=left+width*i/4,next=left+width*(i+1)/4;
                quadrants[i].Bounds=new Rectangle(x,1,next-x,height);
            }
            Invalidate();
        }
        internal void MoveFocus(int index,Keys key)
        {
            int next=key==Keys.Home?0:key==Keys.End?3:(index+(key==Keys.Left?-1:1)+4)%4;
            quadrants[next].Focus();
        }
        protected override void OnResize(EventArgs e){base.OnResize(e);LayoutRegions();}
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);LayoutRegions();}
        protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);Invalidate(true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(quadrants[3]==null)return;
            int left=quadrants[0].Left,right=quadrants[3].Right,bottom=quadrants[0].Bottom;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=WaveOutline())using(var fill=new SolidBrush(Color.White))using(var pen=new Pen(Color.FromArgb(189,209,219)))
            {e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(pen,path);}
            for(int i=0;i<=4;i++)
            {
                int x=left+(right-left)*i/4;
                var label=new Rectangle(x-Inset,bottom+2,Inset*2,LabelHeight-2);
                TextRenderer.DrawText(e.Graphics,(90*i)+"°",Font,label,Enabled?ReviewTheme.Ink:Color.FromArgb(128,143,151),
                    TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class ReviewPhaseQuadrant:CheckBox
    {
        readonly int quadrant;
        bool hover;
        internal ReviewPhaseQuadrant(int index)
        {
            quadrant=index;AutoSize=false;Appearance=Appearance.Button;FlatStyle=FlatStyle.Flat;UseVisualStyleBackColor=false;
            Cursor=Cursors.Hand;TabStop=true;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            UpdateColors();
        }
        void UpdateColors()
        {
            BackColor=Checked?ReviewTheme.ActiveSoft:ReviewTheme.Surface;
            ForeColor=Checked?ReviewTheme.Active:ReviewTheme.SecondaryInk;Invalidate();
        }
        protected override void OnCheckedChanged(EventArgs e){UpdateColors();base.OnCheckedChanged(e);}
        protected override void OnEnabledChanged(EventArgs e){hover=false;Invalidate();base.OnEnabledChanged(e);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key=keyData&Keys.KeyCode;
            return key==Keys.Left||key==Keys.Right||key==Keys.Home||key==Keys.End||base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right||e.KeyCode==Keys.Home||e.KeyCode==Keys.End)
            {
                var selector=Parent as ReviewPhaseSelector;
                if(selector!=null)selector.MoveFocus(quadrant,e.KeyCode);
                e.Handled=true;e.SuppressKeyPress=true;return;
            }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            Color fill=Enabled?(hover?(Checked?Color.FromArgb(209,232,242):Color.FromArgb(240,246,249)):BackColor):(Checked?Color.FromArgb(239,245,248):Color.FromArgb(249,250,251));
            Color ink=Enabled?ForeColor:ReviewTheme.DisabledInk;
            var selector=Parent as ReviewPhaseSelector;
            g.Clear(selector==null?Color.White:selector.BackColor);
            // Clip only the two outside corners of the complete surface. There
            // are no independent card/button borders between adjacent phases.
            if(selector!=null)using(var outline=selector.WaveOutline())using(var shift=new Matrix())
            {
                shift.Translate(-Left,-Top);outline.Transform(shift);g.SetClip(outline,CombineMode.Intersect);
            }
            using(var background=new SolidBrush(fill))g.FillRectangle(background,ClientRectangle);
            float header=Math.Max(22,Font.Height+6),centre=header+(Height-header)*.5f,amplitude=Math.Max(1,(Height-header)*.38f);
            using(var axis=new Pen(Color.FromArgb(195,210,218)))
            {
                axis.DashStyle=DashStyle.Dash;g.DrawLine(axis,0,centre,Width,centre);
                if(quadrant>0)g.DrawLine(axis,0,0,0,Height);
            }
            // Use one shared phase scale, including across integer-pixel quarter
            // boundaries at fractional DPI. Extend each stroke beyond its clip
            // to avoid pen-cap gaps where the four click regions meet.
            var points=new PointF[Math.Max(24,Width)+5];
            int waveLeft=selector==null?0:selector.Quadrants[0].Left;
            int waveWidth=selector==null?Width*4:selector.Quadrants[3].Right-waveLeft;
            for(int i=0;i<points.Length;i++)
            {
                float x=-2+(Width+4f)*i/(points.Length-1);
                double fraction=selector==null?(quadrant+(double)x/Math.Max(1,Width))/4:(Left-waveLeft+x)/Math.Max(1,waveWidth);
                points[i]=new PointF(x,centre-amplitude*(float)Math.Sin(fraction*Math.PI*2));
            }
            using(var wave=new Pen(ink,Math.Max(2.2f,Font.Height*.19f)))
            {
                wave.LineJoin=LineJoin.Round;
                g.DrawLines(wave,points);
            }
            // A tick or a minus identifies the selection without adding another
            // tiny checkbox: the waveform region itself is the button.
            int icon=Math.Max(11,Font.Height-2),textWidth=TextRenderer.MeasureText(Text,Font).Width;
            float start=(Width-icon-6-textWidth)/2f,top=Math.Max(4,(header-icon)/2);
            using(var pen=new Pen(ink,Math.Max(1.5f,Font.Height*.115f)))
            {
                pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                if(Checked)g.DrawLines(pen,new[]{new PointF(start+1,top+icon*.5f),new PointF(start+icon*.4f,top+icon*.8f),new PointF(start+icon-1,top+icon*.18f)});
                else g.DrawLine(pen,start+2,top+icon*.5f,start+icon-2,top+icon*.5f);
            }
            TextRenderer.DrawText(g,Text,Font,new Rectangle((int)start+icon+6,0,textWidth,(int)header),ink,
                TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-3,-3),ReviewTheme.Accent,fill);
        }
    }
}
