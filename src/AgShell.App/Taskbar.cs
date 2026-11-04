using System.Runtime.InteropServices;

namespace AgShell.App
{
    internal static class Taskbar
    {
        private const uint FlashTray = 0x2;
        private const uint FlashUntilForeground = 0xC;

        /// <summary>Flashes the taskbar button until the window comes to the foreground, or a few times.</summary>
        public static void Flash(IntPtr window, bool untilForeground)
        {
            FlashInfo info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = window,
                Flags = untilForeground ? FlashTray | FlashUntilForeground : FlashTray,
                Count = untilForeground ? 0u : 3u,
                Timeout = 0,
            };
            FlashWindowEx(ref info);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashInfo info);
    }
}
