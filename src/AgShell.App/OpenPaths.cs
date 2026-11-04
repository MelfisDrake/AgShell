using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AgShell.App
{
    /// <summary>
    /// "Open a terminal here": paths given to agshell.exe reach the running instance, as macOS hands agterm the URLs of
    /// `open -a agterm`. The instance listens on a hidden message-only window named for its state directory, so a second
    /// launch finds the instance of its own state and nothing else.
    /// </summary>
    internal static class OpenPaths
    {
        private const int CopyData = 0x004A;
        private static readonly IntPtr MessageOnly = new IntPtr(-3);
        private static readonly IntPtr Tag = new IntPtr(0x41475348);
        private static HwndSource? listener;

        /// <summary>A jump list item's arguments, in place of directories.</summary>
        private static readonly IntPtr DockTag = new IntPtr(0x41475344);

        /// <summary>
        /// Starts listening; <paramref name="opened"/> gets each batch of directories and <paramref name="dock"/> each jump list
        /// item's arguments, on the app's thread.
        /// </summary>
        public static void Listen(string pipeName, Action<IReadOnlyList<string>> opened, Action<IReadOnlyList<string>> dock)
        {
            HwndSourceParameters parameters = new HwndSourceParameters("AgShell.Open." + pipeName) { ParentWindow = MessageOnly, Width = 0, Height = 0, WindowStyle = 0 };
            listener = new HwndSource(parameters);
            listener.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == CopyData && Marshal.PtrToStructure<CopyDataStruct>(lParam) is { } data && (data.dwData == Tag || data.dwData == DockTag))
                {
                    string[] items = Marshal.PtrToStringUni(data.lpData, data.cbData / 2).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (data.dwData == Tag)
                    {
                        opened(items);
                    }
                    else
                    {
                        dock(items);
                    }

                    handled = true;
                    return new IntPtr(1);
                }

                return IntPtr.Zero;
            });
        }

        /// <summary>
        /// Hands the directories to the running instance and lets it come to the front; false when no instance listens.
        /// </summary>
        public static bool Send(string pipeName, IReadOnlyList<string> directories)
        {
            return Send(pipeName, directories, Tag);
        }

        /// <summary>Hands a jump list item's arguments to the running instance; false when no instance listens.</summary>
        public static bool SendDock(string pipeName, IReadOnlyList<string> arguments)
        {
            return Send(pipeName, arguments, DockTag);
        }

        private static bool Send(string pipeName, IReadOnlyList<string> directories, IntPtr tag)
        {
            IntPtr target = FindWindowEx(MessageOnly, IntPtr.Zero, null, "AgShell.Open." + pipeName);
            if (target == IntPtr.Zero)
            {
                return false;
            }

            // The launching process may bring a window forward; it passes that right on to the instance.
            GetWindowThreadProcessId(target, out uint processId);
            AllowSetForegroundWindow(processId);
            string joined = string.Join('\0', directories);
            IntPtr text = Marshal.StringToHGlobalUni(joined);
            try
            {
                CopyDataStruct data = new CopyDataStruct { dwData = tag, cbData = joined.Length * 2, lpData = text };
                IntPtr block = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
                try
                {
                    Marshal.StructureToPtr(data, block, false);
                    SendMessageTimeout(target, CopyData, IntPtr.Zero, block, 0x0002, 5000, out _);
                }
                finally
                {
                    Marshal.FreeHGlobal(block);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }

            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyDataStruct
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);
    }
}
