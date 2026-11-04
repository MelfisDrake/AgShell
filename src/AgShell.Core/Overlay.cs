namespace AgShell.Core
{
    /// <summary>The nine HUD anchors agterm uses.</summary>
    public static class HudPositions
    {
        public static readonly IReadOnlyList<string> All = AgShell.Protocol.ControlHud.Positions;

        /// <summary>agterm's edgeMarginPercent: the gap a panel that is not sticky keeps from the edge it is anchored to.</summary>
        public const int EdgeMarginPercent = 10;

        /// <summary>Accepts the nine names plus agterm's aliases top and bottom.</summary>
        public static string? Normalize(string? value)
        {
            return value switch
            {
                null => "center",
                "top" => "top-center",
                "bottom" => "bottom-center",
                _ => All.Contains(value) ? value : null,
            };
        }
    }

    public static class HudSpinners
    {
        public const string Default = "bar";
        public const string None = AgShell.Protocol.ControlHud.NoSpinner;

        public static readonly IReadOnlyList<string> All = AgShell.Protocol.ControlHud.Spinners;
    }

    /// <summary>
    /// What covers a session: a program in its own terminal, or a passive HUD panel. They share one slot, as in agterm.
    /// Not persisted.
    /// </summary>
    public sealed record SessionOverlay
    {
        /// <summary>Changes on every open, so a late exit or timer of an earlier overlay can be told apart.</summary>
        public required string Id { get; init; }

        public string? Command { get; init; }

        /// <summary>A program started as it is, not through the session's shell: a handed-over overlay's ssh.</summary>
        public string? CommandLine { get; init; }

        public string? Cwd { get; init; }

        /// <summary>Hold the terminal after the program exits until a key is pressed.</summary>
        public bool Wait { get; init; }

        /// <summary>A floating framed panel at this percentage of the session; null fills it.</summary>
        public int? SizePercent { get; init; }

        /// <summary>agterm's point-sized axes of a floating panel: the content's width or height in place of the percent's, at most 80% of the session.</summary>
        public int? WidthPoints { get; init; }

        public int? HeightPoints { get; init; }

        public string? BackgroundColor { get; init; }

        /// <summary>Set once the program has exited and the overlay is held by <see cref="Wait"/>.</summary>
        public int? ExitCode { get; init; }

        public HudSpec? Hud { get; init; }

        /// <summary>When a HUD was opened or last updated, from which its hide-after counts.</summary>
        public DateTimeOffset? ShownAt { get; init; }

        public bool IsHud => Hud is not null;

        /// <summary>The pane a HUD is drawn over (Main or Split); null over the whole session. A HUD's slot is always the session's.</summary>
        public PaneRole? HudPane { get; init; }

        public PageSpec? Page { get; init; }

        /// <summary>agterm's hidden overlay: out of sight but running, and still holding its slot until it closes.</summary>
        public bool Hidden { get; init; }

        /// <summary>A program runs in a terminal; a HUD and a page do not.</summary>
        public bool IsProgram => Hud is null && Page is null;
    }

    /// <summary>A local HTML file or a web page shown in an overlay instead of a program.</summary>
    public sealed record PageSpec
    {
        /// <summary>An absolute file path for a file page, or an http(s) URL for a web page.</summary>
        public required string Source { get; init; }

        public bool IsFile { get; init; }

        /// <summary>The directory a file page may read besides itself; null allows only the file.</summary>
        public string? Root { get; init; }

        /// <summary>The page may run its own JavaScript.</summary>
        public bool Js { get; init; }

        /// <summary>Show back, forward, reload, open in browser and reveal or copy buttons.</summary>
        public bool Navigation { get; init; }

        /// <summary>No strip naming the page.</summary>
        public bool Chromeless { get; init; }

        /// <summary>A web page whose site data is kept after it closes; otherwise it is cleared, as agterm's pages are.</summary>
        public bool Persistent { get; init; }

        /// <summary>A web page that may leave its first site, as agterm's --browse; otherwise links off it open in the browser.</summary>
        public bool Browse { get; init; }

        /// <summary>
        /// agterm's grantError: why a file page cannot open under the directory it may read, or null. Both are absolute, the
        /// file sits strictly inside the directory, and a drive's root or the home directory is too broad to hand a page.
        /// </summary>
        public static string? GrantError(string file, string? root, string home)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                return "html file must be an absolute path";
            }

            if (root is null)
            {
                return null;
            }

            if (!Path.IsPathFullyQualified(root))
            {
                return "cwd must be an absolute path";
            }

            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string fullFile = Path.GetFullPath(file);
            if (string.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(directory) ?? string.Empty), StringComparison.OrdinalIgnoreCase)
                || string.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)), StringComparison.OrdinalIgnoreCase))
            {
                return "cwd must not be a drive root or the home directory";
            }

            if (string.Equals(directory, fullFile, StringComparison.OrdinalIgnoreCase))
            {
                return "cwd must be a directory containing the html file";
            }

            return fullFile.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? null : "html file is outside cwd";
        }
    }

    /// <summary>
    /// What became of the last overlay in a slot: a program's exit code, or for a page whether it was answered.
    /// </summary>
    public sealed record OverlayResult(string Id, int? ExitCode)
    {
        /// <summary>Run by the machine presenting the session, as an overlay job.</summary>
        public bool Remote { get; init; }

        /// <summary>How a job ended without an exit status: launch-failed, canceled or unknown.</summary>
        public string? Failure { get; init; }

        public bool IsPage { get; init; }

        /// <summary>pending, submitted or dismissed.</summary>
        public string PageState { get; init; } = "pending";

        /// <summary>The value a page submitted, as raw JSON.</summary>
        public string? PageValue { get; init; }
    }

    public sealed record HudSpec
    {
        /// <summary>agterm's cap on a plain message and on the detail line.</summary>
        public const int MaxTextLength = 256;

        /// <summary>agterm's cap on a markdown message.</summary>
        public const int MaxMarkdownLength = 4096;

        /// <summary>agterm's longest auto-hide: a day.</summary>
        public const double MaxHideAfter = AgShell.Protocol.ControlHud.MaxHideAfter;

        public const double MinFontSize = AgShell.Protocol.ControlHud.MinFontSize;

        public const double MaxFontSize = AgShell.Protocol.ControlHud.MaxFontSize;

        /// <summary>agterm's HudLayout bounds on a panel's width: a sticky one off center may span the pane.</summary>
        public const int MinWidthPercent = 10;

        public const int MaxWidthPercent = 80;

        public static int ClampWidth(int requested, HudSpec hud)
        {
            return HudLayout.ClampSizePercent(requested, hud);
        }

        /// <summary>
        /// agterm's CommandFailure: escape sequences and control characters go (the panel prints into a live terminal), and
        /// the text is cut to what a HUD line takes.
        /// </summary>
        public static string Cleaned(string text)
        {
            string stripped = System.Text.RegularExpressions.Regex.Replace(text, @"\x1b(\[[0-?]*[ -/]*[@-~]?|\][^\x07\x1b]*(\x07|\x1b\\)?|.)?", string.Empty);
            stripped = new string(stripped.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return Capped(stripped, MaxTextLength);
        }

        /// <summary>agterm's capped: whole characters off the end until the text fits <paramref name="cap"/> in <see cref="TextLength"/>'s unit.</summary>
        private static string Capped(string text, int cap)
        {
            if (TextLength(text) <= cap)
            {
                return text;
            }

            System.Globalization.StringInfo info = new System.Globalization.StringInfo(text);
            // Every character is at least one scalar, so no more than cap of them can fit.
            int elements = Math.Min(info.LengthInTextElements, cap);
            text = info.SubstringByTextElements(0, elements);
            while (elements > 0 && TextLength(text) > cap)
            {
                elements--;
                text = elements == 0 ? string.Empty : info.SubstringByTextElements(0, elements);
            }

            return text;
        }

        /// <summary>agterm's CommandFailure.tailLimit: how much of a failed command's stderr is kept; only its last line is shown.</summary>
        public const int ErrorTailLimit = 16 * 1024;

        /// <summary>
        /// agterm's detail(fromTail:): the last line of the tail that is still something once cleaned, or null. Decoding is
        /// lenient, since the cut may split a character.
        /// </summary>
        public static string? TailDetail(byte[] tail)
        {
            string text = System.Text.Encoding.UTF8.GetString(tail);
            return text.Split('\n', '\r', '\u0085', '\u2028', '\u2029', '\v', '\f').Reverse().Select(Cleaned).FirstOrDefault(line => line.Length > 0);
        }

        /// <summary>agterm's failure line: the command's name and why it failed, the name cut first so the reason stays.</summary>
        public static string FailureMessage(string name, string reason)
        {
            string why = Cleaned(reason);
            int room = MaxTextLength - TextLength(why + ": ");
            if (room <= 0)
            {
                return why;
            }

            string who = Capped(Cleaned(name), room);
            return who.Length == 0 ? why : who + ": " + why;
        }

        /// <summary>
        /// agterm's HudLayout.textLength: Unicode scalars of the precomposed form, so an accented letter without a precomposed
        /// form or a joined emoji counts each of its scalars.
        /// </summary>
        public static int TextLength(string text)
        {
            return text.Normalize().EnumerateRunes().Count();
        }

        public required string Message { get; init; }

        public string? Detail { get; init; }

        /// <summary>Spinner style, or null for none.</summary>
        public string? Spinner { get; init; }

        public string Position { get; init; } = "center";

        public string? TextColor { get; init; }

        public double? FontSize { get; init; }

        /// <summary>Hug the edge instead of keeping a margin from it.</summary>
        public bool Sticky { get; init; }

        public bool Frame { get; init; } = true;

        public double? HideAfter { get; init; }

        public bool Markdown { get; init; }
    }

    /// <summary>agterm's HtmlZoom ladder for the pages' zoom; a factor off it steps to its neighbour.</summary>
    public static class PageZoom
    {
        private static readonly double[] Steps = { 0.5, 0.75, 0.85, 1, 1.15, 1.25, 1.5, 1.75, 2, 2.5, 3 };

        public static double Apply(string action, double current)
        {
            return action switch
            {
                "increase_font_size" => Steps.FirstOrDefault(step => step > current, 3),
                "decrease_font_size" => Steps.Reverse().FirstOrDefault(step => step < current, 0.5),
                _ => 1,
            };
        }
    }
}
