namespace AgShell.Core.Tests
{
    public class HudLayoutTests
    {
        private static string Text(IReadOnlyList<HudRun> row)
        {
            return string.Concat(row.Select(run => run.Text));
        }

        [Fact]
        public void ShortMessageBoxIsContentPlusPadding()
        {
            (int columns, int rows) = HudLayout.Box(new HudSpec { Message = "gathering options" });

            Assert.Equal(17 + HudLayout.HorizontalPadding * 2, columns);
            Assert.Equal(1 + HudLayout.VerticalPadding * 2, rows);
        }

        [Fact]
        public void EmptyMessageStillProducesAOneRowBox()
        {
            HudSpec spec = new HudSpec { Message = string.Empty };
            (int Columns, int Rows) box = HudLayout.Box(spec);

            Assert.Equal(1 + HudLayout.HorizontalPadding * 2, box.Columns);
            Assert.Equal(1 + HudLayout.VerticalPadding * 2, box.Rows);
            HudPaint paint = HudLayout.Paint(spec, null, box);
            Assert.Equal((5, 3, 0), (paint.Columns, paint.Rows, paint.BlockWidth));
            Assert.Empty(paint.Lines);
        }

        [Fact]
        public void LongSingleWordIsBrokenAtMaxColumns()
        {
            string word = new string('x', 130);

            List<string> lines = HudLayout.Wrap(word, HudLayout.MaxColumns);
            (int columns, int rows) = HudLayout.Box(new HudSpec { Message = word });

            Assert.Equal(new[] { new string('x', 60), new string('x', 60), new string('x', 10) }, lines);
            Assert.Equal(HudLayout.MaxColumns + HudLayout.HorizontalPadding * 2, columns);
            Assert.Equal(3 + HudLayout.VerticalPadding * 2, rows);
        }

        [Fact]
        public void WordWrapKeepsWordsWholeUpToMaxColumns()
        {
            string message = string.Join(" ", Enumerable.Repeat("word", 20));

            List<string> lines = HudLayout.Wrap(message, HudLayout.MaxColumns);

            Assert.Equal(2, lines.Count);
            Assert.Equal(string.Join(" ", Enumerable.Repeat("word", 12)), lines[0]);
            Assert.Equal(string.Join(" ", Enumerable.Repeat("word", 8)), lines[1]);
            Assert.All(lines, line => Assert.True(line.Length <= HudLayout.MaxColumns));
        }

        [Fact]
        public void DetailFollowsMessageAfterASingleEmptyLine()
        {
            HudSpec spec = new HudSpec { Message = "gathering options", Detail = "scanning 4 repositories" };

            (int Columns, int Rows) box = HudLayout.Box(spec);
            HudPaint paint = HudLayout.Paint(spec, null, box);

            Assert.Equal(new[] { "gathering options", "", "scanning 4 repositories" }, HudLayout.BodyLines(spec));
            Assert.Equal((27, 5, 0), (paint.Columns, paint.Rows, paint.BlockWidth));
            Assert.Equal(new[] { new HudRun("gathering options", HudStyle.None) }, paint.Lines[0]);
            Assert.Empty(paint.Lines[1]);
            Assert.Equal(new[] { new HudRun("scanning 4 repositories", HudStyle.Dim) }, paint.Lines[2]);
            Assert.Equal(23 + HudLayout.HorizontalPadding * 2, box.Columns);
            Assert.Equal(3 + HudLayout.VerticalPadding * 2, box.Rows);
        }

        [Fact]
        public void EmptyDetailAddsNoSeparator()
        {
            HudSpec spec = new HudSpec { Message = "working", Detail = "   " };

            Assert.Equal(new[] { "working" }, HudLayout.BodyLines(spec));
            Assert.Equal((11, 3), HudLayout.Box(spec));
        }

        [Fact]
        public void EmbeddedNewlinesBecomeHardBreaksWithNoBlankLines()
        {
            HudSpec spec = new HudSpec { Message = "one\n\ntwo three", Detail = "four\nfive" };

            Assert.Equal(new[] { "one", "two three", "", "four", "five" }, HudLayout.BodyLines(spec));
            Assert.Equal(5 + HudLayout.VerticalPadding * 2, HudLayout.Box(spec).Rows);
        }

        [Fact]
        public void SpinnerWidensTheBoxWithoutRewrapping()
        {
            (int Columns, int Rows) plain = HudLayout.Box(new HudSpec { Message = "hi" });
            (int Columns, int Rows) spinning = HudLayout.Box(new HudSpec { Message = "hi", Spinner = "braille" });

            Assert.Equal(plain.Columns + HudLayout.SpinnerWidth, spinning.Columns);
            Assert.Equal(plain.Rows, spinning.Rows);
        }

        [Fact]
        public void EachAxisMeasuresItsOwnNeed()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 1200, 800);

            Assert.Equal(15, HudLayout.WidthPercent((22, 3), pane));
            Assert.Equal(7, HudLayout.HeightPercent((22, 3), pane));
            Assert.Equal(15, HudLayout.WidthPercent((22, 20), pane));
            Assert.Equal(45, HudLayout.HeightPercent((22, 20), pane));
        }

        [Fact]
        public void AWideMessageDoesNotMakeATallPanel()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 700, 800);
            HudSpec spec = new HudSpec { Message = "waiting on the package index — this can take a minute on a cold cache", Detail = "registry.example.org" };
            (int Columns, int Rows) box = HudLayout.Box(spec);

            Assert.Equal(71, HudLayout.WidthPercent(box, pane));
            Assert.Equal(14, HudLayout.HeightPercent(box, pane));
        }

        [Fact]
        public void ACallerOverrideIsBoundedByTheSameClampTheMeasurementTakes()
        {
            Assert.Equal(HudLayout.MaxSizePercent, HudLayout.ClampSizePercent(100));
            Assert.Equal(HudLayout.MinSizePercent, HudLayout.ClampSizePercent(1));
            Assert.Equal(40, HudLayout.ClampSizePercent(40));
            Assert.Equal(HudLayout.MaxSizePercent, HudLayout.ClampSizePercent(HudLayout.MaxSizePercent));
        }

        [Fact]
        public void OnlyTheWidthTakesTheMinimumFloor()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 2000, 1000);

            Assert.Equal(HudLayout.MinSizePercent, HudLayout.WidthPercent((6, 3), pane));
            Assert.Equal(6, HudLayout.HeightPercent((6, 3), pane));
        }

        [Fact]
        public void AHugeMessageGrowsTallAndIsCappedThere()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 1200, 800);
            (int Columns, int Rows) box = HudLayout.Box(new HudSpec { Message = string.Join(" ", Enumerable.Repeat("word", 600)) });

            Assert.Equal(42, HudLayout.WidthPercent(box, pane));
            Assert.Equal(HudLayout.MaxSizePercent, HudLayout.HeightPercent(box, pane));
        }

        [Fact]
        public void UnmeasuredPaneWidensButDoesNotHeighten()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 0, 0);

            Assert.Equal(HudLayout.MaxSizePercent, HudLayout.WidthPercent((10, 3), pane));
            Assert.Equal(HudLayout.MinSizePercent, HudLayout.HeightPercent((10, 3), pane));
        }

        [Fact]
        public void ThePaintCarriesThePanelsOwnGrid()
        {
            PaneMetrics pane = new PaneMetrics(7.8, 15, 1000, 700, 8, 6);
            HudSpec spec = new HudSpec { Message = "gathering options…" };
            (int Columns, int Rows) box = HudLayout.Box(spec);
            int width = HudLayout.WidthPercent(box, pane);
            int height = HudLayout.HeightPercent(box, pane);

            (int Columns, int Rows) grid = HudLayout.PaintGrid(spec, null, new HudPanelSize(width, height), pane);

            Assert.Equal(19, width);
            Assert.Equal(9, height);
            Assert.Equal((int)((pane.PaneWidth * 0.19 - 16) / 7.8), grid.Columns);
            Assert.Equal((int)((pane.PaneHeight * 0.09 - 12) / 15), grid.Rows);
            Assert.Equal((22, 3), grid);
            Assert.Equal((22, 3), box);
            HudPaint paint = HudLayout.Paint(spec, null, grid);
            Assert.Equal((22, 3, 0), (paint.Columns, paint.Rows, paint.BlockWidth));
            Assert.Equal(1, (grid.Rows - HudLayout.BodyLines(spec).Count) / 2);
        }

        [Fact]
        public void ACallerOverrideWidensThePanelWithoutHeighteningIt()
        {
            PaneMetrics pane = new PaneMetrics(7.8, 15, 1000, 700, 8, 6);
            HudSpec spec = new HudSpec { Message = "ok" };
            HudPanelSize size = HudLayout.PanelSize(spec, HudLayout.MaxSizePercent, pane);

            (int Columns, int Rows) grid = HudLayout.PaintGrid(spec, HudLayout.MaxSizePercent, size, pane);

            Assert.Equal(HudLayout.MaxSizePercent, size.WidthPercent);
            Assert.Equal(9, size.HeightPercent);
            Assert.Equal((100, 3), grid);
            Assert.Equal((6, 3), HudLayout.Box(spec));
        }

        [Fact]
        public void PaintGridFallsBackToTheBoxWithoutAMeasuredPane()
        {
            HudSpec spec = new HudSpec { Message = "working" };
            PaneMetrics unmeasured = new PaneMetrics(8, 18, 0, 0);
            PaneMetrics noCell = new PaneMetrics(0, 0, 1000, 700);

            Assert.Null(HudLayout.PanelGrid(new HudPanelSize(40, 10), unmeasured));
            Assert.Null(HudLayout.PanelGrid(new HudPanelSize(40, 10), noCell));
            Assert.Equal(HudLayout.Box(spec), HudLayout.PaintGrid(spec, null, new HudPanelSize(80, 10), unmeasured));
            PaneMetrics tiny = new PaneMetrics(8, 18, 30, 30, 8, 6);
            Assert.Null(HudLayout.PanelGrid(new HudPanelSize(40, 40), tiny));
        }

        [Fact]
        public void WidthIsCountedInTheUnitThePageCounts()
        {
            string decomposed = "café au lait";

            IReadOnlyList<string> lines = HudLayout.BodyLines(new HudSpec { Message = decomposed });

            Assert.Single(lines);
            Assert.Equal(12, lines[0].EnumerateRunes().Count());
            Assert.Equal(12, HudLayout.CellCount(lines[0]));
            Assert.Equal(12 + HudLayout.HorizontalPadding * 2, HudLayout.Box(new HudSpec { Message = decomposed }).Columns);
        }

        [Fact]
        public void AZwjSequenceIsMeasuredInCodePointsNotClusters()
        {
            string family = "\U0001F468‍\U0001F469‍\U0001F466";

            Assert.Equal(5, HudLayout.CellCount(family));
            Assert.Equal(5 + HudLayout.HorizontalPadding * 2, HudLayout.Box(new HudSpec { Message = family }).Columns);
        }

        [Fact]
        public void AMarkdownPaintCarriesItsBlockWidthAndStyledRows()
        {
            HudPaint paint = HudLayout.Paint(new HudSpec { Message = "**hi** there", Markdown = true }, null, (20, 5));

            Assert.Equal((20, 5, 8), (paint.Columns, paint.Rows, paint.BlockWidth));
            Assert.Equal(new[] { new HudRun("hi", HudStyle.Bold), new HudRun(" there", HudStyle.None) }, paint.Lines.Single());
        }

        [Fact]
        public void AMarkdownLinkAddsNoWidthBeyondItsVisibleText()
        {
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, int blockWidth) = HudLayout.MarkdownBody(new HudSpec { Message = "[ab](http://x)", Markdown = true }, null, (40, 5));

            Assert.Equal(2, blockWidth);
            Assert.Equal(new[] { new HudRun("ab", HudStyle.Underline, "http://x") }, lines.Single());
        }

        [Theory]
        [InlineData(100)]
        [InlineData(5)]
        public void AMarkdownMessageIsMeasuredAtTheClampedWidthItIsPaintedAt(int percent)
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 576, 2000);
            HudSpec spec = new HudSpec { Message = string.Join(" ", Enumerable.Repeat("word", 24)), Markdown = true };

            HudPanelSize measured = HudLayout.PanelSize(spec, percent, pane);
            HudPanelSize taken = new HudPanelSize(HudLayout.ClampSizePercent(percent), measured.HeightPercent);
            (int Columns, int Rows) grid = HudLayout.PaintGrid(spec, percent, taken, pane);
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, _) = HudLayout.MarkdownBody(spec, percent, grid);

            Assert.DoesNotContain(lines, row => Text(row).Contains("more"));
            Assert.True(grid.Rows - lines.Count - HudLayout.VerticalPadding * 2 <= 1);
        }

        [Fact]
        public void ASpinningMarkdownBodyIndentsEveryRowAfterTheFirstByTheGutter()
        {
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, int blockWidth) = HudLayout.MarkdownBody(new HudSpec { Message = "- a\n- bb", Spinner = "bar", Markdown = true }, null, (20, 6));

            Assert.Equal(new[] { "• a", "  • bb" }, lines.Select(Text));
            Assert.Equal(4 + HudLayout.SpinnerWidth, blockWidth);
        }

        [Fact]
        public void AMarkdownBodyWrapsAtTheGridLessItsPaddingAndCountsWhatDoesNotFit()
        {
            string message = string.Join("\n", Enumerable.Range(1, 10).Select(index => "- item " + index));

            (IReadOnlyList<IReadOnlyList<HudRun>> lines, int blockWidth) = HudLayout.MarkdownBody(new HudSpec { Message = message, Markdown = true }, null, (9, 5));

            Assert.Equal(new[] { "• ite", "  m 1", "… 19…" }, lines.Select(Text));
            Assert.Equal(new[] { new HudRun("… 19", HudStyle.Dim), new HudRun("…", HudStyle.None) }, lines[2]);
            Assert.Equal(5, blockWidth);
        }

        [Fact]
        public void AMarkdownTableWiderThanThePanelIsStillClipped()
        {
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, _) = HudLayout.MarkdownBody(new HudSpec { Message = "| abcdef |\n|---|\n| x |", Markdown = true }, null, (10, 9));

            Assert.Equal("┌────…", Text(lines[0]));
        }

        [Fact]
        public void AMarkdownMessageIsMeasuredAndWrappedAtTheWidthThePaneAllows()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 400, 800);
            HudSpec spec = new HudSpec { Message = "aaaa bbbb cccc dddd eeee ffff gggg [the link](http://x) hhhh iiii jjjj", Markdown = true };

            HudPanelSize size = HudLayout.PanelSize(spec, null, pane);
            (int Columns, int Rows) grid = HudLayout.PaintGrid(spec, null, size, pane);
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, _) = HudLayout.MarkdownBody(spec, null, grid);

            Assert.Equal(38, grid.Columns);
            Assert.Equal(new[] { "aaaa bbbb cccc dddd eeee ffff gggg", "the link hhhh iiii jjjj" }, lines.Select(Text));
            Assert.Equal(new[] { new HudRun("the link", HudStyle.Underline, "http://x"), new HudRun(" hhhh iiii jjjj", HudStyle.None) }, lines[1]);
            Assert.Equal(lines.Count + HudLayout.VerticalPadding * 2, grid.Rows);
        }

        [Fact]
        public void AOneRowSpinningMarkdownBodyShowsOnlyTheMarker()
        {
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, int blockWidth) = HudLayout.MarkdownBody(new HudSpec { Message = "a\n\nb", Spinner = "bar", Markdown = true }, null, (30, 3));

            Assert.Equal(new[] { new HudRun("… 3 more", HudStyle.Dim) }, lines.Single());
            Assert.Equal(8 + HudLayout.SpinnerWidth, blockWidth);
        }

        [Fact]
        public void AnEmptyMarkdownBudgetStillWritesAMarkdownBlockWidth()
        {
            HudPaint paint = HudLayout.Paint(new HudSpec { Message = "a", Markdown = true }, null, (4, 2));

            Assert.Empty(paint.Lines);
            Assert.Equal(1, paint.BlockWidth);
        }

        [Fact]
        public void AMarkdownDetailFollowsABlankRowDimmed()
        {
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, _) = HudLayout.MarkdownBody(new HudSpec { Message = "x", Detail = "d", Markdown = true }, null, (20, 10));

            Assert.Equal(3, lines.Count);
            Assert.Equal(new[] { new HudRun("x", HudStyle.None) }, lines[0]);
            Assert.Empty(lines[1]);
            Assert.Equal(new[] { new HudRun("d", HudStyle.Dim) }, lines[2]);
        }

        [Fact]
        public void TheMarkdownBoxMeasuresTheRenderedRows()
        {
            (int columns, int rows) = HudLayout.Box(new HudSpec { Message = "# T\n\n- a\n- b", Markdown = true });

            Assert.Equal(4 + HudLayout.VerticalPadding * 2, rows);
            Assert.Equal(3 + HudLayout.HorizontalPadding * 2, columns);
        }

        [Fact]
        public void EdgeMarginLeavesRoomForTheLargestPanelOnBothAxes()
        {
            Assert.True(HudPositions.EdgeMarginPercent * 2 + HudLayout.MaxSizePercent <= 100);
            Assert.True(HudPositions.EdgeMarginPercent * 2 + HudLayout.ClampSizePercent(100) <= 100);
        }

        [Fact]
        public void AMarkdownMessageWithACallerWidthWrapsAtThatWidth()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 1600, 800);
            string line = string.Join(" ", Enumerable.Repeat("word", 20));
            HudSpec spec = new HudSpec { Message = line, Markdown = true };

            HudPanelSize size = HudLayout.PanelSize(spec, 80, pane);
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, _) = HudLayout.MarkdownBody(spec, 80, HudLayout.PaintGrid(spec, 80, size, pane));

            Assert.Equal(new[] { line }, lines.Select(Text));
            Assert.Equal(2 + HudLayout.VerticalPadding * 2, HudLayout.Box(spec, HudLayout.WrapColumns(spec, null, pane)).Rows);
        }

        public static IEnumerable<object[]> Positions => HudPositions.All.Select(position => new object[] { position });

        [Theory]
        [MemberData(nameof(Positions))]
        public void AStickyPanelMaySpanThePaneUnlessItIsCentered(string position)
        {
            HudSpec sticky = new HudSpec { Message = "caption", Position = position, Sticky = true };
            HudSpec loose = new HudSpec { Message = "caption", Position = position };
            int widest = position == "center" ? HudLayout.MaxSizePercent : 100;

            Assert.Equal(widest, HudLayout.ClampSizePercent(100, sticky));
            Assert.Equal(HudLayout.MaxSizePercent, HudLayout.ClampSizePercent(100, loose));
            Assert.Equal(HudLayout.MinSizePercent, HudLayout.ClampSizePercent(1, sticky));
        }

        [Fact]
        public void AMeasuredStickyPanelTakesTheWidthItsTextNeedsUpToThePane()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 400, 800);
            string message = new string('x', 58);

            Assert.Equal(100, HudLayout.PanelSize(new HudSpec { Message = message, Position = "top-center", Sticky = true }, null, pane).WidthPercent);
            Assert.Equal(HudLayout.MaxSizePercent, HudLayout.PanelSize(new HudSpec { Message = message, Position = "top-center" }, null, pane).WidthPercent);
        }

        [Fact]
        public void AFramelessPanelIsAsTallAsItsText()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 1200, 800, 8, 6);
            HudSpec spec = new HudSpec { Message = "- a\n- b", Markdown = true, Frame = false };

            HudPanelSize size = HudLayout.PanelSize(spec, null, pane);
            (int Columns, int Rows) grid = HudLayout.PaintGrid(spec, null, size, pane);

            Assert.Equal(2, HudLayout.Box(spec).Rows);
            Assert.Equal(1, HudLayout.Box(new HudSpec { Message = "one line", Frame = false }).Rows);
            Assert.Equal(48.0, size.HeightPoints);
            Assert.Equal(2, grid.Rows);
            Assert.Equal(new[] { "• a", "• b" }, HudLayout.MarkdownBody(spec, null, grid).Lines.Select(Text));
        }

        [Theory]
        [InlineData(400.0)]
        [InlineData(800.0)]
        [InlineData(1600.0)]
        public void TheHeightInPointsDoesNotFollowThePane(double paneHeight)
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 1200, paneHeight, 8, 6);
            HudSpec spec = new HudSpec { Message = "gathering options" };

            HudPanelSize size = HudLayout.PanelSize(spec, null, pane);

            Assert.Equal(66.0, size.HeightPoints);
            Assert.Equal(3, HudLayout.PaintGrid(spec, null, size, pane).Rows);
        }

        [Theory]
        [InlineData(15.4)]
        [InlineData(17.3)]
        [InlineData(19.7)]
        public void AFractionalCellStillYieldsTheWholeRowsThePointsWereBuiltFrom(double cellHeight)
        {
            PaneMetrics pane = new PaneMetrics(7.8, cellHeight, 1200, 900, 8, 6);
            HudSpec spec = new HudSpec { Message = "- a\n- b\n- c", Markdown = true, Frame = false };

            HudPanelSize size = HudLayout.PanelSize(spec, null, pane);

            Assert.Equal(3, HudLayout.PaintGrid(spec, null, size, pane).Rows);
        }

        [Fact]
        public void ContentPastTheHeightCapAgreesOnPointsGridBodyAndPercent()
        {
            PaneMetrics pane = new PaneMetrics(8, 18, 1200, 800, 8, 6);
            HudSpec spec = new HudSpec { Message = string.Join("\n", Enumerable.Range(1, 80).Select(index => "- item " + index)), Markdown = true };

            HudPanelSize size = HudLayout.PanelSize(spec, null, pane);
            (int Columns, int Rows) grid = HudLayout.PaintGrid(spec, null, size, pane);
            (IReadOnlyList<IReadOnlyList<HudRun>> lines, _) = HudLayout.MarkdownBody(spec, null, grid);

            Assert.Equal(640.0, size.HeightPoints);
            Assert.Equal(HudLayout.MaxSizePercent, size.HeightPercent);
            Assert.Equal((int)((640.0 - 12) / 18), grid.Rows);
            Assert.Equal(grid.Rows - HudLayout.VerticalPadding * 2, lines.Count);
            Assert.Contains("more", Text(lines[^1]));
        }

        [Fact]
        public void AnUnmeasuredPaneHasNoHeightInPoints()
        {
            Assert.Null(HudLayout.PanelSize(new HudSpec { Message = "working" }, null, new PaneMetrics(8, 18, 0, 0)).HeightPoints);
        }
    }
}
