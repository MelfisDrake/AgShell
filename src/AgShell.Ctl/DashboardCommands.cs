using System.CommandLine;
using AgShell.Protocol;

namespace AgShell.Ctl
{
    internal static partial class Commands
    {
        private static Command Dashboard()
        {
            Argument<string[]> ids = new Argument<string[]>("ids") { Description = "Session ids (or unique prefixes) to show, max 9. Each may carry a :left/:right pane suffix; a bare id takes every pane of the session. Omit only with --mru or --close.", Arity = ArgumentArity.ZeroOrMore };
            Option<double?> fontSize = new Option<double?>("--font-size") { Description = "Absolute cell font size in points (mutually exclusive with --auto-size)." };
            Option<bool> autoSize = new Option<bool>("--auto-size") { Description = "Size cells relative to the Settings default font, shrinking as the grid grows." };
            Option<bool> mru = new Option<bool>("--mru") { Description = "Populate the grid from the window's most-recently-used sessions (up to 9)." };
            Option<bool> close = new Option<bool>("--close") { Description = "Close the open dashboard (takes no ids, --mru, or font options)." };
            CtlCommand dashboard = new CtlCommand("dashboard", "Open a view-only grid of live sessions, or --close the open one.")
            {
                Discussion = """
                    dashboard S1 S2 S3                 open a grid of the named sessions (ids or unique prefixes, max 9)
                    dashboard S1:left                  place only the main pane of a split session
                    dashboard S1:left S2:right         mix panes across sessions; a bare id still takes all of its panes
                    dashboard S1 S2 --font-size 12     open with an absolute cell font size (points)
                    dashboard S1 S2 --auto-size        open sizing cells relative to the Settings default font
                    dashboard --mru                    open a grid of the window's most-recently-used sessions (up to 9)
                    dashboard --mru --auto-size        the same, sizing cells relative to the Settings default font
                    dashboard S1 --window W            open in a specific window (defaults to the frontmost)
                    dashboard --close                  close the open dashboard

                    The 9-cell cap counts PANES, so a split session normally takes two of them. A pane suffix keeps the
                    pane you want and frees the other cell; `:right` on a session with no split is reported as
                    unresolved. The suffix is the same form `tree --json` reports in `dashboardMembers`.
                    """,
            };
            dashboard.Add(ids);
            dashboard.Add(fontSize);
            dashboard.Add(autoSize);
            dashboard.Add(mru);
            dashboard.Add(close);
            // agterm's dashboard.validate, in its order; the app checks again.
            dashboard.Check(
                parsed => parsed.GetValue(close) && (parsed.GetValue(ids) is { Length: > 0 } || parsed.GetValue(mru) || parsed.GetValue(fontSize) is not null || parsed.GetValue(autoSize)) ? "--close takes no ids, --mru, or font options" : null,
                parsed => parsed.GetValue(close) ? null
                    : parsed.GetValue(mru) && parsed.GetValue(ids) is { Length: > 0 } ? "--mru cannot be combined with session ids"
                    : parsed.GetValue(ids) is not { Length: > 0 } && !parsed.GetValue(mru) ? "dashboard requires at least one session id (or --mru, or --close)"
                    : parsed.GetValue(fontSize) is not null && parsed.GetValue(autoSize) ? "--font-size is mutually exclusive with --auto-size"
                    : parsed.GetValue(fontSize) is double points && !(double.IsFinite(points) && points > 0) ? "--font-size must be a positive number"
                    : null);
            dashboard.SetAction(result => Send(result, "dashboard", null, new ControlArgs
            {
                Targets = result.GetValue(ids) is { Length: > 0 } list ? list.ToList() : null,
                FontSize = result.GetValue(fontSize),
                AutoSize = Flag(result, autoSize),
                Mru = Flag(result, mru),
                Close = Flag(result, close),
            }));
            return dashboard;
        }
    }
}
