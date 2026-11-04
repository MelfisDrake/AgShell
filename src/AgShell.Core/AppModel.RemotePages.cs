using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's remote page: a web page opened here on a session another machine presents, shown on that machine. It loads
    /// there with that machine's network and storage; this machine keeps its slot, its outcome and what it last reported.
    /// </summary>
    public sealed class RemotePage
    {
        public required string Id { get; init; }

        public required Guid SessionId { get; init; }

        /// <summary>Follows its pane across a swap, and to the left when the split is promoted.</summary>
        public required PaneRole Slot { get; set; }

        /// <summary>The presenter generation it was handed to.</summary>
        public required int Owner { get; init; }

        public required PageSpec Spec { get; init; }

        /// <summary>agterm's single-use token: session overlay page-serve claims a file page's channel with it once, within the claim window.</summary>
        public string? Token { get; init; }

        public DateTimeOffset ClaimDeadline { get; init; }

        /// <summary>Its channel was claimed: the token is spent.</summary>
        public bool Claimed { get; internal set; }

        public int? SizePercent { get; internal set; }

        public string? BackgroundColor { get; init; }

        public bool Follow { get; init; }

        /// <summary>The visibility asked for here; the presenter's report says what it shows.</summary>
        public bool HiddenRequested { get; internal set; }

        /// <summary>Closed here: the presenter is told, and the slot is free.</summary>
        public bool Closed { get; internal set; }

        /// <summary>Over for good: closed here or there, refused there, or its presenter gone.</summary>
        public bool Ended { get; internal set; }

        public List<ControlPresentationPageCommand> Commands { get; } = new List<ControlPresentationPageCommand>();

        public ControlPresentationPageState? Reported { get; internal set; }
    }

    public sealed partial class AppModel
    {
        private readonly List<RemotePage> remotePages = new List<RemotePage>();

        /// <summary>agterm's negotiated kinds: what each stream's hello said it speaks, by its token.</summary>
        private readonly Dictionary<string, HashSet<string>> streamKinds = new Dictionary<string, HashSet<string>>();

        /// <summary>A page handed over ended, however: its channel, if one was claimed, closes.</summary>
        public event Action<RemotePage>? RemotePageEnded;

        /// <summary>The kinds the stream with this token speaks, from its hello.</summary>
        public void SetStreamKinds(string token, IEnumerable<string> kinds)
        {
            streamKinds[token] = kinds.ToHashSet();
        }

        /// <summary>agterm's presenterSupports: whether the session's presenter listed the kind in its hello.</summary>
        public bool PresenterSupports(Guid sessionId, string kind)
        {
            return presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) && streamKinds.TryGetValue(holder.Token, out HashSet<string>? kinds) && kinds.Contains(kind);
        }

        /// <summary>Whether the session's presenter speaks every page kind, as agterm hands a page only to one that does.</summary>
        public bool PresenterShowsPages(Guid sessionId)
        {
            return ControlPresentationKinds.PageKinds.All(kind => PresenterSupports(sessionId, kind));
        }

        /// <summary>The page this machine shows on the presenter in the slot, while it is open.</summary>
        public RemotePage? RemotePageAt(Guid sessionId, PaneRole slot)
        {
            return remotePages.FirstOrDefault(page => page.SessionId == sessionId && page.Slot == slot && !page.Ended);
        }

        public IEnumerable<RemotePage> RemotePagesOf(Guid sessionId)
        {
            return remotePages.Where(page => page.SessionId == sessionId && !page.Ended);
        }

        /// <summary>
        /// Hands a new web page to the session's presenter, as a program overlay goes there: a HUD in the slot gives way, the
        /// slot is held for it, and its outcome waits here, pending. False, with nothing changed, for a request too large for
        /// one presentation frame: agterm refuses it, since a frame over the cap ends the presenter's stream.
        /// </summary>
        public bool HandPageOver(Guid sessionId, SessionOverlay overlay, PaneRole slot, bool follow, DateTimeOffset? now = null)
        {
            Session session = RequireSession(sessionId);
            RemotePage handed = new RemotePage
            {
                Id = overlay.Id,
                SessionId = sessionId,
                Slot = slot,
                Owner = PresenterGeneration(sessionId),
                Spec = overlay.Page!,
                Token = overlay.Page!.IsFile ? Ids.New() : null,
                ClaimDeadline = (now ?? DateTimeOffset.UtcNow) + JobLaunchWindow,
                SizePercent = overlay.SizePercent,
                BackgroundColor = overlay.BackgroundColor,
                Follow = follow,
            };
            ControlPresentationFrame request = new ControlPresentationFrame { Kind = ControlPresentationKinds.PageRequest, Page = PresentationPageOf(handed) with { Commands = null } };
            if (System.Text.Encoding.UTF8.GetByteCount(ControlJson.Serialize(request)) > PageServe.MaxFrameBytes)
            {
                return false;
            }

            CloseOverlay(sessionId, slot: slot);
            remotePages.Add(handed);
            session.OverlayResults[slot] = new OverlayResult(overlay.Id, null) { IsPage = true, Remote = true };
            PageOutcomes.Register(overlay.Id);
            TreeChanged?.Invoke();
            Changed?.Invoke();
            return true;
        }

        /// <summary>A page closed here: the presenter takes it down, and it ends unanswered.</summary>
        public void CloseRemotePage(RemotePage page)
        {
            page.Closed = true;
            EndRemotePage(page, "dismissed", null);
        }

        /// <summary>overlay submit here on a page shown there: it is answered and closes, as a local page does.</summary>
        public void SubmitRemotePage(RemotePage page, string value)
        {
            page.Closed = true;
            EndRemotePage(page, "submitted", value);
        }

        /// <summary>A reload, history step or browser hand-off for the presenter to run on the page.</summary>
        public void QueuePageCommand(RemotePage page, string action)
        {
            page.Commands.Add(new ControlPresentationPageCommand { Seq = page.Commands.Count + 1, Action = action });
            Changed?.Invoke();
        }

        public void ResizeRemotePage(RemotePage page, int? sizePercent)
        {
            page.SizePercent = sizePercent;
            Changed?.Invoke();
        }

        /// <summary>From the presenter: the page closed there, or it could not show it. Either way it ends unanswered.</summary>
        public bool RemotePageGone(Guid sessionId, string presenter, ControlPresentationFrame frame)
        {
            if (FromPresenter(sessionId, presenter, frame) is not RemotePage page)
            {
                return false;
            }

            EndRemotePage(page, "dismissed", null);
            return true;
        }

        /// <summary>From the presenter: what the page shows now.</summary>
        public bool RemotePageState(Guid sessionId, string presenter, ControlPresentationFrame frame)
        {
            if (FromPresenter(sessionId, presenter, frame) is not RemotePage page || frame.PageState is null)
            {
                return false;
            }

            bool hiddenChanged = page.Reported?.Hidden != frame.PageState.Hidden;
            page.Reported = frame.PageState;
            // Hidden there by its own strip or keys: what is asked for here follows, so a toggle here starts from it.
            page.HiddenRequested = frame.PageState.Hidden;
            if (hiddenChanged)
            {
                TreeChanged?.Invoke();
            }

            return true;
        }

        private RemotePage? FromPresenter(Guid sessionId, string presenter, ControlPresentationFrame frame)
        {
            return presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) && holder.Token == presenter
                ? remotePages.FirstOrDefault(page => page.SessionId == sessionId && page.Id == frame.Id && page.Owner == frame.Owner && !page.Ended)
                : null;
        }

        /// <summary>Ends the pages of a session leaving, or of one slot whose pane closed here: null for all.</summary>
        public void EndRemotePagesOf(Guid sessionId, PaneRole? slot)
        {
            foreach (RemotePage page in remotePages.Where(page => page.SessionId == sessionId && !page.Ended && (slot is null || page.Slot == slot)).ToList())
            {
                CloseRemotePage(page);
            }
        }

        /// <summary>
        /// agterm's claim: the file page the token was handed out for, unclaimed, inside its window and still held by the
        /// presenter it was handed to. A token claims once.
        /// </summary>
        public RemotePage? ClaimRemotePage(string token, DateTimeOffset now)
        {
            RemotePage? page = remotePages.FirstOrDefault(page => page.Token is not null && page.Token == token && !page.Ended
                && !page.Claimed && now <= page.ClaimDeadline && presenters.ContainsKey(page.SessionId) && page.Owner == PresenterGeneration(page.SessionId));
            if (page is not null)
            {
                page.Claimed = true;
            }

            return page;
        }

        /// <summary>Whether a page of this model was handed out with the token, for routing its claim to the window holding it.</summary>
        public bool HasRemotePageToken(string? token)
        {
            return token is not null && remotePages.Any(page => page.Token == token && !page.Ended);
        }

        /// <summary>agterm's remotePagePlace: a page handed over while it is open and still held by the presenter it was handed to.</summary>
        public RemotePage? OpenRemotePage(string pageId)
        {
            return remotePages.FirstOrDefault(page => page.Id == pageId && !page.Ended && presenters.ContainsKey(page.SessionId) && page.Owner == PresenterGeneration(page.SessionId));
        }

        /// <summary>agterm's endRemotePage: the page's channel ended, so the page ends, and the presenter is told.</summary>
        public void EndClaimedRemotePage(string pageId)
        {
            if (remotePages.FirstOrDefault(page => page.Id == pageId && !page.Ended) is RemotePage page)
            {
                CloseRemotePage(page);
            }
        }

        /// <summary>
        /// The presenter it was handed to is gone: as agterm's page on a lost presenter, it ends unanswered. So does a file
        /// page whose channel nobody claimed in time.
        /// </summary>
        private void EndOrphanedRemotePages(DateTimeOffset now)
        {
            foreach (RemotePage page in remotePages.Where(page => !page.Ended && (!presenters.ContainsKey(page.SessionId) || page.Owner != PresenterGeneration(page.SessionId))).ToList())
            {
                EndRemotePage(page, "dismissed", null);
            }

            foreach (RemotePage page in remotePages.Where(page => !page.Ended && page.Token is not null && !page.Claimed && now > page.ClaimDeadline).ToList())
            {
                CloseRemotePage(page);
            }
        }

        private void EndRemotePage(RemotePage page, string outcome, string? value)
        {
            if (page.Ended)
            {
                return;
            }

            page.Ended = true;
            if (FindSessionOrPending(page.SessionId) is Session session && session.OverlayResults.GetValueOrDefault(page.Slot)?.Id == page.Id)
            {
                session.OverlayResults[page.Slot] = new OverlayResult(page.Id, null) { IsPage = true, Remote = true, PageState = outcome, PageValue = value };
            }

            PageOutcomes.Finish(page.Id, outcome, value);
            RemotePageEnded?.Invoke(page);
            // Kept while the stream may still have to tell the presenter it closed; the oldest go once there are many.
            List<RemotePage> ended = remotePages.Where(other => other.Ended).ToList();
            foreach (RemotePage old in ended.Take(Math.Max(0, ended.Count - FinishedJobsKept)))
            {
                remotePages.Remove(old);
            }

            TreeChanged?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>The pages handed to the presenter of this generation, for its stream: those open, and those closed here.</summary>
        private List<ControlPresentationPage>? PresentationPages(Guid sessionId, int generation)
        {
            List<ControlPresentationPage> pages = remotePages
                .Where(page => page.SessionId == sessionId && page.Owner == generation && (!page.Ended || page.Closed))
                .Select(PresentationPageOf)
                .ToList();
            return pages.Count == 0 ? null : pages;
        }

        private ControlPresentationPage PresentationPageOf(RemotePage page)
        {
            return new ControlPresentationPage
            {
                Id = page.Id,
                Owner = page.Owner,
                Pane = FindSessionOrPending(page.SessionId) is Session session ? PresentationPaneOf(session, page.Slot) : null,
                Url = page.Spec.Source,
                IsFile = page.Spec.IsFile,
                Root = page.Spec.Root,
                Token = page.Closed || page.Claimed ? null : page.Token,
                Js = page.Spec.Js,
                Navigation = page.Spec.Navigation,
                Chromeless = page.Spec.Chromeless,
                Persistent = page.Spec.Persistent,
                Browse = page.Spec.Browse,
                SizePercent = page.SizePercent,
                BackgroundColor = page.BackgroundColor,
                Follow = page.Follow,
                Hidden = page.HiddenRequested,
                Closed = page.Closed,
                Commands = page.Commands.Count == 0 ? null : page.Commands.ToList(),
            };
        }
    }
}
