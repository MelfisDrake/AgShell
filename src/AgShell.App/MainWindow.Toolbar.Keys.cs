using System.Runtime.InteropServices;

namespace AgShell.App
{
    public partial class MainWindow
    {
        private long toolbarPopoverToken;

        private bool ForwardToolbarKey(int message, IntPtr wParam, IntPtr lParam)
        {
            if (toolbarPopup?.IsOpen != true || message is not (0x0100 or 0x0101 or 0x0104 or 0x0105)) return false;
            if (!UiActionsEnabled) { CloseToolbarPopover(true); return true; }
            int virtualKey = wParam.ToInt32();
            if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >= 0xa0 and <= 0xa5) return false;
            bool ctrl = ToolbarKeyDown(0x11);
            bool alt = ToolbarKeyDown(0x12);
            bool meta = ToolbarKeyDown(0x5b) || ToolbarKeyDown(0x5c);
            bool function = virtualKey is >= 0x70 and <= 0x87;
            if ((!ctrl && !alt && !meta && !function) || virtualKey == 0x1b) return false;
            long bits = lParam.ToInt64();
            int scan = (int)((bits >> 16) & 0xff);
            bool extended = (bits & (1L << 24)) != 0;
            string code = ToolbarPhysicalCode(scan, extended, virtualKey);
            if (code.Length == 0) return false;
            string key = code.StartsWith("Key", StringComparison.Ordinal) ? code[3..].ToLowerInvariant()
                : code.StartsWith("Digit", StringComparison.Ordinal) ? code[5..] : code;
            Post(new
            {
                type = "toolbarKey", token = toolbarPopoverToken,
                kind = message is 0x0101 or 0x0105 ? "keyup" : "keydown",
                code, key, ctrlKey = ctrl, altKey = alt, metaKey = meta, shiftKey = ToolbarKeyDown(0x10),
                repeat = (bits & (1L << 30)) != 0,
            });
            return true;
        }

        private static string ToolbarPhysicalCode(int scan, bool extended, int virtualKey)
        {
            const string letters = "QWERTYUIOPASDFGHJKLZXCVBNM";
            int[] positions = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x2c, 0x2d, 0x2e, 0x2f, 0x30, 0x31, 0x32 };
            int letter = Array.IndexOf(positions, scan);
            if (letter >= 0) return "Key" + letters[letter];
            if (scan is >= 2 and <= 11) return "Digit" + (scan == 11 ? 0 : scan - 1);
            if (virtualKey is >= 0x70 and <= 0x87) return "F" + (virtualKey - 0x6f);
            return scan switch
            {
                0x01 => "Escape", 0x0c => "Minus", 0x0d => "Equal", 0x0e => "Backspace", 0x0f => "Tab",
                0x1a => "BracketLeft", 0x1b => "BracketRight", 0x1c => extended ? "NumpadEnter" : "Enter",
                0x27 => "Semicolon", 0x28 => "Quote", 0x29 => "Backquote", 0x2b => "Backslash",
                0x33 => "Comma", 0x34 => "Period", 0x35 => extended ? "NumpadDivide" : "Slash",
                0x37 => "NumpadMultiply", 0x39 => "Space", 0x45 => "NumLock", 0x46 => "ScrollLock",
                0x47 => extended ? "Home" : "Numpad7", 0x48 => extended ? "ArrowUp" : "Numpad8",
                0x49 => extended ? "PageUp" : "Numpad9", 0x4a => "NumpadSubtract",
                0x4b => extended ? "ArrowLeft" : "Numpad4", 0x4c => "Numpad5",
                0x4d => extended ? "ArrowRight" : "Numpad6", 0x4e => "NumpadAdd",
                0x4f => extended ? "End" : "Numpad1", 0x50 => extended ? "ArrowDown" : "Numpad2",
                0x51 => extended ? "PageDown" : "Numpad3", 0x52 => extended ? "Insert" : "Numpad0",
                0x53 => extended ? "Delete" : "NumpadDecimal", 0x56 => "IntlBackslash", _ => "",
            };
        }

        private static bool ToolbarKeyDown(int key) => (ToolbarGetKeyState(key) & 0x8000) != 0;
        [DllImport("user32.dll", EntryPoint = "GetKeyState")] private static extern short ToolbarGetKeyState(int key);
    }
}
