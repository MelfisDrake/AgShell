using System.Globalization;

namespace AgShell.Core
{
    /// <summary>A terminal color theme in Ghostty's format, which agterm uses: the 16 ANSI colors and the special ones.</summary>
    public sealed record TerminalTheme(
        string Name,
        string Background,
        string Foreground,
        string? Cursor,
        string? CursorText,
        string? SelectionBackground,
        string? SelectionForeground,
        IReadOnlyList<string?> Palette)
    {
        public bool IsDark => Themes.IsDark(Background);
    }

    /// <summary>
    /// Themes as agterm finds them: one file per theme named after it, the user's folder before the bundled one.
    /// The bundled set is Ghostty's, from iTerm2-Color-Schemes, plus agshell's own default.
    /// </summary>
    public sealed class ThemeCatalog
    {
        private readonly string userDirectory;
        private readonly string? bundledDirectory;

        public ThemeCatalog(string userDirectory, string? bundledDirectory)
        {
            this.userDirectory = userDirectory;
            this.bundledDirectory = bundledDirectory;
        }

        public string UserDirectory => userDirectory;

        /// <summary>Theme names from both folders, sorted as agterm's localizedCaseInsensitiveCompare.</summary>
        public IReadOnlyList<string> Names()
        {
            return new[] { userDirectory, bundledDirectory }
                .Where(directory => directory is not null && Directory.Exists(directory))
                .SelectMany(directory => Directory.EnumerateFiles(directory!))
                .Select(path => Path.GetFileName(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))
                .ToList();
        }

        /// <summary>agterm's contains: the name exactly as listed.</summary>
        public bool Contains(string name)
        {
            return Names().Contains(name, StringComparer.Ordinal);
        }

        /// <summary>A listed theme by its name, or null when it does not load.</summary>
        public TerminalTheme? Load(string name)
        {
            return Find(name, string.Empty).Theme;
        }

        /// <summary>A theme value as Ghostty looks it up (a name or a path), with Ghostty's messages when it does not load.</summary>
        public (TerminalTheme? Theme, IReadOnlyList<string> Diagnostics) Find(string value, string home)
        {
            return ThemeName.Find(value, userDirectory, bundledDirectory, home);
        }
    }

    public static class Themes
    {
        /// <summary>The bundled theme a fresh install starts with; no theme at all is <see cref="BuiltIn"/>, as in agterm.</summary>
        public const string DefaultName = "agshell";

        /// <summary>agterm's name for no theme: the terminal's own colors, Ghostty's.</summary>
        public const string BuiltInTitle = "default ghostty";

        /// <summary>agterm's light side when following Windows starts from no theme, which cannot be one side of a pair.</summary>
        public const string DefaultLightName = "Builtin Light";

        public static TerminalTheme BuiltIn { get; } = Parse(BuiltInTitle, string.Empty)!;

        /// <summary>
        /// Reads a Ghostty theme file as libghostty's config reader does: a value in quotes is unquoted, an empty value puts
        /// the key back to its default, a value that does not parse leaves the key as it was, and a later line wins.
        /// Background and foreground fall back to Ghostty's own defaults.
        /// </summary>
        public static TerminalTheme? Parse(string name, string text)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            string?[] palette = new string?[16];
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || equals < 0)
                {
                    continue;
                }

                string key = line[..equals].Trim();
                string value = Unquoted(line[(equals + 1)..].Trim());
                if (key == "palette")
                {
                    int inner = value.IndexOf('=');
                    if (inner > 0 && int.TryParse(value[..inner].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < 16)
                    {
                        string entry = value[(inner + 1)..].Trim();
                        palette[index] = entry.Length == 0 ? null : GhosttyColor(entry) ?? palette[index];
                    }
                }
                else if (value.Length == 0)
                {
                    values.Remove(key);
                }
                else if (GhosttyColor(value) is string color)
                {
                    values[key] = color;
                }
            }

            return new TerminalTheme(
                name,
                values.GetValueOrDefault("background") ?? GhosttyBackground,
                values.GetValueOrDefault("foreground") ?? GhosttyForeground,
                values.GetValueOrDefault("cursor-color"),
                values.GetValueOrDefault("cursor-text"),
                values.GetValueOrDefault("selection-background"),
                values.GetValueOrDefault("selection-foreground"),
                palette);
        }

        /// <summary>A color as CSS takes it: what agterm's #rrggbb grammar accepts, with the # it may leave out; else the value as given.</summary>
        public static string? Css(string? value)
        {
            return value is null ? null : Color(value) ?? value;
        }

        /// <summary>Ghostty's colors when a theme sets none.</summary>
        public const string GhosttyBackground = "#282c34";

        public const string GhosttyForeground = "#ffffff";

        /// <summary>
        /// A color as Ghostty's RGB.parse reads it, as #rrggbb in lower case: #rgb, #rrggbb, #rrrgggbbb or #rrrrggggbbbb; an
        /// X11 color name from Ghostty's rgb.txt, any case; rgb or rrggbb without #; rgb:r/g/b with 1 to 4 hex digits each,
        /// or rgbi:r/g/b with fractions from 0 to 1; null otherwise. Spaces and tabs around it are ignored.
        /// </summary>
        public static string? GhosttyColor(string value)
        {
            string input = value.Trim(' ', '\t');
            if (input.Length == 0)
            {
                return null;
            }

            if (input[0] == '#')
            {
                int width = input.Length switch { 4 => 1, 7 => 2, 10 => 3, 13 => 4, _ => 0 };
                return width == 0 ? null : Rgb(FromHex(input.Substring(1, width)), FromHex(input.Substring(1 + width, width)), FromHex(input.Substring(1 + 2 * width, width)));
            }

            if (X11Colors.Value.TryGetValue(input, out string? named))
            {
                return named;
            }

            if (input.Length is 3 or 6)
            {
                int width = input.Length / 3;
                return Rgb(FromHex(input[..width]), FromHex(input.Substring(width, width)), FromHex(input.Substring(2 * width, width)));
            }

            bool intensity = input.StartsWith("rgbi:", StringComparison.Ordinal);
            if (!intensity && !input.StartsWith("rgb:", StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts = input[(intensity ? 5 : 4)..].Split('/');
            if (parts.Length != 3)
            {
                return null;
            }

            int[] channels = parts.Select(part => intensity ? FromIntensity(part) : FromHex(part)).ToArray();
            return Rgb(channels[0], channels[1], channels[2]);
        }

        /// <summary>Ghostty's fromHex: one to four hex digits, the value scaled from 4, 8, 12 or 16 bits to 8; -1 when invalid.</summary>
        private static int FromHex(string digits)
        {
            if (digits.Length is 0 or > 4 || !digits.All(Uri.IsHexDigit))
            {
                return -1;
            }

            int max = (1 << (4 * digits.Length)) - 1;
            return int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 255 / max;
        }

        /// <summary>Ghostty's fromIntensity: a plain decimal fraction from 0 to 1 (a sign, digits, a point), times 255; -1 when invalid.</summary>
        private static int FromIntensity(string text)
        {
            string digits = text.StartsWith('+') || text.StartsWith('-') ? text[1..] : text;
            int point = digits.IndexOf('.');
            bool valid = digits.Count(char.IsAsciiDigit) > 0 && digits.All(c => char.IsAsciiDigit(c) || c == '.') && (point < 0 || point == digits.LastIndexOf('.'));
            if (!valid || !double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double fraction) || !(fraction >= 0 && fraction <= 1))
            {
                return -1;
            }

            return (int)(fraction * 255);
        }

        private static string? Rgb(int red, int green, int blue)
        {
            return red < 0 || green < 0 || blue < 0 ? null : $"#{red:x2}{green:x2}{blue:x2}";
        }

        /// <summary>Ghostty's X11 color names (src/terminal/res/rgb.txt), matched without regard to ASCII case.</summary>
        private static readonly Lazy<Dictionary<string, string>> X11Colors = new Lazy<Dictionary<string, string>>(() =>
        {
            Dictionary<string, string> colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using Stream stream = typeof(Themes).Assembly.GetManifestResourceStream("AgShell.Core.rgb.txt")!;
            using StreamReader reader = new StreamReader(stream);
            while (reader.ReadLine() is string line)
            {
                if (line.Length < 12)
                {
                    continue;
                }

                int red = int.Parse(line[..3].Trim(), CultureInfo.InvariantCulture);
                int green = int.Parse(line[4..7].Trim(), CultureInfo.InvariantCulture);
                int blue = int.Parse(line[8..11].Trim(), CultureInfo.InvariantCulture);
                colors.TryAdd(line[12..].Trim(' ', '\t'), $"#{red:x2}{green:x2}{blue:x2}");
            }

            return colors;
        });

        /// <summary>agterm's #rrggbb grammar for colors it is given (the # optional), as #rrggbb in lower case; else null.</summary>
        public static string? Color(string value)
        {
            string hex = value.Trim().TrimStart('#');
            return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToLowerInvariant() : null;
        }

        private static string Unquoted(string value)
        {
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }

        /// <summary>Whether a background reads as dark, by Rec. 601 perceived luminance against the midpoint, as in agterm.</summary>
        public static bool IsDark(string color)
        {
            return IsDark(color, 0);
        }

        /// <summary>
        /// agterm's isDark(shiftAmount:): whether the sidebar reads as dark once the tint is washed over the background, a
        /// negative amount washing white over it, a positive one black, its size the wash's opacity.
        /// </summary>
        public static bool IsDark(string color, double shiftAmount)
        {
            double opacity = Math.Min(1, Math.Abs(shiftAmount));
            double added = shiftAmount < 0 ? opacity : 0;
            string hex = color.TrimStart('#');
            double Wash(int index) => int.Parse(hex.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0 * (1 - opacity) + added;
            return 0.299 * Wash(0) + 0.587 * Wash(2) + 0.114 * Wash(4) < 0.5;
        }

        /// <summary>
        /// The Windows mode apps follow, as agterm takes NSApp's appearance: AppsUseLightTheme 0 is dark, and with no value
        /// Windows shows apps light.
        /// </summary>
        public static bool AppsDark(object? appsUseLightTheme)
        {
            return appsUseLightTheme is int light && light == 0;
        }

        /// <summary>
        /// The theme in force, as agterm hands it to Ghostty: following the system appearance with both sides set, the side
        /// of the Windows theme; else the single theme, or the dark one when that is all there is. With no theme in the
        /// settings, ghostty.conf's theme (loaded before them in agterm) applies, its side picked by the Windows theme; null
        /// for no theme.
        /// </summary>
        public static string? ActiveName(AppSettings settings, bool systemDark, ThemePair? ghosttyTheme = null)
        {
            string? light = string.IsNullOrWhiteSpace(settings.Theme) ? null : settings.Theme.Trim();
            string? dark = string.IsNullOrWhiteSpace(settings.DarkTheme) ? null : settings.DarkTheme.Trim();
            if (settings.FollowSystemAppearance == true && light is not null && dark is not null)
            {
                return systemDark ? dark : light;
            }

            return light ?? dark ?? ghosttyTheme?.Side(systemDark);
        }
    }
}
