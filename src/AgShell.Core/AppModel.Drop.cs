namespace AgShell.Core
{
    /// <summary>agterm's batch move and the sidebar drops: the page names the row and the side, SidebarDrop does the arithmetic.</summary>
    public sealed partial class AppModel
    {
        /// <summary>
        /// agterm's moveSessions: the sessions move in tree order as one block. Without an index several of them leave those
        /// already in the target where they are and append the rest (one alone appends, as moveSession); with an index (a
        /// drop) all are taken out first and the block goes in at that index. Returns how many moved.
        /// </summary>
        public int MoveSessions(IEnumerable<Guid> sessionIds, Guid targetWorkspaceId, int? index = null)
        {
            Workspace? target = FindWorkspace(targetWorkspaceId);
            if (target is null)
            {
                return 0;
            }

            HashSet<Guid> wanted = sessionIds.ToHashSet();
            List<Session> moving = AllSessions.Where(session => wanted.Contains(session.Id)).ToList();
            if (index is null && moving.Count > 1)
            {
                moving = moving.Where(session => FindWorkspaceOf(session.Id) != target).ToList();
            }

            if (moving.Count == 0)
            {
                return 0;
            }

            string before = TreeOrder();
            foreach (Session session in moving)
            {
                FindWorkspaceOf(session.Id)!.SessionList.Remove(session);
            }

            int destination = Math.Clamp(index ?? target.SessionList.Count, 0, target.SessionList.Count);
            target.SessionList.InsertRange(destination, moving);
            if (SelectedSessionId is Guid selected && wanted.Contains(selected))
            {
                DisableFocusIfSelectionOutside();
            }

            // agterm: tree.changed only when the order changed.
            if (TreeOrder() != before)
            {
                RaiseStructureChanged();
            }
            else
            {
                RaiseSaved();
            }

            return moving.Count;
        }

        private string TreeOrder()
        {
            return string.Join("|", workspaces.Select(workspace => string.Join(",", workspace.SessionList.Select(session => session.Id))));
        }

        /// <summary>
        /// Where dragged sessions would land: on a workspace row at its end, on a session row before or after it. Null when the
        /// drop changes nothing or names what is gone.
        /// </summary>
        public SidebarDrop.SessionResolution? ResolveSessionDrop(IReadOnlyCollection<Guid> sessionIds, Guid workspaceId, Guid? sessionId, bool after)
        {
            Workspace? workspace = FindWorkspace(workspaceId);
            if (workspace is null)
            {
                return null;
            }

            List<SidebarDrop.SessionSource> sources = new List<SidebarDrop.SessionSource>();
            foreach (Session session in AllSessions.Where(session => sessionIds.Contains(session.Id)))
            {
                Workspace owner = FindWorkspaceOf(session.Id)!;
                sources.Add(new SidebarDrop.SessionSource(owner.Id, owner.SessionList.IndexOf(session)));
            }

            if (sources.Count == 0 || sources.Count != sessionIds.Distinct().Count())
            {
                return null;
            }

            int count = workspace.SessionList.Count;
            if (sessionId is not Guid rowId)
            {
                return SidebarDrop.ResolveSessions(sources, new SidebarDrop.SessionDropTarget.WorkspaceRow(workspace.Id, count), SidebarDrop.OnItemIndex);
            }

            int row = workspace.SessionList.FindIndex(session => session.Id == rowId);
            if (row < 0)
            {
                return null;
            }

            return SidebarDrop.ResolveSessions(sources, new SidebarDrop.SessionDropTarget.SessionRow(workspace.Id, row, count), after ? SidebarDrop.OnItemIndex : row);
        }

        /// <summary>Drops sessions as agterm's acceptDrop does; returns how many moved, 0 for a no-op.</summary>
        public int DropSessions(IReadOnlyCollection<Guid> sessionIds, Guid workspaceId, Guid? sessionId, bool after)
        {
            SidebarDrop.SessionResolution? resolution = ResolveSessionDrop(sessionIds, workspaceId, sessionId, after);
            return resolution is null ? 0 : MoveSessions(sessionIds, resolution.Workspace, resolution.Destination);
        }

        /// <summary>
        /// Where a dragged workspace would land: <paramref name="slot"/> counts the shown workspaces above the drop, mapped onto
        /// the full list past those the focus filter hides. Null for a no-op.
        /// </summary>
        public SidebarDrop.WorkspaceResolution? ResolveWorkspaceDrop(Guid workspaceId, int slot)
        {
            int source = workspaces.FindIndex(workspace => workspace.Id == workspaceId);
            if (source < 0)
            {
                return null;
            }

            List<int> visible = VisibleWorkspaces.Select(workspace => workspaces.IndexOf(workspace)).ToList();
            return SidebarDrop.ResolveWorkspace(source, workspaces.Count, SidebarDrop.WorkspaceInsertIndex(visible, slot));
        }

        /// <summary>
        /// agterm's spring-loaded expansion that a successful drop keeps: the row a drag opened stays open on screen, its saved
        /// fold untouched, as a reveal.
        /// </summary>
        public void KeepSpringOpen(Guid workspaceId)
        {
            if (FindWorkspace(workspaceId) is { Collapsed: true } && revealed.Add(workspaceId))
            {
                Changed?.Invoke();
            }
        }

        /// <summary>Drops a workspace as agterm's acceptDrop does; false for a no-op.</summary>
        public bool DropWorkspace(Guid workspaceId, int slot)
        {
            if (ResolveWorkspaceDrop(workspaceId, slot) is not SidebarDrop.WorkspaceResolution resolution)
            {
                return false;
            }

            MoveWorkspace(workspaceId, resolution.Destination);
            return true;
        }
    }
}
