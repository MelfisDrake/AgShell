namespace AgShell.Core.Tests
{
    public class HudMarkdownTests
    {
        private static HudRun R(string text, HudStyle style = HudStyle.None, string? link = null)
        {
            return new HudRun(text, style, link);
        }

        private static string Text(IReadOnlyList<HudRun> row)
        {
            return string.Concat(row.Select(run => run.Text));
        }

        private static List<string> Plain(string source)
        {
            return HudMarkdown.Lines(source).Select(line => line.Kind == HudLineKind.Rule ? line.Lead + "<rule>" : line.Lead + Text(line.Runs)).ToList();
        }

        private static List<IReadOnlyList<HudRun>> Styled(string source)
        {
            return HudMarkdown.Lines(source).Select(line => line.Runs).ToList();
        }

        private static List<string> LaidOut(string source, int width)
        {
            return HudMarkdown.Rows(HudMarkdown.Lines(source), width).Select(Text).ToList();
        }

        [Fact]
        public void ASoftBreakJoinsWithASpaceAndAHardBreakStartsARow()
        {
            Assert.Equal(new[] { "one two" }, Plain("one\ntwo"));
            Assert.Equal(new[] { "one", "two" }, Plain("one  \ntwo"));
            Assert.Equal(new[] { "one", "two" }, Plain("one\\\ntwo"));
        }

        [Fact]
        public void ParagraphsAreSeparatedByOneBlankRowWhateverTheSourceHad()
        {
            Assert.Equal(new[] { "a", "", "b", "", "c" }, Plain("a\n\nb\n\n\n\nc"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void EveryHeadingLevelIsBold(int level)
        {
            IReadOnlyList<HudLine> lines = HudMarkdown.Lines(new string('#', level) + " Tasks");

            Assert.Single(lines);
            Assert.Equal(new[] { R("Tasks", HudStyle.Bold) }, lines[0].Runs);
        }

        [Fact]
        public void AHeadingKeepsItsBoldAfterAnInnerStrongSpan()
        {
            Assert.Equal(new[] { R("x", HudStyle.Bold), R(" y", HudStyle.Bold) }, Styled("# **x** y").Single());
        }

        [Fact]
        public void UnorderedItemsGetABulletAndHangTheirContinuation()
        {
            IReadOnlyList<HudLine> lines = HudMarkdown.Lines("- build\n- test");

            Assert.Equal(new[] { "• ", "• " }, lines.Select(line => line.Lead));
            Assert.Equal(new[] { "  ", "  " }, lines.Select(line => line.Hang));
            Assert.Equal(new[] { "• build", "• test" }, Plain("- build\n- test"));
        }

        [Fact]
        public void OrderedItemsKeepTheirOrdinalAndHangByItsWidth()
        {
            IReadOnlyList<HudLine> lines = HudMarkdown.Lines("9. nine\n10. ten");

            Assert.Equal(new[] { "9. ", "10. " }, lines.Select(line => line.Lead));
            Assert.Equal(new[] { "   ", "    " }, lines.Select(line => line.Hang));
        }

        [Fact]
        public void ANestedListIndentsUnderItsParentsText()
        {
            Assert.Equal(new[] { "• parent", "  • child", "    • grandchild" }, Plain("- parent\n  - child\n    - grandchild"));
        }

        [Fact]
        public void TightAndLooseListsRenderAlike()
        {
            Assert.Equal(new[] { "• a", "• b" }, Plain("- a\n- b"));
            Assert.Equal(new[] { "• a", "• b" }, Plain("- a\n\n- b"));
        }

        [Fact]
        public void AListIsSeparatedFromTheParagraphAroundIt()
        {
            Assert.Equal(new[] { "status", "", "• a", "• b", "", "done" }, Plain("status\n\n- a\n- b\n\ndone"));
        }

        [Fact]
        public void ATaskCheckboxStaysLiteralText()
        {
            Assert.Equal(new[] { "• [ ] queued", "• [x] done" }, Plain("- [ ] queued\n- [x] done"));
        }

        [Fact]
        public void ACodeBlockKeepsItsLinesVerbatimAndIndented()
        {
            IReadOnlyList<HudLine> lines = HudMarkdown.Lines("```\n# not a heading\n- not a bullet\n**not bold**\n\n\tx\n```");

            Assert.Equal(new[] { "  # not a heading", "  - not a bullet", "  **not bold**", "  ", "      x" }, lines.Select(line => line.Lead + Text(line.Runs)));
            Assert.All(lines, line => Assert.All(line.Runs, run => Assert.Equal(HudStyle.None, run.Style)));
        }

        [Fact]
        public void TabsExpandToTheNextStop()
        {
            Assert.Equal("a   b", HudMarkdown.ExpandTabs("a\tb"));
            Assert.Equal("abcd    e", HudMarkdown.ExpandTabs("abcd\te"));
        }

        [Fact]
        public void ABlockQuotePrefixesEveryRow()
        {
            Assert.Equal(new[] { "│ first", "│ second" }, Plain("> first  \n> second"));
        }

        [Fact]
        public void AThematicBreakIsARuleRow()
        {
            IReadOnlyList<HudLine> lines = HudMarkdown.Lines("above\n\n---\n\nbelow");

            Assert.Equal(new[] { HudLineKind.Text, HudLineKind.Text, HudLineKind.Rule, HudLineKind.Text, HudLineKind.Text }, lines.Select(line => line.Kind));
        }

        [Fact]
        public void ATableIsFramedWithAPaddedGridAndABoldHeader()
        {
            string source = "| task | state |\n|---|---|\n| build | ok |\n| lint **x** | running |";
            IReadOnlyList<HudLine> lines = HudMarkdown.Lines(source);

            Assert.Equal(new[]
            {
                "┌────────┬─────────┐",
                "│ task   │ state   │",
                "├────────┼─────────┤",
                "│ build  │ ok      │",
                "│ lint x │ running │",
                "└────────┴─────────┘",
            }, Plain(source));
            Assert.All(lines[1].Runs.Where(run => run.Text.Any(char.IsLetter)), run => Assert.True(run.Style.HasFlag(HudStyle.Bold)));
            Assert.All(lines[1].Runs.Where(run => run.Text.Contains('│')), run => Assert.Equal(HudStyle.None, run.Style));
            Assert.Contains(R("x", HudStyle.Bold), lines[4].Runs);
            Assert.Single(lines.Select(line => HudMarkdown.Width(line.Runs)).Distinct());
        }

        [Fact]
        public void AnAllEmptyHeaderLeavesNoHeaderRule()
        {
            Assert.Equal(new[] { "┌───┬───┐", "│ 1 │ 2 │", "└───┴───┘" }, Plain("|  |  |\n|---|---|\n| 1 | 2 |"));
        }

        [Fact]
        public void ATableOpeningAListItemCarriesTheMarkerOnItsTopBorder()
        {
            Assert.Equal(new[] { "• ┌───┬───┐", "  │ a │ b │", "  ├───┼───┤", "  │ 1 │ 2 │", "  └───┴───┘" }, Plain("- | a | b |\n  |---|---|\n  | 1 | 2 |"));
        }

        [Fact]
        public void ATableAfterTextInAListItemHangsUnderIt()
        {
            Assert.Equal(new[] { "• intro", "  ┌───┐", "  │ a │", "  ├───┤", "  │ 1 │", "  └───┘" }, Plain("- intro\n\n  | a |\n  |---|\n  | 1 |"));
        }

        [Fact]
        public void AQuotedTableCarriesTheBarOnEveryFrameRow()
        {
            Assert.Equal(new[] { "│ ┌───┐", "│ │ a │", "│ ├───┤", "│ │ 1 │", "│ └───┘" }, Plain("> | a |\n> |---|\n> | 1 |"));
        }

        [Fact]
        public void AFrameWiderThanThePanelEndsInTheEllipsis()
        {
            IReadOnlyList<IReadOnlyList<HudRun>> rows = HudMarkdown.Rows(HudMarkdown.Lines("| alpha | beta |\n|---|---|\n| 1 | 2 |"), 60);

            List<string> clipped = HudMarkdown.Fitted(rows, 6, 10).Select(Text).ToList();

            Assert.Equal("┌────…", clipped[0]);
            Assert.All(clipped, row => Assert.Equal(6, HudLayout.CellCount(row)));
        }

        [Fact]
        public void EmptyCellsAndRowsKeepTheirPlace()
        {
            string source = "| a | b | c |\n|---|---|---|\n|  | x | y |\n| p |  | r |\n| s | t |  |\n|  |  |  |\n| u | v | w |";

            Assert.Equal(new[]
            {
                "┌───┬───┬───┐", "│ a │ b │ c │", "├───┼───┼───┤", "│   │ x │ y │", "│ p │   │ r │", "│ s │ t │   │",
                "│   │   │   │", "│ u │ v │ w │", "└───┴───┴───┘",
            }, Plain(source));
        }

        [Fact]
        public void AnImageShowsItsAltTextAndHtmlStaysLiteral()
        {
            Assert.Equal(new[] { "diagram <b>hi</b>" }, Plain("![diagram](d.png) <b>hi</b>"));
            Assert.Equal(new[] { "<!-- note -->" }, Plain("<!-- note -->"));
        }

        [Fact]
        public void InlineStylesMapOntoRunStyles()
        {
            Assert.Equal(new[]
            {
                R("b", HudStyle.Bold), R(" "), R("i", HudStyle.Italic), R(" "), R("s", HudStyle.Strikethrough), R(" "),
                R("c"), R(" "), R("label", HudStyle.Underline, "http://x"),
            }, Styled("**b** *i* ~~s~~ `c` [label](http://x)").Single());
        }

        [Fact]
        public void ALinkShowsItsUnderlinedLabelAlone()
        {
            Assert.Equal(new[] { "see the PR now" }, Plain("see [the PR](https://example.com/pr/7) now"));
            Assert.Equal(new[] { R("a b", HudStyle.Bold | HudStyle.Underline, "https://example.com") }, Styled("**[a b](https://example.com)**")[0]);
            Assert.Contains("│ a │", Plain("| h |\n|---|\n| [a](http://x) |"));
            Assert.Equal(new[] { "https://example.com/a" }, Plain("<https://example.com/a>"));
        }

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("x-custom://do")]
        [InlineData("relative/path.md")]
        [InlineData("file://other.host/tmp/x")]
        public void ALinkThePolicyIgnoresIsItsPlainLabel(string url)
        {
            Assert.Equal(new[] { R("label") }, Styled("[label](" + url + ")")[0]);
        }

        [Fact]
        public void ALocalFileLinkIsClickable()
        {
            Assert.Equal(R("log", HudStyle.Underline, "file:///tmp/build.log"), Styled("[log](file:///tmp/build.log)")[0][0]);
        }

        [Fact]
        public void ALinkTargetIsAlwaysPrintableAscii()
        {
            Assert.Equal("https://example.com/a%1Bb", HudMarkdown.LinkTarget("https://example.com/a%1Bb"));
            Assert.Equal("https://example.com/%D1%84", Styled("[x](https://example.com/%D1%84)")[0][0].Link);
        }

        [Fact]
        public void ALinkInAHeadingKeepsTheHeadingStyle()
        {
            Assert.Equal(new[] { R("t", HudStyle.Bold | HudStyle.Underline, "http://x"), R(" u", HudStyle.Bold) }, Styled("# [t](http://x) u")[0]);
        }

        [Fact]
        public void AWrappedLinkKeepsItsTargetOnEveryRow()
        {
            IReadOnlyList<IReadOnlyList<HudRun>> rows = HudMarkdown.Rows(HudMarkdown.Lines("[ab cd ef](http://x/yz)"), 6);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { R("ab cd", HudStyle.Underline, "http://x/yz") }, rows[0]);
            Assert.Equal(new[] { R("ef", HudStyle.Underline, "http://x/yz") }, rows[1]);
        }

        [Fact]
        public void AClippedLinkLeavesTheEllipsisOutsideIt()
        {
            IReadOnlyList<HudRun> row = HudMarkdown.Clipped(new[] { R("abcdef", HudStyle.Underline, "http://x") }, 4);

            Assert.Equal(new[] { R("abc", HudStyle.Underline, "http://x"), R("…") }, row);
        }

        [Fact]
        public void StrongInsideEmphasisCarriesBoth()
        {
            Assert.Equal(R("b", HudStyle.Bold | HudStyle.Italic), Styled("*a **b***")[0][^1]);
        }

        [Fact]
        public void DecodedControlCharactersAreNeutralized()
        {
            Assert.Equal(new[] { "x �[31m y" }, Plain("x &#27;[31m y"));
            Assert.Equal(new[] { "a�b" }, Plain("a&#10;b"));
            Assert.Equal(new[] { "a b" }, Plain("a&#9;b"));
        }

        [Fact]
        public void UnmatchedBracketsStayLiteral()
        {
            Assert.Equal(new[] { "[a] b ![c" }, Plain("[a] b ![c"));
        }

        [Fact]
        public void HardBreaksAndCodeNewlinesSurviveNeutralization()
        {
            Assert.Equal(new[] { "a", "b", "", "  c", "  d" }, Plain("a  \nb\n\n```\nc\nd\n```"));
        }

        [Fact]
        public void WrappingBreaksAtSpacesAcrossStyleRuns()
        {
            IReadOnlyList<IReadOnlyList<HudRun>> rows = HudMarkdown.Rows(HudMarkdown.Lines("aa **bb** cc"), 5);

            Assert.Equal(new[] { "aa bb", "cc" }, rows.Select(Text));
            Assert.Equal(new[] { R("aa "), R("bb", HudStyle.Bold) }, rows[0]);
        }

        [Fact]
        public void AWordSpanningStyleRunsStaysOneWord()
        {
            IReadOnlyList<IReadOnlyList<HudRun>> rows = HudMarkdown.Rows(HudMarkdown.Lines("pre**fix**suffix tail"), 12);

            Assert.Equal(new[] { "prefixsuffix", "tail" }, rows.Select(Text));
            Assert.Equal(R("fix", HudStyle.Bold), rows[0][1]);
        }

        [Fact]
        public void LiteralSpacingSurvivesAWideRow()
        {
            Assert.Equal(new[] { "run echo \"a  b\" now" }, LaidOut("run `echo \"a  b\"` now", 60));
            Assert.Equal(new[] { "<div title=\"a  b\">", "    indented", "</div>" }, LaidOut("<div title=\"a  b\">\n    indented\n</div>", 60));
        }

        [Fact]
        public void LiteralSpacingSurvivesWrappingAndIsDroppedOnlyAtTheBreak()
        {
            Assert.Equal(new[] { "a  b", "c" }, LaidOut("`a  b  c`", 4));
        }

        [Fact]
        public void AWordLongerThanTheRowIsSplit()
        {
            Assert.Equal(new[] { "abcd", "efgh", "ij" }, LaidOut("abcdefghij", 4));
        }

        [Fact]
        public void ListContinuationsHangUnderTheItemText()
        {
            Assert.Equal(new[] { "• one two", "  three" }, LaidOut("- one two three", 9));
            Assert.Equal(new[] { "10. alpha", "    beta" }, LaidOut("10. alpha beta", 12));
        }

        [Fact]
        public void CodeWrapsAtTheWidthKeepingItsSpaces()
        {
            Assert.Equal(new[] { "      ", "  a b" }, LaidOut("```\n    a b\n```", 6));
        }

        [Fact]
        public void ARuleSpansTheWidestOtherRowAndATableNeverWraps()
        {
            Assert.Equal(new[] { "───" }, LaidOut("---", 60));
            Assert.Equal(new[] { "│ ───" }, LaidOut("> ---", 60));
            Assert.Equal(new[] { "a heading of sorts", "", new string('─', 18) }, LaidOut("a heading of sorts\n\n---", 60));
            Assert.Equal("──────", LaidOut("a heading of sorts\n\n---", 6)[^1]);
            Assert.Equal(new[]
            {
                "┌─────────────┬───────┐", "│ long header │ other │", "├─────────────┼───────┤", "│ x           │ y     │",
                "└─────────────┴───────┘",
            }, LaidOut("| long header | other |\n|---|---|\n| x | y |", 6));
        }

        [Fact]
        public void AHeadingWithAnInnerStrongSpanLaysOutAsOneBoldRun()
        {
            IReadOnlyList<IReadOnlyList<HudRun>> rows = HudMarkdown.Rows(HudMarkdown.Lines("# **x** y"), 20);

            Assert.Equal(new[] { R("x y", HudStyle.Bold) }, rows[0]);
        }

        [Fact]
        public void ATooWideRowEndsInAnEllipsis()
        {
            HudRun[] row = { R("ab", HudStyle.Bold), R("cdef") };

            Assert.Equal(new[] { R("ab", HudStyle.Bold), R("…") }, HudMarkdown.Clipped(row, 3));
            Assert.Equal(row, HudMarkdown.Clipped(row, 6));
        }

        [Fact]
        public void OverflowingRowsEndInACountOfEveryHiddenRow()
        {
            List<IReadOnlyList<HudRun>> rows = Enumerable.Range(1, 10).Select(index => (IReadOnlyList<HudRun>)new[] { R("row " + index) }).ToList();

            IReadOnlyList<IReadOnlyList<HudRun>> fitted = HudMarkdown.Fitted(rows, 20, 5);

            Assert.Equal(5, fitted.Count);
            Assert.Equal(new[] { R("… 6 more", HudStyle.Dim) }, fitted[^1]);
            Assert.Equal(new[] { "row 1", "row 2", "row 3", "row 4" }, fitted.Take(4).Select(row => row[0].Text));
        }

        [Fact]
        public void TheOverflowMarkerIsClippedLikeAnyRow()
        {
            List<IReadOnlyList<HudRun>> rows = Enumerable.Range(1, 10).Select(index => (IReadOnlyList<HudRun>)new[] { R("row " + index) }).ToList();

            Assert.Equal(new[] { "row…", "… 9…" }, HudMarkdown.Fitted(rows, 4, 2).Select(Text));
        }

        [Fact]
        public void TinyBudgetsNeverPaintOutsideThem()
        {
            IReadOnlyList<HudRun>[] rows = { new[] { R("abc") }, new[] { R("def") } };

            Assert.Empty(HudMarkdown.Fitted(rows, 0, 5));
            Assert.Empty(HudMarkdown.Fitted(rows, 5, 0));
            Assert.Equal(HudMarkdown.Clipped(new[] { R("… 2 more", HudStyle.Dim) }, 5), HudMarkdown.Fitted(rows, 5, 1).Single());
            Assert.Equal(new[] { "…", "…" }, HudMarkdown.Fitted(rows, 1, 5).Select(Text));
        }

        [Fact]
        public void OnlyMarkupThatLaysOutToACellIsVisibleText()
        {
            Assert.True(HudMarkdown.RendersVisibleText("# done"));
            Assert.True(HudMarkdown.RendersVisibleText("---"));
            Assert.False(HudMarkdown.RendersVisibleText("[ref]: https://example.com"));
            Assert.False(HudMarkdown.RendersVisibleText("&nbsp;"));
            Assert.False(HudMarkdown.RendersVisibleText("#\n\n&nbsp; &#32;"));
        }
    }
}
