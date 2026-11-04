using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's menu bar. The page builds the menus from the state with the predicates its palette and keys use, so an item
    /// is enabled exactly when its action can run; a click runs the action on the page, and the window's own items here.
    /// The bar is drawn in the window's colors; its menus are the system's popup menus, which, like macOS menus, leave the
    /// keyboard where it was (a field being edited stays) and take the keys while open.
    /// </summary>
    public partial class MainWindow
    {
        private JsonElement[] menus = Array.Empty<JsonElement>();
        private string? appliedMenu;
        private TaskCompletionSource<bool>? menuRefreshed;
        private bool menuOpen;
        private int openMenuIndex = -1;
        private int? menuSwitch;
        private int menuDepth;
        private bool menuItemHasSubmenu;
        private Rect[] menuHeaderRects = Array.Empty<Rect>();
        private IntPtr menuHook;
        private HookProc? menuHookProc;
        private Brush menuHighlight = Brushes.Transparent;
        private bool menuDark = true;
        private NativePoint menuPointer;

        private List<ControlKeymapMenuItem>? MenuReadback()
        {
            if (menus.Length == 0) return null;
            List<ControlKeymapMenuItem> rows = new List<ControlKeymapMenuItem>();
            foreach (JsonElement menu in menus)
            {
                Read(menu.GetProperty("items"), menu.GetProperty("title").GetString() ?? string.Empty, true);
            }

            return rows;

            void Read(JsonElement items, string title, bool parentEnabled)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    bool enabled = parentEnabled && (!item.TryGetProperty("enabled", out JsonElement flag) || flag.GetBoolean());
                    if (item.TryGetProperty("items", out JsonElement children)) Read(children, title, enabled);
                    if (!item.TryGetProperty("chord", out JsonElement chord) || chord.ValueKind != JsonValueKind.String ||
                        !item.TryGetProperty("keys", out JsonElement keys) || string.IsNullOrEmpty(keys.GetString())) continue;
                    rows.Add(new ControlKeymapMenuItem
                    {
                        Menu = title,
                        Title = item.GetProperty("title").GetString() ?? string.Empty,
                        Chord = chord.GetString()!,
                        Selector = item.GetProperty("id").GetString(),
                        Enabled = enabled ? null : false,
                    });
                }
            }
        }

        private void ApplyMenu(JsonElement message)
        {
            string json = message.GetRawText();
            if (json != appliedMenu)
            {
                appliedMenu = json;
                JsonElement copy = message.Clone();
                ApplyMenuColors(copy.GetProperty("colors"));
                List<JsonElement> list = new List<JsonElement>();
                foreach (JsonElement menu in copy.GetProperty("menus").EnumerateArray())
                {
                    list.Add(menu);
                }

                menus = list.ToArray();
                BuildMenuBar();
            }

            menuRefreshed?.TrySetResult(true);
        }

        private void BuildMenuBar()
        {
            if (menuBar.Children.Count != menus.Length || menuBarReserved)
            {
                menuBarReserved = false;
                menuBar.Children.Clear();
                for (int index = 0; index < menus.Length; index++)
                {
                    int captured = index;
                    Border header = MenuHeader();
                    header.MouseLeftButtonDown += (_, e) =>
                    {
                        e.Handled = true;
                        OpenMenu(captured);
                    };
                    menuBar.Children.Add(header);
                }
            }

            for (int index = 0; index < menus.Length; index++)
            {
                Border header = (Border)menuBar.Children[index];
                ((TextBlock)header.Child).Text = menus[index].GetProperty("title").GetString();
                header.Background = index == openMenuIndex ? menuHighlight : Brushes.Transparent;
            }

            menuBarHost.Visibility = Visibility.Visible;
        }

        private static Border MenuHeader()
        {
            TextBlock title = new TextBlock { FontFamily = SystemFonts.MenuFontFamily, FontSize = SystemFonts.MenuFontSize };
            return new Border { Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = title };
        }

        private bool menuBarReserved;

        /// <summary>The hidden bar holds a blank header until the page sends its menus, so it has the height it will have.</summary>
        private void ReserveMenuBar()
        {
            menuBarReserved = true;
            Border placeholder = MenuHeader();
            ((TextBlock)placeholder.Child).Text = " ";
            menuBar.Children.Add(placeholder);
        }

        private void ApplyMenuColors(JsonElement colors)
        {
            menuBarHost.Background = MenuBrush(colors, "bar");
            menuBar.SetValue(TextElement.ForegroundProperty, MenuBrush(colors, "text"));
            menuHighlight = MenuBrush(colors, "highlight");
            menuDark = colors.GetProperty("dark").GetBoolean();
        }

        private Popup? menuBarPopup;
        private DispatcherTimer? menuEdgeTimer;

        /// <summary>
        /// As macOS hides the menu bar in full screen and slides it over the content when the pointer reaches the top edge:
        /// the bar moves into a popup, a window of its own above the browser, shown while the pointer is at the edge or on it.
        /// </summary>
        private void SetMenuBarFullscreen(bool fullscreen)
        {
            if (fullscreen && menuBarPopup is null)
            {
                ((Panel)menuBarHost.Parent).Children.Remove(menuBarHost);
                menuBarPopup = new Popup { Child = menuBarHost, PlacementTarget = webView, Placement = PlacementMode.Relative, StaysOpen = true };
                menuEdgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                menuEdgeTimer.Tick += (_, _) => FollowMenuEdge();
                menuEdgeTimer.Start();
            }
            else if (!fullscreen && menuBarPopup is not null)
            {
                menuEdgeTimer?.Stop();
                menuEdgeTimer = null;
                menuBarPopup.IsOpen = false;
                menuBarPopup.Child = null;
                menuBarPopup = null;
                menuBarHost.Width = double.NaN;
                ((Panel)webView.Parent).Children.Insert(1, menuBarHost);
            }
        }

        private void FollowMenuEdge()
        {
            if (menuBarPopup is null || !GetCursorPos(out NativePoint point) || menuBarHost.Visibility != Visibility.Visible)
            {
                return;
            }

            Point origin = webView.PointToScreen(new Point(0, 0));
            Point corner = webView.PointToScreen(new Point(webView.ActualWidth, webView.ActualHeight));
            bool inside = point.X >= origin.X && point.X < corner.X;
            if (!menuBarPopup.IsOpen)
            {
                if (IsActive && inside && point.Y <= origin.Y + 1)
                {
                    menuBarHost.Width = webView.ActualWidth;
                    menuBarPopup.IsOpen = true;
                }

                return;
            }

            double bottom = menuBarHost.IsVisible ? menuBarHost.PointToScreen(new Point(0, menuBarHost.ActualHeight)).Y : origin.Y;
            if (!menuOpen && (!inside || point.Y > bottom))
            {
                menuBarPopup.IsOpen = false;
            }
        }

        private static Brush MenuBrush(JsonElement colors, string name)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.GetProperty(name).GetString()));
            brush.Freeze();
            return brush;
        }

        /// <summary>Opens a menu of the bar; moving to another title or Left/Right goes on to that menu, as on a menu bar.</summary>
        private async void OpenMenu(int index)
        {
            if (menuOpen)
            {
                return;
            }

            menuOpen = true;
            Post(new { type = "menuTracking" });
            try
            {
                // The model is read again first: times like "5 min ago" and the Edit menu's selection are those of now.
                menuRefreshed = new TaskCompletionSource<bool>();
                Post(new { type = "menuRefresh" });
                await Task.WhenAny(menuRefreshed.Task, Task.Delay(200));
                menuRefreshed = null;
                int? next = index;
                while (next is int current && current >= 0 && current < menus.Length)
                {
                    next = TrackMenu(current);
                }
            }
            finally
            {
                menuOpen = false;
            }
        }

        private int? TrackMenu(int index)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource? source = HwndSource.FromHwnd(handle);
            Dictionary<int, string> ids = new Dictionary<int, string>();
            IntPtr menu = BuildPopup(menus[index].GetProperty("items"), ids);
            menuHeaderRects = new Rect[menuBar.Children.Count];
            for (int i = 0; i < menuHeaderRects.Length; i++)
            {
                FrameworkElement header = (FrameworkElement)menuBar.Children[i];
                menuHeaderRects[i] = new Rect(header.PointToScreen(new Point(0, 0)), header.PointToScreen(new Point(header.ActualWidth, header.ActualHeight)));
            }

            Rect rect = menuHeaderRects[index];
            openMenuIndex = index;
            menuSwitch = null;
            menuDepth = 0;
            menuItemHasSubmenu = false;
            GetCursorPos(out menuPointer);
            BuildMenuBar();
            ApplyMenuTheme();
            menuHookProc = OnMenuMessage;
            menuHook = SetWindowsHookEx(MsgFilterHook, menuHookProc, IntPtr.Zero, GetCurrentThreadId());
            source?.AddHook(OnMenuOwnerMessage);
            int command;
            try
            {
                TpmParams exclude = new TpmParams
                {
                    Size = Marshal.SizeOf<TpmParams>(),
                    Left = (int)rect.Left,
                    Top = (int)rect.Top,
                    Right = (int)rect.Right,
                    Bottom = (int)rect.Bottom,
                };
                command = TrackPopupMenuEx(menu, TpmReturnCommand | TpmVertical, (int)rect.Left, (int)rect.Bottom, handle, ref exclude);
            }
            finally
            {
                source?.RemoveHook(OnMenuOwnerMessage);
                UnhookWindowsHookEx(menuHook);
                menuHook = IntPtr.Zero;
                DestroyMenu(menu);
                openMenuIndex = -1;
                BuildMenuBar();
            }

            if (menuSwitch is int next)
            {
                return next;
            }

            if (command > 0 && ids.TryGetValue(command, out string? id))
            {
                RunMenuItem(id);
            }

            return null;
        }

        private IntPtr BuildPopup(JsonElement nodes, Dictionary<int, string> ids)
        {
            IntPtr menu = CreatePopupMenu();
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                string title = (node.TryGetProperty("title", out JsonElement value) ? value.GetString() ?? "" : "").Replace("&", "&&");
                switch (node.GetProperty("kind").GetString())
                {
                    case "separator":
                        AppendMenu(menu, MenuSeparator, IntPtr.Zero, null);
                        break;
                    case "header":
                        // agterm's section titles (Open Recent's Sessions and Workspaces): a system menu has none, a grey row stands for one.
                        AppendMenu(menu, MenuGrayed, IntPtr.Zero, title);
                        break;
                    case "submenu":
                        IntPtr submenu = BuildPopup(node.GetProperty("items"), ids);
                        AppendMenu(menu, MenuPopup | (node.GetProperty("enabled").GetBoolean() ? 0 : MenuGrayed), submenu, title);
                        break;
                    default:
                        string id = node.GetProperty("id").GetString() ?? "";
                        bool enabled = node.GetProperty("enabled").GetBoolean() && (id != "edit:paste" || Clipboard.ContainsText());
                        bool check = node.TryGetProperty("checked", out JsonElement mark) && mark.ValueKind == JsonValueKind.True;
                        string keys = node.TryGetProperty("keys", out JsonElement chord) ? chord.GetString() ?? "" : "";
                        int command = ids.Count + 1;
                        ids[command] = id;
                        AppendMenu(menu, (enabled ? 0 : MenuGrayed) | (check ? MenuChecked : 0), (IntPtr)command, keys.Length > 0 ? title + "\t" + keys : title);
                        break;
                }
            }

            return menu;
        }

        /// <summary>Dark or light menus with the window, as macOS menus follow the appearance. Windows has no public API for it.</summary>
        private void ApplyMenuTheme()
        {
            try
            {
                SetPreferredAppMode(menuDark ? 2 : 3);
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        /// <summary>The menu loop's messages: a move over another title of the bar, or Left/Right at the edge, goes to that menu.</summary>
        private IntPtr OnMenuMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == MsgfMenu && menuSwitch is null)
            {
                Msg message = Marshal.PtrToStructure<Msg>(lParam);
                // Only a real move: the menu that a key opened stays open under a pointer resting on another title.
                if (message.Message == WmMouseMove && GetCursorPos(out NativePoint point) && (point.X != menuPointer.X || point.Y != menuPointer.Y))
                {
                    menuPointer = point;
                    for (int i = 0; i < menuHeaderRects.Length; i++)
                    {
                        if (i != openMenuIndex && menuHeaderRects[i].Contains(new Point(point.X, point.Y)))
                        {
                            menuSwitch = i;
                            EndMenu();
                            break;
                        }
                    }
                }
                else if (message.Message == WmKeyDown && menus.Length > 1)
                {
                    int key = message.WParam.ToInt32();
                    if (key == VkLeft && menuDepth <= 1)
                    {
                        menuSwitch = (openMenuIndex + menus.Length - 1) % menus.Length;
                        EndMenu();
                        return (IntPtr)1;
                    }

                    if (key == VkRight && !menuItemHasSubmenu)
                    {
                        menuSwitch = (openMenuIndex + 1) % menus.Length;
                        EndMenu();
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(menuHook, code, wParam, lParam);
        }

        /// <summary>Which submenu level is open and whether the highlighted row opens a submenu, for Left and Right.</summary>
        private IntPtr OnMenuOwnerMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (message)
            {
                case WmInitMenuPopup:
                    menuDepth++;
                    break;
                case WmUninitMenuPopup:
                    menuDepth = Math.Max(0, menuDepth - 1);
                    break;
                case WmMenuSelect:
                    menuItemHasSubmenu = ((wParam.ToInt64() >> 16) & MenuPopup) != 0;
                    break;
            }

            return IntPtr.Zero;
        }

        internal void RunMenuItem(string id)
        {
            switch (id)
            {
                case "host:close-window":
                    Close();
                    return;
                case "host:minimize":
                    WindowState = WindowState.Minimized;
                    return;
                case "host:zoom":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return;
                case "host:front":
                    BringAllToFront();
                    return;
                case "host:emoji":
                    // The system's Emoji & Symbols is Windows' emoji panel, opened by its own keys while the page has the keyboard.
                    SendEmojiPanelKeys();
                    return;
                case "edit:paste":
                    Post(new { type = "menuAction", id, text = Clipboard.ContainsText() ? Clipboard.GetText() : "" });
                    return;
                default:
                    Post(new { type = "menuAction", id });
                    return;
            }
        }

        /// <summary>The system's Bring All to Front: every open window that is not minimized above other programs, this one on top.</summary>
        private void BringAllToFront()
        {
            foreach (MainWindow window in Library.OpenWindows)
            {
                if (window != this && window.WindowState != WindowState.Minimized)
                {
                    SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                }
            }

            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
            Activate();
        }

        private static void SendEmojiPanelKeys()
        {
            const ushort win = 0x5B;
            const ushort period = 0xBE;
            Input[] inputs =
            {
                KeyInput(win, false), KeyInput(period, false), KeyInput(period, true), KeyInput(win, true),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        }

        private static Input KeyInput(ushort key, bool up)
        {
            return new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } };
        }

        private const uint MenuGrayed = 0x1;
        private const uint MenuChecked = 0x8;
        private const uint MenuPopup = 0x10;
        private const uint MenuSeparator = 0x800;
        private const uint TpmReturnCommand = 0x100;
        private const uint TpmVertical = 0x40;
        private const int MsgFilterHook = -1;
        private const int MsgfMenu = 2;
        private const int WmKeyDown = 0x100;
        private const int WmMouseMove = 0x200;
        private const int WmInitMenuPopup = 0x117;
        private const int WmUninitMenuPopup = 0x125;
        private const int WmMenuSelect = 0x11F;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct TpmParams
        {
            public int Size;
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Window;
            public int Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public int Time;
            public NativePoint Point;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)]
            public uint Type;
            [FieldOffset(8)]
            public KeyboardInput Keyboard;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, ref TpmParams parameters);

        [DllImport("user32.dll")]
        private static extern bool EndMenu();

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();
    }
}
