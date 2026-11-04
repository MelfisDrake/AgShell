using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AgShell.Core;
using Microsoft.Win32;

namespace AgShell.App
{
    /// <summary>A keyboard layout Windows can select, as the settings list shows it.</summary>
    internal sealed record KeyboardInputSource(string Id, string Name);

    /// <summary>
    /// agterm's TerminalInputSource: holds the configured keyboard layout while a terminal has the keyboard and puts the
    /// earlier one back when it does not. The only owner of layout selection in the app; <see cref="InputSourceLock"/> decides,
    /// this performs.
    ///
    /// The keyboard is held by WebView2's window in its browser process, so the layout is read from the thread of the window
    /// that has the keyboard and selected by asking that window for it. Windows applies the request for the whole session, as
    /// macOS selects a source system-wide, so the restore is what keeps other applications on the user's own layout. Windows
    /// tells only the thread whose layout changed, the browser's, so where agterm is notified of a switch the user makes
    /// while the lock holds, this reads the layout every quarter second until the lock lets go. A crash or a killed process
    /// runs no restore and leaves the configured layout selected.
    /// </summary>
    internal sealed class TerminalInputSource
    {
        private const int InputLanguageChangeRequest = 0x0050;

        private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);

        private static TerminalInputSource? shared;

        private readonly InputSourceLock lockState = new InputSourceLock();
        private readonly Dictionary<IntPtr, bool> terminalFocus = new Dictionary<IntPtr, bool>();
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer poll;
        private string? target;

        /// <summary>The layout last read where a page of ours had the keyboard.</summary>
        private string? lastInPage;
        private bool refreshScheduled;
        private bool stopped;

        private TerminalInputSource(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            InputLanguageManager.Current.InputLanguageChanged += OnLanguageChanged;
            poll = new DispatcherTimer(PollEvery, DispatcherPriority.Background, (sender, args) => Refresh(), dispatcher);
            poll.Stop();
        }

        private void OnLanguageChanged(object sender, InputLanguageEventArgs args)
        {
            RequestRefresh();
        }

        public static TerminalInputSource Shared => shared ??= new TerminalInputSource(Application.Current.Dispatcher);

        /// <summary>Takes the configured layout, null for off, and applies it to the current focus at once.</summary>
        public void SetTarget(string? id)
        {
            target = id;
            Refresh();
        }

        /// <summary>A window's page says whether one of its terminals has the page's keyboard focus.</summary>
        public void Report(IntPtr window, bool focused)
        {
            terminalFocus[window] = focused;
            RequestRefresh();
        }

        public void Forget(IntPtr window)
        {
            terminalFocus.Remove(window);
            RequestRefresh();
        }

        /// <summary>Re-evaluates after the current work: a window's activation event comes before the foreground settles.</summary>
        public void RequestRefresh()
        {
            if (refreshScheduled || stopped)
            {
                return;
            }

            refreshScheduled = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
        }

        /// <summary>Restores the earlier layout and ends enforcement for good, for the app's exit.</summary>
        public void Stop()
        {
            stopped = true;
            InputLanguageManager.Current.InputLanguageChanged -= OnLanguageChanged;
            poll.Stop();
            if (Keyboard() is Focus focus && lockState.Release(Current(focus)) is string restore)
            {
                Select(focus.Window, restore);
            }
        }

        /// <summary>The layouts in the user's language list, as Windows can select them.</summary>
        public static List<KeyboardInputSource> EnabledSources()
        {
            return Layouts().Select(layout => new KeyboardInputSource(Id(layout), Describe(layout))).ToList();
        }

        private void Refresh()
        {
            refreshScheduled = false;
            if (stopped || (target is null && lockState.Baseline is null) || Keyboard() is not Focus focus)
            {
                poll.Stop();
                return;
            }

            HashSet<string> enabled = Layouts().Select(Id).ToHashSet();
            string current = Current(focus);
            bool terminalFocused = focus.Ours && focus.InPage && terminalFocus.TryGetValue(focus.Foreground, out bool focused) && focused;
            if (lockState.Reconcile(terminalFocused, target, enabled, current) is string wanted && !Select(focus.Window, wanted))
            {
                lockState.SelectionFailed();
            }

            // A window of ours keeps being read while the keyboard settles into its page after activation, and while the
            // lock holds a switch the user makes is found the same way.
            poll.IsEnabled = lockState.Baseline is not null || (target is not null && focus.Ours);
        }

        /// <summary>
        /// The layout in effect. Windows hands it to the thread that gets the keyboard lazily, so right after the keyboard
        /// leaves our page, for another application or for our own window's thread, that thread may still show its own earlier
        /// one; there it is the layout last read in our page, which is what Windows will hand over.
        /// </summary>
        private string Current(Focus focus)
        {
            if (focus.Ours && focus.InPage)
            {
                lastInPage = focus.Layout;
                return focus.Layout;
            }

            return lastInPage ?? focus.Layout;
        }

        /// <summary>
        /// The window that has the keyboard, whatever application it is in, and the layout of its thread. In a window of ours
        /// the keyboard is in its page once it is in WebView2's process; just after activation it is still in our own.
        /// </summary>
        private Focus? Keyboard()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return null;
            }

            GuiThreadInfo info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            IntPtr window = GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) && info.Focus != IntPtr.Zero ? info.Focus : foreground;
            uint thread = GetWindowThreadProcessId(window, out uint process);
            return new Focus(foreground, window, Id(GetKeyboardLayout(thread)), terminalFocus.ContainsKey(foreground), process != Environment.ProcessId);
        }

        /// <summary>Where the keyboard is: the foreground window, the window in it with focus and that thread's layout.</summary>
        private sealed record Focus(IntPtr Foreground, IntPtr Window, string Layout, bool Ours, bool InPage);

        private static bool Select(IntPtr window, string id)
        {
            return uint.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint layout)
                && SendMessageTimeout(window, InputLanguageChangeRequest, IntPtr.Zero, (IntPtr)unchecked((int)layout), 0x2, 100, out _) != IntPtr.Zero
                && Id(GetKeyboardLayout(GetWindowThreadProcessId(window, out _))) == id;
        }

        private static IntPtr[] Layouts()
        {
            int count = GetKeyboardLayoutList(0, null);
            IntPtr[] layouts = new IntPtr[count];
            return GetKeyboardLayoutList(count, layouts) == count ? layouts : Array.Empty<IntPtr>();
        }

        /// <summary>A layout's handle as eight hex digits: the language in the low word, the layout in the high one.</summary>
        private static string Id(IntPtr layout)
        {
            return ((uint)(layout.ToInt64() & 0xFFFFFFFF)).ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>The language and the layout's own name, as Windows lists them.</summary>
        private static string Describe(IntPtr layout)
        {
            uint value = (uint)(layout.ToInt64() & 0xFFFFFFFF);
            int language = (int)(value & 0xFFFF);
            string languageName;
            try
            {
                languageName = CultureInfo.GetCultureInfo(language).DisplayName;
            }
            catch (CultureNotFoundException)
            {
                languageName = language.ToString("X4", CultureInfo.InvariantCulture);
            }

            return LayoutName(value) is string name ? languageName + " — " + name : languageName;
        }

        /// <summary>The name of the layout a handle names, from the registry's list of keyboard layouts.</summary>
        private static string? LayoutName(uint value)
        {
            uint device = value >> 16;
            using RegistryKey? layouts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            if (layouts is null)
            {
                return null;
            }

            string? klid = (device & 0xF000) switch
            {
                // A layout id: the variant whose Layout Id says so.
                0xF000 => layouts.GetSubKeyNames().FirstOrDefault(name =>
                {
                    using RegistryKey? key = layouts.OpenSubKey(name);
                    return key?.GetValue("Layout Id") is string id && int.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number) && number == (device & 0x0FFF);
                }),
                // An input method: the handle itself.
                0xE000 => value.ToString("X8", CultureInfo.InvariantCulture),
                _ => device.ToString("X8", CultureInfo.InvariantCulture),
            };
            if (klid is null)
            {
                return null;
            }

            using RegistryKey? entry = layouts.OpenSubKey(klid);
            if (entry?.GetValue("Layout Display Name") is string indirect)
            {
                StringBuilder text = new StringBuilder(256);
                if (SHLoadIndirectString(indirect, text, text.Capacity, IntPtr.Zero) == 0)
                {
                    return text.ToString();
                }
            }

            return entry?.GetValue("Layout Text") as string;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr Caret;
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint thread);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int count, IntPtr[]? layouts);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
    }
}
