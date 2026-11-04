using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>Where a notification came from, which decides whether it travels to a machine showing the session.</summary>
    public static class NotificationSources
    {
        /// <summary>agshellctl notify; the only kind another machine is told about.</summary>
        public const string Control = "control";

        /// <summary>OSC 9 or 777 from a pane: the bytes reach another machine's pane, which raises it there itself.</summary>
        public const string Terminal = "terminal";

        /// <summary>Received from the machine that runs the session; never passed on.</summary>
        public const string Mirrored = "mirrored";
    }

    /// <summary>
    /// agterm's presentation stream, both ends: what this machine's session looks like to one that attached it, and how a
    /// remote row takes on what its machine says. A lost stream takes back what it put on the row.
    /// </summary>
    /// <summary>What a layout from the other machine asks of the window.</summary>
    public enum RemoteLayoutChange
    {
        None,
        SplitRemoved,
    }

    public sealed partial class AppModel
    {
        /// <summary>A presenter that has not polled for this long is gone; its questions come back here.</summary>
        public static readonly TimeSpan PresenterTimeout = TimeSpan.FromSeconds(5);

        private readonly Dictionary<Guid, (string Token, DateTimeOffset LastSeen)> presenters = new Dictionary<Guid, (string, DateTimeOffset)>();
        private readonly Dictionary<Guid, int> presenterGenerations = new Dictionary<Guid, int>();
        /// <summary>Every stream reading each session, by token, with its last read: the presenter and the mirrors.</summary>
        private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> presentationReaders = new Dictionary<Guid, Dictionary<string, DateTimeOffset>>();
        private readonly Dictionary<Guid, List<ControlPresentationSound>> presenterSounds = new Dictionary<Guid, List<ControlPresentationSound>>();

        /// <summary>
        /// Tokens of streams this machine holds open, as agterm's origin holds its connections: they never go quiet, and they
        /// go when their connection closes.
        /// </summary>
        private readonly HashSet<string> heldStreams = new HashSet<string>();

        /// <summary>A stream on an open connection: its role and its reading stay until it is released.</summary>
        public void HoldPresentationStream(string token)
        {
            heldStreams.Add(token);
        }

        /// <summary>
        /// What another machine shows of the session. A stream naming <paramref name="presenter"/> asks for the presenter
        /// role: the first to ask while nobody holds it keeps it until it goes quiet, as agterm's grant.
        /// </summary>
        public ControlPresentation Presentation(Guid sessionId, DateTimeOffset now, string? presenter = null, bool acquire = true)
        {
            Session session = RequireSession(sessionId);
            // As agterm's: a stream asks for the role when it connects; the others mirror and ask again only on a reconnect.
            bool presenting = presenter is not null && (acquire ? AcquirePresenter(sessionId, presenter, now) : RefreshPresenter(sessionId, presenter, now));
            if (presenter is not null)
            {
                if (!presentationReaders.TryGetValue(sessionId, out Dictionary<string, DateTimeOffset>? readers))
                {
                    presentationReaders[sessionId] = readers = new Dictionary<string, DateTimeOffset>();
                }

                // A reconnected stream comes with a new token: the quiet ones go, so the list does not grow.
                foreach (string gone in readers.Where(pair => now - pair.Value > PresenterTimeout && !heldStreams.Contains(pair.Key)).Select(pair => pair.Key).ToList())
                {
                    readers.Remove(gone);
                }

                readers[presenter] = now;
            }

            int generation = PresenterGeneration(sessionId);
            AskRequest? handed = presenting ? AsksOf(sessionId).FirstOrDefault(ask => ask.RemoteOwner == generation) : null;
            ControlPresentationStatus? status = session.Status == AgentStatus.Idle ? null : new ControlPresentationStatus
            {
                Status = AgentStatuses.Name(session.Status),
                Blink = session.StatusBlink,
                Color = session.StatusColor,
                Shape = session.StatusShape,
                Pane = PresentationPaneOf(session, session.StatusPane),
                ChangedAt = session.StatusChangedAt?.ToUnixTimeMilliseconds() / 1000.0,
            };
            return new ControlPresentation
            {
                Status = status,
                Context = session.Context,
                // agterm's layout: the panes by identity, the main one first and primary, the axis, whether the split shows.
                Layout = new ControlPresentationLayout
                {
                    Panes = session.HasSplit ? new List<string> { PaneIdFor(sessionId, PaneRole.Main), PaneIdFor(sessionId, PaneRole.Split) } : new List<string> { PaneIdFor(sessionId, PaneRole.Main) },
                    Primary = PaneIdFor(sessionId, PaneRole.Main),
                    Axis = session.HasSplit ? Panes.AxisName(session.SplitAxis) : null,
                    Shown = session.HasSplit && session.SplitShown,
                },
                Hud = PresentationHud(session, now),
                Presenter = presenting ? true : null,
                Ask = handed is null ? null : PresentationAsk(session, handed, generation),
                Overlays = presenting ? PresentationOverlays(sessionId, generation) : null,
                Pages = presenting ? PresentationPages(sessionId, generation) : null,
                Sounds = presenting && presenterSounds.Remove(sessionId, out List<ControlPresentationSound>? sounds) ? sounds : null,
            };
        }

        /// <summary>The machines reading the session now: the mirrors, and whether one presents it; null when none.</summary>
        public ControlPresentersNode? PresentersOf(Guid sessionId, DateTimeOffset now)
        {
            if (!presentationReaders.TryGetValue(sessionId, out Dictionary<string, DateTimeOffset>? readers))
            {
                return null;
            }

            foreach (string gone in readers.Where(pair => now - pair.Value > PresenterTimeout && !heldStreams.Contains(pair.Key)).Select(pair => pair.Key).ToList())
            {
                readers.Remove(gone);
            }

            if (readers.Count == 0)
            {
                presentationReaders.Remove(sessionId);
                return null;
            }

            bool presented = presenters.ContainsKey(sessionId);
            return new ControlPresentersNode { Mirrors = presented ? readers.Count - 1 : readers.Count, Presenter = presented ? true : null };
        }

        /// <summary>
        /// Hands a status sound to the session's presenter, which takes it on its next read; false with no presenter or one whose
        /// hello did not list sound, as agterm's sendToPresenter, so the sound plays here.
        /// </summary>
        public bool SendSoundToPresenter(Guid sessionId, string? name)
        {
            if (!presenters.ContainsKey(sessionId) || !PresenterSupports(sessionId, ControlPresentationKinds.Sound))
            {
                return false;
            }

            if (!presenterSounds.TryGetValue(sessionId, out List<ControlPresentationSound>? sounds))
            {
                presenterSounds[sessionId] = sounds = new List<ControlPresentationSound>();
            }

            sounds.Add(new ControlPresentationSound { Name = name });
            Changed?.Invoke();
            return true;
        }

        /// <summary>Counts changes of the session's presenter, a grant and a loss alike; a handed question is bound to one.</summary>
        public int PresenterGeneration(Guid sessionId)
        {
            return presenterGenerations.GetValueOrDefault(sessionId);
        }

        public bool HasPresenter(Guid sessionId)
        {
            return presenters.ContainsKey(sessionId);
        }

        /// <summary>Whether a stream with this token has read the session before: false for a stream that is connecting.</summary>
        public bool IsPresentationReader(Guid sessionId, string token)
        {
            return presentationReaders.TryGetValue(sessionId, out Dictionary<string, DateTimeOffset>? readers) && readers.ContainsKey(token);
        }

        private bool AcquirePresenter(Guid sessionId, string token, DateTimeOffset now)
        {
            bool held = presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder);
            if (held && holder.Token != token)
            {
                return false;
            }

            if (!held)
            {
                presenterGenerations[sessionId] = PresenterGeneration(sessionId) + 1;
            }

            presenters[sessionId] = (token, now);
            return true;
        }

        private bool RefreshPresenter(Guid sessionId, string token, DateTimeOffset now)
        {
            if (!presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) || holder.Token != token)
            {
                return false;
            }

            presenters[sessionId] = (token, now);
            return true;
        }

        /// <summary>A stream that closed gives its role back at once.</summary>
        public void ReleasePresenter(Guid sessionId, string token)
        {
            heldStreams.Remove(token);
            streamKinds.Remove(token);
            presentationReaders.GetValueOrDefault(sessionId)?.Remove(token);
            if (presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) && holder.Token == token)
            {
                Release(sessionId);
            }
        }

        /// <summary>Releases presenters that stopped polling; the window calls it on a timer.</summary>
        public void ExpirePresenters(DateTimeOffset now)
        {
            foreach (Guid sessionId in presenters.Where(pair => now - pair.Value.LastSeen > PresenterTimeout && !heldStreams.Contains(pair.Value.Token)).Select(pair => pair.Key).ToList())
            {
                Release(sessionId);
            }
        }

        private void Release(Guid sessionId)
        {
            // agterm: a job handed to the presenter that went, never claimed, ends canceled now; a late claim is refused.
            int leaving = PresenterGeneration(sessionId);
            foreach (OverlayJob job in overlayJobs.Where(job => job.SessionId == sessionId && job.Owner == leaving && job.State == OverlayJob.Unclaimed).ToList())
            {
                FinishOverlayJob(job, null, "canceled");
            }

            presenters.Remove(sessionId);
            presenterSounds.Remove(sessionId);
            presenterGenerations[sessionId] = PresenterGeneration(sessionId) + 1;
            foreach (AskRequest ask in AsksOf(sessionId).Where(ask => ask.RemoteOwner is not null).ToList())
            {
                TakeAskBack(ask);
            }

            EndOrphanedRemotePages(DateTimeOffset.UtcNow);
            // The role is free: a stream waiting for it asks now.
            Changed?.Invoke();
        }

        private IEnumerable<AskRequest> AsksOf(Guid sessionId)
        {
            return new[] { terminalAsks.GetValueOrDefault(sessionId), GuiAsk?.SessionId == sessionId ? GuiAsk : null }.OfType<AskRequest>();
        }

        /// <summary>
        /// agterm's handover: a new question goes to the session's presenter when this machine follows another's lead where
        /// it would show, its pane or every pane for one over the whole session. Otherwise it stays here. True when handed.
        /// </summary>
        public bool CanHandAskOver(Guid sessionId, PaneRole? pane, Func<PaneRole, string?> lead)
        {
            if (!presenters.ContainsKey(sessionId) || FindSession(sessionId) is not Session session)
            {
                return false;
            }

            PaneRole[] panes = pane is PaneRole one ? new[] { one } : session.HasSplit ? new[] { PaneRole.Main, PaneRole.Split } : new[] { PaneRole.Main };
            return panes.All(role => lead(role) == "follower");
        }

        /// <summary>
        /// Opens a question handed to the session's presenter. It waits in the session's own slot, whatever its style, as in
        /// agterm: a GUI one does not take this window's slot, so other questions and pickers here go on.
        /// </summary>
        public string? OpenHandedAsk(AskRequest ask)
        {
            Guid sessionId = ask.SessionId ?? throw new ArgumentException("a handed question needs a session");
            if (terminalAsks.ContainsKey(sessionId))
            {
                return "ask already pending";
            }

            ask.RemoteOwner = PresenterGeneration(sessionId);
            terminalAsks[sessionId] = ask;
            Changed?.Invoke();
            return null;
        }

        /// <summary>For tests and callers that opened the question already: marks it handed when the rule allows.</summary>
        public bool HandAskOver(AskRequest ask, Func<PaneRole, string?> lead)
        {
            if (ask.SessionId is not Guid sessionId || !CanHandAskOver(sessionId, ask.Pane, lead))
            {
                return false;
            }

            ask.RemoteOwner = PresenterGeneration(sessionId);
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// The presenter's answer to a question handed to it: a button, none for escape, or a refusal, which brings the
        /// question back here. False unless it is that presenter's and the question still waits under its generation.
        /// </summary>
        public bool AnswerRemoteAsk(Guid sessionId, string presenter, ControlPresentationFrame answer)
        {
            if (!presenters.TryGetValue(sessionId, out (string Token, DateTimeOffset LastSeen) holder) || holder.Token != presenter
                || answer.Id is not string id || FindAsk(id) is not AskRequest ask || ask.SessionId != sessionId || ask.RemoteOwner is not int owner || owner != answer.Owner)
            {
                return false;
            }

            if (answer.Kind == ControlPresentationKinds.AskRejected)
            {
                TakeAskBack(ask);
                return true;
            }

            // agterm's validatedAnswer: an answer the question's form could not have produced changes nothing; it keeps waiting.
            return answer.Kind == ControlPresentationKinds.AskResolve && ValidatedAnswer(ask, answer.Button, answer.Text, answer.Selected) is ControlAskResult result && ResolveAsk(id, result);
        }

        /// <summary>
        /// A question back from its presenter is drawn here from now on. A GUI one whose session is not the selected one has
        /// nowhere to go and ends cancelled, with reason presentation-lost, rather than wait unseen.
        /// </summary>
        private void TakeAskBack(AskRequest ask)
        {
            ask.RemoteOwner = null;
            if (ask.Style == "gui" && terminalAsks.GetValueOrDefault(ask.SessionId ?? Guid.Empty) == ask)
            {
                // A GUI question waited in its session's slot; back here it needs the window's, its session on screen and, as
                // agterm's guiAskFits, the pane it is anchored to shown.
                if (!GuiTargetShown(ask.SessionId!.Value) || !AskPaneShown(ask) || GuiAsk is not null || Pick is not null)
                {
                    ResolveAsk(ask.Id, new ControlAskResult { Result = "cancelled", Reason = "presentation-lost" });
                    return;
                }

                terminalAsks.Remove(ask.SessionId!.Value);
                GuiAsk = ask;
            }
            else if (ask.Style == "gui" && (ask.SessionId is not Guid anchor || !GuiTargetShown(anchor) || !AskPaneShown(ask)))
            {
                ResolveAsk(ask.Id, new ControlAskResult { Result = "cancelled", Reason = "presentation-lost" });
                return;
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// agterm's applyRemoteLayout: a remote row follows its machine's split, its panes rebound by identity to the places the
        /// layout names them in (the window then puts the primary in the main place), its direction and whether it shows. It
        /// never opens a pane; a split the machine removed is closed here. The caller has refused an invalid layout.
        /// </summary>
        public RemoteLayoutChange ApplyRemoteLayout(Guid sessionId, ControlPresentationLayout layout)
        {
            Session session = RequireSession(sessionId);
            LearnRemotePaneIds(session, layout);
            RebindRemotePanes(session, layout);
            // Only a split that came from there follows it; one opened here is this machine's own, and a row whose one pane
            // of that machine sits beside one of this machine's has no split there to follow.
            if (!RemotePanePresent(session, PaneRole.Main) || !RemotePanePresent(session, PaneRole.Split))
            {
                return RemoteLayoutChange.None;
            }

            // The other machine closed its split: the window takes down the pane here that stood for it, which after a
            // swap made here is the main one.
            if (!layout.HasSplit)
            {
                return RemoteLayoutChange.SplitRemoved;
            }

            // As agterm's: an axis it cannot read, or none, keeps the one here; whether the split shows applies all the same.
            SplitAxis axis = Panes.TryParseAxis(layout.Axis, out SplitAxis read) ? read : session.SplitAxis;
            if (session.SplitShown != layout.Shown || session.SplitAxis != axis)
            {
                session.SplitAxis = axis;
                SetSplit(sessionId, layout.Shown);
            }

            return RemoteLayoutChange.None;
        }

        private const int MaxAskTextBytes = 64 * 1024;

        /// <summary>
        /// An answer from elsewhere checked against the question: a known button, the cancel role, and a payload matching the
        /// form — the field's text within the cap and its control-character rule, or known ids in the list's order, plus the
        /// free-form line's non-blank text, exactly one of them for a radio list. Null refuses it.
        /// </summary>
        public static ControlAskResult? ValidatedAnswer(AskRequest ask, string? buttonId, string? text, IReadOnlyList<string>? selected)
        {
            if (buttonId is null || buttonId == ask.CancelButton)
            {
                return buttonId is not null && ask.Buttons.All(button => button.Id != buttonId) ? null
                    : text is null && selected is null ? new ControlAskResult { Result = "escaped" } : null;
            }

            int index = ask.Buttons.ToList().FindIndex(button => button.Id == buttonId);
            if (index < 0)
            {
                return null;
            }

            ControlAskResult answered = new ControlAskResult { Result = "answered", Id = buttonId, Label = ask.Buttons[index].Label, Index = index };
            if (ask.Input is string mode)
            {
                return text is not null && selected is null && FitsField(text, multiline: mode == "multiline") ? answered with { Text = text } : null;
            }

            if (ask.Choices is IReadOnlyList<ControlAskChoice> choices)
            {
                if (selected is null || (text is not null && (ask.Other is null || string.IsNullOrWhiteSpace(text) || !FitsField(text, multiline: false))))
                {
                    return null;
                }

                HashSet<string> chosen = new HashSet<string>(selected);
                int picks = selected.Count + (text is null ? 0 : 1);
                bool ordered = choices.Select(choice => choice.Id).Where(chosen.Contains).SequenceEqual(selected);
                return ordered && (ask.Multiple || picks == 1) ? answered with { Text = text, Selected = selected.ToList() } : null;
            }

            return text is null && selected is null ? answered : null;
        }

        private static bool FitsField(string text, bool multiline)
        {
            return System.Text.Encoding.UTF8.GetByteCount(text) <= MaxAskTextBytes && !text.Any(c => (c < ' ' || c == '\u007f') && !(multiline && c is '\n' or '\t'));
        }

        private static ControlPresentationAsk PresentationAsk(Session session, AskRequest ask, int owner)
        {
            return new ControlPresentationAsk
            {
                Id = ask.Id,
                Owner = owner,
                Title = ask.Title,
                Message = ask.Message,
                Buttons = ask.Buttons.ToList(),
                DefaultButton = ask.DefaultButton,
                DestructiveButton = ask.DestructiveButton,
                CancelButton = ask.CancelButton,
                Style = ask.Style,
                Align = ask.Align,
                Width = ask.Width,
                Input = ask.Input,
                InputValue = ask.InputValue,
                Placeholder = ask.Placeholder,
                Choices = ask.Choices?.ToList(),
                Multiple = ask.Multiple,
                Checked = ask.Checked?.ToList(),
                Other = ask.Other,
                Pane = PresentationPaneOf(session, ask.Pane),
            };
        }

        /// <summary>
        /// The presenting side: a question handed over, drawn on the remote row under an id of its own. Null with why it
        /// cannot be shown, which goes back as a refusal.
        /// </summary>
        public (AskRequest? Replica, string? Error) OpenReplicaAsk(Guid sessionId, ControlPresentationAsk ask)
        {
            Session session = RequireSession(sessionId);
            PaneRole? pane = null;
            if (ask.Pane is string name)
            {
                // agterm's rendersPane: a question over a pane with no counterpart on screen here is refused.
                if (RemoteRoleOfPresentationPane(session, name) is not PaneRole role || ShownLocalPane(session, role) is not PaneRole shown)
                {
                    return (null, "pane not shown here");
                }

                pane = shown;
            }

            // agterm's GUI question needs its session on screen: selected, not hidden by a zoomed pane or the dashboard.
            if (ask.Style == "gui" && !GuiTargetShown(sessionId))
            {
                return (null, "a GUI question's session must be on screen");
            }

            AskRequest replica = new AskRequest
            {
                Id = Ids.New(),
                Title = ask.Title,
                Message = ask.Message,
                Buttons = ask.Buttons,
                DefaultButton = ask.DefaultButton,
                DestructiveButton = ask.DestructiveButton,
                CancelButton = ask.CancelButton,
                Style = ask.Style,
                Align = ask.Align,
                Width = ask.Width,
                Input = ask.Input,
                InputValue = ask.InputValue,
                Placeholder = ask.Placeholder,
                Choices = ask.Choices,
                Multiple = ask.Multiple,
                Checked = ask.Checked,
                Other = ask.Other,
                SessionId = sessionId,
                Pane = pane,
                Replica = true,
            };
            // Handed here, it waits in its session's slot whatever its style, as agterm's replica: this window's own questions
            // and pickers are not held up by it.
            if (terminalAsks.ContainsKey(sessionId))
            {
                return (null, "ask already pending");
            }

            terminalAsks[sessionId] = replica;
            Changed?.Invoke();
            return (replica, null);
        }

        private static ControlPresentationHud? PresentationHud(Session session, DateTimeOffset now)
        {
            // The session's one HUD sits in its own slot; the pane it is drawn over goes along by name.
            if (session.Overlay is not SessionOverlay { Hud: HudSpec hud } overlay)
            {
                return null;
            }

            double? remaining = hud.HideAfter is double hide ? Math.Max(0, hide - (now - (overlay.ShownAt ?? now)).TotalSeconds) : null;
            return new ControlPresentationHud
            {
                Id = overlay.Id,
                Message = hud.Message,
                Detail = hud.Detail,
                Spinner = hud.Spinner,
                Position = hud.Position,
                TextColor = hud.TextColor,
                FontSize = hud.FontSize,
                Sticky = hud.Sticky,
                Frame = hud.Frame,
                Markdown = hud.Markdown,
                SizePercent = overlay.SizePercent,
                BackgroundColor = overlay.BackgroundColor,
                Pane = PresentationPaneOf(session, overlay.HudPane),
                Remaining = remaining,
                Published = overlay.ShownAt?.ToUnixTimeMilliseconds(),
            };
        }

        /// <summary>
        /// The other machine's status on its row; null is idle. A snapshot comes with every reconnect, so it does not end a
        /// status set on this machine, only one the stream put there.
        /// </summary>
        public void ApplyRemoteStatus(Guid sessionId, ControlPresentationStatus? status, bool snapshot)
        {
            Session session = RequireSession(sessionId);
            if (snapshot && !session.RemoteStatusBridged && session.Status != AgentStatus.Idle)
            {
                return;
            }

            AgentStatus value = status is not null && AgentStatuses.TryParse(status.Status, out AgentStatus parsed) ? parsed : AgentStatus.Idle;
            // The other machine's left is this row's main pane, its right the split that came with the row. Its scratch, or a
            // right this row no longer shows, has no counterpart: the glyph is the session's, owned by no pane here.
            PaneRole? named = status?.Pane is string pane ? RemoteRoleOfPresentationPane(session, pane) : PaneRole.Main;
            bool owned = named is PaneRole role && RemotePanePresent(session, role);
            SetStatus(sessionId, value, status?.Blink == true, pane: owned ? LocalPaneOf(session, named!.Value) : PaneRole.Main, force: true, color: status?.Color, shape: status?.Shape, paneNamed: owned && status?.Pane is not null);
            session.RemoteStatusUnowned = value != AgentStatus.Idle && !owned;
            if (status?.ChangedAt is double stamp)
            {
                session.StatusChangedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(stamp * 1000));
            }

            session.RemoteStatusBridged = value != AgentStatus.Idle;
        }

        public void ApplyRemoteContext(Guid sessionId, string? context)
        {
            Session session = RequireSession(sessionId);
            // A context that breaks the rule here is dropped, as agterm drops one its own validation refuses.
            context = context is not null && Session.IsValidContext(context.Trim()) ? context.Trim() : null;
            string? shown = session.EffectiveContext;
            session.MirroredContext = context;
            // The row is never saved, and the tree moves only when what it shows changed.
            if (shown != session.EffectiveContext)
            {
                TreeChanged?.Invoke();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// A remote row left the tree (false) or came back with an undo (true). agterm stops its stream while it is hidden and
        /// gives up the presenter role, so nothing is handed to a row nobody sees.
        /// </summary>
        public event Action<Session, bool>? RemoteRowVisibility;

        /// <summary>
        /// Whether the other machine's pane (Main is its left, Split its right) is still on this row: its main unless it went
        /// there and a split of this machine took its place, and the split it brought. Where it sits here is LocalPaneOf.
        /// </summary>
        public static bool RemotePanePresent(Session session, PaneRole remote)
        {
            return session.RemoteHost is not null && session.RemoteBinding.Any(pair => pair.Value == remote && (pair.Key == PaneRole.Main || session.HasSplit));
        }

        /// <summary>
        /// Whether the pane here shows one of the other machine's, bound to it as agterm binds a replica to its shell. agterm's
        /// row stays remote either way; it is never saved.
        /// </summary>
        public static bool IsRemotePane(Session session, PaneRole pane)
        {
            return RemoteRoleOf(session, pane) is not null;
        }

        /// <summary>agterm's PresentationPane for a pane of this machine's session: its identity, a pane's overlay its pane's, the scratch by name.</summary>
        private static string? PresentationPaneOf(Session session, PaneRole? pane)
        {
            return pane switch
            {
                PaneRole.Main or PaneRole.OverlayLeft => session.PaneIdMap.GetValueOrDefault(PaneRole.Main),
                PaneRole.Split or PaneRole.OverlayRight => session.PaneIdMap.GetValueOrDefault(PaneRole.Split),
                PaneRole.Scratch => PresentationPaneConverter.Scratch,
                _ => null,
            };
        }

        /// <summary>
        /// The other machine's pane (Main its left, Split its right) a presentation pane names, as agterm binds by identity: the
        /// pane here whose daemon has that name. Null for the scratch, a pane this row does not show, or none.
        /// </summary>
        public static PaneRole? RemoteRoleOfPresentationPane(Session session, string? pane)
        {
            foreach ((PaneRole local, string id) in session.RemotePaneIds)
            {
                if (string.Equals(id, pane, StringComparison.OrdinalIgnoreCase) && RemoteRoleOf(session, local) is PaneRole remote)
                {
                    return remote;
                }
            }

            return null;
        }

        /// <summary>The daemon on the other machine whose shell the pane here shows; null when the row never learned it.</summary>
        public static string? RemoteDaemonOf(Session session, PaneRole pane)
        {
            return session.RemotePaneIds.GetValueOrDefault(pane);
        }

        /// <summary>The other machine's pane the pane here shows (Main is its left, Split its right); null for a shell of this machine.</summary>
        public static PaneRole? RemoteRoleOf(Session session, PaneRole pane)
        {
            return session.RemoteHost is not null && pane is PaneRole.Main or PaneRole.Split && (pane == PaneRole.Main || session.HasSplit)
                && session.RemoteBinding.TryGetValue(pane, out PaneRole remote) ? remote : null;
        }

        /// <summary>
        /// The panes here whose pane of the other machine is no longer in its layout, by the ids it names them with, as agterm
        /// tells a removed pane by its identity. Empty when nothing left.
        /// </summary>
        public static List<PaneRole> RemotePanesGone(Session session, ControlPresentationLayout layout)
        {
            HashSet<string> there = new HashSet<string>(layout.Panes, StringComparer.OrdinalIgnoreCase);
            return session.RemotePaneIds.Where(pair => IsRemotePane(session, pair.Key) && !there.Contains(pair.Value)).Select(pair => pair.Key).ToList();
        }

        /// <summary>A pane here learns the id of the pane it shows the first time a layout names it; an id never changes.</summary>
        private static void LearnRemotePaneIds(Session session, ControlPresentationLayout layout)
        {
            foreach ((PaneRole local, PaneRole remote) in session.RemoteBinding)
            {
                string? id = remote == PaneRole.Main ? layout.MainId : layout.HasSplit ? layout.SplitId : null;
                if (id is not null && !session.RemotePaneIds.ContainsKey(local))
                {
                    session.RemotePaneIds[local] = id;
                }
            }
        }

        /// <summary>
        /// A swap there: a pane here stays with its shell, whose daemon keeps its name, as agterm's replica stays with its zmx
        /// session, and now stands for the place the layout names that daemon in.
        /// </summary>
        private void RebindRemotePanes(Session session, ControlPresentationLayout layout)
        {
            bool changed = false;
            foreach ((PaneRole local, string id) in session.RemotePaneIds.ToList())
            {
                PaneRole? place = id == layout.MainId ? PaneRole.Main : layout.HasSplit && id == layout.SplitId ? PaneRole.Split : null;
                if (place is PaneRole remote && session.RemoteBinding.TryGetValue(local, out PaneRole bound) && bound != remote)
                {
                    session.RemoteBinding[local] = remote;
                    changed = true;
                }
            }

            if (changed)
            {
                Changed?.Invoke();
            }
        }

        /// <summary>The pane here showing the other machine's pane, when it has one and it is on screen; null otherwise.</summary>
        public static PaneRole? ShownLocalPane(Session session, PaneRole remote)
        {
            if (!RemotePanePresent(session, remote))
            {
                return null;
            }

            PaneRole local = LocalPaneOf(session, remote);
            return session.RendersPane(local) ? local : null;
        }

        /// <summary>
        /// The slot here for something the other machine puts over one of its panes (by its identity) or over the whole session
        /// (null): the pane's own slot while the row shows its split, the whole session while it shows one pane; null when that
        /// pane has no counterpart on screen here.
        /// </summary>
        public static PaneRole? LocalOverlaySlot(Session session, string? pane)
        {
            if (pane is null)
            {
                return PaneRole.Overlay;
            }

            PaneRole? remote = RemoteRoleOfPresentationPane(session, pane);
            if (remote is not PaneRole role || ShownLocalPane(session, role) is not PaneRole local)
            {
                return null;
            }

            return session.HasSplit && session.SplitShown ? Panes.OverlayOf(local) : PaneRole.Overlay;
        }

        public static bool HasRemotePanes(Session session)
        {
            return IsRemotePane(session, PaneRole.Main) || IsRemotePane(session, PaneRole.Split);
        }

        /// <summary>
        /// The pane here standing for one of the other machine's (left is its main, right its split), followed through a swap
        /// made here, as agterm follows panes by identity. The mapping is its own inverse.
        /// </summary>
        public static PaneRole LocalPaneOf(Session session, PaneRole remote)
        {
            if (remote is PaneRole.OverlayLeft or PaneRole.OverlayRight)
            {
                return Panes.OverlayOf(LocalPaneOf(session, remote == PaneRole.OverlayLeft ? PaneRole.Main : PaneRole.Split));
            }

            foreach (KeyValuePair<PaneRole, PaneRole> pair in session.RemoteBinding)
            {
                if (pair.Value == remote)
                {
                    return pair.Key;
                }
            }

            return remote;
        }

        /// <summary>A workspace's selection as a closed workspace keeps it: a remote row's is not kept, as agterm's.</summary>
        private Guid? LocalSelection(Workspace workspace)
        {
            return SelectedSessionId is Guid id && workspace.SessionList.FirstOrDefault(session => session.Id == id) is { RemoteHost: null } ? id : null;
        }

        /// <summary>agterm's guiTargetShown: the session is selected, with no zoomed pane and no dashboard over it.</summary>
        private bool GuiTargetShown(Guid sessionId)
        {
            return SelectedSessionId == sessionId && !Zoomed && Dashboard is null;
        }

        /// <summary>A question over the split needs the split on screen; one over the main pane or the session always has it.</summary>
        private bool AskPaneShown(AskRequest ask)
        {
            return ask.Pane is not PaneRole pane || pane is not (PaneRole.Main or PaneRole.Split) || (ask.SessionId is Guid id && FindSession(id) is Session session && session.RendersPane(pane));
        }

        private void AppendClosed(Guid workspaceId, Session session)
        {
            presentationReaders.Remove(session.Id);
            // agterm drops the session's streams as it leaves: the presenter role goes now, not when the stream goes quiet,
            // so an undo right after finds no presenter that is no longer there.
            if (presenters.ContainsKey(session.Id))
            {
                Release(session.Id);
            }

            // Its pages end dismissed: a caller waiting on one hears so, and an undo brings back no page.
            foreach (PaneRole slot in session.OverlayMap.Keys.ToList())
            {
                ClosePendingPage(session, slot);
            }

            // agterm's releaseLeavingSession: a session leaving the tree, softly or for good, ends the overlays it handed out,
            // and an undo brings it back with no results of them.
            EndOverlayJobsOf(session.Id, null);
            // agterm keeps the exit code of a job that finished; only a reservation or a failure goes.
            foreach (PaneRole slot in session.OverlayResults.Where(pair => pair.Value.Remote && pair.Value.ExitCode is null).Select(pair => pair.Key).ToList())
            {
                session.OverlayResults.Remove(slot);
            }
            Events.Append(ControlEventKinds.SessionClosed, workspaceId, session.Id, new ControlEventPayload { Name = session.DisplayName });
            if (session.RemoteHost is string host)
            {
                Events.Append(ControlEventKinds.RemoteClosed, workspaceId, session.Id, new ControlEventPayload { Name = session.DisplayName, Host = host });
                RemoteRowVisibility?.Invoke(session, false);
            }
        }

        private void AppendCreated(Guid workspaceId, Session session)
        {
            Events.Append(ControlEventKinds.SessionCreated, workspaceId, session.Id, new ControlEventPayload { Name = session.DisplayName });
            if (session.RemoteHost is string host)
            {
                Events.Append(ControlEventKinds.RemoteOpened, workspaceId, session.Id, new ControlEventPayload { Name = session.DisplayName, Host = host });
                RemoteRowVisibility?.Invoke(session, true);
            }
        }

        /// <summary>
        /// Puts up, repaints or takes down the HUD the other machine shows. A slot holding something of this machine's is left
        /// alone. Returns the panel put up, whose HideAfter is what remains of the other machine's.
        /// </summary>
        public SessionOverlay? ApplyRemoteHud(Guid sessionId, ControlPresentationHud? hud)
        {
            Session session = RequireSession(sessionId);
            if (session.RemoteHudId is string shown && shown != hud?.Id)
            {
                CloseOverlayById(sessionId, shown);
                session.RemoteHudId = null;
            }

            // As agterm's: zero remaining means the origin's panel is already due down; the bridged one goes too, same id or not.
            if (hud is null || hud.Remaining <= 0)
            {
                if (session.RemoteHudId is string bridged)
                {
                    CloseOverlayById(sessionId, bridged);
                    session.RemoteHudId = null;
                }

                return null;
            }

            // The session's one slot; drawn over the pane here standing for the one it names, or over the whole session when
            // that pane has no counterpart on screen, as agterm's.
            PaneRole slot = PaneRole.Overlay;
            PaneRole? anchor = RemoteRoleOfPresentationPane(session, hud.Pane) is PaneRole named ? ShownLocalPane(session, named) : null;
            if (session.Overlays.GetValueOrDefault(slot) is SessionOverlay current && current.Id != hud.Id)
            {
                // This machine's own HUD is left alone quietly, as agterm's; a program or page in the slot refuses the open.
                if (!current.IsHud)
                {
                    Log.Notice("RemotePresentation", "mirrored HUD not shown for " + sessionId.Wire() + ": overlay already open");
                }

                return null;
            }

            SessionOverlay overlay = OpenOverlay(sessionId, new SessionOverlay
            {
                Id = hud.Id,
                Hud = new HudSpec
                {
                    Message = hud.Message,
                    Detail = hud.Detail,
                    Spinner = hud.Spinner,
                    Position = hud.Position,
                    TextColor = hud.TextColor,
                    FontSize = hud.FontSize,
                    Sticky = hud.Sticky,
                    Frame = hud.Frame,
                    HideAfter = hud.Remaining,
                    Markdown = hud.Markdown,
                },
                SizePercent = hud.SizePercent,
                BackgroundColor = hud.BackgroundColor,
                HudPane = anchor,
                ShownAt = DateTimeOffset.UtcNow,
            }, slot);
            session.RemoteHudId = hud.Id;
            return overlay;
        }

        /// <summary>The stream is gone: a glyph, context or panel it put on the row would describe nothing now.</summary>
        public void ClearRemotePresentation(Guid sessionId)
        {
            Session? session = FindSessionOrPending(sessionId);
            if (session is null)
            {
                return;
            }

            ApplyRemoteContext(sessionId, null);
            if (session.RemoteStatusBridged)
            {
                SetStatus(sessionId, AgentStatus.Idle, force: true);
                session.RemoteStatusBridged = false;
            }

            ApplyRemoteHud(sessionId, null);
        }
    }
}
