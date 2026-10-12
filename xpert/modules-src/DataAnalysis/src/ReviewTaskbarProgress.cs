using System;
using System.Runtime.InteropServices;

namespace HVX.PdReview
{
    // Windows shell progress belongs to the LabVIEW taskbar button, not a
    // second button for the owned progress dialog. All calls stay on the UI STA.
    internal sealed class ReviewTaskbarProgress : IDisposable
    {
        const uint RootOwner = 3;
        const uint NoProgress = 0;
        const uint Normal = 2;
        const uint Paused = 8;
        static readonly Guid TaskbarClass = new Guid("56FDF344-FD6D-11D0-958A-006097C9A090");
        ITaskbarList3 shell;
        IntPtr target;
        bool unavailable;

        [DllImport("user32.dll", ExactSpelling=true)]
        static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);

        [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9E3A62EEB0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd,[MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            [PreserveSig] int SetProgressValue(IntPtr hwnd,ulong completed,ulong total);
            [PreserveSig] int SetProgressState(IntPtr hwnd,uint state);
        }

        internal void Update(IntPtr ownerOrDialog,int completed,int total,bool stopping)
        {
            if(unavailable||ownerOrDialog==IntPtr.Zero)return;
            try
            {
                if(shell==null)
                {
                    shell=(ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarClass));
                    Marshal.ThrowExceptionForHR(shell.HrInit());
                }
                IntPtr root=GetAncestor(ownerOrDialog,RootOwner);
                IntPtr next=root==IntPtr.Zero?ownerOrDialog:root;
                if(target!=IntPtr.Zero&&target!=next)Marshal.ThrowExceptionForHR(shell.SetProgressState(target,NoProgress));
                target=next;
                Marshal.ThrowExceptionForHR(shell.SetProgressState(target,stopping?Paused:Normal));
                Marshal.ThrowExceptionForHR(shell.SetProgressValue(target,(ulong)Math.Max(0,Math.Min(completed,total)),(ulong)Math.Max(1,total)));
            }
            catch(COMException){Disable();}
            catch(InvalidCastException){Disable();}
            catch(PlatformNotSupportedException){Disable();}
        }

        internal void Clear()
        {
            if(shell!=null&&target!=IntPtr.Zero)
            {
                try{shell.SetProgressState(target,NoProgress);}catch(COMException){}
                target=IntPtr.Zero;
            }
        }
        void Disable(){Clear();unavailable=true;}
        public void Dispose()
        {
            Clear();
            if(shell!=null){Marshal.ReleaseComObject(shell);shell=null;}
        }
    }
}
