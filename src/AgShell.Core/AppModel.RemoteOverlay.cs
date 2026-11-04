using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's overlay job: an overlay program handed to the machine presenting the session. That machine shows it in a
    /// terminal running ssh back to agshellctl session overlay run-job, which runs the program once here, its connection
    /// open until it reports how the program ended. The slot's result waits for it.
    /// </summary>
    public sealed class OverlayJob
    {
        /// <summary>Unclaimed until a helper picks it up, then claimed, running, and finished.</summary>
        public const string Unclaimed = "unclaimed";
        public const string Claimed = "claimed";
        public const string Running = "running";
        public const string Finished = "finished";

        public required string Id { get; init; }

        public required Guid SessionId { get; init; }

        /// <summary>Follows its pane across a swap, and to the left when the split is promoted.</summary>
        public required PaneRole Slot { get; set; }

        /// <summary>The presenter generation it was handed to.</summary>
        public required int Owner { get; init; }

        public required string Command { get; init; }

        public string? Cwd { get; init; }

        public bool Wait { get; init; }

        public bool Follow { get; init; }

        /// <summary>What the helper runs, fixed when the overlay opened, as agterm builds its launch context then.</summary>
        public ControlOverlayJob? Launch { get; init; }

        public int? SizePercent { get; internal set; }

        /// <summary>The presenter's surface for it is gone.</summary>
        public bool SurfaceClosed { get; internal set; }

        /// <summary>Closed here while the presenter's surface held the result.</summary>
        public bool Dismissed { get; internal set; }

        /// <summary>
        /// The job ended, however it ended, and a --wait surface on the presenter still shows it: the slot stays held until
        /// that surface goes, as agterm's.
        /// </summary>
        public bool Held => State == Finished && Wait && WasClaimed && !Abandoned && !SurfaceClosed && !Dismissed;

        /// <summary>A helper took it, so a surface on the presenter showed it; one never claimed has none to hold the slot for.</summary>
        public bool WasClaimed { get; internal set; }

        public string? BackgroundColor { get; init; }

        public string State { get; internal set; } = Unclaimed;

        /// <summary>Unclaimed or claimed past this, the job ends: launch-failed or unknown.</summary>
        public DateTimeOffset Deadline { get; internal set; }

        public bool CancelRequested { get; internal set; }

        /// <summary>Its pane closed or restarted here: the slot is free at once and a late outcome is not recorded.</summary>
        public bool Abandoned { get; internal set; }

        public string? Failure { get; internal set; }

        public int? ExitCode { get; internal set; }
    }

    public sealed partial class AppModel
    {
        /// <summary>An ssh connect and login on the presenting machine, with room to spare.</summary>
        public static readonly TimeSpan JobLaunchWindow = TimeSpan.FromSeconds(30);

        /// <summary>Claim to started: the helper spawning the program.</summary>
        public static readonly TimeSpan JobStartWindow = TimeSpan.FromSeconds(10);

        private readonly List<OverlayJob> overlayJobs = new List<OverlayJob>();

        private const int FinishedJobsKept = 32;

        /// <summary>
        /// Hands a new overlay program to the session's presenter under the same rule as a question: this machine follows
        /// another's lead in the slot's pane, or in every pane for the session-wide slot. True when handed.
        /// </summary>
        public bool CanHandOverlayOver(Guid sessionId, PaneRole slot, Func<PaneRole, string?> lead)
        {
            if (!presenters.ContainsKey(sessionId) || FindSession(sessionId) is not Session session)
            {
                return false;
            }

            PaneRole[] panes = slot switch
            {
                PaneRole.OverlayLeft => new[] { PaneRole.Main },
                PaneRole.OverlayRight => new[] { PaneRole.Split },
                _ => session.HasSplit ? new[] { PaneRole.Main, PaneRole.Split } : new[] { PaneRole.Main },
            };
            return panes.All(role => lead(role) == "follower");
        }

        /// <summary>The slots held for the presenter's overlays, for tree; null when none.</summary>
        public List<ControlRemoteOverlayNode>? RemoteOverlaysOf(Guid sessionId)
        {
            List<ControlRemoteOverlayNode> held = overlayJobs
                .Where(job => job.SessionId == sessionId && !job.Abandoned && (job.State != OverlayJob.Finished || job.Held))
                .Select(job => new ControlRemoteOverlayNode
                {
                    Pane = job.Slot switch { PaneRole.OverlayLeft => "left", PaneRole.OverlayRight => "right", _ => null },
                    SizePercent = job.SizePercent,
                })
                .Concat(RemotePagesOf(sessionId).Select(page => new ControlRemoteOverlayNode
                {
                    Pane = page.Slot switch { PaneRole.OverlayLeft => "left", PaneRole.OverlayRight => "right", _ => null },
                    SizePercent = page.SizePercent,
                    PageId = page.Id,
                }))
                .ToList();
            return held.Count == 0 ? null : held;
        }

        public bool HandOverlayOver(Guid sessionId, SessionOverlay overlay, PaneRole slot, Func<PaneRole, string?> lead, DateTimeOffset now, bool follow = false, ControlOverlayJob? launch = null)
        {
            if (!CanHandOverlayOver(sessionId, slot, lead) || FindSession(sessionId) is not Session session || overlay.Command is not string command)
            {
                return false;
            }

            if (SlotJob(sessionId, slot) is OverlayJob previous)
            {
                CloseJob(previous);
            }

            // A HUD gives way to it, as agterm's remote open replaces a panel; a program here was refused by the caller.
            CloseOverlay(sessionId, slot: slot);
            overlayJobs.Add(new OverlayJob
            {
                Id = overlay.Id,
                SessionId = sessionId,
                Slot = slot,
                Owner = PresenterGeneration(sessionId),
                Command = command,
                Cwd = overlay.Cwd,
                Wait = overlay.Wait,
                SizePercent = overlay.SizePercent,
                BackgroundColor = overlay.BackgroundColor,
                Follow = follow,
                Launch = launch,
                Deadline = now + JobLaunchWindow,
            });
            session.OverlayResults[slot] = new OverlayResult(overlay.Id, null) { Remote = true };
            TreeChanged?.Invoke();
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// agterm: the slot of an overlay running on the presenter stays reserved until its job ends, and after that while a
        /// --wait surface there still shows the result.
        /// </summary>
        public bool OverlaySlotReserved(Guid sessionId, PaneRole slot)
        {
            return SlotJob(sessionId, slot) is not null || RemotePageAt(sessionId, slot) is not null;
        }

        /// <summary>A program, not a page, runs for the slot on the presenter: what cannot be hidden or read from here.</summary>
        public bool OverlayJobReserved(Guid sessionId, PaneRole slot)
        {
            return SlotJob(sessionId, slot) is not null;
        }

        private OverlayJob? SlotJob(Guid sessionId, PaneRole slot)
        {
            return overlayJobs.FirstOrDefault(job => job.SessionId == sessionId && job.Slot == slot && !job.Abandoned && (job.State != OverlayJob.Finished || job.Held));
        }

        /// <summary>The origin swapped its panes: each pane's slot goes with it.</summary>
        private void SwapOverlayJobSlots(Guid sessionId)
        {
            foreach (OverlayJob job in overlayJobs.Where(job => job.SessionId == sessionId))
            {
                job.Slot = Swapped(job.Slot);
            }

            foreach (RemotePage page in remotePages.Where(page => page.SessionId == sessionId))
            {
                page.Slot = Swapped(page.Slot);
            }
        }

        private static PaneRole Swapped(PaneRole slot)
        {
            return slot switch
            {
                PaneRole.OverlayLeft => PaneRole.OverlayRight,
                PaneRole.OverlayRight => PaneRole.OverlayLeft,
                PaneRole other => other,
            };
        }

        /// <summary>
        /// The split took the main pane's place: as agterm's dropRemoteOverlay, the main pane's job leaves its slot at once,
        /// before the split's moves left.
        /// </summary>
        private void PromoteOverlayJobSlots(Guid sessionId)
        {
            EndOverlayJobsOf(sessionId, PaneRole.OverlayLeft);
            foreach (OverlayJob job in overlayJobs.Where(job => job.SessionId == sessionId && job.Slot == PaneRole.OverlayRight))
            {
                job.Slot = PaneRole.OverlayLeft;
            }

            foreach (RemotePage page in remotePages.Where(page => page.SessionId == sessionId && page.Slot == PaneRole.OverlayRight))
            {
                page.Slot = PaneRole.OverlayLeft;
            }
        }

        /// <summary>
        /// session overlay resize on a session-wide slot whose overlay runs on the presenter: it is resized there. Null when no
        /// such overlay; false when the stream it was handed to is gone, even if another presents now.
        /// </summary>
        public bool? ResizeOverlayJob(Guid sessionId, int? sizePercent)
        {
            if (SlotJob(sessionId, PaneRole.Overlay) is not OverlayJob job)
            {
                return null;
            }

            if (!presenters.ContainsKey(sessionId) || job.Owner != PresenterGeneration(sessionId))
            {
                return false;
            }

            job.SizePercent = sizePercent;
            return true;
        }

        /// <summary>
        /// Ends the jobs of a session leaving, or of one slot whose pane closed here: null for all. As agterm drops a pane's
        /// slot with the pane, the slot is free at once, its result reads canceled, and the helper hears cancel.
        /// </summary>
        public void EndOverlayJobsOf(Guid sessionId, PaneRole? slot)
        {
            EndRemotePagesOf(sessionId, slot);
            List<OverlayJob> ending = overlayJobs.Where(job => job.SessionId == sessionId && (slot is null || job.Slot == slot) && !job.Abandoned && (job.State != OverlayJob.Finished || job.Held)).ToList();
            foreach (OverlayJob job in ending)
            {
                if (job.Held)
                {
                    job.Dismissed = true;
                    continue;
                }

                if (FindSessionOrPending(sessionId) is Session session && session.OverlayResults.GetValueOrDefault(job.Slot)?.Id == job.Id)
                {
                    session.OverlayResults[job.Slot] = new OverlayResult(job.Id, null) { Remote = true, Failure = "canceled" };
                }

                job.Abandoned = true;
                CancelJob(job);
            }

            if (ending.Count > 0)
            {
                TreeChanged?.Invoke();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// agterm's clearPaneOwnedState, for a pane whose shell is replaced in place: what its program owned goes, the HUD
        /// drawn over it, its question, its own overlay, the status it set and the job over it. The pane itself stays.
        /// </summary>
        public void ClearPaneOwnedState(Guid sessionId, PaneRole pane)
        {
            Session session = RequireSession(sessionId);
            if (session.Overlay is { IsHud: true, HudPane: PaneRole anchor } hud && anchor == pane)
            {
                CloseOverlay(sessionId, hud.Id);
            }

            if (terminalAsks.GetValueOrDefault(sessionId) is AskRequest ask && ask.Pane == pane)
            {
                CancelAsk(ask.Id);
            }

            if (session.OverlayMap.ContainsKey(Panes.OverlayOf(pane)))
            {
                CloseOverlay(sessionId, slot: Panes.OverlayOf(pane));
            }

            ClearStatusOwnedBy(session, pane);
            EndOverlayJobsOfPane(sessionId, pane);
            Changed?.Invoke();
        }

        /// <summary>
        /// A pane's shell restarts here: as agterm's clearPaneOwnedState, its own slot's job is closed, a held result taken
        /// down and a running job asked to cancel. The pane stays, so the slot holds until the helper reports how it ended.
        /// </summary>
        public void EndOverlayJobsOfPane(Guid sessionId, PaneRole pane)
        {
            EndRemotePagesOf(sessionId, pane == PaneRole.Split ? PaneRole.OverlayRight : PaneRole.OverlayLeft);
            if (SlotJob(sessionId, pane == PaneRole.Split ? PaneRole.OverlayRight : PaneRole.OverlayLeft) is not OverlayJob job)
            {
                return;
            }

            // agterm's surfaceGone: no result is shown for it any more, so once it ends the slot is free.
            job.SurfaceClosed = true;
            CloseJob(job);
            TreeChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>The window is closing: every overlay it handed out ends.</summary>
        public void EndAllOverlayJobs()
        {
            foreach (Guid sessionId in overlayJobs.Select(job => job.SessionId).Distinct().ToList())
            {
                EndOverlayJobsOf(sessionId, null);
            }
        }

        /// <summary>Whether this window holds the job, for routing a helper whose session may be gone.</summary>
        public bool HasOverlayJob(string id)
        {
            return overlayJobs.Any(job => job.Id == id);
        }

        public OverlayJob? OverlayJobById(string? id)
        {
            return overlayJobs.FirstOrDefault(job => job.Id == id);
        }

        /// <summary>
        /// The presenter's surface for the job is gone: one never claimed ends canceled, one running ends unknown (its program
        /// went with the ssh terminal), and a held result frees its slot.
        /// </summary>
        public bool OverlaySurfaceClosed(Guid sessionId, string presenter, ControlPresentationFrame frame)
        {
            if (!presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) || holder.Token != presenter
                || OverlayJobOf(sessionId, frame.Id) is not OverlayJob job || job.Owner != frame.Owner)
            {
                return false;
            }

            // agterm's surfaceGone: a running job keeps its slot, how it ended being the helper's to report; one never
            // claimed waits out its launch deadline and ends launch-failed. Either way no result is held for that surface.
            job.SurfaceClosed = true;
            TreeChanged?.Invoke();
            Changed?.Invoke();

            return true;
        }

        public OverlayJob? OverlayJobOf(Guid sessionId, string? id)
        {
            return overlayJobs.FirstOrDefault(job => job.Id == id && job.SessionId == sessionId);
        }

        private OverlayJob? ActiveOverlayJob(Guid sessionId, PaneRole slot)
        {
            return overlayJobs.FirstOrDefault(job => job.SessionId == sessionId && job.Slot == slot && job.State != OverlayJob.Finished);
        }

        /// <summary>The jobs handed to the presenter of this generation, for its stream: a failed one is taken down there.</summary>
        private List<ControlPresentationOverlay>? PresentationOverlays(Guid sessionId, int generation)
        {
            List<ControlPresentationOverlay> open = overlayJobs
                .Where(job => job.SessionId == sessionId && job.Owner == generation)
                .Select(job => new ControlPresentationOverlay
                {
                    Job = job.Id,
                    Owner = job.Owner,
                    Pane = FindSessionOrPending(job.SessionId) is Session session ? PresentationPaneOf(session, job.Slot) : null,
                    SizePercent = job.SizePercent,
                    BackgroundColor = job.BackgroundColor,
                    Wait = job.Wait,
                    Follow = job.Follow,
                    // An abandoned job is over for the presenter too: its surface comes down while the helper cancels.
                    Failure = job.Abandoned ? job.Failure ?? "canceled" : job.Failure,
                    ExitCode = job.ExitCode,
                    Dismissed = job.Dismissed,
                })
                .ToList();
            return open.Count == 0 ? null : open;
        }

        /// <summary>A helper takes the job; once only, and only before the launch deadline.</summary>
        public bool ClaimOverlayJob(OverlayJob job, DateTimeOffset now)
        {
            ExpireOverlayJobs(now);
            if (job.State != OverlayJob.Unclaimed)
            {
                return false;
            }

            job.State = OverlayJob.Claimed;
            job.WasClaimed = true;
            job.Deadline = now + JobStartWindow;
            return true;
        }

        /// <summary>
        /// agterm's started: once running, no timeout can end the job; a start reported after the claim's deadline is too
        /// late and ends it unknown, as the expiry would have.
        /// </summary>
        public void OverlayJobStarted(OverlayJob job, DateTimeOffset now)
        {
            if (job.State != OverlayJob.Claimed)
            {
                return;
            }

            if (now >= job.Deadline)
            {
                FinishOverlayJob(job, null, "unknown");
                return;
            }

            job.State = OverlayJob.Running;
        }

        /// <summary>Whether the job's helper is to stop its program: it was cancelled, or ended here another way.</summary>
        public static bool OverlayJobStopping(OverlayJob job)
        {
            return job.CancelRequested || job.State == OverlayJob.Finished;
        }

        /// <summary>agterm's helperGone: the helper's connection closed with nothing reported, so how it ended is unknown.</summary>
        public void OverlayJobHelperGone(OverlayJob job)
        {
            FinishOverlayJob(job, null, "unknown");
        }

        /// <summary>The job's outcome: an exit status, or a failure name. Only the first counts.</summary>
        public void FinishOverlayJob(OverlayJob job, int? exitCode, string? failure)
        {
            if (job.State == OverlayJob.Finished)
            {
                return;
            }

            job.State = OverlayJob.Finished;
            job.Failure = failure;
            job.ExitCode = exitCode;
            if (!job.Abandoned && FindSession(job.SessionId) is Session session && session.OverlayResults.GetValueOrDefault(job.Slot)?.Id == job.Id)
            {
                session.OverlayResults[job.Slot] = new OverlayResult(job.Id, exitCode) { Remote = true, Failure = failure };
            }

            // agterm keeps the last 32 finished jobs, so a late report finds its job rather than an unknown id.
            List<OverlayJob> finished = overlayJobs.Where(other => other.State == OverlayJob.Finished && !other.Held).ToList();
            foreach (OverlayJob old in finished.Take(Math.Max(0, finished.Count - FinishedJobsKept)))
            {
                overlayJobs.Remove(old);
            }

            TreeChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>session overlay close on a slot whose program runs elsewhere: an unclaimed job ends, a running one is asked to.</summary>
        public bool CancelOverlayJob(Guid sessionId, PaneRole slot)
        {
            if (SlotJob(sessionId, slot) is not OverlayJob job)
            {
                return false;
            }

            CloseJob(job);
            return true;
        }

        /// <summary>Ends a job, or for a held result asks the presenter to take its surface down and frees the slot.</summary>
        private void CloseJob(OverlayJob job)
        {
            if (job.Held)
            {
                job.Dismissed = true;
                TreeChanged?.Invoke();
                Changed?.Invoke();
                return;
            }

            CancelJob(job);
        }

        private void CancelJob(OverlayJob job)
        {
            if (job.State == OverlayJob.Unclaimed)
            {
                job.SurfaceClosed = true;
                FinishOverlayJob(job, null, "canceled");
            }
            else
            {
                // agterm's close tells the presenter at once and gives up the hold, so the slot frees however the program
                // ends, even by itself before the helper hears cancel.
                job.CancelRequested = true;
                job.SurfaceClosed = true;
                job.Dismissed = true;
                TreeChanged?.Invoke();
                Changed?.Invoke();
            }
        }

        /// <summary>The presenter could not show it: the overlay fails rather than opening here.</summary>
        public bool RejectOverlayJob(Guid sessionId, string presenter, ControlPresentationFrame answer)
        {
            if (!presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) || holder.Token != presenter
                || OverlayJobOf(sessionId, answer.Id) is not OverlayJob job || job.Owner != answer.Owner || job.State != OverlayJob.Unclaimed)
            {
                return false;
            }

            FinishOverlayJob(job, null, "launch-failed");
            return true;
        }

        /// <summary>Ends jobs past their deadline, unclaimed or claimed and never started; the window calls it on a timer.</summary>
        public void ExpireOverlayJobs(DateTimeOffset now)
        {
            EndOrphanedRemotePages(now);
            foreach (OverlayJob job in overlayJobs.ToList())
            {
                if (job.State == OverlayJob.Unclaimed && (now >= job.Deadline || !presenters.ContainsKey(job.SessionId)))
                {
                    FinishOverlayJob(job, null, "launch-failed");
                }
                else if (job.State == OverlayJob.Claimed && now >= job.Deadline)
                {
                    FinishOverlayJob(job, null, "unknown");
                }
                else if (job.Held && job.Owner != PresenterGeneration(job.SessionId))
                {
                    // The presenter that showed the result is gone, and its surface with it.
                    job.SurfaceClosed = true;
                    TreeChanged?.Invoke();
                    Changed?.Invoke();
                }
            }
        }
    }
}
