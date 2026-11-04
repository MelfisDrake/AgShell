namespace AgShell.Core
{
    /// <summary>
    /// agterm's RestartReplay: what a session.restart without a command runs, the pane's foreground program again in the
    /// directory it is running in, or why it cannot. Every refusal is decided before anything is ended.
    /// </summary>
    public static class RestartReplay
    {
        public const int MaxCommandBytes = 4096;

        public const string Hint = "; pass --command to name the line. nothing was changed";

        /// <summary>
        /// One read of the pane's foreground: the program with its directory (null when unreadable or not a directory), or
        /// with only shells there the shell holding the pane. Both null when the foreground could not be read.
        /// </summary>
        public readonly record struct Observation((string Executable, string CommandLine, string? Directory)? Program, string? Shell);

        /// <summary>The line to run again and its directory, or the refusal with agterm's words.</summary>
        public static (string? Line, string? Directory, string? Error) Resolve(Observation observation, IReadOnlySet<string> denylist)
        {
            if (observation.Program is not { } found)
            {
                return observation.Shell is string shell
                    ? (null, null, "no replayable foreground program: " + CommandRestore.ProgramName(shell) + " holds the pane" + Hint)
                    : (null, null, "the pane's foreground program cannot be read" + Hint);
            }

            if (string.IsNullOrWhiteSpace(found.CommandLine))
            {
                return (null, null, "the pane's foreground program cannot be read" + Hint);
            }

            string name = CommandRestore.ProgramName(found.Executable);
            if (CommandRestore.IsIdleShellLine(found.Executable, found.CommandLine))
            {
                return (null, null, "no replayable foreground program: " + name + " holds the pane" + Hint);
            }

            if (denylist.Contains(name))
            {
                return (null, null, name + " is on the restore denylist" + Hint);
            }

            if (CommandRestore.HasControlCharacter(found.CommandLine))
            {
                return (null, null, "the foreground program's arguments cannot be replayed" + Hint);
            }

            if (System.Text.Encoding.UTF8.GetByteCount(found.CommandLine) > MaxCommandBytes)
            {
                return (null, null, "the foreground program's command line is too long to replay (max " + MaxCommandBytes + " bytes)" + Hint);
            }

            if (found.Directory is null)
            {
                return (null, null, "the foreground program's working directory is unavailable" + Hint);
            }

            return (found.CommandLine, found.Directory, null);
        }
    }
}
