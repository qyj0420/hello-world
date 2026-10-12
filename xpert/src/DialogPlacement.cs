using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Ui
{
    // UI-thread-local, never a system-wide hook. Covers module Forms and native
    // MessageBox/file dialogs before their first activation; subsequent user moves
    // and focus changes must not pull a dialog back to the centre.
    internal sealed class DialogPlacement : IDisposable
    {
        [ThreadStatic]static DialogPlacement current;
        readonly HashSet<IntPtr> placed=new HashSet<IntPtr>();
        readonly HookProc callback;
        IntPtr hook;
        internal static bool Install()
        {
            if(current!=null)return true;
            var policy=new DialogPlacement();
            if(policy.hook==IntPtr.Zero){Debug.WriteLine(new Win32Exception(Marshal.GetLastWin32Error()));return false;}
            current=policy;Application.ThreadExit+=OnThreadExit;return true;
        }
        static void OnThreadExit(object sender,EventArgs e){if(current!=null)current.Dispose();}
        DialogPlacement(){callback=OnHook;hook=SetWindowsHookEx(5,callback,IntPtr.Zero,GetCurrentThreadId());}
        IntPtr OnHook(int code,IntPtr window,IntPtr data)
        {
            try {
                if(code==4)placed.Remove(window);
                else if(code==5&&!placed.Contains(window)&&IsDialog(window)){placed.Add(window);Center(window);}
            }catch(Exception ex){Debug.WriteLine("DialogPlacement: "+ex.Message);}
            return CallNextHookEx(hook,code,window,data);
        }
        static bool IsDialog(IntPtr window)
        {
            if((GetWindowLong(window,-16)&0x40000000)!=0||IsIconic(window)||IsZoomed(window))return false;
            var name=new StringBuilder(128);GetClassName(window,name,name.Capacity);
            if(name.ToString()=="#32770")return true;
            var form=Control.FromHandle(window) as Form;
            return form!=null&&form.TopLevel&&(form.Modal||!form.ShowInTaskbar||GetWindow(window,4)!=IntPtr.Zero);
        }
        static void Center(IntPtr window)
        {
            Rect bounds;if(!GetWindowRect(window,out bounds))return;
            IntPtr owner=GetWindow(window,4);
            if(owner==IntPtr.Zero||!IsWindowVisible(owner)||IsIconic(owner)) {
                owner=IntPtr.Zero;
                foreach(Form f in Application.OpenForms)
                    if(f.IsHandleCreated&&f.Handle!=window&&f.Visible&&f.WindowState!=FormWindowState.Minimized&&f.ShowInTaskbar)owner=f.Handle;
            }
            var monitor=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};
            IntPtr screen=owner!=IntPtr.Zero?MonitorFromWindow(owner,2):MonitorFromPoint(new NativePoint(Cursor.Position.X,Cursor.Position.Y),2);
            if(!GetMonitorInfo(screen,ref monitor))return;
            Rect ownerBounds;
            Rectangle area=monitor.Work.ToRectangle(),anchor=owner!=IntPtr.Zero&&GetWindowRect(owner,out ownerBounds)?ownerBounds.ToRectangle():area;
            Rectangle target=Calculate(anchor,area,new Size(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top));
            // Keep dialog size; oversized windows are clamped to keep their top-left
            // controls reachable. Each resizable dialog owns its scrolling.
            SetWindowPos(window,IntPtr.Zero,target.X,target.Y,0,0,0x0015);
        }
        internal static Rectangle Calculate(Rectangle anchor,Rectangle work,Size size)
        {
            int x=anchor.Left+(anchor.Width-size.Width)/2,y=anchor.Top+(anchor.Height-size.Height)/2;
            x=Math.Max(work.Left,Math.Min(x,work.Right-size.Width));
            y=Math.Max(work.Top,Math.Min(y,work.Bottom-size.Height));
            return new Rectangle(x,y,size.Width,size.Height);
        }
        public void Dispose()
        {
            if(hook!=IntPtr.Zero){UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}
            placed.Clear();Application.ThreadExit-=OnThreadExit;if(current==this)current=null;
        }
        delegate IntPtr HookProc(int code,IntPtr window,IntPtr data);
        [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;public Rectangle ToRectangle(){return Rectangle.FromLTRB(Left,Top,Right,Bottom);}}
        [StructLayout(LayoutKind.Sequential)]struct MonitorInfo{public int Size;public Rect Monitor,Work;public uint Flags;}
        [StructLayout(LayoutKind.Sequential)]struct NativePoint{public int X,Y;public NativePoint(int x,int y){X=x;Y=y;}}
        [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint thread);
        [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr window,IntPtr data);
        [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder name,int maximum);
        [DllImport("user32.dll")]static extern IntPtr GetWindow(IntPtr window,uint command);
        [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out Rect rect);
        [DllImport("user32.dll")]static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")]static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    }
}
