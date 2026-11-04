using System.Diagnostics;
using System.IO;
using System.Windows;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App
{
    /// <summary>
    /// agterm's Reset Live Sessions: LiveResetCoordinator's confirm path for the action and live.reset, armLiveReset at the
    /// quit, and LiveResetConsumer at the next launch, on this machine's per-pane daemons.
    /// </summary>
    internal sealed partial class WindowLibrary
    {
        /// <summary>agterm's LiveResetCoordinator.Refusal, in the order it refuses.</summary>
        internal enum LiveResetRefusal
        {
            NotLive,
            ListingFailed,
            InventoryIncomplete,
            NothingToReset,
        }

        private static readonly TimeSpan LiveResetBudget = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan LiveResetListTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan LiveResetKillTimeout = TimeSpan.FromSeconds(5);

        /// <summary>agterm's zmx invocation timeout, for the launch reap's listing and its kill.</summary>
        private static readonly TimeSpan LiveReapTimeout = TimeSpan.FromSeconds(3);

        /// <summary>agterm's zmxOutdatedBefore: when this state directory first launched the bundled daemon build; null without one.</summary>
        private DateTime? liveOutdatedBefore;

        private LiveResetOutcome? liveResetLast;

        /// <summary>agterm's suppressedLaunchPayloads: panes whose shell outlived this launch's reset get no saved command.</summary>
        private readonly HashSet<string> suppressedLaunchPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public LiveReset.Selection? LiveResetPending { get; private set; }

        private LiveResetMarkerStore LiveResetMarkers => new LiveResetMarkerStore(DataDirectory);

        /// <summary>agterm's menuVisible: live both as configured and as this launch runs.</summary>
        public bool LiveResetMenuVisible => LiveReset.MenuVisible(Settings.EffectiveRestoreMode, RestoreStatus().Active);

        /// <summary>
        /// agterm's armablePending: the confirmed selection while live is still both modes; a mode change after confirmation
        /// leaves the next launch unable to hold back a survivor's command, so the reset is neither armed nor skips the question.
        /// </summary>
        public LiveReset.Selection? ArmableLiveReset => LiveResetMenuVisible ? LiveResetPending : null;

        public static string LiveResetMessage(LiveResetRefusal refusal) => refusal switch
        {
            LiveResetRefusal.NotLive => "live.reset requires Live sessions mode both configured and active for this launch",
            LiveResetRefusal.ListingFailed => "live.reset could not read the live session list",
            LiveResetRefusal.InventoryIncomplete => "live.reset refused: the pane inventory is incomplete",
            _ => "live.reset found no live session to reset",
        };

        private static string LiveResetUserMessage(LiveResetRefusal refusal) => refusal switch
        {
            LiveResetRefusal.NotLive => Strings.T("Для сброса живых сессий режим «живыми сессиями» нужен и в этом запуске, и в следующем."),
            LiveResetRefusal.ListingFailed => Strings.T("Не удалось прочитать список живых сессий. Ничего не сброшено."),
            LiveResetRefusal.InventoryIncomplete => Strings.T("Не удалось проверить сохранённые сессии. Ничего не сброшено."),
            _ => Strings.T("Не нашлось живых сессий для сброса."),
        };

        /// <summary>agterm's paneClaims as the reset reads it: each claim's pane and session.</summary>
        private LiveReset.ClaimWalk LiveClaimWalk(bool launch = false)
        {
            (List<LiveInventory.Claim> claims, bool complete) = PaneClaims(launch);
            return new LiveReset.ClaimWalk(claims.Select(claim => new LiveReset.Claim(claim.PaneId, claim.SessionId)).ToList(), complete);
        }

        /// <summary>
        /// agterm's paneClaims: the panes of the open windows, and of every other window's saved state, the closed ones and
        /// those the index does not name; incomplete when one cannot be read, a pane has no id, or the windows folder cannot
        /// be listed. At launch, before any window opens, it is agterm's launch pane inventory and logs as that does.
        /// </summary>
        internal (List<LiveInventory.Claim> Claims, bool Complete) PaneClaims(bool launch = false)
        {
            List<LiveInventory.Claim> claims = new List<LiveInventory.Claim>();
            bool complete = true;
            foreach (MainWindow window in open)
            {
                (List<LiveInventory.Claim> shown, bool read) = window.LiveClaims(Label(window.Entry));
                claims.AddRange(shown);
                complete &= read;
            }

            HashSet<WindowEntry> openEntries = open.Select(window => window.Entry).ToHashSet();
            List<(string File, Guid Id, string? Name, bool Stray)> files = entries.Where(entry => !openEntries.Contains(entry)).Select(entry => (entry.StateFile, entry.Id, (string?)Label(entry), false)).ToList();
            try
            {
                files.AddRange(StrayWindowFiles(entries.Select(entry => entry.StateFile).ToHashSet(StringComparer.OrdinalIgnoreCase)).Select(file => (file.StateFile, file.Id, (string?)null, true)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // An unlistable folder hides the unindexed windows entirely, so the silence must not read as "none".
                Log.Notice("WindowLibrary", launch ? "pane inventory could not enumerate the windows directory" : "live claim walk could not enumerate " + Path.Combine(DataDirectory, "windows"));
                complete = false;
            }

            foreach ((string file, Guid windowId, string? name, bool stray) in files)
            {
                string window = Path.GetFileNameWithoutExtension(file);
                if (LoadSaved(file) is not StateSnapshot saved)
                {
                    Log.Notice("WindowLibrary", launch ? "pane inventory could not read or upgrade " + (stray ? "unindexed" : "closed") + " window " + window : "live claim walk could not read window " + window);
                    complete = false;
                    continue;
                }

                // agterm's launch inventory gives a saved pane without an id one and writes it back before the reap reads it.
                if (launch && PaneIdentityInventory.Run(saved).Changed && !SaveUpgraded(file, saved))
                {
                    Log.Notice("WindowLibrary", "pane inventory could not read or upgrade " + (stray ? "unindexed" : "closed") + " window " + window);
                    complete = false;
                    continue;
                }

                foreach (WorkspaceSnapshot workspace in saved.Workspaces)
                {
                    foreach (SessionSnapshot session in workspace.Sessions)
                    {
                        // agterm's persistedName: the custom name, else the directory's last part, ~ without one.
                        string sessionName = session.Name ?? (string.IsNullOrEmpty(session.Cwd) ? "~" : Path.GetFileName(Path.TrimEndingDirectorySeparator(session.Cwd)));
                        // agterm's persistedClaims: each pane on its own, and one without an id leaves the walk incomplete.
                        foreach (PaneRole pane in session.HasSplit ? new[] { PaneRole.Main, PaneRole.Split } : new[] { PaneRole.Main })
                        {
                            if (session.PaneIds?.GetValueOrDefault(Panes.Name(pane)) is { Length: > 0 } id)
                            {
                                claims.Add(new LiveInventory.Claim(id, Panes.Name(pane), false, windowId.Wire(), name, stray ? "unindexed" : "closed", workspace.Id.Wire(), workspace.Name, session.Id.Wire(), sessionName));
                                complete &= Guid.TryParse(id, out _);
                            }
                            else
                            {
                                complete = false;
                            }
                        }
                    }
                }
            }

            return (claims, complete);
        }

        private bool SaveUpgraded(string stateFile, StateSnapshot snapshot)
        {
            try
            {
                new StateStore(Path.Combine(DataDirectory, stateFile)).Save(snapshot);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// agterm's launch reap, after the reset consumer and before any window opens a pane: the daemons no pane claims and
        /// no client shows are ended. A launch that asked for live and could not read every window's claims ends none; one
        /// that did not ask for live claims none, so every daemon without a client goes.
        /// </summary>
        private void ReapLive(LiveDaemons daemons, bool liveRequested, LiveReset.ClaimWalk claims)
        {
            HashSet<string>? claimed = null;
            LaunchRunningNames = null;
            if (liveRequested)
            {
                if (!claims.Complete)
                {
                    Log.Error("ZmxClient", "skipping live reap because the persisted pane inventory is incomplete");
                    return;
                }

                claimed = claims.Claims.Select(claim => claim.PaneIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            Task<List<LivePaneInfo>> listing = Task.Run(daemons.ListAsync);
            if (!listing.Wait(LiveReapTimeout))
            {
                Log.Error("ZmxClient", "live list failed during launch reap: timed out");
                return;
            }

            LaunchRunningNames = listing.Result.Select(shell => shell.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> names = LiveReap.NamesToKill(listing.Result.Select(shell => new LiveReap.Listed(shell.Id, shell.Clients)), liveRequested, claimed)!;
            if (names.Count > 0)
            {
                daemons.StopBatch(names, LiveReapTimeout);
            }
        }

        /// <summary>
        /// The daemons as the reset reads them; null when the listing failed or did not finish in time. One that did not answer
        /// stays in it without a leader, as zmx list shows it with clients unread, so its pane is skipped rather than gone.
        /// </summary>
        private static List<LiveReset.Daemon>? LiveResetDaemons(LiveDaemons daemons, TimeSpan timeout)
        {
            Task<List<(string PaneId, List<LivePaneInfo>? Shells)>?> listing = Task.Run(daemons.RecordsAsync);
            if (!listing.Wait(timeout) || listing.Result is not { } records)
            {
                return null;
            }

            return records.SelectMany(record => record.Shells is null
                ? new[] { new LiveReset.Daemon(record.PaneId, 0, null) }
                : record.Shells.Select(shell => new LiveReset.Daemon(shell.Id, shell.Pid, shell.Created))).ToList();
        }

        /// <summary>agterm's LiveResetCoordinator.request: refuses in a fixed order, else the selection to confirm.</summary>
        private async Task<(LiveResetRefusal? Refusal, LiveReset.Selection? Selection)> SelectLiveResetAsync()
        {
            if (!LiveResetMenuVisible || Live is not LiveDaemons daemons)
            {
                return (LiveResetRefusal.NotLive, null);
            }

            List<LiveReset.Daemon>? listed = await Task.Run(() => LiveResetDaemons(daemons, LiveResetListTimeout));
            if (listed is null)
            {
                return (LiveResetRefusal.ListingFailed, null);
            }

            LiveReset.Selection selection = LiveReset.Select(LiveClaimWalk(), listed, liveOutdatedBefore);
            if (!selection.InventoryComplete)
            {
                return (LiveResetRefusal.InventoryIncomplete, null);
            }

            return selection.Targets.Count == 0 ? (LiveResetRefusal.NothingToReset, null) : (null, selection);
        }

        /// <summary>agterm's zmx.reset: the action's confirm path without the dialog. The quit follows once this reply is written.</summary>
        private async Task<ControlResponse> LiveResetCommandAsync(ControlRequest request)
        {
            if (request.Args?.Force != true)
            {
                return Fail("--force is required to end the processes in the affected live sessions");
            }

            (LiveResetRefusal? refusal, LiveReset.Selection? selection) = await SelectLiveResetAsync();
            if (refusal is not null || selection is null)
            {
                return Fail(LiveResetMessage(refusal ?? LiveResetRefusal.NothingToReset));
            }

            LiveResetPending = selection;
            int outdated = selection.OutdatedSessionCount;
            return Ok(new ControlResult
            {
                Text = Strings.InEnglish(() => LiveReset.DialogBody(selection.SessionCount, outdated)),
                LiveReset = new ControlLiveResetStatus { Sessions = selection.SessionCount, Panes = selection.Targets.Count, Pending = true, Outdated = outdated > 0 ? outdated : null },
            });
        }

        /// <summary>agterm's terminateIfPending, after live.reset's reply is on the wire.</summary>
        public void TerminateIfLiveResetPending()
        {
            if (ArmableLiveReset is not null && !Terminating)
            {
                QuitNow();
            }
        }

        /// <summary>agterm's runFromMenu: a refusal is shown, a cancel is silent, a confirmation quits.</summary>
        public async void ResetLiveFromMenu(MainWindow from)
        {
            (LiveResetRefusal? refusal, LiveReset.Selection? selection) = await SelectLiveResetAsync();
            if (refusal is not null || selection is null)
            {
                MessageBox.Show(from, Strings.T("Сбросить живые сессии") + "\n\n" + LiveResetUserMessage(refusal ?? LiveResetRefusal.NothingToReset), "AgShell", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            WindowClosePrompt.Show(from, LiveReset.DialogTitle, LiveReset.DialogBody(selection.SessionCount, selection.OutdatedSessionCount), confirmed =>
            {
                if (confirmed && !Terminating)
                {
                    LiveResetPending = selection;
                    TerminateIfLiveResetPending();
                }
            }, Strings.T("Сбросить"));
        }

        /// <summary>agterm's liveResetReadback: null when nothing is pending and this launch consumed no marker.</summary>
        public ControlLiveResetReadback? LiveResetReadback()
        {
            int? pending = LiveResetPending?.Targets.Count;
            return pending is null && liveResetLast is null ? null : new ControlLiveResetReadback { Pending = pending, Last = liveResetLast };
        }

        public bool IsOutdatedLive(DateTime? created) => LiveReset.IsOutdated(created, liveOutdatedBefore);

        /// <summary>Whether a pane's saved command is held back at this launch: its shell outlived the reset.</summary>
        public bool TakeSuppressedLaunch(string paneId) => suppressedLaunchPayloads.Remove(paneId);

        /// <summary>Whether a pane's saved command is held back at this launch, without spending the mark (the pacer asks).</summary>
        public bool IsSuppressedLaunch(string paneId) => suppressedLaunchPayloads.Contains(paneId);

        /// <summary>
        /// agterm's ReapOutcome.runningNames: every daemon the launch reap listed, alive with or without a client; null when
        /// the reap was skipped or its listing failed. A pane whose daemon is in it attaches and runs no program.
        /// </summary>
        public IReadOnlySet<string>? LaunchRunningNames { get; private set; }

        /// <summary>
        /// agterm's armLiveReset: writes the marker, then starts the relauncher; a relauncher that cannot start takes the marker
        /// with it, so a reset is never armed for a launch nobody starts.
        /// </summary>
        private void ArmLiveReset(LiveReset.Selection selection)
        {
            LiveResetMarkerStore store = LiveResetMarkers;
            try
            {
                store.Write(new LiveReset.Marker { CreatedAt = Math.Floor((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds), Targets = selection.Targets.ToList() });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Error("AppDelegate", "live sessions reset not armed: marker write failed: " + ex.Message);
                return;
            }

            if (!SpawnRelauncher())
            {
                store.Remove();
                Log.Error("AppDelegate", "live sessions reset not armed: the relauncher did not start");
            }
        }

        /// <summary>
        /// agterm's LiveResetRelauncher: a new agshell.exe waits for this process to exit, then launches as usual on the same
        /// state directory, which it inherits through the environment.
        /// </summary>
        /// <summary>The relauncher waits for this process to end, so it leaves the app's job as agterm's disclaims.</summary>
        private static bool SpawnRelauncher()
        {
            string program = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "agshell.exe");
            if (DetachedProcess.Start(program, Program.RelaunchAfter, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is string error)
            {
                Log.Error("LiveResetRelauncher", "could not start the relauncher: " + error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// agterm's LiveResetConsumer, before any window opens: the marker is consumed before anything ends, re-checked against
        /// this launch's claims and listing so it only narrows, the confirmed daemons are told to end at once, and their leaders
        /// are waited for. A launch that did not get live discards the marker without ending anything.
        /// </summary>
        private void ConsumeLiveReset(LiveDaemons? daemons, LiveReset.ClaimWalk claims)
        {
            LiveResetMarkerStore store = LiveResetMarkers;
            if (daemons is null)
            {
                store.Remove();
                return;
            }

            LiveReset.Marker? marker;
            try
            {
                marker = store.Consume();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Log.Error("LiveResetConsumer", "live sessions reset marker discarded: " + ex.Message);
                return;
            }

            if (marker is null)
            {
                return;
            }

            Stopwatch clock = Stopwatch.StartNew();
            TimeSpan Remaining() => LiveResetBudget - clock.Elapsed > TimeSpan.Zero ? LiveResetBudget - clock.Elapsed : TimeSpan.Zero;
            HashSet<string>? claimed = claims.Complete ? claims.Claims.Select(claim => claim.PaneIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            TimeSpan listTimeout = Remaining() < LiveResetListTimeout ? Remaining() : LiveResetListTimeout;
            List<LiveReset.Daemon>? listed = LiveResetDaemons(daemons, listTimeout > TimeSpan.FromMilliseconds(100) ? listTimeout : TimeSpan.FromMilliseconds(100));
            LiveReset.Narrowed narrowed = LiveReset.Narrow(marker, claimed, listed, liveOutdatedBefore);
            IReadOnlyList<LiveReset.Target> kill = narrowed.Kill;
            HashSet<int> survivors = new HashSet<int>();
            if (kill.Count > 0)
            {
                TimeSpan timeout = Remaining() < LiveResetKillTimeout ? Remaining() : LiveResetKillTimeout;
                if (timeout <= TimeSpan.Zero)
                {
                    // A batch that could not start before the budget ran out is one whose result is unknown: every selected
                    // leader stays held back rather than restored.
                    Log.Error("LiveResetConsumer", "live sessions reset: the budget expired before the kill; leaving every selected pane suppressed");
                    survivors = kill.Select(target => target.LeaderPid).ToHashSet();
                }
                else
                {
                    if (!daemons.StopBatch(kill.Select(target => target.Daemon).ToList(), timeout))
                    {
                        Log.Error("LiveResetConsumer", "live sessions reset: the batched kill did not complete; polling every leader anyway");
                    }

                    survivors = LeadersAlive(kill.Select(target => target.LeaderPid).ToHashSet(), Remaining());
                }
            }

            LiveResetOutcome outcome = LiveReset.Outcome(narrowed, survivors, narrowed.InventoryFailed);
            suppressedLaunchPayloads.UnionWith(outcome.Unconfirmed);
            store.RemoveConsumed();
            liveResetLast = outcome;
            Log.Info("LiveResetConsumer", "live sessions reset: " + outcome.Panes.Killed + " killed, " + outcome.Panes.Gone + " gone, " + outcome.Panes.Skipped + " skipped, " + outcome.Unconfirmed.Count + " unconfirmed");
        }

        /// <summary>agterm's leadersExited: polls the leaders until each has gone or the time is up; returns those still running.</summary>
        private static HashSet<int> LeadersAlive(HashSet<int> leaders, TimeSpan within)
        {
            Stopwatch clock = Stopwatch.StartNew();
            HashSet<int> alive = new HashSet<int>(leaders);
            while (true)
            {
                alive.RemoveWhere(pid => !IsAlive(pid));
                if (alive.Count == 0 || clock.Elapsed >= within)
                {
                    return alive;
                }

                Thread.Sleep(50);
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                // A process that cannot be opened is still there, as agterm counts EPERM alive.
                return true;
            }
        }

        /// <summary>agterm's notifyLiveResetOutcome: a banner only when the reset could not read the list or covered part of the sessions.</summary>
        private void NotifyLiveResetOutcome()
        {
            if (liveResetLast is LiveResetOutcome outcome && LiveReset.NotificationText(outcome) is string body)
            {
                NotifyApp("live-reset", Strings.T("Живые сессии"), body);
            }
        }
    }
}
