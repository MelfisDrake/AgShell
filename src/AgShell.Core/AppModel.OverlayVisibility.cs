namespace AgShell.Core
{
    /// <summary>agterm's overlay visibility: a program or page hidden without closing it, and shown again.</summary>
    public sealed partial class AppModel
    {
        /// <summary>
        /// The slots <paramref name="scope"/> addresses that hold a program or page: that pane's own slot, or every slot of the
        /// session for null. A HUD holds nothing that can be hidden.
        /// </summary>
        public static IReadOnlyList<PaneRole> HideableSlots(Session session, PaneRole? scope)
        {
            PaneRole[] slots = scope is PaneRole one ? new[] { one } : new[] { PaneRole.Overlay, PaneRole.OverlayLeft, PaneRole.OverlayRight };
            return slots.Where(slot => session.Overlays.GetValueOrDefault(slot) is { IsHud: false }).ToList();
        }

        /// <summary>
        /// Whether a slot <paramref name="scope"/> addresses holds a program or page that is not hidden: what a toggle hides. A page
        /// shown on the presenter counts by the visibility asked for it.
        /// </summary>
        public bool AnyOverlayShown(Session session, PaneRole? scope)
        {
            return HideableSlots(session, scope).Any(slot => !session.Overlays[slot].Hidden)
                || RemotePagesIn(session.Id, scope).Any(page => !page.HiddenRequested);
        }

        private IEnumerable<RemotePage> RemotePagesIn(Guid sessionId, PaneRole? scope)
        {
            return RemotePagesOf(sessionId).Where(page => scope is null || page.Slot == scope);
        }

        /// <summary>
        /// Hides or shows the overlays <paramref name="scope"/> addresses, or with null <paramref name="hidden"/> toggles them:
        /// hides them all while any is shown, else shows them all. Returns why it could not, or null.
        /// </summary>
        public string? SetOverlaysHidden(Guid sessionId, PaneRole? scope, bool? hidden)
        {
            Session session = RequireSession(sessionId);
            IReadOnlyList<PaneRole> slots = HideableSlots(session, scope);
            List<RemotePage> pages = RemotePagesIn(sessionId, scope).ToList();
            if (slots.Count == 0 && pages.Count == 0)
            {
                bool reserved = scope is PaneRole one ? OverlayJobReserved(sessionId, one) : new[] { PaneRole.Overlay, PaneRole.OverlayLeft, PaneRole.OverlayRight }.Any(slot => OverlayJobReserved(sessionId, slot));
                return reserved ? "overlay is shown on another machine" : "no overlay";
            }

            bool hide = hidden ?? AnyOverlayShown(session, scope);
            // agterm: a page shown on the presenter takes the request; the reply does not wait for that machine.
            foreach (RemotePage page in pages.Where(page => page.HiddenRequested != hide))
            {
                page.HiddenRequested = hide;
                Changed?.Invoke();
            }

            List<PaneRole> changing = slots.Where(slot => session.Overlays[slot].Hidden != hide).ToList();
            foreach (PaneRole slot in changing)
            {
                session.OverlayMap[slot] = session.OverlayMap[slot] with { Hidden = hide };
            }

            if (changing.Count > 0)
            {
                TreeChanged?.Invoke();
                Changed?.Invoke();
            }

            return null;
        }
    }
}
