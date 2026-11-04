namespace AgShell.Core
{
    /// <summary>
    /// agterm's restore-running-command, host-free: whether a pane's foreground program is worth starting again, and
    /// how to type it back into a fresh shell. The window only reads the process tree.
    /// </summary>
    public static class CommandRestore
    {
        public const string None = "none";
        public const string Rerun = "rerun";
        public const string Live = "live";

        /// <summary>agterm's hasControlCharacter: a C0 control or DEL, which would smuggle a line or an escape into the shell.</summary>
        public static bool HasControlCharacter(string value)
        {
            return value.Any(character => character < 0x20 || character == 0x7F);
        }

        public static readonly IReadOnlyList<string> Modes = new[] { None, Rerun, Live };

        /// <summary>Shells and console plumbing: one of these in the foreground means nothing to restore.</summary>
        private static readonly HashSet<string> Shells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pwsh", "powershell", "cmd", "bash", "zsh", "sh", "dash", "fish", "ksh", "tcsh", "nu", "wsl", "wslhost", "conhost", "openconsole", "login",
        };

        public const string DenylistStarter = """
            # Programs agshell does not start again when it restores sessions with Restore sessions = re-run,
            # one name per line, as the process is called (claude, ssh, vim); case does not matter.
            """;

        /// <summary>The program's name without directory or .exe, lower case: how the denylist and the shell set name it.</summary>
        public static string ProgramName(string executable)
        {
            string name = executable.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..].TrimStart('-');
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        public static bool IsShell(string executable)
        {
            return Shells.Contains(ProgramName(executable));
        }

        /// <summary>
        /// agterm's isIdleShell: a shell with only option flags after it holds nothing to restore. One running a script
        /// (bash ./run.sh, pwsh -File x.ps1) or a -c line is a program. cmd and PowerShell also take /flags.
        /// </summary>
        public static bool IsIdleShell(IReadOnlyList<string> argv)
        {
            if (argv.Count == 0 || !IsShell(argv[0]))
            {
                return false;
            }

            string name = ProgramName(argv[0]).ToLowerInvariant();
            // Console plumbing (wsl.exe, conhost) is looked through whatever it was given, as before.
            if (name is "wsl" or "wslhost" or "conhost" or "openconsole" or "login")
            {
                return true;
            }

            bool slashFlags = name is "cmd" or "pwsh" or "powershell";
            return argv.Skip(1).All(argument => argument.StartsWith('-') || (slashFlags && argument.StartsWith('/')));
        }

        /// <summary>The words of a saved line: "double" (Windows) and 'single' (POSIX) quotes group, spaces part.</summary>
        public static List<string> SplitLine(string line)
        {
            List<string> words = new List<string>();
            System.Text.StringBuilder word = new System.Text.StringBuilder();
            char? quote = null;
            bool any = false;
            foreach (char character in line)
            {
                if (quote is char open)
                {
                    if (character == open)
                    {
                        quote = null;
                    }
                    else
                    {
                        word.Append(character);
                    }
                }
                else if (character is '"' or '\'')
                {
                    quote = character;
                    any = true;
                }
                else if (character is ' ' or '\t')
                {
                    if (any || word.Length > 0)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        any = false;
                    }
                }
                else
                {
                    word.Append(character);
                }
            }

            if (any || word.Length > 0)
            {
                words.Add(word.ToString());
            }

            return words;
        }

        public static HashSet<string> ParseDenylist(string text)
        {
            return text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && line[0] != '#')
                .Select(ProgramName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a captured command comes back: not a shell, not on the user's denylist, and with no control characters
        /// that would type something other than the command.
        /// </summary>
        /// <summary><see cref="IsIdleShell"/> for a saved line whose program is <paramref name="executable"/>.</summary>
        public static bool IsIdleShellLine(string executable, string commandLine)
        {
            List<string> argv = SplitLine(commandLine);
            if (argv.Count == 0)
            {
                argv.Add(executable);
            }

            argv[0] = executable;
            return IsIdleShell(argv);
        }

        public static bool ShouldRestore(string executable, string commandLine, IReadOnlySet<string> denylist)
        {
            // agterm's isUnreplayable: a control character, or the U+FFFD a bad byte in the argv decodes to.
            return !string.IsNullOrWhiteSpace(commandLine)
                && !IsIdleShellLine(executable, commandLine)
                && !denylist.Contains(ProgramName(executable))
                && !commandLine.Any(character => char.IsControl(character) || character == '\uFFFD');
        }

        /// <summary>
        /// The same check for a capture saved as a line: the denylist is applied when the capture is replayed, as agterm's
        /// launch does, so a program added to it since the quit does not come back. The program is the line's first word.
        /// </summary>
        public static bool ShouldRestoreLine(string commandLine, IReadOnlySet<string> denylist)
        {
            return SplitLine(commandLine) is { Count: > 0 } argv && ShouldRestore(argv[0], commandLine, denylist);
        }

        /// <summary>
        /// agterm's restoreInput: a pin, when there is one, typed as written (empty or outside re-run, nothing); else the
        /// capture, already checked against the denylist.
        /// </summary>
        public static string? RestoreInput(bool restoreEnabled, string? pin, string? captured)
        {
            if (pin is null)
            {
                return captured;
            }

            return restoreEnabled && pin.Length > 0 && !HasControlCharacter(pin) ? pin : null;
        }

        /// <summary>
        /// agterm's restorePlan, what a starting main or split pane runs: a pin wins over everything and is only ever typed;
        /// otherwise a capture (even one the denylist dropped, which leaves a plain shell) wins over the session's command,
        /// and a restored session runs that command only in re-run mode, a new one always.
        /// </summary>
        public static RestorePlan Plan(RestoreInputs inputs)
        {
            if (inputs.Pin is not null)
            {
                return new RestorePlan(null, RestoreInput(inputs.RestoreEnabled, inputs.Pin, inputs.CapturedInput), false);
            }

            bool mayRunCommand = !inputs.WasRestored || inputs.RestoreEnabled;
            string? command = !inputs.HadCapture && mayRunCommand ? inputs.Command : null;
            return new RestorePlan(command, command is null ? inputs.CapturedInput : null, command is not null && inputs.Wait);
        }

        /// <summary>The line typed into a fresh shell: PowerShell needs the call operator before a quoted program path.</summary>
        public static string ForShell(string profileId, string commandLine)
        {
            string line = commandLine.Trim();
            bool powerShell = profileId is "pwsh" or "powershell";
            return powerShell && line.StartsWith('"') ? "& " + line : line;
        }

        /// <summary>Joins a Linux argv into one line a POSIX shell reads back as the same words.</summary>
        public static string PosixJoin(IEnumerable<string> argv)
        {
            return string.Join(" ", argv.Select(argument => argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./=:,@+%".Contains(c))
                ? argument
                : "'" + argument.Replace("'", "'\\''") + "'"));
        }
    }

    /// <summary>What agterm's restorePlan decides from: <see cref="CommandRestore.Plan"/>.</summary>
    /// <param name="WasRestored">The session came from a snapshot rather than being created now.</param>
    /// <param name="RestoreEnabled">This launch's restore mode is re-run.</param>
    /// <param name="HadCapture">A capture was armed for the pane, whether or not the denylist let it through.</param>
    /// <param name="CapturedInput">The capture to type, or null when there is none or it was refused.</param>
    /// <param name="Command">The pane's session command (session new --command).</param>
    /// <param name="Pin">The pane's pin as at launch: null none, empty a plain shell.</param>
    /// <param name="Wait">The command holds its output when it exits.</param>
    public sealed record RestoreInputs(bool WasRestored, bool RestoreEnabled, bool HadCapture, string? CapturedInput, string? Command, string? Pin, bool Wait);

    /// <summary>A pane starts its command in place of a shell, or a shell with a line typed into it, or a plain shell.</summary>
    public sealed record RestorePlan(string? Command, string? Input, bool Wait);
}
