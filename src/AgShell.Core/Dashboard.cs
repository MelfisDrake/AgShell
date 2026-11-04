namespace AgShell.Core
{
    /// <summary>One cell of the dashboard: a pane of a session.</summary>
    public sealed record DashboardCell(Guid SessionId, PaneRole Pane);

    /// <summary>
    /// agterm's DashboardTarget: a head to resolve plus an optional pane. Split on the first colon, since no head holds one,
    /// so <c>surface:&lt;id&gt;:left</c> fails here; the suffix takes left|top|primary or right|bottom|split in any case.
    /// </summary>
    public sealed record DashboardTarget(string Head, PaneRole? Pane)
    {
        public static DashboardTarget? Parse(string raw)
        {
            int colon = raw.IndexOf(':');
            if (colon < 0)
            {
                return raw.Length == 0 ? null : new DashboardTarget(raw, null);
            }

            string head = raw[..colon];
            if (head.Length == 0)
            {
                return null;
            }

            return raw[(colon + 1)..].ToLowerInvariant() switch
            {
                "left" or "top" or "primary" => new DashboardTarget(head, PaneRole.Main),
                "right" or "bottom" or "split" => new DashboardTarget(head, PaneRole.Split),
                _ => null,
            };
        }
    }

    /// <summary>A view-only grid of live panes over the window, as agterm's dashboard. Not persisted.</summary>
    public sealed record DashboardState
    {
        public required IReadOnlyList<DashboardCell> Cells { get; init; }

        public int Highlight { get; init; }

        /// <summary>An absolute cell font size, or null.</summary>
        public double? FontSize { get; init; }

        /// <summary>Shrink the font as the grid grows.</summary>
        public bool AutoSize { get; init; }
    }

    /// <summary>Grid geometry, navigation and font math, ported from agterm's DashboardLayout.</summary>
    public static class DashboardLayout
    {
        public const int MaxCells = 9;

        /// <summary>agterm's dashboardClickEnterDelay: how long a clicked cell's frame flashes before the click enters it.</summary>
        public const double ClickEnterDelay = 0.18;

        public const double MinFontSize = 6;

        /// <summary>agterm's ghosttyDefaultFontSize: the auto-size base when Settings set no font size.</summary>
        public const double GhosttyDefaultFontSize = 13;

        /// <summary>cols = ceil(sqrt(n)), rows = ceil(n / cols), with n clamped to 1..9.</summary>
        public static (int Cols, int Rows) Grid(int count)
        {
            int n = Math.Clamp(count, 1, MaxCells);
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling(n / (double)cols);
            return (cols, rows);
        }

        /// <summary>A one-step highlight move; off an edge or into an empty slot of the last row it stays put.</summary>
        public static int Move(int from, string direction, int cols, int count)
        {
            int width = Math.Max(cols, 1);
            int total = Math.Max(count, 1);
            if (from < 0 || from >= total)
            {
                return Math.Clamp(from, 0, total - 1);
            }

            int col = from % width;
            int row = from / width;
            return direction switch
            {
                "left" => col > 0 ? from - 1 : from,
                "right" => col + 1 < width && from + 1 < total ? from + 1 : from,
                "up" => row > 0 ? from - width : from,
                "down" => (row + 1) * width + col < total ? (row + 1) * width + col : from,
                _ => from,
            };
        }

        /// <summary>
        /// agterm's dashboardCover: what hides the pane's terminal in the session's own view, a full session-wide program or
        /// page first, then the pane's own overlay. A HUD, a floating overlay and a hidden one leave the panes lit.
        /// </summary>
        public static SessionOverlay? Cover(Session session, PaneRole pane)
        {
            if (session.Overlay is { Hidden: false, IsHud: false, SizePercent: null } full)
            {
                return full;
            }

            SessionOverlay? own = session.Overlays.GetValueOrDefault(pane == PaneRole.Split ? PaneRole.OverlayRight : PaneRole.OverlayLeft);
            return own is { Hidden: false } ? own : null;
        }

        /// <summary>agterm's appliedFontSize: the fixed size, the auto size for the grid, or null when the cells keep their own.</summary>
        public static double? AppliedFontSize(DashboardState dashboard, double baseSize)
        {
            (int cols, int rows) = Grid(dashboard.Cells.Count);
            return dashboard.FontSize ?? (dashboard.AutoSize ? FontSize(cols, rows, baseSize) : null);
        }

        /// <summary>The auto-size font for a grid shape: smaller as the grid gets denser, never under 6 points.</summary>
        public static double FontSize(int cols, int rows, double baseSize)
        {
            double factor = (cols, rows) switch
            {
                (1, 1) => 1.00,
                (2, 1) => 0.85,
                (2, 2) => 0.75,
                (3, 2) => 0.62,
                _ => 0.55,
            };
            return Math.Max(MinFontSize, Math.Round(baseSize * factor, MidpointRounding.AwayFromZero));
        }
    }
}
