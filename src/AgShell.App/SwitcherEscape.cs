using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgShell.App
{
    /// <summary>
    /// Esc with Ctrl held while the Ctrl+Tab switcher is open: agterm's key monitor takes it and cancels the switcher, but on
    /// Windows Ctrl+Esc opens Start before the window sees the key, and RegisterHotKey refuses the chord. A low-level keyboard
    /// hook, set only while the switcher is open and acting only while this window is in front, eats the key instead.
    /// </summary>
    internal sealed class SwitcherEscape
    {
        private const int LowLevelKeyboardHook = 13;
        private const int KeyDown = 0x0100;
        private const int SystemKeyDown = 0x0104;
        private const int VirtualEscape = 0x1B;
        private const int VirtualControl = 0x11;

        private readonly Window window;
        private readonly Action cancel;
        private readonly HookProc proc;
        private IntPtr hook;

        public SwitcherEscape(Window window, Action cancel)
        {
            this.window = window;
            this.cancel = cancel;
            proc = OnKey;
        }

        public void Hold(bool open)
        {
            if (open && hook == IntPtr.Zero)
            {
                hook = SetWindowsHookEx(LowLevelKeyboardHook, proc, GetModuleHandle(null), 0);
            }
            else if (!open && hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        }

        private IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && Marshal.ReadInt32(data) == VirtualEscape && (GetAsyncKeyState(VirtualControl) & 0x8000) != 0
                && GetForegroundWindow() == new WindowInteropHelper(window).Handle)
            {
                if ((int)message is KeyDown or SystemKeyDown)
                {
                    window.Dispatcher.BeginInvoke(cancel, DispatcherPriority.Input);
                }

                return 1;
            }

            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);
    }
}
