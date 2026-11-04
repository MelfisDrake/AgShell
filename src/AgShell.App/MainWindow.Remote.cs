using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's remote attach: a row for a session another machine's live daemons run. Its panes are joined through ssh; a
    /// drop is retried after 1, 2, 4, 8, 16 then 30 seconds, every 5 minutes after eight failures in a row, without end.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// agterm's reconnect book, by pane: disconnected (its ssh dropped, waiting for the machine) or held (ssh ended with a
        /// status of its own, waiting for a key). Keyed by the row and the pane's daemon, which follows the shell through a
        /// swap as agterm's pane identity does; a pane not here is connected.
        /// </summary>
        private readonly Dictionary<(Guid Row, string Daemon), string> remoteStates = new Dictionary<(Guid, string), string>();
        private readonly Dictionary<(Guid Row, string Daemon), int> remoteFailures = new Dictionary<(Guid, string), int>();
        private readonly Dictionary<(Guid Row, string Daemon), DispatcherTimer> reconnectTimers = new Dictionary<(Guid, string), DispatcherTimer>();
        private readonly Dictionary<(Guid Row, string Daemon), string?> remoteReasons = new Dictionary<(Guid, string), string?>();
        private readonly Dictionary<(Guid Row, string Daemon), (DateTime At, int Failures)> remoteResumed = new Dictionary<(Guid, string), (DateTime, int)>();
        private readonly HashSet<(Guid Row, string Daemon)> remoteProbing = new HashSet<(Guid, string)>();

        /// <summary>A pane that drops again this soon after coming back keeps its backoff.</summary>
        private static readonly TimeSpan ReconnectSettle = TimeSpan.FromSeconds(60);

        /// <summary>Each remote pane's own ssh to its daemon there, by the row and the daemon, so it follows the shell through a swap.</summary>
        private readonly Dictionary<(Guid Row, string Daemon), LiveClient> remoteClients = new Dictionary<(Guid, string), LiveClient>();

        /// <summary>Remote panes joined before: one coming back after a drop does not take the lead, as agterm's.</summary>
        private readonly HashSet<PaneKey> remoteJoined = new HashSet<PaneKey>();

        /// <summary>Panes whose ssh ended with a status of its own (not 255): they wait for a key, as agterm's held pane.</summary>
        private readonly Dictionary<(Guid Row, string Daemon), int> remoteHeldCodes = new Dictionary<(Guid, string), int>();

        /// <summary>Panes of a row closed with undo in reach that ended there meanwhile: undo takes them down, as a live row would.</summary>
        private readonly HashSet<PaneKey> endedWhileHidden = new HashSet<PaneKey>();

        /// <summary>The pane of each session whose search bar the page shows, for session search next and prev.</summary>
        private readonly Dictionary<Guid, PaneRole> searchPanes = new Dictionary<Guid, PaneRole>();

        /// <summary>Main panes that ended there while a split of this machine had not started: they wait for a key.</summary>
        private readonly HashSet<PaneKey> endedThere = new HashSet<PaneKey>();

        /// <summary>agterm's paneMetrics for each HUD, by overlay id, as its page measured them.</summary>
        private readonly Dictionary<string, PaneMetrics> hudMetrics = new Dictionary<string, PaneMetrics>();

        /// <summary>A floating overlay's content size as its page laid it out, for tree's overlayWidth and overlayHeight.</summary>
        private readonly Dictionary<string, (int Width, int Height)> overlayGeometry = new Dictionary<string, (int, int)>();

        /// <summary>Pages' titles as their views report them, by overlay id.</summary>

        /// <summary>The line a pane's next shell runs, from session restart: its --command or the program it ran before.</summary>
        private readonly Dictionary<PaneKey, string> restartLines = new Dictionary<PaneKey, string>();
        private readonly Dictionary<PaneKey, string> restartDirectories = new Dictionary<PaneKey, string>();

        /// <summary>Reattaching panes whose fresh attach claims the lead, taken from a key or session lead, as agterm's reattach with the claim.</summary>
        private readonly HashSet<PaneKey> claiming = new HashSet<PaneKey>();

        /// <summary>Covered panes joining again with a fresh attach, covered until their first lead report, as agterm's reattaching.</summary>
        private readonly HashSet<PaneKey> reattaching = new HashSet<PaneKey>();
        private readonly Dictionary<Guid, RemotePresentationStream> presentations = new Dictionary<Guid, RemotePresentationStream>();

        /// <summary>Overlays handed over by another machine, by the id of the terminal showing each here.</summary>
        private readonly Dictionary<string, (Guid SessionId, string Job)> replicaOverlays = new Dictionary<string, (Guid, string)>();

        /// <summary>The window closed: a probe still out when it did has nothing left to reconnect.</summary>
        private bool windowClosed;

        private DispatcherTimer? expiry;

        /// <summary>Handed-over overlays still running when their stream went: they close as their ssh ends.</summary>
        private readonly HashSet<string> orphanedOverlays = new HashSet<string>();

        /// <summary>The presenter generation each handed-over overlay job came under, which its closing echoes.</summary>
        private readonly Dictionary<string, int> replicaOwners = new Dictionary<string, int>();

        /// <summary>agterm's replica pages: pages the session's machine handed over, by their id here, with theirs there.</summary>
        /// <summary>The channels of pages this machine handed over, by page id, while their pages are open.</summary>
        private readonly Dictionary<string, PageServeStream> pageChannels = new Dictionary<string, PageServeStream>();

        private readonly Dictionary<string, (Guid SessionId, string Origin, int Owner)> replicaPages = new Dictionary<string, (Guid, string, int)>();

        /// <summary>What each replica page last reported there, so only a change goes out.</summary>
        private readonly Dictionary<string, ControlPresentationPageState> replicaPageStates = new Dictionary<string, ControlPresentationPageState>();

        /// <summary>Questions handed over by another machine, by the id they are drawn under here.</summary>
        private readonly Dictionary<string, (Guid SessionId, string RemoteId, int Owner)> replicaAsks = new Dictionary<string, (Guid, string, int)>();

        /// <summary>Whether remotes.conf lists a machine, for attach_remote: agterm's watcher entries, not a read of its own.</summary>
        private bool RemotesAvailable()
        {
            return Library.RemoteMachines.Count > 0;
        }

        /// <summary>agterm's picker never offers more than this many sessions; a machine with more is reported instead.</summary>
        private const int MaxRemoteSessions = 1000;

        /// <summary>agterm's name for the action in its messages: "Attach Remote".</summary>
        private static string AttachRemoteName => Strings.T("Подключиться к сессии на другой машине");

        /// <summary>
        /// agterm's RemoteAttachHud: the panel an attach posts over the session it started from, its text led by the action's
        /// name. A window with no session, or one a program covers, has no place for it: a failure then becomes a desktop
        /// notification. Only the panel this attach opened is its to close.
        /// </summary>
        private sealed class RemoteAttachHud
        {
            private readonly MainWindow window;
            private readonly Guid? session;
            private string? owned;

            public RemoteAttachHud(MainWindow window)
            {
                this.window = window;
                session = window.model.SelectedSessionId;
            }

            /// <summary>As agterm's: a bar spinner, gone by itself after 30 seconds should the machine never answer.</summary>
            public void Progress(string text)
            {
                Open(new HudSpec { Message = HudSpec.FailureMessage(AttachRemoteName, text), Spinner = "bar", HideAfter = 30 });
            }

            public void Close()
            {
                if (session is Guid id && owned is string mine)
                {
                    window.model.CloseOverlayById(id, mine);
                    owned = null;
                }
            }

            /// <summary>The failure for five seconds over the same session, opened afresh; with no place, agterm's notifyCommandFailure.</summary>
            public void Fail(string text)
            {
                if (Open(new HudSpec { Message = HudSpec.FailureMessage(AttachRemoteName, text), HideAfter = 5 }))
                {
                    return;
                }

                Close();
                window.NotifyApp("command-failure:attach-remote", Strings.T("Команда не сработала"), AttachRemoteName + " (" + text + ")");
            }

            private bool Open(HudSpec hud)
            {
                // Over a HUD too, as agterm's; a program covering the session leaves no place.
                if (session is not Guid id || window.model.FindSession(id) is not Session shown || shown.Overlay is { IsHud: false })
                {
                    return false;
                }

                SessionOverlay panel = window.model.OpenOverlay(id, new SessionOverlay { Id = Ids.New(), Hud = hud, ShownAt = DateTimeOffset.UtcNow });
                window.ScheduleHudHide(id, panel.Id, hud.HideAfter);
                owned = panel.Id;
                return true;
            }
        }

        /// <summary>agterm's controller.pick: the window's picker, awaited; null when the slot is already taken.</summary>
        private Task<ControlPickResult?> PickAsync(PickRequest pick)
        {
            TaskCompletionSource<ControlPickResult?> done = new TaskCompletionSource<ControlPickResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnResolved(string id, ControlPickResult result)
            {
                if (id == pick.Id)
                {
                    model.PickResolved -= OnResolved;
                    done.TrySetResult(result);
                }
            }

            model.PickResolved += OnResolved;
            if (model.OpenPick(pick) is not null)
            {
                model.PickResolved -= OnResolved;
                done.TrySetResult(null);
            }

            return done.Task;
        }

        /// <summary>
        /// agterm's attachRemote: which machine to attach from is asked in the window's picker, a single one needs no question;
        /// with none in remotes.conf there is nothing to do.
        /// </summary>
        private async Task AttachRemoteAsync()
        {
            IReadOnlyList<RemoteMachine> machines = Library.RemoteMachines;
            if (!UiActionsEnabled || machines.Count == 0)
            {
                return;
            }

            if (machines.Count == 1)
            {
                await AttachRemoteFromAsync(machines[0].Destination);
                return;
            }

            ControlPickResult? picked = await PickAsync(new PickRequest
            {
                Id = Ids.New(),
                Prompt = Strings.T("С какой машины подключить?"),
                Items = machines.Select(machine => new ControlPickItem { Id = machine.Destination, Label = machine.Label, Subtitle = machine.Label == machine.Destination ? null : machine.Destination }).ToList(),
            });
            if (picked is { Result: "picked", Id: string destination } && !windowClosed)
            {
                await AttachRemoteFromAsync(destination);
            }
        }

        /// <summary>
        /// agterm's attachRemote from one machine: its sessions listed under the panel, picked in the window's picker once
        /// they came, and the one picked attached under the panel again.
        /// </summary>
        private async Task AttachRemoteFromAsync(string destination)
        {
            if (!UiActionsEnabled)
            {
                return;
            }

            RemoteAttachHud hud = new RemoteAttachHud(this);
            hud.Progress(Strings.T("получаю сессии с {0}…", destination));
            (ControlRemoteTree? tree, string? error) = await RemoteHosts.ListAsync(destination);
            if (windowClosed)
            {
                return;
            }

            if (tree is null)
            {
                hud.Fail(destination + ": " + (error ?? Strings.T("нет ответа")));
                return;
            }

            if (tree.Sessions.Count == 0 || tree.Sessions.Count > MaxRemoteSessions)
            {
                hud.Fail(tree.Sessions.Count == 0 ? Strings.T("на {0} нечего подключать", destination) : Strings.T("{0} предлагает слишком много сессий", destination));
                return;
            }

            hud.Close();
            ControlPickResult? picked = await PickAsync(new PickRequest
            {
                Id = Ids.New(),
                Prompt = Strings.T("Подключить с {0}", destination),
                Items = tree.Sessions.Select(RemotePickItem).ToList(),
            });
            if (picked is not { Result: "picked", Id: string chosen } || !Guid.TryParse(chosen, out Guid remoteId) || windowClosed)
            {
                return;
            }

            hud.Progress(Strings.T("подключаю {0}…", picked.Label ?? chosen));
            (ControlRemoteTree? fresh, ControlRemoteSession? found, string? failure) = await FindRemoteAsync(destination, remoteId);
            if (windowClosed)
            {
                return;
            }

            failure ??= RemotePanesFailure(destination, found!);
            if (failure is null && model.CurrentWorkspace is null)
            {
                failure = Strings.T("нет workspace, куда подключить сессию");
            }

            if (failure is not null)
            {
                hud.Fail(failure);
                return;
            }

            hud.Close();
            AttachRemote(destination, remoteId, found!, fresh!.Presentation);
        }

        /// <summary>agterm's remotePickItem: the name, then where it is, what it is for, where it works and what runs in it.</summary>
        private static ControlPickItem RemotePickItem(ControlRemoteSession session)
        {
            IEnumerable<string> programs = (session.Panes ?? new List<ControlRemotePane>())
                .Select(pane => pane.Foreground?.FirstOrDefault() is string program ? program[(Math.Max(program.LastIndexOf('/'), program.LastIndexOf('\\')) + 1)..] : null)
                .OfType<string>();
            string[] parts = { session.Window + "/" + session.Workspace, session.Context ?? string.Empty, session.Cwd ?? string.Empty, string.Join(" | ", programs) };
            return new ControlPickItem
            {
                Id = session.Id,
                Label = session.Name,
                Subtitle = string.Join("  ·  ", parts.Where(part => part.Length > 0)),
                Status = session.Status,
                StatusColor = session.StatusColor,
                StatusShape = session.StatusShape,
            };
        }

        /// <summary>
        /// agterm's attach: the session is looked up on the machine again, so one gone since the listing fails instead of
        /// coming back as a fresh shell under its name.
        /// </summary>
        internal static async Task<(ControlRemoteTree? Tree, ControlRemoteSession? Found, string? Error)> FindRemoteAsync(string destination, Guid remoteId)
        {
            if (!RemoteHosts.IsHost(destination))
            {
                return (null, null, "invalid host");
            }

            (ControlRemoteTree? tree, string? error) = await RemoteHosts.ListAsync(destination);
            if (tree is null)
            {
                return (null, null, error ?? "ssh " + destination + " failed");
            }

            return tree.Sessions.FirstOrDefault(session => Guid.TryParse(session.Id, out Guid offered) && offered == remoteId) is ControlRemoteSession found
                ? (tree, found, null)
                : (tree, null, "no attachable session " + remoteId + " on " + destination);
        }

        /// <summary>
        /// agterm's check before any row is made: each pane named once by a role this app knows, a left one among them, and
        /// every daemon a name a pane here can join; the split is there when a right pane is.
        /// </summary>
        internal static string? RemotePanesFailure(string destination, ControlRemoteSession found)
        {
            List<ControlRemotePane> panes = found.Panes ?? new List<ControlRemotePane>();
            List<PaneRole?> roles = panes.Select(pane => pane.Pane == Panes.Name(PaneRole.Main) ? PaneRole.Main : pane.Pane == Panes.Name(PaneRole.Split) ? PaneRole.Split : (PaneRole?)null).ToList();
            if (roles.Distinct().Count() != roles.Count || !roles.Contains(PaneRole.Main))
            {
                return destination + " reported panes agshell cannot address";
            }

            return panes.All(pane => Guid.TryParse(pane.Daemon, out _)) ? null : destination + " reported a session agshell cannot address";
        }

        /// <summary>live attach, once the library found the session and chose this window.</summary>
        internal ControlResponse AttachRemoteFound(string destination, Guid remoteId, ControlRemoteSession found, int? presentation)
        {
            if (model.CurrentWorkspace is null)
            {
                return ControlResponse.Failure("no window to attach into");
            }

            return ControlResponse.Success(new ControlResult { Id = AttachRemote(destination, remoteId, found, presentation).ToString() });
        }

        /// <summary>Adds the row in the current workspace and selects it; its panes join the other machine's shells as they open.</summary>
        private Guid AttachRemote(string destination, Guid remoteSessionId, ControlRemoteSession found, int? presentation)
        {
            Workspace workspace = model.CurrentWorkspace!;
            string? Daemon(string pane) => found.Panes?.FirstOrDefault(each => each.Pane == pane)?.Daemon;
            bool split = found.Panes?.Any(pane => pane.Pane == Panes.Name(PaneRole.Split)) == true;
            Session session = model.AddRemoteSession(workspace.Id, destination, remoteSessionId, found.Name, split, found.Cwd, presentation, found.SplitAxis, Daemon(Panes.Name(PaneRole.Main)), Daemon(Panes.Name(PaneRole.Split)));
            Guid id = session.Id;
            // agterm: a machine too old to stream still attaches, with nothing mirrored.
            if (presentation is null)
            {
                return id;
            }

            StartPresentationStream(session);
            return id;
        }

        private void StartPresentationStream(Session session)
        {
            Guid id = session.Id;
            RemotePresentationStream stream = new RemotePresentationStream(session.RemoteHost!, session.RemoteSessionId, Dispatcher, frame => OnPresentationFrame(id, frame), reason => Log.Warning("RemotePresentation", "presentation stream for " + id.Wire() + ": " + reason), () =>
            {
                model.ClearRemotePresentation(id);
                DropReplicaAsks(id, null);
                CloseReplicaOverlays(id, null);
                CloseReplicaPages(id, null);
            });
            presentations[id] = stream;
            stream.StateChanged += PostState;
            stream.Start();
        }

        /// <summary>
        /// agterm: a remote row closed with undo still in reach stops its stream, handing back what was handed to it and
        /// giving up the presenter role; undo starts the stream again.
        /// </summary>
        private void OnRemoteRowVisibility(Session session, bool visible)
        {
            if (visible)
            {
                if (session.RemotePresentation is not null && !presentations.ContainsKey(session.Id))
                {
                    StartPresentationStream(session);
                }

                // Panes that ended there while the row was closed go now, after the undo has finished.
                if (endedWhileHidden.Any(key => key.SessionId == session.Id))
                {
                    Dispatcher.BeginInvoke(() => RemoveEndedWhileHidden(session.Id));
                }

                // agterm: a pane that waited while its row was closed goes on with its schedule; the next due probe joins it.
                foreach ((Guid Row, string Daemon) wait in remoteStates.Where(pair => pair.Key.Row == session.Id && pair.Value == "disconnected").Select(pair => pair.Key).ToList())
                {
                    if (!reconnectTimers.ContainsKey(wait) && !remoteProbing.Contains(wait))
                    {
                        ScheduleProbe(wait, RemoteHosts.RetryDelay(Math.Max(1, remoteFailures.GetValueOrDefault(wait))));
                    }
                }

                return;
            }

            StopRemoteRow(session.Id);
        }

        private void RemoveEndedWhileHidden(Guid sessionId)
        {
            foreach (PaneKey key in endedWhileHidden.Where(key => key.SessionId == sessionId).OrderByDescending(key => key.Pane == PaneRole.Split).ToList())
            {
                endedWhileHidden.Remove(key);
                if (model.FindSession(sessionId) is Session session && session.RemoteHost is string remote && AppModel.IsRemotePane(session, key.Pane))
                {
                    OnRemotePaneRemoved(session, remote, key);
                }
            }
        }

        /// <summary>
        /// The row leaves this window, for now or for good: what was handed to it goes back, every handed-over overlay closes,
        /// running ones included, and its stream ends, giving the presenter role back.
        /// </summary>
        private void StopRemoteRow(Guid sessionId)
        {
            if (!presentations.Remove(sessionId, out RemotePresentationStream? stream))
            {
                return;
            }

            foreach ((string replicaId, (Guid SessionId, string RemoteId, int Owner) replica) in replicaAsks.Where(pair => pair.Value.SessionId == sessionId).ToList())
            {
                stream.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.AskRejected, Id = replica.RemoteId, Owner = replica.Owner });
            }

            DropReplicaAsks(sessionId, null);
            presentations[sessionId] = stream;
            foreach ((string overlayId, (Guid SessionId, string Job) replica) in replicaOverlays.Where(pair => pair.Value.SessionId == sessionId).ToList())
            {
                if (model.FindSessionOrPending(sessionId)?.Overlays.FirstOrDefault(pair => pair.Value.Id == overlayId) is { Value: not null } slot)
                {
                    model.CloseOverlay(sessionId, overlayId, slot.Key);
                }
                else if (replicaOwners.Remove(replica.Job, out int owner))
                {
                    replicaOverlays.Remove(overlayId);
                    stream.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayClosed, Id = replica.Job, Owner = owner });
                }
            }

            presentations.Remove(sessionId);
            model.ClearRemotePresentation(sessionId);
            stream.Dispose();
        }

        public ControlPresentationNode? PresentationOf(Guid sessionId)
        {
            if (presentations.GetValueOrDefault(sessionId) is RemotePresentationStream stream)
            {
                return new ControlPresentationNode { State = stream.State, Mode = stream.Presenter ? "presenter" : "mirror", Error = stream.Error };
            }

            return model.FindSession(sessionId) is { RemoteHost: not null, RemotePresentation: null } ? new ControlPresentationNode { State = "unsupported", Mode = "mirror" } : null;
        }

        public ControlReconnectNode? ReconnectOf(Guid sessionId, PaneRole pane)
        {
            return model.FindSession(sessionId) is Session session && WaitKey(session, pane) is (Guid, string) wait && remoteStates.GetValueOrDefault(wait) == "disconnected"
                ? new ControlReconnectNode { Failures = remoteFailures.GetValueOrDefault(wait), Reason = remoteReasons.GetValueOrDefault(wait) }
                : null;
        }

        /// <summary>The key of a pane of the other machine in the reconnect book: its row and daemon; null for a pane of this machine.</summary>
        private static (Guid Row, string Daemon)? WaitKey(Session session, PaneRole pane)
        {
            return AppModel.IsRemotePane(session, pane) && AppModel.RemoteDaemonOf(session, pane) is string daemon ? (session.Id, daemon) : null;
        }

        /// <summary>A pane's state in the reconnect book: disconnected, held, or null while it is connected.</summary>
        private string? RemoteStateOf(Session session, PaneRole pane)
        {
            return WaitKey(session, pane) is (Guid, string) wait ? remoteStates.GetValueOrDefault(wait) : null;
        }

        /// <summary>The pane here still holding the waiting one, its row closed with undo in reach included; null once it is gone.</summary>
        private PaneKey? WaitingPane((Guid Row, string Daemon) wait)
        {
            return model.FindSessionOrPending(wait.Row) is Session session
                && new[] { PaneRole.Main, PaneRole.Split }.Where(pane => WaitKey(session, pane) == wait).Select(pane => (PaneRole?)pane).FirstOrDefault() is PaneRole role
                ? new PaneKey(wait.Row, role)
                : null;
        }

        /// <summary>agterm's cancel: the pane leaves the reconnect book, its schedule and its held status.</summary>
        private void ForgetWait((Guid Row, string Daemon) wait)
        {
            if (reconnectTimers.Remove(wait, out DispatcherTimer? pending))
            {
                pending.Stop();
            }

            remoteStates.Remove(wait);
            remoteHeldCodes.Remove(wait);
            remoteFailures.Remove(wait);
            remoteReasons.Remove(wait);
            remoteResumed.Remove(wait);
        }


        private void StopPresentation(Guid sessionId)
        {
            foreach ((Guid Row, string Daemon) wait in reconnectTimers.Keys.Where(wait => wait.Row == sessionId).ToList())
            {
                reconnectTimers.Remove(wait, out DispatcherTimer? waiting);
                waiting!.Stop();
            }

            remoteJoined.RemoveWhere(key => key.SessionId == sessionId);
            StopRemoteRow(sessionId);
            DropReplicaAsks(sessionId, null);
        }

        /// <summary>The window closes while others stay: its remote rows' streams and waits end with it, as agterm's.</summary>
        private void StopRemoteRows()
        {
            windowClosed = true;
            expiry?.Stop();
            foreach (Session session in model.AllSessions.Where(session => session.RemoteHost is not null).ToList())
            {
                StopRemoteRow(session.Id);
            }

            foreach (DispatcherTimer timer in reconnectTimers.Values)
            {
                timer.Stop();
            }

            reconnectTimers.Clear();
            model.EndAllOverlayJobs();
        }

        /// <summary>
        /// Both ends of agterm's ask handover: questions this window handed to a presenter come back when it goes quiet,
        /// and an answer to one handed here goes back to the session's machine.
        /// </summary>
        private void StartRemoteAsks()
        {
            WatchWakeAndNetwork();
            model.RemoteRowVisibility += OnRemoteRowVisibility;
            expiry = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (sender, args) =>
            {
                model.ExpirePresenters(DateTimeOffset.UtcNow);
                model.ExpireOverlayJobs(DateTimeOffset.UtcNow);
            }, Dispatcher);
            expiry.Start();
            // A handed-over overlay's surface closed here: the session's machine frees the slot it held for it.
            // An overlay left its slot: its terminal stops (a split closing takes its pane's overlay with it), and a
            // handed-over one tells the session's machine, which frees the slot it held for it.
            model.RemotePageEnded += page =>
            {
                if (pageChannels.Remove(page.Id, out PageServeStream? channel))
                {
                    channel.Shutdown();
                }
            };
            model.OverlayClosed += (sessionId, overlayId, slot) =>
            {
                if (ghosttyEditorReload?.Close(sessionId, overlayId) == true)
                {
                    Library.ReloadGhostty();
                }

                host.Stop(new PaneKey(sessionId, slot));
                orphanedOverlays.Remove(overlayId);
                if (replicaOverlays.Remove(overlayId, out (Guid SessionId, string Job) replica) && replicaOwners.Remove(replica.Job, out int owner))
                {
                    presentations.GetValueOrDefault(replica.SessionId)?.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayClosed, Id = replica.Job, Owner = owner });
                }

                // A page handed over that closed here, by its strip, its window.close or its pane: the session's machine frees its slot.
                if (replicaPages.Remove(overlayId, out (Guid SessionId, string Origin, int Owner) page))
                {
                    replicaPageStates.Remove(overlayId);
                    presentations.GetValueOrDefault(page.SessionId)?.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageClosed, Id = page.Origin, Owner = page.Owner });
                }
            };
            model.AskResolved += (id, result) =>
            {
                if (!replicaAsks.Remove(id, out (Guid SessionId, string RemoteId, int Owner) replica) || !presentations.TryGetValue(replica.SessionId, out RemotePresentationStream? stream))
                {
                    return;
                }

                stream.Send(result.Result switch
                {
                    "answered" => new ControlPresentationFrame { Kind = ControlPresentationKinds.AskResolve, Id = replica.RemoteId, Owner = replica.Owner, Button = result.Id, Text = result.Text, Selected = result.Selected },
                    "escaped" => new ControlPresentationFrame { Kind = ControlPresentationKinds.AskResolve, Id = replica.RemoteId, Owner = replica.Owner },
                    _ => new ControlPresentationFrame { Kind = ControlPresentationKinds.AskRejected, Id = replica.RemoteId, Owner = replica.Owner },
                });
            };
        }

        /// <summary>
        /// Shows an overlay the other machine handed over: a terminal here running ssh to agshellctl session overlay run-job, which runs the
        /// program there once. A slot taken here, or a pane not shown, is a refusal, and the overlay fails there.
        /// </summary>
        private void OpenReplicaOverlay(Guid sessionId, ControlPresentationOverlay overlay)
        {
            Session session = model.FindSession(sessionId)!;
            PaneRole? slot = AppModel.LocalOverlaySlot(session, overlay.Pane);
            // The job id goes onto the ssh command line: only a well-formed id is taken.
            // A HUD gives way, as agterm's open closes one; a program or page in the slot refuses.
            if (slot is not PaneRole free || !Guid.TryParse(overlay.Job, out _) || (session.Overlays.TryGetValue(free, out SessionOverlay? taken) && !taken.IsHud)
                || replicaOverlays.ContainsValue((sessionId, overlay.Job)))
            {
                presentations.GetValueOrDefault(sessionId)?.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayRejected, Id = overlay.Job, Owner = overlay.Owner });
                return;
            }

            if (session.Overlays.TryGetValue(free, out SessionOverlay? hud))
            {
                model.CloseOverlay(sessionId, hud.Id, free);
            }

            SessionOverlay shown = model.OpenOverlay(sessionId, new SessionOverlay
            {
                Id = Ids.New(),
                CommandLine = RemoteHosts.JobCommandLine(session.RemoteHost!, overlay.Job),
                Wait = overlay.Wait,
                SizePercent = overlay.SizePercent,
                BackgroundColor = overlay.BackgroundColor,
            }, free);
            replicaOverlays[shown.Id] = (sessionId, overlay.Job);
            replicaOwners[overlay.Job] = overlay.Owner;
            if (overlay.Follow)
            {
                model.SelectSession(sessionId);
            }
        }

        /// <summary>
        /// agterm's replica page: a page the session's machine handed over. A web page loads here with this machine's network
        /// and storage; a file page reads its files from that machine, through session overlay page-serve with the page's token. A slot
        /// taken here, a pane not shown or an address that is neither is a refusal, and the page closes there.
        /// </summary>
        private void OpenReplicaPage(Guid sessionId, ControlPresentationPage page)
        {
            Session session = model.FindSession(sessionId)!;
            PaneRole? slot = AppModel.LocalOverlaySlot(session, page.Pane);
            bool web = !page.IsFile && Uri.TryCreate(page.Url, UriKind.Absolute, out Uri? address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
            bool file = page.IsFile && page.Token is not null && Path.IsPathFullyQualified(page.Url) && session.RemoteHost is not null && pageHost is not null;
            // agterm's showReplicaPage: the refusal names its reason, which the session's machine logs.
            string? reason = page.IsFile && !file ? "the origin cannot be reached for this page"
                : !page.IsFile && !web ? "invalid url"
                : !page.IsFile && page.Persistent && Library.ClearingBrowserData ? "browser storage is being cleared"
                : slot is null ? "pane not visible"
                : (session.Overlays.TryGetValue(slot.Value, out SessionOverlay? taken) && !taken.IsHud) || replicaPages.ContainsValue((sessionId, page.Id, page.Owner))
                    ? slot == PaneRole.Overlay ? "overlay already open" : "pane overlay already open"
                : null;
            if (reason is not null || slot is not PaneRole free)
            {
                presentations.GetValueOrDefault(sessionId)?.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageRejected, Id = page.Id, Owner = page.Owner, Reason = reason });
                return;
            }

            if (session.Overlays.TryGetValue(free, out SessionOverlay? hud))
            {
                model.CloseOverlay(sessionId, hud.Id, free);
            }

            string id = Ids.New();
            if (file)
            {
                // agterm: the channel's end closes the page, which tells the session's machine.
                pageHost!.Serve(id, new RemotePageChannel(session.RemoteHost!, page.Token!, Dispatcher, () =>
                {
                    KeyValuePair<PaneRole, SessionOverlay> place = model.FindSession(sessionId)?.Overlays.FirstOrDefault(pair => pair.Value.Id == id) ?? default;
                    if (place.Value is not null)
                    {
                        model.CloseOverlay(sessionId, id, place.Key);
                    }
                }));
            }

            SessionOverlay shown = model.OpenOverlay(sessionId, new SessionOverlay
            {
                Id = id,
                Page = new PageSpec
                {
                    Source = page.Url,
                    IsFile = file,
                    Root = file ? page.Root : null,
                    Js = page.Js,
                    Navigation = page.Navigation,
                    Chromeless = page.Chromeless,
                    Persistent = page.Persistent,
                    Browse = page.Browse,
                },
                SizePercent = page.SizePercent,
                BackgroundColor = page.BackgroundColor,
                Hidden = page.Hidden,
            }, free);
            replicaPages[shown.Id] = (sessionId, page.Id, page.Owner);
            if (page.Follow)
            {
                model.SelectSession(sessionId);
            }
        }

        /// <summary>The page handed over, here: its id and slot, while it is open.</summary>
        private (string Id, PaneRole Slot)? ReplicaPageSlot(Guid sessionId, string origin)
        {
            string? id = replicaPages.FirstOrDefault(pair => pair.Value.SessionId == sessionId && pair.Value.Origin == origin).Key;
            KeyValuePair<PaneRole, SessionOverlay> slot = id is null ? default : model.FindSession(sessionId)?.Overlays.FirstOrDefault(pair => pair.Value.Id == id) ?? default;
            return slot.Value is null ? null : (id!, slot.Key);
        }

        /// <summary>The session's machine changed the page's size or the visibility it asks for, or asked it to step or reload.</summary>
        private void ApplyReplicaPage(Guid sessionId, ControlPresentationFrame frame)
        {
            if (frame.PageCommand is not ControlPresentationPageCommand command || frame.Id is not string origin || ReplicaPageSlot(sessionId, origin) is not (string id, PaneRole slot))
            {
                return;
            }

            switch (command.Action)
            {
                case "resize":
                    model.UpdateOverlay(sessionId, overlay => overlay.Id == id && slot == PaneRole.Overlay ? overlay with { SizePercent = command.SizePercent } : overlay);
                    break;
                case "hide" or "show":
                    if (model.FindSession(sessionId)?.Overlays.GetValueOrDefault(slot)?.Hidden != (command.Action == "hide"))
                    {
                        model.SetOverlaysHidden(sessionId, slot, command.Action == "hide");
                    }

                    break;
                default:
                    if (PageCommand(sessionId, slot, command.Action, null) is string refusal)
                    {
                        Log.Notice("RemotePage", "origin request " + command.Action + " not applied: " + refusal);
                    }

                    break;
            }
        }

        /// <summary>The page closed there, or the stream went: the page here closes without telling the other side again.</summary>
        private void CloseReplicaPages(Guid sessionId, string? origin)
        {
            foreach ((string id, (Guid SessionId, string Origin, int Owner) page) in replicaPages.Where(pair => pair.Value.SessionId == sessionId && (origin is null || pair.Value.Origin == origin)).ToList())
            {
                replicaPages.Remove(id);
                replicaPageStates.Remove(id);
                KeyValuePair<PaneRole, SessionOverlay> slot = model.FindSession(sessionId)?.Overlays.FirstOrDefault(pair => pair.Value.Id == id) ?? default;
                if (slot.Value is not null)
                {
                    model.CloseOverlay(sessionId, id, slot.Key);
                }
            }
        }

        /// <summary>Tells the session's machine what each page handed over shows now, when that changed.</summary>
        private void ReportReplicaPages()
        {
            foreach ((string id, (Guid SessionId, string Origin, int Owner) page) in replicaPages.ToList())
            {
                Session? session = model.FindSession(page.SessionId);
                SessionOverlay? overlay = session?.Overlays.Values.FirstOrDefault(candidate => candidate.Id == id);
                if (overlay is null || PageState(id) is not ControlPageView view)
                {
                    continue;
                }

                ControlPresentationPageState state = new ControlPresentationPageState
                {
                    State = view.State,
                    Error = view.Error,
                    Page = view.Page,
                    Title = view.Title,
                    CanGoBack = view.CanGoBack,
                    CanGoForward = view.CanGoForward,
                    Hidden = overlay.Hidden,
                };
                if (replicaPageStates.GetValueOrDefault(id) != state)
                {
                    replicaPageStates[id] = state;
                    presentations.GetValueOrDefault(page.SessionId)?.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageState, Id = page.Origin, Owner = page.Owner, PageState = state });
                }
            }
        }

        /// <summary>
        /// The job ended there, or the stream went: a terminal held after its ssh ended closes; a running one closes when
        /// the job ended, and otherwise keeps its ssh until the program ends.
        /// </summary>
        private void CloseReplicaOverlays(Guid sessionId, string? job)
        {
            Session? session = model.FindSession(sessionId);
            foreach ((string id, (Guid SessionId, string Job) replica) in replicaOverlays.Where(pair => pair.Value.SessionId == sessionId && (job is null || pair.Value.Job == job)).ToList())
            {
                KeyValuePair<PaneRole, SessionOverlay> slot = session?.Overlays.FirstOrDefault(pair => pair.Value.Id == id) ?? default;
                if (slot.Value is null)
                {
                    replicaOverlays.Remove(id);
                    replicaOwners.Remove(replica.Job);
                }
                else if (job is not null || slot.Value.ExitCode is not null)
                {
                    // Closing it says so to the session's machine as well, as agterm's replica does even on that side's close.
                    model.CloseOverlay(sessionId, id, slot.Key);
                }
                else
                {
                    // Running when the stream went: nothing will close it later, so it closes when its ssh ends, --wait or not.
                    orphanedOverlays.Add(id);
                }
            }
        }

        private void OpenReplicaAsk(Guid sessionId, ControlPresentationAsk ask)
        {
            (AskRequest? replica, string? error) = model.OpenReplicaAsk(sessionId, ask);
            if (replica is null)
            {
                presentations.GetValueOrDefault(sessionId)?.Send(new ControlPresentationFrame { Kind = ControlPresentationKinds.AskRejected, Id = ask.Id, Owner = ask.Owner, Text = error });
                return;
            }

            replicaAsks[replica.Id] = (sessionId, ask.Id, ask.Owner);
        }

        /// <summary>Takes down questions handed here without answering them: one, or all of the session's with null.</summary>
        private void DropReplicaAsks(Guid sessionId, string? remoteId)
        {
            foreach (string id in replicaAsks.Where(pair => pair.Value.SessionId == sessionId && (remoteId is null || pair.Value.RemoteId == remoteId)).Select(pair => pair.Key).ToList())
            {
                replicaAsks.Remove(id);
                model.CancelAsk(id);
            }
        }

        /// <summary>What the other machine says of the session: its status, context and HUD on the row, its notifications here.</summary>
        private void OnPresentationFrame(Guid sessionId, ControlPresentationFrame frame)
        {
            if (model.FindSession(sessionId) is not Session session)
            {
                return;
            }

            switch (frame.Kind)
            {
                case ControlPresentationKinds.Snapshot:
                    ApplyRemoteLayout(session, frame.Snapshot?.Layout);
                    model.ApplyRemoteStatus(sessionId, frame.Snapshot?.Status, snapshot: true);
                    model.ApplyRemoteContext(sessionId, frame.Snapshot?.Context);
                    ShowRemoteHud(sessionId, frame.Snapshot?.Hud);
                    break;
                case ControlPresentationKinds.Status:
                    model.ApplyRemoteStatus(sessionId, frame.Status, snapshot: false);
                    break;
                case ControlPresentationKinds.Context:
                    model.ApplyRemoteContext(sessionId, frame.Context);
                    break;
                case ControlPresentationKinds.Hud:
                    ShowRemoteHud(sessionId, frame.Hud);
                    break;
                case ControlPresentationKinds.AskRequest when frame.Ask is not null:
                    OpenReplicaAsk(sessionId, frame.Ask);
                    break;
                case ControlPresentationKinds.Layout:
                    ApplyRemoteLayout(session, frame.Layout);
                    break;
                case ControlPresentationKinds.Sound:
                    // No name is this machine's own blocked sound, so each machine's choice applies where it plays; none chosen here
                    // stays silent. A name this machine lacks still sounds, as agterm's alert: the other side stayed silent for it.
                    string? sound = string.IsNullOrWhiteSpace(frame.Title) ? settings.BlockedStatusSoundName : frame.Title;
                    if (!string.IsNullOrWhiteSpace(sound) && !PlaySound(sound))
                    {
                        Log.Notice("RemotePresentation", "status sound " + sound + " from " + sessionId.Wire() + "'s origin is unknown here, playing the alert");
                        PlaySound("default");
                    }

                    break;
                case ControlPresentationKinds.OverlayRequest when frame.Overlay is not null:
                    OpenReplicaOverlay(sessionId, frame.Overlay);
                    break;
                case ControlPresentationKinds.OverlayResize when frame.Overlay is not null:
                    string? resized = replicaOverlays.FirstOrDefault(pair => pair.Value.SessionId == sessionId && pair.Value.Job == frame.Overlay.Job).Key;
                    model.UpdateOverlay(sessionId, overlay => overlay.Id == resized ? overlay with { SizePercent = frame.Overlay.SizePercent } : overlay);
                    break;
                case ControlPresentationKinds.OverlayClose when frame.Id is not null:
                    CloseReplicaOverlays(sessionId, frame.Id);
                    break;
                case ControlPresentationKinds.PageRequest when frame.Page is not null:
                    OpenReplicaPage(sessionId, frame.Page);
                    break;
                case ControlPresentationKinds.PageCommandKind:
                    ApplyReplicaPage(sessionId, frame);
                    break;
                case ControlPresentationKinds.PageClose when frame.Id is not null:
                    CloseReplicaPages(sessionId, frame.Id);
                    break;
                case ControlPresentationKinds.AskDismiss when frame.Id is not null:
                    DropReplicaAsks(sessionId, frame.Id);
                    break;
                case ControlPresentationKinds.Notify:
                    string title = string.IsNullOrEmpty(frame.Title) ? session.DisplayName : frame.Title;
                    model.RecordNotification(sessionId, title, frame.Body ?? string.Empty, NotificationSources.Mirrored);
                    NotifySession(sessionId, NotifySlot, title, frame.Body ?? string.Empty, "send");
                    break;
            }
        }

        /// <summary>
        /// The row follows its machine's split. A split removed there closes here with its connection, or, after a swap made
        /// here, the main pane that stood for it goes and the other takes its place; panes that swapped there swap here too.
        /// </summary>
        private void ApplyRemoteLayout(Session session, ControlPresentationLayout? layout)
        {
            if (layout is null)
            {
                return;
            }

            // agterm's applyLayout: a layout that is not valid is warned of and skipped; the rest of the frame stands.
            if (!layout.IsValid)
            {
                Log.Warning("RemotePresentation", "invalid layout for " + session.Id.Wire());
                return;
            }

            RemoteLayoutChange change = model.ApplyRemoteLayout(session.Id, layout);
            // agterm closes the pane that left by its identity, at once.
            foreach (PaneRole gone in AppModel.RemotePanesGone(session, layout))
            {
                if (model.FindSession(session.Id) is Session current && current.RemoteHost is string remote && AppModel.IsRemotePane(current, gone))
                {
                    PaneKey key = new PaneKey(current.Id, gone);
                    host.Stop(key);
                    OnRemotePaneRemoved(current, remote, key);
                }
            }

            if (model.FindSession(session.Id) is null || change == RemoteLayoutChange.SplitRemoved)
            {
                return;
            }

            // The layout rebound the panes here to their daemons' new places by identity. agterm puts the primary in the main
            // place: the terminals change places with their shells, as a swap of this machine's own does, so nothing is joined
            // again and the scrollback stays; a swap made here gives way to it the same way.
            if (model.RestoreRemoteOrder(session.Id))
            {
                SwapPaneShells(session.Id);
            }
        }

        private void ShowRemoteHud(Guid sessionId, ControlPresentationHud? hud)
        {
            if (model.ApplyRemoteHud(sessionId, hud) is SessionOverlay overlay)
            {
                ScheduleHudHide(sessionId, overlay.Id, overlay.Hud?.HideAfter);
            }
        }

        private async Task StartRemotePaneAsync(Session session, PaneKey key, int columns, int rows)
        {
            string remote = session.RemoteHost!;
            if (AppModel.RemoteDaemonOf(session, key.Pane) is not string daemon)
            {
                PostOutput(key, "\u001b[2m[" + Strings.T("{0} не назвал демон этой панели", remote) + "]\u001b[0m\r\n");
                return;
            }

            LiveClient? client = await RemoteHosts.ConnectAsync(remote, daemon);
            // The pane may have gone, or started another way, while ssh was being set up.
            if (windowClosed || model.FindSession(session.Id) is null || !AppModel.IsRemotePane(session, key.Pane) || host.IsRunning(key) || AppModel.RemoteDaemonOf(session, key.Pane) != daemon)
            {
                client?.Dispose();
                return;
            }

            if (remoteClients.Remove((session.Id, daemon), out LiveClient? stale))
            {
                stale.Dispose();
            }

            // agterm's attach wrapper: ssh that cannot be run exits 127 through its shell, and the pane holds for a key.
            if (client is null)
            {
                remoteStates[(session.Id, daemon)] = "held";
                remoteHeldCodes[(session.Id, daemon)] = 127;
                PostOutput(key, "\u001b[2m[" + Strings.T("не удалось запустить ssh до {0}", remote) + "]\r\n[" + Strings.T("{0}: сессия {1}, панель {2}: ssh завершился с кодом {3}. Нажмите клавишу, чтобы закрыть панель", remote, session.DisplayName, Panes.Name(key.Pane), 127) + "]\u001b[0m\r\n");
                PostState();
                return;
            }

            remoteStates.Remove((session.Id, daemon));
            remoteHeldCodes.Remove((session.Id, daemon));
            // The pane here joins the daemon of the shell it shows there, whose name stays through swaps on either side; a
            // join after the first follows, unless it is a takeover, which claims the lead.
            remoteClients[(session.Id, daemon)] = client;
            bool rejoined = !remoteJoined.Add(key);
            host.StartRemote(key, client.Attach(daemon, columns, rows, follow: rejoined && !claiming.Contains(key)));
            PostState();
        }

        /// <summary>
        /// A remote pane ended: a lost connection is retried, ssh's own answer holds the pane for a key, and a pane the other
        /// machine no longer has goes here too, as agterm's confirmed removal.
        /// </summary>
        private async Task OnRemotePaneEndedAsync(Session session, string remote, PaneKey key)
        {
            reattaching.Remove(key);
            claiming.Remove(key);
            // The daemon said the pane ended (or had no shell to join): it ended there; otherwise its ssh dropped. A row
            // closed with undo in reach loses the pane when the undo brings it back.
            LiveClient? ended = AppModel.RemoteDaemonOf(session, key.Pane) is string daemon ? remoteClients.GetValueOrDefault((session.Id, daemon)) : null;
            if (ended is { PaneEnded: true })
            {
                if (model.FindSession(session.Id) is null)
                {
                    endedWhileHidden.Add(key);
                }
                else
                {
                    OnRemotePaneRemoved(session, remote, key);
                }

                return;
            }

            // How ssh ended can take a moment to be known; it is waited for off the window's thread.
            int? relayStatus = ended is null ? null : await Task.Run(() => ended.RelayExitCode);
            if (windowClosed || model.FindSessionOrPending(session.Id) is not Session current || !AppModel.IsRemotePane(current, key.Pane))
            {
                return;
            }

            if (WaitKey(current, key.Pane) is not (Guid, string) wait)
            {
                return;
            }

            string? state = remoteStates.GetValueOrDefault(wait);
            // ssh's own failure (255: no route, refused login) reconnects by itself; any other end is the far side's answer
            // and holds for a key, as agterm, under what ssh or the far side printed, which agterm's pane shows above it.
            if (relayStatus is int status && status != 255)
            {
                if (state != "held")
                {
                    ForgetWait(wait);
                    remoteStates[wait] = "held";
                    remoteHeldCodes[wait] = status;
                }

                string said = ended?.LastError is string reason ? "\r\n" + reason : string.Empty;
                PostOutput(key, "\u001b[0m" + said + "\r\n\u001b[2m[" + Strings.T("{0}: сессия {1}, панель {2}: ssh завершился с кодом {3}. Нажмите клавишу, чтобы закрыть панель", remote, session.DisplayName, Panes.Name(key.Pane), status) + "]\u001b[0m\r\n");
                PostState();
                return;
            }

            // agterm switches off what the remote program left on (mouse, focus, paste, theme and size reporting), so reports
            // do not echo onto the kept screen, hides the cursor and shows the reconnecting line.
            PostOutput(key, "\u001b[0m\u001b[?1000l\u001b[?1002l\u001b[?1003l\u001b[?1006l\u001b[?1004l\u001b[?2004l\u001b[?2031l\u001b[?2048l\u001b[?25l\r\n\u001b[30;43m " + Strings.T("Связь с {0} потеряна · переподключение… · любая клавиша — сейчас", remote) + " \u001b[K\u001b[0m\r\n");
            if (state is not ("disconnected" or "held"))
            {
                ScheduleReconnect(wait);
            }
        }

        /// <summary>
        /// agterm's retryNow for a key on a waiting pane: only that pane tries now, its backoff over; a held pane closes
        /// instead, as the press-any-key prompt does there.
        /// </summary>
        private void RetryRemoteNow(Guid sessionId, PaneRole pane)
        {
            if (model.FindSession(sessionId) is not Session session || WaitKey(session, pane) is not (Guid, string) wait)
            {
                return;
            }

            switch (remoteStates.GetValueOrDefault(wait))
            {
                case "held":
                    CloseHeldPane(session, pane);
                    break;
                case "disconnected":
                    RetryWaitNow(wait);
                    PostState();
                    break;
            }
        }

        /// <summary>Due now with the backoff over; a probe already running is not started twice.</summary>
        private void RetryWaitNow((Guid Row, string Daemon) wait)
        {
            remoteFailures.Remove(wait);
            if (remoteProbing.Contains(wait))
            {
                return;
            }

            ScheduleProbe(wait, TimeSpan.Zero);
        }

        /// <summary>Waking up or a network change: every waiting pane and dropped stream retries now, not on its backoff.</summary>
        private void RetryAllRemotes()
        {
            foreach ((Guid Row, string Daemon) wait in remoteStates.Where(pair => pair.Value == "disconnected").Select(pair => pair.Key).ToList())
            {
                RetryWaitNow(wait);
            }

            foreach (RemotePresentationStream stream in presentations.Values)
            {
                stream.RetryNow();
            }

            PostState();
        }

        private void WatchWakeAndNetwork()
        {
            // As agterm's RemoteLinkObserver: the displays waking (not the system resuming) and the network retry. As agterm's path observer: any change of the network while it has a usable route (Wi-Fi to Ethernet, a new
            // address) retries, not its loss.
            NetworkAvailabilityChangedEventHandler available = (sender, args) =>
            {
                if (args.IsAvailable && HasRoute())
                {
                    Dispatcher.BeginInvoke(RetryAllRemotes);
                }
            };
            NetworkAddressChangedEventHandler changed = (sender, args) =>
            {
                if (HasRoute())
                {
                    Dispatcher.BeginInvoke(RetryAllRemotes);
                }
            };
            WatchDisplayWake();
            NetworkChange.NetworkAvailabilityChanged += available;
            NetworkChange.NetworkAddressChanged += changed;
            Closed += (sender, args) =>
            {
                NetworkChange.NetworkAvailabilityChanged -= available;
                NetworkChange.NetworkAddressChanged -= changed;
            };
        }

        /// <summary>
        /// agterm's satisfied path: an interface that is up, not loopback or a tunnel, with a gateway. Virtual adapters that
        /// stay up without one (WSL, Hyper-V) do not count, so losing Wi-Fi is not read as a change to a working network.
        /// </summary>
        private static bool HasRoute()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && adapter.GetIPProperties().GatewayAddresses.Any(gateway => !gateway.Address.Equals(System.Net.IPAddress.Any) && !gateway.Address.Equals(System.Net.IPAddress.IPv6Any)));
            }
            catch (NetworkInformationException)
            {
                return true;
            }
        }

        /// <summary>
        /// agterm retries when the screens wake, not only when the system resumes: the display turning on is told to the
        /// window as a power setting change. Its first report is the state at registration, never a wake.
        /// </summary>
        private void WatchDisplayWake()
        {
            void Register()
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Guid display = ConsoleDisplayState;
                IntPtr registration = RegisterPowerSettingNotification(handle, ref display, 0);
                bool reported = false;
                System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (message == 0x218 && wParam.ToInt32() == 0x8013)
                    {
                        PowerBroadcastSetting setting = System.Runtime.InteropServices.Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                        if (setting.PowerSetting == ConsoleDisplayState)
                        {
                            if (reported && setting.Data == 1)
                            {
                                RetryAllRemotes();
                            }

                            reported = true;
                        }
                    }

                    return IntPtr.Zero;
                });
                Closed += (sender, args) => UnregisterPowerSettingNotification(registration);
            }

            if (new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero)
            {
                Register();
            }
            else
            {
                SourceInitialized += (sender, args) => Register();
            }
        }

        private static readonly Guid ConsoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public uint DataLength;
            public uint Data;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        /// <summary>
        /// agterm's confirmed removal: the other machine no longer has the pane, so its replica goes without a key. A split
        /// closes; the row closes with its last replica, except while a split opened here keeps it.
        /// </summary>
        private void OnRemotePaneRemoved(Session session, string remote, PaneKey key)
        {
            if (WaitKey(session, key.Pane) is (Guid, string) wait)
            {
                ForgetWait(wait);
            }

            remoteJoined.Remove(key);
            // agterm: the main pane gone there while a split lives on, the other machine's or one opened here, is the split
            // promoted to the row's only pane. A split of this machine not started yet would be lost, so the main pane stays.
            PaneKey splitKey = new PaneKey(session.Id, PaneRole.Split);
            if (key.Pane == PaneRole.Main && session.HasSplit && host.IsRunning(splitKey))
            {
                PromoteAfterMainGone(session);
                return;
            }

            // agterm keeps the pane, marked ended, while a split of this machine has not started: a key closes it and the
            // split takes its place.
            if (key.Pane == PaneRole.Main && session.HasSplit && !AppModel.IsRemotePane(session, PaneRole.Split))
            {
                endedThere.Add(key);
                PostOutput(key, "\u001b[0m\r\n\u001b[2m[" + Strings.T("{0}: сессия {1}, панель {2} там закончилась. Нажмите клавишу, чтобы закрыть панель", remote, session.DisplayName, Panes.Name(key.Pane)) + "]\u001b[0m\r\n");
                PostState();
                return;
            }

            bool mainGone = key.Pane == PaneRole.Main || !host.IsRunning(new PaneKey(session.Id, PaneRole.Main));
            if (key.Pane == PaneRole.Split)
            {
                model.CloseSplit(session.Id);
            }

            // Only the split went: the row stays with its main pane, waiting for nothing when that one is of this machine.
            if (!mainGone)
            {
                if (!AppModel.HasRemotePanes(session))
                {
                    ForgetRemoteRow(session.Id);
                }

                PostState();
                return;
            }

            StopSession(session.Id);
            model.RemoveSession(session.Id);
        }

        /// <summary>
        /// The split takes the main pane's place. When it was a shell of this machine, the row keeps its place, name and status
        /// stream as a remote row, as agterm's, but no pane waits for that machine any more.
        /// </summary>
        private void PromoteAfterMainGone(Session session)
        {
            bool remoteSplit = AppModel.IsRemotePane(session, PaneRole.Split);
            PaneKey main = new PaneKey(session.Id, PaneRole.Main);
            PaneKey split = new PaneKey(session.Id, PaneRole.Split);
            host.Move(split, main);
            host.Move(new PaneKey(session.Id, PaneRole.OverlayRight), new PaneKey(session.Id, PaneRole.OverlayLeft));
            remoteJoined.Remove(split);
            if (remoteSplit)
            {
                remoteJoined.Add(main);
            }

            Post(new { type = "promote", sessionId = session.Id });
            model.PromoteSplit(session.Id);
            if (!remoteSplit)
            {
                ForgetRemoteRow(session.Id);
            }

            PostState();
        }

        /// <summary>
        /// The row has no pane of the other machine left: nothing waits for it and a connection of the row's own closes. The
        /// status stream follows the row, as agterm's, and stays while the row is shown.
        /// </summary>
        private void ForgetRemoteRow(Guid sessionId)
        {
            ReleaseRemoteClients(sessionId);
            ForgetWaits(sessionId);
        }

        /// <summary>
        /// agterm's held pane: ssh ended with a status of its own, and a key on it closes it, as the press-any-key prompt
        /// does there. The row goes with its last pane; a split left behind takes the main pane's place.
        /// </summary>
        /// <summary>A key on a pane that ended there while a split of this machine was starting: the split takes its place.</summary>
        private void CloseEndedPane(Session session, PaneKey key)
        {
            endedThere.Remove(key);
            host.Stop(key);
            // After a swap here the ended pane may be the split: it closes, and the main pane stays.
            if (key.Pane == PaneRole.Split)
            {
                remoteJoined.Remove(key);
                model.CloseSplit(session.Id);
                PostState();
            }
            else if (session.HasSplit)
            {
                PromoteAfterMainGone(session);
            }
            else
            {
                StopSession(session.Id);
                model.RemoveSession(session.Id);
            }
        }

        private void CloseHeldPane(Session session, PaneRole pane)
        {
            if (WaitKey(session, pane) is not (Guid, string) wait)
            {
                return;
            }

            ForgetWait(wait);
            if (pane == PaneRole.Split && session.HasSplit)
            {
                host.Stop(new PaneKey(session.Id, PaneRole.Split));
                remoteJoined.Remove(new PaneKey(session.Id, PaneRole.Split));
                model.CloseSplit(session.Id);
            }
            else if (session.HasSplit)
            {
                // agterm closes each held pane with its own key: a split, held as well or not, takes the main pane's place.
                host.Stop(new PaneKey(session.Id, PaneRole.Main));
                PromoteAfterMainGone(session);
            }
            else
            {
                StopSession(session.Id);
                model.RemoveSession(session.Id);
                return;
            }

            PostState();
        }

        /// <summary>
        /// agterm's reconnect, by pane: it waits for its machine, probing it at once, then after 1, 2, 4, 8, 16 and 30 seconds,
        /// doubling to every 5 minutes after eight failures. Only a probe that answers ends the wait; a pane that drops again
        /// within a minute of coming back keeps its backoff, so a machine that answers but fails the attach is not hammered.
        /// </summary>
        private void ScheduleReconnect((Guid Row, string Daemon) wait)
        {
            if (remoteStates.ContainsKey(wait))
            {
                return;
            }

            remoteStates[wait] = "disconnected";
            TimeSpan delay = TimeSpan.Zero;
            if (remoteResumed.Remove(wait, out (DateTime At, int Failures) last) && DateTime.UtcNow - last.At < ReconnectSettle)
            {
                remoteFailures[wait] = last.Failures + 1;
                delay = RemoteHosts.RetryDelay(last.Failures + 1);
            }

            ScheduleProbe(wait, delay);
            PostState();
        }

        private void ScheduleProbe((Guid Row, string Daemon) wait, TimeSpan delay)
        {
            if (windowClosed)
            {
                return;
            }

            if (reconnectTimers.Remove(wait, out DispatcherTimer? earlier))
            {
                earlier.Stop();
            }

            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = delay };
            timer.Tick += (sender, args) =>
            {
                timer.Stop();
                reconnectTimers.Remove(wait);
                _ = ProbeAsync(wait);
            };
            reconnectTimers[wait] = timer;
            timer.Start();
        }

        /// <summary>agterm's tickReconnects for one pane: its machine probed, a pane gone meanwhile dropped from the book.</summary>
        private async Task ProbeAsync((Guid Row, string Daemon) wait)
        {
            if (remoteStates.GetValueOrDefault(wait) != "disconnected")
            {
                return;
            }

            if (model.FindSessionOrPending(wait.Row)?.RemoteHost is not string remote || WaitingPane(wait) is null)
            {
                ForgetWait(wait);
                return;
            }

            if (!remoteProbing.Add(wait))
            {
                return;
            }

            (bool answered, string? reason) = await RemoteHosts.ProbeAsync(remote);
            remoteProbing.Remove(wait);
            if (windowClosed || remoteStates.GetValueOrDefault(wait) != "disconnected")
            {
                return;
            }

            if (WaitingPane(wait) is not PaneKey pane)
            {
                ForgetWait(wait);
                return;
            }

            if (answered)
            {
                int failures = remoteFailures.GetValueOrDefault(wait);
                // agterm: the wait ends and the pane attaches again; a row closed with undo in reach cannot, so it waits
                // again, as one that dropped right after coming back: its failures count on and its step grows.
                if (model.FindSession(wait.Row) is Session session)
                {
                    remoteResumed[wait] = (DateTime.UtcNow, failures);
                    ReconnectPane(session, pane.Pane, wait);
                }
                else
                {
                    remoteFailures[wait] = failures + 1;
                    remoteReasons.Remove(wait);
                    ScheduleProbe(wait, RemoteHosts.RetryDelay(failures + 1));
                }

                return;
            }

            int failed = remoteFailures.GetValueOrDefault(wait) + 1;
            remoteFailures[wait] = failed;
            // What the last probe's ssh said, or nothing: never a reason of an earlier try.
            remoteReasons[wait] = reason;
            ScheduleProbe(wait, RemoteHosts.RetryDelay(failed));
            PostState();
        }

        /// <summary>The machine answered: this pane joins its daemon again through its own ssh, as agterm's PaneLead.reconnect.</summary>
        private void ReconnectPane(Session session, PaneRole pane, (Guid Row, string Daemon) wait)
        {
            if (windowClosed)
            {
                return;
            }

            remoteStates.Remove(wait);
            remoteFailures.Remove(wait);
            remoteReasons.Remove(wait);
            PaneKey key = new PaneKey(session.Id, pane);
            // agterm covers a pane that had reported a role, leading or not, until the new attach reports one; it joins
            // without the claim.
            claiming.Remove(key);
            if (host.Lead(key) is not null)
            {
                reattaching.Add(key);
            }

            // The page makes the terminal again and attaches it; the other pane is left as it is.
            host.Stop(key);
            Post(new { type = "restart", sessionId = session.Id, pane = Panes.Name(pane) });
            PostState();
        }

        /// <summary>Every pane of the row leaves the reconnect book.</summary>
        private void ForgetWaits(Guid sessionId)
        {
            foreach ((Guid Row, string Daemon) wait in remoteStates.Keys.Concat(reconnectTimers.Keys).Concat(remoteResumed.Keys).Where(wait => wait.Row == sessionId).Distinct().ToList())
            {
                ForgetWait(wait);
            }
        }

        /// <summary>
        /// agterm's PaneLead.reattach: the pane lets go of its daemon, which keeps the shell, and joins it again with a fresh
        /// terminal from the daemon's snapshot, covered until the new attach reports its lead. A takeover claims the lead; a
        /// pane nobody leads joins without the claim, so it leads only when nobody claimed the pane meanwhile.
        /// </summary>
        private void Reattach(PaneKey key, bool claim)
        {
            reattaching.Add(key);
            if (claim)
            {
                claiming.Add(key);
            }
            else
            {
                claiming.Remove(key);
            }

            host.Detach(key);
            Post(new { type = "restart", sessionId = key.SessionId, pane = Panes.Name(key.Pane) });
            PostState();
        }

        /// <summary>
        /// agterm's PaneLead.consumes: a key on a covered pane takes it over, unless it is already on its way, whose fresh
        /// terminal is covered until its first report.
        /// </summary>
        private void TakeOver(PaneKey key)
        {
            if (Covered(key) && !reattaching.Contains(key) && host.IsRunning(key))
            {
                Reattach(key, claim: true);
            }
        }

        /// <summary>
        /// agterm's ZmxLeadState.covered: another client leads the pane, nobody does, or it is joining again and has not
        /// reported its lead yet; its terminal here is not the program's, so it is covered and takes no keys.
        /// </summary>
        private bool Covered(PaneKey key)
        {
            return host.Lead(key) is "follower" or "unowned" || reattaching.Contains(key);
        }

        /// <summary>agterm's "Taking over…": the pane is joining again with a fresh attach and no report came yet.</summary>
        private bool Taking(PaneKey key)
        {
            return reattaching.Contains(key);
        }

        /// <summary>The row is gone, or has no pane of the other machine left: its panes' ssh connections close.</summary>
        private void ReleaseRemoteClients(Guid sessionId)
        {
            foreach ((Guid Row, string Daemon) entry in remoteClients.Keys.Where(entry => entry.Row == sessionId).ToList())
            {
                if (remoteClients.Remove(entry, out LiveClient? client))
                {
                    client.Dispose();
                }
            }
        }
    }
}
