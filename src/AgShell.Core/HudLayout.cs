using System.Globalization;
using System.Text;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's PaneMetrics: the pane a HUD is laid out over, in points. The padding is the terminal's own INSIDE the panel,
    /// per side: it holds no cells, so <see cref="HudLayout.PanelGrid"/> owes it to the grid math.
    /// </summary>
    public sealed record PaneMetrics(double CellWidth, double CellHeight, double PaneWidth, double PaneHeight, double PaddingWidth = 0, double PaddingHeight = 0);

    /// <summary>
    /// agterm's HudPanelSize: the panel's share of the pane on each axis, measured separately. <see cref="HeightPoints"/> is the
    /// height the panel is drawn at, already capped; null for an unmeasured pane, which falls back to the percent.
    /// </summary>
    public sealed record HudPanelSize(int WidthPercent, int HeightPercent, double? HeightPoints = null);

    /// <summary>
    /// What the page paints in the panel's grid: agterm's renderedBody without the SGR encoding. <see cref="BlockWidth"/> 0 is
    /// plain mode (the message lines, one empty line, the dimmed detail lines, each centered on its own); any other value is
    /// markdown mode, finished rows printed at one shared offset.
    /// </summary>
    public sealed record HudPaint(int Columns, int Rows, int BlockWidth, IReadOnlyList<IReadOnlyList<HudRun>> Lines);

    /// <summary>
    /// agterm's HudLayout: message to a cell box, cell box to the pane percentages the overlay slot understands, and the rows
    /// the page paints. The caller's width (agterm's spec.sizePercent) is the overlay's, passed as sizePercent.
    /// </summary>
    public static class HudLayout
    {
        /// <summary>Widest content line before wrapping; the frame padding sits outside it.</summary>
        public const int MaxColumns = 60;

        public const int MaxSizePercent = HudSpec.MaxWidthPercent;

        public const int MinSizePercent = HudSpec.MinWidthPercent;

        /// <summary>Frame padding in cells, applied on both sides of the content.</summary>
        public const int HorizontalPadding = 2;

        public const int VerticalPadding = 1;

        /// <summary>Cells the spinner glyph and its trailing space claim, so turning the spinner on cannot rewrap text.</summary>
        public const int SpinnerWidth = 2;

        /// <summary>agterm's clampSizePercent: a width in the range a panel holding its edge margin may take.</summary>
        public static int ClampSizePercent(int requested)
        {
            return Math.Min(Math.Max(requested, MinSizePercent), MaxSizePercent);
        }

        /// <summary>agterm's maxWidthPercent: a sticky panel off center owes no edge margin and may span the pane.</summary>
        public static int MaxWidthPercent(HudSpec spec)
        {
            return spec.Sticky && spec.Position != "center" ? 100 : MaxSizePercent;
        }

        /// <summary>agterm's clampSizePercent(_:for:): the bound every path sizing a live HUD takes.</summary>
        public static int ClampSizePercent(int requested, HudSpec spec)
        {
            return Math.Min(Math.Max(requested, MinSizePercent), MaxWidthPercent(spec));
        }

        /// <summary>agterm's verticalPadding(for:): one blank row above and below the text of a framed panel, none for a frameless one.</summary>
        public static int VerticalPaddingFor(HudSpec spec)
        {
            return spec.Frame ? VerticalPadding : 0;
        }

        /// <summary>
        /// agterm's box: the cell box the panel needs, the wrapped content plus the frame padding. It decides how BIG the panel
        /// is; <see cref="PanelGrid"/> decides where the text sits inside it. Measured in <see cref="CellCount"/>'s unit.
        /// </summary>
        public static (int Columns, int Rows) Box(HudSpec spec, int wrapColumns = MaxColumns)
        {
            List<int> widths = spec.Markdown
                ? MarkdownRows(spec, wrapColumns).Select(HudMarkdown.Width).ToList()
                : BodyLines(spec).Select(CellCount).ToList();
            int content = Math.Max(widths.DefaultIfEmpty(0).Max() + (spec.Spinner is not null ? SpinnerWidth : 0), 1);
            return (content + HorizontalPadding * 2, Math.Max(widths.Count, 1) + VerticalPaddingFor(spec) * 2);
        }

        /// <summary>
        /// agterm's panelSize: the one place both axes are decided, off a single box. The caller's percent reaches the width
        /// alone; the height is always measured.
        /// </summary>
        public static HudPanelSize PanelSize(HudSpec spec, int? sizePercent, PaneMetrics pane)
        {
            (int Columns, int Rows) box = Box(spec, WrapColumns(spec, sizePercent, pane));
            int width = sizePercent ?? WidthPercent(box, pane, MaxWidthPercent(spec));
            return new HudPanelSize(width, HeightPercent(box, pane), HeightPoints(box, pane));
        }

        /// <summary>
        /// agterm's wrapColumns: the width a markdown message wraps at before its panel exists, the text columns of the widest
        /// panel this pane allows. <see cref="MaxColumns"/> for a plain message and for a pane with no measured width.
        /// </summary>
        public static int WrapColumns(HudSpec spec, int? sizePercent, PaneMetrics pane)
        {
            HudPanelSize widest = new HudPanelSize(sizePercent is int percent ? ClampSizePercent(percent, spec) : MaxWidthPercent(spec), MaxSizePercent);
            if (!spec.Markdown || PanelGrid(widest, pane) is not (int Columns, int Rows) grid)
            {
                return MaxColumns;
            }

            return TextColumns(grid.Columns, spec, sizePercent);
        }

        /// <summary>
        /// agterm's textColumns: what a panel <paramref name="columns"/> wide leaves for text once its padding and the spinner's
        /// gutter are taken. <see cref="MaxColumns"/> bounds it only while the width is measured.
        /// </summary>
        public static int TextColumns(int columns, HudSpec spec, int? sizePercent)
        {
            int available = Math.Max(columns - HorizontalPadding * 2 - (spec.Spinner is not null ? SpinnerWidth : 0), 1);
            return sizePercent is null ? Math.Min(available, MaxColumns) : available;
        }

        /// <summary>
        /// agterm's widthPercent: the box's columns plus the terminal's padding, clamped into the minimum and
        /// <paramref name="maxPercent"/>. An unmeasured pane takes <paramref name="maxPercent"/>.
        /// </summary>
        public static int WidthPercent((int Columns, int Rows) box, PaneMetrics pane, int maxPercent = MaxSizePercent)
        {
            double needed = Math.Max(box.Columns, 0) * pane.CellWidth + pane.PaddingWidth * 2;
            int measured = Percent(needed, pane.PaneWidth) ?? maxPercent;
            return Math.Min(Math.Max(measured, MinSizePercent), maxPercent);
        }

        /// <summary>
        /// agterm's heightPercent: measured from the content alone, with no minimum floor; an unmeasured pane falls back to
        /// <see cref="MinSizePercent"/>.
        /// </summary>
        public static int HeightPercent((int Columns, int Rows) box, PaneMetrics pane)
        {
            double needed = Math.Max(box.Rows, 0) * pane.CellHeight + pane.PaddingHeight * 2;
            if (Percent(needed, pane.PaneHeight) is not int measured)
            {
                return MinSizePercent;
            }

            return Math.Min(Math.Max(measured, 1), MaxSizePercent);
        }

        /// <summary>agterm's heightPoints: the same need in points under the same cap, null for an unmeasured pane.</summary>
        public static double? HeightPoints((int Columns, int Rows) box, PaneMetrics pane)
        {
            if (pane.PaneHeight <= 0 || pane.CellHeight <= 0)
            {
                return null;
            }

            double needed = Math.Max(box.Rows, 0) * pane.CellHeight + pane.PaddingHeight * 2;
            return Math.Min(needed, pane.PaneHeight * MaxSizePercent / 100);
        }

        /// <summary>
        /// agterm's panelGrid: the cell grid the panel itself gets, its width percent and height in points less the terminal's
        /// padding, over one cell. Null when the pane is not measured.
        /// </summary>
        public static (int Columns, int Rows)? PanelGrid(HudPanelSize size, PaneMetrics pane)
        {
            if (pane.CellWidth <= 0 || pane.CellHeight <= 0 || pane.PaneWidth <= 0 || pane.PaneHeight <= 0)
            {
                return null;
            }

            int columns = (int)((pane.PaneWidth * size.WidthPercent / 100 - pane.PaddingWidth * 2) / pane.CellWidth);
            double height = size.HeightPoints ?? pane.PaneHeight * size.HeightPercent / 100;
            // The points were built from whole rows, and the division can land a hair under the integer.
            int rows = (int)((height - pane.PaddingHeight * 2) / pane.CellHeight + 0.001);
            if (columns <= 0 || rows <= 0)
            {
                return null;
            }

            return (columns, rows);
        }

        /// <summary>
        /// agterm's paintGrid: the panel's own grid, or the content box when nothing was measured. <paramref name="size"/> must be
        /// the effective one the panel took. The box is agterm's at <see cref="MaxColumns"/>, whatever the caller's width.
        /// </summary>
        public static (int Columns, int Rows) PaintGrid(HudSpec spec, int? sizePercent, HudPanelSize size, PaneMetrics pane)
        {
            return PanelGrid(size, pane) ?? Box(spec);
        }

        /// <summary>agterm's renderedBody, less the header and the SGR encoding: the rows the page paints in <paramref name="grid"/>.</summary>
        public static HudPaint Paint(HudSpec spec, int? sizePercent, (int Columns, int Rows) grid)
        {
            if (spec.Markdown)
            {
                (IReadOnlyList<IReadOnlyList<HudRun>> lines, int blockWidth) = MarkdownBody(spec, sizePercent, grid);
                return new HudPaint(grid.Columns, grid.Rows, blockWidth, lines);
            }

            // As hud.sh, the empty line is the separator and every line after it is dimmed.
            List<IReadOnlyList<HudRun>> rows = new List<IReadOnlyList<HudRun>>();
            HudStyle style = HudStyle.None;
            foreach (string line in BodyLines(spec))
            {
                if (line.Length == 0)
                {
                    style = HudStyle.Dim;
                    rows.Add(Array.Empty<HudRun>());
                    continue;
                }

                rows.Add(new[] { new HudRun(line, style) });
            }

            return new HudPaint(grid.Columns, grid.Rows, 0, rows);
        }

        /// <summary>agterm's markdownRows: the message's rows at <paramref name="columns"/>, then a blank row and the detail's rows dimmed.</summary>
        public static IReadOnlyList<IReadOnlyList<HudRun>> MarkdownRows(HudSpec spec, int columns)
        {
            List<IReadOnlyList<HudRun>> rows = HudMarkdown.Rows(HudMarkdown.Lines(spec.Message), columns).ToList();
            List<string> detail = Wrap(spec.Detail ?? string.Empty, columns);
            if (detail.Count == 0)
            {
                return rows;
            }

            rows.Add(Array.Empty<HudRun>());
            rows.AddRange(detail.Select(text => (IReadOnlyList<HudRun>)new[] { new HudRun(text, HudStyle.Dim) }));
            return rows;
        }

        /// <summary>
        /// agterm's markdownBody: the markdown rows wrapped at <paramref name="grid"/> less its padding and the spinner's gutter,
        /// clipped to what fits, every row after the first indented by that gutter (the glyph sits on the first). BlockWidth is
        /// the widest painted row, gutter included, and never 0, which is plain mode's.
        /// </summary>
        public static (IReadOnlyList<IReadOnlyList<HudRun>> Lines, int BlockWidth) MarkdownBody(HudSpec spec, int? sizePercent, (int Columns, int Rows) grid)
        {
            int gutter = spec.Spinner is not null ? SpinnerWidth : 0;
            IReadOnlyList<IReadOnlyList<HudRun>> rows = HudMarkdown.Fitted(
                MarkdownRows(spec, TextColumns(grid.Columns, spec, sizePercent)),
                grid.Columns - HorizontalPadding * 2 - gutter,
                grid.Rows - VerticalPaddingFor(spec) * 2);
            int blockWidth = Math.Max(rows.Select(HudMarkdown.Width).DefaultIfEmpty(0).Max() + gutter, 1);
            string indent = new string(' ', gutter);
            List<IReadOnlyList<HudRun>> lines = rows.Select((row, index) => index > 0 && row.Count > 0 && indent.Length > 0
                ? (IReadOnlyList<HudRun>)new[] { new HudRun(indent, HudStyle.None) }.Concat(row).ToList()
                : row).ToList();
            return (lines, blockWidth);
        }

        /// <summary>agterm's bodyLines: the wrapped message, then, when a detail is set, one empty line and the wrapped detail.</summary>
        public static IReadOnlyList<string> BodyLines(HudSpec spec)
        {
            List<string> lines = Wrap(spec.Message, MaxColumns);
            List<string> detail = Wrap(spec.Detail ?? string.Empty, MaxColumns);
            if (detail.Count == 0)
            {
                return lines;
            }

            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.AddRange(detail);
            return lines;
        }

        /// <summary>
        /// agterm's wrap: lines of at most <paramref name="columns"/> cells, a newline a hard break, a word longer than the line
        /// split. Blank lines are dropped, which keeps the single empty line in the body unambiguous as the separator. The text
        /// is precomposed first, and split at newlines and spaces that stand as whole characters, as Swift splits a String.
        /// </summary>
        public static List<string> Wrap(string text, int columns)
        {
            int width = Math.Max(columns, 1);
            List<string> lines = new List<string>();
            foreach (List<string> paragraph in Split(text.Normalize(NormalizationForm.FormC), "\n"))
            {
                string current = string.Empty;
                foreach (List<string> chunk in Split(string.Concat(paragraph), " "))
                {
                    string word = string.Concat(chunk);
                    while (CellCount(word) > width)
                    {
                        if (current.Length > 0)
                        {
                            lines.Add(current);
                            current = string.Empty;
                        }

                        List<Rune> runes = word.EnumerateRunes().ToList();
                        lines.Add(string.Concat(runes.Take(width).Select(rune => rune.ToString())));
                        word = string.Concat(runes.Skip(width).Select(rune => rune.ToString()));
                    }

                    if (word.Length == 0)
                    {
                        continue;
                    }

                    if (current.Length == 0)
                    {
                        current = word;
                    }
                    else if (CellCount(current) + 1 + CellCount(word) <= width)
                    {
                        current += " " + word;
                    }
                    else
                    {
                        lines.Add(current);
                        current = word;
                    }
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                }
            }

            return lines;
        }

        /// <summary>
        /// agterm's cellCount: Unicode scalars. A double-width glyph still counts as one cell and overflows the frame, accepted as
        /// in agterm.
        /// </summary>
        public static int CellCount(string text)
        {
            return text.EnumerateRunes().Count();
        }

        /// <summary>agterm's textLength: the cap's unit, on the precomposed form the text is laid out in.</summary>
        public static int TextLength(string text)
        {
            return HudSpec.TextLength(text);
        }

        /// <summary>Swift's split(separator:): the text's characters in pieces between <paramref name="separator"/>, empty pieces dropped.</summary>
        private static List<List<string>> Split(string text, string separator)
        {
            List<List<string>> pieces = new List<List<string>>();
            List<string> piece = new List<string>();
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (element == separator)
                {
                    if (piece.Count > 0)
                    {
                        pieces.Add(piece);
                        piece = new List<string>();
                    }

                    continue;
                }

                piece.Add(element);
            }

            if (piece.Count > 0)
            {
                pieces.Add(piece);
            }

            return pieces;
        }

        /// <summary>agterm's percent: a whole percent of <paramref name="available"/>, rounded up so a panel never lands a cell short; null when unmeasured.</summary>
        private static int? Percent(double size, double available)
        {
            if (available <= 0)
            {
                return null;
            }

            return (int)Math.Ceiling(size / available * 100);
        }
    }
}
