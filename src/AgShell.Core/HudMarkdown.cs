using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgShell.Core
{
    /// <summary>agterm's HudMarkdown.Style.</summary>
    [Flags]
    public enum HudStyle
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Strikethrough = 4,
        Dim = 8,
        Underline = 16,
    }

    /// <summary>agterm's HudMarkdown.Run. <see cref="Link"/> is the target a click opens, set only for a URL <see cref="HudMarkdown.LinkTarget"/> accepts.</summary>
    public sealed record HudRun(string Text, HudStyle Style, string? Link = null);

    /// <summary>agterm's HudMarkdown.Line.Kind.</summary>
    public enum HudLineKind
    {
        /// <summary>Wraps at word boundaries.</summary>
        Text,

        /// <summary>Wraps at the width, keeping every space.</summary>
        Code,

        /// <summary>Never wraps; a row wider than the panel is clipped.</summary>
        Table,

        /// <summary>A thematic break, drawn across whatever width the row is laid out at.</summary>
        Rule,
    }

    /// <summary>
    /// agterm's HudMarkdown.Line: one logical row. <see cref="Lead"/> prefixes its first wrapped row (indent, quote bars, list
    /// marker) and <see cref="Hang"/> every continuation, so wrapped list text hangs under the item rather than under the marker.
    /// </summary>
    public sealed record HudLine(string Lead, string Hang, IReadOnlyList<HudRun> Runs, HudLineKind Kind = HudLineKind.Text)
    {
        public static readonly HudLine Blank = new HudLine(string.Empty, string.Empty, Array.Empty<HudRun>());
    }

    /// <summary>
    /// agterm's HudMarkdown: lays a markdown HUD message out for the page. Markdig parses CommonMark with pipe tables and
    /// strikethrough, <see cref="Lines"/> walks the parsed blocks into prefixed logical rows of styled runs, <see cref="Rows"/>
    /// wraps them and <see cref="Fitted"/> clips them to the panel's grid.
    /// </summary>
    public static class HudMarkdown
    {
        public const string CodeIndent = "  ";
        public const string QuoteBar = "│ ";
        public const string Bullet = "• ";
        public const int MinRuleWidth = 3;
        public const int TabWidth = 4;
        public const string Ellipsis = "…";
        public const string RuleGlyph = "─";

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .Build();

        /// <summary>
        /// agterm's lines: parses <paramref name="source"/> as markdown. Blocks are separated by one blank row, except blocks
        /// sharing a list, so tight and loose lists render alike.
        /// </summary>
        public static IReadOnlyList<HudLine> Lines(string source)
        {
            MarkdownDocument document = Markdown.Parse(source.Normalize(NormalizationForm.FormC), Pipeline);
            Walker walker = new Walker();
            foreach (Block block in document)
            {
                walker.Visit(block);
            }

            return walker.Out;
        }

        /// <summary>agterm's rendersVisibleText: whether <paramref name="source"/> lays out to at least one non-space cell.</summary>
        public static bool RendersVisibleText(string source)
        {
            return Rows(Lines(source), HudLayout.MaxColumns).Any(row => row.Any(run => run.Text.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune))));
        }

        /// <summary>
        /// agterm's sanitized: control characters the parser decoded from entities become U+FFFD, so none can split a row. A tab
        /// in running text becomes a space; code blocks expand theirs before this runs.
        /// </summary>
        public static string Sanitized(string text)
        {
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (rune.Value == 0x09)
                {
                    builder.Append(' ');
                }
                else if (rune.Value < 0x20 || rune.Value == 0x7f)
                {
                    builder.Append('�');
                }
                else
                {
                    builder.Append(rune.ToString());
                }
            }

            return builder.ToString();
        }

        /// <summary>agterm's linkTarget: the URL a click may act on, or null when <see cref="LinkPolicy"/> would ignore it. Only printable ASCII passes.</summary>
        public static string? LinkTarget(string raw)
        {
            LinkRoute route = LinkPolicy.Route(raw, false, LinkOrigin.Pane, path => path);
            return route.Action != LinkAction.Ignore && raw.All(c => c >= 0x21 && c <= 0x7e) ? raw : null;
        }

        /// <summary>agterm's expandTabs: tabs become spaces up to the next <see cref="TabWidth"/> stop, counted from the line start.</summary>
        public static string ExpandTabs(string line)
        {
            StringBuilder builder = new StringBuilder();
            int column = 0;
            foreach (Rune rune in line.EnumerateRunes())
            {
                if (rune.Value == '\t')
                {
                    int pad = TabWidth - column % TabWidth;
                    builder.Append(' ', pad);
                    column += pad;
                    continue;
                }

                builder.Append(rune.ToString());
                column++;
            }

            return builder.ToString();
        }

        /// <summary>
        /// agterm's rows: wraps <paramref name="lines"/> at <paramref name="width"/> cells. Every row starts with its line's lead
        /// (first row) or hang (continuations); a blank line is one empty row. A rule spans the widest other row, and its glyphs
        /// number at least <see cref="MinRuleWidth"/> after any container prefix.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<HudRun>> Rows(IReadOnlyList<HudLine> lines, int width)
        {
            int widest = lines.Where(line => line.Kind != HudLineKind.Rule).SelectMany(line => Wrapped(line, width)).Select(Width).DefaultIfEmpty(0).Max();
            int ruleWidth = Math.Min(Math.Max(widest, MinRuleWidth), width);
            return lines.SelectMany(line => Wrapped(line, line.Kind == HudLineKind.Rule ? ruleWidth : width)).ToList();
        }

        /// <summary>
        /// agterm's fitted: clips <paramref name="rows"/> to a <paramref name="columns"/> x <paramref name="limit"/> budget. A row
        /// too wide keeps columns - 1 cells and ends in …; too many rows keep limit - 1 and end in a dimmed "… N more", N
        /// counting every hidden row.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<HudRun>> Fitted(IReadOnlyList<IReadOnlyList<HudRun>> rows, int columns, int limit)
        {
            if (limit <= 0 || columns <= 0)
            {
                return Array.Empty<IReadOnlyList<HudRun>>();
            }

            List<IReadOnlyList<HudRun>> kept = rows.ToList();
            if (rows.Count > limit)
            {
                kept = rows.Take(limit - 1).ToList();
                kept.Add(new[] { new HudRun(Ellipsis + " " + (rows.Count - kept.Count) + " more", HudStyle.Dim) });
            }

            return kept.Select(row => Clipped(row, columns)).ToList();
        }

        public static IReadOnlyList<HudRun> Clipped(IReadOnlyList<HudRun> row, int columns)
        {
            if (Width(row) <= columns)
            {
                return row;
            }

            if (columns <= 0)
            {
                return Array.Empty<HudRun>();
            }

            List<HudRun> clipped = Merged(Cells(row).Take(columns - 1).ToList());
            clipped.Add(new HudRun(Ellipsis, HudStyle.None));
            return clipped;
        }

        public static int Width(IReadOnlyList<HudRun> row)
        {
            return row.Sum(run => HudLayout.CellCount(run.Text));
        }

        private static List<IReadOnlyList<HudRun>> Wrapped(HudLine line, int width)
        {
            int leadWidth = HudLayout.CellCount(line.Lead);
            switch (line.Kind)
            {
                case HudLineKind.Rule:
                    string glyphs = string.Concat(Enumerable.Repeat(RuleGlyph, Math.Max(width - leadWidth, MinRuleWidth)));
                    return new List<IReadOnlyList<HudRun>> { Prefixed(line.Lead, new List<HudRun> { new HudRun(glyphs, HudStyle.None) }) };
                case HudLineKind.Table:
                    return new List<IReadOnlyList<HudRun>> { Prefixed(line.Lead, line.Runs.ToList()) };
                default:
                    (int First, int Later) limits = (Math.Max(width - leadWidth, 1), Math.Max(width - HudLayout.CellCount(line.Hang), 1));
                    List<List<Cell>> broken = line.Kind == HudLineKind.Code ? HardWrapped(Cells(line.Runs), limits) : WordWrapped(Cells(line.Runs), limits);
                    return broken.Select((cells, index) => Prefixed(index == 0 ? line.Lead : line.Hang, Merged(cells))).ToList();
            }
        }

        private static List<List<Cell>> HardWrapped(List<Cell> cells, (int First, int Later) limits)
        {
            List<List<Cell>> rows = new List<List<Cell>>();
            int offset = 0;
            do
            {
                int limit = rows.Count == 0 ? limits.First : limits.Later;
                rows.Add(cells.Skip(offset).Take(limit).ToList());
                offset += limit;
            }
            while (offset < cells.Count);

            return rows;
        }

        /// <summary>
        /// agterm's wordWrapped: breaks at spaces, carrying each word's styles across the break, and splits a word longer than
        /// the row. Spaces are kept as written, the line's leading ones included, since code spans and raw HTML carry their
        /// spacing as content; they are dropped at a row break and at the line's end.
        /// </summary>
        private static List<List<Cell>> WordWrapped(List<Cell> cells, (int First, int Later) limits)
        {
            List<List<Cell>> rows = new List<List<Cell>>();
            List<Cell> current = new List<Cell>();
            int Limit() => rows.Count == 0 ? limits.First : limits.Later;
            List<(List<Cell> Word, List<Cell> Separator)> entries = Words(cells);
            for (int index = 0; index < entries.Count; index++)
            {
                (List<Cell> entryWord, List<Cell> separator) = entries[index];
                List<Cell> word = index == 0 ? separator.Concat(entryWord).ToList() : entryWord;
                if (current.Count > 0)
                {
                    if (current.Count + separator.Count + word.Count <= Limit())
                    {
                        current.AddRange(separator);
                        current.AddRange(word);
                        continue;
                    }

                    rows.Add(current);
                    current = new List<Cell>();
                }

                while (word.Count > Limit())
                {
                    int limit = Limit();
                    rows.Add(word.Take(limit).ToList());
                    word = word.Skip(limit).ToList();
                }

                current = word;
            }

            if (current.Count > 0 || rows.Count == 0)
            {
                rows.Add(current);
            }

            return rows;
        }

        /// <summary>agterm's words: splits <paramref name="cells"/> at spaces, pairing each word with the run of spaces before it.</summary>
        private static List<(List<Cell> Word, List<Cell> Separator)> Words(List<Cell> cells)
        {
            List<(List<Cell> Word, List<Cell> Separator)> result = new List<(List<Cell> Word, List<Cell> Separator)>();
            List<Cell> word = new List<Cell>();
            List<Cell> separator = new List<Cell>();
            foreach (Cell cell in cells)
            {
                if (cell.Scalar.Value == ' ')
                {
                    if (word.Count > 0)
                    {
                        result.Add((word, separator));
                        word = new List<Cell>();
                        separator = new List<Cell>();
                    }

                    separator.Add(cell);
                    continue;
                }

                word.Add(cell);
            }

            if (word.Count > 0)
            {
                result.Add((word, separator));
            }

            return result;
        }

        private static List<Cell> Cells(IEnumerable<HudRun> runs)
        {
            return runs.SelectMany(run => run.Text.EnumerateRunes().Select(rune => new Cell(rune, run.Style, run.Link)).ToList()).ToList();
        }

        private static List<HudRun> Merged(List<Cell> cells)
        {
            List<HudRun> runs = new List<HudRun>();
            foreach (Cell cell in cells)
            {
                if (runs.Count > 0 && runs[^1].Style == cell.Style && runs[^1].Link == cell.Link)
                {
                    runs[^1] = runs[^1] with { Text = runs[^1].Text + cell.Scalar.ToString() };
                }
                else
                {
                    runs.Add(new HudRun(cell.Scalar.ToString(), cell.Style, cell.Link));
                }
            }

            return runs;
        }

        private static IReadOnlyList<HudRun> Prefixed(string prefix, List<HudRun> runs)
        {
            if (prefix.Length > 0)
            {
                runs.Insert(0, new HudRun(prefix, HudStyle.None));
            }

            return runs;
        }

        private readonly record struct Cell(Rune Scalar, HudStyle Style, string? Link);

        /// <summary>The inline intents Foundation splits its runs by; a run is the longest stretch sharing them, its link and its image.</summary>
        [Flags]
        private enum Intent
        {
            None = 0,
            Emphasized = 1,
            StronglyEmphasized = 2,
            Strikethrough = 4,
            Code = 8,
            InlineHtml = 16,
            SoftBreak = 32,
            LineBreak = 64,
        }

        private sealed record Segment(string Text, Intent Inline, string? Link, string? Image);

        /// <summary>agterm's Walker, over Markdig's blocks in place of Foundation's attributed runs.</summary>
        private sealed class Walker
        {
            public readonly List<HudLine> Out = new List<HudLine>();

            private readonly HashSet<object> seenItems = new HashSet<object>(ReferenceEqualityComparer.Instance);

            private HashSet<object>? lastListIds;

            public void Visit(Block block)
            {
                switch (block)
                {
                    case Table table:
                        AddTable(table);
                        break;
                    case LinkReferenceDefinitionGroup:
                    case LinkReferenceDefinition:
                        break;
                    case ContainerBlock container:
                        foreach (Block child in container)
                        {
                            Visit(child);
                        }

                        break;
                    case HtmlBlock html:
                        // Foundation gives a raw HTML block no block intent: no container prefixes, and never part of a list.
                        Emit(TextLines(HtmlRows(html), string.Empty, string.Empty), new List<ContainerBlock>());
                        break;
                    case LeafBlock leaf:
                        List<ContainerBlock> containers = Containers(leaf);
                        Emit(Render(leaf, containers), containers);
                        break;
                }
            }

            private static List<List<HudRun>> HtmlRows(HtmlBlock html)
            {
                List<string> body = LineTexts(html);
                while (body.Count > 0 && body[^1].Length == 0)
                {
                    body.RemoveAt(body.Count - 1);
                }

                return body.Select(text => new List<HudRun> { new HudRun(Sanitized(text), HudStyle.None) }).ToList();
            }

            private static List<string> LineTexts(LeafBlock block)
            {
                List<string> texts = new List<string>();
                for (int index = 0; index < block.Lines.Count; index++)
                {
                    texts.Add(block.Lines.Lines[index].Slice.ToString());
                }

                return texts;
            }

            /// <summary>The containers around <paramref name="block"/>, innermost first, as Foundation orders a run's block intents.</summary>
            private static List<ContainerBlock> Containers(Block block)
            {
                List<ContainerBlock> containers = new List<ContainerBlock>();
                for (ContainerBlock? parent = block.Parent; parent is not null and not MarkdownDocument; parent = parent.Parent)
                {
                    containers.Add(parent);
                }

                return containers;
            }

            /// <summary>agterm's render. A block with no text is skipped before its prefixes, as Foundation has no run to place it by.</summary>
            private List<HudLine> Render(LeafBlock block, List<ContainerBlock> containers)
            {
                if (block is CodeBlock)
                {
                    List<string> body = LineTexts(block);
                    if (body.Count == 0)
                    {
                        return new List<HudLine>();
                    }

                    (string codeLead, string codeHang) = Prefixes(containers);
                    return body.Select((raw, index) => new HudLine(
                        (index == 0 ? codeLead : codeHang) + CodeIndent, codeHang + CodeIndent,
                        new[] { new HudRun(Sanitized(ExpandTabs(raw)), HudStyle.None) }, HudLineKind.Code)).ToList();
                }

                if (block is ThematicBreakBlock)
                {
                    (string ruleLead, string ruleHang) = Prefixes(containers);
                    return new List<HudLine> { new HudLine(ruleLead, ruleHang, Array.Empty<HudRun>(), HudLineKind.Rule) };
                }

                List<Segment> segments = Segments(block.Inline);
                if (segments.Count == 0)
                {
                    return new List<HudLine>();
                }

                (string lead, string hang) = Prefixes(containers);
                return TextLines(InlineRows(segments, block is HeadingBlock ? HudStyle.Bold : HudStyle.None), lead, hang);
            }

            /// <summary>agterm's flushTable: the table framed in box-drawing borders, with a rule under the header when it has one.</summary>
            private void AddTable(Table table)
            {
                int columns = table.ColumnDefinitions.Count;
                Dictionary<int, List<HudRun>>? header = null;
                Dictionary<int, Dictionary<int, List<HudRun>>> body = new Dictionary<int, Dictionary<int, List<HudRun>>>();
                int bodyIndex = 0;
                foreach (TableRow row in table.OfType<TableRow>())
                {
                    if (!row.IsHeader)
                    {
                        bodyIndex++;
                    }

                    int column = 0;
                    foreach (TableCell cell in row.OfType<TableCell>())
                    {
                        List<HudRun> runs = CellRuns(cell, row.IsHeader ? HudStyle.Bold : HudStyle.None);
                        // Foundation emits nothing for an empty cell, nor for a row whose cells are all empty.
                        if (runs.Count > 0 && column < columns)
                        {
                            if (row.IsHeader)
                            {
                                header ??= new Dictionary<int, List<HudRun>>();
                                header[column] = runs;
                            }
                            else
                            {
                                if (!body.TryGetValue(bodyIndex, out Dictionary<int, List<HudRun>>? cells))
                                {
                                    cells = new Dictionary<int, List<HudRun>>();
                                    body[bodyIndex] = cells;
                                }

                                cells[column] = runs;
                            }
                        }

                        column++;
                    }
                }

                if (header is null && body.Count == 0)
                {
                    return;
                }

                List<Dictionary<int, List<HudRun>>> source = new List<Dictionary<int, List<HudRun>>>();
                if (header is not null)
                {
                    source.Add(header);
                }

                int last = body.Count == 0 ? 0 : body.Keys.Max();
                for (int index = 1; index <= last; index++)
                {
                    source.Add(body.GetValueOrDefault(index) ?? new Dictionary<int, List<HudRun>>());
                }

                List<List<List<HudRun>>> grid = source.Select(row => Enumerable.Range(0, columns).Select(index => row.GetValueOrDefault(index) ?? new List<HudRun>()).ToList()).ToList();
                int[] widths = new int[columns];
                foreach (List<List<HudRun>> row in grid)
                {
                    for (int index = 0; index < row.Count; index++)
                    {
                        widths[index] = Math.Max(widths[index], Width(row[index]));
                    }
                }

                List<ContainerBlock> containers = Containers(table);
                (string lead, string hang) = Prefixes(containers);

                List<HudRun> Border(string left, string join, string right)
                {
                    IEnumerable<string> segments = widths.Select(width => string.Concat(Enumerable.Repeat("─", width + 2)));
                    return new List<HudRun> { new HudRun(left + string.Join(join, segments) + right, HudStyle.None) };
                }

                List<HudRun> Framed(List<List<HudRun>> row)
                {
                    List<HudRun> runs = new List<HudRun> { new HudRun("│ ", HudStyle.None) };
                    for (int index = 0; index < row.Count; index++)
                    {
                        if (index > 0)
                        {
                            runs.Add(new HudRun(" ", HudStyle.None));
                        }

                        runs.AddRange(row[index]);
                        runs.Add(new HudRun(new string(' ', widths[index] - Width(row[index]) + 1) + "│", HudStyle.None));
                    }

                    return runs;
                }

                List<List<HudRun>> rows = new List<List<HudRun>> { Border("┌", "┬", "┐") };
                for (int offset = 0; offset < grid.Count; offset++)
                {
                    rows.Add(Framed(grid[offset]));
                    if (offset == 0 && header is not null)
                    {
                        rows.Add(Border("├", "┼", "┤"));
                    }
                }

                rows.Add(Border("└", "┴", "┘"));
                Emit(rows.Select((runs, index) => new HudLine(index == 0 ? lead : hang, hang, runs, HudLineKind.Table)).ToList(), containers);
            }

            private static List<HudRun> CellRuns(TableCell cell, HudStyle style)
            {
                List<Segment> segments = new List<Segment>();
                foreach (LeafBlock leaf in cell.Descendants<LeafBlock>())
                {
                    segments.AddRange(Segments(leaf.Inline));
                }

                return InlineRows(segments, style).SelectMany(row => row).ToList();
            }

            private void Emit(List<HudLine> lines, List<ContainerBlock> containers)
            {
                if (lines.Count == 0)
                {
                    return;
                }

                HashSet<object> listIds = new HashSet<object>(containers.OfType<ListBlock>(), ReferenceEqualityComparer.Instance);
                if (lastListIds is not null && (!lastListIds.Overlaps(listIds) || listIds.Count == 0))
                {
                    Out.Add(HudLine.Blank);
                }

                lastListIds = listIds;
                Out.AddRange(lines);
            }

            private static List<HudLine> TextLines(List<List<HudRun>> rows, string lead, string hang)
            {
                return rows.Select((runs, index) => new HudLine(index == 0 ? lead : hang, hang, runs)).ToList();
            }

            /// <summary>
            /// agterm's inlineRows: inline intents map onto styles and a hard break starts a row; a soft break is a space. A link
            /// is its label, underlined when a click can open it.
            /// </summary>
            private static List<List<HudRun>> InlineRows(List<Segment> segments, HudStyle style)
            {
                List<List<HudRun>> rows = new List<List<HudRun>> { new List<HudRun>() };
                foreach (Segment segment in segments)
                {
                    if (segment.Inline.HasFlag(Intent.LineBreak))
                    {
                        rows.Add(new List<HudRun>());
                        continue;
                    }

                    HudStyle runStyle = style;
                    if (segment.Inline.HasFlag(Intent.StronglyEmphasized))
                    {
                        runStyle |= HudStyle.Bold;
                    }

                    if (segment.Inline.HasFlag(Intent.Emphasized))
                    {
                        runStyle |= HudStyle.Italic;
                    }

                    if (segment.Inline.HasFlag(Intent.Strikethrough))
                    {
                        runStyle |= HudStyle.Strikethrough;
                    }

                    string? target = segment.Link is null ? null : LinkTarget(segment.Link);
                    if (target is not null)
                    {
                        runStyle |= HudStyle.Underline;
                    }

                    string text = segment.Inline.HasFlag(Intent.SoftBreak) ? " " : Sanitized(segment.Text);
                    rows[^1].Add(new HudRun(text, runStyle, target));
                }

                return rows;
            }

            /// <summary>
            /// agterm's prefixes: walks the containers outermost first. A quote adds a bar to both prefixes, and a list item adds
            /// its marker to the lead only for the item's first block, spaces of the same width otherwise.
            /// </summary>
            private (string Lead, string Hang) Prefixes(List<ContainerBlock> containers)
            {
                string lead = string.Empty;
                string hang = string.Empty;
                bool ordered = false;
                for (int index = containers.Count - 1; index >= 0; index--)
                {
                    switch (containers[index])
                    {
                        case QuoteBlock:
                            lead += QuoteBar;
                            hang += QuoteBar;
                            break;
                        case ListBlock list:
                            ordered = list.IsOrdered;
                            break;
                        case ListItemBlock item:
                            string marker = ordered ? item.Order + ". " : Bullet;
                            string pad = new string(' ', HudLayout.CellCount(marker));
                            lead += seenItems.Add(item) ? marker : pad;
                            hang += pad;
                            break;
                    }
                }

                return (lead, hang);
            }

            /// <summary>The block's inlines as Foundation's runs: the longest stretches sharing their intents, link and image.</summary>
            private static List<Segment> Segments(ContainerInline? inline)
            {
                List<Segment> segments = new List<Segment>();
                if (inline is not null)
                {
                    Collect(inline, Intent.None, null, null, segments);
                }

                return segments;
            }

            private static void Collect(Inline inline, Intent intent, string? link, string? image, List<Segment> into)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        Add(into, new Segment(literal.Content.ToString(), intent, link, image));
                        break;
                    case HtmlEntityInline entity:
                        Add(into, new Segment(entity.Transcoded.ToString(), intent, link, image));
                        break;
                    case CodeInline code:
                        Add(into, new Segment(code.Content, intent | Intent.Code, link, image));
                        break;
                    case HtmlInline html:
                        Add(into, new Segment(html.Tag, intent | Intent.InlineHtml, link, image));
                        break;
                    case AutolinkInline autolink:
                        Add(into, new Segment(autolink.Url, intent, autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url, image));
                        break;
                    case LineBreakInline lineBreak:
                        Add(into, new Segment("\n", intent | (lineBreak.IsHard ? Intent.LineBreak : Intent.SoftBreak), link, image));
                        break;
                    case EmphasisInline emphasis:
                        Intent added = emphasis.DelimiterChar == '~' ? Intent.Strikethrough
                            : emphasis.DelimiterCount >= 2 ? Intent.StronglyEmphasized : Intent.Emphasized;
                        CollectChildren(emphasis, intent | added, link, image, into);
                        break;
                    case DelimiterInline delimiter:
                        // An unmatched bracket stays in the tree as a delimiter; it is literal text before its children.
                        Add(into, new Segment(delimiter.ToLiteral(), intent, link, image));
                        CollectChildren(delimiter, intent, link, image, into);
                        break;
                    case LinkInline linkInline when linkInline.IsImage:
                        CollectChildren(linkInline, intent, link, linkInline.Url ?? string.Empty, into);
                        break;
                    case LinkInline linkInline:
                        CollectChildren(linkInline, intent, linkInline.Url ?? string.Empty, image, into);
                        break;
                    case ContainerInline container:
                        CollectChildren(container, intent, link, image, into);
                        break;
                }
            }

            private static void CollectChildren(ContainerInline container, Intent intent, string? link, string? image, List<Segment> into)
            {
                foreach (Inline child in container)
                {
                    Collect(child, intent, link, image, into);
                }
            }

            private static void Add(List<Segment> into, Segment segment)
            {
                if (segment.Text.Length == 0)
                {
                    return;
                }

                if (into.Count > 0 && into[^1] is Segment last && last.Inline == segment.Inline && last.Link == segment.Link && last.Image == segment.Image
                    && (segment.Inline & (Intent.SoftBreak | Intent.LineBreak)) == 0)
                {
                    into[^1] = last with { Text = last.Text + segment.Text };
                    return;
                }

                into.Add(segment);
            }
        }
    }
}
