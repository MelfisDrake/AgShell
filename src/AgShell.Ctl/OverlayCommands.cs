using System.CommandLine;
using System.Globalization;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    internal static partial class Commands
    {
        private static Command Overlay()
        {
            Command overlay = new CtlCommand("overlay", "Run a program in a temporary terminal over a session.");

            Argument<string?> program = new Argument<string?>("command") { Description = "Program to run (wrap shell syntax in pwsh -c \"...\" or sh -c '...'); it closes when the program exits. Omit with --html or --url.", Arity = ArgumentArity.ZeroOrOne };
            Option<string?> html = new Option<string?>("--html") { Description = "Show this local HTML file instead of running a program." };
            Option<string?> url = new Option<string?>("--url") { Description = "Show this http or https page instead of running a program." };
            Option<bool> js = new Option<bool>("--js") { Description = "Let the page run its own JavaScript (off by default)." };
            Option<bool> navigation = new Option<bool>("--navigation") { Description = "Add back, forward, reload, open in browser, and reveal or copy-link buttons." };
            Option<bool> chromeless = new Option<bool>("--chromeless") { Description = "No strip naming the page (--html, or a loopback --url)." };
            Option<bool> persistent = new Option<bool>("--persistent") { Description = "With --url, keep the site's cookies and logins after the page closes (agshellctl browser clear empties them)." };
            Option<bool> browse = new Option<bool>("--browse") { Description = "With --url, let the page leave its first site: links, redirects and scripts may take it to any http or https address." };
            Option<string?> cwd = new Option<string?>("--cwd") { Description = "Working directory (default: the session's current directory). With --html, the directory the page may read." };
            Option<bool> wait = new Option<bool>("--wait") { Description = "Keep the overlay open after the program exits (press any key to close)." };
            Option<bool> block = new Option<bool>("--block") { Description = "Wait until the program exits and exit with its status." };
            Option<bool> follow = new Option<bool>("--follow") { Description = "Switch to the target session (default: open without switching)." };
            Option<int?> size = new Option<int?>("--size-percent") { Description = "A floating, framed panel at PERCENT (1-100) of the session instead of full size." };
            Option<string?> background = new Option<string?>("--background-color") { Description = "Solid background color (#rrggbb) for the overlay." };
            Option<int?> widthPoints = PointOption("--width", "Panel content width in points, in place of --size-percent's width (at most 80% of the session).");
            Option<int?> heightPoints = PointOption("--height", "Panel content height in points, in place of --size-percent's height (at most 80% of the session); a page's strip is added on top.");
            Option<string?> pane = OverlayPane();
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command open = new CtlCommand("open", "Open an overlay running COMMAND, or showing a page with --html or --url.")
            {
                program, html, url, js, navigation, chromeless, persistent, browse, cwd, wait, block, follow, size, widthPoints, heightPoints, background, pane, target,
            };
            // agterm's open.validate, in its order.
            open.Check(
                parsed => parsed.GetValue(block) && parsed.GetValue(wait) ? "--block cannot be combined with --wait" : null,
                parsed => new object?[] { parsed.GetValue(program), parsed.GetValue(html), parsed.GetValue(url) }.Count(given => given is not null) != 1 ? "provide exactly one of COMMAND, --html or --url" : null,
                // A page holds no terminal to keep, and a --url page answers no one.
                parsed => parsed.GetValue(program) is null && (parsed.GetValue(wait) || (parsed.GetValue(url) is not null && parsed.GetValue(block))) ? "a page takes no --wait, and a --url page no --block" : null,
                parsed => parsed.GetValue(navigation) && parsed.GetValue(program) is not null ? "--navigation requires --html or --url" : null,
                parsed => parsed.GetValue(js) && parsed.GetValue(program) is not null ? "--js requires --html or --url" : null,
                parsed => parsed.GetValue(chromeless) && parsed.GetValue(program) is not null ? "--chromeless requires --html or a loopback --url" : null,
                parsed => parsed.GetValue(chromeless) && parsed.GetValue(navigation) ? "--chromeless cannot be combined with --navigation" : null,
                parsed => parsed.GetValue(chromeless) && parsed.GetValue(browse) ? "--chromeless cannot be combined with --browse" : null,
                parsed => parsed.GetValue(chromeless) && parsed.GetValue(url) is string address && ControlUrls.IsWeb(address) && !ControlUrls.IsLoopback(address) ? "--chromeless with --url requires a loopback host: 127.0.0.1, ::1 or localhost" : null,
                parsed => parsed.GetValue(persistent) && parsed.GetValue(url) is null ? "--persistent requires --url" : null,
                parsed => parsed.GetValue(browse) && parsed.GetValue(url) is null ? "--browse requires --url" : null,
                parsed => parsed.GetValue(url) is not null && parsed.GetValue(cwd) is not null ? "--cwd cannot be combined with --url" : null,
                parsed => parsed.GetValue(background) is string color && !Checks.IsHexColor(color) ? "background-color must be a #rrggbb hex value" : null,
                OverlayPaneCheck(pane),
                parsed => parsed.GetValue(pane) is not null && parsed.GetValue(size) is not null ? "--pane cannot be combined with --size-percent (pane overlays are always full)" : null,
                parsed => parsed.GetValue(pane) is not null && (parsed.GetValue(widthPoints) ?? parsed.GetValue(heightPoints)) is not null ? "--pane cannot be combined with --width/--height (pane overlays are always full)" : null,
                SizePercentCheck(size),
                PointsCheck(widthPoints, heightPoints),
                parsed => (parsed.GetValue(widthPoints) ?? parsed.GetValue(heightPoints)) is not null && parsed.GetValue(size) is null ? "--width/--height need --size-percent" : null);
            open.SetAction(result =>
            {
                string? file = result.GetValue(html);
                string? directory = result.GetValue(cwd);
                ControlArgs args = new ControlArgs
                {
                    Command = result.GetValue(program),
                    Html = file is null ? null : Path.GetFullPath(file),
                    Url = result.GetValue(url),
                    Js = Flag(result, js),
                    Navigation = Flag(result, navigation),
                    Chromeless = Flag(result, chromeless),
                    Persistent = Flag(result, persistent),
                    Browse = Flag(result, browse),
                    // A relative directory means one next to the caller, which the app cannot know.
                    Cwd = directory is not null && file is not null ? Path.GetFullPath(directory) : directory,
                    Wait = Flag(result, wait),
                    Follow = Flag(result, follow),
                    SizePercent = result.GetValue(size),
                    Width = result.GetValue(widthPoints),
                    Height = result.GetValue(heightPoints),
                    Color = result.GetValue(background),
                    Pane = result.GetValue(pane),
                };
                if (result.GetValue(block) && args.Command is null)
                {
                    return RunBlockingPage(result, result.GetValue(target)!, args);
                }

                return result.GetValue(block)
                    ? RunBlocking(result, result.GetValue(target)!, args)
                    : Send(result, "session.overlay.open", result.GetValue(target), args);
            });
            overlay.Subcommands.Add(open);

            // agterm's helpers the machine presenting a session runs here through ssh.
            Argument<string> jobId = new Argument<string>("job") { Description = "The job id the other machine was handed." };
            Command runJob = new CtlCommand("run-job", "Run a remote overlay job this app handed to another machine, in that machine's ssh terminal; not meant to be run by hand. A job that cannot be claimed exits 1 having launched nothing, one whose program cannot be launched exits 127, otherwise the program's status.") { jobId };
            runJob.SetAction(result => OverlayRunJob.Run(result.GetValue(jobId)!, result.GetValue(Pipe)));
            overlay.Subcommands.Add(runJob);
            Argument<string> pageToken = new Argument<string>("token") { Description = "The token the other machine was handed with the page." };
            Command pageServe = new CtlCommand("page-serve", "Carry a handed-over page's channel between stdin/stdout and this app; not meant to be run by hand.") { pageToken };
            pageServe.SetAction(result => OverlayPageServe.Run(result.GetValue(pageToken)!, result.GetValue(Pipe)));
            overlay.Subcommands.Add(pageServe);

            overlay.Subcommands.Add(PaneScoped("close", "Close the overlay, ending its program.", "session.overlay.close"));
            overlay.Subcommands.Add(PaneScoped("hide", "Hide the session's programs and pages (or one pane's) without closing them; prints hidden or shown.", "session.overlay.hide"));
            overlay.Subcommands.Add(PaneScoped("show", "Show hidden programs and pages again; prints hidden or shown.", "session.overlay.show"));
            overlay.Subcommands.Add(PaneScoped("toggle", "Hide them all while any shows, otherwise show them all; prints hidden or shown.", "session.overlay.toggle"));

            Option<string?> resultPane = OverlayPane();
            Option<string?> pageResult = new Option<string?>("--page") { Description = "Print the outcome of the page with this id (as overlay open --json named it) as JSON with pageID, outcome and value; exit 0 submitted, 2 dismissed, 1 still open." };
            Option<string> resultTarget = Target("Target session id, unique prefix, or 'active'.");
            Command status = new CtlCommand("result", "Print the last overlay program's exit status; an error while it runs ('overlay still running') or when it left none.") { resultPane, pageResult, resultTarget };
            status.Check(OverlayPaneCheck(resultPane), parsed => parsed.GetValue(pageResult) is not null && parsed.GetValue(resultPane) is not null ? "--page cannot be combined with --pane" : null);
            status.SetAction(result =>
            {
                if (result.GetValue(pageResult) is string pageId)
                {
                    return ReadPage(result, pageId, block: false);
                }

                ControlRequest request = new ControlRequest
                {
                    Cmd = "session.overlay.result",
                    Target = result.GetValue(resultTarget),
                    Args = new ControlArgs { Pane = result.GetValue(resultPane) },
                };
                if (Request(result, request) is not ControlReply reply)
                {
                    return 1;
                }

                if (result.GetValue(Json))
                {
                    Console.WriteLine(reply.Line);
                    return reply.Response.Ok ? 0 : 1;
                }

                if (!reply.Response.Ok)
                {
                    return Error(reply.Response.Error ?? "unknown error");
                }

                // agterm's line: exit N.
                Console.WriteLine("exit " + reply.Response.Result?.ExitCode?.ToString(CultureInfo.InvariantCulture));
                return 0;
            });
            overlay.Subcommands.Add(status);

            Option<bool> current = new Option<bool>("--current") { Description = "Reload the page the overlay shows now instead of the original file or URL." };
            Option<string?> reloadPane = OverlayPane();
            Option<string> reloadTarget = Target("Target session id, unique prefix, or 'active'.");
            Command reload = new CtlCommand("reload", "Reload an HTML overlay: the file or URL it was opened with, or with --current the page shown now.") { current, reloadPane, reloadTarget };
            reload.Check(OverlayPaneCheck(reloadPane));
            reload.SetAction(result => Send(result, "session.overlay.reload", result.GetValue(reloadTarget), new ControlArgs { Pane = result.GetValue(reloadPane), Current = Flag(result, current) }));
            overlay.Subcommands.Add(reload);

            // agterm's step is positional; an http or https address instead loads it in a web page, this machine's addition.
            Argument<string> navigateStep = new Argument<string>("step") { Description = "back, forward, browser, or finder (file pages only); or an http or https address to load in a web page." };
            Option<string?> navigatePane = OverlayPane();
            Option<string> navigateTarget = Target("Target session id, unique prefix, or 'active'.");
            Command navigate = new CtlCommand("navigate", "Step an HTML overlay's history, open it in the browser, or reveal its current file in Explorer.") { navigateStep, navigatePane, navigateTarget };
            navigate.Check(
                parsed => IsWebStep(parsed.GetValue(navigateStep)!) || parsed.GetValue(navigateStep) is "back" or "forward" or "browser" or "finder" ? null : "step must be back, forward, browser, or finder",
                OverlayPaneCheck(navigatePane));
            navigate.SetAction(result =>
            {
                string step = result.GetValue(navigateStep)!;
                bool web = IsWebStep(step);

                return Send(result, "session.overlay.navigate", result.GetValue(navigateTarget), new ControlArgs
                {
                    Url = web ? step : null,
                    To = web ? null : step,
                    Pane = result.GetValue(navigatePane),
                });
            });
            overlay.Subcommands.Add(navigate);

            Option<string> submitValue = new Option<string>("--value") { Description = "The answer handed back to the caller; empty is a real answer.", Required = true };
            Option<string?> submitPane = OverlayPane();
            Option<string> submitTarget = Target("Target session id, unique prefix, or 'active'.");
            Command submit = new CtlCommand("submit", "Answer an HTML overlay with a value and close it, as its page would; a waiting --block prints the value.") { submitValue, submitPane, submitTarget };
            submit.Check(OverlayPaneCheck(submitPane));
            submit.SetAction(result =>
            {
                return Send(result, "session.overlay.submit", result.GetValue(submitTarget), new ControlArgs { Value = result.GetValue(submitValue), Pane = result.GetValue(submitPane) });
            });
            overlay.Subcommands.Add(submit);

            overlay.Subcommands.Add(PaneScoped("copy", "Print the selection made inside the overlay (session copy reads the pane underneath).", "session.overlay.copy"));

            Option<int?> resizePercent = new Option<int?>("--size-percent") { Description = "Make it a floating panel at PERCENT (1-100)." };
            Option<bool> full = new Option<bool>("--full") { Description = "Return it to full size." };
            Option<string> resizeTarget = Target("Target session id, unique prefix, or 'active'.");
            Option<int?> resizeWidth = PointOption("--width", "Panel content width in points; alone it changes only that axis of a floating panel.");
            Option<int?> resizeHeight = PointOption("--height", "Panel content height in points; alone it changes only that axis of a floating panel.");
            Command resize = new CtlCommand("resize", "Resize an open overlay in place; the program keeps running.") { resizePercent, resizeWidth, resizeHeight, full, resizeTarget };
            resize.Check(
                parsed => parsed.GetValue(full) && parsed.GetValue(resizePercent) is not null ? "--full cannot be combined with --size-percent" : null,
                parsed => parsed.GetValue(full) && (parsed.GetValue(resizeWidth) ?? parsed.GetValue(resizeHeight)) is not null ? "--full cannot be combined with --width/--height" : null,
                parsed => !parsed.GetValue(full) && parsed.GetValue(resizePercent) is null && (parsed.GetValue(resizeWidth) ?? parsed.GetValue(resizeHeight)) is null ? "provide --size-percent PERCENT, --width/--height POINTS or --full" : null,
                SizePercentCheck(resizePercent),
                PointsCheck(resizeWidth, resizeHeight));
            resize.SetAction(result => Send(result, "session.overlay.resize", result.GetValue(resizeTarget), new ControlArgs
            {
                SizePercent = result.GetValue(resizePercent),
                Width = result.GetValue(resizeWidth),
                Height = result.GetValue(resizeHeight),
                Full = Flag(result, full),
            }));
            overlay.Subcommands.Add(resize);

            Option<bool> all = new Option<bool>("--all") { Description = "Read the whole buffer instead of the visible screen." };
            Option<int?> lines = new Option<int?>("--lines") { Description = "Keep only the last N lines." };
            Option<string?> textPane = OverlayPane();
            Option<string> textTarget = Target("Target session id, unique prefix, or 'active'.");
            Command text = new CtlCommand("text", "Print what the overlay's terminal shows.") { all, lines, textPane, textTarget };
            text.Check(
                parsed => parsed.GetValue(all) && parsed.GetValue(lines) is not null ? "use either --all or --lines, not both" : null,
                parsed => parsed.GetValue(lines) is <= 0 ? "--lines must be greater than 0" : null,
                OverlayPaneCheck(textPane));
            text.SetAction(result => Send(result, "session.overlay.text", result.GetValue(textTarget), new ControlArgs
            {
                All = Flag(result, all),
                Lines = result.GetValue(lines),
                Pane = result.GetValue(textPane),
            }));
            overlay.Subcommands.Add(text);

            return overlay;
        }

        /// <summary>agterm's outcome object: {"pageID":…,"outcome":…,"value":…}, the value only when the page submitted one.</summary>
        private static string PageOutcome(ControlPageResult page)
        {
            using MemoryStream buffer = new MemoryStream();
            using (System.Text.Json.Utf8JsonWriter writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("pageID", page.PageId);
                writer.WriteString("outcome", page.Outcome);
                if (page.Value is string value)
                {
                    writer.WriteString("value", value);
                }

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>Opens a page and waits until it submits a value (exit 0, printing the outcome) or closes unanswered (exit 2).</summary>
        private static int RunBlockingPage(ParseResult result, string target, ControlArgs args)
        {
            if (Request(result, new ControlRequest { Cmd = "session.overlay.open", Target = target, Args = args }) is not ControlReply opened)
            {
                return 1;
            }

            if (!opened.Response.Ok)
            {
                return Refused(result, opened);
            }

            if (opened.Response.Result?.PageId is not string pageId)
            {
                return Error("session.overlay.open result missing pageID");
            }

            return ReadPage(result, pageId, block: true);
        }

        /// <summary>
        /// agterm's page reader: polls by the page's id, never by session, so a page reopened in the same slot cannot answer
        /// for this one; prints the outcome (the whole answer with --json) and exits 0 submitted, 2 dismissed.
        /// </summary>
        private static int ReadPage(ParseResult result, string pageId, bool block)
        {
            for (int pending = 1; ; pending++)
            {
                if (Request(result, new ControlRequest { Cmd = "session.overlay.result", Args = new ControlArgs { PageId = pageId } }) is not ControlReply reply)
                {
                    return 1;
                }

                if (!reply.Response.Ok)
                {
                    return Refused(result, reply);
                }

                if (reply.Response.Result?.Page is not ControlPageResult page)
                {
                    return Error("session.overlay.result missing the page outcome");
                }

                if (block && page.Outcome == "pending")
                {
                    Thread.Sleep(PollDelay(pending));
                    continue;
                }

                // agterm's codes, as pick's: 0 answered, 2 dismissed; a one-shot read of a page still open is a failure.
                Console.WriteLine(result.GetValue(Json) ? reply.Line : PageOutcome(page));
                return page.Outcome switch { "submitted" => 0, "dismissed" => 2, _ => 1 };
            }
        }

        /// <summary>A refusal as agterm prints it: the answer's own line with --json, its error otherwise.</summary>
        private static int Refused(ParseResult result, ControlReply reply)
        {
            if (result.GetValue(Json))
            {
                Console.WriteLine(reply.Line);
                return 1;
            }

            return Error(reply.Response.Error ?? "unknown error");
        }

        /// <summary>Opens the overlay, then polls its result until the program exits, and exits with the program's status.</summary>
        private static int RunBlocking(ParseResult result, string target, ControlArgs args)
        {
            if (Request(result, new ControlRequest { Cmd = "session.overlay.open", Target = target, Args = args }) is not ControlReply opened)
            {
                return 1;
            }

            if (!opened.Response.Ok)
            {
                return Refused(result, opened);
            }

            // agterm polls by the session the open named, so switching sessions meanwhile cannot make it read another slot.
            if (opened.Response.Result?.Id is not string session)
            {
                return Refused(result, opened);
            }

            while (true)
            {
                if (Request(result, new ControlRequest { Cmd = "session.overlay.result", Target = session, Args = new ControlArgs { Pane = args.Pane } }) is not ControlReply reply)
                {
                    return 1;
                }

                if (reply.Response.Ok)
                {
                    if (result.GetValue(Json))
                    {
                        Console.WriteLine(reply.Line);
                    }

                    // agterm: a successful result must carry the status; its absence is a protocol violation, not success.
                    return reply.Response.Result?.ExitCode is int code ? code : Error("result missing exit code");
                }

                if (reply.Response.Error == OverlayResultErrors.StillRunning)
                {
                    Thread.Sleep(100);
                    continue;
                }

                // As agterm's, a refusal is printed as the server gave it, the whole answer with --json.
                if (result.GetValue(Json))
                {
                    Console.WriteLine(reply.Line);
                    return 1;
                }

                return Error(reply.Response.Error ?? "unknown error");
            }
        }

        /// <summary>
        /// agterm's session hud: open is the default, so posting one is `session hud "text"`; a message that is literally
        /// update or close needs the explicit `hud open`. A session has one HUD; --pane or --pane-id only places it.
        /// </summary>
        private static Command Hud()
        {
            Command hud = HudCommand("hud", "Post, update, or close a passive message panel over a session; the session keeps focus and stays typable.", "session.hud.open", update: false);
            CtlCommand.AddDefault(hud, HudCommand("open", "Post a message panel over the session; the session keeps focus and stays typable.", "session.hud.open", update: false));
            hud.Subcommands.Add(HudCommand("update", "Replace the panel's text in place; every option an update takes must be repeated to survive.", "session.hud.update", update: true));
            Option<string> closeTarget = Target("Target session id, unique prefix, or 'active'.");
            Command close = new CtlCommand("close", "Take the message panel down (a program overlay in the same slot is left alone).") { closeTarget };
            close.SetAction(result => Send(result, "session.hud.close", result.GetValue(closeTarget), new ControlArgs()));
            hud.Subcommands.Add(close);
            return hud;
        }

        private static Command HudCommand(string name, string description, string cmd, bool update)
        {
            Argument<string?> message = new Argument<string?>("message") { Description = update ? "New message; it replaces the old one entirely (omit with --file)." : "Message shown in the panel (omit with --file).", Arity = ArgumentArity.ZeroOrOne };
            Option<string?> file = new Option<string?>("--file") { Description = "Read the message from FILE instead of the argument." };
            Option<string?> detail = new Option<string?>("--detail") { Description = "Dim second line under the message." };
            Option<bool> spinner = new Option<bool>("--spinner") { Description = "Animate a spinner in the default style." };
            Option<string?> spinnerStyle = new Option<string?>("--spinner-style") { Description = "Spinner style: bar, braille, circle, blocks, dot, or none. Implies --spinner." };
            Option<string?> position = new Option<string?>("--position") { Description = "top-left, top-center, top-right, center-left, center (default), center-right, bottom-left, bottom-center, bottom-right; also top and bottom." };
            Option<double?> hideAfter = new Option<double?>("--hide-after") { Description = "Take the panel down by itself after SECONDS (0-86400); omit or 0 to leave it up." };
            Option<int?> size = new Option<int?>("--size-percent") { Description = "Set the panel's width to PERCENT (1-100) of the pane." };
            Option<string?> background = new Option<string?>("--background-color") { Description = "Solid background color (#rrggbb) for the panel, independent of the session's own." };
            Option<string?> textColor = new Option<string?>("--text-color") { Description = "Color (#rrggbb) for the panel's text; omit to keep the terminal foreground." };
            Option<double?> fontSize = new Option<double?>("--font-size") { Description = "The panel's own font size in points, 6-72; omit to use the session's. Fixed for the panel's life." };
            Option<bool> sticky = new Option<bool>("--sticky") { Description = "Put the panel flush against the edge or corner --position names, with no margin." };
            Option<bool> noFrame = new Option<bool>("--no-frame") { Description = "Draw no border, no rounded corners and no blank row above and below the text." };
            Option<bool> markdown = new Option<bool>("--markdown") { Description = "Render the message as markdown, up to 4096 characters." };
            Option<string?> pane = new Option<string?>("--pane") { Description = update ? "Anchor over the left or right pane; omit to return to whole-session placement." : "Anchor over the left or right pane; omit for the whole session." };
            Option<string?> paneId = new Option<string?>("--pane-id") { Description = "Stable pane token ($AGSHELL_PANE_ID); overrides --pane when it resolves." };
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            CtlCommand command = new CtlCommand(name, description) { message, file, detail, spinner, spinnerStyle, position, hideAfter, size, textColor, sticky, noFrame, markdown, pane, paneId, target };
            if (!update)
            {
                // As agterm's, the background and font are read once when the panel is made, so only an open takes them.
                command.Add(background);
                command.Add(fontSize);
            }

            // agterm's open and update validate, in their order; only an open carries the background and the font.
            List<Func<System.CommandLine.Parsing.CommandResult, string?>> checks = new List<Func<System.CommandLine.Parsing.CommandResult, string?>>();
            if (!update)
            {
                checks.Add(parsed => parsed.GetValue(background) is string color && !Checks.IsHexColor(color) ? "background-color must be a #rrggbb hex value" : null);
            }

            checks.Add(parsed => parsed.GetValue(textColor) is string color && !Checks.IsHexColor(color) ? "text-color must be a #rrggbb hex value" : null);
            checks.Add(parsed => parsed.GetValue(position) is string place && !ControlHud.Positions.Concat(ControlHud.PositionAliases).Contains(place) ? "position must be one of: " + string.Join(", ", ControlHud.Positions.Concat(ControlHud.PositionAliases)) : null);
            checks.Add(parsed => parsed.GetValue(spinnerStyle) is string style && style != ControlHud.NoSpinner && !ControlHud.Spinners.Contains(style) ? "spinner style must be one of: " + string.Join(", ", ControlHud.Spinners.Append(ControlHud.NoSpinner)) : null);
            checks.Add(parsed => parsed.GetValue(hideAfter) is double seconds && !(double.IsFinite(seconds) && seconds >= 0 && seconds <= ControlHud.MaxHideAfter) ? "hide-after must be 0..." + ControlHud.MaxHideAfter + " seconds" : null);
            checks.Add(parsed => (parsed.GetValue(message), parsed.GetValue(file)) switch
            {
                (null, null) => "provide MESSAGE or --file",
                (not null, not null) => "MESSAGE and --file are mutually exclusive",
                _ => null,
            });
            if (!update)
            {
                checks.Add(parsed => parsed.GetValue(fontSize) is double points && !(points >= ControlHud.MinFontSize && points <= ControlHud.MaxFontSize) ? "font-size must be " + ControlHud.MinFontSize + "..." + ControlHud.MaxFontSize + " points" : null);
            }

            checks.Add(SizePercentCheck(size));
            checks.Add(OverlayPaneCheck(pane));
            command.Check(checks.ToArray());
            command.SetAction(result =>
            {
                string? text = result.GetValue(message);
                string? path = result.GetValue(file);

                if (path is not null)
                {
                    try
                    {
                        text = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)).Replace("\r\n", "\n");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.DecoderFallbackException)
                    {
                        return Usage(result, ex is System.Text.DecoderFallbackException ? "--file " + path + " is not valid UTF-8" : "cannot read --file " + path + ": " + ex.Message);
                    }

                    if (text.EndsWith('\n'))
                    {
                        text = text[..^1];
                    }
                }

                string? style = result.GetValue(spinnerStyle);
                return Send(result, cmd, result.GetValue(target), new ControlArgs
                {
                    Message = text,
                    Detail = result.GetValue(detail),
                    Spinner = style == "none" ? null : style ?? (result.GetValue(spinner) ? "bar" : null),
                    Position = result.GetValue(position),
                    HideAfter = result.GetValue(hideAfter),
                    SizePercent = result.GetValue(size),
                    Color = update ? null : result.GetValue(background),
                    TextColor = result.GetValue(textColor),
                    FontSize = update ? null : result.GetValue(fontSize),
                    Sticky = Flag(result, sticky),
                    Frame = result.GetValue(noFrame) ? false : null,
                    Markdown = Flag(result, markdown),
                    Pane = result.GetValue(pane),
                    PaneId = result.GetValue(paneId),
                });
            });
            return command;
        }

        private static bool IsWebStep(string step)
        {
            return step.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || step.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>agterm's Overlay.validatePane: the two pane roles and their aliases, never scratch.</summary>
        private static Func<System.CommandLine.Parsing.CommandResult, string?> OverlayPaneCheck(Option<string?> pane)
        {
            return parsed => parsed.GetValue(pane) is string value && value is not ("left" or "right" or "top" or "bottom" or "primary" or "split") ? "--pane must be left or right" : null;
        }

        private static Option<string?> OverlayPane()
        {
            return new Option<string?>("--pane") { Description = "Scope it to one pane of a split, left or right, leaving the other pane live; omit for the whole session." };
        }

        /// <summary>agterm's --width and --height of a floating overlay: positive points.</summary>
        private static Option<int?> PointOption(string name, string description)
        {
            return new Option<int?>(name) { Description = description };
        }

        private static Func<System.CommandLine.Parsing.CommandResult, string?> PointsCheck(Option<int?> width, Option<int?> height)
        {
            return parsed => parsed.GetValue(width) is <= 0 || parsed.GetValue(height) is <= 0 ? "--width/--height must be positive" : null;
        }

        private static Func<System.CommandLine.Parsing.CommandResult, string?> SizePercentCheck(Option<int?> size)
        {
            return parsed => parsed.GetValue(size) is < 1 or > 100 ? "--size-percent must be between 1 and 100" : null;
        }

        private static Command PaneScoped(string name, string description, string cmd)
        {
            Option<string?> pane = OverlayPane();
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command command = new CtlCommand(name, description) { pane, target };
            command.Check(OverlayPaneCheck(pane));
            command.SetAction(result => Send(result, cmd, result.GetValue(target), new ControlArgs { Pane = result.GetValue(pane) }));
            return command;
        }

        private static Command Quick()
        {
            Argument<string> mode = new Argument<string>("mode") { Description = "show, hide, or toggle (default). Shown from here it stays up on click-away.", DefaultValueFactory = _ => "toggle" };
            Command quick = new CtlCommand("quick", "The app-wide quick terminal.") { mode };
            quick.SetAction(result => Send(result, "quick", null, new ControlArgs { Mode = result.GetValue(mode) }));
            // agterm's default subcommand: quick [mode] above is the same as quick visibility [mode].
            Argument<string> visibilityMode = new Argument<string>("mode") { Description = "show, hide, or toggle (default).", DefaultValueFactory = _ => "toggle" };
            Command visibility = new CtlCommand("visibility", "Quick terminal visibility (show|hide|toggle).") { visibilityMode };
            visibility.SetAction(result => Send(result, "quick", null, new ControlArgs { Mode = result.GetValue(visibilityMode) }));
            CtlCommand.AddDefault(quick, visibility);

            Argument<string?> text = new Argument<string?>("text") { Description = "Text to type; every newline is a Return. Omit with --stdin.", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> stdin = new Option<bool>("--stdin") { Description = "Read the text from stdin." };
            Command type = new CtlCommand("type", "Type text into the quick terminal.") { text, stdin };
            type.SetAction(result =>
            {
                string? value = result.GetValue(text);
                if (result.GetValue(stdin))
                {
                    value = ReadTypedStdin();
                    if (value is null)
                    {
                        return Usage(result, "stdin must be valid UTF-8");
                    }
                }

                return value is null ? Usage(result, "provide TEXT or --stdin") : Send(result, "quick.type", null, new ControlArgs { Text = value });
            });
            quick.Subcommands.Add(type);

            Option<bool> all = new Option<bool>("--all") { Description = "Read the whole buffer instead of the visible screen." };
            Option<int?> lines = new Option<int?>("--lines") { Description = "Keep only the last N lines." };
            Command read = new CtlCommand("text", "Print the quick terminal's text.") { all, lines };
            read.Check(
                parsed => parsed.GetValue(all) && parsed.GetValue(lines) is not null ? "use either --all or --lines, not both" : null,
                parsed => parsed.GetValue(lines) is <= 0 ? "--lines must be greater than 0" : null);
            read.SetAction(result => Send(result, "quick.text", null, new ControlArgs { All = Flag(result, all), Lines = result.GetValue(lines) }));
            quick.Subcommands.Add(read);
            return quick;
        }
    }
}
