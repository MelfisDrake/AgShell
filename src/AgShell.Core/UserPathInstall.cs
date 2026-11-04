namespace AgShell.Core
{
    /// <summary>What putting agshell's folder on the user's PATH changes: the new value and the other agshellctl folders it drops.</summary>
    public sealed record UserPathChange(string Path, IReadOnlyList<string> Removed, bool AlreadyPresent);

    /// <summary>
    /// agterm's ln -sf always points agtermctl at the running app; on Windows the same comes from the user's PATH holding
    /// this folder and no other folder with agshellctl.exe in it, so a new terminal cannot find an older copy first.
    /// </summary>
    public static class UserPathInstall
    {
        public const string ToolName = "agshellctl.exe";

        /// <summary>
        /// The user's PATH (as stored, variables unexpanded) with <paramref name="folder"/> on it once and without the other
        /// entries whose folder <paramref name="hasTool"/> says holds agshellctl.exe; an entry that cannot be read is kept.
        /// </summary>
        public static UserPathChange Apply(string path, string folder, Func<string, string> expand, Func<string, bool> hasTool)
        {
            return Change(path, folder, expand, hasTool, add: true);
        }

        /// <summary>The system PATH without the other agshellctl folders; this folder is neither added nor dropped there.</summary>
        public static UserPathChange Without(string path, string folder, Func<string, string> expand, Func<string, bool> hasTool)
        {
            return Change(path, folder, expand, hasTool, add: false);
        }

        private static UserPathChange Change(string path, string folder, Func<string, string> expand, Func<string, bool> hasTool, bool add)
        {
            string own = Normalize(folder);
            List<string> kept = new List<string>();
            List<string> removed = new List<string>();
            bool present = false;
            foreach (string part in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = expand(part).Trim().Trim('"');
                if (string.Equals(Normalize(entry), own, StringComparison.OrdinalIgnoreCase))
                {
                    if (!present)
                    {
                        kept.Add(part);
                        present = true;
                    }

                    continue;
                }

                if (entry.Length > 0 && hasTool(entry))
                {
                    removed.Add(part);
                    continue;
                }

                kept.Add(part);
            }

            if (!present && add)
            {
                kept.Add(folder.TrimEnd('\\'));
            }

            return new UserPathChange(!add && removed.Count == 0 ? path : string.Join(";", kept), removed, present);
        }

        private static string Normalize(string entry)
        {
            return entry.Trim().Trim('"').TrimEnd('\\');
        }
    }
}
