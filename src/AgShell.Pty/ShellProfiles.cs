using System.Diagnostics;
using System.Text;
using AgShell.Protocol;

namespace AgShell.Pty
{
    public enum ShellKind
    {
        PowerShell,
        Cmd,
        Wsl,
    }

    public sealed record ShellProfile(string Id, string Name, ShellKind Kind, string Executable, string? Distribution = null);

    /// <summary>
    /// Finds installed shells and builds their start options. Every shell is started with a small integration that reports
    /// the current directory after each command: OSC 7 as ghostty's shell integration sends it (kitty-shell-cwd://host/path),
    /// and cmd OSC 9;9 (the Windows Terminal convention), since its PROMPT has the path only as $P, unencoded.
    /// </summary>
    public static class ShellProfiles
    {
        public const string PowerShellCoreId = "pwsh";
        public const string WindowsPowerShellId = "powershell";
        public const string CmdId = "cmd";
        public const string WslIdPrefix = "wsl:";

        private const string PowerShellIntegration = """
            $global:__agshellOriginalPrompt = $function:prompt
            function global:prompt {
                $location = $executionContext.SessionState.Path.CurrentLocation
                $text = & $global:__agshellOriginalPrompt
                if ($location.Provider.Name -eq 'FileSystem') {
                    return "$([char]27)]7;kitty-shell-cwd://$env:COMPUTERNAME/$($location.ProviderPath -replace '\\', '/')$([char]7)" + $text
                }
                return $text
            }
            """;

        /// <summary>Starts the user's login shell interactive with a line to run first, as agterm's `$SHELL -lic`.</summary>
        private const string WslReplayLauncher = """s=${SHELL:-$(getent passwd "$(id -un)" | cut -d: -f7)}; export SHELL="${s:-/bin/sh}"; exec "$SHELL" -lic "$1" """;

        private const string BashPromptCommand = """builtin printf "\e]7;kitty-shell-cwd://%s%s\a" "$HOSTNAME" "$PWD" """;

        public static IReadOnlyList<ShellProfile> Detect()
        {
            List<ShellProfile> profiles = new List<ShellProfile>();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);

            string? pwsh = FindOnPath("pwsh.exe") ?? ExistingOrNull(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
            if (pwsh is not null)
            {
                profiles.Add(new ShellProfile(PowerShellCoreId, "PowerShell", ShellKind.PowerShell, pwsh));
            }

            string? windowsPowerShell = ExistingOrNull(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
            if (windowsPowerShell is not null)
            {
                profiles.Add(new ShellProfile(WindowsPowerShellId, "Windows PowerShell", ShellKind.PowerShell, windowsPowerShell));
            }

            string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(system, "cmd.exe");
            profiles.Add(new ShellProfile(CmdId, "Command Prompt", ShellKind.Cmd, cmd));

            string? wsl = ExistingOrNull(Path.Combine(system, "wsl.exe"));
            if (wsl is not null)
            {
                foreach (string distribution in ListWslDistributions(wsl))
                {
                    profiles.Add(new ShellProfile(WslIdPrefix + distribution, distribution, ShellKind.Wsl, wsl, distribution));
                }
            }

            return profiles;
        }

        /// <summary>
        /// The shell's start options. <paramref name="replay"/>, a line in the shell's own syntax, is agterm's replay script:
        /// the shell runs it as its first command when it is created and stays interactive after it, also when Ctrl-C ends
        /// it, so the line is in no history and does not wait for a prompt.
        /// </summary>
        public static PtyStartOptions CreateStartOptions(ShellProfile profile, string? cwd, int columns, int rows, IReadOnlyDictionary<string, string?>? environment = null, string? replay = null)
        {
            Dictionary<string, string?> variables = environment is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string commandLine;
            string workingDirectory;

            switch (profile.Kind)
            {
                case ShellKind.PowerShell:
                    // -NoExit keeps the shell after the line, and after a Ctrl-C that stops it.
                    string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(replay is null ? PowerShellIntegration : PowerShellIntegration + "\n" + replay));
                    commandLine = Quote(profile.Executable) + " -NoLogo -NoExit -EncodedCommand " + encoded;
                    workingDirectory = ExistingDirectoryOr(cwd, home);
                    break;
                case ShellKind.Cmd:
                    string prompt = Environment.GetEnvironmentVariable("PROMPT") ?? "$P$G";
                    variables["PROMPT"] = "$e]9;9;$P$e\\" + prompt;
                    // /k runs the line and stays; /s takes the quotes around it off as they are.
                    commandLine = replay is null ? Quote(profile.Executable) : Quote(profile.Executable) + " /s /k \"" + replay + "\"";
                    workingDirectory = ExistingDirectoryOr(cwd, home);
                    break;
                case ShellKind.Wsl:
                    string linuxDirectory = string.IsNullOrWhiteSpace(cwd) || !cwd.StartsWith('/') ? "~" : Quote(cwd);
                    // wsl.exe takes the -d value verbatim, quotes included; distribution names cannot contain spaces.
                    commandLine = Quote(profile.Executable) + " -d " + profile.Distribution + " --cd " + linuxDirectory;
                    if (replay is not null)
                    {
                        // The user's login shell runs the line first and then becomes the interactive shell; an interactive
                        // shell drops the rest of a list when Ctrl-C ends its command, so the trap goes on to it then.
                        commandLine += " -e sh -c " + QuoteArgument(WslReplayLauncher) + " sh " + QuoteArgument("trap 'exec \"$SHELL\" -il' INT; " + replay + "\nexec \"$SHELL\" -il");
                    }

                    variables["PROMPT_COMMAND"] = BashPromptCommand;
                    // Linux sees only the variables WSLENV lists. PATH is left out: WSL builds its own and appends the Windows one.
                    IEnumerable<string> forwarded = variables
                        .Where(pair => pair.Value is not null && !string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase))
                        .Select(pair => pair.Key + "/u");
                    string? wslEnv = Environment.GetEnvironmentVariable("WSLENV");
                    variables["WSLENV"] = string.Join(":", new[] { wslEnv }.Where(value => !string.IsNullOrEmpty(value)).Concat(forwarded));
                    workingDirectory = home;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, "Unknown shell kind.");
            }

            return new PtyStartOptions
            {
                CommandLine = commandLine,
                WorkingDirectory = workingDirectory,
                Columns = columns,
                Rows = rows,
                Environment = variables,
            };
        }

        /// <summary>
        /// Runs one command rather than an interactive shell, as agterm's overlay, scratch and session --command do through
        /// /bin/sh -c: here through cmd.exe /d /s /c, whose exit status is the command's, and for a WSL profile through sh -c
        /// inside the same distribution, so programs installed there are found and the line is POSIX shell as in agterm.
        /// </summary>
        public static PtyStartOptions CreateCommandOptions(ShellProfile profile, string command, string? cwd, int columns, int rows, IReadOnlyDictionary<string, string?>? environment = null)
        {
            Dictionary<string, string?> variables = environment is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Kind != ShellKind.Wsl)
            {
                return new PtyStartOptions
                {
                    CommandLine = Quote(Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")) + " /d /s /c \"" + command + "\"",
                    WorkingDirectory = ExistingDirectoryOr(cwd, home),
                    Columns = columns,
                    Rows = rows,
                    Environment = variables,
                };
            }

            string linuxDirectory = string.IsNullOrWhiteSpace(cwd) || !cwd.StartsWith('/') ? "~" : Quote(cwd);
            IEnumerable<string> forwarded = variables
                .Where(pair => pair.Value is not null && !string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key + "/u");
            string? wslEnv = Environment.GetEnvironmentVariable("WSLENV");
            variables["WSLENV"] = string.Join(":", new[] { wslEnv }.Where(value => !string.IsNullOrEmpty(value)).Concat(forwarded));
            return new PtyStartOptions
            {
                CommandLine = Quote(profile.Executable) + " -d " + profile.Distribution + " --cd " + linuxDirectory + " -e sh -c " + QuoteArgument(command),
                WorkingDirectory = home,
                Columns = columns,
                Rows = rows,
                Environment = variables,
            };
        }

        private static IReadOnlyList<string> ListWslDistributions(string wsl)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(wsl, "-l -q")
            {
                StandardOutputEncoding = Encoding.Unicode,
                CreateNoWindow = true,
            };

            try
            {
                if (ProcessOutputCapture.Run(startInfo, TimeSpan.FromSeconds(5)) is not { Succeeded: true } output)
                {
                    return Array.Empty<string>();
                }

                return output.Stdout
                    .Split('\n')
                    .Select(line => line.Trim('\0', '\r', ' ', '\t'))
                    .Where(line => line.Length > 0 && !line.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return Array.Empty<string>();
            }
        }

        private static string ExistingDirectoryOr(string? directory, string fallback)
        {
            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : fallback;
        }

        private static string? ExistingOrNull(string path)
        {
            return File.Exists(path) ? path : null;
        }

        private static string? FindOnPath(string fileName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        /// <summary>One argument of a Windows command line, quoted by the rules CommandLineToArgvW reads back.</summary>
        private static string QuoteArgument(string argument)
        {
            System.Text.StringBuilder quoted = new System.Text.StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(c);
            }

            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }
}
