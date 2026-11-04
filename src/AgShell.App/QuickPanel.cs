using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgShell.App
{
    /// <summary>
    /// agterm's quick terminal: one floating panel for the whole app, over the screen of the window in front, with one
    /// shell that lives on while the panel is hidden. The user's toggle closes it again when it loses the keyboard; a
    /// panel shown over the control pipe stays until something hides it. The page is the windows' own, in its #quick mode.
    /// </summary>
    internal sealed class QuickPanel : IClipboardPromptView
    {
        private static readonly PaneKey Key = new PaneKey(Guid.Empty, PaneRole.Quick);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly WindowLibrary library;
        private readonly SessionHost host = new SessionHost();
        private readonly TerminalClipboardHost clipboard;
        private readonly Dictionary<int, TaskCompletionSource<string?>> pending = new Dictionary<int, TaskCompletionSource<string?>>();
        private Window? window;
        private WebView2? webView;
        private bool pageReady;
        private int nextRequest;
        private int flushQueued;
        private DateTime hiddenByFocus;

        public QuickPanel(WindowLibrary library)
        {
            this.library = library;
            // The panel's terminal answers the clipboard as every terminal does, under the front window's setting.
            clipboard = new TerminalClipboardHost(this, (key, data) => host.Write(key, data), () => library.Ghostty.ClipboardRead, () => library.ClipboardWritePolicy, _ => Running, _ => library.Active is MainWindow front ? front.ClipboardText() is { Length: > 0 } text ? text : null : null);
            host.OutputPending += () =>
            {
                if (Interlocked.Exchange(ref flushQueued, 1) == 0)
                {
                    Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                    {
                        Volatile.Write(ref flushQueued, 0);
                        host.Flush(Post);
                    });
                }
            };
            host.Exited += (shell, code) => Application.Current.Dispatcher.BeginInvoke(() => OnExited(shell));
        }

        public bool Shown { get; private set; }

        /// <summary>Shown over the control pipe: it stays when the keyboard goes elsewhere.</summary>
        public bool Pinned { get; private set; }

        /// <summary>agterm's surface zoom --target quick: the panel fills its screen.</summary>
        public bool Zoomed { get; private set; }

        /// <summary>The panel terminal's own font size from the font keys, as agterm's surface keeps one; null at the settings' size.</summary>
        public double? FontSize { get; private set; }

        public bool Running => host.IsRunning(Key);

        /// <summary>Raised when the panel shows, hides or its shell starts or ends, so the windows can show it.</summary>
        public event Action? Changed;

        /// <summary>
        /// The user's toggle, from a key or the title bar button. A click on the button first takes the keyboard from the
        /// panel, which hides it, so a toggle right after that hiding leaves it hidden instead of bringing it back. As
        /// agterm's, the global hotkey skips that wait: a key pressed in another program takes no keyboard from the panel.
        /// </summary>
        public void Toggle()
        {
            Toggle(fromGlobalHotkey: false);
        }

        public void Toggle(bool fromGlobalHotkey)
        {
            if (!fromGlobalHotkey && !Shown && DateTime.UtcNow - hiddenByFocus < TimeSpan.FromMilliseconds(300))
            {
                return;
            }

            SetShown(!Shown, pinned: false);
        }

        public void SetShown(bool shown, bool pinned)
        {
            if (!shown)
            {
                Hide();
                return;
            }

            // agterm's canShow: no window to belong to, or a picker that owns the keyboard, and the panel stays away.
            if (library.Active is not MainWindow front || front.PickPending)
            {
                return;
            }

            // agterm: a script's show pins the panel, and the user's own show over it does not take the pin away.
            Pinned = Shown ? Pinned || pinned : pinned;
            if (Shown)
            {
                window!.Activate();
                return;
            }

            Shown = true;
            Window panel = window ?? Create();
            Place(panel);
            panel.Show();
            panel.Activate();
            webView?.Focus();
            PostState();
            Changed?.Invoke();
        }

        public void Hide()
        {
            if (!Shown)
            {
                return;
            }

            Shown = false;
            Pinned = false;
            Zoomed = false;
            window?.Hide();
            PostState();
            Changed?.Invoke();
        }

        /// <summary>agterm's ⌘W rung for the panel: a zoomed panel un-zooms first, the next close hides it.</summary>
        public void CloseRung()
        {
            if (Zoomed)
            {
                SetZoom(false);
            }
            else
            {
                Hide();
            }
        }

        public void SetZoom(bool zoomed)
        {
            if (Zoomed == zoomed)
            {
                return;
            }

            Zoomed = zoomed;
            if (window is not null && Shown)
            {
                Place(window);
                window.Activate();
                webView?.Focus();
            }

            PostState();
        }

        public void Write(string data)
        {
            host.Write(Key, data);
        }

        /// <summary>Reads the panel's terminal back, as session text does; null when it cannot be read.</summary>
        public async Task<string?> ReadTextAsync(bool all, int? lines)
        {
            try
            {
                return await AskPageAsync(id => new { type = "readText", requestId = id, sessionId = Guid.Empty, pane = "quick", all, lines });
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>The column of the panel terminal's cursor, as surface cursor reads it; null before it has one.</summary>
        public Task<string?> CursorAsync()
        {
            return AskPageAsync(id => new { type = "terminal", requestId = id, sessionId = Guid.Empty, pane = "quick", action = "cursor", text = (string?)null });
        }

        private async Task<string?> AskPageAsync(Func<int, object> message)
        {
            if (webView?.CoreWebView2 is null || !pageReady)
            {
                return null;
            }

            int id = ++nextRequest;
            TaskCompletionSource<string?> reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = reply;
            Post(message(id));
            Task finished = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            pending.Remove(id);
            return finished == reply.Task ? reply.Task.Result : throw new TimeoutException("the quick terminal did not answer");
        }

        /// <summary>The look follows the windows: a window's settings, theme or keys changed.</summary>
        public void Refresh()
        {
            PostState();
        }

        public void Dispose()
        {
            host.Dispose();
            window?.Close();
        }

        private Window Create()
        {
            webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1A, 0x18) };
            window = new Window
            {
                Title = "AgShell quick terminal",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1A, 0x18)),
                Content = webView,
            };
            // agterm's panel has rounded corners and a thin light outline; Windows 11 draws both for the window itself.
            window.SourceInitialized += (sender, args) =>
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                int round = 2;
                DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
                // White at 18% over the panel's dark backing, as a COLORREF (0x00BBGGRR).
                int outline = 0x00443F44;
                DwmSetWindowAttribute(handle, 34, ref outline, sizeof(int));
            };
            window.Activated += (sender, args) => { library.ReconcileShortcutOwnership(); TerminalInputSource.Shared.RequestRefresh(); };
            window.Deactivated += (sender, args) =>
            {
                TerminalInputSource.Shared.RequestRefresh();
                library.ShortcutWindowResigned();
                if (!Pinned && Shown)
                {
                    hiddenByFocus = DateTime.UtcNow;
                    Hide();
                }
            };
            // The panel only hides: its page and shell stay for the next time.
            window.Closing += (sender, args) =>
            {
                if (!library.ShuttingDown)
                {
                    args.Cancel = true;
                    Hide();
                }
            };
            window.Loaded += async (sender, args) => await LoadPageAsync();
            return window;
        }

        private async Task LoadPageAsync()
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, library.WebViewDataFolder);
            await webView!.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = webView.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            string? devUrl = Environment.GetEnvironmentVariable("AGSHELL_DEV_URL");
            if (!string.IsNullOrEmpty(devUrl))
            {
                core.Navigate(devUrl + "#quick");
            }
            else
            {
                core.Settings.AreDevToolsEnabled = false;
                core.SetVirtualHostNameToFolderMapping(MainWindow.VirtualHostName, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
                core.Navigate("https://" + MainWindow.VirtualHostName + "/index.html#quick");
            }
        }

        /// <summary>
        /// agterm's panel: 90% of the screen, at most 1100×700 unless the settings give a share; zoomed, all of it. It opens
        /// on the screen under the mouse pointer, as agterm's, since it is called from other programs; the front window's
        /// screen only when the pointer is on none.
        /// </summary>
        private void Place(Window panel)
        {
            List<Displays.Display> displays = Displays.All();
            MainWindow? front = library.Active;
            int index = Displays.UnderPointer(displays) ?? (front is null ? 0 : Displays.Of(new WindowInteropHelper(front).Handle, displays));
            double scale = Displays.Scale(displays[index]);
            Rect pixels = displays[index].WorkArea;
            Rect area = new Rect(pixels.Left / scale, pixels.Top / scale, pixels.Width / scale, pixels.Height / scale);
            double share = library.Settings.EffectiveQuickTerminalSizePercent / 100.0;
            double width = Zoomed ? area.Width : area.Width * share;
            double height = Zoomed ? area.Height : area.Height * share;
            if (!Zoomed && library.Settings.QuickTerminalSizeSetting is null)
            {
                width = Math.Min(width, 1100);
                height = Math.Min(height, 700);
            }

            panel.Width = width;
            panel.Height = height;
            panel.Left = area.Left + (area.Width - width) / 2;
            panel.Top = area.Top + (area.Height - height) / 2;
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    pageReady = true;
                    PostState();
                    library.BroadcastShortcuts();
                    break;
                case "shortcutState":
                    library.ReceiveShortcuts(message, window?.IsActive == true);
                    break;
                case "attach":
                    int columns = message.GetProperty("cols").GetInt32();
                    int rows = message.GetProperty("rows").GetInt32();
                    if (Running)
                    {
                        host.Resize(Key, columns, rows);
                    }
                    else if (Shown && library.Active is MainWindow front)
                    {
                        StartShell(front, columns, rows);
                    }

                    break;
                case "input":
                    host.Write(Key, message.GetProperty("data").GetString() ?? string.Empty);
                    if (message.TryGetProperty("stroke", out _))
                    {
                        library.Active?.NoteQuickActivity();
                    }

                    break;
                case "osc52":
                    clipboard.Osc52(Key, message.GetProperty("data").GetString() ?? string.Empty);
                    break;
                case "osc5522":
                    clipboard.Osc5522(Key, message.GetProperty("data").GetString() ?? string.Empty, message.TryGetProperty("bel", out JsonElement bel) && bel.GetBoolean());
                    break;
                case "kittyPaste":
                    clipboard.PasteEvent(Key, message.TryGetProperty("selection", out JsonElement fromSelection) && fromSelection.GetBoolean());
                    break;
                case "selectionCopied":
                    TerminalClipboardHost.Selection = message.TryGetProperty("text", out JsonElement selected) ? selected.GetString() : null;
                    break;
                case "resize":
                    host.Resize(Key, message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    break;
                case "reply":
                    if (pending.Remove(message.GetProperty("requestId").GetInt32(), out TaskCompletionSource<string?>? reply))
                    {
                        bool missing = message.TryGetProperty("missing", out JsonElement absent) && absent.ValueKind == JsonValueKind.True;
                        reply.TrySetResult(missing ? null : message.GetProperty("text").GetString() ?? string.Empty);
                    }

                    break;
                case "terminalFocus":
                    TerminalInputSource.Shared.Report(new WindowInteropHelper(window!).Handle, message.GetProperty("value").GetBoolean());
                    break;
                case "command":
                    // The quick terminal's own toggle closes the panel; links and the right-click paste go as a window's do.
                    if (message.GetProperty("command").GetString() == "setQuick")
                    {
                        Hide();
                    }
                    else if (message.GetProperty("command").GetString() == "runCustom" && library.Active is MainWindow customWindow)
                    {
                        string name = message.GetProperty("name").GetString() ?? string.Empty;
                        _ = customWindow.RunPageCustomAsync(name, customWindow.Model.SelectedSessionId, null, null, keybind: true);
                    }
                    else if (message.GetProperty("command").GetString() == "quickAction")
                    {
                        // agterm's menu acts on the active window while the panel holds the keyboard; close peels that window's asks first.
                        string name = message.GetProperty("name").GetString() ?? string.Empty;
                        if (library.Active is MainWindow actionWindow)
                        {
                            actionWindow.RunQuickAction(name);
                        }
                        else if (name == "close_session")
                        {
                            CloseRung();
                        }
                    }
                    else if (message.GetProperty("command").GetString() == "setZoom")
                    {
                        // agterm's toggleTerminalZoom and ⌘W over the panel zoom the panel itself; the toggle not while the
                        // active window holds a pick or an ask.
                        if (message.TryGetProperty("zoomed", out JsonElement zoomed) && zoomed.ValueKind != JsonValueKind.Null)
                        {
                            SetZoom(zoomed.GetBoolean());
                        }
                        else if (library.Active is not MainWindow { ModalPending: true })
                        {
                            SetZoom(!Zoomed);
                        }
                    }
                    else if (message.GetProperty("command").GetString() == "changeFont")
                    {
                        ChangeFont(message.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Number ? delta.GetDouble() : null);
                    }
                    else if (message.GetProperty("command").GetString() == "openLink" && library.Active is MainWindow linkWindow)
                    {
                        linkWindow.OpenQuickLink(message.GetProperty("url").GetString() ?? string.Empty);
                    }
                    else if (message.GetProperty("command").GetString() == "paste" && library.Active is MainWindow pasteWindow)
                    {
                        Post(new { type = "paste", sessionId = Guid.Empty, pane = "quick", text = pasteWindow.ClipboardText(), clipboard = true });
                    }
                    else if (message.GetProperty("command").GetString() == "pasteSelection")
                    {
                        Post(new { type = "paste", sessionId = Guid.Empty, pane = "quick", text = TerminalClipboardHost.Selection ?? string.Empty, clipboard = true, selection = true });
                    }
                    else if (message.GetProperty("command").GetString() == "clipboardPrompt")
                    {
                        ClipboardPrompts.Shared.Answer(message.GetProperty("id").GetString() ?? string.Empty, message.GetProperty("allow").GetBoolean(), message.GetProperty("remember").GetBoolean());
                    }

                    break;
            }
        }

        /// <summary>agterm's font keys over the panel change its own terminal, as they change the focused one in a window.</summary>
        private void ChangeFont(double? delta)
        {
            double baseSize = library.TerminalAppearance.FontSize;
            double size = AppModel.SteppedFontSize(FontSize ?? baseSize, delta, baseSize);
            FontSize = size == baseSize ? null : size;
            PostState();
        }

        private void StartShell(MainWindow front, int columns, int rows)
        {
            try
            {
                host.Start(Key, front.QuickStartOptions(columns, rows));
                PostState();
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Post(Key, Strings.T("Не удалось запустить шелл: {0}", ex.Message) + "\r\n");
            }
        }

        private void OnExited(object shell)
        {
            if (host.KeyOf(shell) is not PaneKey)
            {
                return;
            }

            host.Flush(Post);
            host.Stop(Key);
            clipboard.Forget(Key);
            // The next shell is a new surface, which starts at the settings' size.
            FontSize = null;
            Hide();
            PostState();
            Changed?.Invoke();
        }

        /// <summary>The page's state: the quick terminal and what it needs to look like the windows, from the one in front.</summary>
        void IClipboardPromptView.ClipboardPromptsChanged()
        {
            PostState();
        }

        private void PostState()
        {
            if (!pageReady || library.Active is not MainWindow front)
            {
                return;
            }

            (object settings, object theme, object keymap) = front.QuickLook();
            JsonNode settingsNode = JsonSerializer.SerializeToNode(settings, JsonOptions)!;
            // The panel is a plain window: the system backdrop the main windows show through is not behind it.
            settingsNode["backdropSupported"] = false;
            Post(new
            {
                type = "state",
                state = new
                {
                    quickPage = true,
                    settings = settingsNode,
                    theme,
                    keymap,
                    windowsBuild = Environment.OSVersion.Version.Build,
                    quick = new { shown = Shown, pinned = Pinned, running = Running, zoomed = Zoomed, fontSize = FontSize },
                    clipboardPrompt = ClipboardPrompts.Shared.FirstFor(this),
                },
            });
        }

        private void Post(PaneKey key, string text)
        {
            Post(new { type = "output", sessionId = Guid.Empty, pane = "quick", data = text });
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal void PostShortcuts(object message) => Post(message);

        private void Post(object message)
        {
            webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }
    }
}
