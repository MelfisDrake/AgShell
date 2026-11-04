using System.Text;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's CommandPath.widened: the PATH a shell, a custom command or a hook starts with. agshell's folder goes first, so
    /// agshellctl matches the running app; then the system and user PATH as the registry holds them now, so a program
    /// installed after agshell started is found; then whatever else agshell's own PATH carries. Each folder appears once.
    /// </summary>
    public static class CommandPath
    {
        public static string Widened(string? bundledDirectory, string? machinePath, string? userPath, string? processPath, Func<string, string?> lookup)
        {
            List<string> entries = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string entry)
            {
                string trimmed = entry.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed.Trim('"').TrimEnd('\\')))
                {
                    entries.Add(trimmed);
                }
            }

            if (bundledDirectory is not null)
            {
                Add(bundledDirectory.TrimEnd('\\'));
            }

            foreach (string? source in new[] { machinePath, userPath })
            {
                foreach (string part in (source ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    Add(Expand(part, lookup));
                }
            }

            foreach (string part in (processPath ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                Add(part);
            }

            return string.Join(";", entries);
        }

        /// <summary>Windows' %NAME% expansion: a name <paramref name="lookup"/> does not know stays as written.</summary>
        public static string Expand(string text, Func<string, string?> lookup)
        {
            StringBuilder result = new StringBuilder();
            int index = 0;
            while (index < text.Length)
            {
                int start = text.IndexOf('%', index);
                int end = start < 0 ? -1 : text.IndexOf('%', start + 1);
                if (end < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }

                result.Append(text, index, start - index);
                string name = text.Substring(start + 1, end - start - 1);
                if (name.Length > 0 && lookup(name) is string value)
                {
                    result.Append(value);
                    index = end + 1;
                }
                else
                {
                    result.Append('%').Append(name);
                    index = end;
                }
            }

            return result.ToString();
        }
    }
}
