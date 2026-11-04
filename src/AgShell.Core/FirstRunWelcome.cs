namespace AgShell.Core
{
    /// <summary>agterm's FirstRunWelcome: the due-decision of the one-time welcome.</summary>
    public static class FirstRunWelcome
    {
        /// <summary>Files a previous launch leaves behind; the control pipe says nothing about earlier launches.</summary>
        public static readonly IReadOnlyList<string> PriorStateFiles = new[] { "state.json", "settings.json", "windows.json" };

        public const string PriorStateFolder = "windows";

        /// <summary>Read before anything is written: the first launch saves its session within a second.</summary>
        public static bool HasPriorState(string directory)
        {
            return PriorStateFiles.Any(name => File.Exists(Path.Combine(directory, name))) || Directory.Exists(Path.Combine(directory, PriorStateFolder));
        }

        /// <summary>Never twice, and never for someone who ran AgShell before the welcome existed.</summary>
        public static bool IsDue(bool? welcomeShown, bool hasPriorState) => welcomeShown != true && !hasPriorState;
    }
}
