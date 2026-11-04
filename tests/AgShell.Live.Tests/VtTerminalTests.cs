namespace AgShell.Live.Tests
{
    public class VtTerminalTests
    {
        [Fact]
        public void TheSnapshotGivesTheScreenAndNotTheQueries()
        {
            using VtTerminal terminal = new VtTerminal(40, 5);
            terminal.Write("\x1b]2;title\x07\x1b[c\x1b[?2004hline one\r\n\x1b[31mred\x1b[0m\r\nprompt> ls");

            string state = terminal.Snapshot()!;

            Assert.Contains("line one", state, StringComparison.Ordinal);
            Assert.Contains("red", state, StringComparison.Ordinal);
            Assert.Contains("\x1b[?2004h", state, StringComparison.Ordinal);
            Assert.Contains("\x1b]2;title\x07", state, StringComparison.Ordinal);
            // zmx 770eb5f: the cursor style comes last, after the title.
            Assert.EndsWith("\x1b]2;title\x07\x1b[0 q", state, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b[c", state, StringComparison.Ordinal);
            Assert.Contains("\x1b[3;11H", state, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSnapshotKeepsTheScrollback()
        {
            using VtTerminal terminal = new VtTerminal(20, 3);
            for (int line = 1; line <= 10; line++)
            {
                terminal.Write("line-" + line + "\r\n");
            }

            string state = terminal.Snapshot()!;

            Assert.Contains("line-1\r\n", state, StringComparison.Ordinal);
            Assert.Contains("\x1b[2J\x1b[H\x1b[0m", state, StringComparison.Ordinal);
            Assert.Contains("line-10", state, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(80)]
        public void ATerminalFedTheSnapshotHasTheWholeHistory(int lines)
        {
            // zmx 186393f: the last screen of history reaches the scrollback before the clear, not under it.
            using VtTerminal terminal = new VtTerminal(20, 5);
            for (int line = 1; line <= lines; line++)
            {
                terminal.Write("row " + line + "\r\n");
            }

            terminal.Write("prompt>");
            using VtTerminal client = new VtTerminal(20, 5);
            client.Write(terminal.Snapshot()!);

            VtScreen original = terminal.Screen(true)!;
            VtScreen restored = client.Screen(true)!;
            Assert.Equal(original.Text, restored.Text);
            Assert.Equal(original.CursorX, restored.CursorX);
            Assert.Equal(original.CursorY, restored.CursorY);
        }

        [Fact]
        public void ATerminalFedTheSnapshotKeepsLongLinesWhole()
        {
            // Lines longer than the pane come back soft-wrapped, so copying them and a resize keep them one line.
            using VtTerminal terminal = new VtTerminal(75, 5);
            string line = new string('x', 100) + "-end";
            for (int row = 1; row <= 6; row++)
            {
                terminal.Write(row + line + "\r\n");
            }

            terminal.Write("PS>");
            using VtTerminal client = new VtTerminal(75, 5);
            client.Write(terminal.Snapshot()!);

            string restored = client.Screen(true)!.Text;
            Assert.Equal(terminal.Screen(true)!.Text, restored);
            Assert.Contains("\n3" + line + "\n", restored, StringComparison.Ordinal);
        }

        [Fact]
        public void AResizeLaysTheSnapshotOutAgain()
        {
            using VtTerminal terminal = new VtTerminal(10, 5);
            terminal.Write("0123456789abcdef");
            terminal.Resize(20, 5);

            string state = terminal.Snapshot()!;

            Assert.Contains("0123456789abcdef", state, StringComparison.Ordinal);
        }

        [Fact]
        public void TheScreenJoinsAWrappedLineAndGivesTheCursor()
        {
            // agterm's zmx patch 0001 test: a line longer than the grid wraps on screen and reads back as one line.
            using VtTerminal terminal = new VtTerminal(20, 5);
            Assert.Equal(string.Empty, terminal.Screen(false)!.Text);

            terminal.Write("0123456789abcdefghijKLMNO\r\n> ab");
            VtScreen screen = terminal.Screen(false)!;

            Assert.Equal("0123456789abcdefghijKLMNO\n> ab", screen.Text);
            Assert.Equal(20, screen.Columns);
            Assert.Equal(5, screen.Rows);
            Assert.Equal(4, screen.CursorX);
            Assert.Equal(2, screen.CursorY);
            Assert.False(screen.Alternate);
        }

        [Fact]
        public void TheScreenIsPlainTextOfWhatIsShownNow()
        {
            using VtTerminal terminal = new VtTerminal(40, 5);
            terminal.Write("\x1b[c\x1b[31mred\x1b[0m   \r\ngone\x1b[2K\rkept");

            Assert.Equal("red\nkept", terminal.Screen(false)!.Text);
        }

        [Fact]
        public void TheScreenWithAllAddsTheScrollback()
        {
            using VtTerminal terminal = new VtTerminal(20, 3);
            for (int line = 1; line <= 10; line++)
            {
                terminal.Write("line-" + line + "\r\n");
            }

            Assert.DoesNotContain("line-1\n", terminal.Screen(false)!.Text, StringComparison.Ordinal);
            Assert.StartsWith("line-1\nline-2\n", terminal.Screen(true)!.Text, StringComparison.Ordinal);
            Assert.Contains("line-10", terminal.Screen(true)!.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void TheScreenTellsTheAlternateScreen()
        {
            using VtTerminal terminal = new VtTerminal(20, 5);
            terminal.Write("shell\r\n\x1b[?1049h\x1b[Hfull screen");

            VtScreen screen = terminal.Screen(false)!;

            Assert.True(screen.Alternate);
            Assert.Equal("full screen", screen.Text);
        }
    }
}
