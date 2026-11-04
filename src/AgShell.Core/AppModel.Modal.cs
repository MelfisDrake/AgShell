using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>agterm's picker: caller items in a fuzzy list, answered by an item, a custom query or a cancel.</summary>
    public sealed class PickRequest
    {
        public required string Id { get; init; }

        public required IReadOnlyList<ControlPickItem> Items { get; init; }

        public string? Prompt { get; init; }

        public string? Query { get; init; }

        public string? Selection { get; init; }

        public bool AllowCustom { get; init; }
    }

    /// <summary>
    /// agterm's ask: a question with buttons, optionally a text field or a choice list. A terminal-style one sits over its
    /// session (one per session); a GUI one takes the window's modal slot, which it shares with the picker.
    /// </summary>
    public sealed class AskRequest
    {
        public required string Id { get; init; }

        public required string Title { get; init; }

        public string? Message { get; init; }

        public required IReadOnlyList<ControlAskButton> Buttons { get; init; }

        public string? DefaultButton { get; init; }

        public string? DestructiveButton { get; init; }

        public string? CancelButton { get; init; }

        public required string Style { get; init; }

        public required string Align { get; init; }

        public int? Width { get; init; }

        public string? Input { get; init; }

        public string? InputValue { get; init; }

        public string? Placeholder { get; init; }

        public IReadOnlyList<ControlAskChoice>? Choices { get; init; }

        public bool Multiple { get; init; }

        public IReadOnlyList<string>? Checked { get; init; }

        public string? Other { get; init; }

        /// <summary>The session a terminal question sits in, or a GUI one is anchored to.</summary>
        public Guid? SessionId { get; init; }

        /// <summary>The pane a terminal question covers; null covers the whole session.</summary>
        public PaneRole? Pane { get; set; }

        /// <summary>
        /// Handed to the machine presenting the session, under this presenter generation: it waits here, but is drawn there.
        /// Null for a question drawn here.
        /// </summary>
        public int? RemoteOwner { get; set; }

        /// <summary>Handed here by another machine, which gets the answer.</summary>
        public bool Replica { get; init; }
    }

    public sealed partial class AppModel
    {
        /// <summary>agterm's retainedResultLimit: the latest answered pickers stay readable after the dialog is gone, per window.</summary>
        public const int RetainedPickResults = 8;

        /// <summary>Monotonic across every window, so results kept from different windows order by when they were answered.</summary>
        private static long pickSequence;

        private readonly Dictionary<Guid, AskRequest> terminalAsks = new Dictionary<Guid, AskRequest>();
        private readonly List<(string Id, ControlPickResult Result, long Sequence)> pickResults = new List<(string, ControlPickResult, long)>();
        private AskResultLog askResults = new AskResultLog();

        public PickRequest? Pick { get; private set; }

        public AskRequest? GuiAsk { get; private set; }

        /// <summary>A question was answered, escaped or cancelled, with its result.</summary>
        public event Action<string, ControlAskResult>? AskResolved;

        /// <summary>The picker was answered or cancelled, with its result.</summary>
        public event Action<string, ControlPickResult>? PickResolved;

        public AskRequest? TerminalAskOf(Guid sessionId)
        {
            return terminalAsks.GetValueOrDefault(sessionId);
        }

        /// <summary>
        /// agterm's window close: the window's open picker and questions are cancelled, so a script waiting on them hears
        /// "cancelled" rather than nothing.
        /// </summary>
        public void CancelAllModals()
        {
            if (Pick is PickRequest pick)
            {
                CancelPick(pick.Id);
            }

            foreach (AskRequest ask in new[] { GuiAsk }.Concat(terminalAsks.Values).OfType<AskRequest>().ToList())
            {
                CancelAsk(ask.Id);
            }
        }

        /// <summary>The kept answers, for the app to keep answering result polls once the window is gone.</summary>
        public IEnumerable<(string Id, ControlPickResult Result, long Sequence)> KeptPickResults => pickResults;

        /// <summary>agterm's AskRegistry.shared: the app's one list of finished questions, which every window adds to.</summary>
        public void ShareAskResults(AskResultLog shared)
        {
            askResults = shared;
        }

        /// <summary>agterm's pendingModalError: the modal blocking another control action, or null when the slot is free.</summary>
        public string? PendingModalError => GuiAsk is not null ? "ask pending" : Pick is not null ? "pick pending" : null;

        /// <summary>Opens the picker in the window's modal slot; returns an error when the slot is taken.</summary>
        public string? OpenPick(PickRequest pick)
        {
            if (Pick is not null)
            {
                return "pick already pending";
            }

            if (GuiAsk is not null)
            {
                return "ask already pending";
            }

            Pick = pick;
            Changed?.Invoke();
            return null;
        }

        /// <summary>The picker's result, pending while it is open; null for an id never seen or long forgotten.</summary>
        public ControlPickResult? PickResult(string id)
        {
            if (Pick?.Id == id)
            {
                return new ControlPickResult { Result = "pending" };
            }

            return pickResults.LastOrDefault(entry => entry.Id == id).Result;
        }

        /// <summary>Answers the open picker; false when it is not the one open.</summary>
        public bool ResolvePick(string id, ControlPickResult result)
        {
            if (Pick?.Id != id)
            {
                return false;
            }

            Pick = null;
            pickResults.Add((id, result, Interlocked.Increment(ref pickSequence)));
            if (pickResults.Count > RetainedPickResults)
            {
                pickResults.RemoveAt(0);
            }

            PickResolved?.Invoke(id, result);
            Changed?.Invoke();
            return true;
        }

        /// <summary>The window's answer: an item by id, else a custom query (when allowed), else a cancel.</summary>
        public bool AnswerPick(string id, string? itemId, string? query)
        {
            if (Pick is not PickRequest pick || pick.Id != id)
            {
                return false;
            }

            if (itemId is not null)
            {
                int index = pick.Items.ToList().FindIndex(item => item.Id == itemId);
                return index >= 0 && ResolvePick(id, new ControlPickResult { Result = "picked", Id = itemId, Label = pick.Items[index].Label, Index = index });
            }

            return query is not null && pick.AllowCustom
                ? ResolvePick(id, new ControlPickResult { Result = "custom", Query = query })
                : ResolvePick(id, new ControlPickResult { Result = "cancelled" });
        }

        /// <summary>Cancels a pending picker; one already answered is left as it is. False for an unknown id.</summary>
        public bool CancelPick(string id)
        {
            return ResolvePick(id, new ControlPickResult { Result = "cancelled" }) || pickResults.Any(entry => entry.Id == id);
        }

        /// <summary>Opens a question: a GUI one in the window's slot, a terminal one in its session. Returns an error when taken.</summary>
        public string? OpenAsk(AskRequest ask)
        {
            if (ask.Style == "gui")
            {
                if (GuiAsk is not null)
                {
                    return "ask already pending";
                }

                if (Pick is not null)
                {
                    return "pick already pending";
                }

                GuiAsk = ask;
            }
            else
            {
                Guid sessionId = ask.SessionId ?? throw new ArgumentException("a terminal question needs a session");
                if (terminalAsks.ContainsKey(sessionId))
                {
                    return "ask already pending";
                }

                terminalAsks[sessionId] = ask;
            }

            Changed?.Invoke();
            return null;
        }

        public ControlAskResult? AskResult(string id)
        {
            if (FindAsk(id) is not null)
            {
                return new ControlAskResult { Result = "pending" };
            }

            return askResults.Find(id) is (ControlAskResult result, object owner) && owner == this ? result : null;
        }

        /// <summary>Answers a pending question; false when no question with that id waits.</summary>
        public bool ResolveAsk(string id, ControlAskResult result)
        {
            AskRequest? ask = FindAsk(id);
            if (ask is null)
            {
                return false;
            }

            if (GuiAsk == ask)
            {
                GuiAsk = null;
            }
            else
            {
                terminalAsks.Remove(ask.SessionId!.Value);
            }

            askResults.Retain(id, result, this);
            AskResolved?.Invoke(id, result);
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// The window's answer: a button (the cancel button or none is agterm's escaped), with the field's text or the
        /// list's ids in caller order and the free-form line's text while it is selected.
        /// </summary>
        public bool AnswerAsk(string id, string? buttonId, string? text, IReadOnlyCollection<string>? selected)
        {
            if (FindAsk(id) is not AskRequest ask)
            {
                return false;
            }

            if (buttonId is null || buttonId == ask.CancelButton)
            {
                return ResolveAsk(id, new ControlAskResult { Result = "escaped" });
            }

            int index = ask.Buttons.ToList().FindIndex(button => button.Id == buttonId);
            if (index < 0)
            {
                return false;
            }

            ControlAskResult result = new ControlAskResult { Result = "answered", Id = buttonId, Label = ask.Buttons[index].Label, Index = index };
            if (ask.Input is not null)
            {
                result = result with { Text = text ?? string.Empty };
            }
            else if (ask.Choices is IReadOnlyList<ControlAskChoice> choices)
            {
                HashSet<string> picked = new HashSet<string>(selected ?? Array.Empty<string>());
                result = result with { Selected = choices.Where(choice => picked.Contains(choice.Id)).Select(choice => choice.Id).ToList(), Text = ask.Other is null ? null : text };
            }

            return ResolveAsk(id, result);
        }

        public bool CancelAsk(string id)
        {
            return ResolveAsk(id, new ControlAskResult { Result = "cancelled" }) || AskResult(id) is not null;
        }

        private AskRequest? FindAsk(string id)
        {
            return GuiAsk?.Id == id ? GuiAsk : terminalAsks.Values.FirstOrDefault(ask => ask.Id == id);
        }

        /// <summary>A closed session takes its question with it; undo brings the session back without it.</summary>
        private void CancelAsksOf(Guid sessionId)
        {
            if (terminalAsks.GetValueOrDefault(sessionId) is AskRequest ask)
            {
                CancelAsk(ask.Id);
            }

            if (GuiAsk?.SessionId == sessionId)
            {
                CancelAsk(GuiAsk.Id);
            }
        }

        /// <summary>
        /// A question over a pane that is gone is cancelled; one over the survivor follows it to its new place, as agterm's
        /// anchor holds the pane's identity. The window's GUI question anchored to the pane goes the same way.
        /// </summary>
        private void MoveAskPane(Session session, PaneRole? gone, Func<PaneRole, PaneRole> moved)
        {
            AskRequest?[] asks = { terminalAsks.GetValueOrDefault(session.Id), GuiAsk?.SessionId == session.Id ? GuiAsk : null };
            foreach (AskRequest ask in asks.OfType<AskRequest>())
            {
                if (ask.Pane is not PaneRole pane)
                {
                    continue;
                }

                if (pane == gone)
                {
                    CancelAsk(ask.Id);
                    continue;
                }

                ask.Pane = moved(pane);
            }
        }

        /// <summary>A GUI question anchored to a session is cancelled when another session is selected, as agterm's anchor loss.</summary>
        private void CancelGuiAskOffSelection(Guid? selected)
        {
            if (GuiAsk?.SessionId is Guid anchor && anchor != selected)
            {
                CancelAsk(GuiAsk.Id);
            }
        }

        /// <summary>
        /// agterm's askAnchorIsValid, checked on every change: a GUI question anchored to a pane the session no longer
        /// renders (its split hidden while the other pane has the focus) is cancelled.
        /// </summary>
        public void CancelGuiAskOffAnchor()
        {
            if (GuiAsk is { SessionId: Guid sessionId, Pane: PaneRole pane, RemoteOwner: null } ask
                && FindSession(sessionId) is Session session && !session.RendersPane(pane))
            {
                CancelAsk(ask.Id);
            }
        }
    }
}
