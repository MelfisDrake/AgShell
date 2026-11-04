using System.CommandLine;
using System.Text;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Output is UTF-8 so names survive in WSL and in pipes; the console's own code page is put back on exit
            // because the shell that ran agshellctl shares that console.
            Encoding original = Console.OutputEncoding;
            Console.OutputEncoding = new UTF8Encoding(false);
            // A watchdog that ends the process from another thread skips the finally below.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.OutputEncoding = original;
            // A hook must never fail or print, whatever arguments it got, so it bypasses the parser.
            if (args.Length > 0 && args[0] == "hook")
            {
                return Hook.Run(args[1..]);
            }

            // The parser's messages stay English and numbers read the same whatever the Windows language, as agterm's do.
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                RootCommand root = Commands.Build();
                ParseResult parsed = root.Parse(args);
                if (Commands.UnknownOption(parsed, args) is string unknown)
                {
                    return Commands.Usage(parsed, unknown);
                }

                if (parsed.Errors.Count > 0 && parsed.Action is System.CommandLine.Invocation.ParseErrorAction)
                {
                    // The first failing check, as agterm's validate() throws it; an unknown word also leaves its command without
                    // a subcommand, and the word is the error worth naming.
                    System.CommandLine.Parsing.ParseError error = parsed.UnmatchedTokens.Count > 0
                        ? parsed.Errors.FirstOrDefault(each => each.Message.Contains("'" + parsed.UnmatchedTokens[0] + "'", StringComparison.Ordinal)) ?? parsed.Errors[0]
                        : parsed.Errors[0];
                    return Commands.Usage(parsed, error.Message);
                }

                return parsed.Invoke();
            }
            finally
            {
                Console.Out.Flush();
                Console.OutputEncoding = original;
            }
        }
    }

    internal static partial class Commands
    {
        private static readonly Option<bool> Json = new Option<bool>("--json") { Description = "Print the raw JSON response." };
        private static readonly Option<string?> Pipe = new Option<string?>("--pipe") { Description = "Override the control pipe name." };
        /// <summary>Where a command declares agterm's option group, when not after everything else.</summary>
        private static ConnectionOptions Connection => ConnectionOptions.Here;

        private static readonly Option<string?> WindowOption = new Option<string?>("--window") { Description = "The window to act in: id, unique prefix, or 'active' (default: the one holding the target, else the front one)." };

        /// <summary>
        /// agterm's BasicOptions commands: app-wide ones, window.* (which name the window as their argument), and the helpers,
        /// where --window is an unknown option. A path ending in a space covers its subcommands too.
        /// </summary>
        private static readonly string[] NoWindow =
        {
            "window ", "version", "events", "session wait", "session overlay run-job", "session overlay page-serve",
            "sidebar", "sidebar visibility", "sidebar mode", "sidebar flagged-layout", "quick", "quick ", "browser ", "keymap reload",
            "keymap list", "restore ", "clipboard ", "live list", "live prune", "live reset", "live tree", "live screen", "live proxy", "live present",
            "recent list", "recent clear", "theme ", "config ", "hooks ", "install ",
        };

        public static RootCommand Build()
        {
            RootCommand root = new RootCommand("Drive AgShell over its control pipe.");
            root.Subcommands.Add(Window());
            root.Subcommands.Add(Simple("tree", "Print the workspace/session tree.", "tree"));
            root.Subcommands.Add(Version());
            root.Subcommands.Add(Workspace());
            root.Subcommands.Add(Session());
            root.Subcommands.Add(Sidebar());
            root.Subcommands.Add(Notify());
            root.Subcommands.Add(Pick());
            root.Subcommands.Add(Ask());
            root.Subcommands.Add(Font());
            root.Subcommands.Add(Surface());
            root.Subcommands.Add(Quick());
            root.Subcommands.Add(Dashboard());

            Command browser = new CtlCommand("browser", "The store web pages opened with --persistent keep.");
            browser.Subcommands.Add(Simple("clear", "Forget the cookies, logins and storage of pages opened with --persistent.", "browser.clear"));
            Argument<string?> linkMode = new Argument<string?>("mode") { Description = "browser (the system browser) or overlay (a web overlay over the session); omit to print the current mode.", Arity = ArgumentArity.ZeroOrOne };
            Command links = new CtlCommand("links", "Where a Ctrl+clicked web link in a terminal opens.") { linkMode };
            links.Check(parsed => parsed.GetValue(linkMode) is null or "browser" or "overlay" ? null : "mode must be browser or overlay");
            links.SetAction(result => Send(result, "browser.links", null, new ControlArgs { Mode = result.GetValue(linkMode) }));
            browser.Subcommands.Add(links);
            root.Subcommands.Add(browser);

            Command keymap = new CtlCommand("keymap", "Key bindings and custom commands from keymap.conf.");
            keymap.Subcommands.Add(Simple("reload", "Re-read and apply keymap.conf (prints the diagnostic count).", "keymap.reload"));
            keymap.Subcommands.Add(Simple("list", "Print the effective bindings, custom commands, global hotkey and diagnostics.", "keymap.list"));
            Argument<string> commandName = new Argument<string>("name") { Description = "The custom command's name, as in keymap.conf." };
            Option<string> runTarget = Target("Session the command runs for: id, unique prefix, or 'active'.");
            Command run = new CtlCommand("run", "Run a custom command from keymap.conf.") { commandName, runTarget };
            run.SetAction(result => Send(result, "keymap.run", result.GetValue(runTarget), new ControlArgs { Name = result.GetValue(commandName) }));
            keymap.Subcommands.Add(run);
            root.Subcommands.Add(keymap);

            Command restore = new CtlCommand("restore", "How sessions come back when agshell starts.");
            Argument<string?> restoreMode = new Argument<string?>("mode") { Description = "none (fresh shells), rerun (start the programs they ran) or live (keep the shells running; after a restart); omit to print the current mode.", Arity = ArgumentArity.ZeroOrOne };
            Command mode = new CtlCommand("mode", "Read or set the restore mode.") { restoreMode };
            mode.Check(parsed => parsed.GetValue(restoreMode) is null or "none" or "rerun" or "live" ? null : "mode must be none, rerun, or live");
            mode.SetAction(result => Send(result, "restore.mode", null, new ControlArgs { Mode = result.GetValue(restoreMode) }));
            restore.Subcommands.Add(mode);
            restore.Subcommands.Add(Simple("capture", "Note what every pane runs now, as a quit does, for an exit that never quits cleanly (rerun mode).", "restore.capture"));
            restore.Subcommands.Add(Simple("clear", "Forget every captured command, so the next launch starts plain shells; pins stay.", "restore.clear"));
            root.Subcommands.Add(restore);

            Command clipboard = new CtlCommand("clipboard", "The clipboard of the terminals showing this pane.");
            Argument<string?> clipboardText = new Argument<string?>("text") { Description = "The text to copy. Read from standard input when omitted. Put -- before text that starts with a dash.", Arity = ArgumentArity.ZeroOrOne };
            Command clipboardSet = new CtlCommand("set", "Copy text to the clipboard of every terminal showing this pane (a main or split pane under restore mode live). Exit status 0 does not confirm that any terminal took it.") { clipboardText };
            // Without TEXT the text is stdin, byte for byte, as agterm reads it; a terminal on stdin is refused, not waited on.
            clipboardSet.SetAction(result =>
            {
                if (result.GetValue(clipboardText) is string given)
                {
                    return LiveClipboard.Run(System.Text.Encoding.UTF8.GetBytes(given));
                }

                if (!Console.IsInputRedirected)
                {
                    return Usage(result, "give the text as an argument or pipe it on standard input");
                }

                using MemoryStream bytes = new MemoryStream();
                Console.OpenStandardInput().CopyTo(bytes);
                return LiveClipboard.Run(bytes.ToArray());
            });
            clipboard.Subcommands.Add(clipboardSet);
            root.Subcommands.Add(clipboard);

            Command live = new CtlCommand("live", "Shells live daemons keep while agshell is closed (restore mode live).");
            live.Subcommands.Add(Simple("list", "List the kept shells and the session pane that claims each.", "live.list"));
            live.Subcommands.Add(Simple("prune", "End the kept shells no session pane claims.", "live.prune"));
            Option<bool> resetForce = new Option<bool>("--force") { Description = "Required. Confirms ending the processes in every affected live session." };
            Command reset = new CtlCommand("reset", "Reset live sessions whose daemon predates the recorded live daemon update, then reopen. The same operation as the Reset Live Sessions… action, without the dialog. Live sessions keep the daemon they started with through updates; the reset ends those sessions' processes at the next launch and recreates them on the current daemon, starting their captured commands again where possible. agshell quits and reopens itself right after answering. Running work in the affected sessions stops, and agent conversations may need to be resumed by hand. Run from inside one of those sessions, this kills the shell this agshellctl runs in. It refuses outside Live sessions mode, when a mode change is waiting for a restart, when the pane inventory is incomplete, and when nothing needs resetting. The next launch re-checks every session and only ever resets fewer than confirmed; the tree's liveReset reports the result.") { resetForce };
            reset.Validators.Add(parsed =>
            {
                if (!parsed.GetValue(resetForce))
                {
                    parsed.AddError("--force is required to end the processes in the affected live sessions");
                }
            });
            reset.SetAction(result => Send(result, "live.reset", null, new ControlArgs { Force = true }));
            live.Subcommands.Add(reset);
            Argument<string?> treeHost = new Argument<string?>("host") { Description = "Another machine's ssh destination; omit for this machine's own.", Arity = ArgumentArity.ZeroOrOne };
            Command tree = new CtlCommand("tree", "List the sessions that can be attached to, here or on another machine. With a HOST, runs over ssh against a machine that also runs agshell; only a session whose every pane is kept by a live daemon is listed, and an empty list is a successful answer. ssh runs with BatchMode, so key-based login must already work: a password or host-key prompt is a failure, not a question.") { treeHost };
            tree.SetAction(result => Send(result, "live.tree", null, new ControlArgs { Host = result.GetValue(treeHost) }));
            live.Subcommands.Add(tree);
            Argument<string> screenName = new Argument<string>("name") { Description = "The shell's name, as live list prints it." };
            Option<bool> screenAll = new Option<bool>("--all") { Description = "The scrollback too, not only the visible screen." };
            Option<int?> screenLines = new Option<int?>("--lines") { Description = "Keep only the last N lines of the whole buffer." };
            Command screen = new CtlCommand("screen", "Print a kept shell's screen as plain text, whether or not a window shows its pane.") { screenName, screenAll, screenLines };
            screen.Validators.Add(parsed =>
            {
                if (parsed.GetValue(screenAll) && parsed.GetValue(screenLines) is not null)
                {
                    parsed.AddError("use either --all or --lines, not both");
                }
                else if (parsed.GetValue(screenLines) is int count && count <= 0)
                {
                    parsed.AddError("--lines must be greater than 0");
                }
            });
            screen.SetAction(result => Send(result, "live.screen", null, new ControlArgs { Name = result.GetValue(screenName), All = Flag(result, screenAll), Lines = result.GetValue(screenLines) }));
            live.Subcommands.Add(screen);
            Option<string> killPane = new Option<string>("--pane") { Description = "Which pane's shell: left/primary/top or right/split/bottom.", Required = true };
            Option<bool> killForce = new Option<bool>("--force") { Description = "Required. Confirms destroying the process running in that pane." };
            Option<string> killTarget = new Option<string>("--target") { Description = "Session id or unique prefix. Required; 'active' is not accepted.", Required = true };
            Command kill = new CtlCommand("kill", "Destroy one pane's kept shell and the process running in it. A shown split then closes; a main pane hands the session to its split, or closes it; one in a closed window comes back as a fresh shell. No undo.") { killPane, killForce, killTarget, WindowOption };
            kill.Validators.Add(parsed =>
            {
                if (parsed.GetValue(killPane) is not ("left" or "primary" or "top" or "right" or "split" or "bottom"))
                {
                    parsed.AddError("--pane must be left/primary/top or right/split/bottom");
                }
                else if (!parsed.GetValue(killForce))
                {
                    parsed.AddError("--force is required to destroy a running process");
                }
                else if (parsed.GetValue(killTarget) == "active")
                {
                    parsed.AddError("--target must name a session; 'active' is not accepted here");
                }
                // --window narrows the search to one window; omitted, every window's shells are searched.
                else if (parsed.GetValue(WindowOption) == "active")
                {
                    parsed.AddError("--window must name a window; 'active' is not accepted here");
                }
            });
            kill.SetAction(result => Send(result, "live.kill", result.GetValue(killTarget), new ControlArgs { Pane = result.GetValue(killPane), Force = true }));
            live.Subcommands.Add(kill);
            Argument<string> attachHost = new Argument<string>("host") { Description = "The machine's ssh destination." };
            Argument<string> attachId = new Argument<string>("session") { Description = "The session id from live tree HOST, not its name." };
            Command attach = new CtlCommand("attach", "Attach a session of another machine as a remote row, split included; prints the row's id.") { attachHost, attachId, WindowOption };
            attach.SetAction(result => Send(result, "live.attach", result.GetValue(attachId), new ControlArgs { Host = result.GetValue(attachHost) }, echoId: true));
            live.Subcommands.Add(attach);
            Argument<string> proxied = new Argument<string>("pane") { Description = "The pane's daemon, as live tree names it." };
            Command proxy = new CtlCommand("proxy", "Relay a live pane's daemon on stdin and stdout, for agshell on another machine: ssh HOST agshellctl live proxy PANE.") { proxied };
            proxy.SetAction(result => LiveProxy.Run(result.GetValue(proxied)!));
            live.Subcommands.Add(proxy);
            Argument<string> presented = new Argument<string>("session") { Description = "The session id the other machine attached." };
            Command present = new CtlCommand("present", "Stream a session's status, context, notifications and HUD as JSON lines, for agshell on another machine.") { presented };
            present.SetAction(result => LivePresent.Run(result.GetValue(presented)!, result.GetValue(Pipe)));
            live.Subcommands.Add(present);
            root.Subcommands.Add(live);

            Command recent = new CtlCommand("recent", "Recently closed sessions and workspaces.");
            recent.Subcommands.Add(Simple("list", "List recently closed items, newest first, with their ids.", "recent.list"));
            Argument<string?> recentId = new Argument<string?>("id") { Description = "Entry id or unique prefix, from 'recent list'; omit for the newest.", Arity = ArgumentArity.ZeroOrOne };
            Command recentOpen = new CtlCommand("open", "Reopen a recently closed session or workspace (the newest when no id is given) with fresh shells. It goes to --window or the front window, unless another window already holds its session or workspace; --json names the window it went to. The window is not raised. An entry whose restore was incomplete stays in the list.") { recentId };
            recentOpen.SetAction(result => Send(result, "recent.open", result.GetValue(recentId), null));
            recent.Subcommands.Add(recentOpen);
            recent.Subcommands.Add(Simple("clear", "Forget the recently closed items.", "recent.clear"));
            root.Subcommands.Add(recent);

            Command theme = new CtlCommand("theme", "Terminal color themes, in Ghostty's format.");
            theme.Subcommands.Add(Simple("list", "List the installed themes; the one in force is marked with *.", "theme.list"));
            Argument<string?> themeName = new Argument<string?>("name") { Description = "The theme (the light one when following Windows); omit with no options for Ghostty's built-in colors (\"default ghostty\"), clearing the dark theme too.", Arity = ArgumentArity.ZeroOrOne };
            Option<string?> light = new Option<string?>("--light") { Description = "The light theme, the same slot as NAME." };
            Option<string?> dark = new Option<string?>("--dark") { Description = "The dark theme: the terminal then follows the Windows light/dark setting. 'none' stops that." };
            Command themeSet = new CtlCommand("set", "Set and save the theme.") { themeName, light, dark };
            themeSet.Check(parsed => parsed.GetValue(themeName) is not null && parsed.GetValue(light) is not null ? "Pass either a NAME or --light, not both." : null);
            themeSet.SetAction(result => Send(result, "theme.set", null, new ControlArgs { Name = result.GetValue(themeName), Light = result.GetValue(light), Dark = result.GetValue(dark) }));
            theme.Subcommands.Add(themeSet);
            root.Subcommands.Add(theme);

            Command config = new CtlCommand("config", "Config commands.");
            config.Subcommands.Add(Simple("reload", "Re-read and apply the AgShell-scoped ghostty.conf (prints the diagnostic count).", "config.reload"));
            root.Subcommands.Add(config);

            Command hooks = new CtlCommand("hooks", "Shell commands run on agshell events, from hooks.conf.");
            hooks.Subcommands.Add(Simple("reload", "Re-read and apply hooks.conf (prints the diagnostic count).", "hooks.reload"));
            hooks.Subcommands.Add(Simple("list", "Print the active hooks and diagnostics.", "hooks.list"));
            root.Subcommands.Add(hooks);
            root.Subcommands.Add(Events());
            root.Subcommands.Add(Install());
            AddWindowOption(root, string.Empty);
            AddConnectionOptions(root, string.Empty);
            // Only ours: --pipe and --json before the command's name too (K7, the user's decision of 2026-10-25).
            root.Options.Add(Pipe);
            root.Options.Add(Json);
            System.CommandLine.Help.HelpOption helpOption = root.Options.OfType<System.CommandLine.Help.HelpOption>().Single();
            helpOption.Action = new DiscussionHelpAction((System.CommandLine.Invocation.SynchronousCommandLineAction)helpOption.Action!);

            return root;
        }

        private static Command Workspace()
        {
            Command workspace = new CtlCommand("workspace", "Create, rename, select, reorder and delete workspaces.");

            Argument<string?> newName = new Argument<string?>("name") { Description = "Workspace name (defaults to the auto-generated name).", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> collapsed = new Option<bool>("--collapsed") { Description = "Create the workspace collapsed in the sidebar." };
            Command create = new CtlCommand("new", "Create a workspace.") { newName, collapsed };
            create.SetAction(result => Send(result, "workspace.new", null, new ControlArgs { Name = result.GetValue(newName), Collapsed = Flag(result, collapsed) }, echoId: true));
            workspace.Subcommands.Add(create);

            Argument<string> renameName = new Argument<string>("name") { Description = "New workspace name." };
            Option<string> renameTarget = Target("Target workspace id, unique prefix, or 'active'.");
            Command rename = new CtlCommand("rename", "Rename a workspace.") { renameName, renameTarget };
            rename.SetAction(result => Send(result, "workspace.rename", result.GetValue(renameTarget), new ControlArgs { Name = result.GetValue(renameName) }));
            workspace.Subcommands.Add(rename);

            workspace.Subcommands.Add(Targeted("delete", "Delete a workspace and its sessions.", "workspace.delete", "workspace"));
            workspace.Subcommands.Add(Targeted("select", "Select a workspace: its first session, or make an empty one current.", "workspace.select", "workspace"));
            Argument<string> focusMode = new Argument<string>("mode") { Description = "on (show only this workspace), off (unmark it), toggle (default), add (mark it too).", DefaultValueFactory = _ => "toggle" };
            Option<string> focusTarget = Target("Target workspace id, unique prefix, or 'active'.");
            Command focus = new CtlCommand("focus", "Mark workspaces for the sidebar focus filter.") { focusMode, focusTarget };
            focus.Check(parsed => parsed.GetValue(focusMode) is "on" or "off" or "toggle" or "add" ? null : "mode must be one of: on, off, toggle, add");
            focus.SetAction(result => Send(result, "workspace.focus", result.GetValue(focusTarget), new ControlArgs { Mode = result.GetValue(focusMode) }));
            workspace.Subcommands.Add(focus);
            Argument<string> filterMode = new Argument<string>("mode") { Description = "on, off, or toggle (default).", DefaultValueFactory = _ => "toggle" };
            Command filter = new CtlCommand("filter", "Turn the focus filter on or off, keeping the marked workspaces.") { filterMode };
            filter.Check(parsed => parsed.GetValue(filterMode) is "on" or "off" or "toggle" ? null : "mode must be on, off, or toggle");
            filter.SetAction(result => Send(result, "workspace.filter", null, new ControlArgs { Mode = result.GetValue(filterMode) }));
            workspace.Subcommands.Add(filter);
            Argument<string> flagAllMode = new Argument<string>("mode") { Description = "on, off, or toggle (default): toggle unflags only when every session is flagged.", DefaultValueFactory = _ => "toggle" };
            flagAllMode.Validators.Add(parsed =>
            {
                if (parsed.GetValueOrDefault<string>() is not ("on" or "off" or "toggle"))
                {
                    parsed.AddError("mode must be on, off, or toggle");
                }
            });
            Option<string> flagAllTarget = Target("Target workspace id, unique prefix, or 'active'.");
            Command flagAll = new CtlCommand("flag", "Flag or unflag every session of a workspace as one value.") { flagAllMode, flagAllTarget };
            flagAll.SetAction(result => Send(result, "workspace.flag", result.GetValue(flagAllTarget), new ControlArgs { Mode = result.GetValue(flagAllMode) }));
            workspace.Subcommands.Add(flagAll);
            workspace.Subcommands.Add(Targeted("collapse", "Collapse a workspace in the sidebar.", "workspace.collapse", "workspace"));
            workspace.Subcommands.Add(Targeted("expand", "Expand a workspace in the sidebar.", "workspace.expand", "workspace"));

            Option<string> goTo = Required("--to", "Direction: next or prev.");
            Command go = new CtlCommand("go", "Step to the next or previous workspace.") { goTo };
            go.SetAction(result => Send(result, "workspace.go", null, new ControlArgs { To = result.GetValue(goTo) }));
            workspace.Subcommands.Add(go);

            Option<string> moveTo = Required("--to", "Direction: up, down, top, or bottom.");
            Option<string> moveTarget = Target("Target workspace id, unique prefix, or 'active'.");
            Command move = new CtlCommand("move", "Reorder a workspace among its siblings.") { moveTo, moveTarget };
            move.SetAction(result => Send(result, "workspace.move", result.GetValue(moveTarget), new ControlArgs { To = result.GetValue(moveTo) }));
            workspace.Subcommands.Add(move);

            return workspace;
        }

        private static Command Session()
        {
            Command session = new CtlCommand("session", "Create, drive, read and close sessions.");

            Option<string?> cwd = new Option<string?>("--cwd") { Description = "Working directory (defaults to the home directory)." };
            Option<string?> workspace = new Option<string?>("--workspace") { Description = "Target workspace by id/prefix/active (defaults to the current one)." };
            Option<string?> workspaceName = new Option<string?>("--workspace-name") { Description = "Target workspace by name; errors if not found unless --create-workspace." };
            Option<bool> createWorkspace = new Option<bool>("--create-workspace") { Description = "With --workspace-name, create the workspace when it does not exist." };
            Option<string?> name = new Option<string?>("--name") { Description = "Initial session name (defaults to the directory name)." };
            Option<string?> shell = new Option<string?>("--shell") { Description = "Shell profile: pwsh, powershell, cmd or wsl:<distribution> (see 'agshellctl tree')." };
            Option<string?> after = new Option<string?>("--after") { Description = "Place the new session right after this session (id/prefix/active)." };
            Option<string?> before = new Option<string?>("--before") { Description = "Place the new session right before this session (id/prefix/active)." };
            Option<bool> noSelect = new Option<bool>("--no-select") { Description = "Create the session in the background without selecting it." };
            Option<string?> sessionCommand = new Option<string?>("--command") { Description = "Run this program instead of a shell; the session closes when it exits. No shell: operators and variables are not interpreted." };
            Option<bool> wait = new Option<bool>("--wait") { Description = "With --command: keep the session and its last output after the program exits, until a key." };
            Command create = new CtlCommand("new", "Create a session.") { cwd, workspace, workspaceName, createWorkspace, name, shell, after, before, noSelect, sessionCommand, wait };
            create.Check(
                parsed => parsed.GetValue(after) is not null && parsed.GetValue(before) is not null ? "use either --after or --before, not both" : null,
                // The anchor session already names the workspace, so placement cannot also address one.
                parsed => (parsed.GetValue(after) ?? parsed.GetValue(before)) is not null && (parsed.GetValue(workspace) ?? parsed.GetValue(workspaceName)) is not null ? "session.new takes --after/--before or a workspace, not both" : null,
                parsed => parsed.GetValue(workspace) is not null && parsed.GetValue(workspaceName) is not null ? "use either --workspace or --workspace-name, not both" : null,
                parsed => parsed.GetValue(createWorkspace) && parsed.GetValue(workspaceName) is null ? "--create-workspace requires --workspace-name" : null,
                parsed => parsed.GetValue(wait) && parsed.GetValue(sessionCommand) is null ? "--wait requires --command" : null);
            create.SetAction(result => Send(result, "session.new", null, new ControlArgs
            {
                Cwd = result.GetValue(cwd),
                Workspace = result.GetValue(workspace),
                WorkspaceName = result.GetValue(workspaceName),
                CreateWorkspace = Flag(result, createWorkspace),
                Name = result.GetValue(name),
                Shell = result.GetValue(shell),
                After = result.GetValue(after),
                Before = result.GetValue(before),
                NoSelect = Flag(result, noSelect),
                Command = result.GetValue(sessionCommand),
                Wait = Flag(result, wait),
            }, echoId: true));
            session.Subcommands.Add(create);

            Option<string> duplicateTarget = Target("Target session id, unique prefix, or 'active'.");
            Command duplicate = new CtlCommand("duplicate", "Open a new session right after this one, in its shell and directory.") { duplicateTarget };
            duplicate.SetAction(result => Send(result, "session.duplicate", result.GetValue(duplicateTarget), null, echoId: true));
            session.Subcommands.Add(duplicate);

            Option<string[]> closeTargets = Targets();
            Command close = new CtlCommand("close", "Close sessions.") { closeTargets };
            close.SetAction(result => SendBatch(result, "session.close", result.GetValue(closeTargets)!, new ControlArgs()));
            session.Subcommands.Add(close);

            session.Subcommands.Add(Targeted("select", "Select a session.", "session.select", "session"));
            Argument<string> flagMode = new Argument<string>("mode") { Description = "on, off, toggle (default: flags all unless all are flagged), or clear (unflags every session, ignoring the targets).", DefaultValueFactory = _ => "toggle" };
            Option<string[]> flagTargets = Targets();
            flagTargets.HelpName = "target";
            Command flag = new CtlCommand("flag", "Flag or unflag sessions: the flagged view lists them across workspaces.") { flagMode, flagTargets };
            flag.Check(parsed => parsed.GetValue(flagMode) is "on" or "off" or "toggle" or "clear" ? null : "mode must be on, off, toggle, or clear");
            flag.SetAction(result => result.GetValue(flagMode) == "clear"
                ? Send(result, "session.flag", null, new ControlArgs { Mode = "clear" })
                : SendBatch(result, "session.flag", result.GetValue(flagTargets)!, new ControlArgs { Mode = result.GetValue(flagMode) }));
            session.Subcommands.Add(flag);
            session.Subcommands.Add(Targeted("reveal", "Open the session's directory in Explorer.", "session.reveal", "session"));
            Option<string?> restartPane = PaneOption("Which pane to restart: left or right.");
            Option<string?> restartPaneId = new Option<string?>("--pane-id") { Description = "The pane's stable token ($AGSHELL_PANE_ID); it wins over --pane, and one that does not resolve is an error." };
            Option<string?> restartCommand = new Option<string?>("--command") { Description = "Shell line the new shell runs (at most 4096 bytes). Omit to run the pane's foreground program again." };
            Option<string> restartTarget = Target("Target session id, unique prefix, or 'active'.");
            Command restart = new CtlCommand("restart", "Replace the shell of one pane and run a shell line, or the same program again, in the new one. The pane keeps its place and id and starts with an empty screen; without --command it is refused when a shell holds the pane or the program is in restore-denylist.conf.") { restartCommand, restartPane, restartPaneId, restartTarget };
            restart.Check(parsed => parsed.GetValue(restartPane) is null && string.IsNullOrEmpty(parsed.GetValue(restartPaneId)) ? "provide --pane-id or --pane" : null, Checks.Pane(restartPane));
            restart.SetAction(result => Send(result, "session.restart", result.GetValue(restartTarget), new ControlArgs { Pane = result.GetValue(restartPane), PaneId = result.GetValue(restartPaneId), Command = result.GetValue(restartCommand) }));
            session.Subcommands.Add(restart);

            Option<string> goTo = Required("--to", "Direction: next, prev, first, last, next-attention, or prev-attention (attention = blocked/completed).");
            Command go = new CtlCommand("go", "Step through sessions in sidebar order.") { goTo };
            go.SetAction(result => Send(result, "session.go", null, new ControlArgs { To = result.GetValue(goTo) }));
            session.Subcommands.Add(go);

            Argument<string> renameName = new Argument<string>("name") { Description = "New session name; an empty one restores the directory name." };
            Option<string> renameTarget = Target("Target session id, unique prefix, or 'active'.");
            Command rename = new CtlCommand("rename", "Rename a session.") { renameName, renameTarget };
            rename.SetAction(result => Send(result, "session.rename", result.GetValue(renameTarget), new ControlArgs { Name = result.GetValue(renameName) }));
            session.Subcommands.Add(rename);

            Argument<string?> moveWorkspace = new Argument<string?>("workspace") { Description = "Destination workspace id/prefix. Omit with --to or --after/--before.", Arity = ArgumentArity.ZeroOrOne };
            Option<string?> moveTo = new Option<string?>("--to") { Description = "Reorder within the workspace: up, down, top, or bottom." };
            Option<string?> moveAfter = new Option<string?>("--after") { Description = "Place right after this session; its workspace is used." };
            Option<string?> moveBefore = new Option<string?>("--before") { Description = "Place right before this session; its workspace is used." };
            Option<string[]> moveTargets = Targets();
            Command move = new CtlCommand("move", "Move sessions to a workspace or reorder them.") { moveWorkspace, moveTo, moveAfter, moveBefore, moveTargets };
            // agterm: exactly one placement among a workspace, --to and --after/--before; the anchor carries its own workspace.
            move.Check(
                parsed => parsed.GetValue(moveAfter) is not null && parsed.GetValue(moveBefore) is not null ? "use either --after or --before, not both" : null,
                parsed => (parsed.GetValue(moveAfter) ?? parsed.GetValue(moveBefore)) is null ? null
                    : parsed.GetValue(moveTo) is not null ? "session.move takes --after/--before or --to, not both"
                    : parsed.GetValue(moveWorkspace) is not null ? "session.move takes --after/--before or a workspace, not both" : null,
                parsed => (parsed.GetValue(moveAfter) ?? parsed.GetValue(moveBefore)) is not null ? null
                    : parsed.GetValue(moveTargets)!.Length > 1 && parsed.GetValue(moveTo) is not null ? "session.move --target can be repeated only with a workspace or --after/--before"
                    : (parsed.GetValue(moveWorkspace), parsed.GetValue(moveTo)) switch
                    {
                        (null, null) => "provide a destination workspace, --to, or --after/--before",
                        (not null, not null) => "provide a destination workspace or --to, not both",
                        _ => null,
                    });
            move.SetAction(result => SendBatch(result, "session.move", result.GetValue(moveTargets)!, new ControlArgs
            {
                Workspace = result.GetValue(moveWorkspace),
                To = result.GetValue(moveTo),
                After = result.GetValue(moveAfter),
                Before = result.GetValue(moveBefore),
            }));
            session.Subcommands.Add(move);

            Argument<string?> text = new Argument<string?>("text") { Description = "Text to type; every newline is a Return. Omit with --stdin.", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> stdin = new Option<bool>("--stdin") { Description = "Read the text from stdin instead of an argument." };
            Option<string> typeTarget = Target("Target session id, unique prefix, or 'active'.");
            Option<string?> typePane = PaneOption("Which pane to type into: left (default), right or scratch.");
            Option<bool> typeSelect = new Option<bool>("--select") { Description = "Select the session first if its surface is not ready (main pane only; a split pane must already exist)." };
            Option<string?> typePaneId = new Option<string?>("--pane-id") { Description = "Stable pane token ($AGSHELL_PANE_ID); overrides --pane, and unknown without --pane is an error." };
            Command type = new CtlCommand("type", "Type text into a session.") { text, stdin, typeSelect, typePane, typeTarget, Connection, typePaneId };
            type.Check(Checks.Pane(typePane));
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

                if (value is null)
                {
                    return Usage(result, "provide TEXT or --stdin");
                }

                return Send(result, "session.type", result.GetValue(typeTarget), new ControlArgs { Text = value, Pane = result.GetValue(typePane), PaneId = result.GetValue(typePaneId), Select = Flag(result, typeSelect) });
            });
            session.Subcommands.Add(type);

            Option<bool> all = new Option<bool>("--all") { Description = "Read the screen and the whole scrollback instead of just the visible screen." };
            Option<int?> lines = new Option<int?>("--lines") { Description = "Keep only the last N lines of the whole buffer." };
            Option<string> textTarget = Target("Target session id, unique prefix, or 'active'.");
            Option<string?> textPane = PaneOption("Which pane to read: left, right or scratch (defaults to the focused one).");
            Option<string?> textPaneId = PaneIdOption();
            Command read = new CtlCommand("text", "Print a session's terminal text.") { all, lines, textPane, textPaneId, textTarget };
            read.Check(
                parsed => parsed.GetValue(all) && parsed.GetValue(lines) is not null ? "use either --all or --lines, not both" : null,
                parsed => parsed.GetValue(lines) is <= 0 ? "--lines must be greater than 0" : null,
                Checks.Pane(textPane));
            read.SetAction(result => Send(result, "session.text", result.GetValue(textTarget), new ControlArgs
            {
                All = Flag(result, all),
                Lines = result.GetValue(lines),
                Pane = result.GetValue(textPane),
                PaneId = result.GetValue(textPaneId),
            }));
            session.Subcommands.Add(read);

            Option<string> copyTarget = Target("Target session id, unique prefix, or 'active'.");
            Command copy = new CtlCommand("copy", "Print the selected text of the session's focused pane; the clipboard is left alone.") { copyTarget };
            copy.SetAction(result => Send(result, "session.copy", result.GetValue(copyTarget), null));
            session.Subcommands.Add(copy);
            session.Subcommands.Add(Targeted("select-all", "Select the whole buffer of the session's main pane; read it with session copy.", "session.selectall", "session"));
            Option<string> pasteTarget = Target("Target session id, unique prefix, or 'active'.");
            Option<string?> pastePane = PaneOption("Which pane to paste into: left (default), right or scratch.");
            Command paste = new CtlCommand("paste", "Paste the clipboard into a pane as Ctrl+V does; nothing is submitted.") { pastePane, pasteTarget };
            paste.Check(Checks.Pane(pastePane));
            paste.SetAction(result => Send(result, "session.paste", result.GetValue(pasteTarget), new ControlArgs { Pane = result.GetValue(pastePane) }));
            session.Subcommands.Add(paste);

            Argument<string?> pinCommand = new Argument<string?>("command") { Description = "The shell line the pane re-runs on every launch.", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> pinNone = new Option<bool>("--none") { Description = "Restore a plain shell, whatever the pane runs at quit." };
            Option<bool> pinClear = new Option<bool>("--clear") { Description = "Drop the pin and go back to what the pane runs at quit." };
            Option<string?> pinPane = PaneOption("Which pane: left (default) or right.");
            Option<string?> pinPaneId = PaneIdOption();
            Option<string> pinTarget = Target("Target session id, unique prefix, or 'active'.");
            Command pin = new CtlCommand("restore", "Pin what a pane re-runs on launch in rerun mode, instead of what it ran at quit.") { pinCommand, pinNone, pinClear, pinPane, pinPaneId, pinTarget };
            pin.Check(
                parsed => new[] { parsed.GetValue(pinCommand) is not null, parsed.GetValue(pinNone), parsed.GetValue(pinClear) }.Count(given => given) != 1 ? "provide exactly one of a COMMAND, --none, or --clear" : null,
                Checks.Pane(pinPane));
            pin.SetAction(result => Send(result, "session.restore", result.GetValue(pinTarget), new ControlArgs
            {
                Command = result.GetValue(pinCommand),
                Mode = result.GetValue(pinNone) ? "none" : result.GetValue(pinClear) ? "clear" : "set",
                Pane = result.GetValue(pinPane),
                PaneId = result.GetValue(pinPaneId),
            }));
            session.Subcommands.Add(pin);

            Argument<string?> needle = new Argument<string?>("needle") { Description = "Text to search for (omit to just open the bar).", Arity = ArgumentArity.ZeroOrOne };
            Option<bool> next = new Option<bool>("--next") { Description = "Step to the next match." };
            Option<bool> prev = new Option<bool>("--prev") { Description = "Step to the previous match." };
            Option<bool> closeSearch = new Option<bool>("--close") { Description = "Close the search bar." };
            Option<string> searchTarget = Target("Target session id, unique prefix, or 'active'.");
            Command search = new CtlCommand("search", "Search a session's terminal output: open the bar, set a needle, or step through matches; prints 'N of M'.")
            {
                needle, next, prev, closeSearch, searchTarget,
            };
            search.SetAction(result =>
            {
                // agterm: the three steps exclude each other, and close ignores a needle, so either is a usage error.
                if (new[] { result.GetValue(next), result.GetValue(prev), result.GetValue(closeSearch) }.Count(flag => flag) > 1)
                {
                    return Usage(result, "--next, --prev, and --close are mutually exclusive");
                }

                if (result.GetValue(closeSearch) && result.GetValue(needle) is not null)
                {
                    return Usage(result, "--close cannot be combined with a needle");
                }

                string? step = result.GetValue(next) ? "next" : result.GetValue(prev) ? "prev" : result.GetValue(closeSearch) ? "close" : null;
                return Send(result, "session.search", result.GetValue(searchTarget), new ControlArgs { Text = result.GetValue(needle), To = step });
            });
            session.Subcommands.Add(search);

            session.Subcommands.Add(Split());
            session.Subcommands.Add(Overlay());
            session.Subcommands.Add(Hud());

            session.Subcommands.Add(Targeted("swap", "Exchange the two panes of a split; the shells keep running.", "session.swap", "session"));

            Option<string?> leadPane = PaneOption("Which pane: left (default) or right.");
            Option<string> leadTarget = Target("Target session id, unique prefix, or 'active'.");
            Command lead = new CtlCommand("lead", "Take the lead of a live pane: the program gets this window's size, and the other machine's view of it is covered.") { leadPane, leadTarget };
            lead.Check(Checks.Pane(leadPane));
            lead.SetAction(result => Send(result, "session.lead", result.GetValue(leadTarget), new ControlArgs { Pane = result.GetValue(leadPane) }));
            session.Subcommands.Add(lead);

            Argument<string> focusPane = new Argument<string>("pane") { Description = "left, right, or other (default).", DefaultValueFactory = _ => "other" };
            Option<string> focusTarget = Target("Target session id, unique prefix, or 'active'.");
            Command focus = new CtlCommand("focus", "Focus a split session's pane by position or role.") { focusPane, focusTarget };
            focus.SetAction(result => Send(result, "session.focus", result.GetValue(focusTarget), new ControlArgs { Pane = result.GetValue(focusPane) }));
            session.Subcommands.Add(focus);

            Option<double?> ratio = new Option<double?>("--split-ratio") { Description = "Absolute primary-pane fraction 0..1 (left or top; e.g. 0.7). Clamped to 0.05..0.95." };
            Option<double?> growLeft = new Option<double?>("--grow-left") { Description = "Grow the left pane by this fraction (e.g. 0.05); shrinks the right." };
            Option<double?> growRight = new Option<double?>("--grow-right") { Description = "Grow the right pane by this fraction (e.g. 0.05); shrinks the left." };
            Option<double?> growPrimary = new Option<double?>("--grow-primary") { Description = "Grow the primary pane by this fraction." };
            Option<double?> growSplit = new Option<double?>("--grow-split") { Description = "Grow the split pane by this fraction." };
            Option<double?> growTop = new Option<double?>("--grow-top") { Description = "Alias for --grow-primary in a horizontal split." };
            Option<double?> growBottom = new Option<double?>("--grow-bottom") { Description = "Alias for --grow-split in a horizontal split." };
            Option<string> resizeTarget = Target("Target session id, unique prefix, or 'active'.");
            Command resize = new CtlCommand("resize", "Resize a split session's divider (set or nudge the primary-pane fraction); prints the applied fraction.") { ratio, growLeft, growRight, growPrimary, growSplit, growTop, growBottom, resizeTarget };
            resize.Validators.Add(parsed =>
            {
                List<double> given = new[] { ratio, growLeft, growRight, growPrimary, growSplit, growTop, growBottom }.Select(parsed.GetValue).OfType<double>().ToList();
                if (given.Count != 1)
                {
                    parsed.AddError("provide exactly one split ratio or grow option");
                }
                else if (!double.IsFinite(given[0]))
                {
                    parsed.AddError("the resize value must be a finite number");
                }
            });
            resize.SetAction(result =>
            {
                // agterm: the left/right and role/axis aliases map to one signed primary-pane nudge.
                double? grow = result.GetValue(growLeft) ?? result.GetValue(growPrimary) ?? result.GetValue(growTop);
                double? shrink = result.GetValue(growRight) ?? result.GetValue(growSplit) ?? result.GetValue(growBottom);
                ControlArgs args = result.GetValue(ratio) is double absolute ? new ControlArgs { Ratio = absolute } : new ControlArgs { RatioDelta = grow ?? -shrink };
                return Send(result, "session.resize", result.GetValue(resizeTarget), args);
            });
            session.Subcommands.Add(resize);

            Argument<string> scratchMode = new Argument<string>("mode") { Description = "on, off, or toggle (default). A hidden scratch keeps running.", DefaultValueFactory = _ => "toggle" };
            Option<string> scratchTarget = Target("Target session id, unique prefix, or 'active'.");
            Option<string?> scratchCommand = new Option<string?>("--command") { Description = "When showing, run this command as the scratch's process instead of a shell (run-once; respawns the scratch if one is already open)." };
            Option<bool> scratchFollow = new Option<bool>("--follow") { Description = "Select (switch to) the target session when showing its scratch (default: show without switching)." };
            Command scratch = new CtlCommand("scratch", "Show or hide the session's scratch shell.") { scratchMode, scratchCommand, scratchFollow, scratchTarget };
            scratch.SetAction(result => Send(result, "session.scratch", result.GetValue(scratchTarget), new ControlArgs { Mode = result.GetValue(scratchMode), Command = result.GetValue(scratchCommand), Follow = Flag(result, scratchFollow) }));
            session.Subcommands.Add(scratch);

            session.Subcommands.Add(Status());
            session.Subcommands.Add(Context());
            session.Subcommands.Add(Background());
            session.Subcommands.Add(Targeted("seen", "Clear the session's unseen-notification count without selecting it.", "session.seen", "session"));
            session.Subcommands.Add(Wait());
            return session;
        }

        private static Command Split()
        {
            Argument<string> mode = new Argument<string>("mode") { Description = "on, off, or toggle (default). A hidden split keeps running.", DefaultValueFactory = _ => "toggle" };
            Option<string?> axis = new Option<string?>("--axis") { Description = "vertical (left/right) or horizontal (top/bottom)." };
            Option<string> target = Target("Target session id, unique prefix, or 'active'.");
            Command split = new CtlCommand("split", "Show, hide or toggle a second shell in the session.") { mode, axis, target };
            split.Check(Axis(axis));
            split.SetAction(result => Send(result, "session.split", result.GetValue(target), new ControlArgs { Mode = result.GetValue(mode), Axis = result.GetValue(axis) }));
            // agterm's default subcommand: session split [mode] above is the same as session split visibility [mode].
            Argument<string> visibilityMode = new Argument<string>("mode") { Description = "on (show), off (hide), or toggle (default). Hidden panes stay alive.", DefaultValueFactory = _ => "toggle" };
            Option<string?> visibilityAxis = new Option<string?>("--axis") { Description = "Divider direction: vertical (left/right) or horizontal (top/bottom)." };
            Option<string> visibilityTarget = Target("Target session id, unique prefix, or 'active'.");
            Command visibility = new CtlCommand("visibility", "Show or hide a session split (on|off|toggle).") { visibilityMode, visibilityAxis, visibilityTarget };
            visibility.Check(Axis(visibilityAxis));
            visibility.SetAction(result => Send(result, "session.split", result.GetValue(visibilityTarget), new ControlArgs { Mode = result.GetValue(visibilityMode), Axis = result.GetValue(visibilityAxis) }));
            CtlCommand.AddDefault(split, visibility);
            split.Subcommands.Add(Targeted("close", "Close the split and end its shell.", "session.split.close", "session"));
            return split;
        }

        private static Command Surface()
        {
            Command surface = new CtlCommand("surface", "Window-level views of a terminal.");
            Argument<string> mode = new Argument<string>("mode") { Description = "show, hide, or toggle (default).", DefaultValueFactory = _ => "toggle" };
            Option<string?> target = new Option<string?>("--target") { Description = "active (default: the focused terminal), surface:<session>:<pane> from tree --json, or quick (the quick terminal fills its screen)." };
            Command zoom = new CtlCommand("zoom", "Fill the window with one terminal, or the screen with the quick terminal.") { mode, target };
            zoom.SetAction(result => Send(result, "surface.zoom", result.GetValue(target) is "active" or null ? null : result.GetValue(target), new ControlArgs { Mode = result.GetValue(mode) }));
            surface.Subcommands.Add(zoom);

            Option<string?> cursorTarget = new Option<string?>("--target") { Description = "active (default), a session with --pane-id, surface:<session>:<pane>, or quick." };
            Option<string?> cursorPaneId = new Option<string?>("--pane-id") { Description = "Stable pane token ($AGSHELL_PANE_ID). With it --target names the session ('active' or a session id), not a surface." };
            Command cursor = new CtlCommand("cursor", "Print the zero-based column of a terminal's cursor.") { cursorPaneId, cursorTarget };
            cursor.SetAction(result => Send(result, "surface.cursor", result.GetValue(cursorTarget) is "active" ? null : result.GetValue(cursorTarget), new ControlArgs { PaneId = result.GetValue(cursorPaneId) }));
            surface.Subcommands.Add(cursor);
            return surface;
        }

        /// <summary>agterm's --pane-id: the terminal's $AGSHELL_PANE_ID, which finds its pane wherever it moved.</summary>
        private static Option<string?> PaneIdOption()
        {
            return new Option<string?>("--pane-id") { Description = "The terminal's $AGSHELL_PANE_ID; overrides --pane while that terminal exists." };
        }

        private static Func<System.CommandLine.Parsing.CommandResult, string?> Axis(Option<string?> axis)
        {
            return parsed => parsed.GetValue(axis) is string value && value is not ("vertical" or "horizontal") ? "--axis must be vertical or horizontal" : null;
        }

        private static Option<string?> PaneOption(string description)
        {
            return new Option<string?>("--pane") { Description = description };
        }

        private static Command Font()
        {
            Command font = new CtlCommand("font", "Change a session's terminal font size.");
            foreach ((string name, string description, string cmd) in new[] { ("inc", "Increase the font size by one point.", "font.inc"), ("dec", "Decrease the font size by one point.", "font.dec"), ("reset", "Reset the font size to the default.", "font.reset") })
            {
                Option<string?> pane = new Option<string?>("--pane") { Description = "Which pane's font to change: left (main), right (split), or scratch (even when hidden). primary/left/top and split/right/bottom are aliases. Defaults to the left pane." };
                Option<string> target = Target("Target session id, unique prefix, or 'active'.");
                Command change = new CtlCommand(name, description) { target, Connection, pane };
                change.Check(Checks.Pane(pane));
                change.SetAction(result => Send(result, cmd, result.GetValue(target), new ControlArgs { Pane = result.GetValue(pane) }));
                font.Subcommands.Add(change);
            }
            return font;
        }

        private static Command Sidebar()
        {
            CtlCommand sidebar = new CtlCommand("sidebar", "Show, hide or resize the sidebar.");

            // agterm's default subcommand: sidebar [mode] is the same as sidebar visibility [mode].
            Argument<string> shown = new Argument<string>("mode") { Description = "show, hide, or toggle (default).", DefaultValueFactory = _ => "toggle" };
            sidebar.Add(shown);
            sidebar.SetAction(result => Send(result, "sidebar", null, new ControlArgs { Mode = result.GetValue(shown) }));
            Argument<string> mode = new Argument<string>("mode") { Description = "show, hide, or toggle (default).", DefaultValueFactory = _ => "toggle" };
            Command visibility = new CtlCommand("visibility", "Sidebar visibility (show|hide|toggle).") { mode };
            visibility.SetAction(result => Send(result, "sidebar", null, new ControlArgs { Mode = result.GetValue(mode) }));
            CtlCommand.AddDefault(sidebar, visibility);

            Argument<string> view = new Argument<string>("mode") { Description = "tree, flagged, or toggle (default).", DefaultValueFactory = _ => "toggle" };
            Command sidebarMode = new CtlCommand("mode", "Show the workspace tree or the flagged sessions.") { view };
            sidebarMode.Check(parsed => parsed.GetValue(view) is "tree" or "flagged" or "toggle" ? null : "mode must be tree, flagged, or toggle");
            sidebarMode.SetAction(result => Send(result, "sidebar.mode", null, new ControlArgs { Mode = result.GetValue(view) }));
            sidebar.Subcommands.Add(sidebarMode);

            Argument<double> width = new Argument<double>("points") { Description = "Sidebar width in points, clamped to the drag range." };
            Command resize = new CtlCommand("width", "Set the sidebar width in points; prints the width applied.") { width };
            resize.Check(parsed => double.IsFinite(parsed.GetValue(width)) ? null : "points must be a number");
            resize.SetAction(result => Send(result, "sidebar.width", null, new ControlArgs { SidebarWidth = result.GetValue(width) }));
            sidebar.Subcommands.Add(resize);

            sidebar.Subcommands.Add(Simple("expand", "Expand every workspace, including those the focus filter hides.", "sidebar.expand"));
            sidebar.Subcommands.Add(Simple("collapse", "Collapse every workspace except the current one.", "sidebar.collapse"));

            Argument<string> layout = new Argument<string>("layout") { Description = "flat, plain, tree, or toggle (default: tree for a flat list, flat for the tree).", DefaultValueFactory = _ => "toggle" };
            Command flaggedLayout = new CtlCommand("flagged-layout", "How the flagged view arranges its sessions.") { layout };
            flaggedLayout.Check(parsed => parsed.GetValue(layout) is "flat" or "plain" or "tree" or "toggle" ? null : "layout must be flat, plain, tree, or toggle");
            flaggedLayout.SetAction(result => Send(result, "sidebar.flagged-layout", null, new ControlArgs { Mode = result.GetValue(layout) }));
            sidebar.Subcommands.Add(flaggedLayout);

            return sidebar;
        }

        private static Command Simple(string name, string description, string cmd)
        {
            Command command = new CtlCommand(name, description);
            command.SetAction(result => Send(result, cmd, null, null));
            return command;
        }

        private static Command Targeted(string name, string description, string cmd, string noun)
        {
            Option<string> target = Target("Target " + noun + " id, unique prefix, or 'active'.");
            Command command = new CtlCommand(name, description) { target };
            command.SetAction(result => Send(result, cmd, result.GetValue(target), null));
            return command;
        }

        private static Option<string> Target(string description)
        {
            return new Option<string>("--target") { Description = description, DefaultValueFactory = _ => ControlTargets.Active };
        }

        private static Option<string[]> Targets()
        {
            return new Option<string[]>("--target") { HelpName = "targets", Description = "Target session id, prefix, or 'active'. Repeat for a batch.", DefaultValueFactory = _ => new[] { ControlTargets.Active } };
        }

        /// <summary>agterm's version: the app's version, then without --json the real path of this agshellctl.</summary>
        private static Command Version()
        {
            Command version = new CtlCommand("version", "Print the running AgShell version and the path of this agshellctl.");
            version.SetAction(result =>
            {
                int code = Send(result, "version", null, null);
                if (code == 0 && !result.GetValue(Json) && Environment.ProcessPath is string path)
                {
                    Console.WriteLine("client: " + (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path));
                }

                return code;
            });
            return version;
        }

        /// <summary>
        /// agterm's unknown option for --window, --pipe or --json where a command does not declare it, before its name too. The
        /// parser alone would take such an option before a positional argument as that argument and name the next word instead.
        /// </summary>
        internal static string? UnknownOption(ParseResult parsed, string[] args)
        {
            Option[] missing = new Option[] { WindowOption, Pipe, Json }.Where(option => !parsed.CommandResult.Command.Options.Contains(option)).ToArray();
            foreach (string arg in args.TakeWhile(arg => arg != "--"))
            {
                if (missing.Any(option => arg == option.Name || arg.StartsWith(option.Name + "=", StringComparison.Ordinal) || arg.StartsWith(option.Name + ":", StringComparison.Ordinal)))
                {
                    return "Unrecognized command or argument '" + arg + "'.";
                }
            }

            return null;
        }

        /// <summary>
        /// agterm's BasicOptions group: every command that runs takes --pipe and --json, groups and clipboard set do not.
        /// </summary>
        private static void AddConnectionOptions(Command command, string path)
        {
            foreach (Command sub in command.Subcommands)
            {
                string subPath = path.Length == 0 ? sub.Name : path + " " + sub.Name;
                if (sub.Action != null && subPath != "clipboard set")
                {
                    sub.Options.Add(Pipe);
                    sub.Options.Add(Json);
                }

                AddConnectionOptions(sub, subPath);
            }
        }

        private static void AddWindowOption(Command command, string path)
        {
            foreach (Command sub in command.Subcommands)
            {
                string subPath = path.Length == 0 ? sub.Name : path + " " + sub.Name;
                if (!sub.Options.Contains(WindowOption) && !NoWindow.Any(entry => entry == subPath || (entry.EndsWith(' ') && subPath.StartsWith(entry, StringComparison.Ordinal))))
                {
                    sub.Options.Add(WindowOption);
                }

                AddWindowOption(sub, subPath);
            }
        }

        private static Option<string> Required(string name, string description)
        {
            return new Option<string>(name) { Description = description, Required = true };
        }

        private static bool? Flag(ParseResult result, Option<bool> option)
        {
            return result.GetValue(option) ? true : null;
        }

        private static string ReadStdin()
        {
            using StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            return reader.ReadToEnd();
        }

        /// <summary>agterm's decodeTypedStdin: a type command's stdin is text, and one invalid byte refuses it whole.</summary>
        private static string? ReadTypedStdin()
        {
            using MemoryStream bytes = new MemoryStream();
            Console.OpenStandardInput().CopyTo(bytes);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static ControlReply? Request(ParseResult result, ControlRequest request)
        {
            if (result.GetValue(WindowOption) is string window && !request.Cmd.StartsWith("window.", StringComparison.Ordinal))
            {
                request = request with { Args = (request.Args ?? new ControlArgs()) with { Window = window } };
            }

            try
            {
                return ControlClient.SendAsync(ControlEndpoint.ResolvePipe(result.GetValue(Pipe)), request, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (ControlClientException ex)
            {
                Failure(ex.Message);
                return null;
            }
        }

        private static int SendBatch(ParseResult result, string cmd, string[] targets, ControlArgs args)
        {
            return targets.Length > 1
                ? Send(result, cmd, null, args with { Targets = targets.ToList() })
                : Send(result, cmd, targets.FirstOrDefault(), args);
        }

        private static int Send(ParseResult result, string cmd, string? target, ControlArgs? args, bool echoId = false)
        {
            ControlRequest request = new ControlRequest { Cmd = cmd, Target = target, Args = args };
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

            Console.WriteLine(Output.Format(reply.Response.Result, echoId));

            return 0;
        }

        /// <summary>A refusal from the app, printed as agterm prints a reply with ok false.</summary>
        private static int Error(string message)
        {
            Console.Error.WriteLine("error: " + message);
            return 1;
        }

        /// <summary>A failure of agshellctl itself, such as the pipe not answering: agterm's thrown error, Error: and exit 1.</summary>
        internal static int Failure(string message)
        {
            Console.Error.WriteLine("Error: " + message);
            return 1;
        }

        /// <summary>
        /// A usage error, refused before anything is sent: agterm's ValidationError, printed with the command's usage line
        /// and exit 64 (EX_USAGE).
        /// </summary>
        /// <summary>
        /// ArgumentParser's synopsis: the command's options and arguments in declaration order, the connection options where
        /// agterm declares its option group; past 12 of them only the positional and required ones, after [&lt;options&gt;].
        /// </summary>
        internal static string UsageLine(IReadOnlyList<Command> path)
        {
            string name = string.Join(' ', path.Skip(1).Select(command => command.Name).Prepend("agshellctl"));
            Command current = path[^1];
            List<Option> connection = new Option[] { Pipe, Json }.Where(current.Options.Contains).ToList();
            if (current.Options.Contains(WindowOption) && !(current is CtlCommand { Order: List<object> declared } && declared.Contains(WindowOption)))
            {
                connection.Add(WindowOption);
            }

            // A command with subcommands is agterm's group, which declares nothing of its own, its default subcommand's included.
            List<object> order = new List<object>();
            if (current.Subcommands.Count == 0)
            {
                order.AddRange(current is CtlCommand ordered ? ordered.Order : current.Options.Cast<object>().Concat(current.Arguments));
                order.AddRange(current.Options.Where(option => !connection.Contains(option) && !order.Contains(option)));
                order.AddRange(current.Arguments.Where(argument => !order.Contains(argument)));
                if (!order.Contains(ConnectionOptions.Here))
                {
                    order.Add(ConnectionOptions.Here);
                }
            }

            List<(string Text, bool Positional, bool Optional)> definition = new List<(string Text, bool Positional, bool Optional)>();
            foreach (object symbol in order.SelectMany(symbol => symbol == ConnectionOptions.Here ? connection.Cast<object>() : new[] { symbol }))
            {
                if (symbol is Option option && !option.Hidden && option is not System.CommandLine.Help.HelpOption && option is not VersionOption)
                {
                    string text = option.ValueType == typeof(bool) ? option.Name : option.Name + " <" + (option.HelpName ?? option.Name.TrimStart('-')) + ">";
                    if (option.Arity.MaximumNumberOfValues > 1)
                    {
                        text += " ...";
                    }

                    definition.Add((option.Required ? text : "[" + text + "]", false, !option.Required));
                }
                else if (symbol is Argument argument && !argument.Hidden)
                {
                    string text = "<" + (argument.HelpName ?? argument.Name) + ">" + (argument.Arity.MaximumNumberOfValues > 1 ? " ..." : "");
                    bool optional = argument.Arity.MinimumNumberOfValues == 0;
                    definition.Add((optional ? "[" + text + "]" : text, true, optional));
                }
            }

            string line = name;
            if (definition.Count > 12)
            {
                List<string> kept = definition.Where(each => each.Positional || !each.Optional).Select(each => each.Text).ToList();
                line += kept.Count > 0 && kept.Count <= 12 ? " [<options>] " + string.Join(' ', kept) : " <options>";
            }
            else if (definition.Count > 0)
            {
                line += " " + string.Join(' ', definition.Select(each => each.Text));
            }

            if (current.Subcommands.Count > 0)
            {
                line += " <subcommand>";
            }

            return line;
        }

        internal static int Usage(ParseResult result, string message)
        {
            List<Command> path = new List<Command>();
            for (System.CommandLine.Parsing.SymbolResult? at = result.CommandResult; at is not null; at = at.Parent)
            {
                if (at is System.CommandLine.Parsing.CommandResult commandResult)
                {
                    path.Insert(0, commandResult.Command);
                }
            }

            // ArgumentParser runs the default subcommand when none is named, so its usage is the one shown.
            if (path[^1] is CtlCommand { DefaultSubcommand: Command standIn })
            {
                path.Add(standIn);
            }

            string name = string.Join(' ', path.Skip(1).Select(command => command.Name).Prepend("agshellctl"));
            Console.Error.WriteLine("Error: " + message);
            Console.Error.WriteLine("Usage: " + UsageLine(path));
            Console.Error.WriteLine("  See '" + name + " --help' for more information.");
            return 64;
        }
    }

    internal static class Output
    {
        /// <summary>
        /// agterm's formatResponse: a result as its human-readable summary, checked in agterm's order, and a bare ok when it
        /// carries nothing to print. --json prints the server's own line instead.
        /// </summary>
        public static string? Format(ControlResult? result, bool echoId)
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (result is null)
            {
                return "ok";
            }

            if (result.Tree is not null)
            {
                return FormatTree(result.Tree);
            }

            if (result.Themes is List<string> themes)
            {
                return FormatThemes(themes, result.Theme, result.Sync == true, result.Light, result.Dark);
            }

            if (result.Remote is ControlRemoteTree remote)
            {
                return FormatRemoteTree(remote);
            }

            if (result.Live is ControlLiveInventory inventory)
            {
                return FormatLive(inventory);
            }

            if (result.Keymap is ControlKeymap keymap)
            {
                return FormatKeymap(keymap);
            }

            if (result.Hooks is ControlHooks hooks)
            {
                return FormatHooks(hooks);
            }

            if (result.Restore is ControlRestoreStatus restore)
            {
                return RestoreStatus(restore);
            }

            if (result.Recent is List<ControlRecentItem> recent)
            {
                // agterm's formatRecent: an entry a line with the full id recent open takes.
                return recent.Count == 0 ? "no recently closed items" : string.Join("\n", recent.Select(item => item.Id + "  " + item.Kind + "  " + item.Title + (item.Subtitle is string subtitle ? " (" + subtitle + ")" : string.Empty)));
            }

            if (result.RecentOpen is ControlRecentOpen reopened)
            {
                string line = "reopened " + reopened.Item.Kind + " " + reopened.Item.Title;
                return reopened.Complete ? line : line + " (incomplete: the entry stays in the list)";
            }

            if (result.App is ControlAppIdentity app)
            {
                return result.Installed is ControlAppIdentity installed && installed != app ? Identity(app) + "\ninstalled: " + Identity(installed) : Identity(app);
            }

            if (result.Text is not null)
            {
                return result.Text;
            }

            if (result.ExitCode is int exitCode)
            {
                return "exit " + exitCode.ToString(invariant);
            }

            if (result.Affected is int affected)
            {
                return affected == 1 ? "1 session" : affected.ToString(invariant) + " sessions";
            }

            if (result.Count is int count)
            {
                // keymap.reload reports its diagnostic count; none reads as a clean reload.
                return count == 0 ? "ok" : count.ToString(invariant) + " diagnostic(s)";
            }

            if (result.Cursor is ControlCursor cursor)
            {
                return cursor.Column.ToString(invariant);
            }

            if (result.Width is int width && result.Height is int height)
            {
                return width.ToString(invariant) + " " + height.ToString(invariant);
            }

            if (result.Ratio is double applied)
            {
                // agterm: the applied (clamped) primary-pane fraction, a bare number for scripts.
                return applied.ToString("0.000", invariant);
            }

            if (result.SidebarWidth is double sidebar)
            {
                return sidebar.ToString(invariant);
            }

            return echoId && result.Id is string id ? id : "ok";
        }

        /// <summary>
        /// agterm's formatThemes: a theme a line, the one in force marked "* "; following Windows, both of the pair are
        /// marked under a line naming them. agterm's first line, the terminal's own colors, has no counterpart: agshell's
        /// default is its agshell theme, which is in the list.
        /// </summary>
        private static string FormatThemes(List<string> themes, string? current, bool sync, string? light, string? dark)
        {
            // As agterm's: "default ghostty", no theme, leads the list, and names a side of the pair that has none.
            bool Active(string? name) => sync ? name is not null && (name == light || name == dark) : name == current;
            string Line(string? name, string label) => (Active(name) ? "* " : "  ") + label;
            string body = string.Join("\n", new[] { Line(null, "default ghostty") }.Concat(themes.Select(name => Line(name, name))));
            return sync ? "syncing with Windows appearance — light: " + (light ?? "default ghostty") + ", dark: " + (dark ?? "default ghostty") + "\n" + body : body;
        }

        /// <summary>
        /// agterm's formatZmx: the restore header, then a row per kept shell with the ids live kill takes, the owner's window
        /// state in its own column, so a closed window's shell at zero clients does not read as a leak.
        /// </summary>
        private static string FormatLive(ControlLiveInventory inventory)
        {
            List<string> lines = new List<string> { RestoreStatus(inventory.Restore) };
            if (!inventory.InventoryComplete)
            {
                lines.Add("inventory incomplete: some pane is unaccounted for, so nothing can be pruned");
            }

            if (inventory.Entries.Count == 0)
            {
                lines.Add("no daemons");
                return string.Join("\n", lines);
            }

            lines.Add(string.Empty);
            foreach (ControlLiveEntry entry in inventory.Entries)
            {
                string clients = entry.Clients is int count ? count + " client" + (count == 1 ? string.Empty : "s") : "-";
                string owner = "-";
                if (entry.SessionId is string session)
                {
                    string path = string.Join(" / ", entry.WindowName ?? entry.WindowState ?? "?", entry.WorkspaceName ?? "?", entry.SessionName ?? "?");
                    owner = session[..Math.Min(8, session.Length)] + " " + path + (entry.Pane is string pane ? " (" + pane + ")" : string.Empty)
                        + (entry.WindowId is string window ? " win " + window[..Math.Min(8, window.Length)] : string.Empty);
                }

                string state = entry.WindowState is string windowState ? entry.State + " [" + windowState + " window]" : entry.State;
                string observation = entry.Outdated == true ? entry.Observation + " outdated" : entry.Observation;
                lines.Add(entry.Daemon + "  " + state + "  " + observation + "  " + clients + "  " + owner);
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// agterm's formatKeymap: the built-in actions, a moved one marked *, a keyless one -, with every key joined by |; then
        /// the custom commands and the file's problems. agshell adds its global hotkey.
        /// </summary>
        private static string FormatKeymap(ControlKeymap keymap)
        {
            List<string> lines = new List<string> { "keymap: " + keymap.Path, string.Empty, "actions:" };
            int width = keymap.Actions.Count == 0 ? 0 : keymap.Actions.Max(action => action.Action.Length);
            foreach (ControlKeymapAction action in keymap.Actions)
            {
                string binds = string.Join("|", new[] { action.Chord }.OfType<string>().Concat(action.Alternates ?? new List<string>()));
                lines.Add("  " + (action.Overridden == true ? "*" : " ") + " " + action.Action.PadRight(width) + "  " + (binds.Length == 0 ? "-" : binds) + (action.Repeats == true ? "  --repeat" : string.Empty));
            }

            if (keymap.Commands.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("commands:");
                lines.AddRange(keymap.Commands.Select(command => "    " + command.Name + "  " + (command.Shortcut ?? "(palette only)") + (command.Repeats ? "  --repeat" : string.Empty)
                    + (command.ErrorHud ? "  --error-hud --error-position " + command.ErrorPosition + (command.ErrorPane is string pane ? " --error-pane " + pane : string.Empty) : string.Empty)));
            }

            if (keymap.GlobalHotkey is string hotkey)
            {
                lines.Add(string.Empty);
                lines.Add("global-hotkey: " + hotkey);
            }

            if (keymap.Diagnostics.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("diagnostics:");
                lines.AddRange(keymap.Diagnostics.Select(diagnostic => diagnostic.Line > 0 ? "    line " + diagnostic.Line + ": " + diagnostic.Message : "    " + diagnostic.Message));
            }

            if (keymap.Menu is List<ControlKeymapMenuItem> menu)
            {
                lines.Add(string.Empty);
                lines.Add("menu:");
                lines.AddRange(menu.Select(item => "    " + item.Chord + "  " + item.Menu + " ▸ " + item.Title
                    + (item.Enabled == false ? "  (disabled)" : string.Empty)));
            }

            return string.Join("\n", lines);
        }

        /// <summary>agterm's formatHooks: a row a hook in file order, with what it is doing, then the file's problems.</summary>
        private static string FormatHooks(ControlHooks hooks)
        {
            List<string> lines = new List<string> { "hooks: " + hooks.Path };
            if (hooks.Hooks.Count == 0)
            {
                lines.Add("  (no hooks)");
            }

            foreach (ControlHookEntry hook in hooks.Hooks)
            {
                string row = "  line " + hook.Line + ": on " + hook.Kind + " " + hook.Command;
                if (hook.RunningPid is int pid)
                {
                    row += "  running pid " + pid + (hook.ElapsedSeconds is double elapsed ? " for " + (int)elapsed + "s" : string.Empty);
                }

                row += hook.Pending > 0 ? "  pending " + hook.Pending : string.Empty;
                row += hook.Dropped > 0 ? "  dropped " + hook.Dropped : string.Empty;
                row += hook.LastFailure is string failure ? "  last failure: " + failure : string.Empty;
                row += hook.Retired == true ? "  (retired, removed from the file)" : string.Empty;
                row += "  runs " + hook.Runs + (hook.Failures > 0 ? ", failures " + hook.Failures : string.Empty);
                lines.Add(row);
            }

            if (hooks.Diagnostics.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("diagnostics:");
                lines.AddRange(hooks.Diagnostics.Select(diagnostic => "    line " + diagnostic.Line + ": " + diagnostic.Message));
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// agterm's formatRestoreStatus: what the next launch will do and what this one did are separate lines, so nobody
        /// wonders why nothing happened.
        /// </summary>
        private static string RestoreStatus(ControlRestoreStatus status)
        {
            List<string> lines = new List<string>
            {
                "configured: " + status.Configured + " (next launch)",
                "this launch: requested " + status.RequestedAtLaunch + ", active " + status.Active,
            };
            if (status.RestartRequired)
            {
                lines.Add("restart agshell to apply the configured mode");
            }

            if (status.UnavailableReason is string reason)
            {
                lines.Add("live unavailable: " + reason);
            }

            return string.Join("\n", lines);
        }

        /// <summary>agterm's formatIdentity: the version, with the commit when one was recorded.</summary>
        private static string Identity(ControlAppIdentity identity)
        {
            return string.IsNullOrEmpty(identity.Commit) ? identity.Version : identity.Version + " (" + identity.Commit + ")";
        }

        /// <summary>
        /// agterm's live tree text: one session a line with what it is for and what runs in it; an empty answer says so, as a
        /// caller cannot tell printed nothing from a failed read.
        /// </summary>
        private static string FormatRemoteTree(ControlRemoteTree tree)
        {
            string place = tree.Host is string host ? " on " + host : string.Empty;
            if (tree.Sessions.Count == 0)
            {
                return "no attachable sessions" + place;
            }

            return string.Join(Environment.NewLine, tree.Sessions.Select(session =>
            {
                string split = (session.Panes?.Count ?? 0) > 1 ? " (split" + (session.SplitAxis is string axis ? " " + axis : string.Empty) + ")" : string.Empty;
                List<string> running = (session.Panes ?? new List<ControlRemotePane>())
                    .Select(pane => pane.Foreground?.FirstOrDefault())
                    .OfType<string>()
                    .Select(program => program[(Math.Max(program.LastIndexOf('/'), program.LastIndexOf('\\')) + 1)..])
                    .ToList();
                string detail = string.Join("  ", new[] { session.Context, running.Count == 0 ? null : string.Join(" | ", running) }.OfType<string>());
                return "  " + session.Window + "/" + session.Workspace + "/" + session.Name + split + "  [" + session.Id + "]  " + session.Cwd + (detail.Length == 0 ? string.Empty : "  " + detail);
            }));
        }

        private static string FormatTree(ControlTree tree)
        {
            StringBuilder builder = new StringBuilder();
            foreach (ControlWorkspaceNode workspace in tree.Workspaces)
            {
                builder.Append(workspace.Active ? "* " : "  ").Append(workspace.Name).Append("  [").Append(workspace.Id).Append(']').Append(workspace.Focused == true ? " (focused)" : string.Empty).AppendLine();
                foreach (ControlSessionNode session in workspace.Sessions)
                {
                    builder.Append("  ").Append(session.Active ? "* " : "  ").Append(session.Name);
                    if (session.Flagged)
                    {
                        builder.Append(" (flagged)");
                    }

                    if (session.Status is not null)
                    {
                        builder.Append(" (").Append(session.Status).Append(')');
                    }

                    // agterm's tags in its order: split, not realized, overlay, scratch.
                    if (session.HasSplit == true)
                    {
                        builder.Append(session.Split ? " (split)" : " (split hidden)");
                    }

                    if (session.Realized == false)
                    {
                        builder.Append(" (not realized)");
                    }

                    if (session.Overlay)
                    {
                        builder.Append(session.HiddenOverlays?.Contains("session") == true ? " (overlay hidden)" : " (overlay)");
                    }

                    if (session.Scratch)
                    {
                        builder.Append(" (scratch)");
                    }

                    if (session.Hud is not null)
                    {
                        builder.Append(" (hud)");
                    }

                    if (session.PaneOverlays is { Count: > 0 } paneOverlays)
                    {
                        builder.Append(" (pane overlay: ").Append(string.Join(", ", paneOverlays)).Append(')');
                    }

                    builder.Append("  [").Append(session.Id).Append("]  ").Append(session.Shell);
                    if (session.RemoteHost is string remoteHost)
                    {
                        builder.Append("  @").Append(remoteHost);
                    }

                    if (session.Cwd.Length > 0)
                    {
                        builder.Append("  ").Append(session.Cwd);
                    }

                    if (session.SplitCwd is string splitCwd && splitCwd != session.Cwd)
                    {
                        builder.Append("  split cwd: ").Append(splitCwd);
                    }

                    if (session.Title is string title)
                    {
                        builder.Append("  title: ").Append(title);
                    }

                    // agterm's stream state on a viewer's row, and on the session's own machine who mirrors and presents it.
                    if (session.Presentation is ControlPresentationNode presentation)
                    {
                        builder.Append("  presentation: ").Append(presentation.State).Append(presentation.Mode == "presenter" ? ", presenter" : string.Empty);
                        if (presentation.Error is string error)
                        {
                            builder.Append(" (").Append(error).Append(')');
                        }
                    }

                    if (session.Presenters is ControlPresentersNode presenters)
                    {
                        builder.Append(presenters.Presenter == true ? "  presented remotely" : string.Empty)
                            .Append(presenters.Mirrors > 0 ? "  mirrored by: " + presenters.Mirrors.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
                    }

                    builder.AppendLine();
                }
            }

            return builder.ToString().TrimEnd();
        }
    }
}
