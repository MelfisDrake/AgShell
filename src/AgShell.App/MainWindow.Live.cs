using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>Live sessions: main and split shells kept by their daemons across window restarts.</summary>
    public partial class MainWindow
    {
        /// <summary>The live panes' daemons, which every window shares; null outside live mode.</summary>
        private static LiveDaemons? liveDaemons => Library.Live;

        private readonly HashSet<string> liveFailureNotices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> failedLiveRestarts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> failedRestartLines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void PostLiveFailure(PaneKey key)
        {
            if (liveFailureNotices.Add(model.PaneIdFor(key.SessionId, key.Pane)))
            {
                PostOutput(key, "\r\n" + Strings.T("Живая панель недоступна. Сессия сохранена; для запуска программы нужен явный перезапуск панели.") + "\r\n");
            }
        }

        private async Task RestartFailedPaneFromMenuAsync(Guid sessionId, PaneRole pane)
        {
            (string? error, _) = await RestartPaneAsync(sessionId, pane, null);
            if (error is not null)
            {
                PostOutput(new PaneKey(sessionId, pane), "agshell: " + error + "\r\n");
            }
        }

        private async Task<(string? Error, ControlRestartReceipt? Receipt)> RestartFailedPaneAsync(Session session, PaneRole pane, string? command)
        {
            string paneId = model.PaneIdFor(session.Id, pane);
            if (!failedLiveRestarts.Add(paneId))
            {
                return ("the pane is already restarting", null);
            }

            try
            {
                return await RestartFailedPaneCoreAsync(session, pane, command);
            }
            finally
            {
                failedLiveRestarts.Remove(paneId);
            }
        }

        private async Task<(string? Error, ControlRestartReceipt? Receipt)> RestartFailedPaneCoreAsync(Session session, PaneRole pane, string? command)
        {
            PaneKey key = new PaneKey(session.Id, pane);
            string paneId = model.PaneIdFor(session.Id, pane);
            if (!Guid.TryParse(paneId, out _))
            {
                return ("the saved pane id is incompatible with live mode; the pane was preserved", null);
            }

            if (Library.LaunchRestoreMode == CommandRestore.Live && liveDaemons is null)
            {
                return ("live mode is unavailable; the pane was preserved", null);
            }

            (ControlLiveInventory? inventory, string? inventoryError) = await Library.LiveInventoryAsync();
            if (inventory is null)
            {
                return (inventoryError ?? "could not read the live session list", null);
            }

            if (LiveInventory.RestartRefusal(inventory, paneId, entry.Id.Wire(), session.Id.Wire(), Panes.Name(pane)) is string refusal)
            {
                return (refusal, null);
            }

            if (model.FindSession(session.Id) != session || session.PaneIds.GetValueOrDefault(pane) != paneId)
            {
                return ("the pane changed during the restart; nothing was started", null);
            }

            if (inventory.Entries.Any(row => Guid.TryParse(row.Daemon, out Guid id) && id == Guid.Parse(paneId) && row.Observation == "running"))
            {
                string? failure = await Library.Daemons.KillConfirmedAsync(paneId);
                if (failure is not null)
                {
                    return ("could not end the previous live shell: " + failure + "; the pane was preserved", null);
                }
            }

            if (model.FindSession(session.Id) != session || (pane == PaneRole.Split && !session.HasSplit)
                || model.PaneIdFor(session.Id, pane) != paneId || host.IsRunning(key))
            {
                return ("the pane changed during the restart; nothing was started", null);
            }

            EndForRestart(session.Id, pane);
            liveFailureNotices.Remove(paneId);
            failedRestartLines[paneId] = command ?? string.Empty;
            model.SetLiveFailed(session.Id, pane, false);
            StartAfterRestart(session.Id, pane);
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                PaneRole? current = model.FindSession(session.Id) == session ? AppModel.PaneOfId(session, paneId) : null;
                if (current is not (PaneRole.Main or PaneRole.Split))
                {
                    failedRestartLines.Remove(paneId);
                    return ("the pane changed during the restart; no further action was taken", null);
                }

                PaneKey currentKey = new PaneKey(session.Id, current.Value);
                if (host.ProcessId(currentKey) is int pid && pid > 0)
                {
                    return (null, new ControlRestartReceipt { PaneId = paneId, NewPid = pid });
                }

                if (session.LiveFailed(current.Value))
                {
                    break;
                }
            }

            failedRestartLines.Remove(paneId);
            PaneRole? remainingPane = model.FindSession(session.Id) == session ? AppModel.PaneOfId(session, paneId) : null;
            if (remainingPane is PaneRole.Main or PaneRole.Split)
            {
                model.SetLiveFailed(session.Id, remainingPane.Value, true);
                PostLiveFailure(new PaneKey(session.Id, remainingPane.Value));
            }

            return ("no new shell was observed; the pane was preserved", null);
        }

        /// <summary>
        /// The pane of this window, a session in its undo window included, a live pane id names: agterm's pane claims, which
        /// tie a daemon to its pane by the pane's identity.
        /// </summary>
        internal PaneKey? LivePane(string paneId)
        {
            foreach (Session session in model.Workspaces.SelectMany(workspace => workspace.Sessions).Concat(PendingSessions()))
            {
                if (AppModel.PaneOfId(session, paneId) is PaneRole pane && (pane == PaneRole.Main || (pane == PaneRole.Split && session.HasSplit)) && !AppModel.IsRemotePane(session, pane))
                {
                    return new PaneKey(session.Id, pane);
                }
            }

            return null;
        }

        private IEnumerable<Session> PendingSessions()
        {
            foreach (PendingClose close in model.PendingCloses)
            {
                if (close.Workspace is Workspace gone)
                {
                    foreach (Session session in gone.Sessions)
                    {
                        yield return session;
                    }
                }

                foreach (ClosedPlace place in close.Places)
                {
                    yield return place.Session;
                }
            }
        }

        /// <summary>
        /// agterm's liveClaims for this window: every session of this machine, those in their undo window included, by its
        /// panes; a remote row claims none, as agterm's locallyManagedPaneIdentities; a pane with no id leaves the walk incomplete.
        /// </summary>
        internal (List<LiveInventory.Claim> Claims, bool Complete) LiveClaims(string windowName)
        {
            List<LiveInventory.Claim> claims = new List<LiveInventory.Claim>();
            bool complete = true;
            List<(Session Session, Guid WorkspaceId, string WorkspaceName, bool Pending)> sessions = model.Workspaces
                .SelectMany(workspace => workspace.Sessions.Select(session => (session, workspace.Id, workspace.Name, false)))
                .ToList();
            foreach (PendingClose close in model.PendingCloses)
            {
                if (close.Workspace is Workspace gone)
                {
                    sessions.AddRange(gone.Sessions.Select(session => (session, gone.Id, gone.Name, true)));
                }

                sessions.AddRange(close.Places.Select(place => (place.Session, place.WorkspaceId, place.WorkspaceName, true)));
            }

            foreach ((Session session, Guid workspaceId, string workspaceName, bool pending) in sessions.Where(item => item.Session.RemoteHost is null))
            {
                LiveInventory.Claim Claim(string pane, string id) => new LiveInventory.Claim(id, pane, pending, entry.Id.Wire(), windowName, "open", workspaceId.Wire(), workspaceName, session.Id.Wire(), session.DisplayName);
                if (session.PaneIds.GetValueOrDefault(PaneRole.Main) is not string main)
                {
                    complete = false;
                    continue;
                }

                claims.Add(Claim("left", main));
                complete &= Guid.TryParse(main, out _);
                if (!session.HasSplit)
                {
                    continue;
                }

                if (session.PaneIds.GetValueOrDefault(PaneRole.Split) is not string split)
                {
                    complete = false;
                    continue;
                }

                claims.Add(Claim("right", split));
                complete &= Guid.TryParse(split, out _);
            }

            return (claims, complete);
        }

        private bool StartPane(PaneKey key, PtyStartOptions options)
        {
            if (liveDaemons is not null && key.Pane is PaneRole.Main or PaneRole.Split)
            {
                string paneId = model.PaneIdFor(key.SessionId, key.Pane);
                if (!Guid.TryParse(paneId, out _))
                {
                    throw new InvalidOperationException(Strings.T("Сохранённый id панели {0} несовместим с режимом live. Панель сохранена без замены id.", paneId));
                }

                // agterm's reattach: a pane joining again attaches and never creates; it claims the lead only as a takeover.
                if (reattaching.Contains(key))
                {
                    host.RejoinLive(key, paneId, options.Columns, options.Rows, liveDaemons, follow: !claiming.Contains(key));
                    return true;
                }

                host.StartLive(key, paneId, options, liveDaemons);
                return true;
            }

            host.Start(key, options);
            return false;
        }

        public Task<(ControlLiveInventory? Inventory, string? Error)> LiveListAsync()
        {
            return Library.LiveInventoryAsync();
        }

        public Task<(string? Error, string Report, int Affected)> LivePruneAsync()
        {
            return Library.LivePruneAsync();
        }

        /// <summary>Whether a session of this window, an undoably closed one included, could own a kept shell.</summary>
        internal bool OwnsLive(Guid sessionId)
        {
            return model.FindSessionOrPending(sessionId) is not null;
        }

        /// <summary>
        /// live.kill on a pane shown here: its shell ends now, and the pane goes as agterm's handlePaneExit: a split closes, a
        /// main pane hands the session to its split, or with none the session closes, with no undo.
        /// </summary>
        internal void KillLivePane(Guid sessionId, PaneRole pane)
        {
            PaneKey key = new PaneKey(sessionId, pane);
            host.Flush(PostOutput);
            host.Stop(key);
            switch (pane)
            {
                case PaneRole.Split:
                    host.Stop(new PaneKey(sessionId, PaneRole.OverlayRight));
                    model.CloseSplit(sessionId);
                    break;
                case PaneRole.Main when model.FindSession(sessionId)?.HasSplit == true:
                    host.Move(new PaneKey(sessionId, PaneRole.Split), key);
                    host.Move(new PaneKey(sessionId, PaneRole.OverlayRight), new PaneKey(sessionId, PaneRole.OverlayLeft));
                    Post(new { type = "promote", sessionId });
                    model.PromoteSplit(sessionId);
                    break;
                default:
                    host.StopSession(sessionId);
                    model.RemoveSession(sessionId);
                    break;
            }
        }

    }
}
