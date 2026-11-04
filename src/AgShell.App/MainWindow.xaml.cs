using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;
using Microsoft.Web.WebView2.Core;

namespace AgShell.App
{
    public partial class MainWindow : Window, IControlHost, IClipboardPromptView
    {
        /// <summary>
        /// Under localhost, which SmartScreen does not look up: any other name it holds for about two seconds on every
        /// navigation, the window's start included.
        /// </summary>
        private const string VirtualHost = "app.agshell.localhost";

        /// <summary>The host the page is served under, for the quick panel too.</summary>
        internal const string VirtualHostName = VirtualHost;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly StateStore store;
        private readonly AppModel model;
        private ConfigEditorReload? ghosttyEditorReload;

        /// <summary>The window's model, which owns its questions' results in <see cref="WindowLibrary.AskResults"/>.</summary>
        internal AppModel Model => model;

        private readonly SessionHost host = new SessionHost();

        /// <summary>Command sessions whose program runs in the main pane now, and those held open after it exited (--wait).</summary>
        /// <summary>The panes running their session's command in this launch, and those holding its output after it ended (--wait).</summary>
        private readonly HashSet<PaneKey> commandRuns = new HashSet<PaneKey>();
        private readonly HashSet<PaneKey> commandHeld = new HashSet<PaneKey>();
        private readonly DispatcherTimer flushTimer;
        private readonly SwitcherEscape switcherEscape;
        private readonly Stopwatch sinceFlush = Stopwatch.StartNew();
        private int flushQueued;
        private readonly DispatcherTimer saveTimer;
        private readonly Task<IReadOnlyList<ShellProfile>> profilesTask;
        private readonly WindowEntry entry;
        private readonly ControlDispatcher dispatcher;
        private readonly Dictionary<int, TaskCompletionSource<string?>> pendingPageRequests = new Dictionary<int, TaskCompletionSource<string?>>();
        private PageHost? pageHost;
        private bool welcomeDue;
        private NativePages? nativePages;

        /// <summary>The selected session's HUD drawn again over a raised page, as agterm's lies above every page.</summary>
        private HudFloat? hudFloat;
        private string ConfigFolder => Library.ConfigFolder;

        private string keymapPath => Path.Combine(ConfigFolder, "keymap.conf");

        private string hooksPath => Path.Combine(ConfigFolder, "hooks.conf");

        private string? watchedFolder;
        private readonly DispatcherTimer reloadTimer;
        private readonly DispatcherTimer treeTimer;
        private KeymapResult keymap = Keymap.Parse(string.Empty);
        private bool keymapLoaded;
        private List<string> hookDiagnostics = new List<string>();
        private string? hotkeyError;
        private FileSystemWatcher? configWatcher;
        private int nextPageRequestId;
        private IReadOnlyList<ShellProfile> profiles = Array.Empty<ShellProfile>();
        private SidebarSettings sidebar;
        private string pageOrigin = "https://" + VirtualHost;
        private bool pageReady;
        private readonly TerminalClipboardHost clipboard;

        internal MainWindow(WindowEntry entry, bool launchRestore)
        {
            InitializeComponent();
            ReserveMenuBar();
            // Clear, so a covered page shows through the interface's hole; the window's own background is under it.
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            this.entry = entry;
            this.launchRestore = launchRestore;
            profilesTask = Task.Run(ShellProfiles.Detect);
            string dataDirectory = Library.DataDirectory;
            store = new StateStore(Path.Combine(dataDirectory, entry.StateFile));
            StateSnapshot? snapshot = store.Load();
            // agterm's loadSnapshotForStore: panes saved without ids get them, and the file at once.
            if (snapshot is not null && PaneIdentityInventory.Run(snapshot).Changed)
            {
                try
                {
                    store.Save(snapshot);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Notice("WindowLibrary", "pane inventory could not persist an open window upgrade: " + ex.Message);
                }
            }

            model = snapshot is null ? new AppModel() : AppModel.FromSnapshot(snapshot, launchRestore);
            // As agterm's loadStore: the captures leave the file at once, armed only in the sessions; a launch that cannot
            // strip them runs none, since a crash would otherwise run them again next time.
            if (snapshot is not null && AppModel.CarriesCaptures(snapshot))
            {
                try
                {
                    store.Save(AppModel.StripCaptures(snapshot));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Notice("WindowLibrary", "stripCaptures failed: " + ex.Message);
                    model.ClearPendingCaptures();
                }
            }

            // agterm's recovery cannot tell a window closed on purpose from one open at the loss, so it replays nothing.
            if (entry.Recovered)
            {
                model.ClearPendingCaptures();
            }

            // agterm's restore: a stored width outside 160 to 560 is clamped into it.
            SidebarSettings stored = snapshot?.Sidebar ?? new SidebarSettings();
            sidebar = stored with { Width = double.IsFinite(stored.Width) ? Math.Clamp(stored.Width, MinSidebarWidth, MaxSidebarWidth) : new SidebarSettings().Width };
            dispatcher = new ControlDispatcher(model, this);
            clipboard = new TerminalClipboardHost(this, ReplyToPane, () => Library.Ghostty.ClipboardRead, () => Library.ClipboardWritePolicy, pane => model.FindSession(pane.SessionId) is not null, ClipboardTextFor);
            InitChrome();
            InitAppearance();
            // As agterm's: never twice, and never for someone who ran it before the welcome existed.
            welcomeDue = FirstRunWelcome.IsDue(settings.WelcomeShown, Library.HadState);
            InitRestore();
            InitClosing();
            InitAttention();
            Library.WindowsChanged += OnWindowsChanged;
            reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (sender, args) => OnConfigFilesChanged(), Dispatcher);
            reloadTimer.Stop();
            // As in agterm, tree.changed is coalesced over 100 ms: it says the tree moved, not what happened.
            treeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (sender, args) =>
            {
                treeTimer!.Stop();
                model.Events.Append(ControlEventKinds.TreeChanged, null, null, new ControlEventPayload());
            }, Dispatcher);
            treeTimer.Stop();
            model.TreeChanged += () =>
            {
                treeTimer.Stop();
                treeTimer.Start();
            };
            ApplyPlacement(snapshot?.Window);

            switcherEscape = new SwitcherEscape(this, () => Post(new { type = "switcherEscape" }));
            flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal, (sender, args) => FlushOutput(), Dispatcher);
            // A keystroke's echo goes out at once instead of waiting for the timer, which Windows ticks every ~15 ms;
            // a stream of output still leaves in timer-sized batches.
            host.OutputPending += () =>
            {
                if (Interlocked.Exchange(ref flushQueued, 1) == 0)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                    {
                        Volatile.Write(ref flushQueued, 0);
                        if (flushTimer.IsEnabled && sinceFlush.ElapsedMilliseconds >= 10)
                        {
                            FlushOutput();
                        }
                    });
                }
            };
            saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (sender, args) => SaveNow(), Dispatcher);
            saveTimer.Stop();

            model.StructureChanged += () => SaveNow();
            model.LiveFailureChanged += () => SaveNow(preservePending: true);
            model.PendingSaveCancelled += () => saveTimer.Stop();
            // agterm's scheduleSave: every change moves the write to 300 ms after the last one.
            model.SelectionChanged += () =>
            {
                saveTimer.Stop();
                saveTimer.Start();
            };
            // agterm's onChange(of: valid): a GUI question whose pane went off screen is cancelled before the state is drawn.
            model.Changed += model.CancelGuiAskOffAnchor;
            model.Changed += PostState;
            // agterm: a pick coming up hides the quick terminal, pinned or not, so nothing sits over the picker; so does a
            // dashboard opening (handleDashboardOpenChange).
            string? shownPick = null;
            bool dashboardShown = false;
            model.Changed += () =>
            {
                string? pick = model.Pick?.Id;
                bool dashboard = model.Dashboard is not null;
                if ((pick is not null && pick != shownPick) || (dashboard && !dashboardShown))
                {
                    Library.Quick.Hide();
                }

                shownPick = pick;
                dashboardShown = dashboard;
            };
            model.Changed += Library.NotifyModelChanged;
            model.TreeChanged += Library.NotifyModelChanged;
            // agterm's pane.focused, settled over 100 ms per window.
            DispatcherTimer paneFocusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (sender, args) =>
            {
                ((DispatcherTimer)sender!).Stop();
                foreach ((Guid sessionId, ControlEventPayload payload) in paneFocus.Settle(model.FindSession))
                {
                    model.Events.Append(ControlEventKinds.PaneFocused, model.FindWorkspaceOf(sessionId)?.Id, sessionId, payload);
                }
            }, Dispatcher);
            paneFocusTimer.Stop();
            // The sessions there already are the baseline: the model says nothing until something changes.
            paneFocus.Observe(model.AllSessions);
            model.Changed += () =>
            {
                if (paneFocus.Observe(model.AllSessions))
                {
                    paneFocusTimer.Stop();
                    paneFocusTimer.Start();
                }
            };
            model.LabelChanged += session =>
            {
                Post(new { type = "label", sessionId = session.Id, name = session.DisplayName });
                SyncWindowTitle();
            };
            host.Exited += (shell, code) => Dispatcher.BeginInvoke(() => OnShellExited(shell, code));
            host.LeadChanged += (shell, leads) => Dispatcher.BeginInvoke(() =>
            {
                if (host.KeyOf(shell) is PaneKey changed)
                {
                    claiming.Remove(changed);
                    reattaching.Remove(changed);
                    if (leads == "unowned")
                    {
                        Reattach(changed, claim: false);
                        return;
                    }
                }

                PostState();
            });
            StartRemoteAsks();

            Loaded += OnLoaded;
            StateChanged += (sender, args) =>
            {
                if (WindowState != WindowState.Minimized)
                {
                    shownState = WindowState;
                }
            };
            Activated += OnActivated;
            Deactivated += (sender, args) => { PostWindowActive(); Library.ShortcutWindowResigned(); };
            model.Seen += ClearDeliveredBanners;
            Closing += (sender, args) => SaveNow();
            Closed += OnClosed;
        }

        private void OnActivated(object? sender, EventArgs e)
        {
            Library.ReconcileShortcutOwnership();
            // As agterm's, coming back counts as seeing a session only when its pane has the keyboard: the page says so.
            webView.Focus();
            PostWindowActive();
        }

        /// <summary>agterm's emphasized selection: the page dims the sidebar's selection while the window is not the active one.</summary>
        private void PostWindowActive()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (pageReady) Post(new { type = "windowActive", value = IsActive, appActive = Application.Current.Windows.OfType<Window>().Any(window => window.IsActive) });
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// agterm's banner click: the window comes up out of a zoom, and the session it named is selected with the pane that
        /// fired it in focus.
        /// </summary>
        internal void RevealFromBanner(Guid sessionId, string? pane)
        {
            if (model.FindSession(sessionId) is not Session session)
            {
                return;
            }

            Deminiaturize();
            Activate();
            model.SetZoom(false);
            NoteActivity();
            model.SelectSession(sessionId);
            if (session.HasSplit)
            {
                model.FocusPane(sessionId, pane == "right" ? PaneRole.Split : PaneRole.Main);
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Library.WebViewDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                profiles = await profilesTask;
                model.AdoptDefaultProfile(DefaultProfile().Id);
                EnsureInitialSession();
                QueueReplays();
                SessionsReady = true;
                Ready?.Invoke();
                LoadConfig();
                WatchConfig();

                CoreWebView2 core = webView.CoreWebView2;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigationStarting += OnNavigationStarting;
                string bridge = Path.Combine(AppContext.BaseDirectory, "web", "page-bridge.js");
                pageHost = new PageHost(File.Exists(bridge) ? File.ReadAllText(bridge) : string.Empty);
                pageHost.Attach(core, environment);
                nativePages = new NativePages(pageLayer, environment, FindOverlay, pageHost, () => webView.Handle);
                hudFloat = new HudFloat(this, pageLayer, () => nativePages.RaisedBounds(), url => OpenLink(url, model.SelectedSessionId, model.FindSession(model.SelectedSessionId ?? Guid.Empty)?.Overlay?.HudPane == PaneRole.Split ? "right" : "left"));
                LocationChanged += (sender, args) => hudFloat.Refresh();
                SizeChanged += (sender, args) => hudFloat.Refresh();
                StateChanged += (sender, args) => hudFloat.Refresh();
                Closed += (sender, args) => hudFloat.Close();
                nativePages.SetTheme(theme);
                nativePages.BridgeRequest += OnPageRequest;
                nativePages.CloseRequested += overlayId =>
                {
                    if (model.AllSessions.SelectMany(session => session.Overlays.Where(pair => pair.Value.Id == overlayId).Select(pair => (session.Id, pair.Key))).FirstOrDefault() is (Guid sessionId, PaneRole slot) && sessionId != Guid.Empty)
                    {
                        model.CloseOverlay(sessionId, overlayId, slot);
                    }
                };
                nativePages.SetZoom(settings.EffectiveHtmlOverlayZoom);
                nativePages.Changed += PostPageInfo;
                // As agterm's: a page over one pane that takes the keyboard moves the split's focus there, as its terminal would.
                nativePages.Focused += overlayId =>
                {
                    if (model.AllSessions.SelectMany(session => session.Overlays.Where(pair => pair.Value.Id == overlayId).Select(pair => (session.Id, pair.Key))).FirstOrDefault() is (Guid sessionId, PaneRole slot)
                        && sessionId != Guid.Empty && slot is PaneRole.OverlayLeft or PaneRole.OverlayRight)
                    {
                        model.FocusPane(sessionId, slot == PaneRole.OverlayLeft ? PaneRole.Main : PaneRole.Split);
                    }
                };
                nativePages.KeyForwarded += key => Post(new { type = "forwardKey", code = key.Code, key = key.Key, ctrlKey = key.Ctrl, shiftKey = key.Shift, altKey = key.Alt });
                nativePages.KeyboardDropped += () =>
                {
                    webView.Focus();
                    // Ctrl let go before the keyboard came back: its key-up went to the hidden page.
                    if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                    {
                        Post(new { type = "ctrlReleased" });
                    }
                };
                core.NewWindowRequested += OnNewWindowRequested;
                core.NavigationCompleted += (s, args) => webView.Focus();

                string? devUrl = Environment.GetEnvironmentVariable("AGSHELL_DEV_URL");
                if (!string.IsNullOrEmpty(devUrl))
                {
                    pageOrigin = new Uri(devUrl).GetLeftPart(UriPartial.Authority);
                    core.Navigate(devUrl);
                }
                else
                {
                    core.Settings.AreDevToolsEnabled = false;
                    core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
                    core.Navigate(pageOrigin + "/index.html");
                }

                // agterm's appLaunchSettled: once a run, with the first window ready; a window opened later is not a launch.
                if (Library.TakeLaunch())
                {
                    model.Events.Append(ControlEventKinds.AppLaunched, null, null, new ControlEventPayload());
                }
            }
            catch (Exception ex)
            {
                // agterm's ghostty_init failure: the terminal engine did not come up.
                Log.Error("GhosttyApp", "WebView2 init failed: " + ex.Message);
                MessageBox.Show(this, Strings.T("Не удалось запустить WebView2: {0}", ex.Message), "AgShell", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        /// <summary>Reads keymap.conf and hooks.conf, writing commented starters the first time, and applies them.</summary>
        /// <summary>Reads keymap.conf, hooks.conf or both, as agterm's Reload buttons read one file each.</summary>
        private IReadOnlyList<string> LoadConfig(bool keymapFile = true, bool hooksFile = true)
        {
            foreach ((string path, string starter, string name) in new[] { (keymapPath, Keymap.Starter, "keymap"), (hooksPath, HooksConfig.Starter, "hooks") })
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, starter + Environment.NewLine);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Notice("SettingsModel", "could not write starter " + name + " at " + path + ": " + ex.Message);
                }
            }

            if (keymapFile)
            {
                keymap = Keymap.Parse(ReadShared(keymapPath));
                if (keymapLoaded) Library.ResetShortcuts();
                keymapLoaded = true;
                Post(new { type = "keymapChanged" });
            }

            if (hooksFile)
            {
                (List<HookLine> hooks, List<string> diagnostics) = HooksConfig.Parse(ReadShared(hooksPath));
                hookDiagnostics = diagnostics;
                Library.Hooks.Apply(hooks);
            }

            if (keymapFile)
            {
                hotkeyError = Library.RegisterHotkey(keymap.GlobalHotkey);
            }

            PostState();
            if (Library.Active == this)
            {
                Library.SettingsWindow.Refresh();
            }

            return keymap.Diagnostics.Select(message => "keymap.conf " + message)
                .Concat(hookDiagnostics.Select(message => "hooks.conf " + message))
                .Concat(hotkeyError is null ? Array.Empty<string>() : new[] { "keymap.conf " + hotkeyError })
                .ToList();
        }

        /// <summary>Reads a file an editor may still hold open, retrying briefly while it is being saved.</summary>
        private static string ReadShared(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using StreamReader reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50);
                }
                catch (FileNotFoundException)
                {
                    return string.Empty;
                }
            }
        }

        private void WatchConfig()
        {
            configWatcher?.Dispose();
            watchedFolder = ConfigFolder;
            configWatcher = new FileSystemWatcher(ConfigFolder, "*.conf")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (sender, args) => Dispatcher.BeginInvoke(() =>
            {
                reloadTimer.Stop();
                reloadTimer.Start();
            });
            configWatcher.Changed += changed;
            configWatcher.Created += changed;
            configWatcher.Renamed += (sender, args) => changed(sender, args);
        }

        /// <summary>agterm's "N issues — see Settings ▸ Key Mapping".</summary>
        private static string IssuesSeeSettings(int count)
        {
            return count == 1 ? Strings.T("1 проблема — см. Настройки ▸ Клавиши и хуки") : Strings.T("Проблем: {0} — см. Настройки ▸ Клавиши и хуки", count);
        }

        /// <summary>agterm's launch banners for the keymap and hooks.conf, read when the window opened.</summary>
        internal void NotifyLaunchDiagnostics()
        {
            int keymapIssues = keymap.Diagnostics.Count + (hotkeyError is null ? 0 : 1);
            if (keymapIssues > 0)
            {
                NotifyApp("keymap-diagnostics", Strings.T("Клавиши"), IssuesSeeSettings(keymapIssues));
            }

            if (hookDiagnostics.Count > 0)
            {
                NotifyApp("hooks-diagnostics", Strings.T("Хуки"), IssuesSeeSettings(hookDiagnostics.Count));
            }
        }

        /// <summary>A saved keymap or hooks file applies at once; its problems are announced rather than silently ignored.</summary>
        private void OnConfigFilesChanged(bool keymapFile = true, bool hooksFile = true)
        {
            reloadTimer.Stop();
            LoadConfig(keymapFile, hooksFile);
            // agterm's diagnostics banners: one per file, its count and where to look.
            int keymapIssues = keymap.Diagnostics.Count + (hotkeyError is null ? 0 : 1);
            if (keymapFile && keymapIssues > 0)
            {
                NotifyApp("keymap-diagnostics", Strings.T("Клавиши"), IssuesSeeSettings(keymapIssues));
            }

            if (hooksFile && hookDiagnostics.Count > 0)
            {
                NotifyApp("hooks-diagnostics", Strings.T("Хуки"), IssuesSeeSettings(hookDiagnostics.Count));
            }
        }

        public IReadOnlyList<string> ReloadConfig(bool keymapFile, bool hooksFile)
        {
            LoadConfig(keymapFile, hooksFile);
            return (keymapFile ? keymap.Diagnostics.Select(message => "keymap.conf " + message).Concat(hotkeyError is null ? Array.Empty<string>() : new[] { "keymap.conf " + hotkeyError }) : Array.Empty<string>())
                .Concat(hooksFile ? hookDiagnostics.Select(message => "hooks.conf " + message) : Array.Empty<string>())
                .ToList();
        }

        public bool IsHeld(Guid sessionId, PaneRole pane)
        {
            return commandHeld.Contains(new PaneKey(sessionId, pane))
                || (model.FindSession(sessionId) is Session session && RemoteStateOf(session, pane) == "held");
        }

        public bool ReleaseHeld(Guid sessionId, PaneRole pane)
        {
            if (model.FindSession(sessionId) is Session remote && RemoteStateOf(remote, pane) == "held")
            {
                CloseHeldPane(remote, pane);
                return true;
            }

            PaneKey key = new PaneKey(sessionId, pane);
            if (!commandHeld.Remove(key) || model.FindSession(sessionId) is not Session session)
            {
                return false;
            }

            EndPane(session, key);
            return true;
        }

        public string? LocalPath(Guid sessionId, string path)
        {
            if (path.Length > 6 && path.StartsWith("/mnt/", StringComparison.Ordinal) && char.IsAsciiLetter(path[5]) && path[6] == '/')
            {
                return char.ToUpperInvariant(path[5]) + ":" + path[6..].Replace('/', '\\');
            }

            if (path.StartsWith('/'))
            {
                string? profile = model.FindSession(sessionId)?.ProfileId;
                return profile is not null && profile.StartsWith(ShellProfiles.WslIdPrefix, StringComparison.Ordinal)
                    ? @"\\wsl.localhost\" + profile[ShellProfiles.WslIdPrefix.Length..] + path.Replace('/', '\\')
                    : null;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        public void Raise()
        {
            Deminiaturize();
            Activate();
        }

        /// <summary>The state the window was in before it was minimized, which agterm's deminiaturize brings back.</summary>
        private WindowState shownState = WindowState.Normal;

        /// <summary>agterm's deminiaturize: a minimized window comes back maximized or full screen if it was.</summary>
        internal void Deminiaturize()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = shownState;
            }
        }

        /// <summary>What every process agshell starts on its own (custom commands, hooks) gets: agshellctl on PATH and the pipe.</summary>
        private Dictionary<string, string?> BaseEnvironment()
        {
            return Library.BaseEnvironment();
        }

        public ControlKeymap KeymapList()
        {
            return new ControlKeymap
            {
                Path = keymapPath,
                Actions = keymap.ActionReadback(),
                Menu = MenuReadback(),
                Commands = keymap.Commands.Select(command => new ControlKeymapCommand
                {
                    Name = command.Name,
                    Shortcut = command.Shortcut.Length > 0 ? command.Shortcut : null,
                    ErrorHud = command.ErrorHud,
                    ErrorPosition = command.ErrorPosition ?? "center",
                    ErrorPane = command.ErrorPane,
                    Repeats = command.Bindings.Any(binding => binding.Repeat),
                }).ToList(),
                Diagnostics = keymap.Diagnostics.Select(ControlConfigDiagnostic.Parse).ToList(),
                GlobalHotkey = keymap.GlobalHotkey?.ToString(),
            };

        }

        public ControlHooks HooksList()
        {
            return new ControlHooks { Path = hooksPath, Diagnostics = hookDiagnostics.Select(ControlConfigDiagnostic.Parse).ToList(), Hooks = Library.Hooks.Entries() };
        }

        public string? RunCustomCommand(string name, Guid? sessionId)
        {
            return RunCustomCommand(name, sessionId, null, null);
        }

        public async Task<string?> RunCustomCommandAsync(string name, Guid? sessionId)
        {
            if (!HasCustomCommand(name)) return "no custom command named " + name;
            Session? session = sessionId is Guid id ? model.FindSession(id) : null;
            PaneRole pane = session is { HasSplit: true, SplitFocused: true } ? PaneRole.Split : PaneRole.Main;
            string? selection = null;
            if (session is not null)
            {
                selection = await AskPageAsync(requestId => new { type = "readSelection", requestId, sessionId = session.Id, pane = Panes.Name(pane) });
                if (selection is null && pane == PaneRole.Split)
                {
                    pane = PaneRole.Main;
                    selection = await AskPageAsync(requestId => new { type = "readSelection", requestId, sessionId = session.Id, pane = "left" });
                }
            }

            return RunCustomCommand(name, sessionId, Panes.Name(pane), selection);
        }

        internal async Task RunPageCustomAsync(string name, Guid? sessionId, string? pane, string? selection, bool keybind)
        {
            if (!keybind && (sessionId is null || model.Zoomed || model.Dashboard is not null || model.Pick is not null || model.GuiAsk is not null)) return;
            try
            {
                if (pane is null) await RunCustomCommandAsync(name, sessionId);
                else RunCustomCommand(name, sessionId, pane, selection);
            }
            catch (ControlException ex)
            {
                Log.Notice("CustomCommandRunner", "custom command context unavailable: " + ex.Message);
                if (keymap.Commands.FirstOrDefault(command => command.Name == name) is CustomCommand command)
                    ReportCustomCommand(command, sessionId is Guid id ? model.FindSession(id) : null, ex.Message, null);
            }
        }

        public bool HasCustomCommand(string name)
        {
            return keymap.Commands.Any(command => command.Name == name);
        }

        /// <summary>
        /// Runs a keymap command, as agterm does: detached, with no terminal, through the shell (cmd.exe here). Its tokens
        /// expand raw into the line and are also set as environment variables. A failure is announced; with --error-hud it
        /// also shows for ten seconds in a HUD over the session.
        /// </summary>
        private string? RunCustomCommand(string name, Guid? sessionId, string? pane, string? selection)
        {
            CustomCommand? command = keymap.Commands.FirstOrDefault(candidate => candidate.Name == name);
            if (command is null)
            {
                return "no custom command named " + name;
            }

            Session? session = sessionId is Guid id ? model.FindSession(id) : null;
            // agterm's empty-window rule: a command naming the session's context does nothing with no session to give it.
            if (session is null && CommandTokens.ReferencesSessionScopedContext(command.Shell))
            {
                Log.Notice("CustomCommandRunner", "custom command \"" + command.Name + "\" references session context but no session is active; ignored");
                return null;
            }

            Workspace? workspace = session is null ? null : model.FindWorkspaceOf(session.Id);
            PaneRole paneRole = pane is not null && Panes.TryParse(pane, out PaneRole named) ? named : session?.FocusedPane ?? PaneRole.Main;
            // agterm's cwd(for:): the pane the command was run from — the split's own directory, else the main pane's.
            string? paneCwd = session is null ? null : paneRole == PaneRole.Split ? session.SplitCwd ?? session.Cwd : session.Cwd;
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                ["AGSHELL_SESSION_ID"] = session?.Id.Wire() ?? string.Empty,
                ["AGSHELL_SESSION_NAME"] = session?.DisplayName ?? string.Empty,
                ["AGSHELL_SESSION_PWD"] = paneCwd ?? string.Empty,
                ["AGSHELL_SESSION_HOST"] = TerminalText.Sanitized(session?.RemoteHost ?? string.Empty),
                ["AGSHELL_WORKSPACE_ID"] = workspace?.Id.Wire() ?? string.Empty,
                ["AGSHELL_WORKSPACE_NAME"] = workspace?.Name ?? string.Empty,
                ["AGSHELL_WINDOW_ID"] = Entry.Id.Wire(),
                ["AGSHELL_WINDOW_NAME"] = Library.Label(Entry),
                ["AGSHELL_PANE"] = pane ?? Panes.Name(paneRole),
                ["AGSHELL_PANE_ID"] = session?.PaneIds.GetValueOrDefault(paneRole) ?? string.Empty,
                ["AGSHELL_SELECTION"] = selection ?? string.Empty,
                ["AGSHELL_PIPE"] = Library.PipeName,
            };
            string line = CommandTokens.Expand(command.Shell, values);
            string? cwd = paneCwd;
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /s /c \"" + line + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd is not null && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach ((string key, string? value) in BaseEnvironment())
            {
                start.Environment[key] = value;
            }

            foreach ((string key, string value) in values)
            {
                start.Environment[key] = value;
            }

            // agterm's run: a command that does not start answers the caller at once, and is announced as a failure too.
            CommandStderrFile? capture = command.ErrorHud ? CommandStderrFile.TryCreate() : null;
            if (command.ErrorHud && capture is null) Log.Error("CustomCommandRunner", "stderr capture unavailable for " + name);
            BackgroundCommand process;
            try
            {
                process = BackgroundCommand.Start(start, pipeInput: false, errorFile: capture?.Path);
                if (process.ErrorCaptureFailure is string reason) Log.Error("CustomCommandRunner", "stderr capture unavailable for " + name + ": " + reason);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException || ex is UnauthorizedAccessException)
            {
                capture?.Dispose();
                Log.Error("CustomCommandRunner", "custom command \"" + command.Name + "\" failed to spawn: " + ex.Message);
                ReportCustomCommand(command, session, ex.Message, null);
                return name + " did not start: " + ex.Message;
            }

            Library.RecordCommandRun(command.Name);
            _ = WatchCustomCommandAsync(command, session, process, capture);
            return null;
        }

        private async Task WatchCustomCommandAsync(CustomCommand command, Session? session, BackgroundCommand process, CommandStderrFile? capture)
        {
            string? failure;
            byte[] tail = Array.Empty<byte>();
            using (process)
            {
                try
                {
                    int status = await process.WaitForExitAsync().ConfigureAwait(false);
                    tail = capture?.Consume(HudSpec.ErrorTailLimit) ?? Array.Empty<byte>();
                    failure = status == 0 ? null : "exit " + status;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
                {
                    capture?.Dispose();
                    failure = ex.Message;
                }
            }

            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => ReportCustomCommand(command, session, failure, HudSpec.TailDetail(tail)));
        }

        /// <summary>A failed command is announced, and with --error-hud also shown for ten seconds in a HUD over the session.</summary>
        private void ReportCustomCommand(CustomCommand command, Session? session, string? failure, string? detail)
        {
            if (failure is null)
            {
                return;
            }

            // agterm's notifyCommandFailure: the name and the reason; stderr's line goes to the HUD only.
            NotifyApp("command-failure:" + command.Name, Strings.T("Команда не сработала"), command.Name + " (" + failure + ")");
            if (command.ErrorHud && session is not null)
            {
                if (model.FindSession(session.Id) is null)
                {
                    Log.Notice("CustomCommandRunner", "custom command \"" + command.Name + "\" failed (" + failure + "); no failure panel: no such session");
                    return;
                }

                // The session's one HUD, over the named pane: the left one always, the right one while it is shown, else the
                // whole session, as agterm falls back.
                PaneRole? anchor = command.ErrorPane is string paneName && Panes.TryParse(paneName, out PaneRole role)
                    ? role == PaneRole.Main ? PaneRole.Main : role == PaneRole.Split && session.RendersPane(PaneRole.Split) ? PaneRole.Split : null
                    : null;
                if (anchor is null && command.ErrorPane is not null && Panes.TryParse(command.ErrorPane, out PaneRole hidden) && hidden == PaneRole.Split)
                {
                    Log.Notice("ControlHud", "failure panel for \"" + HudSpec.FailureMessage(command.Name, failure) + "\" falling back to session-wide placement: pane not visible");
                }

                // A program or page in the slot keeps it; the failure is still in the notification.
                if (session.Overlay is { IsHud: false })
                {
                    Log.Notice("CustomCommandRunner", "custom command \"" + command.Name + "\" failed (" + failure + "); no failure panel: overlay already open");
                    return;
                }

                string id = Ids.New();
                model.OpenOverlay(session.Id, new SessionOverlay
                {
                    Id = id,
                    Hud = new HudSpec { Message = HudSpec.FailureMessage(command.Name, failure), Detail = detail, Position = command.ErrorPosition ?? "center", HideAfter = 10 },
                    HudPane = anchor,
                });
                ScheduleHudHide(session.Id, id, 10);
            }
        }

        /// <summary>
        /// Opens config in the editor named by VISUAL or EDITOR in an overlay, otherwise in Notepad.
        /// </summary>
        /// <summary>The settings window's Edit buttons: the file opens over this window's selected session.</summary>
        internal void EditConfigFile(string which)
        {
            EditConfig(which);
        }

        /// <summary>The settings window's Reload buttons: one file read again and its problems announced.</summary>
        internal void ReloadConfigAndReport(string? which)
        {
            OnConfigFilesChanged(which != "hooks", which != "keymap");
        }

        private void EditConfig(string which)
        {
            string path = which switch
            {
                "hooks" => hooksPath,
                "denylist" => denylistPath,
                "ghostty" => Library.GhosttyConfigPath,
                _ => keymapPath,
            };
            if (which == "denylist" && !File.Exists(path))
            {
                File.WriteAllText(path, CommandRestore.DenylistStarter + Environment.NewLine);
            }

            string? editor = ConfigPaths.Editor(Environment.GetEnvironmentVariable("VISUAL"), Environment.GetEnvironmentVariable("EDITOR"));
            _ = OpenConfigAsync(editor, path, which == "ghostty");
        }

        /// <summary>
        /// The editor in an overlay over the selected session, as agterm's editKeymap: nothing without a session or when a
        /// program or page holds the slot, with a beep when that one is hidden, the one refusal with nothing on screen to
        /// explain it. Notepad when no editor is set.
        /// </summary>
        private async Task OpenConfigAsync(string? editor, string path, bool ghostty)
        {
            if (string.IsNullOrWhiteSpace(editor))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
                return;
            }

            if (model.SelectedSessionId is not Guid selected)
            {
                return;
            }

            ControlResponse opened = await dispatcher.HandleAsync(new ControlRequest
            {
                Cmd = "session.overlay.open",
                Target = selected.ToString(),
                Args = new ControlArgs { Command = editor + " \"" + path + "\"", SizePercent = 95 },
            });
            if (opened.Ok && ghostty && model.FindSession(selected)?.Overlay is { } overlay)
            {
                ghosttyEditorReload = new ConfigEditorReload(selected, overlay.Id, path);
            }

            if (!opened.Ok && model.FindSession(selected)?.Overlay is { Hidden: true })
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        private void EnsureInitialSession()
        {
            if (model.Workspaces.Count > 0)
            {
                return;
            }

            Workspace workspace = model.AddWorkspace();
            model.AddSession(workspace.Id, DefaultProfile().Id, null);
        }

        private ShellProfile DefaultProfile()
        {
            return profiles.First(profile => profile.Kind == ShellKind.PowerShell || profile.Kind == ShellKind.Cmd);
        }

        private ShellProfile ProfileOrDefault(string profileId)
        {
            return profiles.FirstOrDefault(profile => profile.Id == profileId) ?? DefaultProfile();
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!e.Uri.StartsWith(pageOrigin + "/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            AskToOpenExternally(e.Uri);
        }

        private PageSpec? FindPage(string overlayId)
        {
            return FindOverlay(overlayId)?.Page;
        }

        private SessionOverlay? FindOverlay(string overlayId)
        {
            return model.AllSessions.SelectMany(session => session.Overlays.Values).FirstOrDefault(overlay => overlay.Id == overlayId);
        }

        private void AskToOpenExternally(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "mailto"))
            {
                return;
            }

            if (MessageBox.Show(this, Strings.T("Открыть в браузере?") + "\n" + url, "AgShell", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }

        /// <summary>
        /// A command from a local page's bridge. Without a target it acts on the page's own session, and overlay commands on the
        /// page's own overlay, as in agterm. Web pages get no bridge.
        /// </summary>
        private async void OnPageRequest(string overlayId, int requestId, string cmd, string? requestedTarget, string? argsJson)
        {
            ControlResponse response;
            (Session Session, PaneRole Slot)? place = model.AllSessions
                .SelectMany(session => session.Overlays.Where(pair => pair.Value.Id == overlayId && pair.Value.Page is { IsFile: true }).Select(pair => (session, pair.Key)))
                .Cast<(Session, PaneRole)?>()
                .FirstOrDefault();
            if (place is not (Session session, PaneRole slot))
            {
                response = ControlResponse.Failure("page closed");
            }
            else if (replicaPages.ContainsKey(overlayId))
            {
                // agterm: its requests run on the machine it was opened on, through its channel; the answer comes back as sent.
                if (pageHost?.ChannelOf(overlayId) is not RemotePageChannel channel)
                {
                    response = ControlResponse.Failure("page channel closed");
                }
                else
                {
                    try
                    {
                        nativePages?.ReplyRaw(overlayId, requestId, await channel.BridgeAsync(RelayedRequest(cmd, requestedTarget, argsJson)));
                        return;
                    }
                    catch (PageChannelException ex)
                    {
                        response = ControlResponse.Failure(ex.Message);
                    }
                }
            }
            else
            {
                try
                {
                    ControlArgs args = argsJson is null ? new ControlArgs() : ControlJson.DeserializeArgs(argsJson) ?? new ControlArgs();
                    (ControlRequest? request, string? refusal) = PageBridge.Build(new ControlRequest { Cmd = cmd, Target = requestedTarget, Args = argsJson is null ? null : args }, session.Id, slot, entry.Id.Wire());
                    response = request is null ? ControlResponse.Failure(refusal!) : await Library.DispatchAsync(request);
                }
                catch (JsonException ex)
                {
                    response = ControlResponse.Failure("invalid request: " + ex.Message);
                }
            }

            nativePages?.Reply(overlayId, requestId, response);
        }

        /// <summary>A page's request as the control request it names, its arguments kept as the page wrote them.</summary>
        private static string RelayedRequest(string cmd, string? target, string? argsJson)
        {
            using MemoryStream buffer = new MemoryStream();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("cmd", cmd);
                if (target is not null)
                {
                    writer.WriteString("target", target);
                }

                if (argsJson is not null)
                {
                    writer.WritePropertyName("args");
                    writer.WriteRawValue(argsJson);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// agterm's browser clear: refused while any window holds a --persistent page, an undoably closed one included, whose
        /// login lives in memory and would be written back.
        /// </summary>
        public async Task<string?> ClearBrowserDataAsync()
        {
            if (Library.ClearingBrowserData)
            {
                return "browser storage is being cleared";
            }

            int open = Library.OpenWindows.Sum(window => window.PersistentPageCount);
            if (open > 0)
            {
                return open + " persistent " + (open == 1 ? "page" : "pages") + " still open";
            }

            Library.ClearingBrowserData = true;
            try
            {
                if (nativePages is not null)
                {
                    await nativePages.ClearPersistentAsync();
                }
            }
            finally
            {
                Library.ClearingBrowserData = false;
            }

            return null;
        }

        public bool BrowserClearing => Library.ClearingBrowserData;

        private int PersistentPageCount => model.AllSessions.Concat(model.PendingCloses.SelectMany(close => close.Sessions))
            .SelectMany(session => session.Overlays.Values).Count(overlay => overlay.Page is { Persistent: true });

        public string? PageCommand(Guid sessionId, PaneRole slot, string action, string? url)
        {
            if (model.FindSession(sessionId)?.Overlays.GetValueOrDefault(slot) is not { Page: PageSpec spec } overlay || nativePages is null)
            {
                return null;
            }

            if (action is "browser" or "finder")
            {
                // agterm: a file page shown for another machine has no file here to reveal or open.
                if (spec.IsFile && replicaPages.ContainsKey(overlay.Id))
                {
                    return "the page's file is on another machine";
                }

                if (!nativePages.Realized(overlay.Id))
                {
                    return NativePages.NotRealized;
                }

                if (action == "finder")
                {
                    RevealPage(overlay.Id);
                    return null;
                }

                return BrowserUrl(overlay.Id, spec) is Uri address && DefaultBrowser.Open(address) ? null : "no default web browser to open the page in";
            }

            // The page it opened with, unless --current asks for the one shown now.
            if (action == "reload-original")
            {
                return nativePages.Command(overlay.Id, "original", spec.IsFile ? pageHost?.PageUrl(overlay.Id, spec) : spec.Source);
            }

            return nativePages.Command(overlay.Id, action, url);
        }

        /// <summary>
        /// What agterm hands the browser or copies as the link: a local page's own file, never one it moved to, and a web
        /// page's address shown now.
        /// </summary>
        private Uri? BrowserUrl(string overlayId, PageSpec spec)
        {
            string? address = spec.IsFile ? spec.Source : nativePages?.CurrentUrl(overlayId) ?? spec.Source;
            return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ? uri : null;
        }

        /// <summary>agterm's finder step: the file a local page shows now.</summary>
        private void RevealPage(string overlayId)
        {
            string? shown = nativePages?.CurrentUrl(overlayId);
            string? file = (shown is null ? null : pageHost?.LocalPath(shown)) ?? (FindPage(overlayId) is { IsFile: true } spec ? spec.Source : null);
            if (file is not null && File.Exists(file))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith(pageOrigin + "/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    pageReady = true;
                    flushTimer.Start();
                    PostState();
                    Library.BroadcastShortcuts();
                    PostWindowActive();
                    if (welcomeDue && settings.WelcomeShown != true)
                    {
                        // Marked first, so a cancelled or failed install does not bring it back.
                        welcomeDue = false;
                        settings.WelcomeShown = true;
                        Library.SettingsStore.Save(settings);
                        Post(new { type = "welcome" });
                    }

                    break;
                case "toolbarKeyHandled":
                    if (message.GetProperty("token").GetInt64() == toolbarPopoverToken) CloseToolbarPopover(true);
                    break;
                case "toolbar":
                    ApplyToolbar(message);
                    break;
                case "shortcutState":
                    Library.ReceiveShortcuts(message, IsActive);
                    break;
                case "attach":
                    OnAttach(GetPane(message), message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    break;
                case "input":
                    PaneKey inputPane = GetPane(message);
                    string data = message.GetProperty("data").GetString() ?? string.Empty;
                    // agterm counts a key press, not bytes: a paste, a drop or the terminal's own reports carry no stroke.
                    StatusKeystroke? stroke = KeystrokeOf(message);
                    // An overlay held open after its program exited closes on the next key, as agterm's --wait does.
                    if (Panes.IsOverlay(inputPane.Pane) && model.FindSession(inputPane.SessionId)?.Overlays.GetValueOrDefault(inputPane.Pane)?.ExitCode is not null)
                    {
                        if (stroke is not null)
                        {
                            model.CloseOverlay(inputPane.SessionId, slot: inputPane.Pane);
                        }

                        break;
                    }

                    if (endedThere.Contains(inputPane))
                    {
                        if (stroke is not null && model.FindSession(inputPane.SessionId) is Session endedRow)
                        {
                            CloseEndedPane(endedRow, inputPane);
                        }

                        break;
                    }

                    // agterm: a key on a remote pane waiting for its connection retries at once.
                    // Only the row's panes of the other machine wait: a split opened here or an overlay types on.
                    if (model.FindSession(inputPane.SessionId) is Session waitingRow && RemoteStateOf(waitingRow, inputPane.Pane) is "disconnected" or "held")
                    {
                        if (stroke is not null)
                        {
                            RetryRemoteNow(inputPane.SessionId, inputPane.Pane);
                        }

                        break;
                    }

                    // agterm's cover: a key on a covered pane, one another machine leads, nobody leads or that is joining again,
                    // takes the lead instead of typing; the terminal's own reports from a covered view are dropped, the leading
                    // side answers the program. A pane already on its way is not taken again.
                    if (Covered(inputPane))
                    {
                        if (stroke is not null)
                        {
                            TakeOver(inputPane);
                        }

                        break;
                    }

                    if (commandHeld.Contains(inputPane))
                    {
                        if (stroke is not null)
                        {
                            ReleaseHeld(inputPane.SessionId, inputPane.Pane);
                        }

                        break;
                    }

                    if (stroke is not null)
                    {
                        NoteActivity();
                    }

                    host.Write(inputPane, data);
                    if (stroke is StatusKeystroke keystroke)
                    {
                        model.ClearStatusByInput(inputPane.SessionId, keystroke, inputPane.Pane);
                    }

                    break;
                case "osc52":
                    // From a program, or agshellctl clipboard set through the pane.
                    clipboard.Osc52(GetPane(message), GetOptionalString(message, "data") ?? string.Empty);
                    break;
                case "osc5522":
                    clipboard.Osc5522(GetPane(message), GetOptionalString(message, "data") ?? string.Empty, message.TryGetProperty("bel", out JsonElement bel) && bel.GetBoolean());
                    break;
                case "kittyPaste":
                    clipboard.PasteEvent(GetPane(message), message.TryGetProperty("selection", out JsonElement fromSelection) && fromSelection.GetBoolean());
                    break;
                case "selectionCopied":
                    // Ghostty's copy-on-select: a selection made in a terminal goes to the app's own selection clipboard.
                    TerminalClipboardHost.Selection = GetOptionalString(message, "text");
                    break;
                case "notify":
                    OnTerminalNotification(GetGuid(message, "sessionId"), GetOptionalString(message, "pane") ?? "left", message.TryGetProperty("focused", out JsonElement focused) && focused.GetBoolean(), GetOptionalString(message, "title"), GetOptionalString(message, "body") ?? string.Empty);
                    break;
                case "resize":
                    host.Resize(GetPane(message), message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    break;
                case "cwd":
                    PaneKey cwdPane = GetPane(message);
                    model.UpdateCwd(cwdPane.SessionId, message.GetProperty("path").GetString() ?? string.Empty, cwdPane.Pane);
                    break;
                case "title":
                    PaneKey titlePane = GetPane(message);
                    // agterm: ghostty.conf's title stands in place of every title a program sets.
                    model.UpdateTitle(titlePane.SessionId, Library.Ghostty.Title ?? message.GetProperty("title").GetString() ?? string.Empty, titlePane.Pane);
                    break;
                case "reply":
                    int requestId = message.GetProperty("requestId").GetInt32();
                    if (pendingPageRequests.Remove(requestId, out TaskCompletionSource<string?>? pending))
                    {
                        pending.TrySetResult(GetOptionalBool(message, "missing") == true ? null : message.GetProperty("text").GetString() ?? string.Empty);
                    }

                    break;
                case "command":
                    NoteActivity();
                    OnCommand(message);
                    break;
                case "menu":
                    ApplyMenu(message);
                    break;
                case "menuClipboard":
                    Clipboard.SetText(message.GetProperty("text").GetString() ?? "");
                    break;
                case "autoFollowSuppressed":
                    autoFollowSuppressed = message.GetProperty("value").GetBoolean();
                    break;
                case "paneFocused":
                    // agterm's clearUnseenOnRefocus: the main or right pane taking the keyboard; a scratch or overlay has no say.
                    PaneKey focusedPane = GetPane(message);
                    if (focusedPane.Pane is PaneRole.Main or PaneRole.Split)
                    {
                        model.ClearUnseen(focusedPane.SessionId);
                    }

                    break;
                case "terminalFocus":
                    TerminalInputSource.Shared.Report(new WindowInteropHelper(this).Handle, message.GetProperty("value").GetBoolean());
                    break;
                case "switcherHold":
                    switcherEscape.Hold(message.GetProperty("value").GetBoolean());
                    break;
                case "dropFolders":
                    DropFolders(e.AdditionalObjects?.OfType<CoreWebView2File>().Select(file => file.Path) ?? Enumerable.Empty<string>(), GetOptionalGuid(message, "workspaceId"), GetOptionalGuid(message, "springOpen"));
                    break;
                case "dropText":
                    DropText(GetPane(message), GetOptionalString(message, "text") ?? string.Empty,
                        message.TryGetProperty("uris", out JsonElement uris) ? uris.EnumerateArray().Select(uri => uri.GetString() ?? string.Empty).ToList() : new List<string>());
                    break;
                case "dropPaths":
                    DropPaths(GetPane(message), e.AdditionalObjects?.OfType<CoreWebView2File>().Select(file => file.Path) ?? Enumerable.Empty<string>());
                    break;
                case "pageOpenExternal":
                    string external = GetOptionalString(message, "target") ?? string.Empty;
                    if (Uri.TryCreate(external, UriKind.Absolute, out Uri? externalUri) && (externalUri.Scheme == Uri.UriSchemeHttp || externalUri.Scheme == Uri.UriSchemeHttps || externalUri.IsFile))
                    {
                        Process.Start(new ProcessStartInfo(externalUri.IsFile ? externalUri.LocalPath : external) { UseShellExecute = true });
                    }

                    break;
                case "nativePage":
                    nativePages?.Place(
                        GetOptionalString(message, "overlayId") ?? string.Empty,
                        new Rect(message.GetProperty("x").GetDouble(), message.GetProperty("y").GetDouble(), message.GetProperty("width").GetDouble(), message.GetProperty("height").GetDouble()),
                        message.GetProperty("visible").GetBoolean(),
                        message.GetProperty("raised").GetBoolean(),
                        message.GetProperty("focus").GetBoolean(),
                        message.TryGetProperty("radius", out JsonElement radius) ? radius.GetDouble() : 0,
                        message.TryGetProperty("roundTop", out JsonElement roundTop) && roundTop.GetBoolean());
                    hudFloat?.Refresh();
                    break;
                case "hudFloat":
                    hudFloat?.Set(message.Clone());
                    break;
                case "pageNav":
                    nativePages?.Command(GetOptionalString(message, "overlayId") ?? string.Empty, GetOptionalString(message, "action") ?? string.Empty, null);
                    break;
                case "pageZoom":
                    // As agterm's setting: every page overlay, in every window, keeps this zoom across restarts.
                    double factor = message.GetProperty("factor").GetDouble();
                    settings.HtmlOverlayZoom = factor == 1 ? null : factor;
                    SaveSettings();
                    break;
                case "pageOpenCurrent":
                case "pageCopyLink":
                    string pageId = GetOptionalString(message, "overlayId") ?? string.Empty;
                    if (FindPage(pageId) is PageSpec linked && BrowserUrl(pageId, linked) is Uri link)
                    {
                        if (message.GetProperty("type").GetString() == "pageCopyLink")
                        {
                            Clipboard.SetText(link.AbsoluteUri);
                        }
                        else
                        {
                            DefaultBrowser.Open(link);
                        }
                    }

                    break;
                case "pageReveal":
                    if (GetOptionalString(message, "overlayId") is string revealId)
                    {
                        RevealPage(revealId);
                    }

                    break;
            }
        }

        /// <summary>Panes waiting for the pacer before they start, with the size they asked for.</summary>
        private readonly Dictionary<PaneKey, (int Columns, int Rows)> paced = new Dictionary<PaneKey, (int, int)>();

        /// <summary>
        /// agterm's LaunchSpawnPlan: the app's launch arms its SpawnPacer with every pane of the restored sessions, in
        /// sidebar order and in any restore mode: the panes of the session on screen go at once, the rest one every 120 ms,
        /// and a session the user turns to goes next.
        /// </summary>
        private void QueueReplays()
        {
            if (!launchRestore)
            {
                return;
            }

            List<PaneKey> order = new List<PaneKey>();
            foreach (Session session in model.AllSessions.Where(session => session.WasRestored))
            {
                // agterm's isSplit: a hidden split starts nothing at launch, and a key nobody asks for would stall the queue.
                foreach (PaneRole pane in session.SplitShown ? new[] { PaneRole.Main, PaneRole.Split } : new[] { PaneRole.Main })
                {
                    order.Add(new PaneKey(session.Id, pane));
                }
            }

            Guid? selected = model.SelectedSessionId;
            PaneKey[] burst = selected is Guid id ? new[] { new PaneKey(id, PaneRole.Main), new PaneKey(id, PaneRole.Split) } : Array.Empty<PaneKey>();
            Library.Pacer.Granted += OnPacerGranted;
            if (Library.Pacer.Enqueue(order, burst))
            {
                Log.Debug("agshellApp", "launch spawn queue armed with " + order.Count + " panes, " + burst.Length + " in the burst");
            }

            model.SelectionChanged += () =>
            {
                if (model.SelectedSessionId is Guid shown)
                {
                    Library.Pacer.Expedite(new PaneKey(shown, PaneRole.Main));
                    Library.Pacer.Expedite(new PaneKey(shown, PaneRole.Split));
                }
            };
        }

        private void OnPacerGranted(PaneKey key)
        {
            if (paced.Remove(key, out (int Columns, int Rows) size))
            {
                OnAttach(key, size.Columns, size.Rows);
            }
        }

        private void OnAttach(PaneKey key, int columns, int rows)
        {
            // A pane holding its ended command's output (--wait) waits for its key, not for a new shell.
            if (host.IsRunning(key) || commandHeld.Contains(key))
            {
                // A pane already running has nothing left to start, so it must not hold the launch queue at its head.
                Library.Pacer.Discard(key);
                host.Resize(key, columns, rows);
                return;
            }

            Session? session = model.FindSession(key.SessionId);
            if (session is null)
            {
                return;
            }

            if (key.Pane is PaneRole.Main or PaneRole.Split && !AppModel.IsRemotePane(session, key.Pane)
                && Library.LaunchRestoreMode == CommandRestore.Live && liveDaemons is null)
            {
                model.SetLiveFailed(session.Id, key.Pane, true);
            }

            if (session.LiveFailed(key.Pane))
            {
                Library.Pacer.Discard(key);
                PostLiveFailure(key);
                return;
            }

            if (Panes.IsOverlay(key.Pane))
            {
                StartOverlay(session, key, columns, rows);
                return;
            }

            // A restored pane that starts a program waits its turn, as agterm's SpawnPacer has it, another machine's included;
            // any other pane leaves the queue, so it does not wait on a request that never comes.
            if (key.Pane is PaneRole.Main or PaneRole.Split)
            {
                if (!ShouldPace(session, key.Pane))
                {
                    Library.Pacer.Discard(key);
                }
                else if (!Library.Pacer.Request(key))
                {
                    paced[key] = (columns, rows);
                    return;
                }
            }

            // The main pane, and the split it arrived with, are the other machine's; a split opened here later is a local shell.
            if (AppModel.IsRemotePane(session, key.Pane))
            {
                _ = StartRemotePaneAsync(session, key, columns, rows);
                return;
            }

            // The split opens where it was last, the scratch where the user works; a failed main shell waits for a restart.
            string? cwd;
            switch (key.Pane)
            {
                case PaneRole.Split when session.HasSplit:
                    cwd = session.SplitCwd ?? session.Cwd;
                    break;
                case PaneRole.Scratch when session.ScratchShown:
                    // agterm's effectiveCwd: the scratch starts in the main pane's directory.
                    cwd = session.Cwd;
                    break;
                case PaneRole.Main:
                    cwd = session.Cwd;
                    break;
                default:
                    return;
            }

            // agterm's RestartReplay: a program run again by a restart starts where it was running.
            if (restartDirectories.Remove(key, out string? replayDirectory))
            {
                cwd = replayDirectory;
            }

            cwd = LaunchDirectory(session, cwd);
            ShellProfile profile = ProfileOrDefault(session.ProfileId);
            try
            {
                // agterm's scratch command runs once, in place of the shell; the scratch's next start is a shell again.
                if (key.Pane == PaneRole.Scratch && model.TakeScratchCommand(session.Id) is string scratchCommand)
                {
                    host.Start(key, ShellProfiles.CreateCommandOptions(profile, scratchCommand, cwd, columns, rows, SessionEnvironment(key)));
                    PostState();
                    return;
                }

                // agterm's restorePlan, for a pane whose shell starts here: the session's command in place of the shell, or
                // a line typed into it.
                string? paneIdentity = session.PaneIds.GetValueOrDefault(key.Pane);
                bool failedRestart = paneIdentity is not null && failedRestartLines.ContainsKey(paneIdentity);
                RestorePlan? plan = key.Pane is PaneRole.Main or PaneRole.Split && liveDaemons is null && !restartLines.ContainsKey(key) && !failedRestart ? LaunchSeed(session, key.Pane, take: true) : null;
                if (plan?.Command is string planned)
                {
                    host.Start(key, ShellProfiles.CreateCommandOptions(profile, planned, cwd, columns, rows, SessionEnvironment(key)));
                    commandRuns.Add(key);
                    PostState();
                    return;
                }

                // A session restart's line, or the program it runs again, goes into the new shell instead of a restore command.
                // Under live, as agterm's replay script, a line is the first command of the daemon's shell when it is created;
                // otherwise it is typed in, as ghostty's initial input.
                bool live = liveDaemons is not null && key.Pane is PaneRole.Main or PaneRole.Split && !reattaching.Contains(key);
                string? line = paneIdentity is not null && failedRestartLines.Remove(paneIdentity, out string? failedLine) ? failedLine
                    : restartLines.Remove(key, out string? restartLine) ? restartLine : live ? LiveReplay(session, key.Pane) : plan?.Input;
                string? replay = live && line is not null ? CommandRestore.ForShell(profile.Id, line) : null;
                StartPane(key, ShellProfiles.CreateStartOptions(profile, cwd, columns, rows, SessionEnvironment(key), replay));
                PostState();
                if (!live && line is not null)
                {
                    _ = TypeAtPromptAsync(key, profile.Id, line);
                }
            }
            catch (Exception ex)
            {
                PostOutput(key, Strings.T("Не удалось запустить {0}: {1}", profile.Name, ex.Message) + "\r\n");
            }
        }

        private void StartOverlay(Session session, PaneKey key, int columns, int rows)
        {
            SessionOverlay? overlay = session.Overlays.GetValueOrDefault(key.Pane);
            if (overlay is { CommandLine: string commandLine, ExitCode: null })
            {
                host.Start(key, new PtyStartOptions { CommandLine = commandLine, Columns = columns, Rows = rows, Environment = SessionEnvironment(key) });
                return;
            }

            if (overlay?.Command is null || overlay.ExitCode is not null)
            {
                return;
            }

            // agterm's OverlayCapture.cwd: without --cwd every overlay opens in the main pane's directory, whatever has focus.
            string? cwd = overlay.Cwd ?? LaunchDirectory(session, session.Cwd);

            ShellProfile profile = ProfileOrDefault(session.ProfileId);
            try
            {
                host.Start(key, ShellProfiles.CreateCommandOptions(profile, overlay.Command, cwd, columns, rows, SessionEnvironment(key)));
            }
            catch (Exception ex)
            {
                PostOutput(key, Strings.T("Не удалось запустить {0}: {1}", overlay.Command, ex.Message) + "\r\n");
                model.OverlayExited(session.Id, overlay.Id, -1, key.Pane);
            }
        }

        /// <summary>One timer per panel: a new one replaces it, and none (null) leaves the panel up.</summary>
        public void ScheduleHudHide(Guid sessionId, string overlayId, double? seconds)
        {
            if (hudTimers.Remove(overlayId, out DispatcherTimer? earlier))
            {
                earlier.Stop();
            }

            if (seconds is not double hide)
            {
                return;
            }

            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(hide) };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                hudTimers.Remove(overlayId);
                model.CloseOverlayById(sessionId, overlayId);
            };
            hudTimers[overlayId] = timer;
            timer.Start();
        }

        private readonly Dictionary<string, DispatcherTimer> hudTimers = new Dictionary<string, DispatcherTimer>();

        private void OnCommand(JsonElement message)
        {
            switch (message.GetProperty("command").GetString())
            {
                case "newSession":
                    NewSession(GetOptionalGuid(message, "workspaceId"), GetOptionalString(message, "profileId"));
                    break;
                case "newWorkspace":
                    model.AddWorkspace();
                    break;
                case "duplicateSession":
                    if ((GetOptionalGuid(message, "sessionId") ?? model.SelectedSessionId) is Guid source)
                    {
                        model.DuplicateSession(source, DefaultProfile().Id);
                    }

                    break;
                case "revealSession":
                    RevealSession(GetGuid(message, "sessionId"));
                    break;
                case "selectSession":
                    model.SelectSession(GetGuid(message, "sessionId"), message.TryGetProperty("reveal", out JsonElement reveal) && reveal.GetBoolean());
                    break;
                case "selectRelative":
                    model.SelectRelative(message.GetProperty("offset").GetInt32(), reveal: true);
                    break;
                case "selectRelativeWorkspace":
                    model.SelectRelativeWorkspace(message.GetProperty("offset").GetInt32());
                    break;
                case "renameSession":
                    model.RenameSession(GetGuid(message, "sessionId"), GetOptionalString(message, "name"));
                    break;
                case "renameWorkspace":
                    model.RenameWorkspace(GetGuid(message, "workspaceId"), GetOptionalString(message, "name") ?? string.Empty);
                    break;
                case "moveSession":
                    model.MoveSession(GetGuid(message, "sessionId"), GetGuid(message, "workspaceId"), message.GetProperty("index").GetInt32());
                    break;
                case "moveSessions":
                    model.MoveSessions(GetGuids(message, "sessionIds"), GetGuid(message, "workspaceId"));
                    break;
                case "dropSessions":
                    if (model.DropSessions(GetGuids(message, "sessionIds"), GetGuid(message, "workspaceId"), GetOptionalGuid(message, "sessionId"), GetOptionalBool(message, "after") == true) > 0
                        && GetOptionalGuid(message, "springOpen") is Guid sprung)
                    {
                        model.KeepSpringOpen(sprung);
                    }

                    break;
                case "dropWorkspace":
                    model.DropWorkspace(GetGuid(message, "workspaceId"), message.GetProperty("slot").GetInt32());
                    break;
                case "dropPlan":
                    PostDropPlan(message.GetProperty("token").GetInt32(), message.TryGetProperty("sessionIds", out JsonElement planned) ? planned.EnumerateArray().Select(item => item.GetGuid()).ToList() : null, GetOptionalGuid(message, "workspaceId"));
                    break;
                case "closeSession":
                    Guid? sessionId = GetOptionalGuid(message, "sessionId") ?? model.SelectedSessionId;
                    if (sessionId is Guid id)
                    {
                        CloseSessionsFromWindow(new[] { id });
                    }

                    break;
                case "closeSessions":
                    CloseSessionsFromWindow(GetGuids(message, "sessionIds"));
                    break;
                case "setFlag":
                    model.SetFlag(GetGuids(message, "sessionIds"), GetOptionalBool(message, "flagged"));
                    break;
                case "setSidebarMode":
                    model.SetSidebarMode(GetOptionalString(message, "mode"));
                    break;
                case "toggleFocusWorkspace":
                    model.ToggleFocusWorkspace(GetGuid(message, "workspaceId"));
                    break;
                case "setFocusMember":
                    model.SetFocusMember(GetGuid(message, "workspaceId"), GetOptionalBool(message, "member"));
                    break;
                case "setFocusEnabled":
                    model.SetFocusEnabled(GetOptionalBool(message, "enabled"));
                    break;
                case "clearFocus":
                    model.ClearFocus();
                    break;
                case "pickAnswer":
                    model.AnswerPick(GetOptionalString(message, "id") ?? string.Empty, GetOptionalString(message, "itemId"), GetOptionalString(message, "query"));
                    break;
                case "clipboardPrompt":
                    ClipboardPrompts.Shared.Answer(GetOptionalString(message, "id") ?? string.Empty, message.GetProperty("allow").GetBoolean(), message.GetProperty("remember").GetBoolean());
                    break;
                case "askAnswer":
                    model.AnswerAsk(
                        GetOptionalString(message, "id") ?? string.Empty,
                        GetOptionalString(message, "buttonId"),
                        GetOptionalString(message, "text"),
                        message.TryGetProperty("selected", out JsonElement selected) && selected.ValueKind == JsonValueKind.Array
                            ? selected.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList()
                            : null);
                    break;
                case "newWindow":
                    Library.RunWindowCommand("window.new");
                    break;
                case "selectWindow":
                    Library.RunWindowCommand("window.select", GetOptionalString(message, "windowId"));
                    break;
                case "relativeWindow":
                    Library.RunWindowCommand("window.go", null, new ControlArgs { To = message.GetProperty("offset").GetInt32() > 0 ? "next" : "prev" });
                    break;
                case "renameWindow":
                    Library.RunWindowCommand("window.rename", entry.Id.Wire(), new ControlArgs { Name = GetOptionalString(message, "name") });
                    break;
                case "deleteWindow":
                    DeleteWindowFromWindow();
                    break;
                case "closeWindow":
                    Dispatcher.BeginInvoke(new Action(Close));
                    break;
                case "requestRemotes":
                    _ = AttachRemoteAsync();
                    break;
                case "retryRemote":
                    if (Panes.TryParse(GetOptionalString(message, "pane"), out PaneRole keyed))
                    {
                        RetryRemoteNow(GetGuid(message, "sessionId"), keyed);
                    }

                    break;
                case "clearFlags":
                    model.ClearFlags();
                    break;
                case "expandWorkspaces":
                    model.ExpandAllWorkspaces();
                    break;
                case "collapseWorkspaces":
                    model.CollapseOtherWorkspaces();
                    break;
                case "toggleFullscreen":
                    ToggleFullscreen();
                    break;
                case "installCommandLineTool":
                    Dispatcher.BeginInvoke(InstallCommandLineTool);
                    break;
                case "log":
                    // The page's own log lines, agterm's debug and info only: what goes wrong is reported by the host.
                    string category = GetOptionalString(message, "category") ?? "page";
                    string text = GetOptionalString(message, "message") ?? string.Empty;
                    if (GetOptionalString(message, "level") == "info")
                    {
                        Log.Info(category, text);
                    }
                    else
                    {
                        Log.Debug(category, text);
                    }

                    break;
                case "registerEventLog":
                    Dispatcher.BeginInvoke(RegisterEventLog);
                    break;
                case "showAbout":
                    AboutWindow.Show(this, Version);
                    break;
                case "openDocumentation":
                    Process.Start(new ProcessStartInfo(AppVersion.DocumentationUrl(Strings.Language)) { UseShellExecute = true });
                    break;
                case "installAgent":
                    _ = RunInstallerAsync(GetOptionalString(message, "what") == "skill" ? "skill" : "hooks");
                    break;
                case "openDirectory":
                    OpenDirectory(GetOptionalGuid(message, "workspaceId"));
                    break;
                case "undoClose":
                    UndoClose(GetOptionalGuid(message, "closeId"));
                    break;
                case "reopenRecent":
                    Library.ReopenRecent(GetOptionalGuid(message, "itemId"), this);
                    break;
                case "clearRecent":
                    model.ClearRecent();
                    break;
                case "restartSession":
                    RestartSession(GetGuid(message, "sessionId"), PaneRole.Main);
                    break;
                case "closeWorkspace":
                    DeleteWorkspaceFromWindow(GetGuid(message, "workspaceId"));
                    break;
                case "setSplit":
                    model.SetSplit(GetGuid(message, "sessionId"), null, Panes.TryParseAxis(GetOptionalString(message, "axis"), out SplitAxis axis) ? axis : null);
                    break;
                case "closeSplit":
                    Guid splitSession = GetGuid(message, "sessionId");
                    host.Stop(new PaneKey(splitSession, PaneRole.Split));
                    host.Stop(new PaneKey(splitSession, PaneRole.OverlayRight));
                    model.CloseSplit(splitSession);
                    break;
                case "swapPanes":
                    Guid swapSession = GetGuid(message, "sessionId");
                    if (model.SwapPanes(swapSession))
                    {
                        SwapPaneShells(swapSession);
                    }

                    break;
                case "takeLead":
                    if (Panes.TryParse(GetOptionalString(message, "pane"), out PaneRole led))
                    {
                        TakeOver(new PaneKey(GetGuid(message, "sessionId"), led));
                    }

                    break;
                case "focusPane":
                    if (Panes.TryParse(GetOptionalString(message, "pane"), out PaneRole focus))
                    {
                        model.FocusPane(GetGuid(message, "sessionId"), focus);
                    }

                    break;
                case "setSplitRatio":
                    model.SetSplitRatio(GetGuid(message, "sessionId"), message.GetProperty("ratio").GetDouble());
                    break;
                case "setScratch":
                    model.SetScratch(GetGuid(message, "sessionId"), message.TryGetProperty("shown", out JsonElement shown) && shown.ValueKind != JsonValueKind.Null ? shown.GetBoolean() : null);
                    break;
                case "overlayVisibility":
                    // agterm's toggle_overlay, the title bar button and a page's Hide: the same as session.overlay.hide|show|toggle.
                    PaneRole? visibilityScope = Panes.TryParseAny(GetOptionalString(message, "slot"), out PaneRole visibilitySlot) ? visibilitySlot : null;
                    string visibilityMode = GetOptionalString(message, "mode") ?? "toggle";
                    model.SetOverlaysHidden(GetGuid(message, "sessionId"), visibilityScope, visibilityMode == "hide" ? true : visibilityMode == "show" ? false : null);
                    break;
                case "closeOverlay":
                    Guid overlaySession = GetGuid(message, "sessionId");
                    PaneRole slot = Panes.TryParseAny(GetOptionalString(message, "slot"), out PaneRole parsedSlot) && Panes.IsOverlay(parsedSlot) ? parsedSlot : PaneRole.Overlay;
                    host.Stop(new PaneKey(overlaySession, slot));
                    model.CloseOverlay(overlaySession, slot: slot);
                    break;
                case "setQuick":
                    // The user's own toggle: the panel closes again when the keyboard goes elsewhere.
                    if (message.TryGetProperty("shown", out JsonElement quick) && quick.ValueKind != JsonValueKind.Null)
                    {
                        Library.Quick.SetShown(quick.GetBoolean(), pinned: false);
                    }
                    else
                    {
                        Library.Quick.Toggle();
                    }
                    break;
                case "runCustom":
                    string? customName = GetOptionalString(message, "name");
                    if (customName is not null)
                    {
                        _ = RunPageCustomAsync(customName, GetOptionalGuid(message, "sessionId") ?? model.SelectedSessionId,
                            GetOptionalString(message, "pane"), GetOptionalString(message, "selection"), GetOptionalString(message, "source") == "keybind");
                    }

                    break;
                case "editConfig":
                    EditConfig(GetOptionalString(message, "file") ?? "keymap");
                    break;
                case "reloadConfig":
                    string? reloaded = GetOptionalString(message, "file");
                    if (reloaded == "ghostty")
                    {
                        Library.ReloadGhostty();
                        break;
                    }

                    OnConfigFilesChanged(reloaded != "hooks", reloaded != "keymap");
                    break;
                case "openDashboard":
                    _ = dispatcher.HandleAsync(new ControlRequest { Cmd = "dashboard", Args = new ControlArgs { Mru = true, AutoSize = true } });
                    break;
                case "dashboardMove":
                    model.MoveDashboardHighlight(GetOptionalString(message, "direction") ?? string.Empty);
                    break;
                case "dashboardActivate":
                    model.ActivateDashboardCell(message.GetProperty("index").GetInt32());
                    break;
                case "dashboardClick":
                    // agterm's clickDashboardMember: the frame flashes on the cell, which is entered 0.18 s later if still highlighted.
                    if (model.HighlightDashboardCell(message.GetProperty("index").GetInt32()) is DashboardCell clicked)
                    {
                        _ = new DispatcherTimer(TimeSpan.FromSeconds(DashboardLayout.ClickEnterDelay), DispatcherPriority.Normal, (sender, args) =>
                        {
                            ((DispatcherTimer)sender!).Stop();
                            model.EnterDashboardCellIfHighlighted(clicked);
                        }, Dispatcher);
                    }

                    break;
                case "closeDashboard":
                    model.CloseDashboard();
                    break;
                case "overlayGeometry":
                    overlayGeometry[GetOptionalString(message, "overlayId") ?? string.Empty] = (message.GetProperty("width").GetInt32(), message.GetProperty("height").GetInt32());
                    break;
                case "hudMetrics":
                    // agterm's watchHudGeometry: the panel is measured again against the bounds it is laid out in now.
                    hudMetrics[GetOptionalString(message, "overlayId") ?? string.Empty] = new PaneMetrics(
                        message.GetProperty("cellWidth").GetDouble(),
                        message.GetProperty("cellHeight").GetDouble(),
                        message.GetProperty("paneWidth").GetDouble(),
                        message.GetProperty("paneHeight").GetDouble(),
                        message.GetProperty("paddingWidth").GetDouble(),
                        message.GetProperty("paddingHeight").GetDouble());
                    PostState();
                    break;
                case "searchShown":
                    string[] searchKey = (message.GetProperty("key").GetString() ?? string.Empty).Split(':');
                    if (searchKey.Length == 2 && Guid.TryParse(searchKey[0], out Guid searchSession) && Panes.TryParse(searchKey[1], out PaneRole searchPane))
                    {
                        if (message.GetProperty("shown").GetBoolean())
                        {
                            searchPanes[searchSession] = searchPane;
                        }
                        else if (searchPanes.GetValueOrDefault(searchSession) == searchPane)
                        {
                            searchPanes.Remove(searchSession);
                        }
                    }

                    break;
                case "quickClose":
                    Library.Quick.CloseRung();
                    break;
                case "setZoom":
                    model.SetZoom(message.TryGetProperty("zoomed", out JsonElement zoomed) && zoomed.ValueKind != JsonValueKind.Null ? zoomed.GetBoolean() : null);
                    break;
                case "changeFont":
                    // agterm: the font keys change the terminal in focus, not the whole session.
                    model.ChangeFontSize(GetGuid(message, "sessionId"), message.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Number ? delta.GetDouble() : null,
                        message.TryGetProperty("pane", out JsonElement fontPane) && Panes.TryParse(fontPane.GetString(), out PaneRole fontRole) ? fontRole : PaneRole.Main);
                    break;
                case "clearStatus":
                    model.SetStatus(GetGuid(message, "sessionId"), AgentStatus.Idle, force: true);
                    break;
                case "markAttention":
                    model.MarkForAttention(GetGuid(message, "sessionId"));
                    break;
                case "selectAttention":
                    // agterm: the current session's own pane is revealed when it is the only one waiting.
                    if (model.FindAttention(message.GetProperty("forward").GetBoolean()) is Guid attention)
                    {
                        model.SelectSession(attention, reveal: true);
                    }
                    else if (model.SelectedSessionId is Guid selectedId)
                    {
                        model.RevealStatus(selectedId);
                    }

                    break;
                case "selectAttentionEntry":
                    if (!ToolbarAttentionEnabled) break;
                    Guid attentionWindow = GetGuid(message, "windowId");
                    Library.OpenWindows.FirstOrDefault(window => window.Entry.Id == attentionWindow)?.SelectAttention(GetGuid(message, "sessionId"), fromToolbar: true);
                    break;
                case "setCollapsed":
                    model.SetWorkspaceCollapsed(GetGuid(message, "workspaceId"), message.GetProperty("collapsed").GetBoolean());
                    break;
                case "updateSettings":
                    UpdateSettings(message.GetProperty("patch"));
                    break;
                case "openSettings":
                    Library.SettingsWindow.Show();
                    break;
                case "quit":
                    Library.Quit(this);
                    break;
                case "minimizeWindow":
                    WindowState = WindowState.Minimized;
                    break;
                case "resetLiveSessions":
                    Library.ResetLiveFromMenu(this);
                    break;
                case "previewTheme":
                    PreviewTheme(GetOptionalString(message, "name"), message.TryGetProperty("commit", out JsonElement commit) && commit.GetBoolean());
                    break;
                case "requestLists":
                    _ = PostListsAsync();
                    break;
                case "paste":
                    PasteClipboard(GetGuid(message, "sessionId"), GetOptionalString(message, "pane") ?? "left");
                    break;
                case "pasteSelection":
                    // The middle button pastes the selection clipboard, as Ghostty's.
                    Post(new { type = "paste", sessionId = GetGuid(message, "sessionId"), pane = GetOptionalString(message, "pane") ?? "left", text = TerminalClipboardHost.Selection ?? string.Empty, clipboard = true, selection = true });
                    break;
                case "openLink":
                    OpenLink(GetOptionalString(message, "url") ?? string.Empty, GetOptionalGuid(message, "sessionId"), GetOptionalString(message, "pane"));
                    break;
                case "setSidebar":
                    SetSidebar(
                        message.TryGetProperty("visible", out JsonElement visible) ? visible.GetBoolean() : null,
                        message.TryGetProperty("width", out JsonElement width) ? width.GetDouble() : null);
                    break;
            }
        }

        private void NewSession(Guid? workspaceId, string? profileId)
        {
            Workspace workspace = (workspaceId is Guid id ? model.FindWorkspace(id) : null)
                ?? model.CurrentWorkspace
                ?? model.AddWorkspace();
            (string? cwd, string? placeProfile, int? index) = NewSessionPlace(workspace);
            ShellProfile profile = (profileId ?? placeProfile) is string chosen ? ProfileOrDefault(chosen) : DefaultProfile();
            model.AddSession(workspace.Id, profile.Id, profileId is null || profileId == placeProfile ? cwd : null, index);
        }

        private bool RevealSession(Guid sessionId)
        {
            Session? session = model.FindSession(sessionId);
            string? path = session is null ? null : LocalDirectory(session);
            if (path is null || !Directory.Exists(path))
            {
                return false;
            }

            // A drive or share root has no parent folder to select it in, so it is opened instead.
            string arguments = Path.GetDirectoryName(path) is null ? "\"" + path + "\"" : "/select,\"" + path.TrimEnd('\\') + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            return true;
        }

        /// <summary>What a shell learns about agshell: which session and pane it is and where to reach the control pipe. agshellctl is put on its PATH.</summary>
        private Dictionary<string, string?> SessionEnvironment(PaneKey key)
        {
            return new Dictionary<string, string?>
            {
                ["AGSHELL_ENABLED"] = "1",
                // The quick terminal belongs to no session, as in agterm: it only learns where the pipe is.
                ["AGSHELL_SESSION_ID"] = key.Pane == PaneRole.Quick ? null : key.SessionId.Wire(),
                ["AGSHELL_PANE"] = key.Pane is PaneRole.Main or PaneRole.Split or PaneRole.Scratch ? Panes.Name(key.Pane) : null,
                // agterm's stable pane token: it follows this terminal through a swap or a promoted split, where AGSHELL_PANE goes stale.
                ["AGSHELL_PANE_ID"] = key.Pane is PaneRole.Main or PaneRole.Split or PaneRole.Scratch && model.FindSession(key.SessionId) is not null ? model.PaneIdFor(key.SessionId, key.Pane) : null,
                ["AGSHELL_WINDOW_ID"] = key.Pane == PaneRole.Quick ? null : entry.Id.Wire(),
                ["AGSHELL_WORKSPACE_ID"] = model.FindWorkspaceOf(key.SessionId)?.Id.Wire(),
                [ControlEndpoint.PipeVariable] = Library.ServedPipe ?? Library.PipeName + ".unavailable",
                ["TERM_PROGRAM"] = "agshell",
                // Ghostty sets it for every shell, so programs use 24-bit color; ConPTY passes it through.
                ["COLORTERM"] = "truecolor",
                // agterm: the version only; the commit is for diagnostics.
                ["TERM_PROGRAM_VERSION"] = ControlAppIdentity.Parse(Version).Version,
                ["PATH"] = SearchPath.Current(),
                // A live shell's daemon names itself to its shell; one inherited from where AgShell started names another.
                [ControlEndpoint.LivePipeVariable] = null,
            };
        }

        public void RestartSession(Guid sessionId, PaneRole pane)
        {
            // A pane of another machine has no shell here to restart.
            if (model.FindSession(sessionId) is Session remoteRow && AppModel.IsRemotePane(remoteRow, pane))
            {
                return;
            }

            if (model.FindSession(sessionId)?.LiveFailed(pane) == true)
            {
                _ = RestartFailedPaneFromMenuAsync(sessionId, pane);
                return;
            }

            EndForRestart(sessionId, pane);
            StartAfterRestart(sessionId, pane);
        }

        private void EndForRestart(Guid sessionId, PaneRole pane)
        {
            host.Stop(new PaneKey(sessionId, pane));

            // agterm's clearPaneOwnedState: the pane's own overlay, HUD, question, status and job go with its shell.
            if (model.FindSession(sessionId) is not null && pane is PaneRole.Main or PaneRole.Split)
            {
                host.Stop(new PaneKey(sessionId, Panes.OverlayOf(pane)));
                model.ClearPaneOwnedState(sessionId, pane);
            }
        }

        private void StartAfterRestart(Guid sessionId, PaneRole pane)
        {
            PostState();
            Post(new { type = "restart", sessionId, pane = Panes.Name(pane) });
        }

        /// <summary>
        /// agterm's session.restart: the program to run again is read before the shell ends, the new shell gets the line once
        /// it shows a prompt, and the answer waits for that shell to exist.
        /// </summary>
        public async Task<(string? Error, ControlRestartReceipt? Receipt)> RestartPaneAsync(Guid sessionId, PaneRole pane, string? command)
        {
            PaneKey key = new PaneKey(sessionId, pane);
            if (model.FindSession(sessionId) is not Session session)
            {
                return ("no such session", null);
            }

            if (session.LiveFailed(pane))
            {
                return await RestartFailedPaneAsync(session, pane, command);
            }

            // agterm: a pane just created may not have its shell yet, so it is waited for up to five seconds; a pane with no
            // shell running is refused rather than started anew. A live pane has pid 0 until its host says the shell opened.
            DateTime asked = DateTime.UtcNow;
            int? oldPid = null;
            while (true)
            {
                if (host.ProcessId(key) is int running && running > 0)
                {
                    oldPid = running;
                    break;
                }

                if (DateTime.UtcNow - asked >= TimeSpan.FromSeconds(5))
                {
                    return ("the pane's shell is not running", null);
                }

                await Task.Delay(100);
            }

            string? line;
            string? directory = null;
            List<string>? replayed = null;
            int shell = oldPid.Value;
            string? distribution = session.ProfileId.StartsWith(ShellProfiles.WslIdPrefix, StringComparison.Ordinal) ? session.ProfileId[ShellProfiles.WslIdPrefix.Length..] : null;
            // agterm reads the foreground job before the kill: without it the restart could not tell when the old program is gone.
            (RestartReplay.Observation Replay, List<ProcessTree.JobProcess> Job)? observed = distribution is not null
                ? await ProcessTree.WslRestart(distribution, sessionId, Panes.Name(pane))
                : await Task.Run(() => ProcessTree.WindowsForegroundJob(shell) is List<ProcessTree.JobProcess> job ? (command is null ? ProcessTree.WindowsReplay(shell) : default, job) : ((RestartReplay.Observation, List<ProcessTree.JobProcess>)?)null);
            if (command is not null)
            {
                line = command;
            }
            else
            {
                // agterm's RestartReplay refusals, decided before anything ends.
                (string? Line, string? Directory, string? Error) resolved = RestartReplay.Resolve(observed?.Replay ?? default, Library.ReadDenylist());
                if (resolved.Error is not null)
                {
                    return (resolved.Error, null);
                }

                line = resolved.Line!;
                directory = resolved.Directory;
                replayed = SplitCommandLine(line);
            }

            if (observed?.Job is not List<ProcessTree.JobProcess> program)
            {
                return ("the process table cannot be read, so the old program could not be tracked; nothing was changed", null);
            }

            EndForRestart(sessionId, pane);
            // agterm: the new shell starts only once the old program is gone, so it cannot meet a held port or lock.
            bool ended = distribution is not null ? await ProcessTree.WslJobEnded(distribution, program) : await ProgramEndedAsync(program);
            Session? now = model.FindSession(sessionId);
            if (!ended)
            {
                if (now is not null)
                {
                    EndPane(now, key);
                }

                return ("the old shell ended (pid " + shell + ") but its program is still running; nothing was started and the pane was closed", null);
            }

            if (now is null || (pane == PaneRole.Split && !now.HasSplit) || host.ProcessId(key) is not null)
            {
                return ("the old shell ended (pid " + shell + ") and the pane changed during the restart; nothing was started", null);
            }

            restartLines[key] = line;
            if (directory is not null)
            {
                restartDirectories[key] = directory;
            }

            StartAfterRestart(sessionId, pane);
            DateTime started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100);
                if (host.ProcessId(key) is int newPid && newPid > 0 && newPid != oldPid)
                {
                    return (null, new ControlRestartReceipt { PaneId = model.PaneIdFor(sessionId, pane), OldPid = oldPid, NewPid = newPid, ReplayedArgv = replayed });
                }
            }

            restartLines.Remove(key);
            restartDirectories.Remove(key);
            return ("the old shell ended (pid " + shell + ") and no new one was observed", null);
        }

        /// <summary>
        /// agterm's programEnded: a second for the old foreground program to act on the console closing, then it is killed,
        /// as a restart replaces it; false when it outlives the kill too.
        /// </summary>
        private static async Task<bool> ProgramEndedAsync(List<ProcessTree.JobProcess> job)
        {
            foreach ((TimeSpan grace, bool kills) in new[] { (TimeSpan.FromSeconds(1), true), (TimeSpan.FromMilliseconds(500), false) })
            {
                DateTime deadline = DateTime.UtcNow + grace;
                while (DateTime.UtcNow < deadline)
                {
                    if (!ProcessTree.JobRunning(job))
                    {
                        return true;
                    }

                    await Task.Delay(100);
                }

                if (kills)
                {
                    ProcessTree.EndJob(job);
                }
            }

            return !ProcessTree.JobRunning(job);
        }

        public void SwapPaneShells(Guid sessionId)
        {
            // What this window keeps per pane follows the terminal, as the shells do.
            PaneKey main = new PaneKey(sessionId, PaneRole.Main);
            PaneKey split = new PaneKey(sessionId, PaneRole.Split);
            foreach (HashSet<PaneKey> marks in new[] { remoteJoined, claiming, reattaching, endedWhileHidden, endedThere })
            {
                bool hadMain = marks.Remove(main);
                bool hadSplit = marks.Remove(split);
                if (hadMain)
                {
                    marks.Add(split);
                }

                if (hadSplit)
                {
                    marks.Add(main);
                }
            }

            host.Swap(new PaneKey(sessionId, PaneRole.Main), new PaneKey(sessionId, PaneRole.Split));
            host.Swap(new PaneKey(sessionId, PaneRole.OverlayLeft), new PaneKey(sessionId, PaneRole.OverlayRight));
            // agterm swaps the command with its terminal: a running or held command goes on belonging to its pane.
            SwapKeys(commandRuns, sessionId);
            SwapKeys(commandHeld, sessionId);
            // agterm's swap clears the session's delivered banners: their pane names point at the other terminal now.
            ClearDeliveredBanners(sessionId);
            Post(new { type = "swap", sessionId });
            PostState();
        }

        public string Version => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        /// <summary>agterm's installed identity: read from the file on every call, so an update under a running app shows.</summary>
        public string? InstalledVersion
        {
            get
            {
                try
                {
                    string? product = Environment.ProcessPath is string path ? FileVersionInfo.GetVersionInfo(path).ProductVersion : null;
                    return string.IsNullOrEmpty(product) ? null : product;
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
            }
        }

        public IReadOnlyList<string> ShellIds => profiles.Select(profile => profile.Id).ToList();

        public string DefaultShellId => DefaultProfile().Id;

        public bool SidebarVisible => sidebar.Visible;

        public double SidebarWidth => sidebar.Width;

        public bool IsRunning(Guid sessionId, PaneRole pane)
        {
            // A pane of the other machine waiting to reconnect or held for a key is a live terminal, as agterm's attach
            // wrapper keeps its pane running then: it stays in the tree with its reconnect readback, and can be read.
            return host.IsRunning(new PaneKey(sessionId, pane)) || (model.FindSession(sessionId) is Session session && RemoteStateOf(session, pane) is not null);
        }

        public ControlOverlayJob OverlayLaunch(Guid sessionId, PaneRole slot, string command, string? cwd)
        {
            Session session = model.FindSession(sessionId)!;
            // agterm's OverlayCapture.cwd: the main pane's directory unless --cwd names one.
            string? directory = cwd ?? session.Cwd;
            PaneKey key = new PaneKey(sessionId, slot);
            PtyStartOptions options = ShellProfiles.CreateCommandOptions(ProfileOrDefault(session.ProfileId), command, directory, 120, 30, SessionEnvironment(key));
            // A directory asked for with --cwd goes as it is, so a missing one fails the launch as agterm's does.
            return new ControlOverlayJob { Command = options.CommandLine, Cwd = cwd ?? options.WorkingDirectory, Environment = options.Environment?.ToDictionary(pair => pair.Key, pair => pair.Value) };
        }

        public string? PaneLead(Guid sessionId, PaneRole pane)
        {
            return host.Lead(new PaneKey(sessionId, pane));
        }

        public bool PaneCovered(Guid sessionId, PaneRole pane)
        {
            return Covered(new PaneKey(sessionId, pane));
        }

        public async Task<DaemonScreen?> PaneScreenAsync(Guid sessionId, PaneRole pane, bool all)
        {
            return host.LivePaneId(new PaneKey(sessionId, pane)) is string paneId && liveDaemons is LiveDaemons daemons
                ? await daemons.ScreenAsync(paneId, all)
                : null;
        }

        public async Task<bool> PaneTypeAsync(Guid sessionId, PaneRole pane, string text)
        {
            return host.LivePaneId(new PaneKey(sessionId, pane)) is string paneId && liveDaemons is LiveDaemons daemons
                && await daemons.TypeAsync(paneId, text);
        }

        public string? TakeLead(Guid sessionId, PaneRole pane)
        {
            PaneKey key = new PaneKey(sessionId, pane);
            string? lead = host.Lead(key);
            // agterm: a pane with no terminal yet is not realized, one joining again has its fresh one; one whose shell reports
            // no lead has none to take.
            if (!host.IsRunning(key) && !reattaching.Contains(key))
            {
                return "session not realized";
            }

            if (lead is null && !Covered(key))
            {
                return "pane has no lead to take";
            }

            // agterm: a covered pane is taken over, one already on its way is left to arrive; a pane that leads answers ok.
            TakeOver(key);
            return null;
        }

        public void StopSession(Guid sessionId)
        {
            host.StopSession(sessionId);
            StopPresentation(sessionId);
            ForgetWaits(sessionId);
            endedWhileHidden.RemoveWhere(key => key.SessionId == sessionId);
            endedThere.RemoveWhere(key => key.SessionId == sessionId);
            searchPanes.Remove(sessionId);
            claiming.RemoveWhere(key => key.SessionId == sessionId);
            restartLines.Remove(new PaneKey(sessionId, PaneRole.Main));
            restartLines.Remove(new PaneKey(sessionId, PaneRole.Split));
            restartDirectories.Remove(new PaneKey(sessionId, PaneRole.Main));
            restartDirectories.Remove(new PaneKey(sessionId, PaneRole.Split));
            reattaching.RemoveWhere(key => key.SessionId == sessionId);
            ReleaseRemoteClients(sessionId);
            Library.Pacer.Discard(new PaneKey(sessionId, PaneRole.Main));
            Library.Pacer.Discard(new PaneKey(sessionId, PaneRole.Split));
            paced.Remove(new PaneKey(sessionId, PaneRole.Main));
            paced.Remove(new PaneKey(sessionId, PaneRole.Split));
            commandRuns.RemoveWhere(key => key.SessionId == sessionId);
            commandHeld.RemoveWhere(key => key.SessionId == sessionId);
        }

        public void StopPane(Guid sessionId, PaneRole pane)
        {
            host.Stop(new PaneKey(sessionId, pane));
            if (pane == PaneRole.Split)
            {
                host.Stop(new PaneKey(sessionId, PaneRole.OverlayRight));
            }
        }

        public void Write(Guid sessionId, PaneRole pane, string data)
        {
            host.Write(new PaneKey(sessionId, pane), data);
        }

        public async Task<string> ReadTextAsync(Guid sessionId, PaneRole pane, bool all, int? lines)
        {
            // agterm: a pane with no terminal is not realized.
            return await AskPageAsync(requestId => new { type = "readText", requestId, sessionId, pane = Panes.Name(pane), all, lines }) ?? throw new ControlException("session not realized");
        }

        /// <summary>The HUD's width and height as laid out against the bounds its page measured, in percent of its pane.</summary>
        public (int Width, int Height)? HudGeometry(string overlayId)
        {
            return hudMetrics.TryGetValue(overlayId, out PaneMetrics? metrics) && model.AllSessions.Select(session => session.Overlay).FirstOrDefault(overlay => overlay?.Id == overlayId) is { Hud: HudSpec hud } overlay
                ? HudPanel(overlay, hud, metrics) is (HudPanelSize size, _) ? (size.WidthPercent, size.HeightPercent) : null
                : null;
        }

        /// <summary>
        /// agterm's openHud and writeHudBody: the panel's size from the message and the metrics its page reported (none yet:
        /// an unmeasured pane), and the rows painted in its grid.
        /// </summary>
        private static (HudPanelSize Size, HudPaint Paint) HudPanel(SessionOverlay overlay, HudSpec hud, PaneMetrics? metrics)
        {
            PaneMetrics pane = metrics ?? new PaneMetrics(0, 0, 0, 0);
            int? percent = overlay.SizePercent is int asked ? HudSpec.ClampWidth(asked, hud) : null;
            HudPanelSize size = HudLayout.PanelSize(hud, percent, pane);
            return (size, HudLayout.Paint(hud, percent, HudLayout.PaintGrid(hud, percent, size, pane)));
        }

        private object? HudPaintView(SessionOverlay overlay)
        {
            if (overlay.Hud is not HudSpec hud)
            {
                return null;
            }

            (HudPanelSize size, HudPaint paint) = HudPanel(overlay, hud, hudMetrics.GetValueOrDefault(overlay.Id));
            return new
            {
                columns = paint.Columns,
                rows = paint.Rows,
                blockWidth = paint.BlockWidth,
                lines = paint.Lines.Select(line => line.Select(run => new { text = run.Text, style = (int)run.Style, link = run.Link })),
                widthPercent = size.WidthPercent,
                heightPercent = size.HeightPercent,
                heightPoints = size.HeightPoints,
            };
        }

        public (int Width, int Height)? OverlayGeometry(string overlayId)
        {
            return overlayGeometry.TryGetValue(overlayId, out (int Width, int Height) size) ? size : null;
        }

        /// <summary>A page overlay as its view reports it: where it is, its title, how its load went and its history.</summary>
        public ControlPageView? PageState(string overlayId)
        {
            if (nativePages?.LoadState(overlayId) is { } native)
            {
                // A local page names the file it shows, not its host's address.
                string? shown = native.Url is null ? null : pageHost?.LocalPath(native.Url) ?? native.Url;
                return new ControlPageView(shown, native.Title, native.State, native.Error, native.CanGoBack, native.CanGoForward, settings.EffectiveHtmlOverlayZoom);
            }

            // Not placed yet: its browser is made when the interface first gives it room.
            return new ControlPageView(null, null, "loading", null, null, null, settings.EffectiveHtmlOverlayZoom);
        }

        /// <summary>As agterm's, a page lives until its session closes for good: an undone close brings it back as it was.</summary>
        private void RetainPages()
        {
            retainQueued = false;
            IEnumerable<Session> held = model.AllSessions.Concat(model.PendingCloses.SelectMany(close => close.Sessions));
            List<string> pages = held.SelectMany(session => session.Overlays.Values).Where(overlay => overlay.Page is not null).Select(overlay => overlay.Id).ToList();
            pageHost?.Retain(pages);
            foreach (string gone in nativePages?.Retain(pages) ?? new List<string>())
            {
                Post(new { type = "pageGone", overlayId = gone });
            }
        }

        private bool retainQueued;

        private readonly PaneFocusEvents paneFocus = new PaneFocusEvents();

        /// <summary>Hands the interface what a page shows now, for its strip and its failure panel.</summary>
        public bool IsReplicaPage(string overlayId)
        {
            return replicaPages.ContainsKey(overlayId);
        }

        private void PostPageInfo(string overlayId)
        {
            ReportReplicaPages();
            if (PageState(overlayId) is ControlPageView view)
            {
                Post(new { type = "pageInfo", overlayId, state = view.State, error = view.Error, page = view.Page, title = view.Title, canGoBack = view.CanGoBack == true, canGoForward = view.CanGoForward == true, site = nativePages?.SiteOf(overlayId) });
            }
        }

        public PaneRole? SearchPane(Guid sessionId)
        {
            return searchPanes.TryGetValue(sessionId, out PaneRole pane) ? pane : null;
        }

        public async Task<string> SearchAsync(Guid sessionId, PaneRole pane, string? needle, string? step)
        {
            return await AskPageAsync(requestId => new { type = "search", requestId, sessionId, pane = Panes.Name(pane), needle, step }) ?? throw new ControlException("session not realized");
        }

        public Task<string?> TerminalAsync(Guid sessionId, PaneRole pane, string action, string? text)
        {
            return AskPageAsync(requestId => new { type = "terminal", requestId, sessionId, pane = Panes.Name(pane), action, text }, action == "cursor" ? "failed to read cursor position" : "failed to read surface buffer");
        }

        public string? ReadClipboard()
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }

        /// <summary>The clipboard-write setting: whether programs may set the clipboard.</summary>
        internal bool ClipboardWriteAllowed => settings.ClipboardWrite != false;

        /// <summary>The key press behind an input, as the page saw it; "text" is typing with no key event (an IME, dictation).</summary>
        private static StatusKeystroke? KeystrokeOf(JsonElement message)
        {
            if (!message.TryGetProperty("stroke", out JsonElement stroke))
            {
                return null;
            }

            if (stroke.ValueKind == JsonValueKind.String)
            {
                return StatusKeystroke.Other;
            }

            return AgentStatuses.ClassifyKey(
                stroke.GetProperty("key").GetString() ?? string.Empty,
                stroke.GetProperty("code").GetString() ?? string.Empty,
                stroke.GetProperty("ctrl").GetBoolean(),
                stroke.GetProperty("alt").GetBoolean(),
                stroke.GetProperty("shift").GetBoolean(),
                stroke.GetProperty("meta").GetBoolean());
        }

        void IClipboardPromptView.ClipboardPromptsChanged()
        {
            PostState();
        }

        /// <summary>
        /// The terminal's own answer to its program (a clipboard reply, a paste event). As the input path's reports, it is
        /// dropped on a covered pane, as agterm's daemon drops what a managed client that does not lead sends.
        /// </summary>
        private void ReplyToPane(PaneKey pane, string data)
        {
            if (!Covered(pane))
            {
                host.Write(pane, data);
            }
        }

        public void WriteClipboard(string text)
        {
            // Another program can hold the clipboard for a moment; WPF's own retry gives up on the first refusal.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return;
                }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
                {
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// Sends the page a request carrying an id and waits for its "reply" with the same id; null when it had no terminal.
        /// As agterm's: a page not loaded yet is a session not realized, a page that does not answer a failed read.
        /// </summary>
        private async Task<string?> AskPageAsync(Func<int, object> message, string failure = "failed to read surface buffer")
        {
            if (!pageReady)
            {
                throw new ControlException("session not realized");
            }

            int requestId = ++nextPageRequestId;
            TaskCompletionSource<string?> completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingPageRequests[requestId] = completion;
            Post(message(requestId));
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            pendingPageRequests.Remove(requestId);
            return finished == completion.Task ? completion.Task.Result : throw new ControlException(failure);
        }

        public bool Reveal(Guid sessionId)
        {
            return RevealSession(sessionId);
        }

        /// <summary>agshellctl notify's banner, in the session's own slot apart from its panes', as agterm's.</summary>
        public bool Notify(Guid sessionId, string title, string body)
        {
            NotifySession(sessionId, NotifySlot, title, body, "send");
            return settings.NotificationsEnabled != false;
        }

        /// <summary>The slot of a session's banners agshellctl notify and a mirrored note post in.</summary>
        private const string NotifySlot = "notify";

        /// <summary>
        /// agterm's session banner: identified by the window, the session and the pane that fired it (or the notify slot),
        /// so a repeat replaces the last instead of stacking, and a click goes back there. The taskbar button flashes as
        /// agterm bounces the Dock icon. <paramref name="source"/> is agterm's log prefix: notify for a terminal's
        /// notification, send for agshellctl notify and a mirrored one.
        /// </summary>
        private void NotifySession(Guid sessionId, string slot, string title, string body, string source)
        {
            if (settings.NotificationsEnabled != false)
            {
                Log.Notice("NotificationManager", source + ": posting banner for session " + sessionId.Wire());
                Library.ShowBanner(sessionId + ":" + slot, Entry.Id.Wire(), title, body, new ToastArguments { { "session", sessionId.Wire() }, { "pane", slot }, { "window", Entry.Id.Wire() } }, source == "notify" ? "add failed" : "send failed");
            }
            else
            {
                Log.Notice("NotificationManager", source + ": badge bumped for session " + sessionId.Wire() + ", but no banner — \"Show notification banners\" is off in Settings > Notifications");
            }

            // macOS does not bounce the Dock while the app is active, whichever of its windows is in front.
            if (!WindowLibrary.AppActive && settings.EffectiveTaskbarFlash != "off")
            {
                Taskbar.Flash(new WindowInteropHelper(this).Handle, settings.EffectiveTaskbarFlash == "untilFocused");
            }
        }

        /// <summary>One of the app's own banners, posted by the app, not by this window: see <see cref="WindowLibrary.NotifyApp"/>.</summary>
        internal void NotifyApp(string slot, string title, string body)
        {
            Library.NotifyApp(slot, title, body);
        }

        /// <summary>agterm's clearDelivered: a session seen takes its delivered banners out of the notification center.</summary>
        private void ClearDeliveredBanners(Guid sessionId)
        {
            try
            {
                foreach (string slot in new[] { "left", "right", "scratch", "overlay", "overlay-left", "overlay-right", NotifySlot })
                {
                    ToastNotificationManagerCompat.History.Remove(WindowLibrary.BannerKey(sessionId + ":" + slot), WindowLibrary.BannerKey(Entry.Id.Wire()));
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is InvalidOperationException)
            {
            }
        }

        /// <summary>libghostty's limits are the app's, so every window shares them.</summary>
        private static readonly NotificationRateLimit TerminalNotificationLimit = new NotificationRateLimit();

        /// <summary>
        /// A program in the terminal asked for a notification (OSC 9 or 777). As under agterm, Ghostty's desktop-notifications
        /// and its rate limits come first, only a session's own panes notify, and it is dropped while the user types in that very
        /// pane; agshellctl notify is always shown.
        /// </summary>
        private void OnTerminalNotification(Guid sessionId, string pane, bool focused, string? title, string body)
        {
            if (!Library.Ghostty.DesktopNotifications)
            {
                Log.Notice("NotificationManager", "application attempted to display a desktop notification, but 'desktop-notifications' is disabled");
                return;
            }

            if (!TerminalNotificationLimit.Allow(title ?? string.Empty, body, TimeSpan.FromMilliseconds(Environment.TickCount64)))
            {
                Log.Notice("NotificationManager", "rate limiting desktop notifications");
                return;
            }

            // agterm wires notifications to the main and right panes only; a scratch or an overlay is sessionless there.
            Session? session = pane is "left" or "right" ? model.FindSession(sessionId) : null;
            if (session is null)
            {
                Log.Notice("NotificationManager", "notify: no open window owns session " + sessionId.Wire() + "; dropping");
                return;
            }

            // agterm's shouldDeliver: dropped only while the very pane that fired it has the keyboard in the active app.
            if (focused && IsActive)
            {
                Log.Notice("NotificationManager", "notify: session " + sessionId.Wire() + " is the focused pane; suppressed");
                return;
            }

            string effectiveTitle = string.IsNullOrEmpty(title) ? session.DisplayName : title;
            model.RecordNotification(sessionId, effectiveTitle, body, NotificationSources.Terminal);
            NotifySession(sessionId, pane, effectiveTitle, body, "notify");
        }

        /// <summary>agterm's clampSidebarWidth bounds, shared by a drag and sidebar.width.</summary>
        public const double MinSidebarWidth = 160;

        public const double MaxSidebarWidth = 560;

        /// <summary>agterm's setSidebarVisible: a window already so is left alone, nothing saved.</summary>
        internal void SetSidebarVisible(bool visible)
        {
            if (sidebar.Visible != visible)
            {
                SetSidebar(visible, null);
            }
        }

        public void SetSidebar(bool? visible, double? width)
        {
            SidebarSettings next = new SidebarSettings
            {
                Visible = visible ?? sidebar.Visible,
                Width = width is double value ? Math.Clamp(value, MinSidebarWidth, MaxSidebarWidth) : sidebar.Width,
            };
            // As agterm's setSidebarWidth: nothing to save or redraw when nothing changed.
            if (next == sidebar)
            {
                return;
            }
            sidebar = next;
            SaveNow();
            PostState();
        }

        /// <summary>
        /// Where a shell of this machine starts for the session. A remote row's directory is the other machine's: a Windows path
        /// is used when it exists here (the shell profiles fall back to home otherwise), a Linux one is not, as WSL cannot be
        /// asked here whether it exists there.
        /// </summary>
        private static string? LaunchDirectory(Session session, string? cwd)
        {
            return session.RemoteHost is not null && cwd is not null && cwd.StartsWith('/') ? null : cwd;
        }

        /// <summary>The session's directory as a Windows path, reaching WSL directories through \\wsl.localhost.</summary>
        /// <summary>The selected session's focused-pane directory as a Windows path, for agterm's folder pickers; null when none.</summary>
        internal void ResetFontSizes()
        {
            model.ResetFontSizes();
        }

        /// <summary>A picker waits for an answer in this window: it owns the keyboard.</summary>
        internal bool PickPending => model.Pick is not null;

        /// <summary>agterm's pickActive: a pick or a GUI ask waits in this window.</summary>
        internal bool ModalPending => model.PendingModalError is not null;

        internal string? ActiveSessionDirectory => model.SelectedSessionId is Guid id && model.FindSession(id) is Session session ? LocalDirectory(session) : null;

        /// <summary>agterm: typing in the quick terminal is the user at work, so auto-follow leaves the selection alone.</summary>
        internal void NoteQuickActivity()
        {
            NoteActivity();
        }

        private string? LocalDirectory(Session session)
        {
            ShellProfile profile = ProfileOrDefault(session.ProfileId);
            string? cwd = session.FocusedCwd;
            if (profile.Kind != ShellKind.Wsl)
            {
                return cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (cwd is null || !cwd.StartsWith('/'))
            {
                return null;
            }

            return @"\\wsl.localhost\" + profile.Distribution + cwd.Replace('/', '\\');
        }

        private bool CanReveal(Session session)
        {
            string? path = LocalDirectory(session);
            // Checking a WSL path on every state update would hit the 9P share each time.
            return path is not null && (path.StartsWith(@"\\wsl.localhost\", StringComparison.Ordinal) || Directory.Exists(path));
        }

        private void CloseSession(Guid sessionId)
        {
            StopSession(sessionId);
            model.RemoveSession(sessionId);
        }

        private void OnShellExited(object shell, int code)
        {
            // A shell that was stopped or replaced on purpose says nothing about the session.
            if (host.KeyOf(shell) is not PaneKey key)
            {
                return;
            }

            if (host.DaemonPaneId(key) is string daemonPane)
            {
                Log.Error("GhosttySurfaceView", "live attach process exited for pane " + daemonPane);
            }

            bool lostLivePane = host.LostLivePane(shell);
            host.Flush(PostOutput);
            if (lostLivePane)
            {
                host.Detach(key);
            }
            else
            {
                host.Stop(key);
            }
            clipboard.Forget(key);

            Guid sessionId = key.SessionId;
            Session? session = model.FindSession(sessionId);
            if (session is null)
            {
                if (lostLivePane && model.FindSessionOrPending(sessionId) is Session pending && !AppModel.IsRemotePane(pending, key.Pane))
                {
                    model.SetLiveFailed(sessionId, key.Pane, true);
                    reattaching.Remove(key);
                    claiming.Remove(key);
                    return;
                }

                // A remote row closed with undo still in reach: its pane ending is read as on a shown row, and what follows
                // (losing the pane, waiting for the machine, holding for a key) is there when the undo brings it back.
                if (model.FindSessionOrPending(sessionId) is { RemoteHost: string hidden } hiddenRow && AppModel.IsRemotePane(hiddenRow, key.Pane))
                {
                    _ = OnRemotePaneEndedAsync(hiddenRow, hidden, key);
                }

                return;
            }

            if (lostLivePane && !AppModel.IsRemotePane(session, key.Pane))
            {
                reattaching.Remove(key);
                claiming.Remove(key);
                model.SetLiveFailed(sessionId, key.Pane, true);
                PostLiveFailure(key);
                PostState();
                return;
            }

            if (Panes.IsOverlay(key.Pane))
            {
                if (session.Overlays.GetValueOrDefault(key.Pane) is SessionOverlay overlay && !overlay.IsHud)
                {
                    if (overlay.Wait)
                    {
                        PostOutput(key, "\r\n\u001b[2m[" + Strings.T("процесс завершился с кодом {0}, нажмите любую клавишу", code) + "]\u001b[0m");
                    }

                    model.OverlayExited(sessionId, overlay.Id, code, key.Pane);
                    if (orphanedOverlays.Remove(overlay.Id))
                    {
                        model.CloseOverlay(sessionId, overlay.Id, key.Pane);
                    }
                }

                PostState();
                return;
            }

            if (session.RemoteHost is string remote && AppModel.IsRemotePane(session, key.Pane))
            {
                _ = OnRemotePaneEndedAsync(session, remote, key);
                return;
            }

            // A pane that ran its session's command with --wait holds its last output until a key, then ends as a shell does.
            bool wait = key.Pane == PaneRole.Main ? session.CommandWait : key.Pane == PaneRole.Split && session.SplitCommandWait;
            if (commandRuns.Remove(key) && wait)
            {
                PostOutput(key, "\r\n\u001b[2m[" + Strings.T("процесс завершился с кодом {0}, нажмите любую клавишу", code) + "]\u001b[0m");
                commandHeld.Add(key);
                PostState();
                return;
            }

            EndPane(session, key);
        }

        /// <summary>
        /// As agterm's pane exit, whatever the code: a scratch or split closes, a main pane hands the session to a live split,
        /// and a session left with no pane closes.
        /// </summary>
        private void EndPane(Session session, PaneKey key)
        {
            Guid sessionId = session.Id;
            switch (key.Pane)
            {
                case PaneRole.Scratch:
                    model.ScratchClosed(sessionId);
                    PostState();
                    return;
                case PaneRole.Split:
                    host.Stop(new PaneKey(sessionId, PaneRole.OverlayRight));
                    model.CloseSplit(sessionId);
                    return;
                case PaneRole.Main when session.HasSplit:
                    PaneKey split = new PaneKey(sessionId, PaneRole.Split);
                    host.Move(split, key);
                    host.Move(new PaneKey(sessionId, PaneRole.OverlayRight), new PaneKey(sessionId, PaneRole.OverlayLeft));
                    if (commandRuns.Remove(split))
                    {
                        commandRuns.Add(key);
                    }

                    if (commandHeld.Remove(split))
                    {
                        commandHeld.Add(key);
                    }

                    Post(new { type = "promote", sessionId });
                    model.PromoteSplit(sessionId);
                    return;
                default:
                    model.RemoveSession(sessionId);
                    return;
            }
        }

        private static void SwapKeys(HashSet<PaneKey> keys, Guid sessionId)
        {
            bool main = keys.Remove(new PaneKey(sessionId, PaneRole.Main));
            bool split = keys.Remove(new PaneKey(sessionId, PaneRole.Split));
            if (main)
            {
                keys.Add(new PaneKey(sessionId, PaneRole.Split));
            }

            if (split)
            {
                keys.Add(new PaneKey(sessionId, PaneRole.Main));
            }
        }

        /// <summary>As agterm's window title: the current session's name, for the taskbar and Alt+Tab.</summary>
        private void SyncWindowTitle()
        {
            SyncChromeIdentity();
            string title = model.SelectedSessionId is Guid id && model.FindSession(id) is Session session ? session.DisplayName : "AgShell";
            // As agterm's: a window the user named carries the name after the session.
            if (entry.CustomName is string name)
            {
                title += " — " + name;
            }

            if (Title != title)
            {
                Title = title;
            }
        }

        private void PostState()
        {
            foreach (Session session in model.AllSessions.ToList())
            {
                model.DropUnrealizedPaneOverlays(session.Id, slot => host.IsRunning(new PaneKey(session.Id, slot)));
            }

            // agterm's zoom layer: a zoom whose surface is gone ends before the window draws again; that change posts anew.
            if (dispatcher.DropStaleZoom())
            {
                return;
            }

            SyncWindowTitle();
            // A page handed over hidden or shown here, by its strip or keys, says so there.
            ReportReplicaPages();
            if (!pageReady)
            {
                return;
            }

            // After the change in hand is done: an undoable close takes its sessions out of the tree before it holds them.
            if (!retainQueued)
            {
                retainQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, RetainPages);
            }

            Post(new
            {
                type = "state",
                state = new
                {
                    workspaces = model.Workspaces.Select(workspace => new
                    {
                        id = workspace.Id,
                        name = workspace.Name,
                        collapsed = model.ShownCollapsed(workspace),
                        focused = model.IsFocusMember(workspace.Id),
                        sessions = workspace.Sessions.Select(session => new
                        {
                            id = session.Id,
                            flagged = session.Flagged,
                            name = session.DisplayName,
                            customName = session.CustomName,
                            cwd = session.FocusedCwd,
                            profileId = session.ProfileId,
                            hasSplit = session.HasSplit,
                            splitShown = session.SplitShown,
                            splitFocused = session.SplitFocused,
                            splitAxis = Panes.AxisName(session.SplitAxis),
                            splitRatio = session.SplitRatio ?? Panes.DefaultSplitRatio,
                            focusedPane = Panes.Name(session.FocusedPane),
                            scratchShown = session.ScratchShown,
                            scratchRunning = host.IsRunning(new PaneKey(session.Id, PaneRole.Scratch)),
                            overlay = OverlayView(session.Overlay),
                            paneOverlays = new
                            {
                                left = OverlayView(session.Overlays.GetValueOrDefault(PaneRole.OverlayLeft)),
                                right = OverlayView(session.Overlays.GetValueOrDefault(PaneRole.OverlayRight)),
                            },
                            canReveal = CanReveal(session),
                            status = AgentStatuses.Name(session.Status),
                            blink = session.StatusBlink,
                            statusColor = Themes.Css(session.StatusColor),
                            statusShape = session.StatusShape,
                            context = session.EffectiveContext,
                            title = session.FocusedTitle,
                            ask = AskView(model.TerminalAskOf(session.Id)),
                            remoteHost = session.RemoteHost,
                            remoteWaits = new[] { PaneRole.Main, PaneRole.Split }.Where(pane => RemoteStateOf(session, pane) is not null)
                                .ToDictionary(Panes.Name, pane => new { state = RemoteStateOf(session, pane), reason = ReconnectOf(session.Id, pane)?.Reason }),
                            remoteStream = PresentationOf(session.Id)?.State,
                            remoteSplit = AppModel.IsRemotePane(session, PaneRole.Split),
                            remoteMain = AppModel.IsRemotePane(session, PaneRole.Main),
                            remoteStreamError = PresentationOf(session.Id)?.Error,
                            coveredPanes = new[] { PaneRole.Main, PaneRole.Split }.Where(pane => Covered(new PaneKey(session.Id, pane))).Select(Panes.Name),
                            takingPanes = new[] { PaneRole.Main, PaneRole.Split }.Where(pane => Taking(new PaneKey(session.Id, pane))).Select(Panes.Name),
                            unownedPanes = new[] { PaneRole.Main, PaneRole.Split }.Where(pane => host.Lead(new PaneKey(session.Id, pane)) == "unowned").Select(Panes.Name),
                            leaderSizes = new[] { PaneRole.Main, PaneRole.Split }
                                .Where(pane => host.LeaderSize(new PaneKey(session.Id, pane)) is not null)
                                .ToDictionary(Panes.Name, pane => new[] { host.LeaderSize(new PaneKey(session.Id, pane))!.Value.Columns, host.LeaderSize(new PaneKey(session.Id, pane))!.Value.Rows }),
                            background = BackgroundView(session.Background),
                            paneBackgrounds = new
                            {
                                left = BackgroundView(session.PaneBackgrounds.GetValueOrDefault(PaneRole.Main)),
                                right = BackgroundView(session.PaneBackgrounds.GetValueOrDefault(PaneRole.Split)),
                                scratch = BackgroundView(session.PaneBackgrounds.GetValueOrDefault(PaneRole.Scratch)),
                            },
                            unseen = session.UnseenCount,
                            statusChangedAt = session.StatusChangedAt?.ToUnixTimeMilliseconds(),
                            fontSize = session.FontSize ?? model.BaseFontSize,
                            splitFontSize = session.SplitFontSize ?? model.BaseFontSize,
                            scratchFontSize = session.ScratchFontSize ?? model.BaseFontSize,
                            sessionFontSize = session.SessionFontSize ?? model.BaseFontSize,
                        }),
                    }),
                    selectedSessionId = model.SelectedSessionId,
                    // agterm's customWindowName: the name given to the window, none for one called "window N".
                    windowName = Entry.CustomName,
                    zoomed = model.Zoomed,
                    zoomedSessionId = model.ZoomTarget?.Session,
                    zoomedPane = model.ZoomTarget is (Guid, PaneRole zoomedPane) ? Panes.Name(zoomedPane) : null,
                    dashboard = DashboardView(),
                    settings = SettingsView(),
                    pendingClose = PendingCloseView(),
                    sidebarMode = model.SidebarMode,
                    focusEnabled = model.FocusEnabled,
                    visibleWorkspaceIds = model.VisibleWorkspaces.Select(workspace => workspace.Id),
                    navigableSessionIds = model.NavigableSessions.Select(session => session.Id),
                    pendingSessionIds = model.PendingSessionIds,
                    recentClosed = RecentView(),
                    theme = ThemeView(),
                    keymap = KeymapView(),
                    attention = AttentionAcrossWindows(),
                    quick = new { shown = Library.Quick.Shown, pinned = Library.Quick.Pinned, running = Library.Quick.Running },
                    recentSessionIds = model.RecentSessions,
                    windows = Library.Entries.Select(window => new { id = window.Id, name = Library.Label(window), open = window.Open }),
                    currentWindowId = entry.Id,
                    pick = model.Pick is PickRequest pick
                        ? new { id = pick.Id, items = pick.Items.Select(item => item with { StatusColor = Themes.Css(item.StatusColor) }), prompt = pick.Prompt, query = pick.Query, selection = pick.Selection, allowCustom = pick.AllowCustom }
                        : null,
                    ask = AskView(model.GuiAsk),
                    clipboardPrompt = ClipboardPrompts.Shared.FirstFor(this),
                    currentWorkspaceId = model.CurrentWorkspace?.Id,
                    canRemoveWorkspace = model.CanRemoveWorkspace,
                    windowsBuild = Environment.OSVersion.Version.Build,
                    profiles = profiles.Select(profile => new { id = profile.Id, name = profile.Id == ShellProfiles.CmdId ? Strings.T("Командная строка") : profile.Name }),
                    defaultProfileId = DefaultProfile().Id,
                    sidebar = new { visible = sidebar.Visible, width = sidebar.Width },
                },
            });
        }

        private object KeymapView()
        {
            return new
            {
                actions = keymap.Actions.ToDictionary(pair => pair.Key, pair => pair.Value.Select(BindingView)),
                menu = keymap.MenuChords.ToDictionary(pair => pair.Key, pair => pair.Value is KeyChord chord ? BindingView(new KeyBinding(new[] { chord }, false)) : null),
                commands = keymap.Commands.Select(command => new { name = command.Name, shortcut = command.Shortcut, bindings = command.Bindings.Select(BindingView) }),
                mostUsed = keymap.Commands.Count > MostUsedCommandLimit ? Library.CommandUsage.MostUsed(keymap.Commands.Select(command => command.Name).ToList(), MostUsedCommandLimit) : new List<string>(),
            };
        }

        private static object BindingView(KeyBinding binding)
        {
            return new
            {
                sequence = binding.Sequence.Select(chord => new { meta = chord.Meta, ctrl = chord.Ctrl, alt = chord.Alt, shift = chord.Shift, code = chord.Code }),
                repeat = binding.Repeat,
                label = string.Join(">", binding.Sequence.Select(chord => chord.ToString())),
            };
        }

        private object? BackgroundView(ControlBackground? background)
        {
            return background is null ? null : new
            {
                kind = background.Kind,
                url = background.ImagePath is string path ? pageHost?.ImageUrl(path) : null,
                text = background.Text,
                color = Themes.Css(background.ColorHex),
                opacity = background.Opacity ?? 1,
                fit = background.Fit ?? "contain",
                position = background.Position ?? "center",
                repeat = background.Repeats == true,
            };
        }

        private static object? AskView(AskRequest? ask)
        {
            // A question handed to the machine presenting the session waits here undrawn.
            return ask is null || ask.RemoteOwner is not null ? null : new
            {
                id = ask.Id,
                title = ask.Title,
                message = ask.Message,
                buttons = ask.Buttons,
                defaultButton = ask.DefaultButton,
                destructiveButton = ask.DestructiveButton,
                cancelButton = ask.CancelButton,
                style = ask.Style,
                align = ask.Align,
                width = ask.Width,
                input = ask.Input,
                inputValue = ask.InputValue,
                placeholder = ask.Placeholder,
                choices = ask.Choices,
                multiple = ask.Multiple,
                @checked = ask.Checked,
                other = ask.Other,
                pane = ask.Pane is PaneRole pane ? Panes.Name(pane) : null,
                sessionId = ask.SessionId,
            };
        }

        private object? DashboardView()
        {
            if (model.Dashboard is not DashboardState dashboard)
            {
                return null;
            }

            (int cols, int rows) = DashboardLayout.Grid(dashboard.Cells.Count);
            double? fontSize = DashboardLayout.AppliedFontSize(dashboard, model.DashboardBaseFontSize);
            return new
            {
                cells = dashboard.Cells.Select(cell => new { sessionId = cell.SessionId, pane = Panes.Name(cell.Pane), coverId = model.FindSession(cell.SessionId) is Session session ? DashboardLayout.Cover(session, cell.Pane)?.Id : null }),
                highlight = dashboard.Highlight,
                cols,
                rows,
                fontSize,
            };
        }

        private object? OverlayView(SessionOverlay? overlay)
        {
            return overlay is null ? null : new
            {
                id = overlay.Id,
                kind = overlay.IsHud ? "hud" : overlay.Page is not null ? "page" : "program",
                hudPane = overlay.HudPane is PaneRole hudPane ? Panes.Name(hudPane) : null,
                hidden = overlay.Hidden,
                widthPoints = overlay.WidthPoints,
                heightPoints = overlay.HeightPoints,
                command = overlay.IsProgram && !replicaOverlays.ContainsKey(overlay.Id) ? overlay.Command : null,
                sizePercent = overlay.SizePercent,
                backgroundColor = Themes.Css(overlay.BackgroundColor),
                exited = overlay.ExitCode is not null,
                hud = overlay.Hud is HudSpec hud ? hud with { TextColor = Themes.Css(hud.TextColor) } : null,
                paint = HudPaintView(overlay),
                page = overlay.Page is not PageSpec page ? null : new
                {
                    source = page.Source,
                    isFile = page.IsFile,
                    remoteFile = page.IsFile && replicaPages.ContainsKey(overlay.Id),
                    js = page.Js,
                    navigation = page.Navigation,
                    chromeless = page.Chromeless,
                    browse = page.Browse,
                },
            };
        }

        private void FlushOutput()
        {
            host.Flush((key, text) =>
            {
                sinceFlush.Restart();
                PostOutput(key, text);
            });
        }

        private void PostOutput(PaneKey key, string text)
        {
            Post(new { type = "output", sessionId = key.SessionId, pane = Panes.Name(key.Pane), data = text });
        }

        private static PaneKey GetPane(JsonElement message)
        {
            return new PaneKey(GetGuid(message, "sessionId"), Panes.TryParseAny(GetOptionalString(message, "pane"), out PaneRole pane) ? pane : PaneRole.Main);
        }

        /// <summary>A key pressed over the quick panel, run here as this window's own would be.</summary>
        internal void RunQuickAction(string name)
        {
            Post(new { type = "quickAction", name });
        }

        internal void PostShortcuts(object message) => Post(message);

        private void Post(object message)
        {
            webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }

        /// <summary>Writes the window's state now; false when the write failed, which the next change or the exit retries.</summary>
        private bool SaveNow(bool preservePending = false)
        {
            saveTimer.Stop();
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            StateSnapshot snapshot = (preservePending ? model.LiveFailureSnapshot() : model.ToSnapshot()) with
            {
                Window = new WindowPlacement
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Maximized = WindowState == WindowState.Maximized,
                },
                Sidebar = sidebar,
            };

            try
            {
                store.Save(snapshot);
                Library.RetryUnsavedIndex();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notice("AppStore", "save failed: " + ex.Message);
                // Next structural change or exit retries the save.
                return false;
            }
        }

        private void ApplyPlacement(WindowPlacement? placement)
        {
            if (placement is null || placement.Width < 200 || placement.Height < 150)
            {
                return;
            }

            // agterm's constrainedRestoredFrame: onto the display the saved frame overlaps most, else the primary, kept
            // whole there. Before the window has a handle its units are the system's.
            double scale = Displays.SystemScale();
            List<Rect> areas = Displays.All().Select(display => new Rect(display.WorkArea.X / scale, display.WorkArea.Y / scale, display.WorkArea.Width / scale, display.WorkArea.Height / scale)).ToList();
            if (areas.Count == 0)
            {
                return;
            }

            Rect saved = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
            Rect frame = Displays.Constrain(saved, new Size(Math.Max(MinWidth, 200), Math.Max(MinHeight, 150)), areas[Displays.BestFor(saved, areas) ?? 0]);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = frame.Left;
            Top = frame.Top;
            Width = frame.Width;
            Height = frame.Height;
            if (placement.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            TerminalInputSource.Shared.Forget(new WindowInteropHelper(this).Handle);
            switcherEscape.Hold(false);
            ClipboardPrompts.Shared.Drop(this);
            // A closed window's panes leave the launch queue, or one of them at its head would hold up every other.
            Library.Pacer.Granted -= OnPacerGranted;
            foreach (Session session in model.AllSessions)
            {
                Library.Pacer.Discard(new PaneKey(session.Id, PaneRole.Main));
                Library.Pacer.Discard(new PaneKey(session.Id, PaneRole.Split));
            }
            StopRemoteRows();
            flushTimer.Stop();
            model.Seen -= ClearDeliveredBanners;
            Library.SettingsSaved -= OnSettingsSaved;
            Library.Appearance.AppearanceChanged -= OnSystemAppearanceChanged;
            Library.Appearance.AccentChanged -= PostState;
            Library.WindowsChanged -= OnWindowsChanged;
            Library.Recent.Changed -= OnRecentChanged;
            host.Dispose();
        }

        private static WindowLibrary Library => WindowLibrary.Current;

        /// <summary>What agshellctl asks of this window.</summary>
        internal Task<ControlResponse> HandleAsync(ControlRequest request)
        {
            return dispatcher.HandleAsync(request);
        }

        private void OnWindowsChanged()
        {
            SyncWindowTitle();
            PostState();
        }

        internal bool Owns(Guid sessionId)
        {
            return model.FindSession(sessionId) is not null;
        }

        private static Guid GetGuid(JsonElement message, string name)
        {
            return message.GetProperty(name).GetGuid();
        }

        private static List<Guid> GetGuids(JsonElement message, string name)
        {
            return message.GetProperty(name).EnumerateArray().Select(item => item.GetGuid()).ToList();
        }

        private static Guid? GetOptionalGuid(JsonElement message, string name)
        {
            return message.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetGuid() : null;
        }

        private static bool? GetOptionalBool(JsonElement message, string name)
        {
            return message.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        }

        private static string? GetOptionalString(JsonElement message, string name)
        {
            return message.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
    }
}
