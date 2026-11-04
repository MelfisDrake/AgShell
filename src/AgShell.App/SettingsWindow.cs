using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AgShell.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;

namespace AgShell.App
{
    /// <summary>
    /// agterm's Settings window: one for the app, 540×680 and not resizable, opening on General each time and raised when
    /// asked for again. The page is the windows' own, in its #settings mode; what it shows comes from the window in front,
    /// and what it changes goes to the settings every window shares.
    /// </summary>
    internal sealed class SettingsWindow
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly WindowLibrary library;
        private Window? window;
        private WebView2? webView;
        private bool pageReady;

        public SettingsWindow(WindowLibrary library)
        {
            this.library = library;
        }

        public void Show()
        {
            if (window is not null)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
                return;
            }

            window = Create();
            window.Show();
            window.Activate();
        }

        /// <summary>The settings, the theme or what the files say changed: the page shows them again.</summary>
        public void Refresh()
        {
            Paint();
            PostState();
        }

        public void Close()
        {
            window?.Close();
        }

        private Window Create()
        {
            webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1A, 0x18) };
            Window created = new Window
            {
                Title = Strings.T("Настройки"),
                Width = 540,
                Height = 680,
                ResizeMode = ResizeMode.CanMinimize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1A, 0x18)),
                Content = webView,
            };
            if (library.Active is MainWindow front)
            {
                // Centered over the window in front, as agterm's comes up over the app, without being owned by it.
                created.WindowStartupLocation = WindowStartupLocation.Manual;
                created.Left = front.Left + (front.ActualWidth - created.Width) / 2;
                created.Top = front.Top + (front.ActualHeight - created.Height) / 2;
            }

            created.SourceInitialized += (sender, args) => Paint();
            created.Activated += (sender, args) => library.ReconcileShortcutOwnership();
            created.Deactivated += (sender, args) => library.ShortcutWindowResigned();
            created.Loaded += async (sender, args) => await LoadPageAsync();
            created.Closed += (sender, args) =>
            {
                webView?.Dispose();
                webView = null;
                window = null;
                pageReady = false;
            };
            return created;
        }

        private async Task LoadPageAsync()
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, library.WebViewDataFolder);
            if (webView is null)
            {
                return;
            }

            await webView.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = webView.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            string? devUrl = Environment.GetEnvironmentVariable("AGSHELL_DEV_URL");
            if (!string.IsNullOrEmpty(devUrl))
            {
                core.Navigate(devUrl + "#settings");
            }
            else
            {
                core.Settings.AreDevToolsEnabled = false;
                core.SetVirtualHostNameToFolderMapping(MainWindow.VirtualHostName, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
                core.Navigate("https://" + MainWindow.VirtualHostName + "/index.html#settings");
            }
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
                case "command":
                    OnCommand(message);
                    break;
            }
        }

        private void OnCommand(JsonElement message)
        {
            MainWindow? front = library.Active;
            switch (message.GetProperty("command").GetString())
            {
                case "updateSettings":
                    SettingsStore.Patch(library.Settings, message.GetProperty("patch"));
                    library.SaveSettings();
                    break;
                case "previewSettings":
                    SettingsStore.Patch(library.Settings, message.GetProperty("patch"));
                    library.PreviewSettings();
                    break;
                case "commitSettings":
                    library.CommitPreviewedSettings();
                    break;
                case "requestLists":
                    _ = PostListsAsync();
                    break;
                case "chooseDirectory":
                    ChooseNewSessionDirectory();
                    break;
                case "chooseConfigDirectory":
                    ChooseConfigDirectory();
                    break;
                case "editConfig":
                    front?.EditConfigFile(message.TryGetProperty("file", out JsonElement file) ? file.GetString() ?? "keymap" : "keymap");
                    break;
                case "reloadConfig":
                    string? which = message.TryGetProperty("file", out JsonElement reloaded) ? reloaded.GetString() : null;
                    foreach (MainWindow open in library.OpenWindows.ToList())
                    {
                        open.ReloadConfigAndReport(which);
                    }

                    break;
                case "previewSound":
                    PreviewSound(message.GetProperty("kind").GetString(), message.GetProperty("name").GetString());
                    break;
                case "closeSettings":
                    window?.Close();
                    break;
                case "settingsMenuAction":
                    string? name = message.GetProperty("name").GetString();
                    if (name == "minimize" && window is not null)
                    {
                        window.WindowState = WindowState.Minimized;
                    }
                    else if (name == "open_settings")
                    {
                        Show();
                    }
                    else if (name is not null)
                    {
                        front?.RunMenuItem("action:" + name);
                    }

                    break;
            }
        }

        /// <summary>
        /// agterm's preview: a sound chosen in the settings plays once. A status sound is a file in C:\Windows\Media; a
        /// notification sound is the Windows sound event the toast plays.
        /// </summary>
        private void PreviewSound(string? kind, string? name)
        {
            string? path = kind == "notification" ? NotificationSoundFile(name) : name is null ? null : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", name + ".wav");
            if (path is null || name is null || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !File.Exists(path))
            {
                return;
            }

            // Kept in a field: Play is asynchronous and plays from the player's buffer.
            preview = new System.Media.SoundPlayer(path);
            preview.Play();
        }

        private System.Media.SoundPlayer? preview;

        /// <summary>The file Windows plays for a toast sound, from the current sound scheme.</summary>
        private static string? NotificationSoundFile(string? name)
        {
            string? soundEvent = name switch
            {
                "default" => "Notification.Default",
                "im" => "Notification.IM",
                "mail" => "Notification.Mail",
                "reminder" => "Notification.Reminder",
                "sms" => "Notification.SMS",
                _ => null,
            };
            if (soundEvent is null)
            {
                return null;
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\" + soundEvent + @"\.Current");
            return key?.GetValue(null) is string file && file.Length > 0 ? Environment.ExpandEnvironmentVariables(file) : null;
        }

        private void ChooseConfigDirectory()
        {
            AppSettings settings = library.Settings;
            OpenFolderDialog dialog = new OpenFolderDialog { Title = Strings.T("Папка keymap.conf и hooks.conf"), InitialDirectory = DirectoryPanelDefaults.Folder(settings.ConfigDirectory, library.Active?.ActiveSessionDirectory) };
            if (dialog.ShowDialog(window) == true)
            {
                settings.ConfigDirectory = dialog.FolderName;
                library.SaveSettings();
            }
        }

        private void ChooseNewSessionDirectory()
        {
            AppSettings settings = library.Settings;
            OpenFolderDialog dialog = new OpenFolderDialog { Title = Strings.T("Каталог для новых сессий"), InitialDirectory = DirectoryPanelDefaults.Folder(settings.NewSessionCustomDirectory, library.Active?.ActiveSessionDirectory) };
            if (dialog.ShowDialog(window) == true)
            {
                settings.NewSessionCustomDirectory = dialog.FolderName;
                settings.NewSessionDirectory = "custom";
                library.SaveSettings();
            }
        }

        private async Task PostListsAsync()
        {
            if (library.Active is MainWindow front)
            {
                Post(await front.SettingsListsAsync());
            }
        }

        /// <summary>The page's state: the settings as the window in front reads them, and its theme to look like it.</summary>
        private void PostState()
        {
            if (!pageReady || library.Active is not MainWindow front)
            {
                return;
            }

            (object settings, object theme, object keymap) = front.QuickLook();
            // A plain window: the system backdrop the main windows show through is not behind it.
            Post(new { type = "state", state = new { settings, theme, keymap, windowsBuild = Environment.OSVersion.Version.Build, settingsPage = true } });
        }

        /// <summary>The frame dark or light and, on Windows 11, in the theme's colors, as the main windows paint theirs.</summary>
        private void Paint()
        {
            if (window is null || library.Active?.CurrentTheme is not TerminalTheme theme)
            {
                return;
            }

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int dark = theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            int caption = ColorRef(theme.Background);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int text = ColorRef(theme.Foreground);
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            Color background = (Color)ColorConverter.ConvertFromString(theme.Background);
            window.Background = new SolidColorBrush(background);
            if (webView is not null)
            {
                webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(background.R, background.G, background.B);
            }
        }

        private static int ColorRef(string hex)
        {
            int value = Convert.ToInt32(hex.TrimStart('#'), 16);
            return ((value & 0xFF) << 16) | (value & 0xFF00) | ((value >> 16) & 0xFF);
        }

        internal void PostShortcuts(object message) => Post(message);

        private void Post(object message)
        {
            webView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
