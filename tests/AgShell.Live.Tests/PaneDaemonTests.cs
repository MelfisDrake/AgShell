using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using AgShell.Protocol;

namespace AgShell.Live.Tests
{
    public class PaneDaemonTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        [Fact]
        public async Task OpenStartsTheShellAndTheOpenerLeads()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();

            a.Send(Open());
            LiveMessage opened = await a.WaitAsync(message => message.Op == "opened");
            LiveMessage lead = await a.WaitAsync(message => message.Op == "leads");
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo agshell-live-%OS%\r" });
            await a.WaitOutputAsync("agshell-live-Windows_NT");

            Assert.Equal("pane", opened.Id);
            Assert.False(opened.Found);
            Assert.True(opened.Pid > 0);
            Assert.Equal("leader", lead.Lead);
        }

        [Fact]
        public async Task AnAttachReplaysWhatTheShellPrintedAndFollowsTheLeader()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open(columns: 100, rows: 30));
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo replay-%OS%\r" });
            await a.WaitOutputAsync("replay-Windows_NT");

            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 80, Rows = 24, Follow = true });
            LiveMessage opened = await b.WaitAsync(message => message.Op == "opened");
            await b.WaitOutputAsync("replay-Windows_NT");
            LiveMessage lead = await b.WaitAsync(message => message.Op == "leads");

            Assert.True(opened.Found);
            Assert.Equal("follower", lead.Lead);
            Assert.Equal(100, lead.Cols);
            Assert.Equal(30, lead.Rows);
        }

        [Fact]
        public async Task AnAttachBeforeTheShellOpensIsRefused()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();

            a.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 80, Rows = 24 });
            LiveMessage opened = await a.WaitAsync(message => message.Op == "opened");
            LiveMessage exited = await a.WaitAsync(message => message.Op == "exited");

            Assert.False(opened.Found);
            Assert.Equal(-1, exited.Code);
        }

        [Fact]
        public async Task AnAttachWithoutFollowTakesTheLead()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads" && message.Lead == "leader");

            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 90, Rows = 20 });

            await b.WaitAsync(message => message.Op == "leads" && message.Lead == "leader");
            LiveMessage demoted = await a.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");
            Assert.Equal(90, demoted.Cols);
            Assert.Equal(20, demoted.Rows);
        }

        [Fact]
        public async Task WhenTheLeaderGoesNobodyLeadsUntilOneJoinsAgain()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 90, Rows = 20 });
            await a.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");

            await b.DisposeAsync();
            await a.WaitAsync(message => message.Op == "leads" && message.Lead == "unowned");
            // zmx: a managed client's window changing size is not a claim, and the pane keeps the leader's size.
            a.Send(new LiveMessage { Op = "resize", Id = "pane", Cols = 70, Rows = 22 });
            a.Send(new LiveMessage { Op = "list", Request = 1 });
            await a.WaitAsync(message => message.Op == "list");
            await using Peer reader = await daemon.ConnectAsync();
            LiveMessage kept = await ScreenAsync(reader, string.Empty);
            await using Peer again = await daemon.ConnectAsync();
            again.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 70, Rows = 22, Follow = true });

            LiveMessage leads = await again.WaitAsync(message => message.Op == "leads");
            Assert.Equal((90, 20), (kept.Cols, kept.Rows));
            Assert.Equal("leader", leads.Lead);
        }

        [Fact]
        public async Task AManagedFollowersInputIsDropped()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 80, Rows = 24, Follow = true });
            await b.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");

            b.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo dropped-%OS%\r" });
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo kept-%OS%\r" });
            await a.WaitOutputAsync("kept-Windows_NT");

            Assert.DoesNotContain("dropped-Windows_NT", a.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(b.Messages, message => message.Op == "leads" && message.Lead == "leader");
        }

        [Fact]
        public async Task AnUnmanagedClientTakesTheLeadByTypingNotByItsTerminalsReplies()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Id = "pane", Cols = 70, Rows = 22, Follow = true });
            await b.WaitAsync(message => message.Op == "opened");

            b.Send(new LiveMessage { Op = "write", Id = "pane", Data = "\u001b[?62;22c" });
            b.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo typed-%OS%\r" });

            await a.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");
            await a.WaitOutputAsync("typed-Windows_NT");
            await using Peer reader = await daemon.ConnectAsync();
            LiveMessage screen = await ScreenAsync(reader, "typed-Windows_NT");
            Assert.Equal((70, 22), (screen.Cols, screen.Rows));
            Assert.Equal(1, a.Messages.Count(message => message.Op == "leads" && message.Lead == "follower"));
            Assert.DoesNotContain(b.Messages, message => message.Op == "leads");
        }

        [Fact]
        public async Task AFreshAttachTakesTheLeadBack()
        {
            await using Running daemon = Running.Start();
            Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 90, Rows = 20 });
            await a.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");

            // agterm's takeover: the covered side lets go and attaches again with the claim.
            a.Send(new LiveMessage { Op = "detach", Id = "pane" });
            await a.DisposeAsync();
            await using Peer again = await daemon.ConnectAsync();
            again.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 120, Rows = 30 });

            await again.WaitAsync(message => message.Op == "leads" && message.Lead == "leader");
            LiveMessage follower = await b.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");
            Assert.Equal(120, follower.Cols);
        }

        [Fact]
        public async Task AWindowsOpenJoinsWithoutTheClaim()
        {
            await using Running daemon = Running.Start();
            Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 90, Rows = 20 });
            await a.WaitAsync(message => message.Op == "leads" && message.Lead == "follower");
            a.Send(new LiveMessage { Op = "detach", Id = "pane" });
            await a.DisposeAsync();

            // agterm's ZmxLeadAttachment(claim: false): a window coming back finds the other machine leading and follows it.
            await using Peer again = await daemon.ConnectAsync();
            again.Send(Open(columns: 120, rows: 30) with { Follow = true });

            LiveMessage leads = await again.WaitAsync(message => message.Op == "leads");
            Assert.Equal(("follower", 90), (leads.Lead, leads.Cols));
        }

        [Fact]
        public async Task AnInjectReachesEveryViewerAndTellsTheSender()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 80, Rows = 24, Follow = true });
            await b.WaitAsync(message => message.Op == "leads");
            await using Peer sender = await daemon.ConnectAsync();

            sender.Send(new LiveMessage { Op = "inject", Id = "pane", Data = "\u001b]52;c;aW5qZWN0\u0007" });

            await a.WaitOutputAsync("aW5qZWN0");
            await b.WaitOutputAsync("aW5qZWN0");
            LiveMessage injected = await sender.WaitAsync(message => message.Op == "injected");
            Assert.True(injected.Found);
            Assert.DoesNotContain("aW5qZWN0", sender.Output);
        }

        [Fact]
        public async Task AnInjectGoesIntoThePaneTerminal()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            LiveMessage before = await ScreenAsync(a, string.Empty);
            await using Peer sender = await daemon.ConnectAsync();

            sender.Send(new LiveMessage { Op = "inject", Id = "pane", Data = "\r\ninjected-into-terminal\r\n" });
            await sender.WaitAsync(message => message.Op == "injected");

            LiveMessage after = await ScreenAsync(a, "injected-into-terminal");
            Assert.Contains("injected-into-terminal", after.Data, StringComparison.Ordinal);
            Assert.True(after.Revision > before.Revision);
        }

        [Fact]
        public async Task TypeWritesToTheShellWithoutTakingTheLead()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 80, Rows = 24, Follow = true });
            LiveMessage follows = await b.WaitAsync(message => message.Op == "leads");
            Assert.Equal("follower", follows.Lead);

            b.Send(new LiveMessage { Op = "type", Id = "pane", Request = 7, Data = "echo typed-%OS%\r" });

            LiveMessage typed = await b.WaitAsync(message => message.Op == "typed");
            Assert.Equal(7, typed.Request);
            Assert.True(typed.Found);
            await a.WaitOutputAsync("typed-Windows_NT");
            b.Send(new LiveMessage { Op = "screen", Id = "pane", Request = 8 });
            bool led = false;
            await b.WaitAsync(message =>
            {
                led |= message.Op == "leads" && message.Lead == "leader";
                return message.Op == "screen";
            });
            Assert.False(led);
        }

        [Fact]
        public async Task TypeRefusesWhatIsEmptyTooLargeOrHasNoShell()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(new LiveMessage { Op = "type", Id = "pane", Request = 1, Data = "echo early\r" });
            Assert.False((await a.WaitAsync(message => message.Op == "typed" && message.Request == 1)).Found);
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");

            a.Send(new LiveMessage { Op = "type", Id = "pane", Request = 2, Data = string.Empty });
            a.Send(new LiveMessage { Op = "type", Id = "pane", Request = 3, Data = new string('x', PaneDaemon.TypeMax + 1) });

            Assert.False((await a.WaitAsync(message => message.Op == "typed" && message.Request == 2)).Found);
            Assert.False((await a.WaitAsync(message => message.Op == "typed" && message.Request == 3)).Found);
        }

        [Fact]
        public async Task TheListGivesTheShellItsStartAndTheBuild()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            LiveMessage opened = await a.WaitAsync(message => message.Op == "opened");

            a.Send(new LiveMessage { Op = "list", Request = 7 });
            LiveMessage list = await a.WaitAsync(message => message.Op == "list");

            LivePaneInfo pane = Assert.Single(list.Panes!);
            Assert.Equal(7, list.Request);
            Assert.Equal("pane", pane.Id);
            Assert.Equal(opened.Pid, pane.Pid);
            Assert.True(pane.Attached);
            Assert.Equal(1, pane.Clients);
            Assert.NotNull(pane.Created);
            Assert.Matches("^[0-9a-f]{12}$", pane.Build);
        }

        [Fact]
        public async Task TheListGivesTheDirectoryTheTerminalLastReported()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            string start = Path.GetTempPath().TrimEnd('\\');
            LiveMessage open = Open();
            a.Send(open with { Cwd = start });
            await a.WaitAsync(message => message.Op == "opened");

            a.Send(new LiveMessage { Op = "list", Request = 1 });
            LiveMessage first = await a.WaitAsync(message => message.Op == "list" && message.Request == 1);
            a.Send(new LiveMessage { Op = "inject", Id = "pane", Data = "\u001b]7;file://otherhost/home/me%20too\u001b\\" });
            await a.WaitAsync(message => message.Op == "injected");
            a.Send(new LiveMessage { Op = "list", Request = 2 });
            LiveMessage second = await a.WaitAsync(message => message.Op == "list" && message.Request == 2);

            // OSC 9;9 carries a plain path, cmd's as it is and in quotes from prompts written for Windows Terminal.
            a.Send(new LiveMessage { Op = "inject", Id = "pane", Data = "\u001b]9;9;\"C:\\Program Files\"\u001b\\" });
            await a.WaitAsync(message => message.Op == "injected");
            a.Send(new LiveMessage { Op = "list", Request = 3 });
            LiveMessage third = await a.WaitAsync(message => message.Op == "list" && message.Request == 3);

            Assert.Equal(Osc7Cwd.ToUri(start, Environment.MachineName), Assert.Single(first.Panes!).Cwd);
            Assert.Equal("file://otherhost/home/me%20too", Assert.Single(second.Panes!).Cwd);
            Assert.Equal("file://" + Environment.MachineName + "/C:/Program%20Files", Assert.Single(third.Panes!).Cwd);
        }

        [Fact]
        public async Task TheListIsEmptyBeforeTheShellOpens()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();

            a.Send(new LiveMessage { Op = "list", Request = 1 });
            LiveMessage list = await a.WaitAsync(message => message.Op == "list");

            Assert.Empty(list.Panes!);
        }

        [Fact]
        public async Task TheScreenReadsTheDaemonsTerminalAtTheLeadersSize()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open(columns: 101, rows: 33));
            // The prompt asks DA1: the raw output carries the query, the terminal's text does not.
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "prompt $E[c$G\r" });
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo screen-%OS%\r" });
            await a.WaitOutputAsync("screen-Windows_NT");
            await using Peer reader = await daemon.ConnectAsync();

            LiveMessage screen = await ScreenAsync(reader, "screen-Windows_NT");

            Assert.True(screen.Found);
            Assert.Contains("\x1b[c", a.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b", screen.Data, StringComparison.Ordinal);
            Assert.Equal(101, screen.Cols);
            Assert.Equal(33, screen.Rows);
            Assert.False(screen.Alternate);
            Assert.True(screen.Revision > 0);
            Assert.NotNull(screen.CursorX);
            Assert.NotNull(screen.CursorY);
        }

        [Fact]
        public async Task TheScreenDoesNotShowWhatWasErased()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo gone-%OS%\r" });
            await a.WaitOutputAsync("gone-Windows_NT");
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "cls\r" });
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo kept-%OS%\r" });
            await a.WaitOutputAsync("kept-Windows_NT");

            LiveMessage screen = await ScreenAsync(a, "kept-Windows_NT");

            Assert.DoesNotContain("gone-Windows_NT", screen.Data, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheScreenWithAllGivesTheHistoryToo()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "for /L %i in (1,1,300) do @echo filler-line-%i\r" });
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo tail-%OS%\r" });
            await a.WaitOutputAsync("tail-Windows_NT");

            LiveMessage visible = await ScreenAsync(a, "tail-Windows_NT");
            LiveMessage all = await ScreenAsync(a, "tail-Windows_NT", all: true);

            Assert.DoesNotContain("filler-line-150\n", visible.Data, StringComparison.Ordinal);
            Assert.Contains("filler-line-150\n", all.Data, StringComparison.Ordinal);
            Assert.True(visible.Data!.Split('\n').Length <= 24);
        }

        [Fact]
        public async Task TheRevisionMovesWithTheOutput()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo one-%OS%\r" });
            LiveMessage first = await ScreenAsync(a, "one-Windows_NT");
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo two-%OS%\r" });
            LiveMessage second = await ScreenAsync(a, "two-Windows_NT");

            Assert.True(second.Revision > first.Revision);
        }

        [Fact]
        public async Task TheScreenBeforeTheShellOpensFindsNothing()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();

            a.Send(new LiveMessage { Op = "screen", Id = "pane", Request = 1 });
            LiveMessage screen = await a.WaitAsync(message => message.Op == "screen");

            Assert.False(screen.Found);
            Assert.Null(screen.Data);
        }

        [Fact]
        public async Task AStopEndsTheShellTellsTheOthersAndTheDaemonGoes()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");
            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 80, Rows = 24, Follow = true });
            await b.WaitAsync(message => message.Op == "leads");

            a.SendLast(new LiveMessage { Op = "stop", Id = "pane" });

            LiveMessage exited = await b.WaitAsync(message => message.Op == "exited");
            Assert.Equal(-1, exited.Code);
            await daemon.Run.WaitAsync(Timeout);
        }

        [Fact]
        public async Task AStopWithARequestIsConfirmedAsZmxsKilledSession()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");

            a.SendLast(new LiveMessage { Op = "stop", Id = "pane", Request = 4 });

            LiveMessage killed = await a.WaitAsync(message => message.Op == "killed");
            Assert.Equal((4, "pane"), (killed.Request, killed.Id));
            await daemon.Run.WaitAsync(Timeout);
        }

        [Fact]
        public async Task TheDaemonGoesWithItsShell()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open());
            await a.WaitAsync(message => message.Op == "leads");

            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "exit 5\r" });

            LiveMessage exited = await a.WaitAsync(message => message.Op == "exited");
            Assert.Equal(5, exited.Code);
            await daemon.Run.WaitAsync(Timeout);
        }

        [Fact]
        public async Task ADaemonNobodyOpensGoesAfterItsGrace()
        {
            await using Running daemon = Running.Start(openGrace: TimeSpan.FromMilliseconds(300));

            await daemon.Run.WaitAsync(Timeout);
        }

        [Fact]
        public async Task ASecondDaemonOnThePipeLeavesAtOnce()
        {
            string pipe = Running.NewPipeName();
            Task<int> first = Task.Run(() => Program.Main(new[] { pipe }));
            await Running.WaitForPipeAsync(pipe);

            int second = await Task.Run(() => Program.Main(new[] { pipe })).WaitAsync(Timeout);

            Assert.Equal(0, second);
            await using (Peer a = await Peer.ConnectAsync(pipe))
            {
                a.SendLast(new LiveMessage { Op = "stop", Id = "pane" });
                await first.WaitAsync(Timeout);
            }
        }

        [Fact]
        public async Task TheLogTellsTheShellsLife()
        {
            string log = Path.Combine(Path.GetTempPath(), "agshell-live-test-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                await using (Running daemon = Running.Start(log: new LiveLog(log)))
                {
                    await using Peer a = await daemon.ConnectAsync();
                    a.Send(Open());
                    await a.WaitAsync(message => message.Op == "leads");
                    a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "exit 5\r" });
                    await a.WaitAsync(message => message.Op == "exited");
                    await daemon.Run.WaitAsync(Timeout);
                }

                string text;
                using (StreamReader reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                {
                    text = await reader.ReadToEndAsync();
                }
                Assert.Matches(@"^\[\d+\] \[info\] \(default\): daemon started session=agshell-live-test-", text);
                Assert.Contains("[info] (default): client connected client=1 total=1", text);
                Assert.Contains("shell started session=", text);
                Assert.Contains("exit_code=5", text);
                Assert.Contains("[info] (default): daemon loop shutdown\n", text);
            }
            finally
            {
                File.Delete(log);
            }
        }

        [Fact]
        public async Task AReattachGetsTheTerminalStateNotTheShellsQueries()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open(columns: 100, rows: 30));
            // Every prompt asks DA1, as a shell starting asks the terminal it runs in.
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "prompt $E[c$G\r" });
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "echo state-%OS%\r" });
            await a.WaitOutputAsync("state-Windows_NT");
            a.Send(new LiveMessage { Op = "detach", Id = "pane" });

            await using Peer b = await daemon.ConnectAsync();
            b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 100, Rows = 30 });
            await b.WaitAsync(message => message.Op == "opened");
            await b.WaitOutputAsync("state-Windows_NT");

            Assert.Contains("\x1b[c", a.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b[c", b.Output, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AfterAReattachTheShellWritesWhereThePaneTerminalHasItsCursor()
        {
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            a.Send(Open(columns: 40, rows: 6));
            // A short prompt and the cursor on the bottom row, where a step down of ConPTY's size scrolls its screen.
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "cd /d C:\\\r" });
            a.Send(new LiveMessage { Op = "write", Id = "pane", Data = "for /L %i in (1,1,12) do @echo row-%i-%OS%\r" });
            await a.WaitOutputAsync("row-12-Windows_NT");
            await ScreenAsync(a, "row-12-Windows_NT\n\n");
            a.Send(new LiveMessage { Op = "detach", Id = "pane" });

            for (int round = 0; round < 2; round++)
            {
                await using Peer b = await daemon.ConnectAsync();
                b.Send(new LiveMessage { Op = "attach", Managed = true, Id = "pane", Cols = 40, Rows = 6 });
                await b.WaitAsync(message => message.Op == "opened");
                await Task.Delay(500);
                b.Send(new LiveMessage { Op = "type", Id = "pane", Request = 1, Data = "echo probe-" + round + "-%OS%\r" });
                await b.WaitOutputAsync("probe-" + round + "-Windows_NT");

                LiveMessage screen = await ScreenAsync(b, "probe-" + round + "-Windows_NT\n\n");
                Assert.Contains(">echo probe-" + round + "-%OS%\n", screen.Data, StringComparison.Ordinal);
                b.Send(new LiveMessage { Op = "detach", Id = "pane" });
            }
        }

        [Fact]
        public async Task WithNoClientTheDaemonAnswersDeviceAttributes()
        {
            using DeviceAttributesProbe probe = new DeviceAttributesProbe();
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            await StartDeviceAttributesProbeAsync(a, probe);
            a.Send(new LiveMessage { Op = "detach", Id = "pane" });
            a.Send(new LiveMessage { Op = "list", Request = 1 });
            await a.WaitAsync(message => message.Op == "list" && message.Request == 1);

            await using Peer c = await daemon.ConnectAsync();
            probe.Release();
            Assert.Contains("probe-query", await ScreenWithAsync(c, "probe-query"), StringComparison.Ordinal);

            Assert.Contains("got:ESC[?62;22c:end", await ScreenWithAsync(c, ":end"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task WithAClientShowingThePaneTheDaemonDoesNotAnswer()
        {
            using DeviceAttributesProbe probe = new DeviceAttributesProbe();
            await using Running daemon = Running.Start();
            await using Peer a = await daemon.ConnectAsync();
            await StartDeviceAttributesProbeAsync(a, probe);
            probe.Release();
            await a.WaitOutputAsync("probe-query\u001b[c");
            await a.WaitOutputAsync(":end");

            Assert.Contains("got::end", a.Output, StringComparison.Ordinal);
        }

        private static async Task StartDeviceAttributesProbeAsync(Peer peer, DeviceAttributesProbe probe)
        {
            peer.Send(new LiveMessage { Op = "open", Managed = true, Id = "pane", CommandLine = probe.CommandLine, Cols = 80, Rows = 24 });
            StringBuilder startup = new StringBuilder();
            int answeredThrough = 0;
            using CancellationTokenSource limit = new CancellationTokenSource(Timeout);
            while (true)
            {
                LiveMessage message;
                try
                {
                    message = await peer.WaitAsync(message => message.Op == "output").WaitAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail("DA1 probe did not become ready; startup output: " + startup.ToString().Replace("\u001b", "ESC"));
                    throw;
                }
                startup.Append(message.Data);
                string text = startup.ToString();
                int query;
                while ((query = text.IndexOf("\u001b[c", answeredThrough, StringComparison.Ordinal)) >= 0)
                {
                    peer.Send(new LiveMessage { Op = "write", Id = "pane", Data = "\u001b[?62;22c" });
                    answeredThrough = query + 3;
                }

                if (text.Contains("probe-ready", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        // Startup DA1 belongs to ConPTY/PowerShell. Only after ready does the gate permit the query under test.
        private sealed class DeviceAttributesProbe : IDisposable
        {
            private readonly string gate = Path.Combine(Path.GetTempPath(), "agshell-da1-" + Guid.NewGuid().ToString("N"));

            public string CommandLine => "powershell.exe -NoProfile -Command \"$null = [Console]::KeyAvailable; " +
                "[Console]::Write('probe-ready'); while (!(Test-Path -LiteralPath '" + gate.Replace("'", "''") + "')) { Start-Sleep -Milliseconds 20 }; " +
                "[Console]::Write('probe-query' + [char]27 + '[c'); $got = ''; $end = [DateTime]::UtcNow.AddSeconds(4); " +
                "while ([DateTime]::UtcNow -lt $end) { if ([Console]::KeyAvailable) { $got += [Console]::ReadKey($true).KeyChar } else { Start-Sleep -Milliseconds 20 } }; " +
                "'got:' + ($got -replace [char]27, 'ESC') + ':end'; Start-Sleep 30\"";

            public void Release() => File.WriteAllText(gate, string.Empty);

            public void Dispose() => File.Delete(gate);
        }

        /// <summary>The pane's screen text once it holds the text, or the last one read when the wait runs out.</summary>
        private static async Task<string> ScreenWithAsync(Peer peer, string text)
        {
            return (await ScreenAsync(peer, text)).Data ?? string.Empty;
        }

        /// <summary>The pane's screen once its text holds <paramref name="text"/>, or the last one read when the wait runs out.</summary>
        private static async Task<LiveMessage> ScreenAsync(Peer peer, string text, bool all = false)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            int request = 0;
            LiveMessage screen;
            do
            {
                int asked = ++request;
                peer.Send(new LiveMessage { Op = "screen", Id = "pane", Request = asked, All = all });
                screen = await peer.WaitAsync(message => message.Op == "screen" && message.Request == asked);
                if (screen.Data?.Contains(text, StringComparison.Ordinal) == true)
                {
                    break;
                }

                await Task.Delay(100);
            }
            while (DateTime.UtcNow < deadline);

            return screen;
        }

        [Fact]
        public void ADaemonNeedsItsPipe()
        {
            Assert.Equal(2, Program.Main(Array.Empty<string>()));
        }

        private static LiveMessage Open(int columns = 80, int rows = 24)
        {
            return new LiveMessage { Op = "open", Managed = true, Id = "pane", CommandLine = "cmd.exe", Cols = columns, Rows = rows };
        }

        /// <summary>A daemon served in the test's process on a pipe of its own.</summary>
        private sealed class Running : IAsyncDisposable
        {
            private Running(string pipe, Task run)
            {
                Pipe = pipe;
                Run = run;
            }

            public string Pipe { get; }

            public Task Run { get; }

            public static Running Start(TimeSpan? openGrace = null, LiveLog? log = null)
            {
                string pipe = NewPipeName();
                PaneDaemon daemon = new PaneDaemon(pipe, openGrace, log);
                return new Running(pipe, Task.Run(daemon.RunAsync));
            }

            public static string NewPipeName() => "agshell-live-test-" + Guid.NewGuid().ToString("N");

            public static async Task WaitForPipeAsync(string pipe)
            {
                for (int attempt = 0; attempt < 100 && !Directory.GetFiles(@"\\.\pipe\").Any(name => name.EndsWith(@"\" + pipe, StringComparison.OrdinalIgnoreCase)); attempt++)
                {
                    await Task.Delay(50);
                }
            }

            public Task<Peer> ConnectAsync() => Peer.ConnectAsync(Pipe);

            public async ValueTask DisposeAsync()
            {
                if (Run.IsCompleted)
                {
                    return;
                }

                // A daemon already on its way out takes no one new; waiting for a connection then would only hide why the test failed.
                Task<Peer> connecting = Peer.ConnectAsync(Pipe);
                if (await Task.WhenAny(connecting, Run.WaitAsync(Timeout)) == connecting)
                {
                    await using (Peer stopper = await connecting)
                    {
                        stopper.SendLast(new LiveMessage { Op = "stop", Id = "pane" });
                    }
                }
                else
                {
                    _ = connecting.ContinueWith(task => task.Result.DisposeAsync(), TaskContinuationOptions.OnlyOnRanToCompletion);
                }

                await Run.WaitAsync(Timeout);
            }
        }

        /// <summary>A client of the daemon: what it sends, and every message it got, in order.</summary>
        private sealed class Peer : IAsyncDisposable
        {
            private readonly NamedPipeClientStream pipe;
            private readonly StreamWriter writer;
            private readonly Channel<LiveMessage> received = Channel.CreateUnbounded<LiveMessage>();
            private readonly StringBuilder output = new StringBuilder();
            private readonly List<LiveMessage> messages = new List<LiveMessage>();
            private readonly Task reading;

            private Peer(NamedPipeClientStream pipe)
            {
                this.pipe = pipe;
                writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                reading = Task.Run(ReadAsync);
            }

            public string Output
            {
                get
                {
                    lock (output)
                    {
                        return output.ToString();
                    }
                }
            }

            /// <summary>Every message got so far, waited for or not.</summary>
            public List<LiveMessage> Messages
            {
                get
                {
                    lock (messages)
                    {
                        return messages.ToList();
                    }
                }
            }

            public static async Task<Peer> ConnectAsync(string name)
            {
                NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync((int)Timeout.TotalMilliseconds);
                return new Peer(pipe);
            }

            public void Send(LiveMessage message)
            {
                writer.WriteLine(LiveJson.Serialize(message));
            }

            /// <summary>
            /// Sends what the daemon answers by closing the connection: the close can come between the line's write and its
            /// flush, which is the daemon done, not a failure.
            /// </summary>
            public void SendLast(LiveMessage message)
            {
                try
                {
                    Send(message);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                }
            }

            /// <summary>The first message not yet taken that matches; the ones before it are passed over.</summary>
            public async Task<LiveMessage> WaitAsync(Func<LiveMessage, bool> match)
            {
                using CancellationTokenSource limit = new CancellationTokenSource(Timeout);
                while (true)
                {
                    LiveMessage message = await received.Reader.ReadAsync(limit.Token);
                    if (match(message))
                    {
                        return message;
                    }
                }
            }

            public async Task WaitOutputAsync(string text)
            {
                DateTime deadline = DateTime.UtcNow + Timeout;
                while (!Output.Contains(text, StringComparison.Ordinal))
                {
                    Assert.True(DateTime.UtcNow < deadline, "no output with " + text + " in: " + Output);
                    await Task.Delay(50);
                }
            }

            private async Task ReadAsync()
            {
                using StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false));
                try
                {
                    while (await reader.ReadLineAsync() is string line)
                    {
                        LiveMessage message = LiveJson.Parse(line)!;
                        if (message.Op == "output")
                        {
                            lock (output)
                            {
                                output.Append(message.Data);
                            }
                        }

                        lock (messages)
                        {
                            messages.Add(message);
                        }

                        received.Writer.TryWrite(message);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                }

                received.Writer.TryComplete();
            }

            public async ValueTask DisposeAsync()
            {
                await pipe.DisposeAsync();
                await reading.WaitAsync(Timeout);
            }
        }
    }
}
