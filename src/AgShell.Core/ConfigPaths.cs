namespace AgShell.Core
{
    /// <summary>agterm's ConfigPaths: where the user-editable config files live.</summary>
    public static class ConfigPaths
    {
        /// <summary>Files that lived in the state directory before the config got its own; themes is AgShell's own folder.</summary>
        public static readonly IReadOnlyList<string> FormerStateFiles = new[] { "keymap.conf", "hooks.conf", "remotes.conf", "ghostty.conf", "restore-denylist.conf" };

        public const string ThemesFolder = "themes";

        /// <summary>
        /// The configured folder when set and not empty, else <c>config</c> in the state directory the config defaults to
        /// (<see cref="AgShell.Protocol.DebugStateDirectory.ConfigDirectory"/>), as agterm's <c>&lt;stateDir&gt;/config</c>.
        /// </summary>
        public static string ConfigDirectory(string? setting, string configStateDirectory)
        {
            return string.IsNullOrEmpty(setting) ? Path.Combine(configStateDirectory, "config") : setting;
        }

        public static string RestoreDenylistPath(string configDirectory) => Path.Combine(configDirectory, "restore-denylist.conf");

        /// <summary>agterm's ${VISUAL:-${EDITOR:-…}}: an empty variable counts as unset.</summary>
        public static string? Editor(string? visual, string? editor)
        {
            return !string.IsNullOrEmpty(visual) ? visual : !string.IsNullOrEmpty(editor) ? editor : null;
        }

        /// <summary>
        /// Moves the config files a former version kept in the state directory: keymap, hooks, remotes and ghostty.conf to
        /// the default config folder, the restore denylist and the themes folder to the one in use. A file already there
        /// stays and the old one is left alone. Returns what moved.
        /// </summary>
        public static List<string> MoveFromStateDirectory(string stateDirectory, string defaultConfigDirectory, string configDirectory)
        {
            List<string> moved = new List<string>();
            foreach (string name in FormerStateFiles)
            {
                string target = name == "restore-denylist.conf" ? configDirectory : defaultConfigDirectory;
                string from = Path.Combine(stateDirectory, name);
                string to = Path.Combine(target, name);
                if (SamePath(from, to) || !File.Exists(from) || File.Exists(to) || Directory.Exists(to))
                {
                    continue;
                }

                Directory.CreateDirectory(target);
                File.Move(from, to);
                moved.Add(name);
            }

            string themesFrom = Path.Combine(stateDirectory, ThemesFolder);
            string themesTo = Path.Combine(configDirectory, ThemesFolder);
            if (!SamePath(themesFrom, themesTo) && Directory.Exists(themesFrom) && !Directory.Exists(themesTo) && !File.Exists(themesTo))
            {
                Directory.CreateDirectory(configDirectory);
                Directory.Move(themesFrom, themesTo);
                moved.Add(ThemesFolder);
            }

            return moved;
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
