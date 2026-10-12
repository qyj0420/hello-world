using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HVX.PdReview
{
    internal static class ReviewInputBorder
    {
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        internal static void Draw(Control control)
        {
            if(!control.IsHandleCreated||control.Width<2||control.Height<2)return;
            IntPtr dc=GetWindowDC(control.Handle);if(dc==IntPtr.Zero)return;
            try{using(var g=Graphics.FromHdc(dc))using(var p=new Pen(ReviewTheme.Border))g.DrawRectangle(p,0,0,control.Width-1,control.Height-1);}
            finally{ReleaseDC(control.Handle,dc);}
        }
    }
    internal sealed class ReviewNumericInput:NumericUpDown
    {
        internal ReviewNumericInput(){BorderStyle=BorderStyle.FixedSingle;BackColor=ReviewTheme.Surface;ForeColor=ReviewTheme.Ink;}
        protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0xF||m.Msg==0x85)ReviewInputBorder.Draw(this);}
    }
    internal sealed class ReviewComboInput:ComboBox
    {
        internal ReviewComboInput(){FlatStyle=FlatStyle.Flat;BackColor=ReviewTheme.Surface;ForeColor=ReviewTheme.Ink;}
        protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0xF||m.Msg==0x85)ReviewInputBorder.Draw(this);}
    }
}
