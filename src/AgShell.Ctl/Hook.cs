using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    /// <summary>
    /// <c>agshellctl hook &lt;state&gt; [args…]</c> is agterm's status wrapper: <c>session status</c> for this terminal's session
    /// and pane, with every further argument passed on as it came. <c>agshellctl hook claude &lt;state&gt; [args…]</c> is agterm's
    /// Claude Code adapter, the wrapper behind the worker guard, and <c>agshellctl hook codex &lt;event&gt;</c> its Codex adapter,
    /// see <see cref="RunCodex"/>. Outside agshell it does nothing; it never prints and always exits 0, because a hook's output
    /// or failure would disturb the agent. AGSHELL_STATUS_WRAPPER, as agterm's AGTERM_STATUS_WRAPPER, names a program the
    /// adapters run instead of the wrapper, with the state and its arguments.
    /// </summary>
    internal static partial class Hook
    {
        public static int Run(string[] args)
        {
            try
            {
                string? session = Environment.GetEnvironmentVariable("AGSHELL_SESSION_ID");
                if (string.IsNullOrEmpty(session) || args.Length == 0)
                {
                    return 0;
                }

                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                switch (args[0])
                {
                    case "codex":
                        RunCodex(session, args.Skip(1).ToArray());
                        break;
                    case "claude":
                        // agterm guards Claude Code's hooks alone: a worker agent under another agent stays silent.
                        if (args.Length > 1 && !AgentProcesses.IsWorkerAgent())
                        {
                            Report(session, args[1..]);
                        }

                        break;
                    default:
                        Status(session, args);
                        break;
                }
            }
            catch (Exception)
            {
                // A status that did not arrive is better than a hook that breaks the agent's turn.
            }

            return 0;
        }

        private static string? Pane => Environment.GetEnvironmentVariable("AGSHELL_PANE") is { Length: > 0 } pane ? pane : null;

        /// <summary>The terminal's stable id, which finds its pane after a swap or a promoted split, where AGSHELL_PANE is stale.</summary>
        private static string? PaneId => Environment.GetEnvironmentVariable("AGSHELL_PANE_ID") is { Length: > 0 } id ? id : null;

        /// <summary>An adapter's report: the wrapper, or the program AGSHELL_STATUS_WRAPPER names.</summary>
        private static void Report(string session, params string[] args)
        {
            if (Environment.GetEnvironmentVariable("AGSHELL_STATUS_WRAPPER") is { Length: > 0 } wrapper)
            {
                ProcessStartInfo start = new ProcessStartInfo(wrapper)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in args)
                {
                    start.ArgumentList.Add(argument);
                }

                using Process? process = Process.Start(start);
                if (process is not null)
                {
                    process.StandardInput.Close();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    process.StandardOutput.ReadToEnd();
                    errors.Wait();
                    process.WaitForExit();
                }
                return;
            }

            Status(session, args);
        }

        /// <summary>
        /// agterm's wrapper: <c>session status STATE --target SESSION [--pane P] [--pane-id ID] ARGS…</c>, the pane and its id from
        /// the terminal's environment, which tells the app whose agent reports, so a block in one pane is not erased by another.
        /// </summary>
        private static void Status(string session, string[] args)
        {
            List<string> argv = new List<string> { "session", "status", args[0], "--target", session };
            // A pane the caller names itself wins, as the last of a repeated option does in agterm's parser.
            if (Pane is string pane && !args.Contains("--pane"))
            {
                argv.Add("--pane");
                argv.Add(pane);
            }

            if (PaneId is string paneId && !args.Contains("--pane-id"))
            {
                argv.Add("--pane-id");
                argv.Add(paneId);
            }

            argv.AddRange(args.Skip(1));
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.CommandLine.ParseResult parsed = Commands.Build().Parse(argv);
            if (parsed.Errors.Count == 0 && Commands.UnknownOption(parsed, argv.ToArray()) is null)
            {
                parsed.Invoke();
            }
        }

        /// <summary>
        /// agterm's Codex adapter. Codex fires PermissionRequest before its Auto Review decides whether a person must
        /// approve, so that event alone would false-flag automatically reviewed tools: a watcher per pane reads the screen
        /// and reports blocked only while a real approval prompt is visible. Stop checks the last answer for a question.
        /// </summary>
        private static void RunCodex(string session, string[] args)
        {
            string action = args.Length > 0 ? args[0] : string.Empty;
            if (action == "__watch-blocked")
            {
                WatchForBlocker(session, args.Length > 1 ? args[1] : string.Empty, args.Length > 2 ? args[2] : string.Empty);
                return;
            }

            switch (action)
            {
                case "session-start":
                    StopWatcher(session);
                    Report(session, "idle");
                    break;
                case "user-prompt-submit":
                    Report(session, "active", "--blink");
                    StartWatcher(session);
                    break;
                case "pre-tool-use":
                case "post-tool-use":
                    Report(session, "active", "--blink");
                    break;
                case "permission-request":
                    // A candidate only: the watcher reports blocked if Codex then leaves a prompt for the user.
                    StartWatcher(session);
                    break;
                case "stop":
                    StopWatcher(session);
                    if (AsksQuestion(LastAssistantMessage()))
                    {
                        Report(session, "blocked");
                    }
                    else
                    {
                        Report(session, "completed", "--auto-reset");
                    }

                    break;
            }
        }

        /// <summary>The footer texts of Codex's approval and question dialogs, lower case.</summary>
        private static readonly string[] PromptFooters = { "press enter to confirm", "enter to submit answer", "enter to submit all", "allow command?" };

        /// <summary>As agterm's: AGSHELL_CODEX_WATCH_FILE, else one file per terminal, by its stable id before its pane.</summary>
        private static string WatchFile(string session)
        {
            if (Environment.GetEnvironmentVariable("AGSHELL_CODEX_WATCH_FILE") is { Length: > 0 } file)
            {
                return file;
            }

            string key = Regex.Replace(session + "-" + (PaneId ?? Pane ?? "left"), "[^A-Za-z0-9._-]", "_");
            return Path.Combine(Path.GetTempPath(), "agshell-codex-watch-" + key);
        }

        private static void StopWatcher(string session)
        {
            File.Delete(WatchFile(session));
        }

        /// <summary>Starts the watcher as a process of its own; a newer token in the file tells an older one to quit.</summary>
        private static void StartWatcher(string session)
        {
            string file = WatchFile(session);
            string token = Environment.ProcessId + "-" + Random.Shared.Next();
            File.WriteAllText(file, token);
            if (DetachedProcess.Start(Environment.ProcessPath!, "hook", "codex", "__watch-blocked", token, file) is not null && Current(file, token))
            {
                File.Delete(file);
            }
        }

        private static void WatchForBlocker(string session, string token, string file)
        {
            if (token.Length == 0 || file.Length == 0)
            {
                return;
            }

            // Half a second for three hours, as agterm's watcher; AGSHELL_CODEX_WATCH_MAX_CHECKS and _INTERVAL (seconds) change it.
            int maxChecks = int.TryParse(Environment.GetEnvironmentVariable("AGSHELL_CODEX_WATCH_MAX_CHECKS"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int checks) ? checks : 21600;
            TimeSpan interval = double.TryParse(Environment.GetEnvironmentVariable("AGSHELL_CODEX_WATCH_INTERVAL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromMilliseconds(500);
            bool promptVisible = false;
            for (int check = 0; check < maxChecks; check++)
            {
                if (!Current(file, token) || ReadScreen(session) is not string screen)
                {
                    return;
                }

                string footer = string.Join("\n", screen.Split('\n').TakeLast(12)).ToLowerInvariant();
                if (PromptFooters.Any(footer.Contains))
                {
                    // A lifecycle event may have replaced the token while the screen was read; its status wins.
                    if (!promptVisible && Current(file, token))
                    {
                        Report(session, "blocked");
                        promptVisible = true;
                    }
                }
                else
                {
                    // A dialog answered with a denial fires no further tool event, so the watcher restores active itself.
                    if (promptVisible)
                    {
                        Report(session, "active", "--blink");
                    }

                    promptVisible = false;
                }

                Thread.Sleep(interval);
            }
        }

        private static bool Current(string file, string token)
        {
            try
            {
                return File.ReadAllText(file).Trim() == token;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static string? ReadScreen(string session)
        {
            ControlRequest request = new ControlRequest { Cmd = "session.text", Target = session, Args = new ControlArgs { Pane = Pane, PaneId = PaneId } };
            ControlReply reply = ControlClient.SendAsync(ControlEndpoint.ResolvePipe(null), request, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            return reply.Response.Ok ? reply.Response.Result?.Text ?? string.Empty : null;
        }

        /// <summary>Codex hands the Stop hook its payload on stdin; the answer is its last_assistant_message.</summary>
        private static string LastAssistantMessage()
        {
            if (!Console.IsInputRedirected)
            {
                return string.Empty;
            }

            try
            {
                using JsonDocument payload = JsonDocument.Parse(Console.In.ReadToEnd());
                return payload.RootElement.TryGetProperty("last_assistant_message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Whether the answer's prose asks something: a ? ending a word, outside fenced code. Inline code counts as a word,
        /// so "run `make test`?" still asks, and a ? that opens quoted text ("? for shortcuts") does not.
        /// </summary>
        internal static bool AsksQuestion(string message)
        {
            List<string> prose = new List<string>();
            bool fenced = false;
            foreach (string line in message.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    fenced = !fenced;
                    continue;
                }

                if (!fenced)
                {
                    prose.Add(InlineCode().Replace(line, "code"));
                }
            }

            string text = QuotedMark().Replace(string.Join("\n", prose), "$1$2");
            return Question().IsMatch(text);
        }

        [GeneratedRegex("`[^`]*`")]
        private static partial Regex InlineCode();

        [GeneratedRegex("(^|\\s)([“‘\"'(\\[*_]+)\\?", RegexOptions.Multiline)]
        private static partial Regex QuotedMark();

        [GeneratedRegex("\\S\\?[\\]\"')*_!’”]*(\\s|$)", RegexOptions.Multiline)]
        private static partial Regex Question();
    }
}
