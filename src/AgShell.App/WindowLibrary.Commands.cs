using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// Routing and agterm's window commands. A command names its window with --window; without one it goes to the window
    /// holding its target session or workspace, else to the front one. window.* take the window as their target.
    /// </summary>
    internal sealed partial class WindowLibrary
    {
        /// <summary>
        /// agterm's page-serve and job-run claims, routed to the window holding the page or the job (its session may have
        /// closed since): an ok answer, then the connection is that page's channel or that job's until it ends.
        /// </summary>
        private async Task ServePageAsync(ControlRequest request, Stream connection)
        {
            if (request.Cmd == ControlEndpoint.PresentCommand)
            {
                await PresentAsync(request, connection);
                return;
            }

            if (request.Cmd == ControlEndpoint.JobRunCommand)
            {
                await RunJobAsync(request, connection);
                return;
            }

            string? token = request.Target?.Trim();
            string? refusal = string.IsNullOrEmpty(token) ? "session.overlay.page.serve requires a page token" : !Guid.TryParse(token, out _) ? "invalid page token" : null;
            PageServeStream? stream = refusal is not null ? null : await Application.Current.Dispatcher.InvokeAsync(() =>
                open.FirstOrDefault(window => window.HasRemotePageToken(token)) is MainWindow holder ? holder.ClaimPageChannel(token!, connection) : null);
            ControlResponse answer = stream is null ? Fail(refusal ?? "page not claimable") : new ControlResponse { Ok = true };
            try
            {
                await connection.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ControlJson.Serialize(answer) + "\n"));
                await connection.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                stream?.Shutdown();
                return;
            }

            if (stream is not null)
            {
                await stream.RunAsync();
            }
        }

        /// <summary>agterm's job-run claim: the ok answer with the job's id, then the launch context on the same connection.</summary>
        private async Task RunJobAsync(ControlRequest request, Stream connection)
        {
            string? id = request.Target?.Trim();
            string? refusal = string.IsNullOrEmpty(id) ? "session.overlay.job.run requires a job id" : !Guid.TryParse(id, out _) ? "invalid job id" : null;
            OverlayJobStream? stream = refusal is not null ? null : await Application.Current.Dispatcher.InvokeAsync(() =>
                open.FirstOrDefault(window => window.HasOverlayJob(id)) is MainWindow holder ? holder.ClaimOverlayJob(id!, connection) : null);
            ControlResponse answer = stream is null ? Fail(refusal ?? "job not claimable") : new ControlResponse { Ok = true, Result = new ControlResult { Id = id } };
            try
            {
                await connection.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ControlJson.Serialize(answer) + "\n"));
                await connection.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // The claim stands: reading the closed connection ends the job unknown, as a helper gone after its claim.
            }

            if (stream is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(stream.Start);
                await stream.RunAsync();
            }
        }

        /// <summary>Any window's model changed: the presentation streams look again.</summary>
        internal event Action? ModelChanged;

        internal void NotifyModelChanged()
        {
            ModelChanged?.Invoke();
        }

        /// <summary>A stream on an open connection, in the window holding its session: its role stays until it closes.</summary>
        internal void HoldPresentationStream(string token)
        {
            foreach (MainWindow window in open)
            {
                window.HoldPresentationStream(token);
            }
        }

        /// <summary>agterm's present: the connection is the session's presentation stream until either side ends it.</summary>
        private async Task PresentAsync(ControlRequest request, Stream connection)
        {
            PresentStream stream = new PresentStream(this, connection, request.Target ?? string.Empty, Application.Current.Dispatcher);
            ControlResponse answer = await Application.Current.Dispatcher.InvokeAsync(stream.OpenAsync).Task.Unwrap();
            try
            {
                await connection.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ControlJson.Serialize(answer) + "\n"));
                await connection.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
            }

            if (answer.Ok)
            {
                await stream.RunAsync();
            }
            else
            {
                await connection.DisposeAsync();
            }
        }

        /// <summary>
        /// agterm's zmx kill: ends one pane's kept shell and what runs in it. It destroys a process rather than a model object,
        /// reaches panes no window shows and every machine attached, so the session, the pane and --force are all required
        /// and nothing falls back to what is in front. A shown pane then goes as if its shell ended: a split closes, a main
        /// pane hands the session to its split or closes it, with no undo; one in a closed window comes back as a fresh shell.
        /// </summary>
        private async Task<ControlResponse> LiveKillAsync(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if (string.IsNullOrEmpty(request.Target))
            {
                return Fail("live.kill requires an explicit --target");
            }

            if (request.Target == "active")
            {
                return Fail("live.kill needs a session id; 'active' is not accepted");
            }

            if (args.Window == "active")
            {
                return Fail("live.kill needs a window id; 'active' is not accepted");
            }

            PaneRole? role = args.Pane switch
            {
                "left" or "primary" or "top" => PaneRole.Main,
                "right" or "split" or "bottom" => PaneRole.Split,
                _ => null,
            };
            if (role is not PaneRole pane)
            {
                return Fail("live.kill requires --pane left|right");
            }

            if (args.Force != true)
            {
                return Fail("live.kill requires --force");
            }

            (ControlLiveInventory? inventory, string? failure) = await LiveInventoryAsync();
            if (inventory is null)
            {
                return Fail(failure!);
            }

            // agterm resolves against the inventory, which reaches closed and unindexed claims no window shows: an explicit
            // --window narrows the claims before the session resolves, so a prefix shared across windows can be told apart.
            string side = pane == PaneRole.Main ? "left" : "right";
            List<ControlLiveEntry> owned = inventory.Entries.Where(entry => entry.Pane == side && entry.SessionId is not null).ToList();
            if (args.Window is { Length: > 0 } selector)
            {
                if (ResolveId(selector, inventory.Entries.Select(entry => entry.WindowId).OfType<string>()) is not string windowId)
                {
                    return Fail("no such window: " + selector);
                }

                owned = owned.Where(entry => entry.WindowId == windowId).ToList();
            }

            if (ResolveId(request.Target, owned.Select(entry => entry.SessionId!)) is not string sessionId || owned.FirstOrDefault(entry => entry.SessionId == sessionId) is not ControlLiveEntry row)
            {
                return Fail("no " + side + " pane daemon for session " + request.Target);
            }

            if (LiveInventory.KillRefusal(row) is string refusal)
            {
                return Fail(refusal);
            }

            // Only the daemon's own word that it ended may take a pane down: a kill it did not confirm leaves the pane as it is.
            if (await Daemons.KillConfirmedAsync(row.Daemon) is string reason)
            {
                return Fail("could not kill " + row.Daemon + ": " + reason);
            }

            Guid session = Guid.Parse(sessionId);
            open.FirstOrDefault(window => window.LivePane(row.Daemon) is not null)?.KillLivePane(session, pane);
            return Ok(new ControlResult { Id = session.ToString(), Text = "killed " + row.Daemon, Pane = side });
        }

        /// <summary>agterm's ControlResolve over a fixed set of ids: the exact id, else a prefix only one has; null otherwise.</summary>
        private static string? ResolveId(string asked, IEnumerable<string> ids)
        {
            List<string> distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.FirstOrDefault(id => string.Equals(id, asked, StringComparison.OrdinalIgnoreCase)) is string exact)
            {
                return exact;
            }

            List<string> matches = distinct.Where(id => id.StartsWith(asked, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        /// <summary>
        /// agterm's zmx screen: a kept shell's screen as plain text, whether or not a window shows its pane, by the name live
        /// list prints, read from its daemon's own terminal. The visible screen at its leader's size; --all the history too;
        /// --lines the last lines of the whole buffer.
        /// </summary>
        private async Task<ControlResponse> LiveScreenAsync(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            // agterm's isPlain: the name is echoed in the refusal, so one with whitespace or control characters is refused first.
            if (args.Name?.Trim() is not { Length: > 0 } name || name.Any(c => char.IsWhiteSpace(c) || c < ' ' || c == '\u007f'))
            {
                return Fail("live.screen requires a daemon name");
            }

            if (args.All == true && args.Lines is not null)
            {
                return Fail("use either --all or --lines, not both");
            }

            if (args.Lines is int asked && asked <= 0)
            {
                return Fail("--lines must be greater than 0");
            }

            if (await Daemons.ScreenAsync(name, args.All == true || args.Lines is not null) is not DaemonScreen screen)
            {
                return Fail("could not read the live screen of " + name);
            }

            return Ok(new ControlResult { Text = args.Lines is int lines ? screen.LastLines(lines) : screen.Text });
        }

        /// <summary>A page's request, routed as one from agshellctl: agterm's bridge goes through the socket's dispatch.</summary>
        internal Task<ControlResponse> DispatchAsync(ControlRequest request)
        {
            return HandleAsync(request);
        }

        /// <summary>The window list as last read on the window's thread, for answers that need no window; empty until the first.</summary>
        private volatile List<ControlWindowNode> cachedWindows = new List<ControlWindowNode>();

        /// <summary>agterm reads --window trimmed, and a blank one as not given.</summary>
        private static ControlRequest TrimWindow(ControlRequest request)
        {
            if (request.Args?.Window is not string window)
            {
                return request;
            }

            string trimmed = window.Trim();
            return request with { Args = request.Args with { Window = trimmed.Length == 0 ? null : trimmed } };
        }

        /// <summary>agterm: a command's answer and the cached window list come from the same turn, so the cache never lags it.</summary>
        private async Task<ControlResponse> HandleAndCacheAsync(ControlRequest request)
        {
            ControlResponse response = await HandleAsync(TrimWindow(request), request);
            RefreshWindowCache();
            return response;
        }

        private void RefreshWindowCache()
        {
            cachedWindows = entries.Select(Node).ToList();
        }

        /// <summary>
        /// agterm's fastPathResponse, off the window's thread: window.list, and tree --window naming a closed window, from
        /// the cache, so polls are answered while a window is busy. Null to go to the window: a cold cache or anything else.
        /// </summary>
        private ControlResponse? CachedResponse(ControlRequest request)
        {
            List<ControlWindowNode> nodes = cachedWindows;
            if (nodes.Count == 0)
            {
                return null;
            }

            if (request.Cmd == "window.list")
            {
                return Ok(new ControlResult { Windows = nodes });
            }

            if (request.Cmd != "tree" || string.IsNullOrEmpty(request.Args?.Window))
            {
                return null;
            }

            List<Guid> ids = nodes.Select(node => Guid.Parse(node.Id)).ToList();
            Guid? active = nodes.FirstOrDefault(node => node.Active) is ControlWindowNode front ? Guid.Parse(front.Id) : null;
            TargetResolution resolution = ControlTargets.Resolve(request.Args.Window, ids, active);
            return resolution.Match == TargetMatch.Resolved && nodes.First(node => Guid.Parse(node.Id) == resolution.Id) is { Open: false }
                ? Fail("window not open — window.select it first")
                : null;
        }

        private async Task<ControlResponse> HandleAsync(ControlRequest request, ControlRequest? replyRequest = null)
        {
            if (ControlDispatcher.HooksRefusal(request) is string hooksRefused)
            {
                return Fail(hooksRefused);
            }

            if (request.Cmd is "keymap.list" or "keymap.reload")
            {
                MainWindow? front = Active ?? open.FirstOrDefault();
                if (front is null) return Fail("no open window");
                if (request.Cmd == "keymap.reload")
                {
                    foreach (MainWindow other in open.Where(window => window != front)) other.ReloadConfig(true, false);
                }

                return await front.HandleAsync(request with { Target = null, Args = request.Args is null ? null : request.Args with { Window = null } });
            }

            if (request.Cmd.StartsWith("window.", StringComparison.Ordinal))
            {
                return WindowCommand(request, replyRequest);
            }

            // agterm's config.reload: app-global, the count of ghostty.conf's problems.
            if (request.Cmd == "config.reload")
            {
                return Ok(new ControlResult { Count = ReloadGhostty() });
            }

            if (request.Cmd == "events.read")
            {
                return ReadEvents(request);
            }

            // agterm's restore.clear and restore.capture: every open window's panes, and an answer that waits on the saves.
            if (request.Cmd == "restore.clear")
            {
                bool cleared = open.ToList().Select(window => window.ClearRestoreAndSave()).ToList().All(saved => saved);
                return cleared
                    ? new ControlResponse { Ok = true }
                    : Fail("cleared every open pane but at least one window's save failed; those windows keep their captured commands on disk until they save successfully");
            }

            if (request.Cmd == "restore.capture")
            {
                if (Settings.EffectiveRestoreMode != CommandRestore.Rerun)
                {
                    return Fail("restore.capture requires rerun mode; configured restore mode is " + Settings.EffectiveRestoreMode);
                }

                List<(int Count, bool Saved)> captures = open.ToList().Select(window => window.CaptureRestoreAndSave()).ToList();
                int captured = captures.Sum(capture => capture.Count);
                string panes = "captured " + captured + " pane" + (captured == 1 ? string.Empty : "s");
                return captures.All(capture => capture.Saved)
                    ? Ok(new ControlResult { Count = captured, Text = panes })
                    : Fail(panes + " but at least one window's save failed; failed windows keep their argv in memory until they save successfully");
            }

            if (request.Cmd == "live.reset")
            {
                return await LiveResetCommandAsync(request);
            }

            if (request.Cmd == "live.tree")
            {
                // agterm: the host is trimmed, and none is this app's own sessions.
                return await LiveTreeAsync(request.Args?.Host?.Trim() is { Length: > 0 } asked ? asked : null);
            }

            if (request.Cmd == "live.screen")
            {
                return await LiveScreenAsync(request);
            }

            if (request.Cmd == "live.kill")
            {
                return await LiveKillAsync(request);
            }

            if (request.Cmd == "live.attach")
            {
                return await LiveAttachAsync(request);
            }

            if (ControlDispatcher.RecentRefusal(request) is string refused)
            {
                return Fail(refused);
            }

            if (request.Cmd == "recent.open")
            {
                return OpenRecent(request);
            }

            if (request.Cmd is "quick" or "quick.type" or "quick.text" || (request.Cmd is "surface.zoom" or "surface.cursor" && request.Target == "quick"))
            {
                return await QuickCommandAsync(request);
            }

            MainWindow? window;
            if (request.Args?.Window is string selector)
            {
                (WindowEntry? entry, string? error) = ResolveEntry(selector);
                if (entry is null)
                {
                    return Fail(error!);
                }

                window = open.FirstOrDefault(candidate => candidate.Entry == entry);
                if (window is null)
                {
                    if (ClosedModalResponse(request, entry) is ControlResponse kept)
                    {
                        return kept;
                    }

                    return Fail("window not open — window.select it first");
                }
            }
            else
            {
                (MainWindow? holder, string? error) = WindowOfTarget(request);
                if (error is not null)
                {
                    return Fail(error);
                }

                // agterm keeps a closed window's picker and question results: a poll after the close still gets its answer.
                if (holder is null && ClosedModalResponse(request) is ControlResponse kept)
                {
                    return kept;
                }

                window = holder ?? Active;
            }

            if (window is null)
            {
                return Fail("no open window");
            }

            ControlResponse response = await window.HandleAsync(request);
            // The tree names the window it is of; the quick panel is the app's.
            return response.Result?.Tree is ControlTree tree
                ? response with
                {
                    Result = response.Result with
                    {
                        Tree = tree with
                        {
                            WindowId = window.Entry.Id.Wire(),
                            WindowName = Label(window.Entry),
                            IndexUnsaved = IndexUnsaved ? true : null,
                            LiveReset = LiveResetReadback(),
                            QuickVisible = Quick.Shown,
                            // The panel's zoom is the app's and comes first, as agterm reports it.
                            ZoomedSurface = Quick.Shown && Quick.Zoomed ? "quick" : tree.ZoomedSurface,
                        },
                    },
                }
                : response;
        }

        /// <summary>
        /// agterm's recent open: the window first — --window, which must be open, or the front one — then the entry, which goes
        /// there unless another open window already holds its session or workspace. The window is not raised.
        /// </summary>
        private ControlResponse OpenRecent(ControlRequest request)
        {
            MainWindow? requested = null;
            if (request.Args?.Window is string selector)
            {
                (WindowEntry? entry, string? windowError) = ResolveEntry(selector);
                if (entry is null)
                {
                    return Fail(windowError!);
                }

                requested = open.FirstOrDefault(candidate => candidate.Entry == entry);
                if (requested is null)
                {
                    return Fail("window not open — window.select it first");
                }
            }
            else if (Active is null)
            {
                return Fail("no open window");
            }

            (RecentClosedItem? item, string? error) = Recent.Resolve(request.Target);
            if (item is null)
            {
                return Fail(error!);
            }

            ControlRecentItem reopened = ControlDispatcher.RecentNode(item);
            if (ReopenRecentOutcome(item, requested) is not (MainWindow window, bool complete))
            {
                return Fail("could not reopen " + item.Id.Wire());
            }

            // agterm's recentOpen names the window the entry went to.
            return Ok(new ControlResult { RecentOpen = new ControlRecentOpen { Item = reopened, Complete = complete, Window = window.Entry.Id.Wire() } });
        }

        /// <summary>
        /// agterm's quick commands. quick show keeps the panel up (pinned); quick type and text need its shell; surface zoom
        /// --target quick fills the panel's screen while it is shown.
        /// </summary>
        /// <summary>
        /// agterm's poll for the quick terminal: a look now and twelve more thirty milliseconds apart, so quick show then
        /// quick type works. Null once its shell runs; the reason otherwise: no window, never shown, or not up yet.
        /// </summary>
        private async Task<string?> WaitForQuickAsync()
        {
            if (open.Count == 0)
            {
                return "no open window";
            }

            for (int attempt = 0; attempt <= 12; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(30);
                }

                if (Quick.Running)
                {
                    return null;
                }

                if (!Quick.Shown)
                {
                    return "quick terminal not open";
                }
            }

            return "realized";
        }

        private async Task<ControlResponse> QuickCommandAsync(ControlRequest request)
        {
            string? mode = request.Args?.Mode;
            switch (request.Cmd)
            {
                case "quick":
                    if (mode is not (null or "show" or "hide" or "toggle"))
                    {
                        return Fail("invalid quick mode: " + mode);
                    }

                    bool show = mode == "show" || (mode != "hide" && !Quick.Shown);
                    // agterm: show carries the pin even when the panel is up, so it is refused under a pending modal either way.
                    if (show && Active?.PendingModalError is string modal)
                    {
                        return Fail(modal);
                    }

                    Quick.SetShown(show, pinned: show);
                    return Ok(new ControlResult());
                case "surface.cursor":
                    if (request.Args?.PaneId is not null)
                    {
                        return Fail("surface.cursor: --pane-id takes a session target");
                    }

                    return await Quick.CursorAsync() is string column && int.TryParse(column, out int value)
                        ? Ok(new ControlResult { Id = "quick", Cursor = new ControlCursor { Column = value } })
                        : Fail("surface not realized");
                case "surface.zoom":
                    // agterm's dispatcher reads the mode before the target.
                    if (mode is not (null or "show" or "hide" or "toggle"))
                    {
                        return Fail("invalid surface zoom mode: " + mode);
                    }

                    if (!Quick.Shown)
                    {
                        return Fail("surface not available: quick");
                    }

                    Quick.SetZoom(mode == "show" || (mode != "hide" && !Quick.Zoomed));
                    return Ok(new ControlResult { Id = "quick" });
                case "quick.type":
                    if (request.Args?.Text is not string text)
                    {
                        return Fail("quick.type requires text");
                    }

                    if (text.Contains('\0'))
                    {
                        return Fail("text must not contain a NUL byte");
                    }

                    if (await WaitForQuickAsync() is string notReady)
                    {
                        return Fail(notReady == "realized" ? "quick terminal not realized" : notReady);
                    }

                    Keystrokes keystrokes = Keystrokes.From(text);
                    Quick.Write(keystrokes.Head);
                    if (keystrokes.PacedReturn)
                    {
                        await Task.Delay(Keystrokes.SubmitGap);
                        Quick.Write("\r");
                    }

                    return Ok(new ControlResult());
                default:
                    if (request.Args?.All == true && request.Args.Lines is not null)
                    {
                        return Fail("use either --all or --lines, not both");
                    }

                    if (request.Args?.Lines <= 0)
                    {
                        return Fail("--lines must be greater than 0");
                    }

                    // agterm's readQuickText: an unrealized panel is a failed read.
                    if (await WaitForQuickAsync() is string unread)
                    {
                        return Fail(unread == "realized" ? "failed to read surface buffer" : unread);
                    }

                    return await Quick.ReadTextAsync(request.Args?.All == true, request.Args?.Lines) is string read
                        ? Ok(new ControlResult { Text = read })
                        : Fail("failed to read surface buffer");
            }
        }

        /// <summary>
        /// agterm's zmx attach: the machine is asked first and the session found there by its id; only then is the window
        /// resolved, the front one when none was named, so a refusal leaves nothing half made.
        /// </summary>
        private async Task<ControlResponse> LiveAttachAsync(ControlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Args?.Host))
            {
                return Fail("live.attach requires a host");
            }

            if (string.IsNullOrWhiteSpace(request.Target))
            {
                return Fail("live.attach requires a remote session");
            }

            string host = request.Args.Host.Trim();
            string id = request.Target.Trim();
            // agterm's plain value: what goes on the remote command line is one word.
            if (id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return Fail("invalid remote session");
            }

            if (!RemoteHosts.IsHost(host))
            {
                return Fail("invalid host");
            }

            (ControlRemoteTree? tree, string? failure) = await RemoteHosts.ListAsync(host);
            if (tree is null)
            {
                return Fail(failure ?? "ssh " + host + " failed");
            }

            // By id only, as agterm: names repeat and change, and live tree prints the id for exactly this hand-off.
            if (tree.Sessions.FirstOrDefault(session => string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase)) is not ControlRemoteSession found
                || !Guid.TryParse(found.Id, out Guid remoteId))
            {
                return Fail("no attachable session " + id + " on " + host);
            }

            if (MainWindow.RemotePanesFailure(host, found) is string unaddressable)
            {
                return Fail(unaddressable);
            }

            MainWindow? target = Active;
            if (request.Args.Window is string selector && selector != ControlTargets.Active)
            {
                (WindowEntry? named, string? error) = ResolveEntry(selector);
                target = named is null ? null : open.FirstOrDefault(window => window.Entry == named);
                if (target is null)
                {
                    return Fail(error ?? "window not open — window.select it first");
                }
            }

            // agterm's resolveOpenWindow: no window in front is no open window; one with no workspace has nowhere to attach into.
            return target is null ? Fail("no open window") : target.AttachRemoteFound(host, remoteId, found, tree.Presentation);
        }

        /// <summary>
        /// agterm's zmx tree: the sessions of every open window whose shells live daemons keep, for agshell on another
        /// machine to list and attach through live proxy; with a host, that machine's, through ssh.
        /// </summary>
        private async Task<ControlResponse> LiveTreeAsync(string? host)
        {
            if (host is not null)
            {
                if (!RemoteHosts.IsHost(host))
                {
                    return Fail("invalid host");
                }

                (ControlRemoteTree? offered, string? error) = await RemoteHosts.ListAsync(host);
                return offered is null ? Fail(error ?? "ssh " + host + " failed") : Ok(new ControlResult { Remote = offered });
            }

            // As agterm's localAttachableSessions, in any mode: only a listing that failed is an error.
            if (await Daemons.RecordsAsync() is not List<(string PaneId, List<LivePaneInfo>? Shells)> records)
            {
                return Fail("could not read the live session list");
            }

            HashSet<string> kept = records.SelectMany(record => record.Shells ?? new List<LivePaneInfo>()).Select(pane => pane.Id).ToHashSet();
            List<ControlRemoteSession>[] offers = await Task.WhenAll(open.ToList().Select(window => window.RemoteOfferAsync(Label(window.Entry), window.Entry.Id.Wire())));
            List<ControlRemoteSession> sessions = offers
                .SelectMany(offer => offer)
                // Only a session whose every pane is still kept: one missing a shell would come back with a fresh one in its place.
                .Where(session => session.Panes is { Count: > 0 } panes && panes.All(pane => kept.Contains(pane.Daemon)))
                .ToList();
            return Ok(new ControlResult
            {
                Remote = new ControlRemoteTree
                {
                    Endpoint = new ControlRemoteEndpoint { Pipe = ControlEndpoint.LivePipePrefix(DataDirectory) },
                    Presentation = ControlPresentationKinds.Version,
                    Sessions = sessions,
                },
            });
        }

        /// <summary>agterm's events.read over the app's one stream; the arguments are checked as a window would.</summary>
        private ControlResponse ReadEvents(ControlRequest request)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if ((args.Run is null) != (args.After is null))
            {
                return Fail("events.read requires --run and --after together");
            }

            if (args.Run is not null && !Guid.TryParse(args.Run, out _))
            {
                return Fail("invalid event run id");
            }

            long? after = null;
            if (args.After is not null)
            {
                if (!long.TryParse(args.After, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long cursor))
                {
                    return Fail("invalid event cursor");
                }

                after = cursor;
            }

            int limit = args.Limit ?? 100;
            if (limit < 1 || limit > 1000)
            {
                return Fail("event limit must be between 1 and 1000");
            }

            // agterm's: each entry may list several kinds separated by commas, spaces around them ignored.
            List<string> requested = (args.Kinds ?? new List<string>()).SelectMany(field => field.Split(',')).Select(kind => kind.Trim()).ToList();
            if (requested.FirstOrDefault(kind => !ControlEventKinds.All.Contains(kind)) is string unknown)
            {
                return Fail("invalid event kind: " + unknown);
            }

            HashSet<string>? kinds = requested.Count > 0 ? requested.ToHashSet() : null;
            (ControlEventBatch batch, string? error) = Events.Read(args.Run, after, kinds, limit);
            return error is null
                ? Ok(new ControlResult { Events = batch })
                : new ControlResponse { Ok = false, Error = error, Result = new ControlResult { Events = batch } };
        }

        /// <summary>
        /// agterm's restoreDestination: the window holding the session itself, then the one holding its workspace, live or in a
        /// close inside its undo window, else <paramref name="requested"/>. The session comes first because an old snapshot can
        /// leave one session in two windows, where the stale workspace would rebuild the copy just closed.
        /// </summary>
        private MainWindow? RestoreDestination(RecentClosedItem item, MainWindow? requested)
        {
            if (item.Kind == "session" && item.Session is RecentSession session && open.FirstOrDefault(window => window.OwnsLive(session.Snapshot.Id)) is MainWindow holder)
            {
                return holder;
            }

            if (item.WorkspaceId is Guid workspaceId && open.FirstOrDefault(window => window.HoldsWorkspace(workspaceId)) is MainWindow owner)
            {
                return owner;
            }

            return requested ?? Active;
        }

        /// <summary>
        /// agterm's reopenRecentClosedOutcome: restores the entry where <see cref="RestoreDestination"/> puts it, rebuilding none of
        /// the sessions other windows hold; null when nothing was restored. The entry stays when the restore left a session out.
        /// </summary>
        private (MainWindow Window, bool Complete)? ReopenRecentOutcome(RecentClosedItem item, MainWindow? requested)
        {
            if (RestoreDestination(item, requested) is not MainWindow window)
            {
                return null;
            }

            HashSet<Guid> occupied = open.Where(other => other != window).SelectMany(other => other.HeldSessionIds()).ToHashSet();
            RecentClosedRestore outcome = window.ReopenRecentHere(item, occupied);
            return outcome.Restored ? (window, outcome.Complete) : null;
        }

        /// <summary>Reopens an entry from a window's own palette or keys: where agterm's rule puts it, raised if elsewhere.</summary>
        public void ReopenRecent(Guid? itemId, MainWindow from)
        {
            if (Recent.Find(itemId) is not RecentClosedItem item || ReopenRecentOutcome(item, from) is not (MainWindow window, _))
            {
                return;
            }

            if (window != from)
            {
                window.Raise();
            }
        }

        /// <summary>
        /// The one open window an explicit session or workspace target names; null when none does. As agterm's
        /// resolveTargetAcrossWindows, a prefix is matched against every open window's ids at once, so one that fits ids in
        /// two windows is refused as ambiguous rather than run in the front one.
        /// </summary>
        private (MainWindow? Window, string? Error) WindowOfTarget(ControlRequest request)
        {
            string? target = request.Target ?? request.Args?.Targets?.FirstOrDefault();
            if (target is null || target == ControlTargets.Active)
            {
                return (null, null);
            }

            // A picker or question is found by its id in whichever window opened it, as agterm pins it to its owner.
            if (request.Cmd is "pick.result" or "pick.cancel" or "ask.result" or "ask.cancel")
            {
                return (open.FirstOrDefault(window => window.KnowsModal(target)), null);
            }

            bool workspaces = request.Cmd.StartsWith("workspace.", StringComparison.Ordinal);
            TargetResolution resolution = ControlTargets.Resolve(target, open.SelectMany(window => window.TargetIds(workspaces)), null);
            if (resolution.Match == TargetMatch.Ambiguous)
            {
                return (null, ControlTargets.ErrorMessage(workspaces ? "workspace" : "session", target, resolution));
            }

            if (resolution.Match == TargetMatch.Resolved)
            {
                return (open.First(window => window.TargetIds(workspaces).Contains(resolution.Id)), null);
            }

            List<MainWindow> holders = open.Where(window => window.Resolves(target)).ToList();
            return (holders.Count == 1 ? holders[0] : null, null);
        }

        private (WindowEntry? Entry, string? Error) ResolveEntry(string? selector)
        {
            if (selector is null || selector == ControlTargets.Active)
            {
                return Active?.Entry is WindowEntry active ? (active, null) : (null, "no open window");
            }

            TargetResolution resolution = ControlTargets.Resolve(selector, entries.Select(entry => entry.Id), Active?.Entry.Id);
            return resolution.Match switch
            {
                TargetMatch.Resolved => (entries.First(entry => entry.Id == resolution.Id), null),
                TargetMatch.Ambiguous => (null, ControlTargets.ErrorMessage("window", selector, resolution)),
                _ => (null, "no such window: " + selector),
            };
        }

        /// <summary>A window command from a window's own palette or keys, run as agshellctl would.</summary>
        public ControlResponse RunWindowCommand(string cmd, string? target = null, ControlArgs? args = null)
        {
            return WindowCommand(new ControlRequest { Cmd = cmd, Target = target, Args = args });
        }

        private ControlResponse WindowCommand(ControlRequest request, ControlRequest? replyRequest = null)
        {
            ControlArgs args = request.Args ?? new ControlArgs();
            if (request.Cmd == "window.new")
            {
                return NewWindow(args);
            }

            if (request.Cmd == "window.list")
            {
                return Ok(new ControlResult { Windows = entries.Select(Node).ToList() });
            }

            if (request.Cmd == "window.go")
            {
                return GoWindow(args.To);
            }

            // agterm's dispatcher checks a window command's arguments before it looks for the window.
            switch (request.Cmd)
            {
                case "window.rename" when string.IsNullOrWhiteSpace(args.Name):
                    return Fail("window.rename requires a name");
                case "window.resize" when args.Width is not int || args.Height is not int || args.Width <= 0 || args.Height <= 0:
                    return Fail("window.resize requires positive width and height");
                case "window.move" when args.X is not int || args.Y is not int:
                    return Fail("window.move requires x and y");
                case "window.move" when args.Display is int display && (display < 0 || display >= Displays.All().Count):
                    return Fail("display " + display + " out of range (have " + Displays.All().Count + ")");
                case "window.minimize" when args.Mode is not (null or "on" or "off" or "toggle"):
                    return Fail("invalid window minimize mode: " + args.Mode);
            }

            (WindowEntry? entry, string? error) = ResolveEntry(request.Target);
            if (entry is null)
            {
                return Fail(error!);
            }

            MainWindow? window = open.FirstOrDefault(candidate => candidate.Entry == entry);
            switch (request.Cmd)
            {
                case "window.select":
                    if (window is null)
                    {
                        Show(entry);
                    }
                    else
                    {
                        window.Raise();
                    }

                    return Ok(new ControlResult { Id = entry.Id.Wire() });
                case "window.rename":
                    if (TerminalText.Name(args.Name) is not string renamed)
                    {
                        return Fail("window.rename requires a name");
                    }

                    // agterm's renameWindow: the same name changes nothing; a new one is a tree.changed of that window.
                    if (entry.Name == renamed)
                    {
                        return Ok(new ControlResult { Id = entry.Id.Wire() });
                    }

                    entry.Name = renamed;
                    SaveEntries();
                    if (window is not null)
                    {
                        window.Renamed();
                    }
                    else
                    {
                        Events.Append(new ControlEvent { Ts = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds, Kind = ControlEventKinds.TreeChanged, Payload = new ControlEventPayload() }, entry.Id.Wire());
                    }

                    return Ok(new ControlResult { Id = entry.Id.Wire() });
                case "window.delete":
                    return DeleteWindow(entry, window);
                case "window.close" when window is null:
                    // agterm: a window that is not open is closed already.
                    return Ok(new ControlResult { Id = entry.Id.Wire() });
            }

            if (window is null)
            {
                return Fail("window not open — window.select it first");
            }

            switch (request.Cmd)
            {
                case "window.close":
                    if (replyRequest is null)
                    {
                        window.CloseWithoutAsking();
                    }
                    else
                    {
                        closeResponse.Close(replyRequest, window.CloseWithoutAsking);
                    }
                    return Ok(new ControlResult { Id = entry.Id.Wire() });
                case "window.resize":
                    if (args.Width is not int width || args.Height is not int height || width <= 0 || height <= 0)
                    {
                        return Fail("window.resize requires positive width and height");
                    }

                    (int appliedWidth, int appliedHeight) = window.ResizeTo(width, height);
                    return Ok(new ControlResult { Id = entry.Id.Wire(), Width = appliedWidth, Height = appliedHeight });
                case "window.move":
                    if (args.X is not int x || args.Y is not int y)
                    {
                        return Fail("window.move requires x and y");
                    }

                    return window.MoveTo(x, y, args.Display) is string moveError ? Fail(moveError) : Ok(new ControlResult { Id = entry.Id.Wire() });
                case "window.zoom":
                    window.ToggleZoom();
                    return Ok(new ControlResult { Id = entry.Id.Wire() });
                case "window.fullscreen":
                    window.ToggleFullscreenWindow();
                    return Ok(new ControlResult { Id = entry.Id.Wire() });
                case "window.minimize":
                    bool minimized = window.WindowState == WindowState.Minimized;
                    bool? wanted = args.Mode switch
                    {
                        "on" => true,
                        "off" => false,
                        null or "toggle" => !minimized,
                        _ => null,
                    };
                    if (wanted is not bool minimize)
                    {
                        return Fail("invalid window minimize mode: " + args.Mode);
                    }

                    // agterm: a window already as asked is fine; only a real change is refused to a full-screen one.
                    if (minimize == minimized)
                    {
                        return Ok(new ControlResult { Id = entry.Id.Wire() });
                    }

                    if (window.IsFullscreen && minimize)
                    {
                        return Fail("cannot minimize a full-screen window — window.fullscreen it first");
                    }

                    window.SetMinimized(minimize);
                    if (minimize)
                    {
                        HandOffFrontmost(window);
                    }

                    return Ok(new ControlResult { Id = entry.Id.Wire() });
                default:
                    return Fail(ControlDispatcher.UnknownCommand(request.Cmd));
            }
        }

        /// <summary>agterm's window new: a fresh bundle with one workspace and session, in front unless parked minimized.</summary>
        private ControlResponse NewWindow(ControlArgs args)
        {
            WindowEntry entry = NewEntry(args.Name);
            MainWindow window = Show(entry);
            if (args.Minimized == true)
            {
                // agterm's park: the new window goes to the taskbar and the front passes to one still on screen.
                window.SetMinimized(true);
                HandOffFrontmost(window);
            }

            return Ok(new ControlResult { Id = entry.Id.Wire() });
        }

        public WindowEntry NewEntry(string? name)
        {
            // agterm's defaultWindowName: "window N" by the count at creation, kept when others go.
            WindowEntry entry = new WindowEntry { Name = TerminalText.Name(name) ?? "window " + (entries.Count + 1) };
            entry.StateFile = Path.Combine("windows", entry.Id + ".json");
            Directory.CreateDirectory(Path.Combine(DataDirectory, "windows"));
            entries.Add(entry);
            SaveEntries();
            return entry;
        }

        /// <summary>agterm's window go: the next or previous open window in list order, wrapping.</summary>
        private ControlResponse GoWindow(string? to)
        {
            // agterm's WorkspaceNavigation takes previous too.
            if (to is not ("next" or "prev" or "previous"))
            {
                return Fail("window.go requires --to next|prev");
            }

            List<MainWindow> ordered = entries.Select(entry => open.FirstOrDefault(window => window.Entry == entry)).OfType<MainWindow>().ToList();
            if (ordered.Count < 2 || Active is not MainWindow current)
            {
                return Fail("no other open window to navigate to");
            }

            int index = ordered.IndexOf(current);
            MainWindow next = ordered[((index + (to == "next" ? 1 : -1)) % ordered.Count + ordered.Count) % ordered.Count];
            next.Raise();
            return Ok(new ControlResult { Id = next.Entry.Id.Wire() });
        }

        /// <summary>Deletes a window and its state; the last one stays, as agterm keeps at least one.</summary>
        private ControlResponse DeleteWindow(WindowEntry entry, MainWindow? window)
        {
            if (entries.Count == 1)
            {
                return Fail("cannot delete last window");
            }

            entries.Remove(entry);
            SaveEntries();
            // As agterm's finalizeWindowPanes: the window's shells end with it, the live daemons' too; a closed window's are
            // read from its file.
            if (window is not null)
            {
                window.EndForDelete();
            }
            else if (Live is LiveDaemons live)
            {
                try
                {
                    foreach (SessionSnapshot session in new StateStore(Path.Combine(DataDirectory, entry.StateFile)).Load()?.Workspaces.SelectMany(workspace => workspace.Sessions) ?? Enumerable.Empty<SessionSnapshot>())
                    {
                        foreach (string paneId in (session.PaneIds ?? new Dictionary<string, string>()).Where(pair => pair.Key is "left" || (pair.Key is "right" && session.HasSplit)).Select(pair => pair.Value))
                        {
                            live.Stop(paneId);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Notice("WindowLibrary", "window delete could not inventory panes for " + entry.Id.Wire() + ": " + ex.Message);
                }
            }

            // Closing saves the window's state, so the files go after it.
            window?.CloseWithoutAsking();

            File.Delete(Path.Combine(DataDirectory, entry.StateFile));

            return Ok(new ControlResult { Id = entry.Id.Wire() });
        }

        /// <summary>agterm's PickRegistry.retainedResultLimit: how many pickers' answers outlive their window, across the app.</summary>
        private const int ClosedPickResults = 32;

        private readonly List<(string Id, ControlPickResult Result, long Sequence, Guid Window)> closedPicks = new List<(string, ControlPickResult, long, Guid)>();
        private readonly ConditionalWeakTable<AppModel, WindowEntry> closedModalWindows = new ConditionalWeakTable<AppModel, WindowEntry>();

        /// <summary>
        /// agterm's unregister: keeps a closing window's answered pickers, the app's newest 32 by when they were answered,
        /// as a window closing later brings older answers with it. Its questions' answers are already in <see cref="AskResults"/>.
        /// </summary>
        internal void KeepModalResults(WindowEntry window, AppModel model)
        {
            closedModalWindows.AddOrUpdate(model, window);
            closedPicks.AddRange(model.KeptPickResults.Select(pick => (pick.Id, pick.Result, pick.Sequence, window.Id)));
            if (closedPicks.Count > ClosedPickResults)
            {
                closedPicks.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
                closedPicks.RemoveRange(0, closedPicks.Count - ClosedPickResults);
            }
        }

        /// <summary>A question's result whose window is gone.</summary>
        private ControlAskResult? ClosedAsk(string id)
        {
            return AskResults.Find(id) is (ControlAskResult result, object owner) && !open.Any(window => window.Model == owner) ? result : null;
        }

        private ControlResponse? ClosedModalResponse(ControlRequest request, WindowEntry? window = null)
        {
            if (request.Target is not string id)
            {
                return null;
            }

            if (window is not null)
            {
                if (request.Cmd is "pick.result" or "pick.cancel" && closedPicks.LastOrDefault(pick => pick.Id == id).Result is not null
                    && closedPicks.Last(pick => pick.Id == id).Window != window.Id)
                {
                    return Fail("unknown pick: " + id);
                }

                if (request.Cmd is "ask.result" or "ask.cancel" && AskResults.Find(id) is (_, AppModel owner)
                    && (!closedModalWindows.TryGetValue(owner, out WindowEntry? entry) || entry.Id != window.Id))
                {
                    return Fail("unknown ask: " + id);
                }
            }

            return request.Cmd switch
            {
                "pick.result" when closedPicks.LastOrDefault(entry => entry.Id == id).Result is ControlPickResult pick => Ok(new ControlResult { Pick = pick }),
                "pick.cancel" when closedPicks.Any(entry => entry.Id == id) => Ok(new ControlResult()),
                "ask.result" when ClosedAsk(id) is ControlAskResult ask => Ok(new ControlResult { Ask = ask }),
                "ask.cancel" when ClosedAsk(id) is not null => Ok(new ControlResult()),
                _ => null,
            };
        }

        private ControlWindowNode Node(WindowEntry entry)
        {
            MainWindow? window = open.FirstOrDefault(candidate => candidate.Entry == entry);
            return window?.Node(Label(entry), window == Active) ?? new ControlWindowNode { Id = entry.Id.Wire(), Name = Label(entry), Open = false };
        }

        private static ControlResponse Ok(ControlResult result)
        {
            return new ControlResponse { Ok = true, Result = result };
        }

        private static ControlResponse Fail(string error)
        {
            return new ControlResponse { Ok = false, Error = error };
        }
    }
}
