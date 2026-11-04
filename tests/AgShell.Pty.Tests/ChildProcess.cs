using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgShell.Pty.Tests
{
    internal static class ChildProcess
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

        public static Process FindPing(int parentId)
        {
            using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ProcessEntry entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            do
            {
                if (entry.ParentProcessId == parentId && string.Equals(entry.ExeFile, "ping.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return Process.GetProcessById((int)entry.ProcessId);
                }
            }
            while (Process32Next(snapshot, ref entry));

            throw new InvalidOperationException("No ping child of owned shell " + parentId);
        }
    }
}
