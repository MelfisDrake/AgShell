using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class DashboardTests
    {
        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(3, 2, 2)]
        [InlineData(5, 3, 2)]
        [InlineData(9, 3, 3)]
        [InlineData(20, 3, 3)]
        public void GridIsAsWideAsTheSquareRoot(int count, int cols, int rows)
        {
            Assert.Equal((cols, rows), DashboardLayout.Grid(count));
        }

        [Fact]
        public void HighlightStopsAtEdgesAndTheRaggedRow()
        {
            Assert.Equal(0, DashboardLayout.Move(0, "left", 3, 5));
            Assert.Equal(2, DashboardLayout.Move(2, "right", 3, 5));
            Assert.Equal(2, DashboardLayout.Move(2, "down", 3, 5));
            Assert.Equal(4, DashboardLayout.Move(1, "down", 3, 5));
            Assert.Equal(1, DashboardLayout.Move(4, "up", 3, 5));
            Assert.Equal(4, DashboardLayout.Move(4, "right", 3, 5));
        }

        [Fact]
        public void AutoSizeShrinksWithTheGrid()
        {
            Assert.Equal(14, DashboardLayout.FontSize(1, 1, 14));
            Assert.Equal(11, DashboardLayout.FontSize(2, 2, 14));
            Assert.Equal(8, DashboardLayout.FontSize(3, 3, 14));
            Assert.Equal(6, DashboardLayout.FontSize(3, 3, 8));
        }

        [Fact]
        public async Task CellsCountPanesAndReportWhatDidNotFit()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(a.Id, true);

            Assert.Equal("dashboard requires at least one session id", (await Send(dispatcher, new ControlArgs())).Error);
            ControlResponse opened = await Send(dispatcher, new ControlArgs { Targets = new List<string> { a.Id.Wire(), b.Id + ":right", b.Id.Wire() }, AutoSize = true });

            Assert.Equal("unresolved: " + b.Id + ":right", opened.Result!.Text);
            Assert.Equal(new[] { new DashboardCell(a.Id, PaneRole.Main), new DashboardCell(a.Id, PaneRole.Split), new DashboardCell(b.Id, PaneRole.Main) }, model.Dashboard!.Cells);

            ControlTree tree = (await dispatcher.HandleAsync(new ControlRequest { Cmd = "tree" })).Result!.Tree!;
            Assert.Equal(a.Id.Wire() + ":right", tree.DashboardMembers![1]);
            Assert.Equal(a.Id.Wire() + ":left", tree.DashboardHighlighted);

            model.MoveDashboardHighlight("right");
            model.CloseSplit(a.Id);
            Assert.Equal(new[] { new DashboardCell(a.Id, PaneRole.Main), new DashboardCell(b.Id, PaneRole.Main) }, model.Dashboard!.Cells);
            // agterm's reconcile: the highlighted cell went, so the first one takes it.
            Assert.Equal(0, model.Dashboard.Highlight);

            model.SetZoom(true);
            Assert.Null(model.Dashboard);
        }

        [Fact]
        public async Task ActivatingACellJumpsIntoThatPane()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(a.Id, true);
            model.FocusPane(a.Id, PaneRole.Main);
            Session b = model.AddSession(work.Id, "cmd", null);

            await Send(dispatcher, new ControlArgs { Mru = true });
            Assert.Equal(new[] { b.Id, a.Id, a.Id }, model.Dashboard!.Cells.Select(cell => cell.SessionId));

            model.ActivateDashboardCell(2);

            Assert.Null(model.Dashboard);
            Assert.Equal(a.Id, model.SelectedSessionId);
            Assert.Equal(PaneRole.Split, a.FocusedPane);
            Assert.True(model.Zoomed == false);
        }

        [Fact]
        public async Task AClickedCellIsEnteredOnlyWhileItStillHoldsTheHighlight()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            await Send(dispatcher, new ControlArgs { Mru = true });

            // agterm's clickDashboardMember: the highlight moves at once, and an arrow in the gap wins over the click.
            DashboardCell clicked = model.HighlightDashboardCell(1)!;
            Assert.Equal(1, model.Dashboard!.Highlight);
            model.MoveDashboardHighlight("left");
            model.EnterDashboardCellIfHighlighted(clicked);
            Assert.NotNull(model.Dashboard);

            clicked = model.HighlightDashboardCell(1)!;
            model.EnterDashboardCellIfHighlighted(clicked);
            Assert.Null(model.Dashboard);
            Assert.Equal(a.Id, model.SelectedSessionId);
            Assert.Null(model.HighlightDashboardCell(0));
        }

        [Theory]
        [InlineData("x:left", "x", PaneRole.Main)]
        [InlineData("x:TOP", "x", PaneRole.Main)]
        [InlineData("x:Primary", "x", PaneRole.Main)]
        [InlineData("x:right", "x", PaneRole.Split)]
        [InlineData("x:bottom", "x", PaneRole.Split)]
        [InlineData("x:SPLIT", "x", PaneRole.Split)]
        [InlineData("active", "active", null)]
        public void TargetsParseAsAgtermsDashboardTarget(string raw, string head, PaneRole? pane)
        {
            Assert.Equal(new DashboardTarget(head, pane), DashboardTarget.Parse(raw));
        }

        [Theory]
        [InlineData("")]
        [InlineData(":left")]
        [InlineData("x:scratch")]
        [InlineData("surface:x:left")]
        public void MalformedTargetsDoNotParse(string raw)
        {
            Assert.Null(DashboardTarget.Parse(raw));
        }

        [Fact]
        public async Task GrammarIsCheckedBeforeAPendingPick()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            Assert.Null(model.OpenPick(new PickRequest { Id = "p", Items = new List<ControlPickItem> { new ControlPickItem { Id = "1", Label = "one" } } }));

            ControlResponse response = await Send(dispatcher, new ControlArgs { Targets = new List<string> { a.Id.Wire(), "surface:" + a.Id.Wire() + ":left" } });

            Assert.Equal("dashboard: invalid session id 'surface:" + a.Id.Wire() + ":left' — use <id>, <id>:left, or <id>:right", response.Error);
        }

        [Fact]
        public async Task FontSizeIsKeptAsGivenAndAutoSizeStartsFromGhosttysDefault()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);

            await Send(dispatcher, new ControlArgs { Targets = new List<string> { a.Id.Wire() }, FontSize = 72 });
            Assert.Equal(72, (await dispatcher.HandleAsync(new ControlRequest { Cmd = "tree" })).Result!.Tree!.DashboardFontSize);

            await Send(dispatcher, new ControlArgs { Targets = new List<string> { a.Id.Wire() }, AutoSize = true });
            Assert.Equal(13, (await dispatcher.HandleAsync(new ControlRequest { Cmd = "tree" })).Result!.Tree!.DashboardFontSize);
        }

        [Fact]
        public void ARepeatPastTheCapCountsOnce()
        {
            AppModel model = new AppModel();
            Workspace work = model.AddWorkspace("w");
            List<Session> sessions = Enumerable.Range(0, 10).Select(_ => model.AddSession(work.Id, "cmd", null)).ToList();
            Session last = sessions[9];

            (List<DashboardCell> cells, int dropped) = model.DashboardCells(sessions.Select(session => (session, (PaneRole?)null)).Append((last, PaneRole.Main)));

            Assert.Equal(9, cells.Count);
            Assert.Equal(1, dropped);
        }

        [Fact]
        public async Task TheHighlightStaysOnItsCellWhenAnotherLeaves()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            Session c = model.AddSession(work.Id, "cmd", null);
            await Send(dispatcher, new ControlArgs { Targets = new List<string> { a.Id.Wire(), b.Id.Wire(), c.Id.Wire() } });
            model.MoveDashboardHighlight("right");

            model.RemoveSession(a.Id);

            Assert.Equal(new DashboardCell(b.Id, PaneRole.Main), model.Dashboard!.Cells[model.Dashboard.Highlight]);
        }

        [Fact]
        public async Task APromotedSplitKeepsItsCell()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(a.Id, true);
            await Send(dispatcher, new ControlArgs { Targets = new List<string> { b.Id.Wire(), a.Id.Wire() + ":right" } });
            model.MoveDashboardHighlight("right");

            model.PromoteSplit(a.Id);

            Assert.Equal(new[] { new DashboardCell(b.Id, PaneRole.Main), new DashboardCell(a.Id, PaneRole.Main) }, model.Dashboard!.Cells);
            Assert.Equal(1, model.Dashboard.Highlight);

            model.SetSplit(a.Id, true);
            await Send(dispatcher, new ControlArgs { Targets = new List<string> { a.Id.Wire() } });
            model.MoveDashboardHighlight("right");
            model.PromoteSplit(a.Id);

            Assert.Equal(new[] { new DashboardCell(a.Id, PaneRole.Main) }, model.Dashboard!.Cells);
            Assert.Equal(0, model.Dashboard.Highlight);
        }

        [Fact]
        public async Task ValidationRunsInAgtermsOrder()
        {
            AppModel model = new AppModel();
            ControlDispatcher dispatcher = new ControlDispatcher(model, new NullHost());
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            List<string> ids = new List<string> { a.Id.Wire() };

            Assert.Equal("dashboard --close takes no ids, --mru, or font options", (await Send(dispatcher, new ControlArgs { Close = true, Targets = ids, FontSize = 0, AutoSize = true })).Error);
            Assert.Equal("dashboard: --font-size is mutually exclusive with --auto-size", (await Send(dispatcher, new ControlArgs { Targets = new List<string> { ":left" }, FontSize = 0, AutoSize = true })).Error);
            Assert.Equal("dashboard --font-size must be a positive number", (await Send(dispatcher, new ControlArgs { Mru = true, Targets = ids, FontSize = double.NaN })).Error);
            Assert.Equal("dashboard --mru cannot be combined with explicit session ids", (await Send(dispatcher, new ControlArgs { Mru = true, Targets = new List<string> { ":left" } })).Error);
            Assert.Equal("dashboard requires at least one session id", (await Send(dispatcher, new ControlArgs { FontSize = 12 })).Error);
            Assert.Equal("dashboard: invalid session id ':left' — use <id>, <id>:left, or <id>:right", (await Send(dispatcher, new ControlArgs { Targets = new List<string> { "nothing", ":left" } })).Error);
            Assert.Equal("no dashboard sessions resolved", (await Send(dispatcher, new ControlArgs { Targets = new List<string> { "nothing" } })).Error);
            Assert.Equal("no recent sessions", (await Send(new ControlDispatcher(new AppModel(), new NullHost()), new ControlArgs { Mru = true })).Error);
        }

        [Fact]
        public void TheCoverIsAFullProgramOrPageFirstThenThePanesOwn()
        {
            AppModel model = new AppModel();
            Workspace work = model.AddWorkspace("w");
            Session a = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(a.Id, true);
            Assert.Null(DashboardLayout.Cover(a, PaneRole.Main));

            model.OpenOverlay(a.Id, new SessionOverlay { Id = "right", Command = "htop" }, PaneRole.OverlayRight);
            Assert.Null(DashboardLayout.Cover(a, PaneRole.Main));
            Assert.Equal("right", DashboardLayout.Cover(a, PaneRole.Split)?.Id);

            // A HUD and a floating overlay leave the panes lit; a full one covers both.
            model.OpenOverlay(a.Id, new SessionOverlay { Id = "hud", Hud = new HudSpec { Message = "build" } });
            Assert.Null(DashboardLayout.Cover(a, PaneRole.Main));
            model.OpenOverlay(a.Id, new SessionOverlay { Id = "float", Command = "top", SizePercent = 50 });
            Assert.Null(DashboardLayout.Cover(a, PaneRole.Main));
            model.OpenOverlay(a.Id, new SessionOverlay { Id = "full", Command = "top" });
            Assert.Equal("full", DashboardLayout.Cover(a, PaneRole.Main)?.Id);
            Assert.Equal("full", DashboardLayout.Cover(a, PaneRole.Split)?.Id);

            model.OpenOverlay(a.Id, new SessionOverlay { Id = "hidden", Command = "top", Hidden = true });
            Assert.Equal("right", DashboardLayout.Cover(a, PaneRole.Split)?.Id);
        }

        private static Task<ControlResponse> Send(ControlDispatcher dispatcher, ControlArgs args)
        {
            return dispatcher.HandleAsync(new ControlRequest { Cmd = "dashboard", Args = args });
        }

        internal sealed class NullHost : IControlHost
        {
            public string Version => "test";

            public IReadOnlyList<string> ShellIds => new[] { "cmd" };

            public string DefaultShellId => "cmd";

            public bool SidebarVisible => true;

            public double SidebarWidth => 220;

            public bool IsRunning(Guid sessionId, PaneRole pane) => false;

            public (List<string>? Argv, string? Shell) ForegroundOf(Guid sessionId, PaneRole pane) => (null, null);

            public string? InstalledVersion => null;

            public long? IdleMs => null;

            public long? AutoFollowMs => null;


            public void StopSession(Guid sessionId)
            {
            }

            public void StopPane(Guid sessionId, PaneRole pane)
            {
            }

            public void RestartSession(Guid sessionId, PaneRole pane)
            {
            }

            public void SwapPaneShells(Guid sessionId)
            {
            }

            public void Write(Guid sessionId, PaneRole pane, string data)
            {
            }

            public Task<string> ReadTextAsync(Guid sessionId, PaneRole pane, bool all, int? lines) => Task.FromResult(string.Empty);

            public Task<string> SearchAsync(Guid sessionId, PaneRole pane, string? needle, string? step) => Task.FromResult(string.Empty);

            public bool Reveal(Guid sessionId) => true;

            public void SetSidebar(bool? visible, double? width)
            {
            }

            public Task<string?> ClearBrowserDataAsync() => Task.FromResult<string?>(null);

            public bool BrowserClearing => false;

            public IReadOnlyList<string> ReloadConfig(bool keymapFile, bool hooksFile) => Array.Empty<string>();

            public ControlKeymap KeymapList() => new ControlKeymap { Path = string.Empty, Actions = new List<ControlKeymapAction>(), Commands = new List<ControlKeymapCommand>(), Diagnostics = new List<ControlConfigDiagnostic>() };

            public ControlHooks HooksList() => new ControlHooks { Path = string.Empty, Diagnostics = new List<ControlConfigDiagnostic>(), Hooks = new List<ControlHookEntry>() };

            public string? RunCustomCommand(string name, Guid? sessionId) => null;

            public bool HasCustomCommand(string name) => true;

            public (IReadOnlyList<string> Names, string? Current, bool Sync, string? Light, string? Dark) ThemeState() => (new string[0], "agshell", false, null, null);

            public Task<(ControlLiveInventory? Inventory, string? Error)> LiveListAsync() => Task.FromResult<(ControlLiveInventory?, string?)>((new ControlLiveInventory { Restore = new ControlRestoreStatus { Configured = "none", RequestedAtLaunch = "none", Active = "none" }, InventoryComplete = true, Entries = new List<ControlLiveEntry>() }, null));

            public Task<(string? Error, string Report, int Affected)> LivePruneAsync() => Task.FromResult<(string?, string, int)>((null, "no orphan daemons", 0));

            public bool SaveState() => true;

            public (string? Error, ControlRestoreStatus Status) RestoreMode(string? mode) => (null, new ControlRestoreStatus { Configured = "none", RequestedAtLaunch = "none", Active = "none" });

            public (string? Error, string Layout) FlaggedLayout(string? layout) => (null, "flat");

            public (string? Error, string Mode) LinkMode(string? mode) => (null, "browser");

            public Task<string?> TerminalAsync(Guid sessionId, PaneRole pane, string action, string? text) => Task.FromResult<string?>(string.Empty);

            public string? LocalPath(Guid sessionId, string path) => path.StartsWith('/') ? null : path;

            public void Raise()
            {
            }

            public bool ReleaseHeld(Guid sessionId, PaneRole pane) => false;

            public bool IsHeld(Guid sessionId, PaneRole pane) => false;

            public string? ReadClipboard() => null;

            public void WriteClipboard(string text)
            {
            }

            public (string? Error, int Count) CaptureRestoreNow() => (null, 0);

            public List<string> PageZoomSteps { get; } = new List<string>();

            public int CloseSessionsBatch(IReadOnlyList<Guid> sessionIds)
            {
                foreach (Guid id in sessionIds)
                {
                    Model?.RemoveSession(id);
                }

                return sessionIds.Count;
            }

            public AppModel? Model { get; set; }

            public string? SoundError(string name)
            {
                return name == "nope" ? "unknown sound: nope" : null;
            }

            public void StepPageZoom(string action)
            {
                PageZoomSteps.Add(action);
            }

            public void StatusSound(AgentStatus status, string? sound)
            {
            }

            public string? SetTheme(string? light, string? dark) => null;

            public string? PageCommand(Guid sessionId, PaneRole slot, string action, string? url) => null;

            public void ScheduleHudHide(Guid sessionId, string overlayId, double? seconds)
            {
            }

            public bool Notify(Guid sessionId, string title, string body)
            {
                return true;
            }

            /// <summary>Who leads each pane; nobody by default.</summary>
            public Func<Guid, PaneRole, string?> Lead { get; set; } = (sessionId, pane) => null;

            public string? PaneLead(Guid sessionId, PaneRole pane)
            {
                return Lead(sessionId, pane);
            }

            public bool PaneCovered(Guid sessionId, PaneRole pane) => Lead(sessionId, pane) is "follower" or "unowned";

            public Task<DaemonScreen?> PaneScreenAsync(Guid sessionId, PaneRole pane, bool all) => Task.FromResult<DaemonScreen?>(null);

            public Task<bool> PaneTypeAsync(Guid sessionId, PaneRole pane, string text) => Task.FromResult(false);

            public string? TakeLead(Guid sessionId, PaneRole pane)
            {
                return "pane has no lead to take";
            }

            public ControlPresentationNode? PresentationOf(Guid sessionId)
            {
                return null;
            }

            public ControlReconnectNode? ReconnectOf(Guid sessionId, PaneRole pane)
            {
                return null;
            }

            public ControlOverlayJob OverlayLaunch(Guid sessionId, PaneRole slot, string command, string? cwd)
            {
                return new ControlOverlayJob { Command = command, Cwd = cwd };
            }
        }
    }
}
