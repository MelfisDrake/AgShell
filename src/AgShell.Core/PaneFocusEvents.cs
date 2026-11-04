using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's pane.focused: which terminal of each session is selected, by the terminal and not its position, so a swap or
    /// a hidden split shown again stays silent. A change is reported once it has settled, so a move and back emits nothing.
    /// A session the window first sees starts silent.
    /// </summary>
    public sealed class PaneFocusEvents
    {
        private readonly Dictionary<Guid, (PaneRole Role, string? Id)> reported = new Dictionary<Guid, (PaneRole Role, string? Id)>();
        private readonly HashSet<Guid> pending = new HashSet<Guid>();

        /// <summary>Takes in the sessions as they are now; true when some selection moved and should be settled later.</summary>
        public bool Observe(IEnumerable<Session> sessions)
        {
            List<Session> all = sessions.ToList();
            foreach (Guid gone in reported.Keys.Where(id => all.All(session => session.Id != id)).ToList())
            {
                reported.Remove(gone);
                pending.Remove(gone);
            }

            bool moved = false;
            foreach (Session session in all)
            {
                (PaneRole Role, string? Id) now = Selected(session);
                if (!reported.TryGetValue(session.Id, out (PaneRole Role, string? Id) before))
                {
                    reported[session.Id] = now;
                }
                else if (!Same(before, now))
                {
                    pending.Add(session.Id);
                    moved = true;
                }
                else if (!pending.Contains(session.Id))
                {
                    reported[session.Id] = now;
                }
            }

            return moved;
        }

        /// <summary>Reports the selections that settled somewhere new, as pane.focused events to append.</summary>
        public IReadOnlyList<(Guid SessionId, ControlEventPayload Payload)> Settle(Func<Guid, Session?> find)
        {
            List<(Guid, ControlEventPayload)> events = new List<(Guid, ControlEventPayload)>();
            foreach (Guid id in pending)
            {
                if (find(id) is not Session session || !reported.ContainsKey(id))
                {
                    continue;
                }

                (PaneRole Role, string? Id) now = Selected(session);
                if (Same(reported[id], now))
                {
                    reported[id] = now;
                    continue;
                }

                reported[id] = now;
                PaneRole role = session.HasSplit && session.SplitFocused ? PaneRole.Split : PaneRole.Main;
                events.Add((id, new ControlEventPayload
                {
                    Name = session.DisplayName,
                    Pane = role == PaneRole.Split ? "right" : "left",
                    PaneId = session.PaneIds.GetValueOrDefault(role),
                }));
            }

            pending.Clear();
            return events;
        }

        /// <summary>The selected terminal: its role and its pane id, which it has only once its shell starts.</summary>
        private static (PaneRole Role, string? Id) Selected(Session session)
        {
            PaneRole role = session.HasSplit && session.SplitFocused ? PaneRole.Split : PaneRole.Main;
            return (role, session.PaneIds.GetValueOrDefault(role));
        }

        /// <summary>
        /// The same terminal: by id when both have one, so a swap stays silent; by role while one has none yet, as agterm's
        /// pane identity exists from the pane's creation and a shell starting is no move.
        /// </summary>
        private static bool Same((PaneRole Role, string? Id) before, (PaneRole Role, string? Id) now)
        {
            return before.Id is not null && now.Id is not null ? before.Id == now.Id : before.Role == now.Role;
        }
    }
}
