using System.Runtime.InteropServices;
using System.Windows.Interop;
using AgShell.Core;

namespace AgShell.App
{
    /// <summary>The keymap's global-hotkey: one chord registered with Windows that works while any application is in front.</summary>
    internal sealed class GlobalHotkey
    {
        private const int HotkeyId = 0xA7E1;
        private const int HotkeyMessage = 0x0312;

        private readonly HwndSource source;
        private bool registered;
        private KeyChord? chord;
        private uint scan;
        private IntPtr layout;
        private IntPtr keyboardHook;
        private readonly KeyboardHook callback;
        private bool held;
        private readonly HashSet<uint> modifiersDown = new HashSet<uint>();

        public GlobalHotkey(HwndSource source)
        {
            this.source = source;
            callback = OnKeyboard;
            source.AddHook(OnMessage);
        }

        public event Action? Pressed;

        /// <summary>Registers the chord, replacing the previous one; returns why it could not be, or null.</summary>
        public string? Register(KeyChord? chord)
        {
            if (registered) UnregisterHotKey(source.Handle, HotkeyId);
            registered = false;
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
            this.chord = chord;
            held = false;
            modifiersDown.Clear();
            foreach (uint modifier in new uint[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
                if (Down((int)modifier)) modifiersDown.Add(modifier);
            layout = IntPtr.Zero;
            scan = chord is null ? 0 : PhysicalScan(chord.Code);
            if (chord is null || source.Handle == IntPtr.Zero) return null;
            if (scan != 0)
            {
                keyboardHook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (keyboardHook == IntPtr.Zero) return "global-hotkey " + chord + ": could not monitor physical keys";
            }

            return RegisterLayout(KeyboardLayout());
        }

        private string? RegisterLayout(IntPtr current)
        {
            if (registered) UnregisterHotKey(source.Handle, HotkeyId);
            registered = false;
            layout = current;
            if (chord is null) return null;
            uint modifiers = 0x4000 | (chord.Alt ? 0x1u : 0) | (chord.Ctrl ? 0x2u : 0) | (chord.Shift ? 0x4u : 0) | (chord.Meta ? 0x8u : 0);
            uint? key = scan == 0 ? VirtualKey(chord.Code) : MapVirtualKeyEx(scan, 3, current);
            if (key is null or 0) return "global-hotkey " + chord + ": this key cannot be registered";
            registered = RegisterHotKey(source.Handle, HotkeyId, modifiers, key.Value);
            return registered ? null : "global-hotkey " + chord + " is taken by another application";
        }

        // RegisterHotKey takes a virtual key. Resolve the physical position before each event reaches Windows.
        private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
        {
            if (code < 0 || chord is null) return CallNextHookEx(keyboardHook, code, message, data);
            IntPtr current = KeyboardLayout();
            if (current != layout && RegisterLayout(current) is string problem) AgShell.Protocol.Log.Notice("GlobalHotkey", problem);
            KeyboardEvent key = Marshal.PtrToStructure<KeyboardEvent>(data);
            uint keyScan = key.Scan | ((key.Flags & 1) != 0 ? 0xE000u : 0);
            bool up = message.ToInt32() is 0x101 or 0x105;
            if (key.VirtualKey is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
            {
                if (up) modifiersDown.Remove(key.VirtualKey);
                else modifiersDown.Add(key.VirtualKey);
            }
            if (keyScan == scan && held)
            {
                if (up) held = false;
                return new IntPtr(1);
            }

            if (!up && keyScan == scan && registered && chord.Ctrl == (modifiersDown.Contains(0xA2) || modifiersDown.Contains(0xA3)) && chord.Alt == (modifiersDown.Contains(0xA4) || modifiersDown.Contains(0xA5))
                && chord.Shift == (modifiersDown.Contains(0xA0) || modifiersDown.Contains(0xA1)) && chord.Meta == (modifiersDown.Contains(0x5B) || modifiersDown.Contains(0x5C)))
            {
                held = true;
                source.Dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke()));
                return new IntPtr(1);
            }

            return CallNextHookEx(keyboardHook, code, message, data);
        }

        private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        internal static bool IsKeyDown(string code)
        {
            uint position = PhysicalScan(code);
            uint? key = position == 0 ? code switch { "Escape" => 0x1B, "NumpadEnter" => 0x0D, _ => VirtualKey(code) } : MapVirtualKeyEx(position, 3, KeyboardLayout());
            return key is null or 0 || Down((int)key.Value);
        }

        private static IntPtr KeyboardLayout()
        {
            uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            GuiThreadInfo info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (GetGUIThreadInfo(thread, ref info) && info.Focus != IntPtr.Zero) thread = GetWindowThreadProcessId(info.Focus, out _);
            return GetKeyboardLayout(thread);
        }

        private static uint PhysicalScan(string code)
        {
            const string letters = "QWERTYUIOPASDFGHJKLZXCVBNM";
            uint[] positions = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32 };
            if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && letters.IndexOf(code[3]) is int index && index >= 0) return positions[index];
            if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal)) return code[5] == '0' ? 0x0Bu : (uint)(code[5] - '1' + 2);
            return code switch
            {
                "Backquote" => 0x29, "Minus" => 0x0C, "Equal" => 0x0D, "BracketLeft" => 0x1A, "BracketRight" => 0x1B,
                "Backslash" => 0x2B, "Semicolon" => 0x27, "Quote" => 0x28, "Comma" => 0x33, "Period" => 0x34, "Slash" => 0x35,
                _ => 0,
            };
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == HotkeyMessage && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                // Physical keys already fired in the low-level callback; Windows can still post WM_HOTKEY.
                if (scan == 0) Pressed?.Invoke();
            }

            return IntPtr.Zero;
        }

        private static uint? VirtualKey(string code)
        {
            if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
            {
                return code[3];
            }

            if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
            {
                return code[5];
            }

            if (code.Length > 1 && code[0] == 'F' && int.TryParse(code[1..], out int number))
            {
                return (uint)(0x70 + number - 1);
            }

            return code switch
            {
                "Space" => 0x20,
                "Tab" => 0x09,
                "Enter" => 0x0D,
                "Backspace" => 0x08,
                "Delete" => 0x2E,
                "Insert" => 0x2D,
                "Home" => 0x24,
                "End" => 0x23,
                "PageUp" => 0x21,
                "PageDown" => 0x22,
                "ArrowLeft" => 0x25,
                "ArrowUp" => 0x26,
                "ArrowRight" => 0x27,
                "ArrowDown" => 0x28,
                "Backquote" => 0xC0,
                "Minus" => 0xBD,
                "Equal" => 0xBB,
                "BracketLeft" => 0xDB,
                "BracketRight" => 0xDD,
                "Backslash" => 0xDC,
                "Semicolon" => 0xBA,
                "Quote" => 0xDE,
                "Comma" => 0xBC,
                "Period" => 0xBE,
                "Slash" => 0xBF,
                "NumpadAdd" => 0x6B,
                "NumpadSubtract" => 0x6D,
                "Numpad0" => 0x60,
                _ => null,
            };
        }

        private delegate IntPtr KeyboardHook(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardEvent
        {
            public uint VirtualKey, Scan, Flags, Time;
            public UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, Menu, Move, Caret;
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, KeyboardHook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
