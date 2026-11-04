using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's surface commands. A surface is one terminal: surface:&lt;session&gt;:&lt;pane&gt; (left, right, scratch,
    /// overlay, overlay-left, overlay-right), or active, the one the user works in. quick is the app's and goes elsewhere.
    /// </summary>
    public sealed partial class ControlDispatcher
    {
        private const string SurfacePrefix = "surface:";

        public static string SurfaceId(Guid sessionId, PaneRole pane)
        {
            return SurfacePrefix + sessionId.Wire() + ":" + Panes.Name(pane);
        }

        /// <summary>
        /// agterm's setSurfaceZoom: an exact surface with --target surface:…, else active: the zoom that is up, else the
        /// selected session's active surface. The answer names the surface acted on; showing a surface that is gone is
        /// refused, hiding one never is.
        /// </summary>
        private ControlResponse Zoom(ControlRequest request)
        {
            bool? zoomed = ParseToggle(request.Args?.Mode, "show", "hide", "surface zoom");
            (Guid Session, PaneRole Pane)? current = model.ZoomTarget;
            string target = request.Target?.Trim() is { Length: > 0 } given ? given : ControlTargets.Active;
            (Guid Session, PaneRole Pane) wanted;
            if (target != ControlTargets.Active)
            {
                wanted = ParseSurfaceTarget(target);
                bool on = zoomed ?? current != wanted;
                if (on && current != wanted && model.PendingModalError is string modal)
                {
                    return Fail(modal);
                }
            }
            else if (current is (Guid, PaneRole) up)
            {
                wanted = up;
            }
            else
            {
                if (zoomed == false)
                {
                    return Ok();
                }

                if (model.PendingModalError is string pending)
                {
                    return Fail(pending);
                }

                Session? selected = model.SelectedSessionId is Guid selectedId ? model.FindSession(selectedId) : null;
                if (selected is null || AppModel.ActiveSurface(selected) is not PaneRole active)
                {
                    return Fail("no active surface");
                }

                wanted = (selected.Id, active);
            }

            string id = SurfaceId(wanted.Session, wanted.Pane);
            bool show = zoomed ?? current != wanted;
            if (zoomed != false && !ZoomAvailable(wanted))
            {
                return Fail("surface not available: " + id);
            }

            if (show || current == wanted)
            {
                model.ZoomSurface(wanted.Session, wanted.Pane, show);
            }

            return Ok(new ControlResult { Id = id });
        }

        /// <summary>agterm's isTargetValid: the session is still here and the surface is one it can address.</summary>
        private bool ZoomAvailable((Guid Session, PaneRole Pane) target)
        {
            return model.FindSession(target.Session) is Session owner && SurfacesOf(owner).Any(surface => surface.Id == SurfaceId(target.Session, target.Pane));
        }

        /// <summary>
        /// agterm's zoom layer: a zoom whose session or surface is gone (a closed session or split, an exited scratch or
        /// overlay, a hidden overlay) ends. Run after every change of the model; true when it ended one.
        /// </summary>
        public bool DropStaleZoom()
        {
            if (model.ZoomTarget is (Guid, PaneRole) target && !ZoomAvailable(target))
            {
                model.SetZoom(false);
                return true;
            }

            return false;
        }

        /// <summary>agterm's TerminalSurfaceID: surface:&lt;session id&gt;:&lt;kind&gt;, the session in a window here.</summary>
        private (Guid Session, PaneRole Pane) ParseSurfaceTarget(string target)
        {
            string[] parts = target.StartsWith(SurfacePrefix, StringComparison.Ordinal) ? target[SurfacePrefix.Length..].Split(':') : [];
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out Guid sessionId) || !Panes.TryParseAny(parts[1], out PaneRole pane) || pane == PaneRole.Quick)
            {
                throw new ControlException("invalid surface: " + target);
            }

            return model.FindSession(sessionId) is not null ? (sessionId, pane) : throw new ControlException("no such surface: " + target);
        }

        /// <summary>
        /// agterm's readSurfaceCursor: the zero-based column of a surface's cursor. The surface is a session's pane by its
        /// --pane-id, an exact surface:… id, or active: the zoom that is up, else the active surface. A pure read: nothing
        /// is selected or shown.
        /// </summary>
        private async Task<ControlResponse> CursorAsync(ControlRequest request)
        {
            string target = request.Target ?? ControlTargets.Active;
            (Guid Session, PaneRole Pane) surface;
            if (request.Args?.PaneId is { Length: > 0 } paneId)
            {
                if (target.StartsWith(SurfacePrefix, StringComparison.Ordinal))
                {
                    return Fail("surface.cursor: --pane-id takes a session target");
                }

                Session owner = ResolveSession(target);
                surface = AppModel.PaneOfId(owner, paneId) is PaneRole byId ? (owner.Id, byId) : throw new ControlException("unknown pane id: " + paneId);
            }
            else if (target == ControlTargets.Active)
            {
                Session? selected = model.SelectedSessionId is Guid selectedId ? model.FindSession(selectedId) : null;
                if (selected is null)
                {
                    return Fail("no active surface");
                }

                if (model.ZoomTarget is (Guid, PaneRole) zoomTarget)
                {
                    surface = zoomTarget;
                }
                else if (SurfacesOf(selected).FirstOrDefault(node => node.Active) is ControlSurfaceNode active && Panes.TryParseAny(active.Kind, out PaneRole activePane))
                {
                    surface = (selected.Id, activePane);
                }
                else
                {
                    return Fail("no active surface");
                }
            }
            else if (target.StartsWith(SurfacePrefix, StringComparison.Ordinal))
            {
                surface = ParseSurfaceTarget(target);
            }
            else
            {
                return Fail("invalid surface: " + target);
            }

            string id = SurfaceId(surface.Session, surface.Pane);
            Session session = model.FindSession(surface.Session)!;
            if (!SurfacesOf(session).Any(node => node.Id == id))
            {
                return Fail("surface not available: " + id);
            }

            RequireUncovered(session, surface.Pane);
            if (FromDaemon(session, surface.Pane))
            {
                // agterm's coveredCursor.
                return await host.PaneScreenAsync(session.Id, surface.Pane, false) is DaemonScreen screen
                    ? Ok(new ControlResult { Id = id, Cursor = new ControlCursor { Column = screen.CursorColumn } })
                    : Fail("failed to read cursor position");
            }

            string? column = await host.TerminalAsync(session.Id, surface.Pane, "cursor", null);
            return column is not null && int.TryParse(column, out int value)
                ? Ok(new ControlResult { Id = id, Cursor = new ControlCursor { Column = value } })
                : Fail("surface not realized");
        }
    }
}
