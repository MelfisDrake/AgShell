using System.Text.RegularExpressions;

namespace AgShell.Core
{
    public static class DisplayNames
    {
        private static readonly char[] Separators = new char[] { '/', '\\' };

        /// <summary>As in agterm, a bash prompt's own title says nothing a basename does not.</summary>
        private static readonly Regex ShellPromptTitle = new Regex(@"^(\([^)]*\))?[^\s@]+@[^\s:]+:\s", RegexOptions.CultureInvariant);

        /// <summary>ConPTY titles a console with its program's path, and cmd appends the command it runs.</summary>
        private static readonly Regex ConsoleProgramTitle = new Regex(@"^[A-Za-z]:\\.*\.exe(\s+-\s.*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Claude Code leads its title with its own status: ✳ when idle, a braille spinner while working. The sidebar has its own.</summary>
        private static readonly Regex AgentStatusGlyph = new Regex(@"^[\u2733\u2800-\u28FF]\s*", RegexOptions.CultureInvariant);

        public static string ForSession(string? customName, string? title, string? cwd)
        {
            if (!string.IsNullOrWhiteSpace(customName))
            {
                return customName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }

            return Basename(cwd);
        }

        /// <summary>
        /// The title a program set through OSC 0/2, or null when it is one of the shell's own: the bare directory, a
        /// bash prompt's user@host: path, or the console program's path. agterm keeps the shell from titling at all;
        /// here the shell is the user's own, so its titles are dropped instead and the session falls back to the directory.
        /// Unlike agterm, Claude Code's leading status glyph is cut, since the sidebar shows the status itself.
        /// </summary>
        public static string? TerminalTitle(string raw, string? cwd)
        {
            string title = AgentStatusGlyph.Replace(StripControls(raw).Trim(), string.Empty);
            if (title.Length == 0 || title == cwd || ShellPromptTitle.IsMatch(title) || ConsoleProgramTitle.IsMatch(title))
            {
                return null;
            }

            return title;
        }

        private static string StripControls(string value)
        {
            if (!value.Any(c => c < ' ' || c == '\u007f'))
            {
                return value;
            }

            return new string(value.Where(c => c >= ' ' && c != '\u007f').ToArray());
        }

        public static string Basename(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "~";
            }

            string trimmed = path.Trim();
            if (IsRoot(trimmed))
            {
                return trimmed;
            }

            trimmed = trimmed.TrimEnd(Separators);
            int lastSeparator = trimmed.LastIndexOfAny(Separators);
            return lastSeparator < 0 ? trimmed : trimmed.Substring(lastSeparator + 1);
        }

        private static bool IsRoot(string path)
        {
            if (path == "/" || path == "\\")
            {
                return true;
            }

            bool driveLetter = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
            return driveLetter && (path.Length == 2 || (path.Length == 3 && Array.IndexOf(Separators, path[2]) >= 0));
        }
    }
}
