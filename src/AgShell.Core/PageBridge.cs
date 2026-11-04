using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's HtmlBridge: a page's message as the control request a socket would send. What the page left out is filled
    /// from where it sits, and only there: an explicit target, targets or window resolves exactly as it would from
    /// agshellctl. The same for a page shown here and for one handed to another machine, whose requests run here.
    /// </summary>
    public static class PageBridge
    {
        /// <summary>agterm: each needs more than one request and answer, a stream handed over or work done after the answer.</summary>
        private static readonly HashSet<string> Refused = new HashSet<string> { "live.presentation", "live.answer", ControlEndpoint.PageServeCommand, ControlEndpoint.JobRunCommand, ControlEndpoint.PresentCommand, "live.reset" };

        /// <summary>Commands that address no session, or address it some other way than by target.</summary>
        private static readonly HashSet<string> NotSessionTargeted = new HashSet<string> { "session.new", "session.go" };

        /// <summary>Session commands outside the session names; ask.open joins them unless it asks in the window.</summary>
        private static readonly HashSet<string> SessionTargeted = new HashSet<string> { "notify", "font.inc", "font.dec", "font.reset", "keymap.run" };

        /// <summary>The page's own overlay: its pane is filled in, so a pane page's resize meets the pane refusal.</summary>
        private static readonly HashSet<string> OwnOverlay = new HashSet<string>
        {
            "session.overlay.close", "session.overlay.reload", "session.overlay.resize", "session.overlay.navigate",
            "session.overlay.submit", "session.overlay.hide", "session.overlay.show", "session.overlay.toggle",
        };

        /// <summary>App-wide commands that refuse any window.</summary>
        private static readonly HashSet<string> Windowless = new HashSet<string> { "hooks.reload", "hooks.list", "browser.clear", "browser.links", "recent.list", "recent.clear" };

        /// <summary>Their ids name something else, a remote session or dashboard cells, and they still land in the page's window.</summary>
        private static readonly HashSet<string> PlacedLocally = new HashSet<string> { "live.attach", "dashboard", "recent.open" };

        /// <summary>The request to run, or why the page may not send it.</summary>
        /// <param name="window">The id of the window holding the page.</param>
        public static (ControlRequest? Request, string? Error) Build(ControlRequest sent, Guid sessionId, PaneRole slot, string window)
        {
            if (Refused.Contains(sent.Cmd))
            {
                return (null, sent.Cmd + " cannot be sent from a page");
            }

            ControlArgs args = sent.Args ?? new ControlArgs();
            string? target = sent.Target;
            bool addressed = target is not null || args.Targets is not null || args.Window is not null;
            if (sent.Cmd == "session.overlay.reload" && args.Current is null)
            {
                args = args with { Current = true };
            }

            if (PlacedLocally.Contains(sent.Cmd))
            {
                args = args.Window is null ? args with { Window = window } : args;
            }
            else if (!addressed && !Windowless.Contains(sent.Cmd))
            {
                if (IsSessionTargeted(sent.Cmd, args))
                {
                    target = sessionId.Wire();
                    if (OwnOverlay.Contains(sent.Cmd) && args.Pane is null && slot != PaneRole.Overlay)
                    {
                        args = args with { Pane = slot == PaneRole.OverlayRight ? "right" : "left" };
                    }
                }
                else if (IsWindowTargeted(sent.Cmd))
                {
                    target = window;
                }
                else
                {
                    args = args with { Window = window };
                }
            }

            return (sent with { Target = target, Args = args == new ControlArgs() ? null : args }, null);
        }

        private static bool IsSessionTargeted(string cmd, ControlArgs args)
        {
            if (cmd == "ask.open")
            {
                return args.Style != "gui";
            }

            return (cmd.StartsWith("session.", StringComparison.Ordinal) && !NotSessionTargeted.Contains(cmd)) || SessionTargeted.Contains(cmd);
        }

        /// <summary>Window commands take their window as target; new, list and go name none.</summary>
        private static bool IsWindowTargeted(string cmd)
        {
            return cmd.StartsWith("window.", StringComparison.Ordinal) && cmd is not ("window.new" or "window.list" or "window.go");
        }
    }
}
