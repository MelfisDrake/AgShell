using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>Settings and themes: settings.json, the theme in force, the title bar, and what the settings panel asks for.</summary>
    public partial class MainWindow
    {
        /// <summary>The settings every window shares.</summary>
        private AppSettings settings => Library.Settings;
        private ThemeCatalog themes => Library.ThemeCatalog;
        private TerminalTheme theme = null!;
        private bool systemDark => Library.Appearance.Dark;
        private string systemAccent => Library.Appearance.Accent;
        private bool systemTransparency => Library.Appearance.Transparency;
        private bool previewing;
        private string? previewTheme;
        private Task<List<string>>? fontsTask;

        private void InitAppearance()
        {
            Library.SettingsSaved += OnSettingsSaved;
            ApplySettings();
            Library.Appearance.AppearanceChanged += OnSystemAppearanceChanged;
            Library.Appearance.AccentChanged += PostState;
            SourceInitialized += (sender, args) => PaintTitleBar();
            fontsTask = Task.Run(MonospacedFonts);
        }

        /// <summary>Pushes the settings the model and window act on themselves, then resolves the theme.</summary>
        private void ApplySettings()
        {
            Strings.Apply(settings);
            model.StatusReset = settings.EffectiveStatusReset;
            model.BaseFontSize = Library.TerminalAppearance.FontSize;
            model.DashboardBaseFontSize = Library.TerminalAppearance.FontSize;
            ResolveTheme();
        }

        /// <summary>
        /// The theme in force: a palette preview first, then the slot the Windows theme picks, then ghostty.conf's; no theme
        /// is Ghostty's own colors, and so is a theme that does not load, as Ghostty keeps its defaults then.
        /// </summary>
        private void ResolveTheme()
        {
            string? name = previewing ? previewTheme : Themes.ActiveName(settings, systemDark, Library.Ghostty.Theme);
            theme = name is null ? Themes.BuiltIn : themes.Find(name, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Theme ?? Themes.BuiltIn;
            PaintTitleBar();
            nativePages?.SetTheme(theme);
            if (Library.Active == this)
            {
                Library.SettingsWindow.Refresh();
            }
        }

        /// <summary>
        /// The system backdrop shows through: Windows 11 22H2 or later, with Transparency effects on. agterm's Reduce
        /// Transparency, the Windows way: with them off the window is opaque in the theme's color, and the opacity the
        /// settings keep is left as it is for when they come back on.
        /// </summary>
        private bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621 && systemTransparency;

        /// <summary>agterm's window opacity: below 1 with a backdrop to show, and opaque in full screen.</summary>
        private bool Translucent => BackdropSupported && settings.EffectiveBackgroundOpacity < 1 && stateBeforeFullscreen is null;

        private void OnSystemAppearanceChanged()
        {
            ResolveTheme();
            PostState();
        }

        /// <summary>Colors the window frame like the terminal: dark or light, and on Windows 11 the theme's own colors.</summary>
        private void PaintTitleBar()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || theme is null)
            {
                return;
            }

            int dark = theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            // agterm's title bar is part of the translucent surface: a see-through window leaves the caption to the backdrop.
            int caption = Translucent ? unchecked((int)0xFFFFFFFF) : ColorRef(theme.Background);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int text = ColorRef(theme.Foreground);
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            System.Windows.Media.Color background = (System.Windows.Media.Color)ColorConverter.ConvertFromString(theme.Background);
            ApplyBackdrop(handle, background);
            PaintChrome();
        }

        /// <summary>
        /// agterm's background opacity, the Windows 11 way: the window turns transparent over the system backdrop (Mica,
        /// or Acrylic for blur), and the page paints the theme's colors with the chosen opacity on top. Older Windows has no
        /// backdrop, so the window stays opaque there.
        /// </summary>
        private void ApplyBackdrop(IntPtr handle, System.Windows.Media.Color background)
        {
            bool translucent = Translucent;
            HwndSource? source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            Background = Brushes.Transparent;
            Margins margins = translucent
                ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : new Margins { Top = (int)Math.Ceiling(chrome.GlassFrameThickness.Top * ChromeGetDpiForWindow(handle) / 96.0) };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            // agterm without blur shows what is under the window; with no system backdrop the extended frame is black under the
            // page (tried 2026-10-28), so Mica, the wallpaper's tone, stands in for it. Blur is Acrylic.
            int backdrop = translucent ? (settings.BackgroundBlur == true ? 3 : 2) : 1;
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        /// <summary>The system's double-click interval, which agterm's deferred workspace-row toggle waits out.</summary>
        [DllImport("user32.dll")]
        private static extern int GetDoubleClickTime();

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

        private static int ColorRef(string hex)
        {
            int value = Convert.ToInt32(hex.TrimStart('#'), 16);
            return ((value & 0xFF) << 16) | (value & 0xFF00) | ((value >> 16) & 0xFF);
        }

        private object ThemeView()
        {
            return new
            {
                name = theme.Name,
                dark = theme.IsDark,
                // agterm's terminalThemeIsDark: the sidebar's own brightness, the tint washed over the background, which
                // picks the disclosure triangle's light or dark system gray.
                sidebarDark = Themes.IsDark(theme.Background, settings.SidebarShiftAmount),
                background = theme.Background,
                foreground = theme.Foreground,
                cursor = theme.Cursor,
                cursorText = theme.CursorText,
                selectionBackground = theme.SelectionBackground,
                selectionForeground = theme.SelectionForeground,
                palette = theme.Palette,
            };
        }

        private object SettingsView()
        {
            return new
            {
                fontFamily = settings.FontFamily,
                terminalFontFamilies = Library.TerminalAppearance.FontFamilies,
                terminalFontSize = Library.TerminalAppearance.FontSize,
                terminalBackgroundOpacity = Library.TerminalAppearance.BackgroundOpacity,
                fontSize = settings.EffectiveFontSize,
                theme = settings.Theme,
                darkTheme = settings.DarkTheme,
                followSystemAppearance = settings.FollowSystemAppearance == true,
                systemDark,
                systemAccent,
                cursorStyle = Library.TerminalAppearance.CursorStyle,
                cursorBlink = settings.EffectiveCursorBlink,
                // agterm's Default: nothing set, so a program's DECSET 12 drives the blink.
                cursorStyleSetting = settings.CursorStyle is string style && AppSettings.CursorStyles.Contains(style) ? style : null,
                cursorBlinkSetting = settings.CursorBlink,
                notificationsEnabled = settings.NotificationsEnabled ?? true,
                notificationBadgeEnabled = settings.NotificationBadgeEnabled ?? true,
                taskbarFlash = settings.EffectiveTaskbarFlash,
                activeStatusColorHex = settings.ActiveStatusColorHex,
                blockedStatusColorHex = settings.BlockedStatusColorHex,
                completedStatusColorHex = settings.CompletedStatusColorHex,
                statusReset = settings.EffectiveStatusReset,
                rightClickPaste = settings.RightClickPaste ?? true,
                clipboardWrite = settings.ClipboardWrite ?? true,
                // ghostty.conf's terminal keys, as agterm hands them to Ghostty.
                copyOnSelect = Library.Ghostty.CopyOnSelect,
                mouseHideWhileTyping = Library.Ghostty.MouseHideWhileTyping,
                cursorClickToMove = Library.Ghostty.CursorClickToMove,
                terminalPadding = new { left = Library.Ghostty.PaddingX.Left, right = Library.Ghostty.PaddingX.Right, top = Library.Ghostty.PaddingY.Top, bottom = Library.Ghostty.PaddingY.Bottom },
                scrollback = Library.Ghostty.ScrollbackLines,
                newSessionDirectory = settings.EffectiveNewSessionDirectory,
                newSessionCustomDirectory = settings.NewSessionCustomDirectory,
                newSessionPlacement = settings.EffectiveNewSessionPlacement,
                confirmCloseSession = settings.ConfirmCloseSession == true,
                closeGraceUndoEnabled = settings.CloseGraceUndoEnabled != false,
                autoFollowAttention = settings.EffectiveAutoFollowAttention,
                autoFollowStayOnActive = settings.AutoFollowStayOnActive == true,
                notificationSoundName = settings.EffectiveNotificationSound,
                blockedStatusSoundName = settings.BlockedStatusSoundName,
                hiddenInterfaceElements = settings.EffectiveHiddenInterfaceElements,
                hiddenByDefaultElements = AppSettings.HiddenByDefaultElements,
                shownInterfaceElements = settings.ShownInterfaceElements ?? new List<string>(),
                toolbarMode = settings.EffectiveToolbarMode,
                attentionButtonEnabled = settings.AttentionButtonEnabled == true,
                remotesAvailable = RemotesAvailable(),
                activeStatusShape = settings.EffectiveStatusShape(settings.ActiveStatusShape),
                blockedStatusShape = settings.EffectiveStatusShape(settings.BlockedStatusShape),
                completedStatusShape = settings.EffectiveStatusShape(settings.CompletedStatusShape),
                quickTerminalSizePercent = settings.EffectiveQuickTerminalSizePercent,
                quickTerminalSizeSetting = settings.QuickTerminalSizeSetting,
                backgroundOpacity = settings.EffectiveBackgroundOpacity,
                backgroundBlur = settings.BackgroundBlur == true,
                // agterm keeps the sliders live under Reduce Transparency; only a Windows without the backdrop greys them.
                backdropSupported = Environment.OSVersion.Version.Build >= 22621,
                transparencyOff = !systemTransparency,
                fullscreen = stateBeforeFullscreen is not null,
                restoreMode = settings.EffectiveRestoreMode,
                language = settings.EffectiveLanguage,
                uiLanguage = Strings.Language,
                flaggedViewLayout = settings.EffectiveFlaggedViewLayout,
                workspaceRowClickExpands = settings.WorkspaceRowClickExpands != false,
                doubleClickTime = GetDoubleClickTime(),
                mouseScrollMultiplier = settings.EffectiveMouseScrollMultiplier,
                terminalInputSource = settings.TerminalInputSource,
                autoHideSidebarInactiveWindows = settings.AutoHideSidebarInactiveWindows == true,
                htmlOverlayZoom = settings.EffectiveHtmlOverlayZoom,
                configDirectory = settings.ConfigDirectory,
                configFolder = ConfigFolder,
                restoreUnavailableReason = settings.EffectiveRestoreMode == CommandRestore.Live ? Library.LiveUnavailableReason : null,
                liveResetAvailable = Library.LiveResetMenuVisible,
                denylistPath,
                linkOpenMode = settings.EffectiveLinkOpenMode,
                inactivePaneMuteStrength = settings.EffectiveInactivePaneMuteStrength,
                sidebarBackgroundShift = settings.EffectiveSidebarBackgroundShift,
                sidebarFontSize = settings.EffectiveSidebarFontSize,
                interfaceFontSize = settings.EffectiveInterfaceFontSize,
                themesPath = themes.UserDirectory,
                keymapDiagnostics = keymap.Diagnostics.Concat(hotkeyError is null ? Array.Empty<string>() : new[] { hotkeyError }),
                hookDiagnostics,
            };
        }

        /// <summary>Saves the shared settings; every window, this one included, applies them through <see cref="OnSettingsSaved"/>.</summary>
        private bool SaveSettings()
        {
            return Library.SaveSettings();
        }

        private void OnSettingsSaved()
        {
            // A moved config directory brings its own keymap.conf and hooks.conf, as agterm reloads both.
            if (watchedFolder is not null && !string.Equals(watchedFolder, ConfigFolder, StringComparison.OrdinalIgnoreCase))
            {
                LoadConfig();
                WatchConfig();
            }

            ApplySettings();
            ApplyAutoFollowSettings();
            nativePages?.SetZoom(settings.EffectiveHtmlOverlayZoom);
            PostState();
        }

        private void UpdateSettings(JsonElement patch)
        {
            SettingsStore.Patch(settings, patch);
            SaveSettings();
        }

        public (string? Error, string Mode) LinkMode(string? mode)
        {
            if (mode is not null)
            {
                if (!AppSettings.LinkOpenModes.Contains(mode))
                {
                    return ("invalid link mode: " + mode, settings.EffectiveLinkOpenMode);
                }

                string? before = settings.LinkOpenMode;
                settings.LinkOpenMode = mode == "browser" ? null : mode;
                if (!SaveSettings())
                {
                    settings.LinkOpenMode = before;
                    return ("failed to save link mode; the previous mode is still in effect", settings.EffectiveLinkOpenMode);
                }
            }

            return (null, settings.EffectiveLinkOpenMode);
        }

        public (string? Error, string Layout) FlaggedLayout(string? layout)
        {
            string current = settings.EffectiveFlaggedViewLayout;
            if (layout is null)
            {
                return (null, current);
            }

            if (layout != "toggle" && !AppSettings.FlaggedViewLayouts.Contains(layout))
            {
                return ("invalid flagged layout: " + layout, current);
            }

            string next = layout == "toggle" ? (current == "tree" ? "flat" : "tree") : layout;
            string? before = settings.FlaggedViewLayout;
            settings.FlaggedViewLayout = next == "flat" ? null : next;
            if (!SaveSettings())
            {
                settings.FlaggedViewLayout = before;
                return ("failed to save flagged layout; the previous layout is still in effect", current);
            }
            return (null, settings.EffectiveFlaggedViewLayout);
        }

        public (IReadOnlyList<string> Names, string? Current, bool Sync, string? Light, string? Dark) ThemeState()
        {
            bool sync = settings.FollowSystemAppearance == true;
            return (themes.Names().ToList(), sync ? null : settings.Theme, sync, sync ? settings.Theme : null, sync ? settings.DarkTheme : null);
        }

        public string? SetTheme(string? light, string? dark)
        {
            bool clearDark = string.Equals(dark, "none", StringComparison.OrdinalIgnoreCase);
            foreach (string? name in new[] { light, clearDark ? null : dark })
            {
                if (name is not null && !themes.Contains(name))
                {
                    return "unknown theme: " + name;
                }
            }

            string? previousTheme = settings.Theme;
            string? previousDark = settings.DarkTheme;
            bool? previousFollow = settings.FollowSystemAppearance;
            if (light is null && dark is null)
            {
                settings.Theme = null;
                settings.DarkTheme = null;
                settings.FollowSystemAppearance = null;
            }

            if (light is not null)
            {
                settings.Theme = light;
            }

            if (clearDark)
            {
                settings.DarkTheme = null;
                settings.FollowSystemAppearance = null;
            }
            else if (dark is not null)
            {
                // Following the Windows theme keeps the current theme on the light side, as in agterm; no theme cannot be a
                // side, so it starts from agterm's light one.
                settings.Theme ??= Themes.DefaultLightName;
                settings.DarkTheme = dark;
                settings.FollowSystemAppearance = true;
            }

            bool previousPreviewing = previewing;
            string? previousPreview = previewTheme;
            previewing = false;
            previewTheme = null;
            if (!SaveSettings())
            {
                previewing = previousPreviewing;
                previewTheme = previousPreview;
                settings.Theme = previousTheme;
                settings.DarkTheme = previousDark;
                settings.FollowSystemAppearance = previousFollow;
                return "failed to save theme; the previous theme is still in effect";
            }

            return null;
        }

        /// <summary>
        /// The palette previews a theme while the selection moves over it, as agterm's theme picker does; committing writes
        /// the slot the current Windows theme uses, and leaving (a null name) restores what was there. An empty name is the
        /// "default ghostty" row: no theme.
        /// </summary>
        private void PreviewTheme(string? name, bool commit)
        {
            bool known = name == string.Empty || (name is not null && themes.Contains(name));
            string? picked = name == string.Empty ? null : name;
            previewing = known;
            previewTheme = known ? picked : null;
            if (commit && known)
            {
                if (settings.FollowSystemAppearance == true && systemDark)
                {
                    settings.DarkTheme = picked;
                }
                else
                {
                    settings.Theme = picked;
                }

                previewing = false;
                previewTheme = null;
                SaveSettings();
                return;
            }

            ResolveTheme();
            PostState();
        }

        /// <summary>Installed font families whose glyphs are all one width, for the terminal font picker.</summary>
        private static List<string> MonospacedFonts()
        {
            List<string> names = new List<string>();
            foreach (FontFamily family in Fonts.SystemFontFamilies)
            {
                try
                {
                    Typeface typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs)
                        && glyphs.CharacterToGlyphMap.TryGetValue('i', out ushort narrow)
                        && glyphs.CharacterToGlyphMap.TryGetValue('W', out ushort wide)
                        && Math.Abs(glyphs.AdvanceWidths[narrow] - glyphs.AdvanceWidths[wide]) < 0.001)
                    {
                        names.Add(family.Source);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NullReferenceException)
                {
                }
            }

            return names.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task PostListsAsync()
        {
            Post(await SettingsListsAsync());
        }

        /// <summary>The lists the settings choose from: themes, monospaced fonts, sounds and keyboard layouts.</summary>
        internal async Task<object> SettingsListsAsync()
        {
            List<string> fonts = fontsTask is null ? new List<string>() : await fontsTask;
            return new { type = "lists", themes = themes.Names(), fonts, sounds = MediaSounds(), inputSources = TerminalInputSource.EnabledSources().Select(source => new { id = source.Id, name = source.Name }) };
        }

        public void StepPageZoom(string action)
        {
            double zoom = PageZoom.Apply(action, settings.EffectiveHtmlOverlayZoom);
            settings.HtmlOverlayZoom = zoom == 1 ? null : zoom;
            SaveSettings();
        }

        /// <summary>The theme in force here, for the settings window to paint its frame like this window's.</summary>
        internal TerminalTheme CurrentTheme => theme;

        /// <summary>A right click pastes, as in agterm: the clipboard goes back to the page, which pastes it like Ctrl+V.</summary>
        private void PasteClipboard(Guid sessionId, string pane)
        {
            // The page decides: an empty clipboard pastes nothing, but in mode 5522 the paste is an event all the same.
            string text = ClipboardTextFor(new PaneKey(sessionId, Panes.TryParse(pane, out PaneRole role) ? role : PaneRole.Main)) ?? string.Empty;
            Post(new { type = "paste", sessionId, pane, text, clipboard = true });
        }

        /// <summary>
        /// A clicked link from terminal output, as agterm's LinkOpener follows LinkPolicy's route: the system opens a web or
        /// mail link, or the session shows a page over itself; a local file is shown in Explorer.
        /// </summary>
        private void OpenLink(string url, Guid? sessionId, string? pane)
        {
            LinkOrigin origin = pane switch
            {
                "scratch" => LinkOrigin.Scratch,
                "quick" => LinkOrigin.Quick,
                "left" or "right" or null => LinkOrigin.Pane,
                _ => LinkOrigin.ProgramOverlay,
            };
            Guid? owner = sessionId is Guid id && id != Guid.Empty ? id : model.SelectedSessionId;
            LinkRoute route = LinkPolicy.Route(url, settings.EffectiveLinkOpenMode == "overlay", origin, path => owner is Guid session ? LocalPath(session, path) : null);
            switch (route.Action)
            {
                case LinkAction.Overlay when owner is Guid target:
                    _ = OpenLinkOverAsync(new Uri(route.Target), target);
                    break;
                case LinkAction.Overlay:
                case LinkAction.Browser:
                    Process.Start(new ProcessStartInfo(route.Target) { UseShellExecute = true });
                    break;
                case LinkAction.Reveal:
                    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + route.Target } });
                    break;
            }
        }

        /// <summary>The quick terminal's link: its shell is the front window's selected session's kind, so paths resolve as there.</summary>
        internal void OpenQuickLink(string url)
        {
            OpenLink(url, null, "quick");
        }

        /// <summary>The quick terminal's right-click paste: the clipboard goes back to its page, which pastes it like Ctrl+V.</summary>
        internal string ClipboardText()
        {
            return ClipboardTextFor(new PaneKey(model.SelectedSessionId ?? Guid.Empty, PaneRole.Main)) ?? string.Empty;
        }

        /// <summary>
        /// As agterm's, a link that cannot open over the session (a HUD or a zoom is up, another machine presents it, the slot
        /// is held, the browser store is being cleared) opens in the browser.
        /// </summary>
        private async Task OpenLinkOverAsync(Uri uri, Guid target)
        {
            // agterm's openLinkOverlay: a HUD on the session and a zoomed terminal in the window refuse the page up front;
            // the page would end the HUD for good, or show nothing. It opens as a browsing page: navigation, scripts, the
            // saved store and other sites in place.
            if (model.Zoomed || model.FindSession(target)?.Overlay is { IsHud: true } || !(await dispatcher.HandleAsync(new ControlRequest
            {
                Cmd = "session.overlay.open",
                Target = target.ToString(),
                Args = new ControlArgs { Url = uri.AbsoluteUri, Navigation = true, Js = true, Persistent = true, Browse = true },
            })).Ok)
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        }

        /// <summary>
        /// Where Ctrl+Shift+T opens a session and where it goes in the workspace, as the settings say. The current session's
        /// directory comes with its shell, since a WSL path means nothing to PowerShell.
        /// </summary>
        private (string? Cwd, string? ProfileId, int? Index) NewSessionPlace(Workspace workspace)
        {
            Session? current = model.SelectedSessionId is Guid selected ? model.FindSession(selected) : null;
            (string? cwd, string? profileId) = settings.EffectiveNewSessionDirectory switch
            {
                "currentSession" when current is { RemoteHost: not null } => (AppModel.LocalDirectory(current.FocusedCwd), null),
                "currentSession" when current is not null => (current.FocusedCwd, current.ProfileId),
                "custom" when Directory.Exists(settings.NewSessionCustomDirectory) => (settings.NewSessionCustomDirectory, null),
                _ => (null, null),
            };
            int position = current is null ? -1 : workspace.Sessions.ToList().IndexOf(current);
            int? index = settings.EffectiveNewSessionPlacement == "afterCurrent" && position >= 0 ? position + 1 : null;
            return (cwd, profileId, index);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
