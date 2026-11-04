using System.Globalization;

namespace AgShell.Core
{
    /// <summary>
    /// The agterm-scoped ghostty.conf beside keymap.conf: keys in Ghostty's own syntax for what Ghostty does under agterm's
    /// terminals. AgShell's terminals are xterm.js, so it reads the keys whose behavior it carries out; any other Ghostty key
    /// is reported, not silently dropped. A value left empty puts the key back to its default, a later line wins, and
    /// config-file includes another file (a leading ? when it may be missing), as in Ghostty.
    /// </summary>
    public sealed record GhosttyConfig
    {
        public const string FileName = "ghostty.conf";

        public GhosttyAppearance Appearance { get; init; } = new GhosttyAppearance();

        /// <summary>Ghostty's copy-on-select: true copies a mouse selection to the selection clipboard, clipboard to the clipboard as well.</summary>
        public string CopyOnSelect { get; init; } = "true";

        /// <summary>ask, allow or deny: a program reading the clipboard (OSC 52, the Kitty protocol).</summary>
        public string ClipboardRead { get; init; } = "ask";

        /// <summary>ask, allow or deny: a program setting the clipboard; null keeps the setting's choice.</summary>
        public string? ClipboardWrite { get; init; }

        public bool MouseHideWhileTyping { get; init; }

        /// <summary>A fixed title for every pane, in place of the titles programs set; null when unset.</summary>
        public string? Title { get; init; }

        public bool CursorClickToMove { get; init; }

        /// <summary>Ghostty's desktop-notifications: false drops the notifications programs send with OSC 9 and 777.</summary>
        public bool DesktopNotifications { get; init; } = true;

        /// <summary>Left and right padding of the terminal, in points.</summary>
        public (int Left, int Right) PaddingX { get; init; } = (8, 8);

        public (int Top, int Bottom) PaddingY { get; init; } = (6, 6);

        /// <summary>Ghostty's scrollback-limit in bytes; xterm.js keeps lines, at about 1000 bytes a line.</summary>
        public long ScrollbackLimit { get; init; } = 10_000_000;

        public int ScrollbackLines => (int)Math.Clamp(ScrollbackLimit / 1000, 0, 1_000_000);

        /// <summary>Ghostty's theme: a name or an absolute path for both sides, or light:NAME,dark:NAME; the settings' theme wins.</summary>
        public ThemePair? Theme { get; init; }

        /// <summary>The problems found, each with its file and line, as Ghostty's config diagnostics.</summary>
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

        /// <summary>agterm's starter: comments only, so a fresh file changes nothing.</summary>
        public const string Starter = """
            # AgShell-scoped ghostty config, as agterm's ghostty.conf: keys in Ghostty's syntax for AgShell's terminals.
            # Saving the file applies it (so do "Reload ghostty.conf" in the palette and agshellctl config reload). Blank
            # lines and lines starting with # are ignored; an empty value puts a key back to its default; config-file = path
            # includes another file.
            #
            # AgShell's terminals are not Ghostty, so only these keys take effect; any other key is reported:
            #   font-family = Cascadia Mono             (repeatable fallback list; blank clears it)
            # Direct values load before Settings; config-file includes load after Settings for these four keys.
            #   font-size = 13
            #   cursor-style = block | bar | underline | block_hollow
            #   background-opacity = 1                 (renderer; window opacity is a separate setting)
            #   copy-on-select = true | false | clipboard
            #   clipboard-read = ask | allow | deny
            #   clipboard-write = ask | allow | deny      (the setting "Programs may write to the clipboard" wins when set)
            #   mouse-hide-while-typing = true | false
            #   title = fixed title for every pane
            #   cursor-click-to-move = true | false      (Alt+click moves the cursor)
            #   desktop-notifications = true | false     (false drops the notifications programs send)
            #   window-padding-x = 8   or   8,8
            #   window-padding-y = 6   or   6,6
            #   scrollback-limit = 10000000              (bytes; about 1000 a line)
            #   theme = name | light:name,dark:name | C:\path\to\theme   (the theme in the settings wins when set)

            """;

        private static readonly HashSet<string> Unsupported = new HashSet<string>(StringComparer.Ordinal)
        {
            "keybind", "macos-option-as-alt",
            "shell-integration", "shell-integration-features", "term", "env", "command", "working-directory",
        };

        public static GhosttyConfig Load(string path)
        {
            List<string> diagnostics = new List<string>();
            List<(string Key, string Value, string Where)> values = new List<(string, string, string)>();
            List<(string Path, bool Optional)> files = new List<(string, bool)>();
            Read(path, optional: true, values, diagnostics, files);
            ReadIncludes(files, values, diagnostics);
            return Build(values, diagnostics, GhosttyAppearance.Load(path));
        }

        public static GhosttyConfig Parse(string text, string name = FileName)
        {
            List<string> diagnostics = new List<string>();
            List<(string Key, string Value, string Where)> values = new List<(string, string, string)>();
            ReadText(text, name, null, values, diagnostics, new List<(string, bool)>());
            return Build(values, diagnostics, GhosttyAppearance.Parse(text, name));
        }

        private static void ReadIncludes(List<(string Path, bool Optional)> files, List<(string Key, string Value, string Where)> values, List<string> diagnostics)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < files.Count; index++)
            {
                (string path, bool optional) = files[index];
                if (!seen.Add(path))
                {
                    diagnostics.Add(Path.GetFileName(path) + ": config-file cycle detected");
                    continue;
                }

                Read(path, optional, values, diagnostics, files);
            }
        }

        private static void Read(string path, bool optional, List<(string Key, string Value, string Where)> values, List<string> diagnostics, List<(string Path, bool Optional)> files)
        {
            string full = Path.GetFullPath(path);

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!optional)
                {
                    diagnostics.Add(Path.GetFileName(full) + ": file not found");
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Path.GetFileName(full) + ": " + ex.Message);
                return;
            }

            ReadText(text, Path.GetFileName(full), Path.GetDirectoryName(full), values, diagnostics, files);
        }

        private static void ReadText(string text, string name, string? folder, List<(string Key, string Value, string Where)> values, List<string> diagnostics, List<(string Path, bool Optional)> files)
        {
            string[] lines = text.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string where = name + ":" + (index + 1);
                int equals = line.IndexOf('=');
                if (equals < 0)
                {
                    diagnostics.Add(where + ": expected key = value");
                    continue;
                }

                string key = line[..equals].Trim();
                string value = Unquoted(line[(equals + 1)..].Trim());
                if (key == "config-file")
                {
                    if (value.Length == 0)
                    {
                        files.Clear();
                    }
                    else if (folder is not null)
                    {
                        bool optional = value.StartsWith('?');
                        files.Add((Path.GetFullPath(Path.Combine(folder, optional ? value[1..] : value)), optional));
                    }

                    continue;
                }

                values.Add((key, value, where));
            }
        }

        private static GhosttyConfig Build(List<(string Key, string Value, string Where)> values, List<string> diagnostics, GhosttyAppearance appearance)
        {
            diagnostics.AddRange(appearance.Diagnostics);
            GhosttyConfig config = new GhosttyConfig();
            foreach ((string key, string value, string where) in values)
            {
                if (GhosttyAppearance.Handles(key))
                {
                    continue;
                }

                if (value.Length == 0)
                {
                    GhosttyConfig defaults = new GhosttyConfig();
                    GhosttyConfig? reset = key switch
                    {
                        "copy-on-select" => config with { CopyOnSelect = defaults.CopyOnSelect },
                        "clipboard-read" => config with { ClipboardRead = defaults.ClipboardRead },
                        "clipboard-write" => config with { ClipboardWrite = defaults.ClipboardWrite },
                        "mouse-hide-while-typing" => config with { MouseHideWhileTyping = defaults.MouseHideWhileTyping },
                        "title" => config with { Title = defaults.Title },
                        "cursor-click-to-move" => config with { CursorClickToMove = defaults.CursorClickToMove },
                        "desktop-notifications" => config with { DesktopNotifications = defaults.DesktopNotifications },
                        "window-padding-x" => config with { PaddingX = defaults.PaddingX },
                        "window-padding-y" => config with { PaddingY = defaults.PaddingY },
                        "scrollback-limit" => config with { ScrollbackLimit = defaults.ScrollbackLimit },
                        "theme" => config with { Theme = defaults.Theme },
                        _ => null,
                    };
                    if (reset is not null)
                    {
                        config = reset;
                        continue;
                    }
                }

                switch (key)
                {
                    case "copy-on-select" when value is "true" or "false" or "clipboard":
                        config = config with { CopyOnSelect = value };
                        break;
                    case "clipboard-read" when value is "ask" or "allow" or "deny":
                        config = config with { ClipboardRead = value };
                        break;
                    case "clipboard-write" when value is "ask" or "allow" or "deny":
                        config = config with { ClipboardWrite = value };
                        break;
                    case "mouse-hide-while-typing" when value is "true" or "false":
                        config = config with { MouseHideWhileTyping = value == "true" };
                        break;
                    case "cursor-click-to-move" when value is "true" or "false":
                        config = config with { CursorClickToMove = value == "true" };
                        break;
                    case "desktop-notifications" when value is "true" or "false":
                        config = config with { DesktopNotifications = value == "true" };
                        break;
                    case "title":
                        config = config with { Title = TerminalText.Name(value) };
                        break;
                    case "window-padding-x" when Pair(value) is (int left, int right):
                        config = config with { PaddingX = (left, right) };
                        break;
                    case "window-padding-y" when Pair(value) is (int top, int bottom):
                        config = config with { PaddingY = (top, bottom) };
                        break;
                    case "theme" when ThemeName.Parse(value) is ThemePair theme:
                        config = config with { Theme = theme };
                        break;
                    case "scrollback-limit" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long limit):
                        config = config with { ScrollbackLimit = limit };
                        break;
                    case "copy-on-select" or "clipboard-read" or "clipboard-write" or "mouse-hide-while-typing" or "cursor-click-to-move" or "desktop-notifications" or "window-padding-x" or "window-padding-y" or "scrollback-limit" or "theme":
                        diagnostics.Add(where + ": invalid value for " + key + ": " + value);
                        break;
                    default:
                        diagnostics.Add(where + ": " + (Unsupported.Contains(key) ? key + " is not supported by AgShell's terminals (the settings or keymap.conf cover it)" : "unknown key: " + key));
                        break;
                }
            }

            return config with { Diagnostics = diagnostics, Appearance = appearance };
        }

        /// <summary>Ghostty's padding: one number for both sides, or two separated by a comma.</summary>
        private static (int, int)? Pair(string value)
        {
            string[] parts = value.Split(',');
            if (parts.Length is < 1 or > 2 || !parts.All(part => int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number <= 200))
            {
                return null;
            }

            int first = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            return (first, parts.Length == 2 ? int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture) : first);
        }

        private static string Unquoted(string value)
        {
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }
    }
}
