using AgShell.Core;

namespace AgShell.App
{
    /// <summary>agterm's Dock menu, this window's part: what its items list and what they do in it.</summary>
    public partial class MainWindow
    {
        /// <summary>agterm's navigableRecentSessions: the navigable sessions by recency, the selected one left out.</summary>
        internal IEnumerable<Session> DockRecentSessions()
        {
            HashSet<Guid> navigable = model.NavigableSessions.Select(session => session.Id).ToHashSet();
            navigable.Remove(model.SelectedSessionId ?? Guid.Empty);
            return model.RecentSessions.Where(navigable.Contains).Take(10).Select(model.FindSession).OfType<Session>();
        }

        /// <summary>agterm's attentionSessions: this window's non-idle sessions in attention order.</summary>
        internal IEnumerable<Session> DockAttentionSessions()
        {
            return AttentionSessions().Select(entry => entry.Session).OrderBy(session => AttentionRank(session.Status)).ThenByDescending(session => session.StatusChangedAt ?? DateTimeOffset.MinValue);
        }

        /// <summary>agterm's item title: the session's name, then its workspace's when it has one.</summary>
        internal string DockTitle(Session session)
        {
            string? workspace = model.FindWorkspaceOf(session.Id)?.Name;
            return string.IsNullOrEmpty(workspace) ? session.DisplayName : session.DisplayName + " — " + workspace;
        }

        internal bool DashboardOpen => model.Dashboard is not null;

        /// <summary>
        /// An item chosen, with agterm's guards checked now, as its items are only enabled while they hold: the window comes
        /// forward first and is the frontmost one before the action runs.
        /// </summary>
        internal void RunDockAction(string action, Guid? sessionId, bool dashboardWasOpen)
        {
            switch (action)
            {
                case "new-session" when UiActionsEnabled && model.CurrentWorkspace is not null:
                    BringForward();
                    NewSession(null, null);
                    break;
                case "quick-terminal" when UiActionsEnabled:
                    BringForward();
                    Library.Quick.Toggle();
                    break;
                case "dashboard" when !model.Zoomed && DashboardOpen == dashboardWasOpen && (DashboardOpen || DockRecentSessions().Any()):
                    BringForward();
                    if (DashboardOpen)
                    {
                        model.CloseDashboard();
                    }
                    else
                    {
                        _ = dispatcher.HandleAsync(new AgShell.Protocol.ControlRequest { Cmd = "dashboard", Args = new AgShell.Protocol.ControlArgs { Mru = true, AutoSize = true } });
                    }

                    break;
                case "session" when sessionId is Guid id && UiActionsEnabled && model.FindSession(id) is not null:
                    // agterm's activate(session): the window forward, activity noted, the session selected at the pane that set its status.
                    BringForward();
                    SelectAttention(id);
                    break;
            }
        }

        private void BringForward()
        {
            Deminiaturize();
            Activate();
            Library.TakeFrontmost(this);
        }
    }
}
