using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgShell.Core
{
    /// <summary>
    /// User settings, kept apart from the session tree as agterm keeps them. Every field is optional: null means the
    /// default, so a file written before a field existed still loads, and an unknown enum value reads as the default.
    /// </summary>
    public sealed class AppSettings
    {
        public const double DefaultInterfaceFontSize = 13;
        public const double MinInterfaceFontSize = 9;
        public const double MaxInterfaceFontSize = 20;
        public const int DefaultInactivePaneMuteStrength = 5;
        public const int DefaultSidebarBackgroundShift = 5;

        public static readonly IReadOnlyList<string> CursorStyles = new[] { "block", "bar", "underline" };
        public static readonly IReadOnlyList<string> NewSessionDirectories = new[] { "home", "currentSession", "custom" };
        public static readonly IReadOnlyList<string> NewSessionPlacements = new[] { "end", "afterCurrent" };
        public static readonly IReadOnlyList<string> StatusResets = new[] { "firstKey", "enter", "never" };
        public static readonly IReadOnlyList<string> TaskbarFlashes = new[] { "off", "once", "untilFocused" };
        public static readonly IReadOnlyList<string> LinkOpenModes = new[] { "browser", "overlay" };
        public static readonly IReadOnlyList<string> AutoFollowTimeouts = new[] { "off", "s5", "s10", "s30", "s60", "m5" };
        public static readonly IReadOnlyList<string> NotificationSounds = new[] { "default", "im", "mail", "reminder", "sms", "none" };
        public static readonly IReadOnlyList<string> InterfaceElements = new[]
        {
            "sidebarToggle", "workspaceName", "sessionName", "windowName", "remoteHost", "sessionContext", "recentSessions", "scratch", "split", "overlayToggle", "dashboard", "quickTerminal", "customCommands",
            "newWorkspace", "newSession", "flaggedView", "focusFilter", "workspaceAddSession",
        };

        /// <summary>As in agterm, these start hidden until the user turns them on.</summary>
        public static readonly IReadOnlyList<string> HiddenByDefaultElements = new[] { "workspaceName", "customCommands", "overlayToggle" };

        public static readonly IReadOnlyList<string> ToolbarModes = new[] { "compact", "normal", "hidden" };
        public static readonly IReadOnlyList<int> QuickTerminalSizes = new[] { 40, 50, 60, 70, 80, 90 };
        public static readonly IReadOnlyList<string> FlaggedViewLayouts = new[] { "flat", "plain", "tree" };
        public const double DefaultMouseScrollMultiplier = 3;

        public string? FontFamily { get; set; }

        public double? FontSize { get; set; }

        /// <summary>The theme; with <see cref="FollowSystemAppearance"/> it is the light one.</summary>
        public string? Theme { get; set; }

        /// <summary>The theme under a dark Windows theme, used only with <see cref="FollowSystemAppearance"/>.</summary>
        public string? DarkTheme { get; set; }

        public bool? FollowSystemAppearance { get; set; }

        public string? CursorStyle { get; set; }

        public bool? CursorBlink { get; set; }

        public bool? NotificationsEnabled { get; set; }

        public bool? NotificationBadgeEnabled { get; set; }

        /// <summary>How a notification flashes the taskbar button, agterm's Dock bounce: off, once or untilFocused.</summary>
        public string? TaskbarFlash { get; set; }

        public string? ActiveStatusColorHex { get; set; }

        public string? BlockedStatusColorHex { get; set; }

        public string? CompletedStatusColorHex { get; set; }

        /// <summary>Which keystroke clears a blocked or completed status: firstKey, enter or never.</summary>
        public string? StatusReset { get; set; }

        public bool? RightClickPaste { get; set; }

        /// <summary>Programs may set the clipboard with OSC 52, agshellctl clipboard set from another machine included.</summary>
        public bool? ClipboardWrite { get; set; }

        public string? NewSessionDirectory { get; set; }

        public string? NewSessionCustomDirectory { get; set; }

        public string? NewSessionPlacement { get; set; }

        public bool? ConfirmCloseSession { get; set; }

        /// <summary>Whether a close from the window can be undone for a few seconds; on unless turned off, as in agterm.</summary>
        public bool? CloseGraceUndoEnabled { get; set; }

        public string? LinkOpenMode { get; set; }

        public int? InactivePaneMuteStrength { get; set; }

        public int? SidebarBackgroundShift { get; set; }

        public double? SidebarFontSize { get; set; }

        public double? InterfaceFontSize { get; set; }

        /// <summary>After this much idleness the window goes to the oldest blocked session, as agterm's auto-follow.</summary>
        public string? AutoFollowAttention { get; set; }

        /// <summary>Auto-follow leaves a running (active) session alone.</summary>
        public bool? AutoFollowStayOnActive { get; set; }

        /// <summary>The toast's sound: default, im, mail, reminder or sms; null plays none, as in agterm.</summary>
        public string? NotificationSoundName { get; set; }

        /// <summary>A sound from C:\Windows\Media played when a session turns blocked; null plays none.</summary>
        public string? BlockedStatusSoundName { get; set; }

        /// <summary>The names AgShell 0.1 wrote; read once into the agterm names and never written back.</summary>
        [JsonPropertyName("activeStatusColor")]
        public string? LegacyActiveStatusColor { get => null; set => ActiveStatusColorHex ??= value; }

        [JsonPropertyName("blockedStatusColor")]
        public string? LegacyBlockedStatusColor { get => null; set => BlockedStatusColorHex ??= value; }

        [JsonPropertyName("completedStatusColor")]
        public string? LegacyCompletedStatusColor { get => null; set => CompletedStatusColorHex ??= value; }

        [JsonPropertyName("notificationSound")]
        public string? LegacyNotificationSound { get => null; set => NotificationSoundName ??= value; }

        [JsonPropertyName("blockedStatusSound")]
        public string? LegacyBlockedStatusSound { get => null; set => BlockedStatusSoundName ??= value; }

        /// <summary>Title bar and sidebar elements the user has hidden, by name.</summary>
        public List<string>? HiddenInterfaceElements { get; set; }

        /// <summary>Elements hidden by default that the user has turned on.</summary>
        public List<string>? ShownInterfaceElements { get; set; }

        /// <summary>agterm's title bar row above the terminal: compact (one line, the default), normal (two) or hidden.</summary>
        public string? ToolbarMode { get; set; }

        /// <summary>Whether the title bar shows the bell listing the sessions that need attention; off unless turned on.</summary>
        public bool? AttentionButtonEnabled { get; set; }

        /// <summary>The status glyph's shape per state, one of <see cref="AgentStatuses.Shapes"/>; null is a circle.</summary>
        public string? ActiveStatusShape { get; set; }

        public string? BlockedStatusShape { get; set; }

        public string? CompletedStatusShape { get; set; }

        /// <summary>The quick terminal's share of the window, one of <see cref="QuickTerminalSizes"/>; null is the default 90%.</summary>
        public int? QuickTerminalSizePercent { get; set; }

        /// <summary>Window background opacity, 0–1; below 1 the Windows 11 backdrop shows through.</summary>
        public double? BackgroundOpacity { get; set; }

        /// <summary>Blurs what shows through (Acrylic) instead of the plain backdrop (Mica).</summary>
        public bool? BackgroundBlur { get; set; }

        /// <summary>How sessions come back on launch: none (fresh shells), rerun (the programs they ran) or live.</summary>
        public string? RestoreMode { get; set; }

        /// <summary>The interface language: system (Russian when Windows speaks it, else English), ru or en.</summary>
        public string? Language { get; set; }

        /// <summary>With more than one window open, only the front one shows its sidebar, as agterm offers; off unless turned on.</summary>
        public bool? AutoHideSidebarInactiveWindows { get; set; }

        /// <summary>agterm's html overlay zoom: the page zoom every page overlay shows at; null is actual size.</summary>
        public double? HtmlOverlayZoom { get; set; }

        /// <summary>The folder of keymap.conf and hooks.conf; null keeps them in the state directory.</summary>
        public string? ConfigDirectory { get; set; }

        /// <summary>agterm's first-launch welcome has been shown; it never comes back.</summary>
        public bool? WelcomeShown { get; set; }

        /// <summary>
        /// The flagged view: flat labels rows "session : workspace", plain by the session alone, tree nests them under
        /// their workspaces.
        /// </summary>
        public string? FlaggedViewLayout { get; set; }

        /// <summary>Whether a click anywhere on a workspace row folds it; the chevron folds it regardless. On unless turned off.</summary>
        public bool? WorkspaceRowClickExpands { get; set; }

        /// <summary>Ghostty's mouse-scroll-multiplier as agterm offers it, 1–10; agterm's default 3 is the terminal's normal speed.</summary>
        public double? MouseScrollMultiplier { get; set; }

        /// <summary>
        /// agterm's terminalInputSource: the keyboard layout held while a terminal has the keyboard, as the layout's handle in
        /// hex; null is off. <see cref="InputSourceLock"/> owns when it is selected and restored.
        /// </summary>
        public string? TerminalInputSource { get; set; }

        [JsonIgnore]
        public string EffectiveRestoreMode => Known(RestoreMode, CommandRestore.Modes);

        public static readonly IReadOnlyList<string> Languages = new[] { "system", "ru", "en" };

        [JsonIgnore]
        public string EffectiveLanguage => Known(Language, Languages);

        /// <summary>The language the interface speaks, given the two-letter name of the language Windows shows itself in.</summary>
        public string InterfaceLanguage(string windowsLanguage)
        {
            return EffectiveLanguage switch
            {
                "system" => string.Equals(windowsLanguage, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en",
                string chosen => chosen,
            };
        }

        [JsonIgnore]
        public double EffectiveHtmlOverlayZoom => HtmlOverlayZoom is double zoom && double.IsFinite(zoom) ? Math.Clamp(zoom, 0.5, 3) : 1;

        [JsonIgnore]
        public string EffectiveFlaggedViewLayout => Known(FlaggedViewLayout, FlaggedViewLayouts);

        [JsonIgnore]
        public double EffectiveMouseScrollMultiplier => MouseScrollMultiplier ?? DefaultMouseScrollMultiplier;

        [JsonIgnore]
        public string EffectiveAutoFollowAttention => Known(AutoFollowAttention, AutoFollowTimeouts);

        /// <summary>The auto-follow idle grace, or null when it is off.</summary>
        [JsonIgnore]
        public TimeSpan? AutoFollowTimeout => EffectiveAutoFollowAttention switch
        {
            "s5" => TimeSpan.FromSeconds(5),
            "s10" => TimeSpan.FromSeconds(10),
            "s30" => TimeSpan.FromSeconds(30),
            "s60" => TimeSpan.FromSeconds(60),
            "m5" => TimeSpan.FromMinutes(5),
            _ => null,
        };

        [JsonIgnore]
        public string EffectiveNotificationSound => NotificationSoundName is string name && NotificationSounds.Contains(name) ? name : "none";

        /// <summary>agterm's resolvedHiddenInterfaceElements: an element hidden by default shows only when shown, any other hides only when hidden.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> EffectiveHiddenInterfaceElements => InterfaceElements
            .Where(element => HiddenByDefaultElements.Contains(element)
                ? !(ShownInterfaceElements ?? new List<string>()).Contains(element)
                : (HiddenInterfaceElements ?? new List<string>()).Contains(element))
            .ToList();

        [JsonIgnore]
        public string EffectiveToolbarMode => Known(ToolbarMode, ToolbarModes);

        public string EffectiveStatusShape(string? shape) => Known(shape, AgentStatuses.Shapes);

        /// <summary>agterm's effectiveQuickTerminalSizePercent: a share outside the choices reads as unset, the built-in size.</summary>
        [JsonIgnore]
        public int? QuickTerminalSizeSetting => QuickTerminalSizePercent is int size && QuickTerminalSizes.Contains(size) ? size : null;

        [JsonIgnore]
        public int EffectiveQuickTerminalSizePercent => QuickTerminalSizeSetting ?? 90;

        [JsonIgnore]
        public double EffectiveBackgroundOpacity => Math.Clamp(BackgroundOpacity ?? 1, 0, 1);

        [JsonIgnore]
        public double EffectiveFontSize => FontSize ?? AppModel.DefaultFontSize;

        [JsonIgnore]
        public string EffectiveCursorStyle => Known(CursorStyle, CursorStyles);

        [JsonIgnore]
        public bool EffectiveCursorBlink => CursorBlink ?? true;

        [JsonIgnore]
        public string EffectiveNewSessionDirectory => Known(NewSessionDirectory, NewSessionDirectories);

        [JsonIgnore]
        public string EffectiveNewSessionPlacement => Known(NewSessionPlacement, NewSessionPlacements);

        [JsonIgnore]
        public string EffectiveStatusReset => Known(StatusReset, StatusResets);

        [JsonIgnore]
        public string EffectiveTaskbarFlash => Known(TaskbarFlash, TaskbarFlashes);

        [JsonIgnore]
        public string EffectiveLinkOpenMode => Known(LinkOpenMode, LinkOpenModes);

        [JsonIgnore]
        public int EffectiveInactivePaneMuteStrength => Math.Clamp(InactivePaneMuteStrength ?? DefaultInactivePaneMuteStrength, 0, 10);

        [JsonIgnore]
        public int EffectiveSidebarBackgroundShift => Math.Clamp(SidebarBackgroundShift ?? DefaultSidebarBackgroundShift, 0, 10);

        /// <summary>agterm's sidebarShiftAmount: the signed wash over the terminal background, negative lighter, positive darker.</summary>
        [JsonIgnore]
        public double SidebarShiftAmount => (EffectiveSidebarBackgroundShift - 5) * 0.06;

        [JsonIgnore]
        public double EffectiveSidebarFontSize => Math.Clamp(SidebarFontSize ?? DefaultInterfaceFontSize, MinInterfaceFontSize, MaxInterfaceFontSize);

        [JsonIgnore]
        public double EffectiveInterfaceFontSize => Math.Clamp(InterfaceFontSize ?? DefaultInterfaceFontSize, MinInterfaceFontSize, MaxInterfaceFontSize);

        /// <summary>The value when it is one of the allowed ones, otherwise the first, which is the default.</summary>
        private static string Known(string? value, IReadOnlyList<string> allowed)
        {
            return value is not null && allowed.Contains(value) ? value : allowed[0];
        }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }
    }

    public sealed class SettingsStore
    {
        private static readonly SettingsJsonContext Json = new SettingsJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        public SettingsStore(string path)
        {
            FilePath = path;
        }

        public string FilePath { get; }

        public bool WriteBlocked { get; private set; }

        /// <summary>
        /// The saved settings, or defaults when missing or unreadable. An unreadable source blocks writes until a
        /// successful load; a corrupt source must be preserved before defaults can replace it.
        /// </summary>
        public AppSettings Load()
        {
            WriteBlocked = true;
            try
            {
                using FileStream stream = File.OpenRead(FilePath);
                AppSettings settings = JsonSerializer.Deserialize(stream, Json.AppSettings) ?? Seeded();
                WriteBlocked = false;
                return settings;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                WriteBlocked = false;
                return Seeded();
            }
            catch (JsonException)
            {
                try
                {
                    PreservedFile.MoveAside(FilePath, "corrupt");
                    WriteBlocked = false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }

                return Seeded();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Seeded();
            }

            static AppSettings Seeded() => new AppSettings { Theme = Themes.DefaultName };
        }

        /// <summary>Writes the file with its keys sorted, as agterm's sortedKeys; a failed write is dropped, as agterm's try?.</summary>
        public bool Save(AppSettings settings)
        {
            if (WriteBlocked)
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temporaryPath = FilePath + ".tmp";
                JsonElement json = JsonSerializer.SerializeToElement(settings, Json.AppSettings);
                using (FileStream stream = File.Create(temporaryPath))
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in json.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        property.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                File.Move(temporaryPath, FilePath, true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Applies a patch from the settings panel: each present property replaces the field, and a JSON null clears it
        /// back to the default. Unknown names are reported rather than ignored.
        /// </summary>
        public static IReadOnlyList<string> Patch(AppSettings settings, JsonElement patch)
        {
            List<string> unknown = new List<string>();
            foreach (JsonProperty property in patch.EnumerateObject())
            {
                JsonElement value = property.Value;
                bool isNull = value.ValueKind == JsonValueKind.Null;
                string? text = isNull || value.ValueKind != JsonValueKind.String ? null : value.GetString();
                double? number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
                bool? flag = value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
                switch (property.Name)
                {
                    case "fontFamily": settings.FontFamily = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "fontSize": settings.FontSize = number; break;
                    case "theme": settings.Theme = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "darkTheme": settings.DarkTheme = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "followSystemAppearance": settings.FollowSystemAppearance = flag; break;
                    case "cursorStyle": settings.CursorStyle = text; break;
                    case "cursorBlink": settings.CursorBlink = flag; break;
                    case "notificationsEnabled": settings.NotificationsEnabled = flag == true ? null : flag; break;
                    case "notificationBadgeEnabled": settings.NotificationBadgeEnabled = flag == true ? null : flag; break;
                    case "taskbarFlash": settings.TaskbarFlash = text == "off" ? null : text; break;
                    case "activeStatusColorHex": settings.ActiveStatusColorHex = text is null ? null : Themes.Color(text); break;
                    case "blockedStatusColorHex": settings.BlockedStatusColorHex = text is null ? null : Themes.Color(text); break;
                    case "completedStatusColorHex": settings.CompletedStatusColorHex = text is null ? null : Themes.Color(text); break;
                    case "statusReset": settings.StatusReset = text == "firstKey" ? null : text; break;
                    case "rightClickPaste": settings.RightClickPaste = flag == true ? null : flag; break;
                    case "clipboardWrite": settings.ClipboardWrite = flag == true ? null : flag; break;
                    case "newSessionDirectory": settings.NewSessionDirectory = text == "home" ? null : text; break;
                    case "newSessionCustomDirectory": settings.NewSessionCustomDirectory = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "newSessionPlacement": settings.NewSessionPlacement = text == "end" ? null : text; break;
                    case "confirmCloseSession": settings.ConfirmCloseSession = flag == true ? true : null; break;
                    case "closeGraceUndoEnabled": settings.CloseGraceUndoEnabled = flag == false ? false : null; break;
                    case "linkOpenMode": settings.LinkOpenMode = text; break;
                    case "inactivePaneMuteStrength": settings.InactivePaneMuteStrength = number is double mute && (int)Math.Round(mute) != AppSettings.DefaultInactivePaneMuteStrength ? (int)Math.Round(mute) : null; break;
                    case "sidebarBackgroundShift": settings.SidebarBackgroundShift = number is double shift && (int)Math.Round(shift) != AppSettings.DefaultSidebarBackgroundShift ? (int)Math.Round(shift) : null; break;
                    case "sidebarFontSize": settings.SidebarFontSize = number == AppSettings.DefaultInterfaceFontSize ? null : number; break;
                    case "interfaceFontSize": settings.InterfaceFontSize = number == AppSettings.DefaultInterfaceFontSize ? null : number; break;
                    case "autoFollowAttention": settings.AutoFollowAttention = text == "off" ? null : text; break;
                    case "autoFollowStayOnActive": settings.AutoFollowStayOnActive = flag == true ? true : null; break;
                    case "notificationSoundName": settings.NotificationSoundName = text == "none" || string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "blockedStatusSoundName": settings.BlockedStatusSoundName = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "hiddenInterfaceElements":
                        settings.HiddenInterfaceElements = Strings(value);
                        break;
                    case "shownInterfaceElements":
                        settings.ShownInterfaceElements = Strings(value);
                        break;
                    case "toolbarMode": settings.ToolbarMode = text == "compact" ? null : text; break;
                    case "attentionButtonEnabled": settings.AttentionButtonEnabled = flag; break;
                    case "activeStatusShape": settings.ActiveStatusShape = text == "circle" ? null : text; break;
                    case "blockedStatusShape": settings.BlockedStatusShape = text == "circle" ? null : text; break;
                    case "completedStatusShape": settings.CompletedStatusShape = text == "circle" ? null : text; break;
                    case "quickTerminalSizePercent": settings.QuickTerminalSizePercent = number is double size ? (int)Math.Round(size) : null; break;
                    case "backgroundOpacity": settings.BackgroundOpacity = number >= 1 ? null : number; break;
                    case "backgroundBlur": settings.BackgroundBlur = flag == true ? true : null; break;
                    case "restoreMode": settings.RestoreMode = text; break;
                    case "language": settings.Language = text is not null && AppSettings.Languages.Contains(text) && text != "system" ? text : null; break;
                    case "flaggedViewLayout": settings.FlaggedViewLayout = text; break;
                    case "workspaceRowClickExpands": settings.WorkspaceRowClickExpands = flag == false ? false : null; break;
                    case "mouseScrollMultiplier": settings.MouseScrollMultiplier = number == AppSettings.DefaultMouseScrollMultiplier ? null : number; break;
                    case "terminalInputSource": settings.TerminalInputSource = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    case "autoHideSidebarInactiveWindows": settings.AutoHideSidebarInactiveWindows = flag; break;
                    case "htmlOverlayZoom": settings.HtmlOverlayZoom = number is double zoom && zoom != 1 ? zoom : null; break;
                    case "configDirectory": settings.ConfigDirectory = string.IsNullOrWhiteSpace(text) ? null : text; break;
                    default: unknown.Add(property.Name); break;
                }
            }

            return unknown;
        }

        private static List<string>? Strings(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : null;
        }
    }

    [JsonSerializable(typeof(AppSettings))]
    internal sealed partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
