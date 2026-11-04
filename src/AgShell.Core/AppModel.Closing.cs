using System.Text.Json.Serialization;
using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>Where a soft-closed session sat, to put it back on undo.</summary>
    public sealed record ClosedPlace(Session Session, Guid WorkspaceId, string WorkspaceName, int WorkspaceIndex, int Index);

    /// <summary>
    /// A close still inside its undo window, as agterm keeps one: the sessions are out of the tree but their shells
    /// live on, so an undo brings them back with everything they printed.
    /// </summary>
    public sealed class PendingClose
    {
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>The closed workspace, or null for a close of sessions.</summary>
        public Workspace? Workspace { get; init; }

        public int WorkspaceIndex { get; init; }

        public Guid? SelectedSessionId { get; init; }

        public required IReadOnlyList<ClosedPlace> Places { get; init; }

        /// <summary>A closed workspace goes into the recently closed list at once, as agterm's; undo takes it out again.</summary>
        public RecentClosedItem? RecentItem { get; init; }

        /// <summary>The same for closed sessions, one entry each.</summary>
        public IReadOnlyList<RecentClosedItem> RecentSessions { get; init; } = Array.Empty<RecentClosedItem>();

        /// <summary>agterm's focusMember: the closed workspace was in the focus set, and an undo marks it again.</summary>
        public bool FocusMember { get; init; }

        public IEnumerable<Session> Sessions => Workspace?.Sessions ?? Places.Select(place => place.Session);

        public string Title => Workspace is Workspace workspace
            ? Strings.T("Workspace «{0}» закрыт", workspace.Name)
            : Places.Count == 1 ? Strings.T("Сессия «{0}» закрыта", Places[0].Session.DisplayName) : Strings.T("Закрыто сессий: {0}", Places.Count);
    }

    /// <summary>A closed session, as Open Recent brings it back: a fresh shell where the old one was.</summary>
    public sealed record RecentSession
    {
        public Guid WorkspaceId { get; init; }

        public string WorkspaceName { get; init; } = string.Empty;

        public int WorkspaceIndex { get; init; }

        public int SessionIndex { get; init; }

        public required SessionSnapshot Snapshot { get; init; }
    }

    /// <summary>A closed workspace, as Open Recent brings it back: agterm's RecentClosedWorkspace.</summary>
    public sealed record RecentWorkspace
    {
        public required WorkspaceSnapshot Snapshot { get; init; }

        public Guid? SelectedSessionId { get; init; }

        /// <summary>agterm's focusMember: the workspace was in the focus set, and Open Recent marks it again. Null in older entries.</summary>
        public bool? FocusMember { get; init; }
    }

    public sealed record RecentClosedItem
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>"session" or "workspace".</summary>
        public string Kind { get; init; } = "session";

        public string Title { get; init; } = string.Empty;

        public string? Subtitle { get; init; }

        public DateTimeOffset ClosedAt { get; init; }

        public RecentSession? Session { get; init; }

        public RecentWorkspace? Workspace { get; init; }

        /// <summary>The workspace it comes back into, and the sessions it brings back.</summary>
        [JsonIgnore]
        public Guid? WorkspaceId => Workspace?.Snapshot.Id ?? Session?.WorkspaceId;

        [JsonIgnore]
        public IEnumerable<Guid> SessionIds => Workspace?.Snapshot.Sessions.Select(session => session.Id) ?? (Session is RecentSession session ? new[] { session.Snapshot.Id } : Array.Empty<Guid>());
    }

    /// <summary>
    /// What one restore achieved, as agterm's RecentClosedRestore: <see cref="Complete"/> is false when a session of the entry
    /// ended up neither here nor in another window that already held it, so dropping the entry would lose it for good.
    /// </summary>
    public readonly record struct RecentClosedRestore(bool Restored, bool Complete)
    {
        public static readonly RecentClosedRestore Failed = new RecentClosedRestore(false, false);
    }

    public sealed partial class AppModel
    {
        /// <summary>How long a closed session or workspace can be brought back alive, as in agterm.</summary>
        public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

        public const int RecentClosedLimit = RecentClosedList.Limit;

        private readonly List<PendingClose> pendingCloses = new List<PendingClose>();
        private RecentClosedList recent = new RecentClosedList();

        public IReadOnlyList<PendingClose> PendingCloses => pendingCloses;

        /// <summary>The recently closed list, newest first: the app's one, shared by its windows.</summary>
        public IReadOnlyList<RecentClosedItem> RecentClosed => recent.Items;

        public RecentClosedList Recent => recent;

        /// <summary>Joins the app's shared list in place of the model's own.</summary>
        public void ShareRecent(RecentClosedList shared)
        {
            recent = shared;
        }

        /// <summary>Sessions hidden by a close that can still be undone; their shells are alive.</summary>
        public IEnumerable<Guid> PendingSessionIds => pendingCloses.SelectMany(close => close.Sessions).Select(session => session.Id);

        /// <summary>Takes sessions out of the tree as one undoable close. Returns null when none of them exists.</summary>
        public PendingClose? SoftCloseSessions(IEnumerable<Guid> sessionIds)
        {
            List<ClosedPlace> places = new List<ClosedPlace>();
            foreach (Guid id in sessionIds.Distinct())
            {
                if (FindSession(id) is Session session && FindWorkspaceOf(id) is Workspace workspace)
                {
                    places.Add(new ClosedPlace(session, workspace.Id, workspace.Name, workspaces.IndexOf(workspace), workspace.SessionList.IndexOf(session)));
                }
            }

            if (places.Count == 0)
            {
                return null;
            }

            // Put back in tree order, so every saved index is right at the moment its session returns.
            places = places.OrderBy(place => place.WorkspaceIndex).ThenBy(place => place.Index).ToList();
            // Undo puts back these same objects: what the launch armed and no pane took would run when their panes start again.
            places.ForEach(place => ClearPending(place.Session));
            places.ForEach(place => CloseTimedHud(place.Session));
            // As agterm's: each session is in the recently closed list from the moment it is closed, so a crash before the
            // undo window ends cannot lose it; an undo takes the entries out again.
            List<RecentClosedItem> listed = new List<RecentClosedItem>();
            foreach (ClosedPlace place in places)
            {
                if (RecordRecentSession(place.Session, place.WorkspaceId, place.WorkspaceName, place.WorkspaceIndex, place.Index) is RecentClosedItem item)
                {
                    listed.Add(item);
                }
            }

            // As agterm's softCloseSessions: every row goes first, then the selection moves once, from where the selected
            // one stood less the rows closed before it; the sessions keep their place in Ctrl+Tab history for an undo.
            Guid? previous = SelectedSessionId;
            ClosedPlace? active = places.FirstOrDefault(place => place.Session.Id == previous);
            for (int at = places.Count - 1; at >= 0; at--)
            {
                ClosedPlace place = places[at];
                workspaces[place.WorkspaceIndex].SessionList.Remove(place.Session);
                DropFromDashboard(new[] { place.Session.Id });
                CancelAsksOf(place.Session.Id);
            }

            // As agterm's: the rows leave the tree now, so they are reported closed now, before a new selection is.
            foreach (ClosedPlace place in places)
            {
                AppendClosed(place.WorkspaceId, place.Session);
            }

            if (active is not null)
            {
                int closedBefore = places.Count(place => place.WorkspaceIndex == active.WorkspaceIndex && place.Index < active.Index);
                SetSelection(CloseReselectionTarget(active.WorkspaceIndex, active.Index - closedBefore) ?? AllSessions.FirstOrDefault()?.Id);
                DisableFocusIfSelectionOutside();
            }

            PendingClose close = new PendingClose { Places = places, RecentSessions = listed, SelectedSessionId = active?.Session.Id ?? places[0].Session.Id };
            pendingCloses.Add(close);
            TreeChanged?.Invoke();
            Changed?.Invoke();
            PendingSaveCancelled?.Invoke();
            return close;
        }

        /// <summary>Takes a workspace out of the tree as one undoable close. The last workspace stays, returning null.</summary>
        public PendingClose? SoftCloseWorkspace(Guid workspaceId)
        {
            Workspace workspace = RequireWorkspace(workspaceId);
            if (!CanRemoveWorkspace)
            {
                return null;
            }

            int index = workspaces.IndexOf(workspace);
            Guid? selected = LocalSelection(workspace);
            bool member = focusMembers.Contains(workspaceId);
            foreach (Session session in workspace.SessionList)
            {
                ClearPending(session);
                CloseTimedHud(session);
            }

            RemoveWorkspace(workspaceId, final: false);
            // agterm's foldingPendingCloses: an undone session close rebuilt this workspace as a shell, so an earlier close
            // of it still pending is absorbed here, its sessions after these; its membership counts, the shell carries none.
            foreach (PendingClose earlier in pendingCloses.Where(candidate => candidate.Workspace?.Id == workspaceId).ToList())
            {
                pendingCloses.Remove(earlier);
                HashSet<Guid> present = workspace.SessionList.Select(session => session.Id).ToHashSet();
                workspace.SessionList.AddRange(earlier.Workspace!.SessionList.Where(session => !present.Contains(session.Id)));
                member |= earlier.FocusMember;
            }

            RecentClosedItem item = RecordRecentWorkspace(workspace, selected, member);
            PendingClose close = new PendingClose { Workspace = workspace, WorkspaceIndex = index, SelectedSessionId = selected, Places = Array.Empty<ClosedPlace>(), RecentItem = item, FocusMember = member };
            pendingCloses.Add(close);
            Changed?.Invoke();
            PendingSaveCancelled?.Invoke();
            return close;
        }

        /// <summary>agterm's closeTimedHud: a HUD that hides itself goes now, so an undo does not bring it back with time left.</summary>
        private void CloseTimedHud(Session session)
        {
            if (session.Overlay is { Hud: HudSpec { HideAfter: > 0 } } hud)
            {
                CloseOverlay(session.Id, hud.Id);
            }
        }

        /// <summary>
        /// Brings back the latest pending close, or the one with <paramref name="closeId"/>, live. A close of sessions selects
        /// <paramref name="selecting"/> when it holds it, else the one that was selected, else its first, as agterm's. False
        /// when there is none.
        /// </summary>
        public bool UndoClose(Guid? closeId = null, Guid? selecting = null)
        {
            PendingClose? close = closeId is Guid id ? pendingCloses.FirstOrDefault(candidate => candidate.Id == id) : pendingCloses.LastOrDefault();
            if (close is null)
            {
                return false;
            }

            pendingCloses.Remove(close);
            if (close.RecentItem is RecentClosedItem listed)
            {
                recent.Remove(listed);
            }

            foreach (RecentClosedItem item in close.RecentSessions)
            {
                recent.Remove(item);
            }

            Guid? target;
            if (close.Workspace is Workspace workspace)
            {
                // agterm's restorePendingWorkspace: a shell an undone session close or Open Recent rebuilt under this id takes
                // the sessions in, rather than a second workspace with the same id beside it.
                if (FindWorkspace(workspace.Id) is Workspace shell)
                {
                    HashSet<Guid> live = AllSessions.Select(session => session.Id).ToHashSet();
                    foreach (Session restored in workspace.SessionList.Where(session => !live.Contains(session.Id)).ToList())
                    {
                        shell.SessionList.Add(restored);
                        AppendCreated(shell.Id, restored);
                    }
                }
                else
                {
                    workspaces.Insert(Math.Clamp(close.WorkspaceIndex, 0, workspaces.Count), workspace);
                    foreach (Session restored in workspace.SessionList)
                    {
                        AppendCreated(workspace.Id, restored);
                    }
                }

                // Marked before the selection, so a restored member is inside the set when the filter is checked.
                if (close.FocusMember)
                {
                    MarkFocusMember(workspace.Id);
                }

                target = close.SelectedSessionId ?? workspace.SessionList.FirstOrDefault()?.Id;
            }
            else
            {
                foreach (ClosedPlace place in close.Places)
                {
                    Workspace into = WorkspaceForRestore(place.WorkspaceId, place.WorkspaceName, place.WorkspaceIndex);
                    into.SessionList.Insert(Math.Clamp(place.Index, 0, into.SessionList.Count), place.Session);
                    AppendCreated(into.Id, place.Session);
                }

                HashSet<Guid> held = close.Places.Select(place => place.Session.Id).ToHashSet();
                target = selecting is Guid asked && held.Contains(asked) ? asked
                    : close.SelectedSessionId is Guid was && held.Contains(was) ? was
                    : close.Places[0].Session.Id;
            }

            if (target is Guid chosen)
            {
                SetSelection(chosen);
                DisableFocusIfSelectionOutside();
            }

            RaiseStructureChanged();
            return true;
        }

        /// <summary>
        /// Makes a pending close final once its undo window ends: the sessions are reported closed and remembered as
        /// recently closed. Returns the sessions whose shells are to be stopped.
        /// </summary>
        public IReadOnlyList<Session> FinalizeClose(Guid closeId)
        {
            PendingClose? close = pendingCloses.FirstOrDefault(candidate => candidate.Id == closeId);
            if (close is null)
            {
                return Array.Empty<Session>();
            }

            pendingCloses.Remove(close);
            // The sessions leave Ctrl+Tab history only now; until then an undo kept their place in it.
            HashSet<Guid> gone = close.Sessions.Select(session => session.Id).ToHashSet();
            recency.RemoveAll(gone.Contains);
            // The close is final: the file stops holding what it took out.
            StructureChanged?.Invoke();
            Changed?.Invoke();
            return close.Sessions.ToList();
        }

        /// <summary>Ends every undo window at once, for shutdown.</summary>
        public IReadOnlyList<Session> FinalizeAllCloses()
        {
            return pendingCloses.Select(close => close.Id).ToList().SelectMany(FinalizeClose).ToList();
        }

        /// <summary>Reopens the newest entry, or the one with <paramref name="itemId"/>, when no other window holds any of it. False when nothing came back.</summary>
        public bool ReopenRecent(Guid? itemId = null)
        {
            return recent.Find(itemId) is RecentClosedItem item && ReopenRecent(item, new HashSet<Guid>()).Restored;
        }

        /// <summary>
        /// agterm's reopenRecentClosedOutcome for this window: rebuilds the entry with fresh shells, never one of
        /// <paramref name="occupiedElsewhere"/>, the sessions other windows hold. The entry goes only when nothing of it was
        /// left out, and <see cref="RecentClosedRestore.Complete"/> says whether it is gone from the list as reloaded.
        /// </summary>
        public RecentClosedRestore ReopenRecent(RecentClosedItem item, IReadOnlySet<Guid> occupiedElsewhere)
        {
            RecentClosedRestore outcome = RestoreRecent(item, occupiedElsewhere);
            if (!outcome.Restored)
            {
                return RecentClosedRestore.Failed;
            }

            if (outcome.Complete)
            {
                recent.Remove(item);
            }

            return new RecentClosedRestore(true, recent.Find(item.Id) is null);
        }

        /// <summary>agterm's restoreRecentClosed.</summary>
        private RecentClosedRestore RestoreRecent(RecentClosedItem item, IReadOnlySet<Guid> occupiedElsewhere)
        {
            if (item.Kind == "session")
            {
                if (item.Session is not RecentSession recentSession)
                {
                    return RecentClosedRestore.Failed;
                }

                if (RestoreOrSelectExistingRecentSession(recentSession))
                {
                    return new RecentClosedRestore(true, true);
                }

                // The entry is routed to the window holding this session, so it being held elsewhere means that lookup missed:
                // refuse rather than build a second object under the same id.
                if (occupiedElsewhere.Contains(recentSession.Snapshot.Id))
                {
                    return RecentClosedRestore.Failed;
                }

                Workspace into = WorkspaceForRestore(recentSession.WorkspaceId, recentSession.WorkspaceName, recentSession.WorkspaceIndex);
                Session restored = SessionFrom(recentSession.Snapshot);
                into.SessionList.Insert(Math.Clamp(recentSession.SessionIndex, 0, into.SessionList.Count), restored);
                AppendCreated(into.Id, restored);
                SetSelection(restored.Id);
                DisableFocusIfSelectionOutside();
                RaiseStructureChanged();
                return new RecentClosedRestore(true, true);
            }

            if (item.Workspace is not RecentWorkspace recentWorkspace)
            {
                return RecentClosedRestore.Failed;
            }

            if (RestoreOrSelectExistingRecentWorkspace(recentWorkspace, occupiedElsewhere))
            {
                return WorkspaceRestoreOutcome(recentWorkspace, occupiedElsewhere);
            }

            WorkspaceSnapshot snapshot = recentWorkspace.Snapshot;
            // A session of the snapshot may live on in another workspace pending a close, or in another window: its original
            // is alive there, so everything else is rebuilt.
            HashSet<Guid> taken = PlacedSessionIds(occupiedElsewhere);
            Workspace workspace = new Workspace(snapshot.Id, NormalizeName(snapshot.Name, NextWorkspaceName)) { Collapsed = snapshot.Collapsed };
            workspace.SessionList.AddRange(snapshot.Sessions.Where(session => !taken.Contains(session.Id)).Select(session => SessionFrom(session)));
            // As agterm's persistent Open Recent: the workspace comes back last, the others keep their places.
            workspaces.Add(workspace);
            foreach (Session restored in workspace.SessionList)
            {
                AppendCreated(workspace.Id, restored);
            }

            // Marked before the selection, as agterm's, so a restored member is inside the set when the filter is checked.
            if (recentWorkspace.FocusMember == true)
            {
                MarkFocusMember(workspace.Id);
            }

            // An empty workspace has nothing to select, and the live selection stays.
            Guid? select = recentWorkspace.SelectedSessionId is Guid selected && workspace.SessionList.Any(session => session.Id == selected)
                ? selected
                : workspace.SessionList.FirstOrDefault()?.Id;
            if (select is Guid chosen)
            {
                SetSelection(chosen);
                DisableFocusIfSelectionOutside();
            }

            RaiseStructureChanged();
            return WorkspaceRestoreOutcome(recentWorkspace, occupiedElsewhere);
        }

        /// <summary>Whether every session of the snapshot now exists somewhere: here, in a pending close, or in another window.</summary>
        private RecentClosedRestore WorkspaceRestoreOutcome(RecentWorkspace recentWorkspace, IReadOnlySet<Guid> occupiedElsewhere)
        {
            HashSet<Guid> placed = PlacedSessionIds(occupiedElsewhere);
            return new RecentClosedRestore(true, recentWorkspace.Snapshot.Sessions.All(session => placed.Contains(session.Id)));
        }

        private HashSet<Guid> PlacedSessionIds(IReadOnlySet<Guid> occupiedElsewhere)
        {
            return AllSessions.Select(session => session.Id).Concat(PendingSessionIds).Concat(occupiedElsewhere).ToHashSet();
        }

        /// <summary>A session still in its undo grace comes back with that undo, selected; one in the tree is selected.</summary>
        private bool RestoreOrSelectExistingRecentSession(RecentSession recentSession)
        {
            Guid id = recentSession.Snapshot.Id;
            if (pendingCloses.LastOrDefault(close => close.Sessions.Any(session => session.Id == id)) is PendingClose holding)
            {
                return UndoClose(holding.Id, id);
            }

            if (FindSession(id) is null)
            {
                return false;
            }

            SelectSession(id);
            return true;
        }

        /// <summary>
        /// agterm's restoreOrSelectExistingRecentWorkspace: every pending close of the workspace, and of its sessions closed from
        /// it, is undone first, so none is rebuilt beside its live original. A workspace in the tree takes in the sessions it
        /// lacks; otherwise, when every session is already somewhere, one in this window is selected.
        /// </summary>
        private bool RestoreOrSelectExistingRecentWorkspace(RecentWorkspace recentWorkspace, IReadOnlySet<Guid> occupiedElsewhere)
        {
            WorkspaceSnapshot snapshot = recentWorkspace.Snapshot;
            HashSet<Guid> sessionIds = snapshot.Sessions.Select(session => session.Id).ToHashSet();
            while (PendingCloseOfWorkspace(snapshot.Id, sessionIds) is PendingClose pending)
            {
                UndoClose(pending.Id);
            }

            if (FindWorkspace(snapshot.Id) is Workspace existing)
            {
                // A reopened session rebuilt this workspace as a shell holding only itself; the snapshot's other sessions have
                // no copy but this one, so the missing ones are rebuilt.
                HashSet<Guid> taken = PlacedSessionIds(occupiedElsewhere);
                List<Session> missing = snapshot.Sessions.Where(session => !taken.Contains(session.Id)).Select(session => SessionFrom(session)).ToList();
                existing.SessionList.AddRange(missing);
                foreach (Session restored in missing)
                {
                    AppendCreated(existing.Id, restored);
                }

                if (recentWorkspace.FocusMember == true)
                {
                    MarkFocusMember(existing.Id);
                }

                Guid? target = recentWorkspace.SelectedSessionId is Guid selected && existing.SessionList.Any(session => session.Id == selected)
                    ? selected
                    : existing.SessionList.FirstOrDefault()?.Id;
                if (target is Guid chosen)
                {
                    SelectSession(chosen);
                }

                if (missing.Count > 0)
                {
                    RaiseStructureChanged();
                }
                else
                {
                    RaiseSaved();
                }

                return true;
            }

            // Selecting one session while another exists only in the snapshot would drop the entry with it never rebuilt.
            HashSet<Guid> placed = PlacedSessionIds(occupiedElsewhere);
            if (!sessionIds.All(placed.Contains))
            {
                return false;
            }

            if (AllSessions.FirstOrDefault(session => sessionIds.Contains(session.Id)) is Session present)
            {
                SelectSession(present.Id);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The newest pending close a workspace's restore consumes: the workspace's own, or a close of sessions closed from it.
        /// A foreign workspace that merely holds one of its sessions is a close the user meant, so it is not undone.
        /// </summary>
        private PendingClose? PendingCloseOfWorkspace(Guid workspaceId, IReadOnlySet<Guid> sessionIds)
        {
            return pendingCloses.LastOrDefault(close => close.Workspace?.Id == workspaceId
                || close.Places.Any(place => place.WorkspaceId == workspaceId && sessionIds.Contains(place.Session.Id)));
        }

        /// <summary>agterm's pendingHoldsWorkspace: a pending close of the workspace, or of a session closed from it.</summary>
        public bool PendingHoldsWorkspace(Guid workspaceId)
        {
            return pendingCloses.Any(close => close.Workspace?.Id == workspaceId || close.Places.Any(place => place.WorkspaceId == workspaceId));
        }

        /// <summary>Empties the list; false when it is not empty after, as agterm's recent.clear checks the reloaded list.</summary>
        public bool ClearRecent()
        {
            recent.Clear();
            Changed?.Invoke();
            return recent.Items.Count == 0;
        }

        /// <summary>Loads the saved list, keeping the newest entries.</summary>
        public void LoadRecent(IEnumerable<RecentClosedItem> items)
        {
            recent.Load(items);
        }

        /// <summary>The workspace a restore goes into: still there, or rebuilt empty under its old id, name and place.</summary>
        private Workspace WorkspaceForRestore(Guid id, string name, int index)
        {
            if (FindWorkspace(id) is Workspace existing)
            {
                return existing;
            }

            // agterm's rebuiltWorkspaceShell: the newest description of the workspace names the shell — a pending close of it,
            // else its Open Recent entry, else the caller's older copy.
            Workspace workspace = pendingCloses.LastOrDefault(close => close.Workspace?.Id == id)?.Workspace is Workspace closing
                ? new Workspace(id, closing.Name) { Collapsed = closing.Collapsed }
                : recent.Items.FirstOrDefault(entry => entry.Workspace?.Snapshot.Id == id)?.Workspace?.Snapshot is WorkspaceSnapshot listed
                    ? new Workspace(id, NormalizeName(listed.Name, NextWorkspaceName)) { Collapsed = listed.Collapsed }
                    : new Workspace(id, NormalizeName(name, NextWorkspaceName));
            workspaces.Insert(Math.Clamp(index, 0, workspaces.Count), workspace);
            return workspace;
        }

        private void RecordRecentSession(Session session, Workspace workspace, int workspaceIndex, int index)
        {
            RecordRecentSession(session, workspace.Id, workspace.Name, workspaceIndex, index);
        }

        private RecentClosedItem? RecordRecentSession(Session session, Guid workspaceId, string workspaceName, int workspaceIndex, int index)
        {
            // A remote row is the other machine's session; its own app keeps it.
            if (session.RemoteHost is not null)
            {
                return null;
            }

            RecentClosedItem item = new RecentClosedItem
            {
                Kind = "session",
                Title = session.DisplayName,
                Subtitle = workspaceName,
                ClosedAt = DateTimeOffset.UtcNow,
                Session = new RecentSession { WorkspaceId = workspaceId, WorkspaceName = workspaceName, WorkspaceIndex = workspaceIndex, SessionIndex = index, Snapshot = SnapshotOf(session) },
            };
            AddRecent(item, existing => existing.Session?.Snapshot.Id == session.Id);
            return item;
        }

        private RecentClosedItem RecordRecentWorkspace(Workspace workspace, Guid? selected, bool focusMember)
        {
            RecentClosedItem item = new RecentClosedItem
            {
                Kind = "workspace",
                Title = workspace.Name,
                // Rows attached from another machine do not come back with the workspace, so they are not counted.
                Subtitle = SessionCount(workspace.SessionList.Count(session => session.RemoteHost is null)),
                ClosedAt = DateTimeOffset.UtcNow,
                Workspace = new RecentWorkspace
                {
                    Snapshot = new WorkspaceSnapshot { Id = workspace.Id, Name = workspace.Name, Collapsed = workspace.Collapsed, Sessions = workspace.SessionList.Where(session => session.RemoteHost is null).Select(SnapshotOf).ToList() },
                    SelectedSessionId = selected,
                    FocusMember = focusMember,
                },
            };
            AddRecent(item, existing => existing.Workspace?.Snapshot.Id == workspace.Id);
            return item;
        }

        /// <summary>agterm's "N session(s)".</summary>
        private static string SessionCount(int count)
        {
            return count == 1 ? Strings.T("1 сессия") : Strings.T("сессий: {0}", count);
        }

        private void AddRecent(RecentClosedItem item, Func<RecentClosedItem, bool> sameThing)
        {
            recent.Add(item, sameThing);
        }
    }
}
