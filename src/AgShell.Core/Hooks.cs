using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>A shell line run on every event of a kind.</summary>
    public sealed record HookLine(string Kind, string Command)
    {
        /// <summary>Its line in hooks.conf. Not part of its identity: a hook moved in the file keeps its queue and counters.</summary>
        public int Number { get; init; }

        public bool Equals(HookLine? other)
        {
            return other is not null && Kind == other.Kind && Command == other.Command;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Kind, Command);
        }
    }

    /// <summary>Reads hooks.conf, agterm's format: one verb, on &lt;kind&gt; &lt;shell line&gt;.</summary>
    public static class HooksConfig
    {
        public static (List<HookLine> Lines, List<string> Diagnostics) Parse(string text)
        {
            List<HookLine> lines = new List<HookLine>();
            List<string> diagnostics = new List<string>();
            string[] rows = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int number = 0; number < rows.Length; number++)
            {
                string row = rows[number].Trim();
                if (row.Length == 0 || row.StartsWith('#'))
                {
                    continue;
                }

                string where = "line " + (number + 1) + ": ";
                string[] parts = row.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] != "on")
                {
                    diagnostics.Add(where + "unknown directive " + parts[0] + " (on)");
                    continue;
                }

                if (parts.Length < 3)
                {
                    diagnostics.Add(where + "on needs an event kind and a shell line");
                    continue;
                }

                if (!ControlEventKinds.All.Contains(parts[1]))
                {
                    diagnostics.Add(where + "unknown event kind " + parts[1] + " (" + string.Join(", ", ControlEventKinds.All) + ")");
                    continue;
                }

                HookLine line = new HookLine(parts[1], parts[2].Trim()) { Number = number + 1 };
                if (lines.Contains(line))
                {
                    diagnostics.Add(where + "the same hook is already defined; skipped");
                    continue;
                }

                lines.Add(line);
            }

            return (lines, diagnostics);
        }

        /// <summary>The commented starter written on first launch.</summary>
        public const string Starter = """
            # agshell hooks — the format of agterm's hooks.conf. Saved changes apply at once.
            # on <kind> <shell line>   (run by cmd.exe without a window, one at a time per line)
            # Kinds: status, notify, session.created, session.closed, session.selected,
            #        pane.split, pane.scratch, tree.changed, app.launched.
            # The event arrives as one JSON line on stdin; AGSHELL_EVENT_KIND, AGSHELL_EVENT_STATUS,
            # AGSHELL_SESSION_ID, AGSHELL_WORKSPACE_ID and AGSHELL_PIPE are set.
            #
            # on status          C:\scripts\on-status.cmd
            # on session.closed  echo %AGSHELL_SESSION_ID% >> %TEMP%\agshell-closed.log
            """;
    }

    /// <summary>agterm's CommandContext: the {AGSHELL_*} tokens of a custom command, expanded raw into its shell line.</summary>
    public static class CommandTokens
    {
        public static readonly IReadOnlyList<string> Names = new[]
        {
            "AGSHELL_SESSION_ID", "AGSHELL_SESSION_NAME", "AGSHELL_SESSION_PWD", "AGSHELL_SESSION_HOST", "AGSHELL_WORKSPACE_ID", "AGSHELL_WORKSPACE_NAME",
            "AGSHELL_WINDOW_ID", "AGSHELL_WINDOW_NAME", "AGSHELL_PANE", "AGSHELL_PANE_ID", "AGSHELL_SELECTION", "AGSHELL_PIPE",
        };

        /// <summary>
        /// agterm's session-scoped token bases: with no session each expands empty, which is dangerous (an empty
        /// {AGSHELL_SESSION_PWD} turns a delete of its contents into one of the drive's root). The pipe, window and pane resolve
        /// without a session, which keeps a launcher command firable in an emptied window.
        /// </summary>
        public static readonly IReadOnlyList<string> SessionScopedBases = new[] { "AGSHELL_SESSION", "AGSHELL_WORKSPACE", "AGSHELL_SELECTION" };

        /// <summary>Whether a command names a session-scoped token in any form, {X}, %X% or $X: a plain substring match.</summary>
        public static bool ReferencesSessionScopedContext(string shell)
        {
            return SessionScopedBases.Any(name => shell.Contains(name, StringComparison.Ordinal));
        }

        /// <summary>
        /// agterm's expand: each {X} of a known token becomes its value, an unknown {...} stays. One pass, so a value that
        /// itself holds a {X}, such as a selection, is not expanded again.
        /// </summary>
        public static string Expand(string shell, IReadOnlyDictionary<string, string> values)
        {
            System.Text.StringBuilder result = new System.Text.StringBuilder();
            int index = 0;
            while (index < shell.Length)
            {
                int open = shell.IndexOf('{', index);
                if (open < 0)
                {
                    break;
                }

                int close = shell.IndexOf('}', open + 1);
                if (close < 0)
                {
                    break;
                }

                result.Append(shell, index, open - index);
                string name = shell[(open + 1)..close];
                result.Append(Names.Contains(name) ? values.GetValueOrDefault(name, string.Empty) : shell[open..(close + 1)]);
                index = close + 1;
            }

            return result.Append(shell, index, shell.Length - index).ToString();
        }
    }
}
