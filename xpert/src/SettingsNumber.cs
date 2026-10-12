using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // Display precision is independent of the unchanged source double.
    internal sealed class SettingsNumber : NumericUpDown
    {
        private bool hasSource;
        private double source;
        private decimal loaded;
        internal int MinimumDecimalPlaces { get; set; }
        internal void LoadSource(double value)
        {
            decimal converted;
            try { converted = Convert.ToDecimal(value); }
            catch (OverflowException) { converted = Minimum; }
            catch (InvalidCastException) { converted = Minimum; }
            if (converted < Minimum) converted = Minimum;
            if (converted > Maximum) converted = Maximum;
            if (DecimalPlaces != 0)
            {
                int places = Math.Max(2,Math.Min(9,MinimumDecimalPlaces));
                while (converted != 0 && Decimal.Round(converted, places) == 0 && places < 9) places++;
                DecimalPlaces = places;
            }
            Value = converted; loaded = converted; source = value; hasSource = true;
        }
        internal bool SourceUnchanged { get { return hasSource && Value == loaded; } }
        internal double ReadSource() { return SourceUnchanged ? source : (double)Value; }
        protected override void OnMouseWheel(MouseEventArgs e)
        { SettingsWheelScroll.ScrollPage(this,e); }
    }
    // A wheel gesture scrolls settings, never changes a configuration value.
    // Explicit typing, keyboard arrows and clicked spinner/list items retain
    // the standard control behavior and the original precision contract.
    internal sealed class SettingsComboBox : ComboBox
    {
        readonly DropDownWheelWindow dropDownWheel;
        internal SettingsComboBox() { dropDownWheel=new DropDownWheelWindow(this); }
        internal IntPtr DropDownWheelHandle {get{return dropDownWheel.Handle;}}
        [StructLayout(LayoutKind.Sequential)] struct ComboInfo
        {
            internal int Size;internal RectangleNative Item,List;internal int ButtonState;
            internal IntPtr ComboHandle,ItemHandle,ListHandle;
        }
        [StructLayout(LayoutKind.Sequential)] struct RectangleNative {internal int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool GetComboBoxInfo(IntPtr handle,ref ComboInfo info);
        protected override void OnDropDown(EventArgs e)
        {
            var info=new ComboInfo{Size=Marshal.SizeOf(typeof(ComboInfo))};
            if(IsHandleCreated&&GetComboBoxInfo(Handle,ref info)&&info.ListHandle!=IntPtr.Zero)
                dropDownWheel.Attach(info.ListHandle);
            base.OnDropDown(e);
        }
        protected override void OnDropDownClosed(EventArgs e)
        { dropDownWheel.Detach();base.OnDropDownClosed(e); }
        protected override void OnHandleDestroyed(EventArgs e)
        { dropDownWheel.Detach();base.OnHandleDestroyed(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        { SettingsWheelScroll.ScrollPage(this,e); }
        protected override void WndProc(ref Message message)
        {
            if(message.Msg==0x020A) {
                int delta=(short)((message.WParam.ToInt64()>>16)&0xffff);
                OnMouseWheel(new HandledMouseEventArgs(MouseButtons.None,0,0,0,delta));
                message.Result=IntPtr.Zero;return;
            }
            base.WndProc(ref message);
        }
        // The expanded native COMBOLBOX has a separate HWND. Swallow its
        // wheel before native selection changes, while leaving clicks/keys
        // and all non-wheel messages to its original procedure.
        sealed class DropDownWheelWindow : NativeWindow
        {
            readonly SettingsComboBox owner;
            internal DropDownWheelWindow(SettingsComboBox owner){this.owner=owner;}
            internal void Attach(IntPtr handle){if(Handle==handle)return;Detach();AssignHandle(handle);}
            internal void Detach(){if(Handle!=IntPtr.Zero)ReleaseHandle();}
            protected override void WndProc(ref Message message)
            {
                if(message.Msg==0x020A){int delta=(short)((message.WParam.ToInt64()>>16)&0xffff);
                    owner.OnMouseWheel(new HandledMouseEventArgs(MouseButtons.None,0,0,0,delta));message.Result=IntPtr.Zero;return;}
                base.WndProc(ref message);
            }
        }
    }
    internal static class SettingsWheelScroll
    {
        internal static void ScrollPage(Control control,MouseEventArgs e)
        {
            var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;
            if(e.Delta==0)return;
            for(Control parent=control.Parent;parent!=null;parent=parent.Parent) {
                var page=parent as ScrollableControl;
                if(page==null||!page.AutoScroll||!page.VerticalScroll.Visible)continue;
                int lines=SystemInformation.MouseWheelScrollLines;if(lines==0)return;
                int step=lines<0?Math.Max(1,page.ClientSize.Height):Math.Max(1,page.VerticalScroll.SmallChange)*lines;
                int notches=Math.Max(1,Math.Abs(e.Delta)/120);
                long y=-(long)page.AutoScrollPosition.Y-(long)Math.Sign(e.Delta)*step*notches;
                int maximum=Math.Max(0,page.VerticalScroll.Maximum-page.VerticalScroll.LargeChange+1);
                page.AutoScrollPosition=new Point(Math.Max(0,-page.AutoScrollPosition.X),(int)Math.Max(0,Math.Min(maximum,y)));
                return;
            }
        }
    }
    internal sealed partial class SettingsForm
    {
        private static double ReadNumber(NumericUpDown control)
        { var number = control as SettingsNumber; return number == null ? (double)control.Value : number.ReadSource(); }
    }
}
