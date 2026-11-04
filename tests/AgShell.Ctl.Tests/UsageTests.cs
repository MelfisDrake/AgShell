using AgShell.Protocol;

namespace AgShell.Ctl.Tests
{
    public class UsageTests
    {
        private static (int Code, string Error) Run(params string[] args)
        {
            TextWriter original = Console.Error;
            using StringWriter error = new StringWriter();
            Console.SetError(error);
            try
            {
                return (Program.Main(args), error.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetError(original);
            }
        }

        [Fact]
        public void UsageErrorsExit64WithTheUsageLine()
        {
            (int code, string error) = Run("session", "type", "--pipe", "agshell-test-none");
            Assert.Equal(64, code);
            Assert.Equal(
                "Error: provide TEXT or --stdin\n"
                + "Usage: agshellctl session type [<text>] [--stdin] [--select] [--pane <pane>] [--target <target>] [--pipe <pipe>] [--json] [--window <window>] [--pane-id <pane-id>]\n"
                + "  See 'agshellctl session type --help' for more information.\n",
                error);
        }

        [Theory]
        [InlineData("agshellctl session <subcommand>", "session")]
        [InlineData("agshellctl session new <options>", "session", "new")]
        [InlineData("agshellctl session overlay open [<options>] [<command>]", "session", "overlay", "open")]
        [InlineData("agshellctl events [--pipe <pipe>] [--json] [--kind <kind-fields> ...] [--run <run-id>] [--after <after>] [--limit <limit>]", "events")]
        [InlineData("agshellctl clipboard set [<text>]", "clipboard", "set")]
        [InlineData("agshellctl session close [--target <targets> ...] [--pipe <pipe>] [--json] [--window <window>]", "session", "close")]
        [InlineData("agshellctl session flag [<mode>] [--target <target> ...] [--pipe <pipe>] [--json] [--window <window>]", "session", "flag")]
        [InlineData("agshellctl dashboard [<ids> ...] [--font-size <font-size>] [--auto-size] [--mru] [--close] [--pipe <pipe>] [--json] [--window <window>]", "dashboard")]
        [InlineData("agshellctl window rename <id> <name> [--pipe <pipe>] [--json]", "window", "rename")]
        [InlineData("agshellctl window minimize [<id>] [<mode>] [--pipe <pipe>] [--json]", "window", "minimize")]
        [InlineData("agshellctl live attach <host> <session> [--window <window>] [--pipe <pipe>] [--json]", "live", "attach")]
        [InlineData("agshellctl font inc [--target <target>] [--pipe <pipe>] [--json] [--window <window>] [--pane <pane>]", "font", "inc")]
        [InlineData("agshellctl live kill --pane <pane> [--force] --target <target> [--window <window>] [--pipe <pipe>] [--json]", "live", "kill")]
        public void UsageFollowsArgumentParser(string expected, params string[] names)
        {
            List<System.CommandLine.Command> path = new List<System.CommandLine.Command> { Commands.Build() };
            foreach (string name in names)
            {
                path.Add(path[^1].Subcommands.Single(command => command.Name == name));
            }

            Assert.Equal(expected, Commands.UsageLine(path));
        }

        [Fact]
        public void ADefaultSubcommandShowsItsOwnUsage()
        {
            (int code, string error) = Run("sidebar", "show", "extra", "--pipe", "agshell-test-none");
            Assert.Equal(64, code);
            Assert.Contains("\nUsage: agshellctl sidebar visibility [<mode>] [--pipe <pipe>] [--json]\n  See 'agshellctl sidebar visibility --help'", error);
        }

        [Theory]
        [InlineData("use either --after or --before, not both", "session", "new", "--after", "a", "--before", "b")]
        [InlineData("provide a destination workspace, --to, or --after/--before", "session", "move")]
        [InlineData("mode must be on, off, toggle, or clear", "session", "flag", "bad")]
        [InlineData("provide --pane-id or --pane", "session", "restart")]
        [InlineData("--pane must be left, right, or scratch", "session", "restart", "--pane", "bad")]
        [InlineData("use either --all or --lines, not both", "session", "text", "--all", "--lines", "2", "--pane", "bad")]
        [InlineData("--pane must be left, right, or scratch", "font", "inc", "--pane", "bad")]
        [InlineData("--axis must be vertical or horizontal", "session", "split", "--axis", "diagonal")]
        [InlineData("color must be a #rrggbb hex value", "session", "status", "idle", "--color", "red", "--shape", "blob")]
        [InlineData("text must be 1–256 characters", "session", "background", "text", "")]
        [InlineData("provide at least one --until status", "session", "wait")]
        [InlineData("invalid status: idle,done (expected idle, active, completed or blocked)", "session", "wait", "--until", "idle,done")]
        [InlineData("--timeout must be a positive number of seconds", "session", "wait", "--until", "idle", "--timeout", "0")]
        [InlineData("events.read requires --run and --after together", "events", "--after", "1")]
        [InlineData("invalid event run id", "events", "--run", "x", "--after", "1")]
        [InlineData("invalid event cursor", "events", "--run", "8A5C3F1E-0000-4000-8000-000000000001", "--after", "-1")]
        [InlineData("event limit must be between 1 and 1000", "events", "--limit", "0")]
        [InlineData("invalid event kind: ", "events", "--kind", "status,")]
        [InlineData("ask.open requires a title", "ask", " ")]
        [InlineData("position must be one of: top-left, top-center, top-right, center-left, center, center-right, bottom-left, bottom-center, bottom-right, top, bottom", "session", "hud", "open", "hi", "--position", "middle")]
        [InlineData("provide exactly one of COMMAND, --html or --url", "session", "overlay", "open")]
        [InlineData("--width/--height need --size-percent", "session", "overlay", "open", "x", "--width", "10")]
        [InlineData("provide --size-percent PERCENT, --width/--height POINTS or --full", "session", "overlay", "resize")]
        [InlineData("mode must be browser or overlay", "browser", "links", "tab")]
        [InlineData("Pass either a NAME or --light, not both.", "theme", "set", "a", "--light", "b")]
        [InlineData("dashboard requires at least one session id (or --mru, or --close)", "dashboard")]
        [InlineData("points must be a number", "sidebar", "width", "NaN")]
        [InlineData("Unrecognized command or argument '--window'.", "version", "--window", "x")]
        [InlineData("Unrecognized command or argument '--window'.", "window", "close", "--window", "x", "id")]
        [InlineData("Unrecognized command or argument '--window=x'.", "window", "close", "--window=x", "id")]
        [InlineData("Unrecognized command or argument '--json'.", "clipboard", "set", "--json", "text")]
        [InlineData("Unrecognized command or argument '--pipe'.", "clipboard", "set", "--pipe", "x", "text")]
        [InlineData("Unrecognized command or argument '--json'.", "--json", "clipboard", "set", "text")]
        [InlineData("Unrecognized command or argument '--pipe'.", "--pipe", "x", "clipboard", "set", "text")]
        [InlineData("Cannot parse argument 'x' for option '--lines' as expected type 'System.Nullable`1[System.Int32]'.", "session", "text", "--lines", "x")]
        [InlineData("--pane requires a session", "ask", "T", "--style", "gui", "--pane", "left")]
        [InlineData("width must be 10 to 100", "ask", "T", "--width", "-5")]
        [InlineData("--page cannot be combined with --pane", "session", "overlay", "result", "--page", "p", "--pane", "left")]
        [InlineData("--pane must be left or right", "session", "overlay", "navigate", "back", "--pane", "scratch")]
        [InlineData("--force is required to end the processes in the affected live sessions", "live", "reset")]
        public void SessionChecksRunBeforeAnythingIsSent(string expected, params string[] args)
        {
            (int code, string error) = Run(args.Append("--pipe").Append("agshell-test-none").ToArray());
            Assert.Equal(64, code);
            Assert.StartsWith("Error: " + expected + "\n", error);
        }

        [Fact]
        public void ParseErrorsNameTheUnknownWord()
        {
            (int code, string error) = Run("session", "bogus");
            Assert.Equal(64, code);
            Assert.StartsWith("Error: Unrecognized command or argument 'bogus'.\nUsage: agshellctl session ", error);
        }

        [Fact]
        public void AMissingPipeIsAClientFailure()
        {
            (int code, string error) = Run("tree", "--pipe", "agshell-test-none-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(1, code);
            Assert.StartsWith("Error: connect(\\\\.\\pipe\\agshell-test-none-", error);
        }

        [Fact]
        public void ThePipeAndJsonGoBeforeTheCommandNameToo()
        {
            (int code, string error) = Run("--json", "--pipe", "agshell-test-none-" + Guid.NewGuid().ToString("N"), "tree");
            Assert.Equal(1, code);
            Assert.StartsWith("Error: connect(\\\\.\\pipe\\agshell-test-none-", error);
        }

        [Fact]
        public void PickItemsSplitAtEveryLineBreakAndDropBlankLines()
        {
            List<ControlPickItem>? items = Commands.PickItems(System.Text.Encoding.UTF8.GetBytes("a\r\nb\rc\u2028d\n \t\n\u00a0\n e "));
            Assert.Equal(new[] { "a", "b", "c", "d", " e " }, items!.Select(item => item.Id));
        }

        [Fact]
        public void PickItemsRefuseTextThatIsNotUtf8()
        {
            Assert.Null(Commands.PickItems(new byte[] { (byte)'a', 0xff }));
        }

        [Fact]
        public void PickItemsReadAJsonArray()
        {
            List<ControlPickItem>? items = Commands.PickItems(System.Text.Encoding.UTF8.GetBytes(" \n[{\"id\":\"x\",\"label\":\"X\"}]"));
            Assert.Equal("X", Assert.Single(items!).Label);
        }

        [Fact]
        public void PollingIsFastForTheFirstSecond()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100), Commands.PollDelay(10));
            Assert.Equal(TimeSpan.FromMilliseconds(500), Commands.PollDelay(11));
        }

        [Fact]
        public void HelpPrintsTheDiscussionUnderTheDescription()
        {
            TextWriter original = Console.Out;
            using StringWriter output = new StringWriter();
            Console.SetOut(output);
            int code;
            try
            {
                code = Program.Main(new[] { "dashboard", "--help" });
            }
            finally
            {
                Console.SetOut(original);
            }

            string help = output.ToString().Replace("\r\n", "\n");
            Assert.Equal(0, code);
            Assert.Contains("  Open a view-only grid of live sessions, or --close the open one.\n\n  dashboard S1 S2 S3                 open a grid", help);
            Assert.True(help.IndexOf("The 9-cell cap counts PANES", StringComparison.Ordinal) < help.IndexOf("Usage:", StringComparison.Ordinal));
        }
    }
}
