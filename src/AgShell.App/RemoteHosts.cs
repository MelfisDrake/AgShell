using System.Diagnostics;
using System.IO;
using System.Text;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>One line of remotes.conf: what ssh takes, and the name the picker shows.</summary>
    internal sealed record RemoteMachine(string Destination, string Label);

    /// <summary>
    /// agterm's remote attach, from the Windows side: other machines' live panes, one ssh to agshellctl live proxy for each
    /// pane, as agterm runs one ssh to zmx attach for each. ssh runs without a terminal: the pty Windows OpenSSH gives -tt is
    /// a ConPTY, which reorders and rewrites the pane's bytes. Without prompts too, so the machine must take a key. ssh is
    /// found through PATH, as agterm's env finds it, so one the user put ahead of Windows' own is kept.
    /// </summary>
    internal sealed class RemoteHosts
    {
        private const string Ssh = "ssh";

        /// <summary>agterm's: no prompt that nobody could answer, and a bound on the handshake (not on the command).</summary>
        private static readonly string[] BaseOptions = { "-o", "BatchMode=yes", "-o", "ConnectTimeout=5" };

        /// <summary>The panes' connection: ssh's warnings would interleave with the relayed bytes.</summary>
        private static readonly string[] QuietOptions = { "-o", "LogLevel=ERROR" };

        /// <summary>agterm's cap on what a reconnect reason keeps of ssh's last line.</summary>
        private const int ReasonLimit = 200;

        /// <summary>
        /// ssh's options for the machine: without a terminal, or with one for an overlay job. agterm adds its keepalive only to
        /// the panes' long-lived connection; the short commands end on their own deadline.
        /// </summary>
        private static IEnumerable<string> Options(bool terminal = false)
        {
            return new[] { terminal ? "-tt" : "-T" }.Concat(BaseOptions);
        }

        /// <summary>
        /// agterm's keepalive: when the ssh config leaves ServerAliveInterval at 0 for the machine, a dead link is noticed in
        /// about fifteen seconds (5 seconds, two misses); a value of the user's own wins. Asked of ssh -G at every connection,
        /// as agterm does, so a change to the ssh config takes effect without a restart.
        /// </summary>
        private static string[] Keepalive(string destination, string daemon)
        {
            return KeepaliveOff(destination, daemon) ? new[] { "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2" } : Array.Empty<string>();
        }

        /// <summary>
        /// Whether ssh -G, given the connection's own options (a Match on them applies), says serveraliveinterval 0. Anything
        /// else, an answer it could not give included, leaves ssh's settings alone.
        /// </summary>
        private static bool KeepaliveOff(string destination, string daemon)
        {
            ProcessStartInfo start = new ProcessStartInfo(Ssh) { CreateNoWindow = true };
            start.ArgumentList.Add("-G");
            foreach (string option in new[] { "-T" }.Concat(BaseOptions).Concat(QuietOptions))
            {
                start.ArgumentList.Add(option);
            }

            start.ArgumentList.Add(destination);
            start.ArgumentList.Add("agshellctl");
            start.ArgumentList.Add("live");
            start.ArgumentList.Add("proxy");
            start.ArgumentList.Add(daemon);
            try
            {
                if (ProcessOutputCapture.Run(start, TimeSpan.FromSeconds(5)) is not { Succeeded: true } output)
                {
                    return false;
                }

                string? line = output.Stdout.Split('\n').Select(text => text.Trim()).FirstOrDefault(text => text.StartsWith("serveraliveinterval ", StringComparison.OrdinalIgnoreCase));
                return line is not null && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last() == "0";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// agterm's rule for what may reach ssh as its destination: not empty, no whitespace or control characters, and never a
        /// leading '-', which ssh would take as an option. Refused rather than escaped.
        /// </summary>
        public static bool IsHost(string? value)
        {
            return !string.IsNullOrEmpty(value) && !value.StartsWith('-') && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        }

        /// <summary>A command on the machine through ssh, its output read as UTF-8.</summary>
        public static ProcessStartInfo SshStart(string destination, params string[] command)
        {
            if (!IsHost(destination))
            {
                throw new ArgumentException("invalid host");
            }

            ProcessStartInfo start = new ProcessStartInfo(Ssh)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (string argument in Options().Concat(new[] { destination }).Concat(command))
            {
                start.ArgumentList.Add(argument);
            }

            return start;
        }

        /// <summary>
        /// The command line a handed-over overlay's terminal runs: ssh with a terminal, to agshellctl session overlay run-job,
        /// which runs the program on the machine.
        /// </summary>
        public static string JobCommandLine(string destination, string job)
        {
            if (!Guid.TryParse(job, out Guid parsed))
            {
                throw new ArgumentException("invalid overlay job id");
            }

            job = parsed.ToString();
            IEnumerable<string> parts = new[] { Ssh }.Concat(Options(terminal: true)).Concat(new[] { destination, "agshellctl", "session", "overlay", "run-job", job });
            return string.Join(" ", parts.Select(QuoteArgument));
        }

        /// <summary>One argument of a Windows command line, quoted by the rules CommandLineToArgvW reads back.</summary>
        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
            {
                return argument;
            }

            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }

                quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }

            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }

        /// <summary>
        /// agterm's backoff, after the given failures in a row counted from one: 1, 2, 4, 8, 16 then 30 seconds, and past eight
        /// failures doubling on to every 5 minutes, without end.
        /// </summary>
        public static TimeSpan RetryDelay(int failures)
        {
            double cap = failures > 8 ? 300 : 30;
            return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Clamp(failures - 1, 0, 30)), cap));
        }

        /// <summary>
        /// What a failed ssh said, as a row shows it: its last non-empty line, without control characters, at most 200
        /// characters.
        /// </summary>
        public static string? Reason(string? stderr)
        {
            string? last = (stderr ?? string.Empty).Split('\n').Select(line => new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim()).LastOrDefault(line => line.Length > 0);
            if (last is null)
            {
                return null;
            }

            return last.Length > ReasonLimit ? last[..ReasonLimit] : last;
        }

        /// <summary>agterm's probe: proves the machine answers again before a waiting pane is attached anew. Returns why not.</summary>
        public static async Task<(bool Answered, string? Reason)> ProbeAsync(string destination)
        {
            (int status, _, string stderr) = await RunAsync(SshStart(destination, "exit", "0"), TimeSpan.FromSeconds(10));
            return status == 0 ? (true, null) : (false, Reason(stderr));
        }

        /// <summary>
        /// agterm's RemoteCommandProcessRunner: an ssh command bounded by the caller's deadline, since ConnectTimeout ends at
        /// the handshake. Status -1 with agterm's words when it did not start, did not answer in time, or its output did not
        /// close; stdout stays empty then, so a partial answer is never read.
        /// </summary>
        private static async Task<(int Status, string Stdout, string Stderr)> RunAsync(ProcessStartInfo start, TimeSpan deadline)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            ProcessOutput? output;
            try
            {
                output = await ProcessOutputCapture.RunAsync(start, deadline);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return (-1, string.Empty, "could not run " + start.FileName + ": " + ex.Message);
            }

            if (output is { TimedOut: true } || (output is null && elapsed.Elapsed >= deadline))
            {
                return (-1, string.Empty, "the remote did not answer in time");
            }

            return output is { ExitCode: int code } ? (code, output.Stdout, output.Stderr) : (-1, string.Empty, "the remote command output did not close in time");
        }

        /// <summary>
        /// The machines remotes.conf names, one per line as destination [label], # for comments; agterm's file, in the
        /// folder of keymap.conf.
        /// </summary>
        public static List<RemoteMachine> Read(string folder, List<string>? problems = null)
        {
            string path = Path.Combine(folder, "remotes.conf");
            if (Directory.Exists(path))
            {
                problems?.Add(Strings.T("remotes.conf: не файл, а папка"));
                return new List<RemoteMachine>();
            }

            if (!File.Exists(path))
            {
                return new List<RemoteMachine>();
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                problems?.Add("remotes.conf: " + ex.Message);
                return new List<RemoteMachine>();
            }

            List<RemoteMachine> machines = new List<RemoteMachine>();
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                // As agterm's: the address runs to the first whitespace of any kind, the rest of the line is the label.
                int space = line.ToList().FindIndex(char.IsWhiteSpace);
                string destination = space < 0 ? line : line[..space];
                string label = space < 0 ? string.Empty : line[(space + 1)..].Trim();
                string where = Strings.T("remotes.conf, строка {0}: ", index + 1);
                if (!IsHost(destination))
                {
                    problems?.Add(where + Strings.T("не адрес ssh, машина пропущена"));
                    continue;
                }

                if (label.Any(c => (c < ' ' && c != '\t') || c == '\u007f'))
                {
                    problems?.Add(where + Strings.T("в подписи для {0} управляющие символы, машина пропущена", destination));
                    continue;
                }

                if (machines.Any(machine => machine.Destination == destination))
                {
                    problems?.Add(where + Strings.T("{0} уже есть выше, машина пропущена", destination));
                    continue;
                }

                machines.Add(new RemoteMachine(destination, label.Length == 0 ? destination : label));
            }

            return machines;
        }

        /// <summary>
        /// One pane's connection to its daemon on the machine, as agterm's pane runs its own ssh: ssh -G is asked first, off the
        /// window's thread, so a change to the ssh config takes effect on the next attach. Null when ssh does not start.
        /// </summary>
        public static async Task<LiveClient?> ConnectAsync(string destination, string daemon)
        {
            if (!IsHost(destination) || !Guid.TryParse(daemon, out _))
            {
                return null;
            }

            string[] keepalive = await Task.Run(() => Keepalive(destination, daemon));
            LiveClient client = new LiveClient { ClosesWhenEmpty = true };
            return client.ConnectThrough(Ssh, new[] { "-T" }.Concat(BaseOptions).Concat(keepalive).Concat(QuietOptions).Concat(new[] { destination, "agshellctl", "live", "proxy", daemon })) ? client : null;
        }

        /// <summary>agterm's zmx tree over ssh: the sessions the machine's live daemons keep, or why it could not say.</summary>
        public static async Task<(ControlRemoteTree? Tree, string? Error)> ListAsync(string destination)
        {
            (int status, string stdout, string stderr) = await RunAsync(SshStart(destination, "agshellctl", "live", "tree", "--json"), TimeSpan.FromSeconds(10));
            // A failed ssh or agshellctl is never read as a list, whatever it printed: the far side's own refusal comes on
            // stdout, ssh's on stderr.
            if (status != 0)
            {
                string said = stderr.Trim();
                return (null, RemoteError(stdout) ?? (said.Length > 0 ? said : null) ?? "the remote command failed on " + destination);
            }

            ControlResponse response;
            try
            {
                response = ControlJson.DeserializeResponse(stdout.Trim());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is NotSupportedException || ex is ArgumentException)
            {
                return (null, "the remote did not return a readable session list");
            }

            if (!response.Ok)
            {
                return (null, response.Error ?? "the remote session list could not be read");
            }

            // The far side cannot know which name reached it: the destination asked is stamped on here.
            if (response.Result?.Remote is not ControlRemoteTree tree || tree.Sessions is null)
            {
                return (null, "the remote did not return a readable session list");
            }

            return (tree with { Host = destination }, null);
        }

        /// <summary>agterm's remoteError: the error of a not-ok answer the far side printed, null for anything else.</summary>
        private static string? RemoteError(string stdout)
        {
            try
            {
                return ControlJson.DeserializeResponse(stdout.Trim()) is { Ok: false, Error: string error } && error.Trim().Length > 0 ? error.Trim() : null;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is NotSupportedException || ex is ArgumentException)
            {
                return null;
            }
        }
    }
}
