namespace AgShell.Protocol
{
    /// <summary>
    /// agterm's DebugStateDirectory: a Debug build started without AGSHELL_STATE_DIR keeps off the installed app's state in
    /// the sibling agshell-debug, while its keymap, hooks and ghostty.conf stay those of the standard directory.
    /// </summary>
    public static class DebugStateDirectory
    {
        public const string SiblingName = "agshell-debug";

        /// <summary>The directory a Debug launch takes, or null when it names one itself.</summary>
        public static string? Adopted(string? explicitDirectory, string standardDirectory)
        {
            return string.IsNullOrWhiteSpace(explicitDirectory) ? Sibling(standardDirectory) : null;
        }

        /// <summary>
        /// agterm's configStateDirectory: where the config files default to for a state directory — the standard one for
        /// the agshell-debug sibling, Release included when it names that sibling itself; the state directory otherwise.
        /// </summary>
        public static string ConfigDirectory(string stateDirectory, string standardDirectory)
        {
            return SamePath(stateDirectory, Sibling(standardDirectory)) ? standardDirectory : stateDirectory;
        }

        public static string Sibling(string standardDirectory)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(standardDirectory).TrimEnd('\\')) ?? standardDirectory;
            return Path.Combine(parent, SiblingName);
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
