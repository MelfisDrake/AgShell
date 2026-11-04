using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// The workspace tree of one window and its selection. Not thread-safe: use it from one thread.
    /// </summary>
    public sealed partial class AppModel
    {
        public const int RecencyLimit = 100;
        public const double DefaultFontSize = 13;
        public const double MinFontSize = 6;
        public const double MaxFontSize = 48;

        /// <summary>Which keystroke clears a blocked or completed status, from the settings.</summary>
        public string StatusReset { get; set; } = "firstKey";

        /// <summary>The terminal font size of a session without its own, from the settings.</summary>
        public double BaseFontSize { get; set; } = DefaultFontSize;

        /// <summary>agterm's dashboard auto-size base: the font size set in Settings, else Ghostty's default.</summary>
        public double DashboardBaseFontSize { get; set; } = DashboardLayout.GhosttyDefaultFontSize;

        private readonly List<Workspace> workspaces = new List<Workspace>();
        private readonly List<Guid> recency = new List<Guid>();

        // The workspace created last, which receives new sessions until the selection moves.
        private Guid? freshWorkspaceId;

        /// <summary>Raised after every change, including cwd updates.</summary>
        public event Action? Changed;

        /// <summary>
        /// Raised instead of Changed when only a session's title moved, which a program like Codex animates several
        /// times a second; as in agterm, such a tick updates that one label rather than the whole window.
        /// </summary>
        public event Action<Session>? LabelChanged;

        /// <summary>Raised after changes that must be saved right away. Cwd updates are not among them: they are saved with the next structural change or on exit.</summary>
        public event Action? StructureChanged;

        public event Action? LiveFailureChanged;

        /// <summary>agterm's cancelPendingSave: a soft close happened, and a save already scheduled must not write it out.</summary>
        public event Action? PendingSaveCancelled;

        /// <summary>Raised when only the selection changed; saving it can wait.</summary>
        public event Action? SelectionChanged;

        /// <summary>The sidebar tree or an overlay changed; the host coalesces these into tree.changed events.</summary>
        public event Action? TreeChanged;

        public IReadOnlyList<Workspace> Workspaces => workspaces;

        /// <summary>Status, notification and lifecycle events for control clients.</summary>
        public ControlEventRing Events { get; } = new ControlEventRing();

        public Guid? SelectedSessionId { get; private set; }

        /// <summary>Session ids, most recently selected first.</summary>
        public IReadOnlyList<Guid> RecentSessions => recency;

        public IEnumerable<Session> AllSessions => workspaces.SelectMany(workspace => workspace.Sessions);

        public bool CanRemoveWorkspace => workspaces.Count > 1;

        public string NextWorkspaceName => "workspace " + (workspaces.Count + 1);

        public Session? FindSession(Guid sessionId)
        {
            return AllSessions.FirstOrDefault(session => session.Id == sessionId);
        }

        public Workspace? FindWorkspace(Guid workspaceId)
        {
            return workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId);
        }

        public Workspace? FindWorkspaceOf(Guid sessionId)
        {
            return workspaces.FirstOrDefault(workspace => workspace.SessionList.Any(session => session.Id == sessionId));
        }

        /// <summary>The workspace a new session goes to: a just created one, else the selected session's, else the last one.</summary>
        public Workspace? CurrentWorkspace
        {
            get
            {
                if (freshWorkspaceId is Guid fresh && FindWorkspace(fresh) is Workspace created)
                {
                    return created;
                }

                Workspace? selected = SelectedSessionId is Guid id ? FindWorkspaceOf(id) : null;
                return selected ?? workspaces.LastOrDefault();
            }
        }

        /// <summary>
        /// Adds a workspace at the end. <paramref name="reveal"/>, as agterm's revealNewWorkspace, puts it in the focus set
        /// while the filter is on and makes it the one new sessions go to; a background create leaves both alone.
        /// </summary>
        public Workspace AddWorkspace(string? name = null, bool reveal = true)
        {
            Workspace workspace = new Workspace(Guid.NewGuid(), NormalizeName(name, NextWorkspaceName));
            workspaces.Add(workspace);
            if (reveal)
            {
                if (FocusEnabled)
                {
                    focusMembers.Add(workspace.Id);
                }

                freshWorkspaceId = workspace.Id;
            }

            RaiseStructureChanged();
            return workspace;
        }

        /// <summary>
        /// Makes the workspace current: selects its first session, or, when it is empty, makes it the one new sessions go to.
        /// </summary>
        public void SelectWorkspace(Guid workspaceId)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            if (workspace.SessionList.Count > 0)
            {
                SelectSession(workspace.SessionList[0].Id);
                freshWorkspaceId = null;
                return;
            }

            // agterm: an empty workspace the filter hides joins the marked set, so what is current is on screen.
            bool widened = FocusEnabled && focusMembers.Add(workspaceId);
            freshWorkspaceId = workspaceId;
            if (widened)
            {
                ReselectIfHidden();
                StructureChanged?.Invoke();
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// agterm's canStepWorkspaces: never in the flagged view; a lone shown workspace is a dead end only while it is
        /// already current, since entering it from a hidden one is the point of the step.
        /// </summary>
        public bool CanStepWorkspaces
        {
            get
            {
                if (SidebarMode != TreeMode)
                {
                    return false;
                }

                List<Workspace> visible = VisibleWorkspaces.ToList();
                if (visible.Count == 0)
                {
                    return false;
                }

                return CurrentWorkspace is not Workspace current || !visible.Contains(current) || visible.Count > 1;
            }
        }

        /// <summary>
        /// agterm's navigateWorkspace: steps the current workspace <paramref name="offset"/> places through those shown,
        /// wrapping; from a workspace outside them it lands on the end the step comes from. False when there is nowhere to go.
        /// </summary>
        public bool SelectRelativeWorkspace(int offset)
        {
            if (!CanStepWorkspaces)
            {
                return false;
            }

            List<Workspace> visible = VisibleWorkspaces.ToList();
            int current = CurrentWorkspace is Workspace workspace ? visible.IndexOf(workspace) : -1;
            int next = current < 0
                ? (offset > 0 ? 0 : visible.Count - 1)
                : ((current + offset) % visible.Count + visible.Count) % visible.Count;
            SelectWorkspace(visible[next].Id);
            return true;
        }

        /// <summary>Moves a workspace to <paramref name="index"/>, counted after it is taken out of its current place.</summary>
        public void MoveWorkspace(Guid workspaceId, int index)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            int before = workspaces.IndexOf(workspace);
            workspaces.Remove(workspace);
            int clamped = Math.Clamp(index, 0, workspaces.Count);
            workspaces.Insert(clamped, workspace);
            // agterm's moveWorkspace: tree.changed only when the order changed.
            if (clamped != before)
            {
                RaiseStructureChanged();
            }
            else
            {
                RaiseSaved();
            }
        }

        public void RenameWorkspace(Guid workspaceId, string name)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            if (TerminalText.Name(name) is not string renamed || workspace.Name == renamed)
            {
                return;
            }

            workspace.Name = renamed;
            RaiseStructureChanged();
        }

        /// <summary>
        /// agterm's setWorkspaceExpanded: the user's own fold or unfold, saved. It also ends a reveal, so a row a selection
        /// opened on screen folds at once although it was saved folded.
        /// </summary>
        public void SetWorkspaceCollapsed(Guid workspaceId, bool collapsed)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            bool wasRevealed = revealed.Remove(workspaceId);
            if (workspace.Collapsed == collapsed)
            {
                if (wasRevealed)
                {
                    Changed?.Invoke();
                }

                return;
            }

            workspace.Collapsed = collapsed;
            // agterm's setWorkspaceExpanded saves and sends no tree.changed: folding is not the tree.
            RaiseSaved();
        }

        /// <summary>
        /// agterm's sidebar reveal: a workspace saved folded that the selection moved into shows open on screen, and the
        /// saved fold stays (tree reports it collapsed). Not saved; the user's fold of the row ends it.
        /// </summary>
        private readonly HashSet<Guid> revealed = new HashSet<Guid>();

        /// <summary>Whether the row is folded on screen: saved folded and not opened by a reveal. What the sidebar shows and the fold toggle acts on.</summary>
        public bool ShownCollapsed(Workspace workspace)
        {
            return workspace.Collapsed && !revealed.Contains(workspace.Id);
        }

        /// <summary>agterm's Expand Workspaces: every workspace, including those the focus filter hides.</summary>
        public void ExpandAllWorkspaces()
        {
            SetCollapsedWhere(workspace => false);
        }

        /// <summary>agterm's Collapse Workspaces: every workspace but the current one, which stays expanded.</summary>
        public void CollapseOtherWorkspaces()
        {
            Guid? current = CurrentWorkspace?.Id;
            SetCollapsedWhere(workspace => workspace.Id != current);
        }

        private void SetCollapsedWhere(Func<Workspace, bool> collapsed)
        {
            bool changed = false;
            bool unrevealed = false;
            foreach (Workspace workspace in workspaces)
            {
                bool value = collapsed(workspace);
                unrevealed |= revealed.Remove(workspace.Id);
                if (workspace.Collapsed != value)
                {
                    workspace.Collapsed = value;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseSaved();
            }
            else if (unrevealed)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Removes the workspace together with its sessions and returns the ids of the removed sessions.
        /// The last workspace is kept: removing it does nothing and returns an empty list.
        /// </summary>
        public IReadOnlyList<Guid> RemoveWorkspace(Guid workspaceId)
        {
            return RemoveWorkspace(workspaceId, final: true);
        }

        /// <summary>
        /// Takes the workspace out of the tree. A final removal records it as recently closed and reports its sessions
        /// closed; a soft one leaves both to <see cref="FinalizeClose"/>, since an undo may still bring it back.
        /// </summary>
        private IReadOnlyList<Guid> RemoveWorkspace(Guid workspaceId, bool final)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            if (!CanRemoveWorkspace)
            {
                return Array.Empty<Guid>();
            }

            // agterm records the membership before the removal drops it, so Open Recent can mark the workspace again.
            if (final)
            {
                RecordRecentWorkspace(workspace, LocalSelection(workspace), focusMembers.Contains(workspaceId));
            }

            List<Guid> removed = workspace.SessionList.Select(session => session.Id).ToList();
            int index = workspaces.IndexOf(workspace);
            bool removingSelected = SelectedSessionId is Guid selected && removed.Contains(selected);
            workspaces.RemoveAt(index);
            // A soft close keeps its sessions' place in Ctrl+Tab history for an undo, as agterm's; the final close drops it.
            if (final)
            {
                recency.RemoveAll(removed.Contains);
            }

            DropFromDashboard(removed);
            removed.ForEach(CancelAsksOf);
            // As agterm's: the sessions are reported closed before a new selection is.
            foreach (Session session in workspace.SessionList)
            {
                AppendClosed(workspace.Id, session);
            }

            if (freshWorkspaceId == workspaceId)
            {
                freshWorkspaceId = null;
            }

            revealed.Remove(workspaceId);
            DropFocusMember(workspaceId);
            if (removingSelected)
            {
                SetSelection(WorkspaceRemovalTarget(index));
                DisableFocusIfSelectionOutside();
            }

            RaiseRemoved(final);
            return removed;
        }

        /// <summary>
        /// Adds a session. A null <paramref name="index"/> appends it. Without <paramref name="select"/> it is created in the background,
        /// leaving the selection and the sidebar as they were.
        /// </summary>
        public Session AddSession(Guid workspaceId, string profileId, string? cwd, int? index = null, string? name = null, bool select = true, string? remoteHost = null)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            Session session = new Session(Guid.NewGuid(), profileId, cwd is null ? DefaultCwd(profileId) : TerminalText.Sanitized(cwd), TerminalText.Name(name));
            // agterm's Session(paneIdentity:): the main pane's id comes with the session, so the save below carries it.
            session.PaneIdMap[PaneRole.Main] = Guid.NewGuid().Wire();
            workspace.SessionList.Insert(Math.Clamp(index ?? workspace.SessionList.Count, 0, workspace.SessionList.Count), session);
            Events.Append(ControlEventKinds.SessionCreated, workspace.Id, session.Id, new ControlEventPayload { Name = session.DisplayName });
            // agterm's remote.opened rides right behind session.created, before the row is selected.
            if (remoteHost is not null)
            {
                session.RemoteHost = remoteHost;
                Events.Append(ControlEventKinds.RemoteOpened, workspace.Id, session.Id, new ControlEventPayload { Name = session.DisplayName, Host = remoteHost });
            }

            // agterm's addSession writes the selection without selectSession, so the previous session keeps a one-time
            // completed; a session made in a workspace the filter hides switches the filter off.
            if (select)
            {
                SetSelection(session.Id, clearPreviousAutoReset: false);
                DisableFocusIfSelectionOutside();
            }

            RaiseStructureChanged();
            return session;
        }

        /// <summary>
        /// agterm's remote attach: a row for a session another machine runs, with its split when it has one. Its panes are
        /// that machine's shells; it is never saved and never goes to recently closed. The panes' daemons are named as agterm's
        /// tree names them, and a pane here joins its daemon by that name.
        /// </summary>
        public Session AddRemoteSession(Guid workspaceId, string host, Guid remoteSessionId, string name, bool split, string? cwd = null, int? presentation = ControlPresentationKinds.Version, string? splitAxis = null, string? mainDaemon = null, string? splitDaemon = null)
        {
            // The directory is the other machine's: launches here use it when it exists here too, else the home folder.
            Session session = AddSession(workspaceId, RemoteProfile, cwd, name: name, remoteHost: host);
            session.RemoteSessionId = remoteSessionId;
            session.RemoteBinding[PaneRole.Main] = PaneRole.Main;
            if (mainDaemon is { Length: > 0 })
            {
                session.RemotePaneIds[PaneRole.Main] = mainDaemon;
            }

            if (split)
            {
                session.RemoteBinding[PaneRole.Split] = PaneRole.Split;
                if (splitDaemon is { Length: > 0 })
                {
                    session.RemotePaneIds[PaneRole.Split] = splitDaemon;
                }
            }
            if (split && Panes.TryParseAxis(splitAxis, out SplitAxis axis))
            {
                session.SplitAxis = axis;
            }

            // As agterm's attach, the split it brings takes the focus.
            if (split)
            {
                session.SplitFocused = true;
            }

            session.RemotePresentation = presentation;
            if (split)
            {
                session.HasSplit = true;
                session.SplitShown = true;
                session.PaneIdMap[PaneRole.Split] = Guid.NewGuid().Wire();
            }

            RaiseStructureChanged();
            return session;
        }

        /// <summary>The profile a remote row carries: its shells are the other machine's, whatever they are.</summary>
        public const string RemoteProfile = "remote";

        /// <summary>Opens a new session right after the source one, with the same shell and directory and nothing else.</summary>
        public Session DuplicateSession(Guid sessionId, string? localProfile = null)
        {
            Session source = RequireSession(sessionId);
            Workspace workspace = FindWorkspaceOf(sessionId)!;
            // agterm: the copy opens in the focused pane's directory; a remote row's is a shell of this machine, in that
            // directory when it exists here.
            return source.RemoteHost is null
                ? AddSession(workspace.Id, source.ProfileId, source.FocusedCwd, workspace.SessionList.IndexOf(source) + 1)
                : AddSession(workspace.Id, localProfile ?? source.ProfileId, LocalDirectory(source.FocusedCwd), workspace.SessionList.IndexOf(source) + 1);
        }

        /// <summary>
        /// agterm's initialCwd, which a session always has: the home folder when none was given. A WSL session's home is the
        /// distribution's, which this side cannot name, so it stays unset there; another machine's row likewise.
        /// </summary>
        private static string? DefaultCwd(string profileId)
        {
            return profileId.StartsWith("wsl:", StringComparison.Ordinal) || profileId == RemoteProfile
                ? null
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>agterm's localWorkingDirectory: another machine's path, when it is a folder here too; else none, the home folder.</summary>
        public static string? LocalDirectory(string? path)
        {
            return path is not null && System.IO.Directory.Exists(path) ? path : null;
        }

        /// <summary>Records what a pane ran at quit, for re-run restore; null when it held only its shell.</summary>
        public void SetRestoreCommand(Guid sessionId, PaneRole pane, string? command)
        {
            Session session = RequireSession(sessionId);
            if (pane == PaneRole.Split)
            {
                session.SplitRestoreCommand = command;
            }
            else
            {
                session.RestoreCommand = command;
            }
        }

        /// <summary>
        /// agterm's loadStore for a window reopened during the run: its sessions are announced as created, as they come
        /// back into the app, and the tree as changed.
        /// </summary>
        public void AnnounceReopened()
        {
            foreach (Workspace workspace in workspaces)
            {
                foreach (Session session in workspace.SessionList)
                {
                    AppendCreated(workspace.Id, session);
                }
            }

            TreeChanged?.Invoke();
        }

        /// <summary>agterm's closeWindow and removeWindow: the window's sessions leave the app, and are reported closed.</summary>
        public void AnnounceDeleted()
        {
            foreach (Workspace workspace in workspaces)
            {
                foreach (Session session in workspace.SessionList)
                {
                    AppendClosed(workspace.Id, session);
                }
            }

            TreeChanged?.Invoke();
        }

        /// <summary>
        /// agterm's takePendingForegroundCommand: the capture this launch armed for the pane, taken so it runs once; a split
        /// made later in the run finds nothing.
        /// </summary>
        public string? TakePendingCapture(Guid sessionId, PaneRole pane)
        {
            if (FindSession(sessionId) is not Session session)
            {
                return null;
            }

            string? command = PeekPendingCapture(session, pane);
            if (pane == PaneRole.Split)
            {
                session.PendingSplitRestoreCommand = null;
            }
            else if (pane == PaneRole.Main)
            {
                session.PendingRestoreCommand = null;
            }

            return command;
        }

        public static string? PeekPendingCapture(Session session, PaneRole pane)
        {
            return pane switch
            {
                PaneRole.Main => session.PendingRestoreCommand,
                PaneRole.Split => session.PendingSplitRestoreCommand,
                _ => null,
            };
        }

        /// <summary>agterm's takePendingRestoreOverride: the pin as at launch, taken once; the saved pin stays for the next launch.</summary>
        public string? TakePendingPin(Guid sessionId, PaneRole pane)
        {
            if (FindSession(sessionId) is not Session session)
            {
                return null;
            }

            string? pin = PeekPendingPin(session, pane);
            if (pane == PaneRole.Split)
            {
                session.PendingSplitRestorePin = null;
            }
            else if (pane == PaneRole.Main)
            {
                session.PendingRestorePin = null;
            }

            return pin;
        }

        public static string? PeekPendingPin(Session session, PaneRole pane)
        {
            return pane switch
            {
                PaneRole.Main => session.PendingRestorePin,
                PaneRole.Split => session.PendingSplitRestorePin,
                _ => null,
            };
        }

        /// <summary>agterm's clearPendingForegroundCommands over the window: the launch could not strip the file, so nothing replays.</summary>
        public void ClearPendingCaptures()
        {
            foreach (Session session in AllSessions)
            {
                session.PendingRestoreCommand = null;
                session.PendingSplitRestoreCommand = null;
            }
        }

        /// <summary>
        /// agterm's clearPendingRestoreOverrides: a session leaving the tree that undo may bring back as the same object
        /// drops what the launch armed, so it does not run when its panes start again. The saved pins stay.
        /// </summary>
        internal static void ClearPending(Session session)
        {
            session.PendingRestoreCommand = null;
            session.PendingSplitRestoreCommand = null;
            session.PendingRestorePin = null;
            session.PendingSplitRestorePin = null;
        }

        /// <summary>Whether a saved window carries captures, which only the app's launch may replay.</summary>
        public static bool CarriesCaptures(StateSnapshot snapshot)
        {
            return snapshot.Workspaces.Any(workspace => workspace.Sessions.Any(session => session.RestoreCommand is not null || session.SplitRestoreCommand is not null));
        }

        /// <summary>agterm's stripCaptures: the saved window without its one-shot captures; pins and everything else stay.</summary>
        public static StateSnapshot StripCaptures(StateSnapshot snapshot)
        {
            return snapshot with
            {
                Workspaces = snapshot.Workspaces.Select(workspace => workspace with
                {
                    Sessions = workspace.Sessions.Select(session => session with { RestoreCommand = null, SplitRestoreCommand = null }).ToList(),
                }).ToList(),
            };
        }

        /// <summary>agterm's session restore: pins a line (empty for a plain shell) or, with null, goes back to the capture.</summary>
        public void SetRestorePin(Guid sessionId, PaneRole pane, string? pin)
        {
            Session session = RequireSession(sessionId);
            if (pane == PaneRole.Split)
            {
                session.SplitRestorePin = pin;
            }
            else
            {
                session.RestorePin = pin;
            }

            RaiseStructureChanged();
        }

        /// <summary>agterm's session background: without a pane the session's own, with one that pane's; null clears it.</summary>
        public void SetBackground(Guid sessionId, PaneRole? pane, ControlBackground? background)
        {
            Session session = RequireSession(sessionId);
            if (pane is PaneRole own)
            {
                if (background is null)
                {
                    session.PaneBackgroundMap.Remove(own);
                }
                else
                {
                    session.PaneBackgroundMap[own] = background;
                }
            }
            else
            {
                session.Background = background;
            }

            RaiseStructureChanged();
        }

        /// <summary>agterm's resetSessionFontSizes: every session's Ctrl+/- zoom goes, so all panes take the new size.</summary>
        public void ResetFontSizes()
        {
            if (AllSessions.Any(session => session.FontSizeMap.Count > 0 || session.SessionFontSize is not null))
            {
                foreach (Session session in AllSessions)
                {
                    session.FontSizeMap.Clear();
                    session.SessionFontSize = null;
                }

                RaiseStructureChanged();
            }
        }

        /// <summary>The pane's id for the shell starting in it: the one it has, else a new one (a scratch's, for its lifetime).</summary>
        public string PaneIdFor(Guid sessionId, PaneRole pane)
        {
            Session session = RequireSession(sessionId);
            if (!session.PaneIdMap.TryGetValue(pane, out string? id))
            {
                // agterm's pane identity: an upper-case UUID, opaque to the hooks that pass it back.
                id = Guid.NewGuid().Wire();
                session.PaneIdMap[pane] = id;
            }

            return id;
        }

        /// <summary>The pane a pane id names in the session now, or null when no pane of it has that id.</summary>
        public static PaneRole? PaneOfId(Session session, string paneId)
        {
            foreach ((PaneRole pane, string id) in session.PaneIdMap)
            {
                if (id == paneId)
                {
                    return pane;
                }
            }

            return null;
        }

        /// <summary>Makes a new session a command session, before its main pane starts.</summary>
        public void SetCommand(Guid sessionId, string command, bool wait)
        {
            Session session = RequireSession(sessionId);
            session.Command = command;
            session.CommandWait = wait;
            RaiseStructureChanged();
        }


        /// <summary>agterm's session context; null clears it. The caller has checked the text.</summary>
        public void SetContext(Guid sessionId, string? context)
        {
            Session session = RequireSession(sessionId);
            if (session.Context == context)
            {
                return;
            }

            // The tree moves only when what the row shows changed: a mirrored context covers this one. Saving is due either way.
            string? shown = session.EffectiveContext;
            session.Context = context;
            if (shown != session.EffectiveContext)
            {
                TreeChanged?.Invoke();
            }

            StructureChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>
        /// agterm's clearCapturedForegroundCommands: forgets every captured command, saved and still armed for this launch,
        /// so a clear that comes before a pane starts still holds; pins stay.
        /// </summary>
        public void ClearRestoreCommands()
        {
            foreach (Session session in AllSessions)
            {
                session.RestoreCommand = null;
                session.SplitRestoreCommand = null;
                session.PendingRestoreCommand = null;
                session.PendingSplitRestoreCommand = null;
            }

            RaiseStructureChanged();
        }

        public void RenameSession(Guid sessionId, string? name)
        {
            Session session = RequireSession(sessionId);
            string? renamed = TerminalText.Name(name);
            // agterm's renameSession: the same name changes nothing, so nothing is reported or saved.
            if (session.CustomName == renamed)
            {
                return;
            }

            session.CustomName = renamed;
            RaiseStructureChanged();
        }

        /// <summary>Moves a session to <paramref name="index"/> in the target workspace, counted after the session is taken out of its current place.</summary>
        public void MoveSession(Guid sessionId, Guid targetWorkspaceId, int index)
        {
            Session session = RequireSession(sessionId);
            Workspace target = RequireWorkspace(targetWorkspaceId);
            Workspace source = FindWorkspaceOf(sessionId)!;
            int sourceIndex = source.SessionList.IndexOf(session);

            source.SessionList.Remove(session);
            int clamped = Math.Clamp(index, 0, target.SessionList.Count);
            target.SessionList.Insert(clamped, session);
            bool moved = source != target || clamped != sourceIndex;
            // agterm: the selected session moved out of the marked set takes the filter off, keeping the set.
            if (SelectedSessionId == sessionId)
            {
                DisableFocusIfSelectionOutside();
            }

            // agterm's moveSession: tree.changed only when the order changed.
            if (moved)
            {
                RaiseStructureChanged();
            }
            else
            {
                RaiseSaved();
            }
        }

        /// <summary>
        /// Removes a session. When it was selected, the most recently used session of the same workspace takes its place,
        /// then the most recent one anywhere, and only without any history the neighbouring row.
        /// </summary>
        public void RemoveSession(Guid sessionId)
        {
            RemoveSession(sessionId, final: true);
        }

        private void RemoveSession(Guid sessionId, bool final)
        {
            Session session = RequireSession(sessionId);
            Workspace workspace = FindWorkspaceOf(sessionId)!;
            int index = workspace.SessionList.IndexOf(session);
            if (final)
            {
                RecordRecentSession(session, workspace, workspaces.IndexOf(workspace), index);
            }

            workspace.SessionList.RemoveAt(index);
            recency.Remove(sessionId);
            DropFromDashboard(new[] { sessionId });
            CancelAsksOf(sessionId);
            if (final)
            {
                AppendClosed(workspace.Id, session);
            }

            if (SelectedSessionId == sessionId)
            {
                SetSelection(CloseReselectionTarget(workspaces.IndexOf(workspace), index));
                DisableFocusIfSelectionOutside();
            }

            RaiseRemoved(final);
        }

        /// <summary>
        /// Selects a session. As agterm's, only the user's own pick (<paramref name="reveal"/>) brings up the pane whose
        /// status needs attention; a control request, a follow or a notification selects without touching the panes.
        /// </summary>
        public void SelectSession(Guid sessionId, bool reveal = false)
        {
            Session session = RequireSession(sessionId);
            // agterm's captured indicator: a status that clears on visit still names the pane to bring up.
            StatusOwner owner = StatusOwner.Of(session);
            if (SelectedSessionId == sessionId)
            {
                Visit(sessionId);
                if (reveal)
                {
                    RevealStatusPane(session, owner);
                    Changed?.Invoke();
                }

                return;
            }

            SetSelection(sessionId);
            DisableFocusIfSelectionOutside();
            if (reveal)
            {
                RevealStatusPane(session, owner);
            }

            SelectionChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>Selects the session <paramref name="offset"/> positions away in sidebar order, wrapping around.</summary>
        public void SelectRelative(int offset, bool reveal = false)
        {
            List<Session> sessions = NavigableSessions.ToList();
            if (sessions.Count == 0)
            {
                return;
            }

            int current = SelectedSessionId is Guid id ? sessions.FindIndex(session => session.Id == id) : -1;
            int next = current < 0 ? 0 : ((current + offset) % sessions.Count + sessions.Count) % sessions.Count;
            SelectSession(sessions[next].Id, reveal);
        }

        /// <summary>
        /// Sets a session's agent status, recording the pane that set it. Every write restamps the time, repeated values included.
        /// As in agterm, while one pane is blocked another pane may only write blocked, so an agent reporting progress in one
        /// pane cannot erase the other's request for input; <paramref name="force"/> is for the user's own Clear Status.
        /// Returns false when the write was refused.
        /// </summary>
        public bool SetStatus(Guid sessionId, AgentStatus status, bool blink = false, bool autoReset = false, PaneRole pane = PaneRole.Main, bool force = false, string? color = null, string? shape = null, bool paneNamed = false)
        {
            Session session = RequireSession(sessionId);
            PaneRole writer = pane == PaneRole.Split && !session.HasSplit ? PaneRole.Main : pane;
            if (!force && session.Status == AgentStatus.Blocked && status != AgentStatus.Blocked && session.StatusPane != writer)
            {
                return false;
            }

            // A write here makes the status this machine's; the bridge marks its own again after it writes.
            session.RemoteStatusBridged = false;
            session.RemoteStatusUnowned = false;
            AgentStatus previous = session.Status;
            bool changed = previous != status || session.IndicatorBlink != blink || session.StatusAutoReset != autoReset || session.StatusPane != writer
                || session.IndicatorColor != color || session.IndicatorShape != shape || session.StatusPaneNamed != paneNamed;
            session.StatusPane = writer;
            session.StatusPaneNamed = paneNamed;
            session.Status = status;
            ResetAutoFollow(session.Id, previous, status);
            session.IndicatorBlink = blink;
            session.StatusAutoReset = autoReset;
            session.IndicatorColor = color;
            session.IndicatorShape = shape;
            session.StatusChangedAt = DateTimeOffset.UtcNow;
            if (changed)
            {
                Events.Append(ControlEventKinds.Status, FindWorkspaceOf(sessionId)?.Id, sessionId, new ControlEventPayload
                {
                    Name = session.DisplayName,
                    Status = AgentStatuses.Name(status),
                    Previous = AgentStatuses.Name(previous),
                    // agterm's indicator: blink as it stands, false included, and the pane only when the write named one.
                    Blink = session.IndicatorBlink,
                    Pane = paneNamed ? Panes.Name(writer) : null,
                    Color = session.IndicatorColor,
                    Shape = session.IndicatorShape,
                });
            }

            Changed?.Invoke();
            return true;
        }

        /// <summary>Clears the status when the user's keystroke in the pane that set it answers it.</summary>
        public void ClearStatusByInput(Guid sessionId, StatusKeystroke keystroke, PaneRole pane = PaneRole.Main)
        {
            Session? session = FindSession(sessionId);
            if (session is not null && !session.RemoteStatusUnowned && session.StatusPane == pane && AgentStatuses.ClearedBy(session.Status, keystroke, StatusReset))
            {
                SetStatus(sessionId, AgentStatus.Idle, pane: pane, force: true);
            }
        }

        /// <summary>A status set by a pane that is gone goes with it.</summary>
        private void ClearStatusOwnedBy(Session session, PaneRole pane)
        {
            if (session.Status != AgentStatus.Idle && session.StatusPane == pane)
            {
                SetStatus(session.Id, AgentStatus.Idle, force: true);
            }
        }

        /// <summary>
        /// agterm's revealActiveBlockedPane: selecting a session whose agent needs attention brings up the pane that asked.
        /// A shown scratch gives way to a block in another pane, a block in the right pane focuses it and one in the left
        /// pane focuses that; a status from a pane with no counterpart here names nothing to bring up.
        /// </summary>
        private void RevealStatusPane(Session session, StatusOwner owner)
        {
            if (!AgentStatuses.NeedsAttention(owner.Status) || owner.Unowned)
            {
                return;
            }

            if (owner.Pane != PaneRole.Scratch && session.ScratchShown)
            {
                session.ScratchShown = false;
            }

            if (owner.Pane == PaneRole.Split && session.HasSplit)
            {
                session.SplitShown = true;
                session.SplitFocused = true;
            }
            else if (owner.Pane == PaneRole.Scratch)
            {
                session.ScratchShown = true;
            }
            else
            {
                session.SplitFocused = false;
            }
        }

        /// <summary>agterm's reveal on the session already selected, for the next-attention step that finds only it.</summary>
        public void RevealStatus(Guid sessionId)
        {
            Session session = RequireSession(sessionId);
            RevealStatusPane(session, StatusOwner.Of(session));
            Changed?.Invoke();
        }

        /// <summary>The status a session had and the pane that set it, taken before a visit can clear it.</summary>
        private readonly record struct StatusOwner(AgentStatus Status, PaneRole Pane, bool Unowned)
        {
            public static StatusOwner Of(Session session) => new StatusOwner(session.Status, session.StatusPane, session.RemoteStatusUnowned);
        }

        /// <summary>Gives an idle session a completed status that stays when visited, to come back to it later.</summary>
        public void MarkForAttention(Guid sessionId)
        {
            if (RequireSession(sessionId).Status == AgentStatus.Idle)
            {
                SetStatus(sessionId, AgentStatus.Completed, force: true);
            }
        }

        /// <summary>Counts a notification against the session until it is visited.</summary>
        public void RecordNotification(Guid sessionId, string title, string body, string source)
        {
            Session session = RequireSession(sessionId);
            session.UnseenCount++;
            Events.Append(ControlEventKinds.Notify, FindWorkspaceOf(sessionId)?.Id, sessionId, new ControlEventPayload { Name = session.DisplayName, Title = title, Body = body, Source = source });
            Changed?.Invoke();
        }

        /// <summary>agterm's seen: a session visited or one of its panes focused; its delivered banners go with its badge.</summary>
        public event Action<Guid>? Seen;

        /// <summary>
        /// Zeroes a session's notification count. A visit also takes its delivered banners away; agshellctl session seen, as
        /// agterm's clearUnseen, leaves them.
        /// </summary>
        public void ClearUnseen(Guid sessionId, bool clearDelivered = true)
        {
            Session? session = FindSession(sessionId);
            if (session is null)
            {
                return;
            }

            if (session.UnseenCount > 0)
            {
                session.UnseenCount = 0;
                Changed?.Invoke();
            }

            if (clearDelivered)
            {
                Seen?.Invoke(sessionId);
            }
        }

        /// <summary>
        /// The next session, in sidebar order and wrapping, that is blocked or completed. The selected session is skipped,
        /// so repeated steps cycle through the others.
        /// </summary>
        public Guid? FindAttention(bool forward)
        {
            List<Session> sessions = NavigableSessions.ToList();
            int count = sessions.Count;
            int current = SelectedSessionId is Guid id ? sessions.FindIndex(session => session.Id == id) : -1;
            int start = current >= 0 ? current : forward ? -1 : count;
            for (int step = 1; step <= count; step++)
            {
                int index = ((start + (forward ? step : -step)) % count + count) % count;
                if (index == current)
                {
                    break;
                }

                if (AgentStatuses.NeedsAttention(sessions[index].Status))
                {
                    return sessions[index].Id;
                }
            }

            return null;
        }

        /// <summary>agterm's font keys: a step of <paramref name="delta"/> points within the limits, or the base size when null.</summary>
        public static double SteppedFontSize(double current, double? delta, double baseSize)
        {
            return delta is double step ? Math.Clamp(current + step, MinFontSize, MaxFontSize) : baseSize;
        }

        /// <summary>Changes one pane's font size by <paramref name="delta"/> points, or back to the default when null.</summary>
        public void ChangeFontSize(Guid sessionId, double? delta, PaneRole pane = PaneRole.Main)
        {
            Session session = RequireSession(sessionId);
            double current = (session.FontSizeMap.TryGetValue(pane, out double own) ? own : session.SessionFontSize) ?? BaseFontSize;
            double size = SteppedFontSize(current, delta, BaseFontSize);
            if (current == size && session.SessionFontSize == (size == BaseFontSize ? null : size))
            {
                return;
            }

            // The other panes keep the size they show now: in agterm each surface has its own, and only new ones take
            // the session's.
            foreach (PaneRole open in new[] { PaneRole.Main, PaneRole.Split, PaneRole.Scratch })
            {
                bool exists = open == PaneRole.Main || (open == PaneRole.Split && session.HasSplit) || (open == PaneRole.Scratch && session.ScratchShown);
                if (exists && !session.FontSizeMap.ContainsKey(open))
                {
                    session.FontSizeMap[open] = session.SessionFontSize ?? BaseFontSize;
                }
            }

            session.FontSizeMap[pane] = size;
            // agterm's reportFontSize: the session remembers the size, for the panes it opens next and the next launch.
            session.SessionFontSize = size == BaseFontSize ? null : size;
            // Panes at the default need no entry of their own: they read the session's, the default too.
            if (session.SessionFontSize is null)
            {
                foreach (PaneRole plain in session.FontSizeMap.Where(pair => pair.Value == BaseFontSize).Select(pair => pair.Key).ToList())
                {
                    session.FontSizeMap.Remove(plain);
                }
            }

            // Saved with a delay like the selection, so holding Ctrl+= does not write the file on every step.
            SelectionChanged?.Invoke();
            Changed?.Invoke();
        }

        public void UpdateCwd(Guid sessionId, string cwd, PaneRole pane = PaneRole.Main)
        {
            // agterm: a scratch or an overlay has no session of its own, so what it reports leaves the session's directories alone.
            Session? session = FindSession(sessionId);
            if (session is null || pane is not (PaneRole.Main or PaneRole.Split))
            {
                return;
            }

            // agterm: a directory a program reports flows into {AGSHELL_SESSION_PWD} and every spawn's directory.
            cwd = TerminalText.Sanitized(cwd);
            if (pane == PaneRole.Split)
            {
                if (session.SplitCwd == cwd)
                {
                    return;
                }

                session.SplitCwd = cwd;
            }
            else
            {
                if (session.Cwd == cwd)
                {
                    return;
                }

                session.Cwd = cwd;
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// Shows, hides or toggles the split, as agterm does: with an axis, the same shown axis hides, another axis transposes,
        /// and a hidden or absent split is shown in that arrangement. Hiding keeps the second shell running.
        /// </summary>
        public void UpdateTitle(Guid sessionId, string rawTitle, PaneRole pane = PaneRole.Main)
        {
            Session? session = FindSession(sessionId);
            if (session is null || (pane != PaneRole.Main && pane != PaneRole.Split))
            {
                return;
            }

            string? paneCwd = pane == PaneRole.Split ? session.SplitCwd : session.Cwd;
            Log.Debug("GhosttySurfaceView", "terminal title pane=" + (session.PaneIds.GetValueOrDefault(pane) ?? string.Empty) + " split=" + (pane == PaneRole.Split ? "true" : "false") + " cwd=" + (paneCwd ?? "<none>") + " title=" + rawTitle);
            string? title = DisplayNames.TerminalTitle(rawTitle, paneCwd);
            if (pane == PaneRole.Split)
            {
                if (session.SplitTitle == title)
                {
                    return;
                }

                session.SplitTitle = title;
            }
            else
            {
                if (session.Title == title)
                {
                    return;
                }

                session.Title = title;
            }

            LabelChanged?.Invoke(session);
        }

        public void SetSplit(Guid sessionId, bool? shown, SplitAxis? axis = null)
        {
            Session session = RequireSession(sessionId);
            bool show = shown ?? (axis is SplitAxis requested ? !session.SplitShown || session.SplitAxis != requested : !session.SplitShown);
            if (show && axis is SplitAxis chosen)
            {
                session.SplitAxis = chosen;
            }

            bool wasShown = session.SplitShown;
            session.SplitShown = show;
            if (show && !session.HasSplit)
            {
                // A new split opens next to the main pane, in its directory, and takes the focus.
                session.HasSplit = true;
                session.SplitCwd = session.Cwd;
                session.SplitFocused = true;
                // agterm: every split-creating path mints the split's identity before the pane shows.
                session.PaneIdMap[PaneRole.Split] = Guid.NewGuid().Wire();
            }

            if (wasShown != show)
            {
                Events.Append(ControlEventKinds.PaneSplit, FindWorkspaceOf(sessionId)?.Id, sessionId, new ControlEventPayload { Name = session.DisplayName, Status = show ? "shown" : "hidden" });
            }

            RaiseStructureChanged();
        }

        /// <summary>Removes the split; its shell is the caller's to stop.</summary>
        public void CloseSplit(Guid sessionId)
        {
            Session session = RequireSession(sessionId);
            if (!session.HasSplit)
            {
                return;
            }

            bool wasShown = session.SplitShown;
            DropDashboardPane(sessionId, PaneRole.Split);
            ClearStatusOwnedBy(session, PaneRole.Split);
            MoveAskPane(session, PaneRole.Split, pane => pane);
            // A page there ends dismissed, so a caller waiting on it hears so.
            ClosePendingPage(session, PaneRole.OverlayRight);
            if (session.OverlayMap.Remove(PaneRole.OverlayRight, out SessionOverlay? rightOverlay))
            {
                OverlayClosed?.Invoke(sessionId, rightOverlay.Id, PaneRole.OverlayRight);
            }

            EndOverlayJobsOf(sessionId, PaneRole.OverlayRight);
            // agterm drops the right pane's result with it: a new split starts with none.
            session.OverlayResults.Remove(PaneRole.OverlayRight);
            // Whatever pane of the other machine the split showed is off this row now; the main pane keeps its own.
            session.RemoteBinding.Remove(PaneRole.Split);
            session.RemotePaneIds.Remove(PaneRole.Split);
            CloseHudOver(session, PaneRole.Split);
            ResetSplit(session);
            if (wasShown)
            {
                Events.Append(ControlEventKinds.PaneSplit, FindWorkspaceOf(sessionId)?.Id, sessionId, new ControlEventPayload { Name = session.DisplayName, Status = "hidden" });
            }

            RaiseStructureChanged();
        }

        /// <summary>The main shell is gone and the split's shell becomes the session's only one, keeping its directory.</summary>
        public void PromoteSplit(Guid sessionId)
        {
            Session session = RequireSession(sessionId);
            session.Cwd = session.SplitCwd ?? session.Cwd;
            session.Title = session.SplitTitle;
            session.RestorePin = session.SplitRestorePin;
            session.PendingRestorePin = session.PendingSplitRestorePin;
            // agterm: the creation identity and the restore command follow the surviving pane, replacing the dead one's.
            session.Command = session.SplitCommand;
            session.CommandWait = session.SplitCommandWait;
            session.RestoreCommand = session.SplitRestoreCommand;
            MoveSlot(session.PaneBackgroundMap, PaneRole.Split, PaneRole.Main);
            MoveSlot(session.PaneIdMap, PaneRole.Split, PaneRole.Main);
            MoveSlot(session.LiveFailureMap, PaneRole.Split, PaneRole.Main);
            session.PaneIdMap.TryAdd(PaneRole.Main, Guid.NewGuid().Wire());
            MoveSlot(session.FontSizeMap, PaneRole.Split, PaneRole.Main);
            PromoteDashboardSplit(sessionId);
            // A mirrored status owned by no pane here belongs to the row, not to the pane that went.
            if (!session.RemoteStatusUnowned)
            {
                ClearStatusOwnedBy(session, PaneRole.Main);
            }

            MoveAskPane(session, PaneRole.Main, pane => pane == PaneRole.Split ? PaneRole.Main : pane);
            if (session.StatusPane == PaneRole.Split)
            {
                session.StatusPane = PaneRole.Main;
            }

            ClosePendingPage(session, PaneRole.OverlayLeft);
            session.OverlayMap.Remove(PaneRole.OverlayLeft);
            // One pane is left: it goes on showing whichever pane of the other machine it showed. The HUD over the pane that
            // went goes with it; one over the split follows it.
            session.RemoteBinding.Remove(PaneRole.Main);
            if (session.RemoteBinding.Remove(PaneRole.Split, out PaneRole shown))
            {
                session.RemoteBinding[PaneRole.Main] = shown;
            }

            session.RemotePaneIds.Remove(PaneRole.Main);
            if (session.RemotePaneIds.Remove(PaneRole.Split, out string? shownId))
            {
                session.RemotePaneIds[PaneRole.Main] = shownId;
            }

            CloseHudOver(session, PaneRole.Main);
            if (session.Overlay is { IsHud: true, HudPane: PaneRole.Split } splitHud)
            {
                session.OverlayMap[PaneRole.Overlay] = splitHud with { HudPane = PaneRole.Main };
            }
            if (session.OverlayMap.Remove(PaneRole.OverlayRight, out SessionOverlay? moved))
            {
                session.OverlayMap[PaneRole.OverlayLeft] = moved;
            }

            // agterm: the left slot takes the right's result, an empty one included, so the dead main pane's does not linger.
            session.OverlayResults.Remove(PaneRole.OverlayLeft);
            if (session.OverlayResults.Remove(PaneRole.OverlayRight, out OverlayResult? result))
            {
                session.OverlayResults[PaneRole.OverlayLeft] = result;
            }

            PromoteOverlayJobSlots(sessionId);

            ResetSplit(session);
            RaiseStructureChanged();
        }

        /// <summary>Exchanges the two shells' places; the focus follows its shell.</summary>
        public bool SwapPanes(Guid sessionId)
        {
            Session session = RequireSession(sessionId);
            // A remote row's panes change places here as a local layout, as agterm's: the shells keep their names there, and
            // a pane of that machine beside one of this machine's swaps too.
            if (!session.HasSplit)
            {
                return false;
            }

            // The binding goes with the shells in SwapCore: the panes stand for the other machine's the other way round.
            SwapCore(session);
            return true;
        }

        /// <summary>
        /// agterm: the other machine's layout rules its panes, so a swap of its two panes made here lasts until its next layout
        /// or snapshot, which puts them back in its order. True when the panes changed places.
        /// </summary>
        public bool RestoreRemoteOrder(Guid sessionId)
        {
            Session session = RequireSession(sessionId);
            if (!session.RemotePanesSwapped || !RemotePanePresent(session, PaneRole.Main) || !RemotePanePresent(session, PaneRole.Split))
            {
                return false;
            }

            SwapCore(session);
            return true;
        }

        private void SwapCore(Session session)
        {
            (session.Cwd, session.SplitCwd) = (session.SplitCwd ?? session.Cwd, session.Cwd);
            (session.Title, session.SplitTitle) = (session.SplitTitle, session.Title);
            (session.RestorePin, session.SplitRestorePin) = (session.SplitRestorePin, session.RestorePin);
            (session.RestoreCommand, session.SplitRestoreCommand) = (session.SplitRestoreCommand, session.RestoreCommand);
            (session.PendingRestorePin, session.PendingSplitRestorePin) = (session.PendingSplitRestorePin, session.PendingRestorePin);
            (session.PendingRestoreCommand, session.PendingSplitRestoreCommand) = (session.PendingSplitRestoreCommand, session.PendingRestoreCommand);
            (session.Command, session.SplitCommand) = (session.SplitCommand, session.Command);
            (session.CommandWait, session.SplitCommandWait) = (session.SplitCommandWait, session.CommandWait);
            MoveAskPane(session, null, pane => pane == PaneRole.Main ? PaneRole.Split : pane == PaneRole.Split ? PaneRole.Main : pane);
            SwapSlots(session.PaneBackgroundMap, PaneRole.Main, PaneRole.Split);
            SwapSlots(session.FontSizeMap, PaneRole.Main, PaneRole.Split);
            SwapSlots(session.PaneIdMap, PaneRole.Main, PaneRole.Split);
            SwapSlots(session.LiveFailureMap, PaneRole.Main, PaneRole.Split);
            session.SplitFocused = !session.SplitFocused;
            session.StatusPane = session.RemoteStatusUnowned ? session.StatusPane : session.StatusPane switch
            {
                PaneRole.Main => PaneRole.Split,
                PaneRole.Split => PaneRole.Main,
                PaneRole other => other,
            };
            // A pane's overlay, the HUD drawn over it and what it shows of another machine belong to its shell and move with it.
            SwapSlots(session.RemoteBinding, PaneRole.Main, PaneRole.Split);
            SwapSlots(session.RemotePaneIds, PaneRole.Main, PaneRole.Split);
            if (session.Overlay is { IsHud: true, HudPane: PaneRole anchor } hud)
            {
                session.OverlayMap[PaneRole.Overlay] = hud with { HudPane = anchor == PaneRole.Main ? PaneRole.Split : PaneRole.Main };
            }

            SwapSlots(session.OverlayMap, PaneRole.OverlayLeft, PaneRole.OverlayRight);
            SwapSlots(session.OverlayResults, PaneRole.OverlayLeft, PaneRole.OverlayRight);
            SwapOverlayJobSlots(session.Id);
            RaiseStructureChanged();
        }

        /// <summary>agterm closes a HUD with the pane it was drawn over.</summary>
        private void CloseHudOver(Session session, PaneRole pane)
        {
            if (session.Overlay is { IsHud: true, HudPane: PaneRole anchor } hud && anchor == pane)
            {
                session.OverlayMap.Remove(PaneRole.Overlay);
                OverlayClosed?.Invoke(session.Id, hud.Id, PaneRole.Overlay);
            }
        }

        public void SetSplitRatio(Guid sessionId, double ratio)
        {
            Session session = RequireSession(sessionId);
            session.SplitRatio = Math.Clamp(ratio, Panes.MinSplitRatio, Panes.MaxSplitRatio);
            RaiseStructureChanged();
        }

        /// <summary>Focuses a pane of a shown split; the scratch is focused by showing it.</summary>
        public void FocusPane(Guid sessionId, PaneRole pane)
        {
            Session session = RequireSession(sessionId);
            // agterm: focusing a pane means the session was seen.
            ClearUnseen(sessionId);
            bool splitFocused = pane == PaneRole.Split;
            // agterm's setSplitFocus: a hidden split takes it too, and the focused pane is the one shown maximized.
            if (pane == PaneRole.Scratch || !session.HasSplit || session.SplitFocused == splitFocused)
            {
                return;
            }

            session.SplitFocused = splitFocused;
            SelectionChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <param name="emit">False when a visible scratch is replaced: it ends shown as it began, so pane.scratch stays quiet.</param>
        public void SetScratch(Guid sessionId, bool? shown, bool emit = true)
        {
            Session session = RequireSession(sessionId);
            bool show = shown ?? !session.ScratchShown;
            if (session.ScratchShown == show)
            {
                return;
            }

            session.ScratchShown = show;
            if (emit)
            {
                Events.Append(ControlEventKinds.PaneScratch, FindWorkspaceOf(sessionId)?.Id, sessionId, new ControlEventPayload { Name = session.DisplayName, Status = show ? "shown" : "hidden" });
            }

            Changed?.Invoke();
        }

        /// <summary>The scratch's next start runs this once instead of a shell.</summary>
        public void SetScratchCommand(Guid sessionId, string command)
        {
            RequireSession(sessionId).ScratchCommand = command;
        }

        /// <summary>The run-once scratch command, taken by the start that runs it.</summary>
        public string? TakeScratchCommand(Guid sessionId)
        {
            if (FindSession(sessionId) is not Session session)
            {
                return null;
            }

            string? command = session.ScratchCommand;
            session.ScratchCommand = null;
            return command;
        }

        /// <summary>The scratch shell ended: the scratch is hidden and a status it set is cleared.</summary>
        public void ScratchClosed(Guid sessionId, bool emit = true)
        {
            Session? session = FindSession(sessionId);
            if (session is null)
            {
                return;
            }

            ClearStatusOwnedBy(session, PaneRole.Scratch);
            session.PaneBackgroundMap.Remove(PaneRole.Scratch);
            session.FontSizeMap.Remove(PaneRole.Scratch);
            session.PaneIdMap.Remove(PaneRole.Scratch);
            SetScratch(sessionId, false, emit);
        }

        /// <summary>
        /// agterm's TerminalZoomController.target: the session surface that fills the window, fixed when the zoom starts, so
        /// neither a new selection nor a new overlay or focus changes what is zoomed. Null when nothing is.
        /// </summary>
        public (Guid Session, PaneRole Pane)? ZoomTarget { get; private set; }

        public bool Zoomed => ZoomTarget is not null;

        /// <summary>
        /// agterm's TerminalZoomController.set for one surface: on zooms it, a background session's too, without selecting
        /// it; off leaves only if that very surface is zoomed; toggle swaps between it and no zoom.
        /// </summary>
        public void ZoomSurface(Guid sessionId, PaneRole pane, bool? zoomed)
        {
            RequireSession(sessionId);
            bool current = ZoomTarget == (sessionId, pane);
            bool value = zoomed ?? !current;
            if (!value)
            {
                if (current)
                {
                    SetZoomTarget(null);
                }

                return;
            }

            SetZoomTarget((sessionId, pane));
        }

        /// <summary>
        /// agterm's toggle from the GUI: leaves a zoom that is up, else zooms the selected session's active surface; with
        /// no active surface, as under a page, nothing happens.
        /// </summary>
        public void SetZoom(bool? zoomed)
        {
            bool value = zoomed ?? !Zoomed;
            if (!value)
            {
                SetZoomTarget(null);
            }
            else if (!Zoomed && SelectedSessionId is Guid selected && FindSession(selected) is Session session && ActiveSurface(session) is PaneRole active)
            {
                SetZoomTarget((selected, active));
            }
        }

        private void SetZoomTarget((Guid Session, PaneRole Pane)? target)
        {
            if (ZoomTarget != target)
            {
                ZoomTarget = target;
                // Zoom and the dashboard both take over the window, so one ends the other.
                if (target is not null)
                {
                    Dashboard = null;
                }

                Changed?.Invoke();
            }
        }

        /// <summary>
        /// agterm's TerminalZoomSurface.isActive: the one surface the user works in, the program overlay, else a shown
        /// scratch, else the focused pane's own program overlay, else that pane. A page is no terminal, so under one none is.
        /// </summary>
        public static PaneRole? ActiveSurface(Session session)
        {
            bool coverVisible = session.Overlay is { IsHud: false, Hidden: false };
            if (session.Overlay is { IsHud: false, Page: null, Hidden: false })
            {
                return PaneRole.Overlay;
            }

            if (coverVisible)
            {
                return null;
            }

            if (session.ScratchShown)
            {
                return PaneRole.Scratch;
            }

            PaneRole focused = session.HasSplit && session.SplitFocused ? PaneRole.Split : PaneRole.Main;
            if (!session.Overlays.TryGetValue(Panes.OverlayOf(focused), out SessionOverlay? own) || own.Hidden)
            {
                return focused;
            }

            return own.Page is null ? Panes.OverlayOf(focused) : null;
        }

        public DashboardState? Dashboard { get; private set; }

        /// <summary>Opens the dashboard on the given cells, at most nine, ending zoom.</summary>
        public void OpenDashboard(IReadOnlyList<DashboardCell> cells, double? fontSize, bool autoSize)
        {
            Dashboard = new DashboardState { Cells = cells.Take(DashboardLayout.MaxCells).ToList(), FontSize = fontSize, AutoSize = autoSize };
            ZoomTarget = null;
            Changed?.Invoke();
        }

        public void CloseDashboard()
        {
            if (Dashboard is not null)
            {
                Dashboard = null;
                Changed?.Invoke();
            }
        }

        public void MoveDashboardHighlight(string direction)
        {
            if (Dashboard is not DashboardState dashboard)
            {
                return;
            }

            (int cols, _) = DashboardLayout.Grid(dashboard.Cells.Count);
            Dashboard = dashboard with { Highlight = DashboardLayout.Move(dashboard.Highlight, direction, cols, dashboard.Cells.Count) };
            Changed?.Invoke();
        }

        /// <summary>
        /// agterm's clickDashboardMember, first half: the clicked cell takes the highlight; the caller enters it after the
        /// flash with <see cref="EnterDashboardCellIfHighlighted"/>. Returns that cell.
        /// </summary>
        public DashboardCell? HighlightDashboardCell(int index)
        {
            if (Dashboard is not DashboardState dashboard || index < 0 || index >= dashboard.Cells.Count)
            {
                return null;
            }

            if (dashboard.Highlight != index)
            {
                Dashboard = dashboard with { Highlight = index };
                Changed?.Invoke();
            }

            return dashboard.Cells[index];
        }

        /// <summary>
        /// agterm's delayed enter: only while the dashboard is open and that very cell still holds the highlight, so a later
        /// click or arrow wins and a close in the gap cancels it.
        /// </summary>
        public void EnterDashboardCellIfHighlighted(DashboardCell cell)
        {
            if (Dashboard is DashboardState dashboard && dashboard.Highlight < dashboard.Cells.Count && dashboard.Cells[dashboard.Highlight] == cell)
            {
                ActivateDashboardCell(dashboard.Highlight);
            }
        }

        /// <summary>Jumps into a cell: selects its session, focuses its pane, and closes the dashboard.</summary>
        public void ActivateDashboardCell(int index)
        {
            if (Dashboard is not DashboardState dashboard || index < 0 || index >= dashboard.Cells.Count)
            {
                return;
            }

            DashboardCell cell = dashboard.Cells[index];
            Dashboard = null;
            if (FindSession(cell.SessionId) is not null)
            {
                SelectSession(cell.SessionId);
                FocusPane(cell.SessionId, cell.Pane);
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// The cells for the given targets: a bare session takes all its panes, a pane suffix only that one.
        /// Returns the cells, deduplicated and capped at nine, and the targets that could not be placed.
        /// </summary>
        /// <summary>
        /// agterm's dashboardMembers: the cells the resolved targets expand to, a session with a split giving both panes,
        /// duplicates once, and how many panes went past the nine-cell limit.
        /// </summary>
        public (List<DashboardCell> Cells, int Dropped) DashboardCells(IEnumerable<(Session Session, PaneRole? Pane)> targets)
        {
            // agterm: every target expands with duplicates dropped first, then the cap cuts, so a repeat past it counts once.
            List<DashboardCell> expanded = new List<DashboardCell>();
            foreach ((Session session, PaneRole? pane) in targets)
            {
                IEnumerable<PaneRole> panes = pane is PaneRole one
                    ? new[] { one }
                    : session.HasSplit ? new[] { PaneRole.Main, PaneRole.Split } : new[] { PaneRole.Main };
                foreach (PaneRole role in panes)
                {
                    DashboardCell cell = new DashboardCell(session.Id, role);
                    if (!expanded.Contains(cell))
                    {
                        expanded.Add(cell);
                    }
                }
            }

            List<DashboardCell> cells = expanded.Take(DashboardLayout.MaxCells).ToList();
            return (cells, expanded.Count - cells.Count);
        }

        /// <summary>
        /// Puts a program or HUD in an overlay slot: over the whole session (<see cref="PaneRole.Overlay"/>) or over one pane of a
        /// split (<see cref="PaneRole.OverlayLeft"/>, <see cref="PaneRole.OverlayRight"/>), replacing what was there.
        /// </summary>
        public SessionOverlay OpenOverlay(Guid sessionId, SessionOverlay overlay, PaneRole slot = PaneRole.Overlay)
        {
            Session session = RequireSession(sessionId);
            ClosePendingPage(session, slot);
            session.OverlayMap[slot] = overlay;
            if (!overlay.IsHud)
            {
                session.OverlayResults[slot] = new OverlayResult(overlay.Id, null) { IsPage = overlay.Page is not null };
                if (overlay.Page is not null)
                {
                    KeepPageOutcome(session.OverlayResults[slot]);
                }
            }
            else if (session.OverlayResults.GetValueOrDefault(slot) is OverlayResult prior && !(prior.Remote && prior.ExitCode is null && prior.Failure is null))
            {
                // agterm: a HUD clears the slot's earlier result; a job still running elsewhere reports into it later.
                session.OverlayResults.Remove(slot);
            }

            TreeChanged?.Invoke();
            Changed?.Invoke();
            return overlay;
        }

        /// <summary>Changes an open overlay in place, keeping its program running.</summary>
        public void UpdateOverlay(Guid sessionId, Func<SessionOverlay, SessionOverlay> change, PaneRole slot = PaneRole.Overlay)
        {
            Session session = RequireSession(sessionId);
            if (session.OverlayMap.TryGetValue(slot, out SessionOverlay? current))
            {
                session.OverlayMap[slot] = change(current);
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// The overlay program exited: the overlay goes away, or stays with the exit shown when it was opened with wait.
        /// Ignored when <paramref name="overlayId"/> is no longer the slot's overlay.
        /// </summary>
        public void OverlayExited(Guid sessionId, string overlayId, int exitCode, PaneRole slot = PaneRole.Overlay)
        {
            Session? session = FindSession(sessionId);
            if (session is null || !session.OverlayResults.TryGetValue(slot, out OverlayResult? result) || result.Id != overlayId)
            {
                return;
            }

            session.OverlayResults[slot] = result with { ExitCode = exitCode };
            if (session.OverlayMap.TryGetValue(slot, out SessionOverlay? overlay) && overlay.Id == overlayId)
            {
                if (overlay.Wait)
                {
                    session.OverlayMap[slot] = overlay with { ExitCode = exitCode };
                }
                else
                {
                    session.OverlayMap.Remove(slot);
                    OverlayClosed?.Invoke(sessionId, overlayId, slot);
                }
            }

            Changed?.Invoke();
        }

        /// <summary>Takes an overlay down; when <paramref name="overlayId"/> is given, only if it is still that one.</summary>
        public void CloseOverlay(Guid sessionId, string? overlayId = null, PaneRole slot = PaneRole.Overlay)
        {
            Session? session = FindSessionOrPending(sessionId);
            if (session is null || !session.OverlayMap.TryGetValue(slot, out SessionOverlay? overlay) || (overlayId is not null && overlay.Id != overlayId))
            {
                return;
            }

            ClosePendingPage(session, slot);
            session.OverlayMap.Remove(slot);
            OverlayClosed?.Invoke(sessionId, overlay.Id, slot);
            TreeChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>
        /// agterm's dropUnrealizedPaneOverlays: a pane's program overlay whose terminal never started goes away outright, exit
        /// code and all, once its pane is no longer laid out (the split hidden, focus moved on a hidden split, the zoom
        /// changed), instead of starting whenever the pane comes back. A page, one that ran
        /// and the one the surface zoom shows are kept. True when one went.
        /// </summary>
        public bool DropUnrealizedPaneOverlays(Guid sessionId, Func<PaneRole, bool> started)
        {
            Session? session = FindSession(sessionId);
            if (session is null)
            {
                return false;
            }

            bool dropped = false;
            foreach ((PaneRole slot, PaneRole pane) in new[] { (PaneRole.OverlayLeft, PaneRole.Main), (PaneRole.OverlayRight, PaneRole.Split) })
            {
                bool hosted = ZoomTarget == (sessionId, slot);
                if (session.OverlayMap.TryGetValue(slot, out SessionOverlay? overlay) && overlay is { IsProgram: true, ExitCode: null }
                    && !session.RendersPane(pane) && !hosted && !started(slot))
                {
                    session.OverlayResults.Remove(slot);
                    CloseOverlay(sessionId, overlay.Id, slot);
                    dropped = true;
                }
            }

            return dropped;
        }

        /// <summary>An overlay left its slot: closed, or its program ended without --wait.</summary>
        public event Action<Guid, string, PaneRole>? OverlayClosed;

        /// <summary>A page hands a value back to the script that opened it, and goes away.</summary>
        public bool SubmitPage(Guid sessionId, PaneRole slot, string value)
        {
            Session? session = FindSession(sessionId);
            if (session is null || !session.OverlayMap.TryGetValue(slot, out SessionOverlay? overlay) || overlay.Page is null
                || !session.OverlayResults.TryGetValue(slot, out OverlayResult? result) || result.Id != overlay.Id)
            {
                return false;
            }

            session.OverlayResults[slot] = result with { PageState = "submitted", PageValue = value };
            KeepPageOutcome(session.OverlayResults[slot]);
            session.OverlayMap.Remove(slot);
            Changed?.Invoke();
            return true;
        }

        /// <summary>A page that goes away unanswered counts as dismissed.</summary>
        private void ClosePendingPage(Session session, PaneRole slot)
        {
            if (session.OverlayMap.TryGetValue(slot, out SessionOverlay? overlay) && overlay.Page is not null
                && session.OverlayResults.TryGetValue(slot, out OverlayResult? result) && result.Id == overlay.Id && result.PageState == "pending")
            {
                session.OverlayResults[slot] = result with { PageState = "dismissed" };
                KeepPageOutcome(session.OverlayResults[slot]);
            }
        }

        /// <summary>Hands a page's state to the app-wide record overlay result --page ID reads.</summary>
        private static void KeepPageOutcome(OverlayResult outcome)
        {
            if (outcome.PageState == "pending")
            {
                PageOutcomes.Register(outcome.Id);
            }
            else
            {
                PageOutcomes.Finish(outcome.Id, outcome.PageState, outcome.PageValue);
            }
        }

        /// <summary>Takes down the overlay with this id in whichever slot it is, for timers that only know the id.</summary>
        public void CloseOverlayById(Guid sessionId, string overlayId)
        {
            Session? session = FindSessionOrPending(sessionId);
            PaneRole? slot = session?.Overlays.FirstOrDefault(pair => pair.Value.Id == overlayId).Key;
            if (session is not null && slot is PaneRole role && session.Overlays.ContainsKey(role))
            {
                CloseOverlay(sessionId, overlayId, role);
            }
        }

        private void DropDashboardPane(Guid sessionId, PaneRole pane)
        {
            if (Dashboard is DashboardState dashboard && dashboard.Cells.Contains(new DashboardCell(sessionId, pane)))
            {
                KeepDashboardCells(dashboard, dashboard.Cells.Where(cell => cell != new DashboardCell(sessionId, pane)).ToList());
            }
        }

        /// <summary>
        /// agterm's reconcile: the survivors stay in order, the highlight stays on its cell or, when that one went, moves to
        /// the first; with none left the dashboard closes.
        /// </summary>
        private void KeepDashboardCells(DashboardState dashboard, List<DashboardCell> cells)
        {
            if (cells.Count == 0)
            {
                Dashboard = null;
                return;
            }

            DashboardCell? highlighted = dashboard.Highlight >= 0 && dashboard.Highlight < dashboard.Cells.Count ? dashboard.Cells[dashboard.Highlight] : null;
            int highlight = highlighted is null ? 0 : Math.Max(cells.IndexOf(highlighted), 0);
            Dashboard = dashboard with { Cells = cells, Highlight = highlight };
        }

        /// <summary>
        /// agterm's promoteSplitMember: the split's shell moved into the main slot, so its cell follows it there, folding into
        /// a main cell already on the grid, and the highlight goes with it.
        /// </summary>
        private void PromoteDashboardSplit(Guid sessionId)
        {
            DashboardCell promoted = new DashboardCell(sessionId, PaneRole.Split);
            DashboardCell destination = new DashboardCell(sessionId, PaneRole.Main);
            if (Dashboard is not DashboardState dashboard)
            {
                return;
            }

            int index = dashboard.Cells.ToList().IndexOf(promoted);
            if (index < 0)
            {
                return;
            }

            List<DashboardCell> cells = dashboard.Cells.ToList();
            bool highlighted = dashboard.Highlight == index;
            if (cells.Contains(destination))
            {
                cells.RemoveAt(index);
            }
            else
            {
                cells[index] = destination;
            }

            int highlight = highlighted ? cells.IndexOf(destination) : cells.IndexOf(dashboard.Cells[Math.Clamp(dashboard.Highlight, 0, dashboard.Cells.Count - 1)]);
            Dashboard = dashboard with { Cells = cells, Highlight = Math.Max(highlight, 0) };
        }

        /// <summary>Cells of sessions that are gone leave the dashboard; an empty dashboard closes.</summary>
        private void DropFromDashboard(IEnumerable<Guid> sessionIds)
        {
            if (Dashboard is not DashboardState dashboard)
            {
                return;
            }

            HashSet<Guid> gone = sessionIds.ToHashSet();
            List<DashboardCell> cells = dashboard.Cells.Where(cell => !gone.Contains(cell.SessionId)).ToList();
            if (cells.Count != dashboard.Cells.Count)
            {
                KeepDashboardCells(dashboard, cells);
            }
        }

        /// <summary>Moves what <paramref name="from"/> holds to <paramref name="to"/>, dropping what <paramref name="to"/> held.</summary>
        private static void MoveSlot<T>(Dictionary<PaneRole, T> map, PaneRole from, PaneRole to)
        {
            map.Remove(to);
            if (map.Remove(from, out T? value))
            {
                map[to] = value;
            }
        }

        private static Dictionary<string, ControlBackground>? PaneBackgroundsOf(Session session)
        {
            Dictionary<string, ControlBackground> kept = session.PaneBackgroundMap
                .Where(pair => pair.Key == PaneRole.Main || (pair.Key == PaneRole.Split && session.HasSplit))
                .ToDictionary(pair => Panes.Name(pair.Key), pair => pair.Value);
            return kept.Count == 0 ? null : kept;
        }

        private static void SwapSlots<T>(Dictionary<PaneRole, T> map, PaneRole first, PaneRole second)
        {
            bool hasFirst = map.Remove(first, out T? a);
            bool hasSecond = map.Remove(second, out T? b);
            if (hasFirst)
            {
                map[second] = a!;
            }

            if (hasSecond)
            {
                map[first] = b!;
            }
        }

        private static void ResetSplit(Session session)
        {
            session.HasSplit = false;
            session.SplitShown = false;
            session.SplitAxis = SplitAxis.Vertical;
            session.SplitRatio = null;
            session.SplitCwd = null;
            session.SplitTitle = null;
            session.SplitRestorePin = null;
            session.SplitRestoreCommand = null;
            session.PendingSplitRestorePin = null;
            session.PendingSplitRestoreCommand = null;
            session.SplitCommand = null;
            session.SplitCommandWait = false;
            session.PaneBackgroundMap.Remove(PaneRole.Split);
            session.PaneIdMap.Remove(PaneRole.Split);
            session.LiveFailureMap.Remove(PaneRole.Split);
            session.FontSizeMap.Remove(PaneRole.Split);
            session.SplitFocused = false;
        }

        public StateSnapshot ToSnapshot()
        {
            return new StateSnapshot
            {
                // A remote row is not saved: the selection falls to the most recent session of this machine, as agterm's.
                SelectedSessionId = SelectedSessionId is Guid chosen && FindSession(chosen)?.RemoteHost is not null
                    ? recency.Where(id => FindSession(id) is { RemoteHost: null }).Select(id => (Guid?)id).FirstOrDefault()
                    : SelectedSessionId,
                // Rows of another machine are not saved, so neither is their place in the recency.
                SessionRecency = recency.Where(id => FindSession(id) is not { RemoteHost: not null }).ToList(),
                SidebarMode = SidebarMode == FlaggedMode ? FlaggedMode : null,
                FocusedWorkspaceIds = focusMembers.Count == 0 ? null : FocusMembers.ToList(),
                FocusEnabled = FocusEnabled,
                Workspaces = workspaces.Select(workspace => new WorkspaceSnapshot
                {
                    Id = workspace.Id,
                    Name = workspace.Name,
                    Collapsed = workspace.Collapsed,
                    Sessions = workspace.SessionList.Where(session => session.RemoteHost is null).Select(SnapshotOf).ToList(),
                }).ToList(),
            };
        }

        public void SetLiveFailed(Guid sessionId, PaneRole pane, bool failed)
        {
            Session session = FindSessionOrPending(sessionId) ?? throw new InvalidOperationException("no such session");
            bool changed = failed ? session.LiveFailureMap.TryAdd(pane, true) : session.LiveFailureMap.Remove(pane);
            if (changed)
            {
                LiveFailureChanged?.Invoke();
                Changed?.Invoke();
            }
        }

        public StateSnapshot LiveFailureSnapshot()
        {
            StateSnapshot snapshot = ToSnapshot();
            foreach (PendingClose close in pendingCloses.AsEnumerable().Reverse())
            {
                if (close.Workspace is Workspace workspace && snapshot.Workspaces.All(saved => saved.Id != workspace.Id))
                {
                    snapshot.Workspaces.Insert(Math.Clamp(close.WorkspaceIndex, 0, snapshot.Workspaces.Count), new WorkspaceSnapshot
                    {
                        Id = workspace.Id,
                        Name = workspace.Name,
                        Collapsed = workspace.Collapsed,
                        Sessions = workspace.Sessions.Where(session => session.RemoteHost is null).Select(SnapshotOf).ToList(),
                    });
                }

                foreach (ClosedPlace place in close.Places.OrderBy(place => place.Index).Where(place => place.Session.RemoteHost is null))
                {
                    WorkspaceSnapshot? saved = snapshot.Workspaces.FirstOrDefault(workspace => workspace.Id == place.WorkspaceId);
                    if (saved is null)
                    {
                        saved = new WorkspaceSnapshot { Id = place.WorkspaceId, Name = place.WorkspaceName };
                        snapshot.Workspaces.Insert(Math.Clamp(place.WorkspaceIndex, 0, snapshot.Workspaces.Count), saved);
                    }

                    if (saved.Sessions.All(session => session.Id != place.Session.Id))
                    {
                        saved.Sessions.Insert(Math.Clamp(place.Index, 0, saved.Sessions.Count), SnapshotOf(place.Session));
                    }
                }
            }

            return snapshot;
        }

        private static SessionSnapshot SnapshotOf(Session session)
        {
            return new SessionSnapshot
            {
                Id = session.Id,
                ProfileId = session.ProfileId,
                Name = session.CustomName,
                Cwd = session.Cwd,
                FontSize = session.SessionFontSize,
                HasSplit = session.HasSplit,
                SplitShown = session.SplitShown,
                SplitAxis = session.HasSplit ? Panes.AxisName(session.SplitAxis) : null,
                SplitRatio = session.SplitRatio,
                SplitCwd = session.SplitCwd,
                Flagged = session.Flagged,
                RestoreCommand = session.RestoreCommand,
                SplitRestoreCommand = session.HasSplit ? session.SplitRestoreCommand : null,
                RestorePin = session.RestorePin,
                SplitRestorePin = session.HasSplit ? session.SplitRestorePin : null,
                Context = session.Context,
                Command = session.Command,
                CommandWait = session.Command is not null && session.CommandWait,
                SplitCommand = session.HasSplit ? session.SplitCommand : null,
                SplitCommandWait = session.HasSplit && session.SplitCommand is not null && session.SplitCommandWait,
                PaneIds = session.PaneIdMap.Where(pair => pair.Key == PaneRole.Main || (pair.Key == PaneRole.Split && session.HasSplit)).ToDictionary(pair => Panes.Name(pair.Key), pair => pair.Value) is { Count: > 0 } ids ? ids : null,
                LiveFailedPanes = session.LiveFailureMap.Keys.Where(pane => pane == PaneRole.Main || (pane == PaneRole.Split && session.HasSplit)).Select(Panes.Name).ToList() is { Count: > 0 } failed ? failed : null,
                Background = session.Background,
                PaneBackgrounds = PaneBackgroundsOf(session),
            };
        }

        /// <summary>
        /// agterm's session(from:launchRestore:): a saved capture is never loaded back into the session, only armed for
        /// this launch when the app's launch restores it, and the pin likewise; a window reopened later and Recent Closed
        /// run neither.
        /// </summary>
        private static Session SessionFrom(SessionSnapshot snapshot, bool launchRestore = false)
        {
            string profileId = string.IsNullOrWhiteSpace(snapshot.ProfileId) ? string.Empty : snapshot.ProfileId;
            Session session = new Session(snapshot.Id, profileId, snapshot.Cwd ?? DefaultCwd(profileId), snapshot.Name)
            {
                HasSplit = snapshot.HasSplit,
                SplitShown = snapshot.HasSplit && snapshot.SplitShown,
                SplitAxis = Panes.TryParseAxis(snapshot.SplitAxis, out SplitAxis axis) ? axis : SplitAxis.Vertical,
                SplitRatio = snapshot.HasSplit && snapshot.SplitRatio is double ratio ? Math.Clamp(ratio, Panes.MinSplitRatio, Panes.MaxSplitRatio) : null,
                SplitCwd = snapshot.HasSplit ? snapshot.SplitCwd : null,
                Flagged = snapshot.Flagged,
                RestorePin = snapshot.RestorePin,
                SplitRestorePin = snapshot.HasSplit ? snapshot.SplitRestorePin : null,
                WasRestored = true,
                PendingRestoreCommand = launchRestore ? snapshot.RestoreCommand : null,
                PendingSplitRestoreCommand = launchRestore && snapshot.HasSplit ? snapshot.SplitRestoreCommand : null,
                PendingRestorePin = launchRestore ? snapshot.RestorePin : null,
                PendingSplitRestorePin = launchRestore && snapshot.HasSplit ? snapshot.SplitRestorePin : null,
                // agterm's decoder validates the context too: a hand-edited one with a line break would break the title.
                Context = snapshot.Context is string context && Session.ContextError(context) is null ? context.Trim() : null,
                Command = snapshot.Command,
                CommandWait = snapshot.Command is not null && snapshot.CommandWait,
                SplitCommand = snapshot.HasSplit ? snapshot.SplitCommand : null,
                SplitCommandWait = snapshot.HasSplit && snapshot.SplitCommand is not null && snapshot.SplitCommandWait,
                Background = snapshot.Background,
            };
            foreach ((string name, string id) in snapshot.PaneIds ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrEmpty(id) && Panes.TryParse(name, out PaneRole pane) && (pane == PaneRole.Main || (pane == PaneRole.Split && session.HasSplit)))
                {
                    session.PaneIdMap[pane] = id;
                }
            }

            // agterm's session(from:): a saved session without its ids gets new ones.
            session.PaneIdMap.TryAdd(PaneRole.Main, Guid.NewGuid().Wire());
            if (session.HasSplit)
            {
                session.PaneIdMap.TryAdd(PaneRole.Split, Guid.NewGuid().Wire());
            }

            foreach (string name in snapshot.LiveFailedPanes ?? new List<string>())
            {
                if (Panes.TryParse(name, out PaneRole pane) && (pane == PaneRole.Main || (pane == PaneRole.Split && session.HasSplit)))
                {
                    session.LiveFailureMap[pane] = true;
                }
            }

            if (snapshot.FontSize is double size)
            {
                session.SessionFontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
            }

            // agterm keeps the main pane's size only; the right pane's and the scratch's last while the app runs.
            foreach ((string name, ControlBackground background) in snapshot.PaneBackgrounds ?? new Dictionary<string, ControlBackground>())
            {
                if (Panes.TryParse(name, out PaneRole pane) && (pane == PaneRole.Main || (pane == PaneRole.Split && session.HasSplit)))
                {
                    session.PaneBackgroundMap[pane] = background;
                }
            }

            return session;
        }

        /// <summary>Gives the sessions restored with no usable profile the default one, once the profiles are known.</summary>
        public void AdoptDefaultProfile(string profileId)
        {
            foreach (Session session in AllSessions.Where(session => session.ProfileId.Length == 0))
            {
                session.ProfileId = profileId;
            }
        }

        /// <summary>Builds a model from saved state, dropping entries with duplicate ids instead of failing.</summary>
        /// <param name="launchRestore">agterm's launchRestore: the app's launch is loading the window, the only load that arms anything to run.</param>
        public static AppModel FromSnapshot(StateSnapshot snapshot, bool launchRestore = false)
        {
            AppModel model = new AppModel();
            HashSet<Guid> seenSessions = new HashSet<Guid>();

            foreach (WorkspaceSnapshot workspaceSnapshot in snapshot.Workspaces)
            {
                // agterm's restore(from:): a repeated workspace id folds into the first, its sessions after those already there.
                Workspace? workspace = model.FindWorkspace(workspaceSnapshot.Id);
                if (workspace is null)
                {
                    workspace = new Workspace(workspaceSnapshot.Id, NormalizeName(workspaceSnapshot.Name, model.NextWorkspaceName))
                    {
                        Collapsed = workspaceSnapshot.Collapsed,
                    };
                    model.workspaces.Add(workspace);
                }

                foreach (SessionSnapshot sessionSnapshot in workspaceSnapshot.Sessions)
                {
                    if (!seenSessions.Add(sessionSnapshot.Id))
                    {
                        continue;
                    }

                    workspace.SessionList.Add(SessionFrom(sessionSnapshot, launchRestore));
                }
            }

            foreach (Guid id in snapshot.SessionRecency)
            {
                if (model.recency.Count < RecencyLimit && !model.recency.Contains(id) && model.FindSession(id) is not null)
                {
                    model.recency.Add(id);
                }
            }

            model.RestoreFocus(snapshot);
            // As agterm's: a saved selection that names no session is no selection, not the first one.
            model.SelectedSessionId = snapshot.SelectedSessionId is Guid selected && model.FindSession(selected) is not null ? selected : null;
            // The restored selection heads the Ctrl+Tab order, so the "previous session" is the one before it.
            if (model.SelectedSessionId is Guid current)
            {
                model.recency.Remove(current);
                model.recency.Insert(0, current);
                if (model.recency.Count > RecencyLimit)
                {
                    model.recency.RemoveAt(model.recency.Count - 1);
                }
            }

            // Last, once the history is back: a selection the filter or the flagged view hides moves into view.
            model.ReselectIfHidden();
            return model;
        }

        private void SetSelection(Guid? sessionId, bool clearPreviousAutoReset = true)
        {
            Guid? previous = SelectedSessionId;
            if (previous != sessionId)
            {
                freshWorkspaceId = null;
                SelectedSessionId = sessionId;
                if (clearPreviousAutoReset)
                {
                    ClearAutoReset(previous);
                }

                // agterm's reveal: the row of a newly selected session opens on screen, its saved fold untouched.
                if (sessionId is Guid shown && FindWorkspaceOf(shown) is { Collapsed: true } owner)
                {
                    revealed.Add(owner.Id);
                }

                CancelGuiAskOffSelection(sessionId);
                // agterm: every change of the selection, a cleared one included, with the session it left.
                Events.Append(ControlEventKinds.SessionSelected, sessionId is Guid selected ? FindWorkspaceOf(selected)?.Id : null, sessionId,
                    new ControlEventPayload { Name = sessionId is Guid named ? FindSession(named)?.DisplayName : null, Previous = previous.Wire() });
            }

            if (sessionId is Guid visited)
            {
                Visit(visited);
            }

            if (sessionId is Guid id)
            {
                recency.Remove(id);
                recency.Insert(0, id);
                if (recency.Count > RecencyLimit)
                {
                    recency.RemoveAt(recency.Count - 1);
                }
            }
        }

        /// <summary>Visiting a session marks its notifications seen and ends a status that was set to clear on visit.</summary>
        private void Visit(Guid sessionId)
        {
            Session? session = FindSession(sessionId);
            if (session is null)
            {
                return;
            }

            ClearAutoReset(sessionId);
            ClearUnseen(sessionId);
        }

        private void ClearAutoReset(Guid? sessionId)
        {
            if (sessionId is Guid id && FindSession(id) is Session session && session.StatusAutoReset)
            {
                SetStatus(id, AgentStatus.Idle, force: true);
            }
        }

        /// <summary>
        /// agterm's closeReselectionTarget, after the selected session left the tree from the given place: the most recent
        /// session among the shown ones of its workspace, else of all shown ones, else of the whole tree; without history,
        /// the nearest shown row while the view is narrowed, else the neighbouring row.
        /// </summary>
        private Guid? CloseReselectionTarget(int workspaceIndex, int sessionIndex)
        {
            HashSet<Guid> visible = NavigableSessions.Select(session => session.Id).ToHashSet();
            HashSet<Guid> same = workspaces[workspaceIndex].SessionList.Select(session => session.Id).Where(visible.Contains).ToHashSet();
            HashSet<Guid> scope = same.Count > 0 ? same : visible.Count > 0 ? visible : AllSessions.Select(session => session.Id).ToHashSet();
            if (recency.FirstOrDefault(scope.Contains) is Guid recent && recent != Guid.Empty)
            {
                return recent;
            }

            if ((SidebarMode == FlaggedMode || FilterApplies) && NearestInScope(workspaceIndex, sessionIndex, scope) is Guid inScope)
            {
                return inScope;
            }

            return PositionalTarget(workspaceIndex, sessionIndex);
        }

        /// <summary>agterm's nearestInScopeTarget: over the tree flattened in sidebar order, the in-scope row that took the removed one's place, else the one before.</summary>
        private Guid? NearestInScope(int workspaceIndex, int sessionIndex, HashSet<Guid> scope)
        {
            int before = workspaces.Take(workspaceIndex).Sum(workspace => workspace.SessionList.Count);
            int slot = before + Math.Min(sessionIndex, workspaces[workspaceIndex].SessionList.Count);
            List<Session> flattened = AllSessions.ToList();
            return flattened.Skip(slot).FirstOrDefault(session => scope.Contains(session.Id))?.Id
                ?? flattened.Take(slot).LastOrDefault(session => scope.Contains(session.Id))?.Id;
        }

        /// <summary>agterm's reselectionTarget: the row that took the removed one's place, else the one before, else the first anywhere.</summary>
        private Guid? PositionalTarget(int workspaceIndex, int sessionIndex)
        {
            List<Session> sessions = workspaces[workspaceIndex].SessionList;
            if (sessionIndex < sessions.Count)
            {
                return sessions[sessionIndex].Id;
            }

            if (sessionIndex > 0 && sessions.Count > 0)
            {
                return sessions[Math.Min(sessionIndex - 1, sessions.Count - 1)].Id;
            }

            return AllSessions.FirstOrDefault()?.Id;
        }

        /// <summary>
        /// agterm's workspaceRemovalTarget: after the selected session's workspace went, the most recent shown session, else
        /// the first shown; the neighbouring workspace's first only when nothing is shown.
        /// </summary>
        private Guid? WorkspaceRemovalTarget(int index)
        {
            List<Session> visible = NavigableSessions.ToList();
            if (visible.Count > 0)
            {
                HashSet<Guid> ids = visible.Select(session => session.Id).ToHashSet();
                return recency.FirstOrDefault(ids.Contains) is Guid recent && recent != Guid.Empty ? recent : visible[0].Id;
            }

            if (workspaces.Count == 0)
            {
                return null;
            }

            return workspaces[Math.Min(index, workspaces.Count - 1)].SessionList.FirstOrDefault()?.Id ?? AllSessions.FirstOrDefault()?.Id;
        }

        private Workspace RequireWorkspace(Guid workspaceId)
        {
            return FindWorkspace(workspaceId) ?? throw new KeyNotFoundException("Workspace not found: " + workspaceId);
        }

        private Session RequireSession(Guid sessionId)
        {
            return FindSessionOrPending(sessionId) ?? throw new KeyNotFoundException("Session not found: " + sessionId);
        }

        /// <summary>A session in the tree, or one closed with undo still in reach: what it shows can still be cleaned up.</summary>
        public Session? FindSessionOrPending(Guid sessionId)
        {
            return FindSession(sessionId) ?? pendingCloses.SelectMany(close => close.Sessions).FirstOrDefault(session => session.Id == sessionId);
        }

        /// <summary>agterm's scheduleTreeChanged for a change outside the model, such as the window's name.</summary>
        public void NotifyTreeChanged()
        {
            TreeChanged?.Invoke();
        }

        /// <summary>A change that is saved at once but is not the tree's, so it sends no tree.changed.</summary>
        private void RaiseSaved()
        {
            StructureChanged?.Invoke();
            Changed?.Invoke();
        }

        private void RaiseStructureChanged()
        {
            TreeChanged?.Invoke();
            StructureChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>
        /// A soft removal, as agterm's soft close, does not save: until the undo window ends the file still holds what was
        /// closed, so a crash in those seconds loses nothing.
        /// </summary>
        private void RaiseRemoved(bool final)
        {
            if (final)
            {
                RaiseStructureChanged();
                return;
            }

            TreeChanged?.Invoke();
            Changed?.Invoke();
        }

        private static string NormalizeName(string? name, string fallback)
        {
            return TerminalText.Name(name) ?? fallback;
        }
    }
}
