using System.Globalization;

namespace AgShell.Core
{
    public sealed record TerminalAppearance(IReadOnlyList<string> FontFamilies, double FontSize, string CursorStyle, double BackgroundOpacity);

    public sealed class GhosttyAppearance
    {
        private sealed record Entry(string Key, string Value);
        private readonly List<Entry> direct = new List<Entry>();
        private readonly List<Entry> included = new List<Entry>();
        public List<string> Diagnostics { get; } = new List<string>();

        public static bool Handles(string key) => key is "font-family" or "font-size" or "cursor-style" or "background-opacity";

        public static GhosttyAppearance Parse(string text, string name, string? folder = null)
        {
            GhosttyAppearance result = new GhosttyAppearance();
            List<string> files = new List<string>();
            result.Read(text, name, folder, result.direct, files);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < files.Count; index++)
            {
                string path = files[index];
                if (!seen.Add(path))
                {
                    continue;
                }

                try
                {
                    result.Read(File.ReadAllText(path), Path.GetFileName(path), Path.GetDirectoryName(path), result.included, files);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The shared config reader reports include failures.
                }
            }

            return result;
        }

        public static GhosttyAppearance Load(string path)
        {
            try
            {
                return Parse(File.ReadAllText(path), Path.GetFileName(path), Path.GetDirectoryName(Path.GetFullPath(path)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new GhosttyAppearance();
            }
        }

        private void Read(string text, string name, string? folder, List<Entry> entries, List<string> files)
        {
            string[] lines = text.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                int equals = line.IndexOf('=');
                if (line.StartsWith('#') || equals < 0)
                {
                    continue;
                }

                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }
                if (key == "config-file")
                {
                    if (value.Length == 0)
                    {
                        files.Clear();
                    }
                    else if (folder is not null)
                    {
                        files.Add(Path.GetFullPath(Path.Combine(folder, value.StartsWith('?') ? value[1..] : value)));
                    }
                }
                else if (Handles(key))
                {
                    string where = name + ":" + (index + 1);
                    if (value.Length == 0 || key == "font-family" || key == "cursor-style" && value is "block" or "bar" or "underline" or "block_hollow"
                        || key is "font-size" or "background-opacity" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) && number >= 0)
                    {
                        entries.Add(new Entry(key, value));
                    }
                    else
                    {
                        Diagnostics.Add(where + ": invalid value for " + key + ": " + value);
                    }
                }
            }
        }

        public TerminalAppearance Resolve(AppSettings settings)
        {
            List<string> families = new List<string>();
            double size = AppModel.DefaultFontSize;
            string cursor = "block";
            double opacity = 1;
            Apply(direct);
            if (!string.IsNullOrEmpty(settings.FontFamily))
            {
                families.Add(settings.FontFamily);
            }

            if (settings.FontSize is double selectedSize)
            {
                size = selectedSize;
            }

            if (settings.CursorStyle is string style && AppSettings.CursorStyles.Contains(style))
            {
                cursor = style;
            }

            if (settings.BackgroundOpacity < 1)
            {
                opacity = 0;
            }

            Apply(included);
            return new TerminalAppearance(families, size, cursor, opacity);

            void Apply(IEnumerable<Entry> entries)
            {
                foreach (Entry entry in entries)
                {
                    bool reset = entry.Value.Length == 0;
                    switch (entry.Key)
                    {
                        case "font-family":
                            if (reset) families.Clear();
                            else families.Add(entry.Value);
                            break;
                        case "font-size": size = reset ? AppModel.DefaultFontSize : Math.Max(1, double.Parse(entry.Value, CultureInfo.InvariantCulture)); break;
                        case "cursor-style": cursor = reset ? "block" : entry.Value; break;
                        case "background-opacity": opacity = reset ? 1 : Math.Clamp(double.Parse(entry.Value, CultureInfo.InvariantCulture), 0, 1); break;
                    }
                }
            }
        }
    }
}
