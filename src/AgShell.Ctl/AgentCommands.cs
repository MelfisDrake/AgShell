using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    internal static partial class Commands
    {
        private const int WaitClosedExitCode = 2;
        private const int WaitTimeoutExitCode = 124;
        private static readonly string[] StatusNames = { "idle", "active", "completed", "blocked" };

        private static Command Status()
        {
            Argument<string> state = new Argument<string>("state") { Description = "State: idle, active, completed, or blocked." };
            Option<bool> blink = new Option<bool>("--blink") { Description = "Pulse the indicator for attention." };
            Option<bool> autoReset = new Option<bool>("--auto-reset") { Description = "Reset the indicator to idle once the session is visited." };
            Option<string?> pane = new Option<string?>("--pane") { Description = "Which pane set this status: primary/left/top, split/right/bottom, or scratch. Records the blocked pane so navigation reaches it. Defaults to primary." };
            Option<string?> sound = new Option<string?>("--sound") { Description = "Play a sound when set: 'default' (or 'beep') for the system alert sound, or a sound of C:\\Windows\\Media by file name without .wav." };
            Option<string?> paneId = new Option<string?>("--pane-id") { Description = "A surface's stable token (the shell's $AGSHELL_PANE_ID) — the agent-status hook forwards it so a status set from a promoted-then-re-split pane lands on the pane's current slot. Overrides --pane when it resolves; falls back to --pane otherwise." };
            Option<string?> color = new Option<string?>("--color") { Description = "Override the glyph tint for this call only (#rrggbb); reverts on the next status set without it." };
            Option<string?> shape = new Option<string?>("--shape") { Description = "Override the glyph silhouette for this call only: " + string.Join(", ", ControlStatusNames.Shapes) + "; reverts on the next status set without it." };
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command command = new CtlCommand("status", "Set a session's agent status indicator.") { state, blink, autoReset, sound, color, shape, pane, paneId, target };
            command.Check(
                parsed => parsed.GetValue(color) is string hex && !Checks.IsHexColor(hex) ? "color must be a #rrggbb hex value" : null,
                parsed => parsed.GetValue(shape) is string name && !ControlStatusNames.Shapes.Contains(name) ? "shape must be one of: " + string.Join(", ", ControlStatusNames.Shapes) : null,
                Checks.Pane(pane));
            command.SetAction(result => Send(result, "session.status", result.GetValue(target), new ControlArgs
            {
                Status = result.GetValue(state),
                Blink = Flag(result, blink),
                AutoReset = Flag(result, autoReset),
                Pane = result.GetValue(pane),
                Sound = result.GetValue(sound),
                Color = result.GetValue(color),
                Shape = result.GetValue(shape),
                PaneId = result.GetValue(paneId),
            }));
            return command;
        }

        /// <summary>agterm's session context: what the session is for, in the title bar.</summary>
        private static Command Context()
        {
            Argument<string?> text = new Argument<string?>("text") { Description = "One line, up to 256 bytes: a PR, an issue, the task.", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> clear = new Option<bool>("--clear") { Description = "Remove the context." };
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command command = new CtlCommand("context", "Set what a session is for, shown in the title bar.") { text, clear, target };
            command.Validators.Add(parsed =>
            {
                if ((parsed.GetValue(text) is not null) == parsed.GetValue(clear))
                {
                    parsed.AddError("provide exactly one of a TEXT or --clear");
                }
            });
            command.SetAction(result => Send(result, "session.context", result.GetValue(target), new ControlArgs
            {
                Text = result.GetValue(text),
                Mode = result.GetValue(clear) ? "clear" : "set",
            }));
            return command;
        }

        /// <summary>agterm's session background: an image, a word or two of text, or a color behind the terminal.</summary>
        private static Command Background()
        {
            Command background = new CtlCommand("background", "Set or clear what is drawn behind a session's terminal; with --pane, that pane's own.");
            background.Subcommands.Add(BackgroundForm("image", "path", "Show a PNG or JPEG behind the terminal.", image: true));
            background.Subcommands.Add(BackgroundForm("text", "text", "Show a word or two (up to 256 characters) behind the terminal.", text: true));
            background.Subcommands.Add(BackgroundForm("color", "color", "Give the terminal a solid #rrggbb background."));
            background.Subcommands.Add(BackgroundForm("clear", null, "Remove the background; with --pane, the pane inherits the session's again."));
            return background;
        }

        private static Command BackgroundForm(string mode, string? valueName, string description, bool image = false, bool text = false)
        {
            CtlCommand command = new CtlCommand(mode, description);
            Argument<string>? value = valueName is null ? null : new Argument<string>(valueName);
            Option<double?> opacity = new Option<double?>("--opacity") { Description = "0 to 1 (default 1)." };
            Option<string?> fit = new Option<string?>("--fit") { Description = "contain (default), cover, stretch or none." };
            Option<string?> position = new Option<string?>("--position") { Description = "top-left, top-center, top-right, center-left, center (default), center-right, bottom-left, bottom-center or bottom-right." };
            Option<bool> repeat = new Option<bool>("--repeat") { Description = "Tile the image." };
            Option<string?> color = new Option<string?>("--color") { Description = "The text's #rrggbb color (default: the terminal's text color)." };
            Option<string?> pane = PaneOption("Set a pane's own background: left, right or scratch.");
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            if (value is not null)
            {
                command.Add(value);
            }

            if (text)
            {
                command.Add(color);
            }

            if (image || text)
            {
                command.Add(opacity);
                command.Add(fit);
                command.Add(position);
            }

            if (image)
            {
                command.Add(repeat);
            }

            command.Add(pane);
            command.Add(target);
            // agterm's Background.validate with the values the form carries, then the pane.
            command.Check(
                parsed =>
                {
                    string? given = value is null ? null : parsed.GetValue(value);
                    return image ? Checks.Background(parsed.GetValue(fit), parsed.GetValue(position), parsed.GetValue(opacity), path: given)
                        : text ? Checks.Background(parsed.GetValue(fit), parsed.GetValue(position), parsed.GetValue(opacity), parsed.GetValue(color), given)
                        : mode == "color" ? Checks.Background(color: given)
                        : null;
                },
                Checks.Pane(pane));
            command.SetAction(result =>
            {
                string? given = value is null ? null : result.GetValue(value);
                // A relative path is the caller's; a /… path is left for agshell to read as the session's WSL file.
                if (image && given is not null && !given.StartsWith('/'))
                {
                    given = Path.GetFullPath(given);
                }

                return Send(result, "session.background", result.GetValue(target), new ControlArgs
                {
                    Mode = mode,
                    // agterm's fields: the image in path, the text in text, the solid color (or the text's color) in color.
                    Path = image ? given : null,
                    Text = text ? given : null,
                    Opacity = result.GetValue(opacity),
                    Fit = result.GetValue(fit),
                    Position = result.GetValue(position),
                    Repeat = Flag(result, repeat),
                    Color = mode == "color" ? given : result.GetValue(color),
                    Pane = result.GetValue(pane),
                });
            });
            return command;
        }

        private static Command Notify()
        {
            Argument<string> body = new Argument<string>("body") { Description = "Notification text." };
            Option<string?> title = new Option<string?>("--title") { Description = "Notification title (defaults to the session name)." };
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command command = new CtlCommand("notify", "Post a desktop notification for a session.") { body, title, target };
            command.SetAction(result => Send(result, "notify", result.GetValue(target), new ControlArgs { Body = result.GetValue(body), Title = result.GetValue(title) }));
            return command;
        }

        private static Command Wait()
        {
            Option<string[]> until = new Option<string[]>("--until") { Description = "Status to wait for (idle, active, completed, blocked); repeat for several." };
            Option<double?> timeout = new Option<double?>("--timeout") { Description = "Seconds to wait before exiting 124; omit to wait indefinitely." };
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command command = new CtlCommand("wait", "Block until a session's agent status is one of the given values. Exit 0 on a match, 2 when the session closes, 124 on timeout.")
            {
                until, timeout, target,
            };
            command.Check(
                parsed => Statuses(parsed.GetValue(until)).Count == 0 ? "provide at least one --until status" : null,
                parsed => Statuses(parsed.GetValue(until)).FirstOrDefault(status => !StatusNames.Contains(status)) is string invalid ? "invalid status: " + invalid + " (expected idle, active, completed or blocked)" : null,
                parsed => parsed.GetValue(timeout) is double seconds && !(double.IsFinite(seconds) && seconds > 0) ? "--timeout must be a positive number of seconds" : null);
            command.SetAction(result => RunWait(result, Statuses(result.GetValue(until)), result.GetValue(timeout), result.GetValue(target)!));
            return command;
        }

        private static Command Events()
        {
            Option<string[]> kinds = new Option<string[]>("--kind") { HelpName = "kind-fields", Description = "Event kind (" + string.Join(", ", ControlEventKinds.All) + "); repeat or comma-separate." };
            Option<string?> run = new Option<string?>("--run") { HelpName = "run-id", Description = "App-run id paired with --after." };
            Option<string?> after = new Option<string?>("--after") { Description = "Sequence cursor paired with --run." };
            Option<int?> limit = new Option<int?>("--limit") { Description = "Events per read (1-1000)." };
            Command command = new CtlCommand("events", "Continuously print control events, one a line; --json prints each as JSON (Ctrl+C to stop).") { Connection, kinds, run, after, limit };
            // agterm keeps empty items, so `--kind a,` is refused as an invalid kind rather than read as `--kind a`.
            Func<CommandResult, List<string>> rawKinds = parsed => (parsed.GetValue(kinds) ?? Array.Empty<string>()).SelectMany(value => value.Split(',')).Select(kind => kind.Trim()).ToList();
            command.Check(
                parsed => (parsed.GetValue(run) is null) != (parsed.GetValue(after) is null) ? "events.read requires --run and --after together" : null,
                parsed => parsed.GetValue(run) is string id && !Guid.TryParseExact(id, "D", out _) ? "invalid event run id" : null,
                parsed => parsed.GetValue(after) is string cursor && !ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "invalid event cursor" : null,
                parsed => parsed.GetValue(limit) is int count && count is < 1 or > 1000 ? "event limit must be between 1 and 1000" : null,
                parsed => rawKinds(parsed).FirstOrDefault(kind => !ControlEventKinds.All.Contains(kind)) is string invalid ? "invalid event kind: " + invalid : null);
            command.SetAction(result =>
            {
                List<string> selected = (result.GetValue(kinds) ?? Array.Empty<string>()).SelectMany(value => value.Split(',')).Select(kind => kind.Trim()).ToList();
                EventStream stream = new EventStream(selected.Count > 0 ? selected : null, result.GetValue(limit), result.GetValue(run), result.GetValue(after));
                return RunEvents(result, stream);
            });
            return command;
        }

        /// <summary>
        /// agterm's session wait: anchor the event cursor, read the current status, then follow the events from the anchor,
        /// so a status set between the two reads is still seen. Every failure is agterm's thrown error, Error: and exit 1.
        /// </summary>
        private static int RunWait(ParseResult result, List<string> until, double? timeout, string target)
        {
            // agterm's watchdog: the deadline holds even while a pipe call is blocked on an app that never answers.
            using Timer? watchdog = timeout is double seconds
                ? new Timer(_ =>
                {
                    Console.Error.WriteLine("timed out");
                    Environment.Exit(WaitTimeoutExitCode);
                }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan)
                : null;
            string pipe = ControlEndpoint.ResolvePipe(result.GetValue(Pipe));
            Stopwatch clock = Stopwatch.StartNew();
            void Pause(TimeSpan delay)
            {
                Thread.Sleep(timeout is double limit ? TimeSpan.FromSeconds(Math.Clamp(limit - clock.Elapsed.TotalSeconds, 0, delay.TotalSeconds)) : delay);
            }

            try
            {
                EventStream stream = new EventStream(new List<string> { ControlEventKinds.Status, ControlEventKinds.SessionClosed }, null);
                stream.Consume(Call(pipe, stream.Request()));
                (string id, string current) = ResolveWaitSession(pipe, target);
                if (until.Contains(current))
                {
                    return PrintWaitResult(result, id, current);
                }

                while (true)
                {
                    if (timeout is double limit && clock.Elapsed.TotalSeconds >= limit)
                    {
                        Console.Error.WriteLine("timed out");
                        return WaitTimeoutExitCode;
                    }

                    ControlResponse response;
                    try
                    {
                        response = Call(pipe, stream.Request());
                    }
                    catch (ControlClientException ex) when (stream.RetryDelay(ex, pipe) is TimeSpan delay)
                    {
                        Pause(delay);
                        continue;
                    }

                    List<ControlEvent> events = stream.Consume(response);
                    foreach (ControlEvent item in events.Where(item => string.Equals(item.Session, id, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (item.Kind == ControlEventKinds.SessionClosed)
                        {
                            Console.Error.WriteLine("session closed");
                            return WaitClosedExitCode;
                        }

                        if (item.Payload.Status is string status && until.Contains(status))
                        {
                            return PrintWaitResult(result, id, status);
                        }
                    }

                    if (events.Count == 0)
                    {
                        Pause(TimeSpan.FromSeconds(0.25));
                    }
                }
            }
            catch (ControlClientException ex)
            {
                return Failure(ex.Message);
            }
        }

        /// <summary>
        /// agterm's resolveSession: --target across every open window's tree; active is the front window's selected session,
        /// an id or unique prefix matches in any open window. A session without a status is idle.
        /// </summary>
        private static (string Id, string Status) ResolveWaitSession(string pipe, string target)
        {
            ControlResponse list = Call(pipe, new ControlRequest { Cmd = "window.list" });
            if (!list.Ok || list.Result?.Windows is not List<ControlWindowNode> windows)
            {
                throw new ControlClientException(list.Error ?? "window.list response missing windows");
            }

            List<ControlSessionNode> sessions = new List<ControlSessionNode>();
            Guid? active = null;
            foreach (ControlWindowNode window in windows.Where(window => window.Open))
            {
                ControlResponse reply = Call(pipe, new ControlRequest { Cmd = "tree", Args = new ControlArgs { Window = window.Id } });
                if (!reply.Ok || reply.Result?.Tree is not ControlTree tree)
                {
                    throw new ControlClientException(reply.Error ?? "tree response missing tree");
                }

                List<ControlSessionNode> nodes = tree.Workspaces.SelectMany(workspace => workspace.Sessions).ToList();
                sessions.AddRange(nodes);
                if (window.Active && nodes.FirstOrDefault(node => node.Active) is ControlSessionNode selected)
                {
                    active = Guid.Parse(selected.Id);
                }
            }

            TargetResolution resolution = ControlTargets.Resolve(target, sessions.Select(session => Guid.Parse(session.Id)), active);
            if (resolution.Match != TargetMatch.Resolved)
            {
                throw new ControlClientException(ControlTargets.ErrorMessage("session", target, resolution));
            }

            ControlSessionNode node = sessions.First(session => Guid.Parse(session.Id) == resolution.Id);
            return (resolution.Id.Wire(), node.Status ?? "idle");
        }

        private static int PrintWaitResult(ParseResult result, string id, string status)
        {
            Console.WriteLine(result.GetValue(Json) ? "{\"id\":\"" + id + "\",\"status\":\"" + status + "\"}" : status);
            return 0;
        }

        /// <summary>agterm's events: one read after another, each failure ending the stream as a thrown error does.</summary>
        private static int RunEvents(ParseResult result, EventStream stream)
        {
            bool json = result.GetValue(Json);
            string pipe = ControlEndpoint.ResolvePipe(result.GetValue(Pipe));
            try
            {
                while (true)
                {
                    ControlResponse response;
                    try
                    {
                        response = Call(pipe, stream.Request());
                    }
                    catch (ControlClientException ex) when (stream.RetryDelay(ex, pipe) is TimeSpan delay)
                    {
                        Thread.Sleep(delay);
                        continue;
                    }

                    List<ControlEvent> events = stream.Consume(response);
                    foreach (ControlEvent item in events)
                    {
                        Console.WriteLine(json ? ControlJson.Serialize(item) : HumanEvent(item));
                    }

                    Console.Out.Flush();
                    if (events.Count == 0)
                    {
                        Thread.Sleep(250);
                    }
                }
            }
            catch (ControlClientException ex)
            {
                return Failure(ex.Message);
            }
        }

        /// <summary>One request on its own connection, without --window: events and wait address no window.</summary>
        private static ControlResponse Call(string pipe, ControlRequest request)
        {
            return ControlClient.SendAsync(pipe, request, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult().Response;
        }

        /// <summary>
        /// agterm's EventStreamState: the cursor, the kinds in catalog order, and the retry of a read the app is there for but
        /// not taking: 0.25 seconds doubling to 4, for up to 30 seconds in a row, and only once there is a cursor to keep.
        /// </summary>
        private sealed class EventStream
        {
            private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(0.25);
            private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(4);
            private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(30);
            private readonly List<string>? kinds;
            private readonly int? limit;
            private string? run;
            private string? after;
            private TimeSpan nextRetryDelay = FirstRetryDelay;
            private TimeSpan retriedFor = TimeSpan.Zero;

            public EventStream(List<string>? kinds, int? limit, string? run = null, string? after = null)
            {
                this.kinds = kinds is null ? null : ControlEventKinds.All.Where(kinds.Contains).ToList();
                this.limit = limit;
                this.run = run;
                this.after = after;
            }

            public ControlRequest Request()
            {
                return run is null && kinds is null && limit is null
                    ? new ControlRequest { Cmd = "events.read" }
                    : new ControlRequest { Cmd = "events.read", Args = new ControlArgs { Kinds = kinds, Run = run, After = after, Limit = limit } };
            }

            /// <summary>
            /// The wait before reading again, null when the failure ends the stream. A pipe that is gone means the app quit, and
            /// a relaunch is a new run the cursor cannot resume; a pipe that is there but not connecting is a busy app.
            /// </summary>
            public TimeSpan? RetryDelay(ControlClientException failure, string pipe)
            {
                if (run is null || !failure.Connect || !File.Exists(@"\\.\pipe\" + pipe) || retriedFor >= RetryBudget)
                {
                    return null;
                }

                TimeSpan delay = nextRetryDelay;
                retriedFor += delay;
                nextRetryDelay = delay * 2 < MaxRetryDelay ? delay * 2 : MaxRetryDelay;
                return delay;
            }

            /// <exception cref="ControlClientException">The app refused the read, or answered without events.</exception>
            public List<ControlEvent> Consume(ControlResponse response)
            {
                nextRetryDelay = FirstRetryDelay;
                retriedFor = TimeSpan.Zero;
                if (!response.Ok)
                {
                    throw new ControlClientException(response.Error ?? "events.read failed");
                }

                if (response.Result?.Events is not ControlEventBatch batch)
                {
                    throw new ControlClientException("events.read response missing events");
                }

                run = batch.Run;
                after = batch.Next.ToString(CultureInfo.InvariantCulture);
                return batch.Items;
            }
        }

        /// <summary>agterm's line for an event: local time, kind, the session's name, then what the kind carries.</summary>
        internal static string HumanEvent(ControlEvent item)
        {
            string time = DateTimeOffset.FromUnixTimeMilliseconds((long)(item.Ts * 1000)).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string name = item.Payload.Name ?? item.Window ?? "-";
            switch (item.Kind)
            {
                case ControlEventKinds.Status:
                    List<string> parts = new List<string> { time, item.Kind, name, item.Payload.Status ?? "idle" };
                    if (item.Payload.Previous is string previous)
                    {
                        parts.Add("previous=" + previous);
                    }

                    if (item.Payload.Pane is string pane)
                    {
                        parts.Add("pane=" + pane);
                    }

                    if (item.Payload.Blink == true)
                    {
                        parts.Add("blink");
                    }

                    if (item.Payload.Color is string color)
                    {
                        parts.Add("color=" + color);
                    }

                    if (item.Payload.Shape is string shape)
                    {
                        parts.Add("shape=" + shape);
                    }

                    return string.Join(" ", parts);
                case ControlEventKinds.Notify:
                    return time + " " + item.Kind + " " + name + " " + (item.Payload.Title ?? name) + ": " + (item.Payload.Body ?? string.Empty);
                case ControlEventKinds.SessionSelected:
                    return time + " " + item.Kind + " " + name + " previous=" + (item.Payload.Previous ?? "-");
                case ControlEventKinds.PaneSplit:
                case ControlEventKinds.PaneScratch:
                    return time + " " + item.Kind + " " + name + " " + (item.Payload.Status ?? string.Empty);
                case ControlEventKinds.PaneFocused:
                    return time + " " + item.Kind + " " + name + " " + (item.Payload.Pane ?? string.Empty) + " pane-id=" + (item.Payload.PaneId ?? "-");
                case ControlEventKinds.RemoteOpened:
                case ControlEventKinds.RemoteClosed:
                    return time + " " + item.Kind + " " + name + " host=" + (item.Payload.Host ?? string.Empty);
                case ControlEventKinds.AppLaunched:
                    return time + " " + item.Kind;
                default:
                    return time + " " + item.Kind + " " + name;
            }
        }

        private static List<string> Statuses(string[]? values)
        {
            return (values ?? Array.Empty<string>()).ToList();
        }
    }
}
