using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// One window as agterm keeps it: a bundle of workspaces with its own state file, which can be closed and opened again.
    /// The first window keeps the state.json agshell always had; later ones live under windows\.
    /// </summary>
    internal sealed class WindowEntry
    {
        [JsonRequired]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>agterm's name: the one the user gave, or "window N" given at creation; null only in an older index.</summary>
        public string? Name { get; set; }

        /// <summary>agterm's hasCustomName: a name that is not "window N".</summary>
        public string? CustomName => Name is string name && !IsAutoName(name) ? name : null;

        /// <summary>agterm's isAutoName: "window" and a number from 1, nothing else.</summary>
        public static bool IsAutoName(string name)
        {
            string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[0] == "window" && int.TryParse(parts[1], out int number) && number >= 1;
        }

        public bool Open { get; set; } = true;

        /// <summary>The state file, relative to the state directory.</summary>
        [JsonRequired]
        public string StateFile { get; set; } = "state.json";

        /// <summary>
        /// agterm's recoverOrphanedWindows: the window was rebuilt from its file because windows.json could not be read, so
        /// the launch replays none of its captures. Not saved.
        /// </summary>
        [JsonIgnore]
        public bool Recovered { get; set; }
    }

    /// <summary>agterm's WindowsIndex: windows.json, with a version that, when it differs, makes the index count as absent.</summary>
    internal sealed class WindowsIndex
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; }

        public List<WindowEntry> Windows { get; set; } = new List<WindowEntry>();

        /// <summary>agterm's frontmost: the window in front, which the next launch puts in front again.</summary>
        public Guid? Frontmost { get; set; }
    }

    /// <summary>
    /// agterm's window library: the windows and what they share — the settings, the control pipe, the global hotkey and the
    /// live-session host. A request goes to the window it names, else to the one holding its target, else the front one.
    /// </summary>
    internal sealed partial class WindowLibrary
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private List<WindowEntry> entries;
        private readonly string indexPath;

        /// <summary>The open windows, the most recently active first.</summary>
        private readonly List<MainWindow> open = new List<MainWindow>();
        private ControlServer? server;
        private readonly WindowCloseResponse closeResponse = new WindowCloseResponse();
        private readonly ModalCloseDrain modalCloseDrain = new ModalCloseDrain();
        private DispatcherTimer? modalCloseTimer;
        private GlobalHotkey? hotkey;

        public static WindowLibrary Current { get; private set; } = null!;

        private WindowLibrary(string dataDirectory)
        {
            DataDirectory = dataDirectory;
            PipeName = ControlEndpoint.PipeName(dataDirectory);
            // Read before anything is written: the first launch saves its session within a second.
            HadState = FirstRunWelcome.HasPriorState(dataDirectory);
            SettingsStore = new SettingsStore(Path.Combine(dataDirectory, "settings.json"));
            Settings = SettingsStore.Load();
            MoveConfigFromStateDirectory();
            indexPath = Path.Combine(dataDirectory, "windows.json");
            entries = LoadEntries();
            recentStore = new RecentClosedStore(Path.Combine(dataDirectory, "recent-closed.json"));
            Recent.Load(recentStore.Load());
            Recent.Changed += SaveRecent;
            usageStore = new CustomCommandUsageStore(Path.Combine(dataDirectory, "custom-command-usage.json"));
            CommandUsage = usageStore.Load();
        }

        private readonly RecentClosedStore recentStore;
        private readonly CustomCommandUsageStore usageStore;

        /// <summary>agterm's custom command usage, one for the app: runs from every window count.</summary>
        public CustomCommandUsage CommandUsage { get; }

        /// <summary>A keymap command started: counted and saved, and every window's list of most-used commands follows.</summary>
        public void RecordCommandRun(string name)
        {
            CommandUsage.Record(name);
            usageStore.Save(CommandUsage);
            RaiseWindowsChanged();
        }

        /// <summary>agterm's recently closed list, one for the app: every window adds to it and reopens from it.</summary>
        public RecentClosedList Recent { get; } = new RecentClosedList();

        /// <summary>agterm's AskRegistry.shared: the finished questions of every window.</summary>
        public AskResultLog AskResults { get; } = new AskResultLog();

        /// <summary>
        /// Writes the list on every change. As agterm's, whose list is always the file: a write that failed leaves the file as
        /// it was, so the list goes back to it before the windows show it.
        /// </summary>
        private void SaveRecent()
        {
            if (!recentStore.Save(Recent.Items))
            {
                Recent.Load(recentStore.Load());
            }
        }

        public string DataDirectory { get; }

        /// <summary>
        /// The browser data every page shares. A separate state directory (a test instance) gets its own, so it never shares
        /// the main app's browser.
        /// </summary>
        public string WebViewDataFolder => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ControlEndpoint.StateDirVariable))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agshell", "WebView2")
            : Path.Combine(DataDirectory, "WebView2");

        /// <summary>agterm's event stream: one for the app, every window's events in it, marked with their window.</summary>
        public ControlEventRing Events { get; } = new ControlEventRing();

        /// <summary>agterm's hook scheduler, one for the app, fed every window's events from the ring.</summary>
        public HookRunner Hooks { get; private set; } = null!;

        /// <summary>What a hook or custom command starts with: agshellctl on PATH and this app's pipe.</summary>
        public Dictionary<string, string?> BaseEnvironment()
        {
            return new Dictionary<string, string?>
            {
                ["PATH"] = SearchPath.Current(),
                [ControlEndpoint.PipeVariable] = PipeName,
                // A live shell's daemon names itself to its shell; one inherited from where AgShell started names another.
                [ControlEndpoint.LivePipeVariable] = null,
            };
        }

        /// <summary>Other machines whose live sessions this app has attached sessions of.</summary>
        public RemoteHosts Remotes { get; } = new RemoteHosts();

        /// <summary>agterm's quick terminal, one for the app.</summary>
        public QuickPanel Quick { get; private set; } = null!;

        /// <summary>
        /// agterm's takeFrontmost: the window untargeted commands go to, without bringing it forward; Windows tells the
        /// library only of a window activated, not of one that stays behind another minimized.
        /// </summary>
        public void TakeFrontmost(MainWindow window)
        {
            if (open.Remove(window))
            {
                open.Insert(0, window);
                WindowsChanged?.Invoke();
                OnFrontmostChanged();
            }
        }

        /// <summary>
        /// agterm's applyInactiveWindowSidebarHiding: with the setting on, the front window shows its sidebar and every other
        /// open one folds it, saved and seen in window list; a window already so is left alone.
        /// </summary>
        public void ApplyInactiveWindowSidebarHiding()
        {
            if (Active is not MainWindow front)
            {
                return;
            }

            foreach (MainWindow window in open.ToList())
            {
                window.SetSidebarVisible(window == front);
            }
        }

        /// <summary>agterm's takeFrontmost: the front window changed; the index notes it, and auto-hide follows it.</summary>
        private void OnFrontmostChanged()
        {
            frontmostId = Active?.Entry.Id ?? frontmostId;
            SaveEntries();
            if (Settings.AutoHideSidebarInactiveWindows == true)
            {
                ApplyInactiveWindowSidebarHiding();
            }
        }

        /// <summary>
        /// agterm's handOffFrontmost: after the front window is minimized, the first open window still on screen takes the
        /// front; with every window minimized it stays.
        /// </summary>
        public void HandOffFrontmost(MainWindow from)
        {
            if (Active == from && entries.Select(entry => open.FirstOrDefault(window => window.Entry == entry)).OfType<MainWindow>()
                    .FirstOrDefault(window => window != from && window.WindowState != WindowState.Minimized) is MainWindow next)
            {
                TakeFrontmost(next);
            }
        }

        private bool launchAnnounced;

        /// <summary>True once, for the window that announces the app's launch.</summary>
        public bool TakeLaunch()
        {
            bool first = !launchAnnounced;
            launchAnnounced = true;
            return first;
        }

        /// <summary>agterm's Settings window, one for the app.</summary>
        public SettingsWindow SettingsWindow { get; private set; } = null!;

        /// <summary>AgShell's own themes folder sits in the config folder, beside ghostty.conf, as Ghostty keeps it.</summary>
        public ThemeCatalog ThemeCatalog => new ThemeCatalog(Path.Combine(ConfigFolder, ConfigPaths.ThemesFolder), Path.Combine(AppContext.BaseDirectory, "themes"));

        /// <summary>The Windows mode, Transparency effects and accent, watched once for every window.</summary>
        public SystemAppearance Appearance { get; } = new SystemAppearance();

        /// <summary>The app is quitting; the quick panel stops refusing to close.</summary>
        public bool ShuttingDown { get; private set; }

        public string PipeName { get; }

        /// <summary>Whether agshell ran in this state directory before, for the first-launch welcome.</summary>
        public bool HadState { get; }

        public SettingsStore SettingsStore { get; }

        /// <summary>The settings every window shares; save them through <see cref="SaveSettings"/>.</summary>
        public AppSettings Settings { get; }

        /// <summary>The pipe agshellctl reaches, or null when another process already serves it.</summary>
        public string? ServedPipe => server is null ? null : PipeName;

        public LiveDaemons? Live { get; private set; }

        public IReadOnlyList<WindowEntry> Entries => entries;

        public IReadOnlyList<MainWindow> OpenWindows => open;

        /// <summary>The saved browser store of --persistent pages is being emptied.</summary>
        public bool ClearingBrowserData { get; set; }

        /// <summary>The window in front: the last one activated.</summary>
        public MainWindow? Active => open.FirstOrDefault();

        /// <summary>Settings were saved; every window applies them.</summary>
        public event Action? SettingsSaved;

        /// <summary>A window opened, closed, came to the front, or was renamed or deleted; every window shows the list.</summary>
        public event Action? WindowsChanged;

        public void RaiseWindowsChanged()
        {
            WindowsChanged?.Invoke();
        }

        /// <summary>
        /// agterm's drainPendingOpenDirectories: each directory the app was opened with becomes a new session in the last
        /// active window, dropped only once its session landed; until a window can take one, it tries again every 0.1 s, at
        /// most 50 times. The window that got sessions comes forward.
        /// </summary>
        public void OpenDirectories(IReadOnlyList<string> directories)
        {
            pendingOpen.AddRange(directories);
            DrainOpen(0);
        }

        private readonly List<string> pendingOpen = new List<string>();

        private void DrainOpen(int retry)
        {
            if (pendingOpen.Count == 0)
            {
                return;
            }

            if (Active is not MainWindow window)
            {
                RetryOpen(retry, giveUp: true);
                return;
            }

            // A window still starting WebView2 can take longer than the retries last: it is waited for instead.
            if (!window.SessionsReady)
            {
                void OnReady()
                {
                    window.Ready -= OnReady;
                    DrainOpen(retry);
                }

                window.Ready += OnReady;
                return;
            }

            int before = pendingOpen.Count;
            while (pendingOpen.Count > 0 && window.OpenSessionAt(pendingOpen[0]))
            {
                pendingOpen.RemoveAt(0);
            }

            if (pendingOpen.Count < before)
            {
                window.Raise();
            }

            if (pendingOpen.Count > 0)
            {
                RetryOpen(retry, giveUp: false);
            }
        }

        private void RetryOpen(int retry, bool giveUp)
        {
            if (retry >= 50)
            {
                if (giveUp)
                {
                    pendingOpen.Clear();
                }

                return;
            }

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                DrainOpen(retry + 1);
            };
            timer.Start();
        }

        /// <summary>
        /// agterm's notification authorization at start: Windows asks nothing, so a denial is notifications turned off for the
        /// user, the app or by policy.
        /// </summary>
        private static void LogNotificationSetting()
        {
            try
            {
                Windows.UI.Notifications.NotificationSetting setting = CommunityToolkit.WinUI.Notifications.ToastNotificationManagerCompat.CreateToastNotifier().Setting;
                if (setting != Windows.UI.Notifications.NotificationSetting.Enabled)
                {
                    Log.Notice("NotificationManager", "notification authorization denied: " + setting);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                Log.Notice("NotificationManager", "notification authorization denied: " + ex.Message);
            }
        }

        public static void Start(Application application)
        {
            WindowLibrary library = new WindowLibrary(ControlEndpoint.StateDirectory());
            Current = library;
            library.Hooks = new HookRunner(library.BaseEnvironment);
            library.Pacer.Drained += drained => Log.Debug("agshellApp", "launch spawn queue drained in " + drained.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            application.SessionEnding += (sender, args) => library.OnSessionEnding();
            library.Hooks.Failed += (line, reason) => library.NotifyApp("hook-failure:" + line.Kind + ":" + line.Command, Strings.T("Хук не сработал"), "on " + line.Kind + " " + line.Command + " (" + reason + ")");
            library.Events.Appended += library.Hooks.Dispatch;
            CommunityToolkit.WinUI.Notifications.ToastNotificationManagerCompat.OnActivated += activation =>
                application.Dispatcher.BeginInvoke(() => library.OnBannerClicked(CommunityToolkit.WinUI.Notifications.ToastArguments.Parse(activation.Argument)));
            LogNotificationSetting();
            library.Quick = new QuickPanel(library);
            library.Quick.Changed += library.RaiseWindowsChanged;
            library.SettingsSaved += library.Quick.Refresh;
            library.SettingsWindow = new SettingsWindow(library);
            library.SettingsSaved += library.SettingsWindow.Refresh;
            library.SettingsSaved += library.ScheduleBadge;
            library.SettingsSaved += library.ScheduleJumpList;
            library.WindowsChanged += library.ScheduleJumpList;
            library.savedLooks = library.Looks();
            library.savedConfigFolder = library.ConfigFolder;
            library.LoadGhostty();
            library.StartLive();
            library.StartServer();
            library.StartHotkey();
            // agterm's reopen: the windows open at the last exit come back, the frontmost one last so it is in front; with
            // none open, the frontmost one (if it still exists), else the first.
            WindowEntry? frontmost = library.entries.FirstOrDefault(entry => entry.Id == library.frontmostId);
            List<WindowEntry> opening = library.entries.Where(entry => entry.Open).ToList();
            if (opening.Count == 0)
            {
                opening.Add(frontmost ?? library.entries[0]);
            }

            foreach (WindowEntry entry in opening.OrderBy(entry => entry == frontmost))
            {
                library.Show(entry, launchRestore: true);
            }

            library.WatchRemotes();
            library.SettingsSaved += library.WatchRemotes;
            library.NotifyLaunchDiagnostics();
            // agterm applies the auto-hide once when the setting is turned on.
            bool autoHide = library.Settings.AutoHideSidebarInactiveWindows == true;
            library.SettingsSaved += () =>
            {
                bool now = library.Settings.AutoHideSidebarInactiveWindows == true;
                if (now && !autoHide)
                {
                    library.ApplyInactiveWindowSidebarHiding();
                }

                autoHide = now;
            };
            TerminalInputSource.Shared.SetTarget(library.Settings.TerminalInputSource);
            library.SettingsSaved += () => TerminalInputSource.Shared.SetTarget(library.Settings.TerminalInputSource);
        }

        /// <summary>The group of the app's own banners: diagnostics and failures, each in a slot of its own.</summary>
        private const string AppBanners = "agshell";

        /// <summary>
        /// One of the app's own banners, agterm's: a fixed slot per kind (a hook line, a command, a file), no flash and, as agterm
        /// gives them no sound, silent. agterm's NotificationManager is the app's, so a banner needs no window.
        /// </summary>
        internal void NotifyApp(string slot, string title, string body)
        {
            if (Settings.NotificationsEnabled != false)
            {
                ShowBanner(slot, AppBanners, title, body, null, slot.Split(':')[0] + " banner add failed", silent: true);
            }
        }

        /// <summary>A banner Windows does not take is logged under <paramref name="failure"/>, as agterm logs a refused request.</summary>
        internal void ShowBanner(string tag, string group, string title, string body, CommunityToolkit.WinUI.Notifications.ToastArguments? arguments, string failure, bool silent = false)
        {
            CommunityToolkit.WinUI.Notifications.ToastContentBuilder toast = new CommunityToolkit.WinUI.Notifications.ToastContentBuilder().AddText(title).AddText(body);
            foreach ((string key, string value) in arguments ?? new CommunityToolkit.WinUI.Notifications.ToastArguments())
            {
                toast.AddArgument(key, value);
            }

            string sound = Settings.EffectiveNotificationSound;
            if (silent || sound != "default")
            {
                toast.AddAudio(silent ? null : MainWindow.ToastSound(sound), silent: silent || sound == "none");
            }

            try
            {
                toast.Show(shown =>
                {
                    shown.Tag = BannerKey(tag);
                    shown.Group = BannerKey(group);
                });
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                Log.Error("NotificationManager", failure + ": " + ex.Message);
            }
        }

        /// <summary>Windows takes a tag or group of at most 64 characters; a longer one keeps its start and a hash of the whole.</summary>
        internal static string BannerKey(string key)
        {
            return key.Length <= 64 ? key : key[..55] + "#" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..8];
        }

        /// <summary>Whether one of the app's windows has the focus: agterm's NSApp.isActive.</summary>
        internal static bool AppActive => Application.Current?.Windows.OfType<Window>().Any(window => window.IsActive) == true;

        /// <summary>NSApp.activate: the front window comes forward as it was.</summary>
        private void ActivateApp()
        {
            if (Active is MainWindow front)
            {
                front.Deminiaturize();
                front.Activate();
            }
        }

        /// <summary>
        /// agterm's banner click: the window holding the session reveals it at the pane that fired; a window closed since
        /// opens first, as agterm reopens it.
        /// </summary>
        private void OnBannerClicked(CommunityToolkit.WinUI.Notifications.ToastArguments arguments)
        {
            // agterm activates the app on every click: an app banner, or one whose session is gone, just brings it forward.
            if (!arguments.TryGetValue("session", out string? sessionText) || !Guid.TryParse(sessionText, out Guid sessionId))
            {
                ActivateApp();
                return;
            }

            arguments.TryGetValue("pane", out string? pane);
            MainWindow? holder = open.FirstOrDefault(window => window.Owns(sessionId));
            if (holder is null)
            {
                if (!arguments.TryGetValue("window", out string? windowText) || entries.FirstOrDefault(entry => string.Equals(entry.Id.Wire(), windowText, StringComparison.OrdinalIgnoreCase)) is not WindowEntry entry || entry.Open)
                {
                    ActivateApp();
                    return;
                }

                holder = Show(entry);
            }

            MainWindow target = holder;
            target.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => target.RevealFromBanner(sessionId, pane));
        }

        /// <param name="launchRestore">
        /// agterm's launchRestore: the app's launch opens the window, and only then does it run what its panes ran at the
        /// last quit; a window opened later during the run comes back as plain shells.
        /// </param>
        public MainWindow Show(WindowEntry entry, bool launchRestore = false)
        {
            MainWindow window = new MainWindow(entry, launchRestore);
            window.Events.Appended += controlEvent => Events.Append(controlEvent, controlEvent.Kind == ControlEventKinds.AppLaunched ? null : entry.Id.Wire());
            if (!launchRestore)
            {
                window.AnnounceReopened();
            }

            open.Add(window);
            entry.Open = true;
            // agterm marks a window it opens frontmost at once, so untargeted commands reach it before it is activated.
            frontmostId = entry.Id;
            SaveEntries();
            window.Activated += (sender, args) =>
            {
                bool changed = open.FirstOrDefault() != window;
                open.Remove(window);
                open.Insert(0, window);
                WindowsChanged?.Invoke();
                if (changed)
                {
                    OnFrontmostChanged();
                }

                TerminalInputSource.Shared.RequestRefresh();
            };
            window.Deactivated += (sender, args) =>
            {
                WindowsChanged?.Invoke();
                TerminalInputSource.Shared.RequestRefresh();
            };
            window.Closed += (sender, args) => OnWindowClosed(window, entry);
            window.Model.Changed += ScheduleBadge;
            window.Model.Changed += ScheduleJumpList;
            window.Show();
            ApplyBadge();
            return window;
        }

        private void OnWindowClosed(MainWindow window, WindowEntry entry)
        {
            open.Remove(window);
            window.Model.Changed -= ScheduleBadge;
            window.Model.Changed -= ScheduleJumpList;
            ScheduleBadge();
            // agterm's isTerminating: Windows ending the session closes every window, and they all stay marked open.
            if (Terminating)
            {
                if (open.Count == 0)
                {
                    Shutdown();
                }

                return;
            }

            // The app quits with its last window, as agterm does, and that window opens again next time, in front.
            if (open.Count == 0)
            {
                frontmostId = entry.Id;
                SaveEntries();
                if (!closeResponse.DeferShutdown(ShutdownAfterLastWindow))
                {
                    ShutdownAfterLastWindow();
                }
                return;
            }

            if (frontmostId == entry.Id)
            {
                frontmostId = Active?.Entry.Id;
            }

            entry.Open = false;
            SaveEntries();
        }

        /// <summary>agterm's frontmostWindowID, as windows.json keeps it.</summary>
        private Guid? frontmostId;

        /// <summary>agterm's isTerminating: Windows is ending the session, and the app goes with it.</summary>
        public bool Terminating { get; private set; }

        /// <summary>
        /// agterm's applicationWillTerminate for a Windows logoff or shutdown: every window notes its programs and saves now,
        /// while the shells live; the closes that follow neither ask, capture again, nor mark the windows closed.
        /// </summary>
        private void OnSessionEnding()
        {
            if (Terminating)
            {
                return;
            }

            Terminating = true;
            bool saved = true;
            foreach (MainWindow window in open.ToList())
            {
                saved &= window.SaveForQuit();
            }

            frontmostId = Active?.Entry.Id ?? frontmostId;
            SaveEntries();
            // agterm's exitFlush: a confirmed live reset arms only when every window and the index saved.
            if (ArmableLiveReset is LiveReset.Selection pending)
            {
                if (saved && !IndexUnsaved)
                {
                    ArmLiveReset(pending);
                }
                else
                {
                    Log.Error("AppDelegate", "live sessions reset not armed: window state or the index did not save");
                }
            }
        }

        private bool quitPromptOpen;

        /// <summary>
        /// agterm's ⌘Q (applicationShouldTerminate): asks over the window it came from how many windows and sessions go, then
        /// quits the way a Windows logoff does, so every window stays marked open and the next launch opens them all.
        /// </summary>
        public void Quit(MainWindow from)
        {
            if (Terminating)
            {
                return;
            }

            // agterm skips the question for a confirmed live reset, which quits to reopen.
            if (open.Count == 0 || ArmableLiveReset is not null)
            {
                QuitNow();
                return;
            }

            if (quitPromptOpen)
            {
                return;
            }

            quitPromptOpen = true;
            bool live = RestoreStatus().Active == CommandRestore.Live;
            string text = QuitPrompt.Message(open.Count, open.Sum(window => window.SessionCount), live);
            WindowClosePrompt.Show(from, Strings.T("Выйти из AgShell?"), text, quit =>
            {
                quitPromptOpen = false;
                if (quit && !Terminating)
                {
                    QuitNow();
                }
            }, Strings.T("Выйти"));
        }

        private void QuitNow()
        {
            OnSessionEnding();
            Application.Current.Shutdown();
        }

        /// <summary>agterm's retryUnsavedIndex: a window's save landed, so a windows.json write that failed is tried again.</summary>
        public void RetryUnsavedIndex()
        {
            if (IndexUnsaved)
            {
                SaveEntries();
            }
        }

        private readonly List<FileSystemWatcher> remotesWatchers = new List<FileSystemWatcher>();
        private bool remotesFlagged;

        /// <summary>agterm's RemotesWatcher entries: the machines remotes.conf named when it was last read.</summary>
        internal IReadOnlyList<RemoteMachine> RemoteMachines { get; private set; } = new List<RemoteMachine>();

        /// <summary>agterm's config directory: the chosen folder, else <see cref="DefaultConfigFolder"/>.</summary>
        public string ConfigFolder => ConfigPaths.ConfigDirectory(Settings.ConfigDirectory, ConfigStateDirectory);

        /// <summary>Where the config files live without a chosen folder: config in agterm's configStateDirectory.</summary>
        public string DefaultConfigFolder => ConfigPaths.ConfigDirectory(null, ConfigStateDirectory);

        private string ConfigStateDirectory => DebugStateDirectory.ConfigDirectory(DataDirectory, ControlEndpoint.StandardStateDirectory());

        /// <summary>
        /// Before the config got its own folder it lived in the state directory; that launch moves it once. The agshell-debug
        /// sibling leaves the standard directory to the installed app, which moves its own.
        /// </summary>
        private void MoveConfigFromStateDirectory()
        {
            if (SettingsStore.WriteBlocked)
            {
                return;
            }

            if (!string.Equals(Path.GetFullPath(ConfigStateDirectory), Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                List<string> moved = ConfigPaths.MoveFromStateDirectory(DataDirectory, DefaultConfigFolder, ConfigFolder);
                if (moved.Count > 0)
                {
                    Log.Notice("SettingsModel", "moved " + string.Join(", ", moved) + " from the state directory to the config directory");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Notice("SettingsModel", "could not move the config files from the state directory: " + ex.Message);
            }
        }

        /// <summary>
        /// agterm's RemotesWatcher: remotes.conf is read at launch and when the config folder changes, without a report, and
        /// again on every change of the file, its folder or a link's target; lines it cannot use are reported once each time
        /// the file goes from clean to having them.
        /// </summary>
        private void WatchRemotes()
        {
            CheckRemotes(reporting: false);
        }

        /// <summary>
        /// Re-armed on every event, as agterm's: an editor that saves by replacing the file, a link created or pointed
        /// elsewhere later, a folder created later (seen only from the nearest folder that exists).
        /// </summary>
        private void ArmRemotes()
        {
            remotesWatchers.ForEach(watcher => watcher.Dispose());
            remotesWatchers.Clear();
            string path = Path.Combine(ConfigFolder, "remotes.conf");
            Watch(path);
            try
            {
                if (new FileInfo(path).LinkTarget is string target)
                {
                    Watch(Path.GetFullPath(target, ConfigFolder));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>The file in its folder, or, while that folder is missing, the nearest one that exists for the folders under it.</summary>
        private void Watch(string path)
        {
            string? folder = Path.GetDirectoryName(path);
            string filter = Path.GetFileName(path);
            while (folder is not null && !Directory.Exists(folder))
            {
                filter = "*";
                folder = Path.GetDirectoryName(folder);
            }

            if (folder is null || remotesWatchers.Any(watcher => string.Equals(watcher.Path, folder, StringComparison.OrdinalIgnoreCase) && watcher.Filter == filter))
            {
                return;
            }

            FileSystemWatcher watcher = new FileSystemWatcher(folder, filter) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size };
            // The watcher's thread can still fire while the app shuts down, when there is no application to hand it to.
            FileSystemEventHandler changed = (sender, args) => Application.Current?.Dispatcher.BeginInvoke(() => CheckRemotes(reporting: true));
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (sender, args) => Application.Current?.Dispatcher.BeginInvoke(() => CheckRemotes(reporting: true));
            watcher.EnableRaisingEvents = true;
            remotesWatchers.Add(watcher);
        }

        private int remotesIssueCount;

        /// <summary>agterm's remotes banner text: the count only.</summary>
        private static string RemotesIssues(int count)
        {
            return count == 1 ? Strings.T("1 проблема в remotes.conf") : Strings.T("Проблем в remotes.conf: {0}", count);
        }

        /// <summary>
        /// agterm's config banner: the count and where to look, as its log is the system's (Console there, the event log here),
        /// without blaming a file.
        /// </summary>
        private void NotifyConfigDiagnostics(int count)
        {
            NotifyApp("config-diagnostics", Strings.T("Конфиг"), count == 1
                ? Strings.T("1 проблема в конфиге ghostty — см. Просмотр событий, затем «Перечитать ghostty.conf»")
                : Strings.T("Проблем в конфиге ghostty: {0} — см. Просмотр событий, затем «Перечитать ghostty.conf»", count));
        }

        /// <summary>
        /// agterm's launch banners, once after the windows come back: the keymap's and hooks.conf's problems, remotes.conf's,
        /// ghostty.conf's, then the live reset's outcome.
        /// </summary>
        private void NotifyLaunchDiagnostics()
        {
            Active?.NotifyLaunchDiagnostics();
            if (remotesIssueCount > 0)
            {
                NotifyApp("remotes-diagnostics", Strings.T("Удалённые машины"), RemotesIssues(remotesIssueCount));
            }

            if (Ghostty.Diagnostics.Count > 0)
            {
                NotifyConfigDiagnostics(Ghostty.Diagnostics.Count);
            }

            NotifyLiveResetOutcome();
        }

        private void CheckRemotes(bool reporting)
        {
            List<string> problems = new List<string>();
            RemoteMachines = RemoteHosts.Read(ConfigFolder, problems);
            // agterm reports a file that goes from clean to broken while it runs; one already broken at launch or in a newly
            // chosen config folder is not news.
            if (reporting && problems.Count > 0 && !remotesFlagged)
            {
                NotifyApp("remotes-diagnostics", Strings.T("Удалённые машины"), RemotesIssues(problems.Count));
            }

            remotesFlagged = problems.Count > 0;
            remotesIssueCount = problems.Count;
            ArmRemotes();
            WindowsChanged?.Invoke();
        }

        /// <summary>agterm's SpawnPacer for the whole app: panes that replay a program at launch start one every 120 ms.</summary>
        public SpawnPacer<PaneKey> Pacer { get; } = new SpawnPacer<PaneKey>(TimeSpan.FromMilliseconds(120), () => DateTime.UtcNow, (delay, run) =>
        {
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                run();
            };
            timer.Start();
        });

        /// <summary>The agterm-scoped ghostty.conf as last read: read at launch and on an explicit reload, as agterm's.</summary>
        public GhosttyConfig Ghostty { get; private set; } = new GhosttyConfig();

        public TerminalAppearance TerminalAppearance => Ghostty.Appearance.Resolve(Settings);

        internal string GhosttyConfigPath => Path.Combine(ConfigFolder, GhosttyConfig.FileName);

        /// <summary>
        /// agterm: the values the settings write win over ghostty.conf, so the setting "Programs may write to the clipboard",
        /// once set, decides; until then the file's clipboard-write, allow by default.
        /// </summary>
        public string ClipboardWritePolicy => Settings.ClipboardWrite is bool set ? (set ? "allow" : "deny") : Ghostty.ClipboardWrite ?? "allow";

        /// <summary>agterm's starter: written once when the file is missing, all comments, so it changes nothing.</summary>
        private void LoadGhostty()
        {
            try
            {
                if (!File.Exists(GhosttyConfigPath))
                {
                    Directory.CreateDirectory(ConfigFolder);
                    File.WriteAllText(GhosttyConfigPath, GhosttyConfig.Starter);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Notice("SettingsModel", "could not write starter ghostty.conf at " + GhosttyConfigPath + ": " + ex.Message);
            }

            // Ghostty reports a theme that does not load among the config's problems, as libghostty does for agterm.
            GhosttyConfig loaded = GhosttyConfig.Load(GhosttyConfigPath);
            string? theme = Themes.ActiveName(Settings, Appearance.Dark, loaded.Theme);
            IReadOnlyList<string> themeDiagnostics = theme is null ? Array.Empty<string>() : ThemeCatalog.Find(theme, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Diagnostics;
            Ghostty = loaded with { Diagnostics = loaded.Diagnostics.Concat(themeDiagnostics).ToList() };
            foreach (string diagnostic in Ghostty.Diagnostics)
            {
                Log.Warning("GhosttyApp", "config: " + diagnostic);
            }
        }

        /// <summary>
        /// agterm's reloadGhosttyConfig: ghostty.conf read again and applied to every terminal, its problems announced in a
        /// banner; the count is what config reload prints.
        /// </summary>
        public int ReloadGhostty()
        {
            LoadGhostty();
            ResetSessionFontSizes();
            SettingsSaved?.Invoke();
            int count = Ghostty.Diagnostics.Count;
            if (count > 0)
            {
                NotifyConfigDiagnostics(count);
            }

            return count;
        }

        public bool SaveSettings()
        {
            if (!SettingsStore.Save(Settings))
            {
                Log.Notice("SettingsModel", "could not save settings at " + SettingsStore.FilePath);
                NotifyApp("settings-save-failed", Strings.T("Настройки"), Strings.T("Не удалось сохранить настройки. Повторите изменение после устранения ошибки записи."));
                return false;
            }

            // agterm: an explicit change of what the settings write into the terminal's config resets every session's
            // Ctrl+/- zoom; a light/dark flip by the system changes no setting, so it keeps them.
            string looks = Looks();
            if (looks != savedLooks)
            {
                ResetSessionFontSizes();
            }

            savedLooks = looks;
            // agterm's setConfigDirectory: a moved config folder brings its own ghostty.conf (the saved event re-watches
            // remotes.conf, and the windows reload keymap.conf and hooks.conf themselves).
            if (!string.Equals(ConfigFolder, savedConfigFolder, StringComparison.OrdinalIgnoreCase))
            {
                savedConfigFolder = ConfigFolder;
                ReloadGhostty();
                return true;
            }

            SettingsSaved?.Invoke();
            return true;
        }

        private string? savedLooks;

        private string? savedConfigFolder;

        private System.Windows.Threading.DispatcherTimer? previewSaveTimer;

        /// <summary>
        /// agterm's previewBackgroundOpacity: a slider drag applies in every window without saving; the write follows 0.3 s
        /// after the last tick, or at once on release (<see cref="CommitPreviewedSettings"/>).
        /// </summary>
        public void PreviewSettings()
        {
            SettingsSaved?.Invoke();
            if (previewSaveTimer is null)
            {
                previewSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                previewSaveTimer.Tick += (sender, args) => CommitPreviewedSettings();
            }

            previewSaveTimer.Stop();
            previewSaveTimer.Start();
        }

        public void CommitPreviewedSettings()
        {
            if (previewSaveTimer?.IsEnabled == true)
            {
                previewSaveTimer.Stop();
                SaveSettings();
            }
        }

        /// <summary>agterm's ghosttyConfigLines: what a change must alter to reset the sessions' zoom.</summary>
        private string Looks()
        {
            string? light = string.IsNullOrEmpty(Settings.Theme) ? null : Settings.Theme;
            string? dark = string.IsNullOrEmpty(Settings.DarkTheme) ? null : Settings.DarkTheme;
            string? theme = Settings.FollowSystemAppearance == true && light is not null && dark is not null ? "light:" + light + ",dark:" + dark : light ?? dark;
            string? cursorStyle = Settings.CursorStyle is string style && AppSettings.CursorStyles.Contains(style) ? style : null;
            return string.Join("\n", string.IsNullOrEmpty(Settings.FontFamily) ? null : Settings.FontFamily, Settings.FontSize, theme, cursorStyle, Settings.CursorBlink,
                Settings.BackgroundOpacity < 1, Settings.EffectiveMouseScrollMultiplier, Settings.RightClickPaste ?? true);
        }

        /// <summary>
        /// agterm's resetSessionFontSizesAllWindows: open windows at once, closed ones in their saved state, so one opened
        /// later does not bring back a size the new default replaced.
        /// </summary>
        private void ResetSessionFontSizes()
        {
            foreach (MainWindow window in open)
            {
                window.ResetFontSizes();
            }

            foreach (WindowEntry entry in entries.Where(entry => open.All(window => window.Entry != entry)))
            {
                StateStore store = new StateStore(Path.Combine(DataDirectory, entry.StateFile));
                if (store.Load() is StateSnapshot snapshot && snapshot.Workspaces.Any(workspace => workspace.Sessions.Any(session => session.FontSize is not null || session.SplitFontSize is not null)))
                {
                    store.Save(snapshot with
                    {
                        Workspaces = snapshot.Workspaces.Select(workspace => workspace with { Sessions = workspace.Sessions.Select(session => session with { FontSize = null, SplitFontSize = null }).ToList() }).ToList(),
                    });
                }
            }
        }

        /// <summary>The restore mode this launch asked for, read once at launch, as in agterm.</summary>
        private string launchRestoreMode = CommandRestore.None;

        /// <summary>
        /// agterm's restore status: configured now, asked at launch, got at launch (live that could not run is none), and why
        /// live is unusable, checked at launch in any mode.
        /// </summary>
        public ControlRestoreStatus RestoreStatus()
        {
            bool fellBack = launchRestoreMode == CommandRestore.Live && Live is null;
            return new ControlRestoreStatus
            {
                Configured = Settings.EffectiveRestoreMode,
                RequestedAtLaunch = launchRestoreMode,
                Active = fellBack ? CommandRestore.None : launchRestoreMode,
                RestartRequired = Settings.EffectiveRestoreMode != launchRestoreMode,
                UnavailableReason = liveUnavailableReason,
            };
        }

        /// <summary>The restore mode this launch asked for; a mode chosen later applies from the next launch, as in agterm.</summary>
        public string LaunchRestoreMode => launchRestoreMode;

        /// <summary>agterm's restoreDenylist: restore-denylist.conf as read at launch, seeded with its header the first time.</summary>
        public IReadOnlySet<string> RestoreDenylist { get; private set; } = new HashSet<string>();

        public string DenylistPath => ConfigPaths.RestoreDenylistPath(ConfigFolder);

        /// <summary>The denylist file, created with its header when missing; read anew by each capture and restart.</summary>
        public HashSet<string> ReadDenylist()
        {
            try
            {
                if (!File.Exists(DenylistPath))
                {
                    Directory.CreateDirectory(ConfigFolder);
                    File.WriteAllText(DenylistPath, CommandRestore.DenylistStarter + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notice("SettingsModel", "could not write starter restore-denylist at " + DenylistPath + ": " + ex.Message);
            }

            try
            {
                using FileStream stream = new FileStream(DenylistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new StreamReader(stream);
                return CommandRestore.ParseDenylist(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void StartLive()
        {
            launchRestoreMode = Settings.EffectiveRestoreMode;
            RestoreDenylist = ReadDenylist();
            // agterm checks the live configuration at every launch, whatever the mode, and keeps why it is unusable.
            liveUnavailableReason = LiveDaemonCopy.Installed ? null : "the agshell-live executable is unavailable";
            // agterm's launch pane inventory, in every mode and before anything reads the claims: it gives the saved panes
            // their ids, so the reset's narrowing and the reap see the same complete walk.
            LiveReset.ClaimWalk claims = LiveClaimWalk(launch: true);
            Task.Run(() => LiveLog.Prune(ControlEndpoint.StateDirectory()));
            if (Settings.EffectiveRestoreMode != CommandRestore.Live)
            {
                ConsumeLiveReset(null, claims);
                ReapLive(new LiveDaemons(), liveRequested: false, claims);
                Task.Run(LiveDaemonCopy.Prune);
                return;
            }

            if (liveUnavailableReason is not null)
            {
                Log.Error("ZmxLaunch", "agshell-live.exe is missing beside the app");
                ConsumeLiveReset(null, claims);
                ReapLive(new LiveDaemons(), liveRequested: true, claims);
                return;
            }

            LiveDaemons daemons = new LiveDaemons();
            Live = daemons;
            liveOutdatedBefore = LiveBuildRecord.LaunchCutoff(LiveDaemonCopy.BundledBuildId, DataDirectory, DateTime.UtcNow);
            // agterm's LaunchOrchestration: a confirmed reset narrows its marker against the saved claims and ends its daemons
            // before any window opens a pane on them.
            ConsumeLiveReset(daemons, claims);
            ReapLive(daemons, liveRequested: true, claims);
            Task.Run(LiveDaemonCopy.Prune);
        }

        /// <summary>agterm's liveUnavailableReason: why this launch could not run live, checked in every mode; null when it can.</summary>
        private string? liveUnavailableReason;

        /// <summary>agterm's liveRestoreUnavailableReason, which Settings shows under the mode while live is chosen.</summary>
        public string? LiveUnavailableReason => liveUnavailableReason;

        private void StartServer()
        {
            ControlServer candidate = new ControlServer(PipeName, request => Application.Current.Dispatcher.InvokeAsync(() => HandleAndCacheAsync(request)).Task.Unwrap(), ServePageAsync, request => CachedResponse(TrimWindow(request)));
            WindowsChanged += RefreshWindowCache;
            candidate.ReplyCompleted += (request, response) =>
            {
                if (request.Cmd == "window.close")
                {
                    Application.Current.Dispatcher.BeginInvoke(() => closeResponse.Complete(request));
                }
            };
            // agterm quits after a confirmed reset only once THIS reply is on the wire, decided from the request and its answer.
            candidate.Written += (request, response) =>
            {
                if (request.Cmd is "ask.result" or "pick.result")
                {
                    Application.Current.Dispatcher.BeginInvoke(() => modalCloseDrain.Written(request, response));
                }

                if (request.Cmd == "live.reset" && response.Ok)
                {
                    Application.Current.Dispatcher.BeginInvoke(TerminateIfLiveResetPending);
                }
            };
            server = candidate.Start() ? candidate : null;
        }


        /// <summary>The keymap's global hotkey lives on a message-only window, so it outlasts any one of the windows.</summary>
        private void StartHotkey()
        {
            HwndSource source = new HwndSource(new HwndSourceParameters("agshell-hotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
            hotkey = new GlobalHotkey(source);
            // The user's summons: it closes again when the keyboard goes elsewhere.
            hotkey.Pressed += () => Quick.Toggle(fromGlobalHotkey: true);
        }

        /// <summary>Registers the chord every window's keymap names; returns why it could not be, or null.</summary>
        public string? RegisterHotkey(KeyChord? chord)
        {
            return hotkey?.Register(chord);
        }

        /// <summary>The window holding a session, open or not loaded: only open windows are searched.</summary>
        public MainWindow? WindowOf(Guid sessionId)
        {
            return open.FirstOrDefault(window => window.Owns(sessionId));
        }

        private StateSnapshot? LoadSaved(string stateFile)
        {
            try
            {
                return new StateStore(Path.Combine(DataDirectory, stateFile)).Load();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The daemons this state directory's panes have, whatever the restore mode, as agterm's zmx client is there in any
        /// mode: those of the live launch, else one for list, prune, kill and screen alone.
        /// </summary>
        internal LiveDaemons Daemons => Live ?? (idleDaemons ??= new LiveDaemons());

        private LiveDaemons? idleDaemons;

        /// <summary>
        /// agterm's zmx list: the daemons joined against the panes that claim them (open windows, their undo windows, closed
        /// and unindexed windows' saved state), the restore status as a header. A listing that failed is an error, not an
        /// empty inventory.
        /// </summary>
        public async Task<(ControlLiveInventory? Inventory, string? Error)> LiveInventoryAsync()
        {
            if (await Daemons.RecordsAsync() is not List<(string PaneId, List<LivePaneInfo>? Shells)> records)
            {
                return (null, "could not read the live session list");
            }

            (List<LiveInventory.Claim> claims, bool walked) = PaneClaims();
            List<LiveInventory.Daemon> observed = records
                .SelectMany(record => record.Shells is null
                    ? new[] { new LiveInventory.Daemon(record.PaneId, null, null, null) }
                    : record.Shells.Select(shell => new LiveInventory.Daemon(shell.Id, shell.Clients, shell.Pid, shell.Created)))
                .ToList();
            (List<ControlLiveEntry> rows, bool complete) = LiveInventory.Join(observed, claims, walked, IsOutdatedLive);
            return (new ControlLiveInventory
            {
                Restore = RestoreStatus(),
                InventoryComplete = complete,
                Endpoint = new ControlLiveEndpoint { Executable = Path.Combine(AppContext.BaseDirectory, "agshell-live.exe"), SocketDirectory = @"\\.\pipe\" + Daemons.PipePrefix },
                LiveReset = LiveResetReadback(),
                Entries = rows,
            }, null);
        }

        /// <summary>
        /// agterm's zmx prune: the orphans of a complete inventory, observed running with no clients, re-listed just before
        /// so one a client took meanwhile is spared, each ended and reported by what its daemon said: only a confirmed kill
        /// counts.
        /// </summary>
        public async Task<(string? Error, string Report, int Affected)> LivePruneAsync()
        {
            (ControlLiveInventory? inventory, string? error) = await LiveInventoryAsync();
            if (inventory is null)
            {
                return (error, string.Empty, 0);
            }

            if (LiveInventory.NamesToPrune(inventory.Entries, inventory.InventoryComplete) is not List<string> candidates)
            {
                return (LiveInventory.IncompleteInventory, string.Empty, 0);
            }

            if (candidates.Count == 0)
            {
                return (null, "no orphan daemons", 0);
            }

            if (await Daemons.RecordsAsync() is not List<(string PaneId, List<LivePaneInfo>? Shells)> recheck)
            {
                return ("could not re-read the live session list before pruning", string.Empty, 0);
            }

            HashSet<string> detached = recheck.SelectMany(record => record.Shells ?? new List<LivePaneInfo>()).Where(shell => shell.Clients == 0).Select(shell => shell.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> names = candidates.Where(detached.Contains).OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (names.Count == 0)
            {
                return (null, "no orphan daemons left to prune", 0);
            }

            string?[] outcomes = await Task.WhenAll(names.Select(Daemons.KillConfirmedAsync));
            string report = string.Join("; ", names.Select((name, index) => outcomes[index] is string reason ? name + ": not killed (" + reason + ")" : "killed " + name));
            return (null, report, outcomes.Count(outcome => outcome is null));
        }

        /// <summary>The window's name, or agterm's "window N" by its place in the list.</summary>
        public string Label(WindowEntry entry)
        {
            return entry.Name ?? "window " + (entries.IndexOf(entry) + 1);
        }

        private void Shutdown()
        {
            ShuttingDown = true;
            modalCloseTimer?.Stop();
            TerminalInputSource.Shared.Stop();
            Quick.Dispose();
            SettingsWindow.Close();
            server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }

        private void ShutdownAfterLastWindow()
        {
            if (open.Count == 0)
            {
                modalCloseTimer?.Stop();
                if (modalCloseDrain.Begin(() =>
                {
                    modalCloseTimer?.Stop();
                    if (open.Count == 0 && !ShuttingDown)
                    {
                        Shutdown();
                        Application.Current.Shutdown();
                    }
                }))
                {
                    modalCloseTimer = new DispatcherTimer { Interval = ModalCloseDrain.GracePeriod };
                    modalCloseTimer.Tick += (sender, args) => modalCloseDrain.Expire();
                    modalCloseTimer.Start();
                }
            }
        }

        internal void TrackClosingModals(MainWindow window)
        {
            if (Terminating || open.Count != 1 || open[0] != window)
            {
                return;
            }

            AppModel model = window.Model;
            if (model.Pick is PickRequest pick)
            {
                modalCloseDrain.Track("pick", pick.Id);
            }

            if (model.GuiAsk is AskRequest gui)
            {
                modalCloseDrain.Track("ask", gui.Id);
            }

            foreach (Session session in model.AllSessions)
            {
                if (model.TerminalAskOf(session.Id) is AskRequest ask)
                {
                    modalCloseDrain.Track("ask", ask.Id);
                }
            }
        }

        /// <summary>
        /// agterm's bootstrap: a valid windows.json; else every window file there is, recovered as open windows; else one
        /// window. An index from before versions were written (a bare list) is read as version 1.
        /// </summary>
        public bool IndexWriteBlocked { get; private set; }

        private List<WindowEntry> LoadEntries()
        {
            IndexWriteBlocked = false;
            try
            {
                string text = File.ReadAllText(indexPath);
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                bool legacy = root.ValueKind == JsonValueKind.Array;
                int? incompatibleVersion = null;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("version", out JsonElement version)
                    && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number)
                    && number != WindowsIndex.CurrentVersion)
                {
                    incompatibleVersion = number;
                }

                if (incompatibleVersion is int unsupported)
                {
                    PreserveIndex("version-" + unsupported);
                }
                else
                {
                    WindowsIndex? index = legacy ? null : JsonSerializer.Deserialize<WindowsIndex>(text, Json);
                    List<WindowEntry>? loaded = legacy ? JsonSerializer.Deserialize<List<WindowEntry>>(text, Json) : index is { Version: WindowsIndex.CurrentVersion } ? index.Windows : null;
                    if (loaded is { Count: > 0 } && loaded.All(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.StateFile)))
                    {
                        frontmostId = index?.Frontmost;
                        for (int entryNumber = 0; entryNumber < loaded.Count; entryNumber++)
                        {
                            loaded[entryNumber].Name ??= "window " + (entryNumber + 1);
                        }

                        return loaded;
                    }

                    PreserveIndex("corrupt");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (JsonException)
            {
                PreserveIndex("corrupt");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                IndexWriteBlocked = true;
                Log.Notice("WindowLibrary", "could not read windows index; writes blocked until restart: " + ex.Message);
            }

            return RecoverWindows() ?? new List<WindowEntry> { new WindowEntry { Name = "window 1" } };
        }

        private void PreserveIndex(string reason)
        {
            try
            {
                PreservedFile.MoveAside(indexPath, reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                IndexWriteBlocked = true;
                Log.Notice("WindowLibrary", "could not preserve windows index; writes blocked until restart: " + ex.Message);
            }
        }

        /// <summary>
        /// agterm's recoverOrphanedWindows: rather than lose the user's sessions with the index, each window file becomes an
        /// open window named "window N" — the first window's state.json first, then windows\&lt;id&gt;.json by file name, so the
        /// numbering is the same every time. Null when there is none.
        /// </summary>
        private List<WindowEntry>? RecoverWindows()
        {
            List<WindowEntry> recovered = new List<WindowEntry>();
            if (File.Exists(Path.Combine(DataDirectory, "state.json")))
            {
                recovered.Add(new WindowEntry { StateFile = "state.json" });
            }

            recovered.AddRange(StrayWindowFiles(new HashSet<string>()).Select(file => new WindowEntry { Id = file.Id, StateFile = file.StateFile }));
            if (recovered.Count == 0)
            {
                return null;
            }

            for (int number = 0; number < recovered.Count; number++)
            {
                recovered[number].Name = "window " + (number + 1);
                recovered[number].Recovered = true;
            }

            entries = recovered;
            SaveEntries();
            return recovered;
        }

        /// <summary>
        /// agterm's strayWindowFileIDs: the windows\&lt;id&gt;.json files no entry names, by file name. Their sessions still own
        /// their live shells, so the inventory counts them, and prune spares them.
        /// </summary>
        private List<(Guid Id, string StateFile)> StrayWindowFiles(HashSet<string> indexed)
        {
            string directory = Path.Combine(DataDirectory, "windows");
            if (!Directory.Exists(directory))
            {
                return new List<(Guid, string)>();
            }

            return Directory.GetFiles(directory, "*.json")
                .Select(path => Path.GetFileName(path))
                .Order(StringComparer.Ordinal)
                .Select(name => (Valid: Guid.TryParse(Path.GetFileNameWithoutExtension(name), out Guid id), Id: id, StateFile: Path.Combine("windows", name)))
                .Where(file => file.Valid && !indexed.Contains(file.StateFile))
                .Select(file => (file.Id, file.StateFile))
                .ToList();
        }

        /// <summary>agterm's indexUnsaved: the last write of windows.json failed.</summary>
        public bool IndexUnsaved { get; private set; }

        public void SaveEntries()
        {
            WindowsChanged?.Invoke();
            if (IndexWriteBlocked)
            {
                IndexUnsaved = true;
                Log.Notice("WindowLibrary", "windows index write refused after an unsuccessful read or backup; restart to retry reading it");
                return;
            }

            try
            {
                Directory.CreateDirectory(DataDirectory);
                string temporary = indexPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new WindowsIndex { Version = WindowsIndex.CurrentVersion, Windows = entries, Frontmost = frontmostId }, Json));
                File.Move(temporary, indexPath, true);
                IndexUnsaved = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Notice("WindowLibrary", "saveIndex failed: " + ex.Message);
                IndexUnsaved = true;
            }
        }
    }
}
