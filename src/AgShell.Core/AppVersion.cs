namespace AgShell.Core
{
    /// <summary>What agterm's About panel and Help menu show: the version, the short commit, the project's pages.</summary>
    public static class AppVersion
    {
        public const string RepositoryUrl = "https://github.com/MelfisDrake/AgShell";

        /// <summary>The informational version "1.2.3+sha" split into the version and the short commit, as agterm's "1.2.3 (abc1234)".</summary>
        public static (string Version, string? Commit) Split(string informational)
        {
            int plus = informational.IndexOf('+');
            if (plus < 0)
            {
                return (informational, null);
            }

            string commit = informational[(plus + 1)..];
            return (informational[..plus], commit.Length == 0 ? null : commit[..Math.Min(7, commit.Length)]);
        }

        /// <summary>agterm's Developer Documentation…: the agshellctl reference, in the interface language.</summary>
        public static string DocumentationUrl(string language)
        {
            return RepositoryUrl + (language == "ru" ? "/blob/main/docs/agshellctl.ru.md" : "/blob/main/docs/agshellctl.md");
        }
    }
}
