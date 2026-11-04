using System.Text;

namespace AgShell.Core
{
    /// <summary>A theme value as Ghostty takes it: one name or path for both sides, or a light and a dark one.</summary>
    public readonly record struct ThemePair(string Light, string Dark)
    {
        public string Side(bool dark)
        {
            return dark ? Dark : Light;
        }
    }

    /// <summary>
    /// The theme key of ghostty.conf, which agterm hands to libghostty as it is: read as Ghostty's Theme.parseCLI reads it on
    /// Windows, and looked up as Ghostty's theme.open looks it up, with agterm's ~/ for the home folder.
    /// </summary>
    public static class ThemeName
    {
        /// <summary>
        /// Ghostty's Theme.parseCLI: a comma, an equals sign or a colon (other than a drive letter's) makes it a pair, which
        /// needs both light: and dark: (later ones win, quoted values unquoted); otherwise the value, trimmed of spaces and
        /// tabs, is both sides. Null for what Ghostty reports as an invalid value.
        /// </summary>
        public static ThemePair? Parse(string value)
        {
            if (value.Length == 0)
            {
                return null;
            }

            int colon = value.IndexOf(':');
            if (!value.Contains(',') && !value.Contains('=') && (colon < 0 || colon == 1))
            {
                string trimmed = value.Trim(' ', '\t');
                return new ThemePair(trimmed, trimmed);
            }

            string? light = null;
            string? dark = null;
            List<string>? fields = Fields(value);
            if (fields is null)
            {
                return null;
            }

            foreach (string entry in fields)
            {
                int index = entry.IndexOf(':');
                if (index < 0)
                {
                    return null;
                }

                string key = entry[..index].Trim(' ', '\t');
                string? side = Unquoted(entry[(index + 1)..].Trim(' ', '\t'));
                if (side is null || key is not ("light" or "dark"))
                {
                    return null;
                }

                if (key == "light")
                {
                    light = side;
                }
                else
                {
                    dark = side;
                }
            }

            return light is not null && dark is not null ? new ThemePair(light, dark) : null;
        }

        /// <summary>
        /// The theme file for a value, as Ghostty's theme.open finds it: an absolute path is the only place, a name with a
        /// path separator is refused, and a name is looked for in the user's themes folder, then the bundled one; agterm's
        /// ~/ (or ~\ here) stands for the home folder. The theme is null when nothing loads, with Ghostty's messages.
        /// </summary>
        public static (TerminalTheme? Theme, IReadOnlyList<string> Diagnostics) Find(string value, string userDirectory, string? bundledDirectory, string home)
        {
            string name = value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, value[2..]) : value;
            if (IsAbsolute(name))
            {
                if (Directory.Exists(name))
                {
                    return (null, new[] { $"not reading theme from \"{name}\": it is a directory" });
                }

                TerminalTheme? theme = Read(name, Path.GetFileName(name));
                return theme is null ? (null, new[] { $"failed to load theme from the path \"{name}\"" }) : (theme, Array.Empty<string>());
            }

            if (name.Contains('/') || name.Contains('\\'))
            {
                return (null, new[] { $"theme \"{name}\" cannot include path separators unless it is an absolute path" });
            }

            // An empty name joins to the folder itself, which does not open as a file.
            if (name.Length == 0)
            {
                string? folder = new[] { userDirectory, bundledDirectory }.FirstOrDefault(directory => directory is not null && Directory.Exists(directory));
                return (null, folder is null ? Array.Empty<string>() : new[] { $"failed to load theme \"\" from the file \"{folder}\"" });
            }

            List<string> paths = new[] { userDirectory, bundledDirectory }.Where(directory => directory is not null).Select(directory => Path.Combine(directory!, name)).ToList();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    TerminalTheme? theme = Read(path, name);
                    return theme is null ? (null, new[] { $"failed to load theme \"{name}\" from the file \"{path}\"" }) : (theme, Array.Empty<string>());
                }
            }

            return (null, paths.Select(path => $"theme \"{name}\" not found, tried path \"{path}\"").ToList());
        }

        /// <summary>Zig's isAbsoluteWindows: a leading slash or backslash, or a drive letter, a colon and a separator.</summary>
        private static bool IsAbsolute(string path)
        {
            return path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');
        }

        private static TerminalTheme? Read(string path, string name)
        {
            try
            {
                return Themes.Parse(name, File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Ghostty's CommaSplitter on Windows: commas outside double quotes split, a backslash escapes only inside them.</summary>
        private static List<string>? Fields(string value)
        {
            List<string> fields = new List<string>();
            int start = 0;
            bool quoted = false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (quoted && c == '\\')
                {
                    index++;
                }
                else if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (c == ',' && !quoted)
                {
                    fields.Add(value[start..index]);
                    start = index + 1;
                }
            }

            if (quoted)
            {
                return null;
            }

            fields.Add(value[start..]);
            return fields;
        }

        /// <summary>A value in double quotes read as a Zig string literal (the common escapes); else the value as it is.</summary>
        private static string? Unquoted(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            {
                return value;
            }

            StringBuilder text = new StringBuilder();
            for (int index = 1; index < value.Length - 1; index++)
            {
                char c = value[index];
                if (c != '\\')
                {
                    text.Append(c);
                    continue;
                }

                if (++index >= value.Length - 1)
                {
                    return null;
                }

                char? escaped = value[index] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '\'' => '\'', '"' => '"', _ => null };
                if (escaped is null)
                {
                    return null;
                }

                text.Append(escaped.Value);
            }

            return text.ToString();
        }
    }
}
