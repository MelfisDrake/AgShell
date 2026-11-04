using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AgShell.Protocol
{
    /// <summary>
    /// Starts daemons, relaunchers and status watchers without inherited handles, so a WSL hook can return while its
    /// watcher keeps running. Leaves the parent's job when it allows breakaway, otherwise starts in that job.
    /// </summary>
    public static class DetachedProcess
    {
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateNoWindow = 0x08000000;
        private const int ErrorAccessDenied = 5;

        /// <summary>Starts the program with its arguments; null when it started, else why not.</summary>
        public static string? Start(string program, params string[] arguments)
        {
            string commandLine = string.Join(" ", new[] { program }.Concat(arguments).Select(Quote));
            int error = Create(commandLine, CreateBreakawayFromJob | CreateNoWindow);
            // A job without JOB_OBJECT_LIMIT_BREAKAWAY_OK refuses the breakaway; the process then stays in the job.
            if (error == ErrorAccessDenied)
            {
                error = Create(commandLine, CreateNoWindow);
            }

            return error == 0 ? null : new Win32Exception(error).Message;
        }

        private static int Create(string commandLine, uint flags)
        {
            StartupInfo startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref startup, out ProcessInformation info))
            {
                return Marshal.GetLastWin32Error();
            }

            CloseHandle(info.Process);
            CloseHandle(info.Thread);
            return 0;
        }

        /// <summary>One argument as CommandLineToArgvW reads it back.</summary>
        private static string Quote(string argument)
        {
            if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
            {
                return argument;
            }

            StringBuilder quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(c);
            }

            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Count;
            public IntPtr Reserved2;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
