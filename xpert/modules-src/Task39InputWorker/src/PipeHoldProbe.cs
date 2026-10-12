using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Xiongxin.ZeroBench
{
    // Driver-free regression fixture only. No breakaway flag: the helper must
    // remain in the worker's owned job and die when that job is disposed.
    internal static class PipeHoldProbe
    {
        internal static int Start(string directory)
        {
            IntPtr input = IntPtr.Zero, output = IntPtr.Zero, error = IntPtr.Zero;
            ProcessInformation child = new ProcessInformation();
            try
            {
                input = InheritableCopy(-10); output = InheritableCopy(-11); error = InheritableCopy(-12);
                var startup = new StartupInformation
                {
                    Size = (uint)Marshal.SizeOf(typeof(StartupInformation)), Flags = 0x100,
                    Input = input, Output = output, Error = error
                };
                string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var commandLine = new StringBuilder("\"" + executable + "\" --pipe-hold-probe");
                if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
                    IntPtr.Zero, AppDomain.CurrentDomain.BaseDirectory, ref startup, out child))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "不能启动无驱动管道后代。");
                bool inJob;
                if (!IsProcessInJob(child.Process, IntPtr.Zero, out inJob) || !inJob)
                    throw new InvalidOperationException("管道后代未继承受控 Job。");
                using (var process = Process.GetProcessById((int)child.Id))
                    File.WriteAllLines(Path.Combine(directory, "pipe-helper.txt"), new[]
                    {
                        child.Id.ToString(), process.StartTime.ToUniversalTime().Ticks.ToString(), "IN_JOB=True"
                    }, new UTF8Encoding(false));
                return (int)child.Id;
            }
            finally
            {
                if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
                if (child.Process != IntPtr.Zero) CloseHandle(child.Process);
                if (input != IntPtr.Zero) CloseHandle(input);
                if (output != IntPtr.Zero) CloseHandle(output);
                if (error != IntPtr.Zero) CloseHandle(error);
            }
        }

        private static IntPtr InheritableCopy(int standardHandle)
        {
            IntPtr copy, current = GetCurrentProcess();
            if (!DuplicateHandle(current, GetStdHandle(standardHandle), current, out copy, 0, true, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "复制测试管道句柄失败。");
            return copy;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInformation
        {
            public uint Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command,
            IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint flags, IntPtr environment, string directory, ref StartupInformation startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source,
            IntPtr destinationProcess, out IntPtr destination, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
