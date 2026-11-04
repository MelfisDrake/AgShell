namespace AgShell.Core
{
    /// <summary>
    /// agterm's two ways to narrow the sidebar: flagged sessions shown across workspaces, and a focus filter over a
    /// marked set of workspaces. Navigation, the session palette and Ctrl+Tab follow what the sidebar shows.
    /// </summary>
    public sealed partial class AppModel
    {
        public const string TreeMode = "tree";
        public const string FlaggedMode = "flagged";

        private readonly HashSet<Guid> focusMembers = new HashSet<Guid>();

        /// <summary>"tree", or "flagged" for the flat list of flagged sessions.</summary>
        public string SidebarMode { get; private set; } = TreeMode;

        /// <summary>Whether the focus filter applies; the marked set is kept while it is off.</summary>
        public bool FocusEnabled { get; private set; }

        /// <summary>The marked workspaces, in tree order.</summary>
        public IEnumerable<Guid> FocusMembers => workspaces.Where(workspace => focusMembers.Contains(workspace.Id)).Select(workspace => workspace.Id);

        public bool IsFocusMember(Guid workspaceId) => focusMembers.Contains(workspaceId);

        private bool FilterApplies => FocusEnabled && workspaces.Any(workspace => focusMembers.Contains(workspace.Id));

        /// <summary>The workspaces the tree shows: the marked ones while the filter applies, otherwise all.</summary>
        public IEnumerable<Workspace> VisibleWorkspaces => FilterApplies ? workspaces.Where(workspace => focusMembers.Contains(workspace.Id)) : workspaces;

        public IEnumerable<Session> FlaggedSessions => AllSessions.Where(session => session.Flagged);

        /// <summary>The sessions navigation steps through: the flagged ones in the flagged view, else those the tree shows.</summary>
        public IEnumerable<Session> NavigableSessions => SidebarMode == FlaggedMode ? FlaggedSessions : VisibleWorkspaces.SelectMany(workspace => workspace.Sessions);

        /// <summary>The workspace the tree is narrowed to, when the filter applies to exactly one.</summary>
        public Guid? SoleFocusedWorkspaceId => FilterApplies && FocusMembers.Count() == 1 ? FocusMembers.First() : null;

        /// <summary>
        /// Flags or unflags sessions as one, as agterm's Flag/Unflag: null flags them all unless all are flagged already.
        /// </summary>
        public void SetFlag(IReadOnlyCollection<Guid> sessionIds, bool? flagged)
        {
            List<Session> sessions = sessionIds.Select(FindSession).OfType<Session>().ToList();
            bool value = flagged ?? !sessions.All(session => session.Flagged);
            // agterm's setFlag: nothing to change, nothing reported or saved.
            if (sessions.All(session => session.Flagged == value))
            {
                return;
            }

            foreach (Session session in sessions)
            {
                session.Flagged = value;
            }

            ReselectIfHidden();
            RaiseStructureChanged();
        }

        /// <summary>agterm's Clear Flagged and session.flag clear: unflags every session and returns how many there were.</summary>
        public int ClearFlags()
        {
            List<Session> flagged = FlaggedSessions.ToList();
            if (flagged.Count == 0)
            {
                return 0;
            }

            foreach (Session session in flagged)
            {
                session.Flagged = false;
            }

            ReselectIfHidden();
            RaiseStructureChanged();
            return flagged.Count;
        }

        public void SetSidebarMode(string? mode)
        {
            string wanted = mode switch
            {
                FlaggedMode => FlaggedMode,
                TreeMode => TreeMode,
                _ => SidebarMode == FlaggedMode ? TreeMode : FlaggedMode,
            };
            // agterm's setSidebarMode: the same mode does nothing; a change is saved and is not the tree's.
            if (wanted == SidebarMode)
            {
                return;
            }

            SidebarMode = wanted;
            ReselectIfHidden();
            RaiseSaved();
        }

        /// <summary>
        /// The row menu's Focus/Unfocus, as agterm's toggleFocusedWorkspace: narrows the tree to this one workspace, or,
        /// when it is already the only one shown, clears the filter.
        /// </summary>
        public void ToggleFocusWorkspace(Guid workspaceId)
        {
            RequireWorkspace(workspaceId);
            if (SoleFocusedWorkspaceId == workspaceId)
            {
                ClearFocus();
                return;
            }

            CommitFocus(new HashSet<Guid> { workspaceId }, true);
        }

        /// <summary>
        /// agterm's setFocusMembership, Add to Focus / Remove from Focus: changes this workspace's membership and leaves the
        /// others. Marking only marks — an add that turned the filter on would hide the rows the next add needs; the filter
        /// goes off once the set empties.
        /// </summary>
        public void SetFocusMember(Guid workspaceId, bool? member)
        {
            RequireWorkspace(workspaceId);
            bool add = member ?? !focusMembers.Contains(workspaceId);
            HashSet<Guid> wanted = new HashSet<Guid>(focusMembers);
            if (add)
            {
                wanted.Add(workspaceId);
            }
            else
            {
                wanted.Remove(workspaceId);
            }

            CommitFocus(wanted, FocusEnabled);
        }

        /// <summary>Turns the filter on or off (null toggles), keeping the marked set; on with nothing marked is refused.</summary>
        public void SetFocusEnabled(bool? enabled)
        {
            CommitFocus(new HashSet<Guid>(focusMembers), enabled ?? !FocusEnabled);
        }

        public void ClearFocus()
        {
            CommitFocus(new HashSet<Guid>(), false);
        }

        /// <summary>
        /// agterm's commitFocus, the one place the two filter fields change: on with an empty set cannot be, an unchanged
        /// write does nothing (so repeated commands save nothing), and a selection the change hid moves into view.
        /// </summary>
        private void CommitFocus(HashSet<Guid> ids, bool enabled)
        {
            bool wantEnabled = enabled && ids.Count > 0;
            if (focusMembers.SetEquals(ids) && FocusEnabled == wantEnabled)
            {
                return;
            }

            focusMembers.Clear();
            focusMembers.UnionWith(ids);
            FocusEnabled = wantEnabled;
            // agterm's forgetHiddenFreshWorkspace: a target the filter hides is given up for good.
            if (freshWorkspaceId is Guid fresh && !VisibleWorkspaces.Any(workspace => workspace.Id == fresh))
            {
                freshWorkspaceId = null;
            }

            ReselectIfHidden();
            RaiseStructureChanged();
        }

        /// <summary>agterm's dropFocusMember: a removed workspace leaves the set, and the filter goes with the last member. The caller saves.</summary>
        private void DropFocusMember(Guid workspaceId)
        {
            focusMembers.Remove(workspaceId);
            if (focusMembers.Count == 0)
            {
                FocusEnabled = false;
            }
        }

        /// <summary>
        /// agterm's markFocusMember: an undone or reopened workspace goes back into the set; whether the filter is on is
        /// the window's state now, so it stays as it is. The caller saves.
        /// </summary>
        private void MarkFocusMember(Guid workspaceId)
        {
            focusMembers.Add(workspaceId);
        }

        /// <summary>
        /// A session chosen outside the marked set (from the API, a notification) switches the filter off, keeping the
        /// set, so the selection is always on screen. The flagged view ignores the set.
        /// </summary>
        private void DisableFocusIfSelectionOutside()
        {
            if (FilterApplies && SidebarMode == TreeMode && SelectedSessionId is Guid id && FindWorkspaceOf(id) is Workspace workspace && !focusMembers.Contains(workspace.Id))
            {
                FocusEnabled = false;
            }
        }

        /// <summary>After a narrowing hides the selection, moves it to the most recent session still shown, else the first.</summary>
        private void ReselectIfHidden()
        {
            List<Session> visible = NavigableSessions.ToList();
            if (SelectedSessionId is not Guid selected || visible.Count == 0 || visible.Any(session => session.Id == selected))
            {
                return;
            }

            HashSet<Guid> ids = visible.Select(session => session.Id).ToHashSet();
            SetSelection(recency.FirstOrDefault(ids.Contains) is Guid recent && recent != Guid.Empty ? recent : visible[0].Id);
        }

        private void RestoreFocus(StateSnapshot snapshot)
        {
            foreach (Guid id in snapshot.FocusedWorkspaceIds ?? new List<Guid>())
            {
                if (FindWorkspace(id) is not null)
                {
                    focusMembers.Add(id);
                }
            }

            FocusEnabled = snapshot.FocusEnabled && focusMembers.Count > 0;
            SidebarMode = snapshot.SidebarMode == FlaggedMode ? FlaggedMode : TreeMode;
        }
    }
}
