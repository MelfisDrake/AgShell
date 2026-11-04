using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgShell.Pty
{
    internal static partial class NativeMethods
    {
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        internal const int STARTF_USESTDHANDLES = 0x00000100;
        internal const uint INFINITE = 0xFFFFFFFF;
        internal const uint SEM_FAILCRITICALERRORS = 0x0001;
        internal const uint SEM_NOGPFAULTERRORBOX = 0x0002;
        internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new IntPtr(0x00020016);

        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [LibraryImport("kernel32.dll", EntryPoint = "CreatePseudoConsole")]
        private static partial int SystemCreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [LibraryImport("kernel32.dll", EntryPoint = "ResizePseudoConsole")]
        private static partial int SystemResizePseudoConsole(IntPtr hPC, COORD size);

        [LibraryImport("kernel32.dll", EntryPoint = "ClosePseudoConsole")]
        private static partial void SystemClosePseudoConsole(IntPtr hPC);

        /// <summary>
        /// Windows Terminal's ConPTY (conpty.dll with its OpenConsole.exe) when it ships beside the exe: it passes programs'
        /// sequences and the terminal's answers through, which the system one does not. AGSHELL_CONPTY=system keeps the
        /// system one, to tell the two apart when something looks wrong.
        /// </summary>
        private static readonly IntPtr Bundled = LoadBundled();

        private static IntPtr LoadBundled()
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "conpty.dll");
            if (Environment.GetEnvironmentVariable("AGSHELL_CONPTY") == "system" || !File.Exists(dll) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenConsole.exe")))
            {
                return IntPtr.Zero;
            }

            return NativeLibrary.TryLoad(dll, out IntPtr handle) ? handle : IntPtr.Zero;
        }

        internal static unsafe int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC)
        {
            if (Bundled == IntPtr.Zero)
            {
                return SystemCreatePseudoConsole(size, hInput, hOutput, dwFlags, out phPC);
            }

            var create = (delegate* unmanaged[Stdcall]<COORD, IntPtr, IntPtr, uint, IntPtr*, int>)NativeLibrary.GetExport(Bundled, "ConptyCreatePseudoConsole");
            IntPtr result;
            int hr = create(size, hInput.DangerousGetHandle(), hOutput.DangerousGetHandle(), dwFlags, &result);
            phPC = result;
            return hr;
        }

        internal static unsafe int ResizePseudoConsole(IntPtr hPC, COORD size)
        {
            if (Bundled == IntPtr.Zero)
            {
                return SystemResizePseudoConsole(hPC, size);
            }

            return ((delegate* unmanaged[Stdcall]<IntPtr, COORD, int>)NativeLibrary.GetExport(Bundled, "ConptyResizePseudoConsole"))(hPC, size);
        }

        internal static unsafe void ClosePseudoConsole(IntPtr hPC)
        {
            if (Bundled == IntPtr.Zero)
            {
                SystemClosePseudoConsole(hPC);
                return;
            }

            ((delegate* unmanaged[Stdcall]<IntPtr, void>)NativeLibrary.GetExport(Bundled, "ConptyClosePseudoConsole"))(hPC);
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [LibraryImport("kernel32.dll")]
        internal static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool CreateProcess(
            string? lpApplicationName,
            char* lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out int lpExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("kernel32.dll")]
        internal static partial uint SetErrorMode(uint uMode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);
    }
}
