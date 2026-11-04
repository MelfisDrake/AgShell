namespace AgShell.Core
{
    /// <summary>A terminal of a session: its main shell, the second shell of a split, or the scratch shell over both.</summary>
    public enum PaneRole
    {
        Main,
        Split,
        Scratch,

        /// <summary>A program run over the session; it goes away when the program exits.</summary>
        Overlay,

        /// <summary>The app-wide quick terminal; its key uses <see cref="Guid.Empty"/> as the session.</summary>
        Quick,

        /// <summary>A program over the main pane of a split only.</summary>
        OverlayLeft,

        /// <summary>A program over the split pane only.</summary>
        OverlayRight,
    }

    public enum SplitAxis
    {
        /// <summary>Panes side by side, left and right.</summary>
        Vertical,

        /// <summary>Panes stacked, top and bottom.</summary>
        Horizontal,
    }

    public static class Panes
    {
        public const double MinSplitRatio = 0.05;
        public const double MaxSplitRatio = 0.95;

        /// <summary>The primary pane's share of a split never moved, agterm's splitRatioDefault.</summary>
        public const double DefaultSplitRatio = 0.5;

        /// <summary>The API name of a pane, as agterm calls them: left, right, scratch.</summary>
        public static string Name(PaneRole role)
        {
            return role switch
            {
                PaneRole.Split => "right",
                PaneRole.Scratch => "scratch",
                PaneRole.Overlay => "overlay",
                PaneRole.Quick => "quick",
                PaneRole.OverlayLeft => "overlay-left",
                PaneRole.OverlayRight => "overlay-right",
                _ => "left",
            };
        }

        /// <summary>Accepts agterm's names and aliases: left/top/primary, right/bottom/split, scratch.</summary>
        public static bool TryParse(string? value, out PaneRole role)
        {
            switch (value)
            {
                case "left":
                case "top":
                case "primary":
                    role = PaneRole.Main;
                    return true;
                case "right":
                case "bottom":
                case "split":
                    role = PaneRole.Split;
                    return true;
                case "scratch":
                    role = PaneRole.Scratch;
                    return true;
                default:
                    role = PaneRole.Main;
                    return false;
            }
        }

        /// <summary>Every role by its name, overlay and quick included; for the page, not for API arguments.</summary>
        public static bool TryParseAny(string? value, out PaneRole role)
        {
            switch (value)
            {
                case "overlay":
                    role = PaneRole.Overlay;
                    return true;
                case "quick":
                    role = PaneRole.Quick;
                    return true;
                case "overlay-left":
                    role = PaneRole.OverlayLeft;
                    return true;
                case "overlay-right":
                    role = PaneRole.OverlayRight;
                    return true;
                default:
                    return TryParse(value, out role);
            }
        }

        public static bool IsOverlay(PaneRole role)
        {
            return role is PaneRole.Overlay or PaneRole.OverlayLeft or PaneRole.OverlayRight;
        }

        /// <summary>The overlay slot that covers a pane of a split.</summary>
        public static PaneRole OverlayOf(PaneRole pane)
        {
            return pane == PaneRole.Split ? PaneRole.OverlayRight : PaneRole.OverlayLeft;
        }

        public static string AxisName(SplitAxis axis)
        {
            return axis == SplitAxis.Horizontal ? "horizontal" : "vertical";
        }

        public static bool TryParseAxis(string? value, out SplitAxis axis)
        {
            switch (value)
            {
                case "vertical":
                    axis = SplitAxis.Vertical;
                    return true;
                case "horizontal":
                    axis = SplitAxis.Horizontal;
                    return true;
                default:
                    axis = SplitAxis.Vertical;
                    return false;
            }
        }
    }
}
