using System.Security.Cryptography;
using System.Text;

namespace AgShell.Protocol
{
    /// <summary>Where the app and the CLI meet: the state directory and the pipe name derived from it.</summary>
    public static class ControlEndpoint
    {
        public const string StateDirVariable = "AGSHELL_STATE_DIR";
        public const string PipeVariable = "AGSHELL_PIPE";

        /// <summary>The live daemon's pipe, in the shell it runs: agterm's ZMX_SESSION.</summary>
        public const string LivePipeVariable = "AGSHELL_LIVE_PIPE";

        /// <summary>
        /// The pipe of a live pane's daemon: the control pipe's name with -live and the pane's id, as agterm names a zmx
        /// daemon by its pane identity, so the name holds through a swap or a promotion and across builds.
        /// </summary>
        public static string LivePanePipe(string stateDirectory, string paneId)
        {
            return LivePipePrefix(stateDirectory) + Guid.Parse(paneId).ToString("N");
        }

        /// <summary>What every live pane pipe of a state directory starts with: agterm's zmx socket directory.</summary>
        public static string LivePipePrefix(string stateDirectory)
        {
            return PipeName(stateDirectory) + "-live-";
        }

        /// <summary>
        /// The panes whose daemons run for a state directory, by their ids in the upper-case form the app gives them, as
        /// agterm lists its zmx socket directory; a name of another shape is not one of ours. Null when the pipes cannot be listed.
        /// </summary>
        public static List<string>? LivePanes(string stateDirectory)
        {
            string prefix = LivePipePrefix(stateDirectory);
            try
            {
                return Directory.GetFiles(@"\\.\pipe\")
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(name => name[prefix.Length..])
                    .Where(rest => rest.Length == 32 && rest.All(char.IsAsciiHexDigitLower))
                    .Select(rest => Guid.ParseExact(rest, "N").ToString().ToUpperInvariant())
                    .Distinct()
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // agterm's listSessions: a listing that failed is not an empty one, so nobody acts on that silence.
                Log.Error("ZmxClient", "live list failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// agterm's bundled zmx build id: what tells one build of the live daemon from another, the size and time of its files,
        /// so a rebuild of one version is a build of its own. The same in the install and in the copy the daemon runs from.
        /// </summary>
        public static string LiveBuildOf(string directory)
        {
            StringBuilder identity = new StringBuilder();
            foreach (string name in LiveDaemonFiles)
            {
                FileInfo file = new FileInfo(Path.Combine(directory, name));
                if (file.Exists)
                {
                    identity.Append(name).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append(';');
                }
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..12];
        }

        /// <summary>
        /// The files the live daemon runs from: the program, in a build that is not one file its assemblies, ConPTY and
        /// agshell-vt, its terminal state.
        /// </summary>
        public static readonly IReadOnlyList<string> LiveDaemonFiles = new[]
        {
            "agshell-live.exe", "agshell-live.dll", "agshell-live.runtimeconfig.json", "agshell-live.deps.json",
            "AgShell.Protocol.dll", "AgShell.Pty.dll", "conpty.dll", "OpenConsole.exe", "agshell-vt.dll",
        };

        /// <summary>
        /// agterm's session.overlay.page.serve: a helper claiming a handed-over page by its single-use token, the target;
        /// after an ok answer the connection carries that page's channel.
        /// </summary>
        public const string PageServeCommand = "session.overlay.page.serve";

        /// <summary>
        /// agterm's session.overlay.job.run: a helper claiming a handed-over overlay job by its id, the target; after an ok
        /// answer the connection carries the job's launch context and how the program ended.
        /// </summary>
        public const string JobRunCommand = "session.overlay.job.run";

        /// <summary>agterm's zmx.present: after an ok answer the connection carries the session's presentation stream both ways.</summary>
        public const string PresentCommand = "live.present";

        /// <summary>The requests whose connection outlives the answer.</summary>
        public static readonly IReadOnlySet<string> StreamCommands = new HashSet<string> { PageServeCommand, JobRunCommand, PresentCommand };

        /// <summary>
        /// agterm's waitsOnNetwork: requests that wait on ssh or on processes for seconds, answered off the serial line so
        /// the line keeps going, and so the far side's own agshellctl (live tree of this machine, a restarted shell) gets in.
        /// </summary>
        public static readonly IReadOnlySet<string> ParallelCommands = new HashSet<string> { "live.tree", "live.attach", "session.restart" };

        /// <summary>A request line longer than this is refused; the newline is not counted. agterm's maxRequestLineBytes.</summary>
        public const int MaxRequestBytes = 1 << 20;

        /// <summary>%APPDATA%\agshell, the installed app's state directory.</summary>
        public static string StandardStateDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agshell");
        }

        public static string StateDirectory()
        {
            string? custom = Environment.GetEnvironmentVariable(StateDirVariable);
            return string.IsNullOrWhiteSpace(custom) ? StandardStateDirectory() : Path.GetFullPath(custom);
        }

        /// <summary>
        /// One pipe per user, and a separate one for every custom state directory, so a test instance never answers
        /// for the real one.
        /// </summary>
        public static string PipeName(string stateDirectory)
        {
            string user = Environment.UserName;
            string standard = StandardStateDirectory();
            if (string.Equals(Path.GetFullPath(stateDirectory).TrimEnd('\\'), standard.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return "agshell-" + user;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(stateDirectory).ToLowerInvariant()));
            return "agshell-" + user + "-" + Convert.ToHexStringLower(hash)[..12];
        }

        /// <summary>The pipe the CLI talks to: an explicit one, then the session's, then the one for the state directory.</summary>
        public static string ResolvePipe(string? explicitPipe)
        {
            if (!string.IsNullOrWhiteSpace(explicitPipe))
            {
                return explicitPipe;
            }

            string? fromSession = Environment.GetEnvironmentVariable(PipeVariable);
            return string.IsNullOrWhiteSpace(fromSession) ? PipeName(StateDirectory()) : fromSession;
        }
    }
}
