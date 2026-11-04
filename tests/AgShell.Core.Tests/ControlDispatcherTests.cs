using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class ControlDispatcherTests
    {
        private readonly AppModel model = new AppModel();
        private readonly FakeHost host = new FakeHost();
        private readonly ControlDispatcher dispatcher;
        private readonly Workspace work;

        public ControlDispatcherTests()
        {
            dispatcher = new ControlDispatcher(model, host);
            host.Model = model;
            work = model.AddWorkspace("work");
        }

        [Theory]
        [InlineData("hooks.list")]
        [InlineData("hooks.reload")]
        public async Task HooksRejectSelectorsBeforeActing(string command)
        {
            Assert.Equal(command + " takes no target or --window", (await Send(command, "missing")).Error);
            Assert.Equal(command + " takes no target or --window", (await Send(command, null, new ControlArgs { Window = "missing" })).Error);
            Assert.Equal(0, host.ConfigReloads);
            Assert.Equal(0, host.HookReads);
            Assert.True((await Send(command)).Ok);
        }

        [Fact]
        public async Task RecentCommandsAreAppWideAndResolveIdsAsTargetsDo()
        {
            Assert.Equal("recent.list takes no target or --window", (await Send("recent.list", "x")).Error);
            Assert.Equal("recent.clear takes no target or --window", (await Send("recent.clear", null, new ControlArgs { Window = "active" })).Error);
            Assert.Equal("no recently closed items", (await Send("recent.open")).Error);

            Workspace work = model.Workspaces[0];
            Session first = model.AddSession(work.Id, "cmd", null, name: "first");
            Session second = model.AddSession(work.Id, "cmd", null, name: "second");
            model.RemoveSession(first.Id);
            model.RemoveSession(second.Id);
            ControlRecentItem listed = (await Send("recent.list")).Result!.Recent![0];
            Assert.Equal(new[] { second.Id.Wire() }, listed.SessionIds);
            Assert.Equal("no such recent item: ", (await Send("recent.open", string.Empty)).Error);
            string common = new string(model.RecentClosed[0].Id.Wire().Zip(model.RecentClosed[1].Id.Wire()).TakeWhile(pair => pair.First == pair.Second).Select(pair => pair.First).ToArray());
            if (common.Length > 0)
            {
                Assert.StartsWith("ambiguous recent item prefix '" + common + "' → ", (await Send("recent.open", common)).Error);
            }

            ControlResponse opened = await Send("recent.open");
            Assert.Null(opened.Result!.Id);
            Assert.True(opened.Result.RecentOpen!.Complete);
            Assert.NotNull(model.FindSession(second.Id));
            Assert.True((await Send("recent.clear")).Ok);
            Assert.Empty(model.RecentClosed);
        }

        [Fact]
        public async Task StatusChecksItsArgumentsAndSoundBeforeTheSession()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            Assert.Equal("unknown sound: nope", (await Send("session.status", "nowhere", new ControlArgs { Status = "blocked", Sound = "nope" })).Error);
            Assert.Equal("invalid status", (await Send("session.status", "nowhere", new ControlArgs { Status = "busy" })).Error);
            Assert.Equal(AgentStatus.Idle, session.Status);
            Assert.True((await Send("session.status", session.Id.Wire(), new ControlArgs { Status = "blocked", Color = "ff0000" })).Ok);
            Assert.Equal("ff0000", session.StatusColor);
        }

        [Fact]
        public async Task TreeLeavesOutWhatIsEmptyAndWritesIdsUpperCase()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            ControlSessionNode quiet = (await Send("tree")).Result!.Tree!.Workspaces[0].Sessions[0];
            Assert.Equal(session.Id.ToString().ToUpperInvariant(), quiet.Id);
            Assert.Null(quiet.Status);
            Assert.Null(quiet.Unseen);
            Assert.False(quiet.Overlay);
            Assert.Null((await Send("tree")).Result!.Tree!.Workspaces[0].Collapsed);

            model.SetStatus(session.Id, AgentStatus.Blocked);
            model.RecordNotification(session.Id, "t", "b", NotificationSources.Control);
            ControlSessionNode busy = (await Send("tree")).Result!.Tree!.Workspaces[0].Sessions[0];
            Assert.Equal("blocked", busy.Status);
            Assert.Null(busy.StatusPane);
            Assert.Equal(1, busy.Unseen);

            model.SetStatus(session.Id, AgentStatus.Blocked, pane: PaneRole.Main, paneNamed: true);
            Assert.Equal("left", (await Send("tree")).Result!.Tree!.Workspaces[0].Sessions[0].StatusPane);
        }

        [Fact]
        public async Task NewSessionLandsInTheCurrentWorkspaceAndPrintsItsId()
        {
            ControlResponse response = await Send("session.new", args: new ControlArgs { Cwd = @"C:\src", Name = "agent" });

            Session session = Assert.Single(work.Sessions);
            Assert.True(response.Ok);
            Assert.Equal(session.Id.Wire(), response.Result!.Id);
            Assert.Equal("agent", session.DisplayName);
            Assert.Equal(@"C:\src", session.Cwd);
            Assert.Equal("pwsh", session.ProfileId);
            Assert.Equal(session.Id, model.SelectedSessionId);
        }

        [Fact]
        public async Task PaneIdsFollowTheirTerminal()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(session.Id, true);
            host.RunningPanes.Add((session.Id, PaneRole.Split));
            string left = model.PaneIdFor(session.Id, PaneRole.Main);
            string right = model.PaneIdFor(session.Id, PaneRole.Split);
            Assert.Equal(left, model.PaneIdFor(session.Id, PaneRole.Main));

            model.SwapPanes(session.Id);
            Assert.Equal(PaneRole.Split, AppModel.PaneOfId(session, left));
            await Send("session.status", session.Id.Wire(), new ControlArgs { Status = "blocked", Pane = "left", PaneId = left });
            Assert.Equal(PaneRole.Split, session.StatusPane);

            Assert.True((await Send("session.status", session.Id.Wire(), new ControlArgs { Status = "blocked", PaneId = "pgone" })).Ok);
            Assert.Equal("unknown pane id: pgone", (await Send("session.text", session.Id.Wire(), new ControlArgs { PaneId = "pgone" })).Error);
            Assert.Equal("right", (await Send("session.text", session.Id.Wire(), new ControlArgs { PaneId = left })).Result!.Pane);

            ControlSessionNode node = (await Send("tree")).Result!.Tree!.Workspaces.SelectMany(w => w.Sessions).Single(s => s.Id == session.Id.Wire());
            Assert.Equal(new[] { right, left }, node.Surfaces!.Take(2).Select(surface => surface.PaneId));
            Assert.Equal("surface:" + session.Id.Wire() + ":right", node.Surfaces![1].Id);

            Session restored = AppModel.FromSnapshot(model.ToSnapshot()).FindSession(session.Id)!;
            Assert.Equal(left, restored.PaneIds[PaneRole.Split]);
            model.PromoteSplit(session.Id);
            Assert.Equal(left, session.PaneIds[PaneRole.Main]);
            Assert.False(session.PaneIds.ContainsKey(PaneRole.Split));
        }

        [Fact]
        public async Task FloatingOverlaysTakePointSizedAxes()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            string target = session.Id.Wire();
            Assert.Equal("session.overlay.open: --width/--height need --size-percent", (await Send("session.overlay.open", target, new ControlArgs { Command = "htop", Width = 400 })).Error);
            Assert.Equal("session.overlay: --width/--height must be positive", (await Send("session.overlay.open", target, new ControlArgs { Command = "htop", SizePercent = 50, Height = 0 })).Error);

            await Send("session.overlay.open", target, new ControlArgs { Command = "htop", SizePercent = 50, Width = 400 });
            Assert.Equal((50, 400, (int?)null), (session.Overlay!.SizePercent!.Value, session.Overlay.WidthPoints!.Value, session.Overlay.HeightPoints));
            await Send("session.overlay.resize", target, new ControlArgs { Height = 200 });
            Assert.Equal((400, 200), (session.Overlay!.WidthPoints!.Value, session.Overlay.HeightPoints!.Value));
            await Send("session.overlay.resize", target, new ControlArgs { SizePercent = 60, Height = 300 });
            Assert.Equal(((int?)null, 300), (session.Overlay!.WidthPoints, session.Overlay.HeightPoints!.Value));
            Assert.Equal("session.overlay.resize: --full is mutually exclusive with --width/--height", (await Send("session.overlay.resize", target, new ControlArgs { Full = true, Width = 10 })).Error);
            await Send("session.overlay.resize", target, new ControlArgs { Full = true });
            Assert.Null(session.Overlay!.HeightPoints);
            Assert.Equal("session.overlay.resize: --width/--height need a floating overlay; pass --size-percent too", (await Send("session.overlay.resize", target, new ControlArgs { Width = 10 })).Error);
        }

        [Fact]
        public async Task WorkspaceFlagSetsEverySessionAsOneValue()
        {
            Session first = model.AddSession(work.Id, "cmd", null);
            Session second = model.AddSession(work.Id, "cmd", null);
            model.SetFlag(new[] { first.Id }, true);

            Assert.Equal(work.Id.Wire(), (await Send("workspace.flag", work.Id.Wire())).Result!.Id);
            Assert.True(first.Flagged && second.Flagged);
            await Send("workspace.flag", work.Id.Wire());
            Assert.False(first.Flagged || second.Flagged);
            await Send("workspace.flag", work.Id.Wire(), new ControlArgs { Mode = "off" });
            Assert.False(first.Flagged);
            Assert.Equal("invalid workspace flag mode: maybe", (await Send("workspace.flag", work.Id.Wire(), new ControlArgs { Mode = "maybe" })).Error);
        }

        [Fact]
        public void HudTextCountsScalarsOfThePrecomposedForm()
        {
            Assert.Equal(1, HudSpec.TextLength("e\u0301"));
            Assert.Equal(5, HudSpec.TextLength("\U0001F468\u200D\U0001F469\u200D\U0001F467"));
            string failure = HudSpec.FailureMessage(new string('n', 300), "exit 1");
            Assert.Equal(HudSpec.MaxTextLength, HudSpec.TextLength(failure));
            Assert.EndsWith(": exit 1", failure);
            Assert.Equal(HudSpec.MaxTextLength, HudSpec.TextLength(HudSpec.Cleaned(string.Concat(Enumerable.Repeat("\U0001F600", 300)))));
        }

        [Fact]
        public async Task OverlaysHideWithoutClosingAndKeepTheirSlot()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            string target = session.Id.Wire();
            Assert.Equal("no overlay", (await Send("session.overlay.hide", target)).Error);

            await Send("session.overlay.open", target, new ControlArgs { Command = "htop" });
            ControlResponse hidden = await Send("session.overlay.toggle", target);
            Assert.Equal("hidden", hidden.Result!.Text);
            Assert.True(session.Overlay!.Hidden);
            Assert.Equal("overlay hidden", (await Send("session.overlay.open", target, new ControlArgs { Command = "top" })).Error);
            Assert.Equal("overlay hidden", (await Send("session.hud.open", target, new ControlArgs { Message = "x" })).Error);
            // agterm answers the hidden slot before it looks at the pane.
            Assert.Equal("overlay hidden", (await Send("session.hud.open", target, new ControlArgs { Message = "x", Pane = "right" })).Error);
            Assert.Equal("no hud", (await Send("session.hud.update", target, new ControlArgs { Message = "x", Pane = "right" })).Error);
            ControlSessionNode node = (await Send("tree")).Result!.Tree!.Workspaces.SelectMany(w => w.Sessions).Single(s => s.Id == target);
            Assert.Equal(new[] { "session" }, node.HiddenOverlays);

            Assert.Equal("shown", (await Send("session.overlay.toggle", target)).Result!.Text);
            Assert.False(session.Overlay!.Hidden);
            Assert.Equal("hidden", (await Send("session.overlay.hide", target)).Result!.Text);
            await Send("session.overlay.close", target);
            await Send("session.overlay.open", target, new ControlArgs { Command = "htop" });
            Assert.False(session.Overlay!.Hidden);
        }

        [Fact]
        public async Task SurfacesZoomAndReportTheirCursorByExactId()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            Session other = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(session.Id, true);
            model.SetSplit(session.Id, false);
            string right = ControlDispatcher.SurfaceId(session.Id, PaneRole.Split);

            Assert.Equal("surface not available: " + ControlDispatcher.SurfaceId(other.Id, PaneRole.Split), (await Send("surface.zoom", ControlDispatcher.SurfaceId(other.Id, PaneRole.Split))).Error);
            Assert.True((await Send("surface.zoom", right, new ControlArgs { Mode = "show" })).Ok);
            // As agterm's: a background session's surface zooms without being selected.
            Assert.Equal((session.Id, PaneRole.Split), model.ZoomTarget);
            Assert.Equal(other.Id, model.SelectedSessionId);
            Assert.False(session.SplitShown);
            Assert.Equal(right, (await Send("tree")).Result!.Tree!.ZoomedSurface);

            await Send("surface.zoom", right);
            Assert.False(model.Zoomed);
            Assert.True((await Send("surface.zoom", right, new ControlArgs { Mode = "hide" })).Ok);

            // As agterm's: a bare session is no surface; active is the hidden split's right pane, maximized and focused.
            Assert.Equal("invalid surface: " + session.Id.Wire(), (await Send("surface.cursor", session.Id.Wire())).Error);
            model.SelectSession(session.Id);
            Assert.Equal("surface not realized", (await Send("surface.cursor", "active")).Error);
            // As in agterm, the hidden split shows its focused right pane maximized, and the session's cursor is that pane's.
            host.Terminals[(session.Id, PaneRole.Split)] = string.Empty;
            Assert.Equal(7, (await Send("surface.cursor", "active")).Result!.Cursor!.Column);
            Assert.Equal("unknown pane id: pnope", (await Send("surface.cursor", session.Id.Wire(), new ControlArgs { PaneId = "pnope" })).Error);
            Assert.False((await Send("surface.cursor", right, new ControlArgs { PaneId = "p1" })).Ok);
        }

        [Fact]
        public async Task NewSessionCanRunACommandOnce()
        {
            Assert.Equal("--wait requires --command", (await Send("session.new", args: new ControlArgs { Wait = true })).Error);
            string id = (await Send("session.new", args: new ControlArgs { Command = "  htop  ", Wait = true })).Result!.Id!;
            Session session = model.FindSession(Guid.Parse(id))!;
            Assert.Equal(("htop", true), (session.Command, session.CommandWait));

            Session restored = AppModel.FromSnapshot(model.ToSnapshot()).FindSession(session.Id)!;
            Assert.Equal(("htop", true), (restored.Command, restored.CommandWait));
            Assert.Null(model.DuplicateSession(session.Id).Command);
            ControlSessionNode node = (await Send("tree")).Result!.Tree!.Workspaces.SelectMany(w => w.Sessions).Single(s => s.Id == id);
            Assert.Equal(("htop", (bool?)true), (node.Command, node.CommandWait));
        }

        [Fact]
        public async Task NewSessionPlacementOptions()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);

            string after = (await Send("session.new", args: new ControlArgs { After = a.Id.Wire()[..6], NoSelect = true })).Result!.Id!;
            string before = (await Send("session.new", args: new ControlArgs { Before = a.Id.Wire(), Shell = "cmd" })).Result!.Id!;

            Assert.Equal(new[] { before, a.Id.Wire(), after, b.Id.Wire() }, work.Sessions.Select(session => session.Id.Wire()));
            Assert.Equal(before, model.SelectedSessionId.Wire());

            ControlResponse missing = await Send("session.new", args: new ControlArgs { WorkspaceName = "servers" });
            Assert.Equal("no workspace named \"servers\" (pass --create-workspace to add it)", missing.Error);
            await Send("session.new", args: new ControlArgs { WorkspaceName = "servers", CreateWorkspace = true });
            await Send("session.new", args: new ControlArgs { WorkspaceName = "servers", CreateWorkspace = true });
            Assert.Equal(2, model.Workspaces.Single(workspace => workspace.Name == "servers").Sessions.Count);

            ControlResponse badShell = await Send("session.new", args: new ControlArgs { Shell = "zsh" });
            Assert.Equal("no such shell: zsh (available: pwsh, cmd)", badShell.Error);
        }

        [Fact]
        public async Task CloseBatchGoesThroughTheWindowsGroupedCloseAndCountsSessions()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            Session c = model.AddSession(work.Id, "cmd", null);

            // agterm's batch: one grouped close, which the window's undo grace covers.
            ControlResponse response = await Send("session.close", args: new ControlArgs { Targets = new List<string> { a.Id.Wire(), "active" } });

            Assert.Equal(2, response.Result!.Affected);
            Assert.Equal(new[] { b.Id }, work.Sessions.Select(session => session.Id));

            // One target closes at once, its shells stopped.
            Assert.Equal(b.Id.Wire(), (await Send("session.close", b.Id.Wire())).Result!.Id);
            Assert.Equal(new[] { b.Id }, host.Stopped);
        }

        [Fact]
        public async Task MoveAfterAnchorKeepsTreeOrder()
        {
            Workspace other = model.AddWorkspace("other");
            Session x = model.AddSession(other.Id, "cmd", null);
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            Session c = model.AddSession(work.Id, "cmd", null);

            await Send("session.move", args: new ControlArgs { After = x.Id.Wire(), Targets = new List<string> { c.Id.Wire(), a.Id.Wire() } });
            Assert.Equal(new[] { x.Id, a.Id, c.Id }, other.Sessions.Select(session => session.Id));

            await Send("session.move", b.Id.Wire(), new ControlArgs { To = "bottom" });
            await Send("session.move", x.Id.Wire(), new ControlArgs { To = "down" });
            Assert.Equal(new[] { a.Id, x.Id, c.Id }, other.Sessions.Select(session => session.Id));

            ControlResponse both = await Send("session.move", args: new ControlArgs { To = "up", Workspace = work.Id.Wire() });
            Assert.False(both.Ok);
        }

        [Fact]
        public async Task BatchMoveToWorkspaceSkipsThoseAlreadyThere()
        {
            Workspace other = model.AddWorkspace("other");
            Session x = model.AddSession(other.Id, "cmd", null);
            Session a = model.AddSession(work.Id, "cmd", null);

            ControlResponse response = await Send("session.move", args: new ControlArgs { Workspace = other.Id.Wire(), Targets = new List<string> { x.Id.Wire(), a.Id.Wire() } });

            Assert.Equal(1, response.Result?.Affected);
            Assert.Equal(new[] { x.Id, a.Id }, other.Sessions.Select(session => session.Id));
        }

        [Fact]
        public async Task NewWorkspaceJoinsTheFocusUnlessCreatedInTheBackground()
        {
            model.ToggleFocusWorkspace(work.Id);

            Guid shown = Guid.Parse((await Send("workspace.new", args: new ControlArgs { Name = "shown" })).Result!.Id!);
            Guid folded = Guid.Parse((await Send("workspace.new", args: new ControlArgs { Name = "folded", Collapsed = true })).Result!.Id!);

            Assert.True(model.IsFocusMember(shown));
            Assert.False(model.IsFocusMember(folded));
            Assert.Equal(shown, model.CurrentWorkspace?.Id);
        }

        [Fact]
        public async Task ArgumentsAreCheckedBeforeTheTargetAsAgtermDoes()
        {
            const string missing = "ffffffff";

            Assert.Equal("invalid workspace flag mode: maybe", (await Send("workspace.flag", missing, new ControlArgs { Mode = "maybe" })).Error);
            Assert.Equal("invalid focus mode: maybe (on|off|toggle|add)", (await Send("workspace.focus", missing, new ControlArgs { Mode = "maybe" })).Error);
            Assert.Equal("session.context set requires text", (await Send("session.context", missing, new ControlArgs { Mode = "set" })).Error);
            Assert.Equal("invalid split axis: diagonal (vertical|horizontal)", (await Send("session.split", missing, new ControlArgs { Axis = "diagonal" })).Error);
            Assert.Equal("the scratch terminal has no lead", (await Send("session.lead", missing, new ControlArgs { Pane = "scratch" })).Error);
            Assert.Equal("invalid pane: middle", (await Send("font.inc", missing, new ControlArgs { Pane = "middle" })).Error);
            Assert.Equal("session.overlay: --pane must be left or right", (await Send("session.overlay.close", missing, new ControlArgs { Pane = "scratch" })).Error);
            Assert.Equal("session.overlay.submit requires a value", (await Send("session.overlay.submit", missing)).Error);
            Assert.Equal("session.overlay.open: --pane is mutually exclusive with --size-percent", (await Send("session.overlay.open", missing, new ControlArgs { Command = "htop", Pane = "left", SizePercent = 50 })).Error);
            Assert.Equal("session.overlay.resize requires --size-percent, --width/--height or --full", (await Send("session.overlay.resize", missing)).Error);
            Assert.Equal("--pane must be left or right", (await Send("session.hud.open", missing, new ControlArgs { Message = "hi", Pane = "scratch" })).Error);
            Assert.Equal("no such session: " + missing, (await Send("session.hud.open", missing, new ControlArgs { Message = "hi" })).Error);
        }

        [Fact]
        public async Task LastWorkspaceCannotBeDeleted()
        {
            ControlResponse response = await Send("workspace.delete");

            Assert.Equal("cannot delete last workspace", response.Error);
        }

        [Fact]
        public async Task RefusalsUseAgtermsWording()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            string id = a.Id.Wire();

            Assert.Equal("use either --after or --before, not both", (await Send("session.new", args: new ControlArgs { After = id, Before = id })).Error);
            Assert.Equal("session.new takes --after/--before or a workspace, not both", (await Send("session.new", args: new ControlArgs { After = id, WorkspaceName = "x" })).Error);
            Assert.Equal("use either --workspace or --workspace-name, not both", (await Send("session.new", args: new ControlArgs { Workspace = "active", WorkspaceName = "x" })).Error);
            Assert.Equal("--create-workspace requires --workspace-name", (await Send("session.new", args: new ControlArgs { CreateWorkspace = true })).Error);
            Assert.Equal("session.move requires --to or a workspace", (await Send("session.move", id)).Error);
            Assert.Equal("session.move takes either --to or a workspace, not both", (await Send("session.move", id, new ControlArgs { To = "up", Workspace = "active" })).Error);
            Assert.Equal("session.move takes --after/--before or --to, not both", (await Send("session.move", id, new ControlArgs { To = "up", After = id })).Error);
            Assert.Equal("session.move --to must be up|down|top|bottom", (await Send("session.move", id, new ControlArgs { To = "left" })).Error);
            Assert.Equal("session.move --target can be repeated only with a workspace or --after/--before", (await Send("session.move", null, new ControlArgs { To = "up", Targets = new List<string> { id, b.Id.Wire() } })).Error);
            Assert.True((await Send("session.move", null, new ControlArgs { After = id, Targets = new List<string> { id, b.Id.Wire() } })).Ok);
            Assert.Equal("session.rename requires a name", (await Send("session.rename", id, new ControlArgs())).Error);
            Assert.Equal("workspace.rename requires a name", (await Send("workspace.rename", args: new ControlArgs { Name = " " })).Error);
            Assert.Equal("workspace.move requires --to", (await Send("workspace.move")).Error);
            Assert.Equal("workspace.move --to must be up|down|top|bottom", (await Send("workspace.move", args: new ControlArgs { To = "left" })).Error);
            Assert.Equal("invalid split mode: sideways", (await Send("session.split", id, new ControlArgs { Mode = "sideways" })).Error);
            Assert.Equal("invalid split axis: diagonal (vertical|horizontal)", (await Send("session.split", id, new ControlArgs { Axis = "diagonal" })).Error);
            Assert.Equal("invalid flag mode: maybe", (await Send("session.flag", id, new ControlArgs { Mode = "maybe" })).Error);
            Assert.Equal("notify requires a body", (await Send("notify", id, new ControlArgs())).Error);
            Assert.Equal("keymap.run requires a command name", (await Send("keymap.run", id, new ControlArgs())).Error);
            Assert.Equal("no custom command named Nope", (await Send("keymap.run", id, new ControlArgs { Name = "Nope" })).Error);
            Assert.Equal(id, (await Send("keymap.run", id, new ControlArgs { Name = "Known" })).Result!.Id);
            Assert.Equal("invalid background mode: tiles (image|text|color|clear)", (await Send("session.background", id, new ControlArgs { Mode = "tiles" })).Error);
            Assert.Equal("session.background image requires a path", (await Send("session.background", id, new ControlArgs { Mode = "image" })).Error);
            Assert.Equal("session.background color requires a color", (await Send("session.background", id, new ControlArgs { Mode = "color" })).Error);
            Assert.Equal("invalid opacity: 2 (0.0-1.0)", (await Send("session.background", id, new ControlArgs { Mode = "color", Color = "#000000", Opacity = 2 })).Error);
            Assert.Equal("session.overlay.resize requires --size-percent, --width/--height or --full", (await Send("session.overlay.resize", id, new ControlArgs())).Error);
            Assert.Equal("session.overlay.resize: --full is mutually exclusive with --size-percent", (await Send("session.overlay.resize", id, new ControlArgs { Full = true, SizePercent = 50 })).Error);
            Assert.Equal("invalid context mode:  (set|clear)", (await Send("session.context", id, new ControlArgs { Text = "x" })).Error);
            Assert.Equal("context must be at most 256 UTF-8 bytes", (await Send("session.context", id, new ControlArgs { Mode = "set", Text = new string('я', 129) })).Error);
            Assert.Equal("invalid position: top", (await Send("session.background", id, new ControlArgs { Mode = "color", Color = "#000000", Position = "top" })).Error);
            Assert.Equal("session.overlay.open: --pane is mutually exclusive with --width/--height", (await Send("session.overlay.open", id, new ControlArgs { Command = "x", Pane = "left", Width = 100 })).Error);
            Assert.Equal("session.overlay: --pane must be left or right", (await Send("session.overlay.close", id, new ControlArgs { Pane = "scratch" })).Error);
            Assert.Equal("no overlay", (await Send("session.overlay.text", id)).Error);
            Assert.Equal("text must not contain a NUL byte", (await Send("session.type", id, new ControlArgs { Text = "a\0b" })).Error);
            Assert.Equal("invalid color (expected #rrggbb)", (await Send("session.status", id, new ControlArgs { Status = "active", Color = "red" })).Error);
            Assert.Equal("session.close requires at least one --target", (await Send("session.close", null, new ControlArgs { Targets = new List<string>() })).Error);
            Assert.Equal("session.restart requires --pane-id or --pane", (await Send("session.restart", id)).Error);
            Assert.Equal("--pane must be left, right, or scratch", (await Send("session.restart", id, new ControlArgs { Pane = "middle" })).Error);
            Assert.Equal("invalid pane: middle", (await Send("session.lead", id, new ControlArgs { Pane = "middle" })).Error);
        }

        [Fact]
        public async Task AnOpenModalOrZoomHoldsOffWhatWouldCoverIt()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            model.SelectSession(a.Id);
            string id = a.Id.Wire();
            await Send("surface.zoom", args: new ControlArgs { Mode = "show" });
            Assert.Equal("terminal zoom active", (await Send("session.search", id, new ControlArgs { Text = "x" })).Error);
            await Send("surface.zoom", args: new ControlArgs { Mode = "hide" });

            Assert.Null(model.OpenPick(new PickRequest { Id = "p", Items = new List<ControlPickItem> { new ControlPickItem { Id = "1", Label = "one" } } }));
            Assert.Equal("pick pending", (await Send("surface.zoom", args: new ControlArgs { Mode = "show" })).Error);
            Assert.Equal("pick pending", (await Send("session.search", id, new ControlArgs { Text = "x" })).Error);
            Assert.Equal("pick pending", (await Send("dashboard", args: new ControlArgs { Targets = new List<string> { id } })).Error);
            Assert.True((await Send("surface.zoom", args: new ControlArgs { Mode = "hide" })).Ok);
        }

        [Fact]
        public async Task SelectingAnEmptyWorkspaceMakesItCurrent()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Workspace empty = model.AddWorkspace("empty");
            model.SelectSession(a.Id);
            model.AddSession(work.Id, "cmd", null);

            await Send("workspace.select", empty.Id.Wire());
            string id = (await Send("session.new")).Result!.Id!;

            Assert.Equal(id, Assert.Single(empty.Sessions).Id.Wire());
        }

        [Fact]
        public async Task TypeSendsTextThenAPacedReturn()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            host.Running.Add(a.Id);

            await Send("session.type", args: new ControlArgs { Text = "dir\n" });

            Assert.Equal(new[] { "dir", "\r" }, host.Written);
            host.Running.Clear();
            Assert.Equal("session not realized", (await Send("session.type", args: new ControlArgs { Text = "x" })).Error);
        }

        [Theory]
        [InlineData("left")]
        [InlineData("right")]
        public async Task TypingIntoAHeldPaneDoesNotSendThePacedReturnToItsNeighbor(string pane)
        {
            Session session = model.AddRemoteSession(work.Id, "fixture", Guid.NewGuid(), "remote", true);
            host.Running.Add(session.Id);
            host.RunningPanes.Add((session.Id, PaneRole.Split));
            host.Held.Add((session.Id, pane == "left" ? PaneRole.Main : PaneRole.Split));

            Assert.True((await Send("session.type", session.Id.Wire(), new ControlArgs { Text = string.Empty, Pane = pane })).Ok);
            Assert.Single(host.Held);
            host.Written.Clear();
            Assert.True((await Send("session.type", session.Id.Wire(), new ControlArgs { Text = "x\n", Pane = pane })).Ok);

            Assert.Empty(host.Held);
            Assert.Empty(host.Written);
        }

        [Fact]
        public async Task TextValidatesItsExtent()
        {
            model.AddSession(work.Id, "cmd", null);

            Assert.Equal("use either --all or --lines, not both", (await Send("session.text", args: new ControlArgs { All = true, Lines = 3 })).Error);
            Assert.Equal("--lines must be greater than 0", (await Send("session.text", args: new ControlArgs { Lines = 0 })).Error);
            Assert.Equal("all=False lines=5", (await Send("session.text", args: new ControlArgs { Lines = 5 })).Result!.Text);
        }

        [Fact]
        public async Task ALivePaneReadsItsTextAndCursorFromItsDaemonAsAgterm()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            host.Running.Add(session.Id);
            host.Terminals[(session.Id, PaneRole.Main)] = string.Empty;
            string surface = ControlDispatcher.SurfaceId(session.Id, PaneRole.Main);
            host.DaemonScreen = new DaemonScreen("one\ntwo\nthree\n\n", 80, 24, 3, 2, false, 5);

            // agterm's paneSource: a pane its daemon reported no role for reads its own terminal.
            Assert.Equal("all=True lines=", (await Send("session.text", session.Id.Wire(), new ControlArgs { All = true })).Result!.Text);
            Assert.Empty(host.ScreenReads);

            // Leading: the default read is the pane's own viewport; --all, --lines and the cursor are the daemon's.
            host.Leads[(session.Id, PaneRole.Main)] = "leader";
            Assert.Equal("all=False lines=", (await Send("session.text", session.Id.Wire())).Result!.Text);
            Assert.Equal("one\ntwo\nthree\n\n", (await Send("session.text", session.Id.Wire(), new ControlArgs { All = true })).Result!.Text);
            Assert.Equal("two\nthree", (await Send("session.text", session.Id.Wire(), new ControlArgs { Lines = 2 })).Result!.Text);
            Assert.Equal(3, (await Send("surface.cursor", surface)).Result!.Cursor!.Column);
            Assert.Equal(new[] { "left all=True", "left all=True", "left all=False" }, host.ScreenReads);

            // Covered: even the default read is the daemon's, laid out at the leader's grid.
            host.ScreenReads.Clear();
            host.Leads[(session.Id, PaneRole.Main)] = "follower";
            ControlResponse covered = await Send("session.text", session.Id.Wire());
            Assert.Equal(("one\ntwo\nthree\n\n", "left"), (covered.Result!.Text, covered.Result.Pane));
            Assert.Equal(new[] { "left all=False" }, host.ScreenReads);
            host.Leads[(session.Id, PaneRole.Main)] = "unowned";
            Assert.Equal("one\ntwo\nthree\n\n", (await Send("session.text", session.Id.Wire())).Result!.Text);

            host.DaemonScreen = null;
            Assert.Equal("failed to read surface buffer", (await Send("session.text", session.Id.Wire())).Error);
            Assert.Equal("failed to read cursor position", (await Send("surface.cursor", surface)).Error);
        }

        [Fact]
        public async Task ALivePaneTypesThroughItsDaemonAsAgterm()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            host.Running.Add(session.Id);
            host.Terminals[(session.Id, PaneRole.Main)] = string.Empty;

            // agterm's paneSource: a pane its daemon reported no role for types through its own terminal.
            await Send("session.type", session.Id.Wire(), new ControlArgs { Text = "a\n" });
            Assert.Empty(host.DaemonTyped);

            // With a role, leading or not, typing goes through the daemon: the text, then its final Return on its own,
            // a composition under way first, and the agent's status answered.
            host.Leads[(session.Id, PaneRole.Main)] = "leader";
            host.Written.Clear();
            host.Composition = "ha";
            model.SetStatus(session.Id, AgentStatus.Blocked);
            Assert.True((await Send("session.type", session.Id.Wire(), new ControlArgs { Text = "o\r\nnext\n" })).Ok);
            Assert.Equal(new[] { "left:hao\rnext", "left:\r" }, host.DaemonTyped);
            Assert.Empty(host.Written);
            Assert.NotEqual(AgentStatus.Blocked, session.Status);

            host.Composition = string.Empty;
            host.DaemonTyped.Clear();
            host.Leads[(session.Id, PaneRole.Main)] = "follower";
            Assert.True((await Send("session.type", session.Id.Wire(), new ControlArgs { Text = "\n" })).Ok);
            Assert.Equal(new[] { "left:\r" }, host.DaemonTyped);

            host.TypeAnswers.Enqueue(false);
            model.SetStatus(session.Id, AgentStatus.Blocked);
            Assert.Equal("the pane's daemon did not accept the input", (await Send("session.type", session.Id.Wire(), new ControlArgs { Text = "lost" })).Error);
            Assert.Equal(AgentStatus.Blocked, session.Status);
            host.TypeAnswers.Enqueue(true);
            host.TypeAnswers.Enqueue(false);
            Assert.Equal("text typed, but its final Return could not be confirmed; do not retype the text",
                (await Send("session.type", session.Id.Wire(), new ControlArgs { Text = "one\n" })).Error);
        }

        [Fact]
        public async Task AnUnownedOrReattachingPaneIsCoveredAsAFollowerIs()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            host.Running.Add(session.Id);
            host.Terminals[(session.Id, PaneRole.Main)] = "sel";
            const string covered = "pane is covered while another machine leads it; take the lead first (session lead)";

            host.Leads[(session.Id, PaneRole.Main)] = "unowned";
            Assert.Equal(covered, (await Send("session.copy", session.Id.Wire())).Error);
            host.Leads.Remove((session.Id, PaneRole.Main));
            host.Reattaching.Add((session.Id, PaneRole.Main));
            Assert.Equal(covered, (await Send("session.copy", session.Id.Wire())).Error);
            host.Reattaching.Clear();
            Assert.True((await Send("session.copy", session.Id.Wire())).Ok);

            Session remote = model.AddRemoteSession(work.Id, "box", Guid.NewGuid(), "far", split: false);
            host.Running.Add(remote.Id);
            host.Leads[(remote.Id, PaneRole.Main)] = "unowned";
            Assert.Equal("pane is in use on the machine it runs on; take the lead to drive it from here",
                (await Send("session.type", remote.Id.Wire(), new ControlArgs { Text = "x" })).Error);
        }

        [Fact]
        public async Task ARemotePaneDoesNotReadADaemonOfThisMachine()
        {
            Session remote = model.AddRemoteSession(work.Id, "box", Guid.NewGuid(), "far", split: false);
            host.Running.Add(remote.Id);
            host.DaemonScreen = new DaemonScreen("daemon", 80, 24, 0, 0, false, 1);

            host.Leads[(remote.Id, PaneRole.Main)] = "leader";
            Assert.Equal("all=True lines=", (await Send("session.text", remote.Id.Wire(), new ControlArgs { All = true })).Result!.Text);
            host.Leads[(remote.Id, PaneRole.Main)] = "follower";
            Assert.Equal("pane is in use on the machine it runs on; take the lead to drive it from here", (await Send("session.text", remote.Id.Wire())).Error);
            Assert.Empty(host.ScreenReads);
        }

        [Fact]
        public void TheLastLinesOfADaemonScreenDropTheBlankRowsBelow()
        {
            DaemonScreen screen = new DaemonScreen("a\nb\n  \nc\n \n\n", 80, 24, 0, 0, false, 0);

            Assert.Equal("  \nc", screen.LastLines(2));
            Assert.Equal("a\nb\n  \nc", screen.LastLines(10));
            Assert.Equal(string.Empty, new DaemonScreen(string.Empty, 80, 24, 0, 0, false, 0).LastLines(3));
        }

        [Fact]
        public async Task TreeMarksActiveItems()
        {
            Session a = model.AddSession(work.Id, "cmd", @"C:\src");

            ControlTree tree = (await Send("tree")).Result!.Tree!;

            ControlWorkspaceNode workspace = Assert.Single(tree.Workspaces);
            Assert.True(workspace.Active);
            ControlSessionNode session = Assert.Single(workspace.Sessions);
            Assert.True(session.Active);
            Assert.Equal("src", session.Name);
            Assert.Equal("cmd", session.Shell);
        }

        [Fact]
        public async Task StatusNotifyAndTypingWorkTogether()
        {
            Session a = model.AddSession(work.Id, "cmd", @"C:\src");
            host.Running.Add(a.Id);

            Assert.Equal("invalid status", (await Send("session.status", args: new ControlArgs { Status = "busy" })).Error);
            await Send("session.status", args: new ControlArgs { Status = "blocked", Blink = true });
            ControlSessionNode node = (await Send("tree")).Result!.Tree!.Workspaces[0].Sessions[0];
            Assert.Equal("blocked", node.Status);
            Assert.True(node.Blink);
            Assert.NotNull(node.StatusChangedAt);

            await Send("notify", args: new ControlArgs { Body = "done" });
            Assert.Equal(new[] { "src: done" }, host.Notified);
            Assert.Equal(1, a.UnseenCount);
            await Send("session.seen");
            Assert.Equal(0, a.UnseenCount);

            await Send("session.type", args: new ControlArgs { Text = "y\n" });
            Assert.Equal(AgentStatus.Idle, a.Status);
        }

        [Fact]
        public async Task EventsReadValidatesItsCursor()
        {
            Assert.Equal("events.read requires --run and --after together", (await Send("events.read", args: new ControlArgs { Run = "x" })).Error);
            Assert.Equal("invalid event kind: tree", (await Send("events.read", args: new ControlArgs { Kinds = new List<string> { "tree" } })).Error);
            ControlEventBatch anchor = (await Send("events.read")).Result!.Events!;
            model.AddSession(work.Id, "cmd", null);
            ControlEventBatch batch = (await Send("events.read", args: new ControlArgs { Run = anchor.Run, After = anchor.Next.ToString() })).Result!.Events!;
            Assert.Contains(batch.Items, item => item.Kind == ControlEventKinds.SessionCreated);
        }

        [Fact]
        public async Task FontSizeStepsClampsResetsAndSurvivesSnapshot()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            int structural = 0;
            model.StructureChanged += () => structural++;

            await Send("font.inc");
            await Send("font.inc");
            Assert.Equal(AppModel.DefaultFontSize + 2, a.FontSize);
            Assert.Equal(0, structural);
            Assert.Equal(AppModel.DefaultFontSize + 2, AppModel.FromSnapshot(model.ToSnapshot()).AllSessions.Single().FontSize);

            for (int i = 0; i < 100; i++)
            {
                await Send("font.dec");
            }

            Assert.Equal(AppModel.MinFontSize, a.FontSize);
            await Send("font.reset");
            Assert.Null(a.FontSize);
        }

        [Fact]
        public async Task SearchPassesNeedleAndStep()
        {
            Session session = model.AddSession(work.Id, "cmd", null);

            // As agterm's searchDisplayText, with the count beside it.
            host.SearchAnswer = "0 -1";
            ControlResult none = (await Send("session.search", args: new ControlArgs { Text = "error" })).Result!;
            Assert.Equal(("no matches", (int?)0), (none.Text, none.Count));
            host.SearchAnswer = "5 -1";
            Assert.Equal("5 matches", (await Send("session.search", args: new ControlArgs { Text = "warn" })).Result!.Text);
            host.SearchAnswer = "5 1";
            Assert.Equal("2 of 5", (await Send("session.search", args: new ControlArgs { To = "next" })).Result!.Text);
            Assert.Equal(new[] { "error/", "warn/", "/next" }, host.Searches);
            Assert.Equal(session.Id.Wire(), (await Send("session.search", args: new ControlArgs { To = "close" })).Result!.Id);
            Assert.Equal(session.Id.Wire(), (await Send("session.search", args: new ControlArgs { Text = "" })).Result!.Id);
            Assert.Equal("session.search --to must be next|prev|close", (await Send("session.search", args: new ControlArgs { To = "up" })).Error);
        }

        [Fact]
        public async Task SplitTogglesTransposesAndPersists()
        {
            Session a = model.AddSession(work.Id, "cmd", @"C:\src");

            await Send("session.split", args: new ControlArgs { Axis = "vertical" });
            Assert.True(a.SplitShown);
            Assert.True(a.SplitFocused);
            Assert.Equal(@"C:\src", a.SplitCwd);
            Assert.Equal(PaneRole.Split, a.FocusedPane);

            await Send("session.split", args: new ControlArgs { Axis = "horizontal" });
            Assert.True(a.SplitShown);
            Assert.Equal(SplitAxis.Horizontal, a.SplitAxis);

            await Send("session.split", args: new ControlArgs { Axis = "horizontal" });
            Assert.False(a.SplitShown);
            Assert.True(a.HasSplit);
            // As in agterm, hiding keeps the focus, and the focused pane shows maximized.
            Assert.Equal(PaneRole.Split, a.FocusedPane);
            Assert.False(a.RendersPane(PaneRole.Main));

            await Send("session.split", args: new ControlArgs { Mode = "on" });
            Assert.Equal(0.6, (await Send("session.resize", args: new ControlArgs { RatioDelta = 0.1 })).Result!.Ratio!.Value, 6);
            Assert.Equal(0.55, (await Send("session.resize", args: new ControlArgs { RatioDelta = -0.05 })).Result!.Ratio!.Value, 6);
            Assert.Equal("session.resize requires --split-ratio, --grow-left, or --grow-right", (await Send("session.resize")).Error);
            Assert.Equal("session.resize: --split-ratio is mutually exclusive with --grow-left/--grow-right", (await Send("session.resize", args: new ControlArgs { Ratio = 0.5, RatioDelta = 0.1 })).Error);
            Assert.Equal(Panes.MaxSplitRatio, (await Send("session.resize", args: new ControlArgs { Ratio = 2 })).Result!.Ratio);
            Assert.Equal(Panes.MaxSplitRatio, a.SplitRatio);
            model.UpdateCwd(a.Id, @"C:\tmp", PaneRole.Split);
            Assert.Equal("tmp", a.DisplayName);

            Session restored = AppModel.FromSnapshot(model.ToSnapshot()).AllSessions.Single();
            Assert.True(restored.SplitShown);
            Assert.Equal(SplitAxis.Horizontal, restored.SplitAxis);
            Assert.Equal(@"C:\tmp", restored.SplitCwd);
            Assert.Equal(Panes.MaxSplitRatio, restored.SplitRatio);
        }

        [Fact]
        public async Task FocusSwapAndCloseSplit()
        {
            Session a = model.AddSession(work.Id, "cmd", @"C:\one");
            Assert.Equal("session has no split", (await Send("session.focus")).Error);
            await Send("session.split", args: new ControlArgs { Mode = "on" });
            model.UpdateCwd(a.Id, @"C:\two", PaneRole.Split);

            await Send("session.focus", args: new ControlArgs { Pane = "left" });
            Assert.Equal(PaneRole.Main, a.FocusedPane);
            await Send("session.swap");
            Assert.Equal(@"C:\two", a.Cwd);
            Assert.Equal(@"C:\one", a.SplitCwd);
            Assert.Equal(PaneRole.Split, a.FocusedPane);

            await Send("session.split.close");
            Assert.False(a.HasSplit);
            Assert.Equal(new[] { "swap", "stop right" }, host.Calls);
            Assert.True((await Send("session.split.close")).Ok);
        }

        [Fact]
        public async Task PromotedSplitKeepsItsDirectory()
        {
            Session a = model.AddSession(work.Id, "cmd", @"C:\one");
            await Send("session.split", args: new ControlArgs { Mode = "on" });
            model.UpdateCwd(a.Id, @"C:\two", PaneRole.Split);

            model.PromoteSplit(a.Id);

            Assert.False(a.HasSplit);
            Assert.Equal(@"C:\two", a.Cwd);
        }

        [Fact]
        public async Task PaneArgumentsAreValidated()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            host.Running.Add(a.Id);

            Assert.Equal("session has no split pane", (await Send("session.text", args: new ControlArgs { Pane = "right" })).Error);
            Assert.Equal("invalid pane: middle", (await Send("session.text", args: new ControlArgs { Pane = "middle" })).Error);

            await Send("session.scratch", args: new ControlArgs { Mode = "on" });
            host.RunningPanes.Add((a.Id, PaneRole.Scratch));
            Assert.Equal("scratch all=False lines=", (await Send("session.text")).Result!.Text);
            await Send("session.type", args: new ControlArgs { Text = "ls", Pane = "scratch" });
            Assert.Equal(new[] { "scratch:ls" }, host.Written);
            Assert.Equal("session.restart requires --pane-id or --pane", (await Send("session.restart")).Error);
            Assert.Equal("session.restart does not address the scratch pane", (await Send("session.restart", args: new ControlArgs { Pane = "scratch" })).Error);
            Assert.Equal("session has no split pane", (await Send("session.restart", args: new ControlArgs { Pane = "right" })).Error);
            Assert.True((await Send("session.restart", args: new ControlArgs { Pane = "left" })).Ok);
            Assert.Equal(new[] { "restart left" }, host.Calls);

            await Send("surface.zoom");
            Assert.True(model.Zoomed);
            Assert.StartsWith("surface:", (await Send("tree")).Result!.Tree!.ZoomedSurface);
        }

        [Fact]
        public async Task OverlayRunsHoldsAndReportsItsExit()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Assert.Equal("no overlay result", (await Send("session.overlay.result")).Error);
            Assert.Equal("session.overlay.open requires a command", (await Send("session.overlay.open")).Error);
            Assert.Equal("session.overlay.open: --size-percent must be 1...100", (await Send("session.overlay.open", args: new ControlArgs { Command = "htop", SizePercent = 0 })).Error);

            await Send("session.overlay.open", args: new ControlArgs { Command = "htop", SizePercent = 70, Wait = true });
            string id = a.Overlay!.Id;
            Assert.True((await Send("tree")).Result!.Tree!.Workspaces[0].Sessions[0].Overlay);
            Assert.Equal("overlay still running", (await Send("session.overlay.result")).Error);

            await Send("session.overlay.resize", args: new ControlArgs { Full = true });
            Assert.Null(a.Overlay!.SizePercent);

            model.OverlayExited(a.Id, id, 3);
            Assert.Equal(3, a.Overlay!.ExitCode);
            ControlResult result = (await Send("session.overlay.result")).Result!;
            Assert.Equal(3, result.ExitCode);
            Assert.Equal(a.Id.Wire(), result.Id);

            await Send("session.overlay.close");
            Assert.Null(a.Overlay);
            Assert.Equal("no overlay", (await Send("session.overlay.close")).Error);
        }

        [Fact]
        public async Task OverlayWithoutWaitGoesAwayAndALateExitIsIgnored()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            await Send("session.overlay.open", args: new ControlArgs { Command = "one" });
            string first = a.Overlay!.Id;
            // As agterm's, a program in the slot keeps it.
            Assert.Equal("overlay already open", (await Send("session.overlay.open", args: new ControlArgs { Command = "two" })).Error);
            await Send("session.overlay.close");
            await Send("session.overlay.open", args: new ControlArgs { Command = "two" });
            string second = a.Overlay!.Id;

            model.OverlayExited(a.Id, first, 1);
            Assert.Equal(second, a.Overlay!.Id);

            model.OverlayExited(a.Id, second, 0);
            Assert.Null(a.Overlay);
            Assert.Equal(0, a.LastOverlayExitCode);
        }

        [Fact]
        public async Task PaneOverlaysCoverOnePaneAndMoveWithIt()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Assert.Equal("pane not visible", (await Send("session.overlay.open", args: new ControlArgs { Command = "htop", Pane = "right" })).Error);
            await Send("session.split", args: new ControlArgs { Mode = "on" });
            Assert.Equal("session.overlay.open: --pane is mutually exclusive with --size-percent",
                (await Send("session.overlay.open", args: new ControlArgs { Command = "htop", Pane = "right", SizePercent = 50 })).Error);

            await Send("session.overlay.open", args: new ControlArgs { Command = "htop", Pane = "right" });
            string right = a.Overlays[PaneRole.OverlayRight].Id;
            Assert.Equal("pane overlay already open", (await Send("session.overlay.open", args: new ControlArgs { Command = "top", Pane = "right" })).Error);
            // A HUD over a pane sits in the session's slot, beside the pane's own overlay, as agterm's.
            await Send("session.hud.open", args: new ControlArgs { Message = "left hud", Pane = "left" });
            Assert.True(a.Overlay!.IsHud);
            Assert.Equal(PaneRole.Main, a.Overlay.HudPane);
            Assert.Equal(right, a.Overlays[PaneRole.OverlayRight].Id);
            ControlSessionNode node = (await Send("tree")).Result!.Tree!.Workspaces[0].Sessions[0];
            Assert.Equal(new[] { "right" }, node.PaneOverlays);
            Assert.False(node.Overlay);
            Assert.Equal("left", node.Hud!.Pane);

            // The HUD follows the shell it was drawn over, as agterm's anchor, and goes with its pane.
            model.SwapPanes(a.Id);
            Assert.Equal(right, a.Overlays[PaneRole.OverlayLeft].Id);
            Assert.Equal(PaneRole.Split, a.Overlay!.HudPane);
            Assert.Equal("overlay still running", (await Send("session.overlay.result", args: new ControlArgs { Pane = "left" })).Error);

            model.OverlayExited(a.Id, right, 0, PaneRole.OverlayLeft);
            Assert.False(a.Overlays.ContainsKey(PaneRole.OverlayLeft));

            model.CloseSplit(a.Id);
            Assert.Null(a.Overlay);
        }

        [Fact]
        public async Task PagesAreCheckedLikeAgtermAndHandBackAValue()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            string file = Path.Combine(Path.GetTempPath(), "agshell-page-" + Guid.NewGuid().ToString("N") + ".html");
            File.WriteAllText(file, "<title>t</title>");
            try
            {
                Assert.Equal("session.overlay.open takes a command or --html, not both", (await Send("session.overlay.open", args: new ControlArgs { Command = "x", Html = file })).Error);
                Assert.Equal("session.overlay.open: --js requires --html or --url", (await Send("session.overlay.open", args: new ControlArgs { Command = "x", Js = true })).Error);
                Assert.Equal("session.overlay.open: --wait cannot be combined with --html", (await Send("session.overlay.open", args: new ControlArgs { Html = file, Wait = true })).Error);
                Assert.Equal("session.overlay.open: --chromeless with --url requires a loopback host: 127.0.0.1, ::1 or localhost",
                    (await Send("session.overlay.open", args: new ControlArgs { Url = "https://example.com", Chromeless = true })).Error);
                Assert.True((await Send("session.overlay.open", args: new ControlArgs { Url = "http://localhost:5173", Chromeless = true })).Ok);
                Assert.Equal("session.overlay.open: --persistent requires --url", (await Send("session.overlay.open", args: new ControlArgs { Html = file, Persistent = true })).Error);
                await Send("session.overlay.close");
                await Send("session.overlay.open", args: new ControlArgs { Url = "http://localhost:5173", Persistent = true });
                Assert.True(a.Overlay!.Page!.Persistent);
                await Send("browser.clear");
                Assert.Contains("browser clear", host.Calls);
                // As agterm's, reload goes back to what the page opened with; --current reloads the page shown now.
                await Send("session.overlay.reload");
                Assert.Contains("page reload-original", host.Calls);
                await Send("session.overlay.reload", args: new ControlArgs { Current = true });
                Assert.Contains("page reload", host.Calls);
                await Send("session.overlay.close");
                await Send("session.overlay.open", args: new ControlArgs { Command = "htop" });
                Assert.Equal("the overlay is not an html page", (await Send("session.overlay.reload")).Error);

                await Send("session.overlay.close");
                string id = (await Send("session.overlay.open", args: new ControlArgs { Html = file, Js = true })).Result!.PageId!;
                Assert.Equal(file, a.Overlay!.Page!.Source);
                Assert.Equal("pending", (await Send("session.overlay.result", args: new ControlArgs { PageId = id })).Result!.Page!.Outcome);
                Assert.Equal("no overlay result: the slot holds an html page", (await Send("session.overlay.result")).Error);
                Assert.Equal("no overlay to read: the slot holds an html page", (await Send("session.overlay.text")).Error);
                Assert.Equal("a URL can only be loaded in a web page overlay, and must be http or https",
                    (await Send("session.overlay.navigate", args: new ControlArgs { Url = "https://example.com" })).Error);
                await Send("session.overlay.navigate", args: new ControlArgs { To = "back" });
                Assert.Contains("page back", host.Calls);

                Assert.Equal("session.overlay.submit requires a value", (await Send("session.overlay.submit")).Error);
                await Send("session.overlay.submit", args: new ControlArgs { Value = "{\"branch\":\"main\"}" });
                Assert.Null(a.Overlay);
                ControlResult result = (await Send("session.overlay.result", args: new ControlArgs { PageId = id })).Result!;
                Assert.Equal(id, result.Page!.PageId);
                Assert.Equal("submitted", result.Page!.Outcome);
                Assert.Equal("{\"branch\":\"main\"}", result.Page.Value);

                string closed = (await Send("session.overlay.open", args: new ControlArgs { Html = file })).Result!.PageId!;
                await Send("session.overlay.close");
                Assert.Equal("dismissed", (await Send("session.overlay.result", args: new ControlArgs { PageId = closed })).Result!.Page!.Outcome);
                Assert.Equal("no overlay", (await Send("session.overlay.reload")).Error);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task HudSharesTheOverlaySlotAndHidesOnSchedule()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Assert.Equal("no hud", (await Send("session.hud.update", args: new ControlArgs { Message = "x" })).Error);
            Assert.StartsWith("invalid position", (await Send("session.hud.open", args: new ControlArgs { Message = "x", Position = "middle" })).Error);

            // As agterm's, a HUD does not end a program in the slot; a program replaces a HUD.
            await Send("session.overlay.open", args: new ControlArgs { Command = "htop" });
            Assert.Equal("overlay already open", (await Send("session.hud.open", args: new ControlArgs { Message = "x" })).Error);
            await Send("session.overlay.close");
            Assert.Equal(a.Id.Wire(), (await Send("session.hud.open", args: new ControlArgs { Message = "building", Spinner = "dot", Position = "top", HideAfter = 5 })).Result!.Id);
            string id = a.Overlay!.Id;
            Assert.True(a.Overlay!.IsHud);
            Assert.Equal("top-center", a.Overlay.Hud!.Position);
            Assert.Equal(new[] { (id, 5.0) }, host.HudHides);

            await Send("session.hud.update", args: new ControlArgs { Message = "**done**", Markdown = true });
            Assert.True(a.Overlay!.Hud!.Markdown);
            await Send("session.hud.update", args: new ControlArgs { Message = "done" });
            Assert.False(a.Overlay!.Hud!.Markdown);
            Assert.Equal(id, a.Overlay!.Id);
            Assert.Equal("done", a.Overlay.Hud!.Message);
            Assert.Null(a.Overlay.Hud.Spinner);

            model.CloseOverlay(a.Id, "someone else");
            Assert.NotNull(a.Overlay);
            Assert.Equal("a hud is always floating: pass --size-percent, not --full", (await Send("session.overlay.resize", args: new ControlArgs { Full = true })).Error);
            await Send("session.overlay.resize", args: new ControlArgs { SizePercent = 95 });
            Assert.Equal(80, a.Overlay!.SizePercent);
            await Send("session.overlay.close");
            Assert.Null(a.Overlay);
            Assert.Equal("no hud", (await Send("session.hud.close")).Error);
            Assert.Equal("hud text must not contain control characters", (await Send("session.hud.open", args: new ControlArgs { Message = "a\u001b[2Jb" })).Error);
            Assert.Equal("session.hud.open requires a message", (await Send("session.hud.open", args: new ControlArgs { Message = "[x]: http://y", Markdown = true })).Error);
        }

        [Fact]
        public async Task ASessionHasOneHudThatPaneOptionsOnlyPlace()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(a.Id, true);
            await Send("session.hud.open", args: new ControlArgs { Message = "one", Pane = "left", FontSize = 20, Color = "#112233" });
            string id = a.Overlay!.Id;
            Assert.Equal(PaneRole.Main, a.Overlay.HudPane);
            await Send("session.hud.open", args: new ControlArgs { Message = "two", Pane = "right" });
            Assert.NotEqual(id, a.Overlay!.Id);
            Assert.Equal(PaneRole.Split, a.Overlay.HudPane);
            Assert.Single(a.Overlays);

            // An update without a pane returns the panel over the whole session and keeps what it was made with.
            await Send("session.hud.open", args: new ControlArgs { Message = "three", Pane = "left", FontSize = 20, Color = "#112233" });
            Assert.Equal("session.hud.update: --font-size is fixed at open; reopen the hud to change it", (await Send("session.hud.update", args: new ControlArgs { Message = "four", FontSize = 40 })).Error);
            await Send("session.hud.update", args: new ControlArgs { Message = "four", Color = "#445566" });
            Assert.Null(a.Overlay!.HudPane);
            Assert.Equal(20, a.Overlay.Hud!.FontSize);
            Assert.Equal("#112233", a.Overlay.BackgroundColor);
            await Send("session.hud.close");
            Assert.Empty(a.Overlays);

            Assert.Equal("session.hud.open: --font-size must be 6...72 points", (await Send("session.hud.open", args: new ControlArgs { Message = "x", FontSize = 100 })).Error);
            Assert.Equal("session.hud.open requires a message", (await Send("session.hud.open", args: new ControlArgs { Message = " " })).Error);
        }

        [Fact]
        public async Task UnknownCommandsAndTargetsAreErrors()
        {
            Assert.Equal("invalid request: Cannot initialize Command from invalid String value nope", (await Send("nope")).Error);
            Assert.Equal("no such session: active", (await Send("session.select")).Error);
            Assert.Equal("no such session: 1234", (await Send("session.select", "1234")).Error);
        }

        [Fact]
        public async Task StatusWritesAskTheHostForTheirSound()
        {
            model.AddSession(work.Id, "cmd", null);
            await Send("session.status", null, new ControlArgs { Status = "blocked" });
            await Send("session.status", null, new ControlArgs { Status = "active", Sound = "Alarm01" });

            Assert.Equal(new[] { "sound blocked ", "sound active Alarm01" }, host.Calls.Where(call => call.StartsWith("sound", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task ThemeSetTakesTheNameOrLightOnceAndReportsUnknownThemes()
        {
            ControlResponse set = await Send("theme.set", null, new ControlArgs { Name = "Dracula", Dark = "none" });
            Assert.True(set.Ok);
            Assert.Equal(("agshell", false, (List<string>?)null), (set.Result!.Theme, set.Result.Sync, set.Result.Themes));
            Assert.Equal("theme Dracula/none", host.Calls.Last());

            Assert.Equal("theme.set takes either a name or --light, not both", (await Send("theme.set", null, new ControlArgs { Name = "A", Light = "B" })).Error);
            Assert.Equal("unknown theme: Nope", (await Send("theme.set", null, new ControlArgs { Light = "Nope" })).Error);
            Assert.Equal(new[] { "agshell", "Dracula" }, (await Send("theme.list")).Result!.Themes);

            // agterm trims Swift's .whitespaces only: a line break stays part of the name.
            await Send("theme.set", null, new ControlArgs { Name = " \tDracula\u00a0", Dark = " " });
            Assert.Equal("theme Dracula/", host.Calls.Last());
            await Send("theme.set", null, new ControlArgs { Name = "Dracula\n" });
            Assert.Equal("theme Dracula\n/", host.Calls.Last());
        }

        [Fact]
        public async Task FlagClearIgnoresTheTargetAndUnflagsAll()
        {
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            model.SetFlag(new[] { a.Id, b.Id }, true);

            ControlResponse response = await Send("session.flag", a.Id.Wire(), new ControlArgs { Mode = "clear" });

            Assert.Null(response.Result);
            Assert.False(a.Flagged || b.Flagged);
        }

        [Fact]
        public async Task BackgroundsBelongToTheSessionOrFollowTheirPane()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            string id = session.Id.Wire();
            string image = Path.Combine(Path.GetTempPath(), "agshell-bg-" + Guid.NewGuid() + ".png");
            File.WriteAllBytes(image, new byte[] { 1 });
            try
            {
                Assert.True((await Send("session.background", id, new ControlArgs { Mode = "image", Path = image, Fit = "cover", Opacity = 0.3 })).Ok);
                Assert.Equal(("image", image, "cover"), (session.Background!.Kind, session.Background.ImagePath, session.Background.Fit));
                Assert.Equal("no such image file: /nope.png", (await Send("session.background", id, new ControlArgs { Mode = "image", Path = "/nope.png" })).Error);
                Assert.False((await Send("session.background", id, new ControlArgs { Mode = "image", Path = image, Opacity = 2 })).Ok);
                Assert.False((await Send("session.background", id, new ControlArgs { Mode = "text", Text = "x", Position = "middle" })).Ok);
                Assert.Equal("session has no split pane", (await Send("session.background", id, new ControlArgs { Mode = "color", Color = "#112233", Pane = "right" })).Error);
            }
            finally
            {
                File.Delete(image);
            }

            model.SetSplit(session.Id, true);
            await Send("session.background", id, new ControlArgs { Mode = "text", Text = "DRIVER", Pane = "left" });
            await Send("session.background", id, new ControlArgs { Mode = "text", Text = "PEER", Pane = "right" });
            model.SwapPanes(session.Id);
            Assert.Equal("PEER", session.PaneBackgrounds[PaneRole.Main].Text);
            Assert.Equal("DRIVER", session.PaneBackgrounds[PaneRole.Split].Text);

            Session restored = AppModel.FromSnapshot(model.ToSnapshot()).FindSession(session.Id)!;
            Assert.Equal("PEER", restored.PaneBackgrounds[PaneRole.Main].Text);
            Assert.Equal("image", restored.Background!.Kind);

            model.PromoteSplit(session.Id);
            Assert.Equal("DRIVER", session.PaneBackgrounds[PaneRole.Main].Text);
            Assert.False(session.PaneBackgrounds.ContainsKey(PaneRole.Split));

            await Send("session.background", id, new ControlArgs { Mode = "clear" });
            Assert.Null(session.Background);
            Assert.True(session.PaneBackgrounds.ContainsKey(PaneRole.Main));
        }

        [Fact]
        public async Task StatusColorAndShapeLastOneCall()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            string id = session.Id.Wire();

            Assert.True((await Send("session.status", id, new ControlArgs { Status = "blocked", Color = "#FF0000", Shape = "star" })).Ok);
            Assert.Equal("#FF0000", session.StatusColor);
            Assert.Equal("star", session.StatusShape);
            Assert.False((await Send("session.status", id, new ControlArgs { Status = "blocked", Shape = "hexagon" })).Ok);
            Assert.False((await Send("session.status", id, new ControlArgs { Status = "blocked", Color = "red" })).Ok);
            Assert.Equal("star", session.StatusShape);

            await Send("session.status", id, new ControlArgs { Status = "blocked" });
            Assert.Null(session.StatusColor);
            Assert.Null(session.StatusShape);
        }

        [Fact]
        public async Task ContextIsOneCheckedLineUntilCleared()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            string id = session.Id.Wire();

            Assert.True((await Send("session.context", id, new ControlArgs { Mode = "set", Text = "  PR #42  " })).Ok);
            Assert.Equal("PR #42", session.Context);
            Assert.False((await Send("session.context", id, new ControlArgs { Mode = "set", Text = "   " })).Ok);
            Assert.False((await Send("session.context", id, new ControlArgs { Mode = "set", Text = "a\nb" })).Ok);
            Assert.False((await Send("session.context", id, new ControlArgs { Mode = "set", Text = new string('я', 129) })).Ok);
            Assert.False((await Send("session.context", id, new ControlArgs { Text = "x", Mode = "clear" })).Ok);
            Assert.Equal("PR #42", session.Context);
            Assert.Null(model.DuplicateSession(session.Id).Context);

            await Send("session.context", id, new ControlArgs { Mode = "clear" });
            Assert.Null(session.Context);
        }

        [Fact]
        public async Task CopyAndPasteGoThroughThePaneTerminal()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            Assert.Equal("session not realized", (await Send("session.copy", session.Id.Wire())).Error);

            host.Terminals[(session.Id, PaneRole.Main)] = string.Empty;
            Assert.Equal("no selection", (await Send("session.copy", session.Id.Wire())).Error);
            host.Terminals[(session.Id, PaneRole.Main)] = "picked";
            Assert.Equal("picked", (await Send("session.copy", session.Id.Wire())).Result!.Text);
            Assert.True((await Send("session.selectall", session.Id.Wire())).Ok);

            // As agterm's paste_from_clipboard: an empty clipboard pastes nothing and is no error.
            Assert.Equal(session.Id.Wire(), (await Send("session.paste", session.Id.Wire())).Result!.Id);
            host.Pasted.Clear();
            Assert.Equal("--pane must be left, right, or scratch", (await Send("session.paste", session.Id.Wire(), new ControlArgs { Pane = "middle" })).Error);
            host.WriteClipboard("ls\n");
            Assert.Equal(session.Id.Wire(), (await Send("session.paste", session.Id.Wire())).Result!.Id);
            Assert.Equal(new[] { "left:ls\n" }, host.Pasted);
        }

        [Fact]
        public async Task RestorePinsArePerPaneAndOutliveTheirUse()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            string id = session.Id.Wire();

            ControlResponse pinned = await Send("session.restore", id, new ControlArgs { Mode = "set", Command = "claude --resume x" });
            Assert.Equal("left", pinned.Result!.Pane);
            Assert.Contains("rerun", pinned.Result.Text);
            Assert.Equal("invalid restore mode:  (set|none|clear)", (await Send("session.restore", id, new ControlArgs { Command = "a" })).Error);
            Assert.Equal("session.restore set requires a command", (await Send("session.restore", id, new ControlArgs { Mode = "set" })).Error);
            Assert.False((await Send("session.restore", id, new ControlArgs { Mode = "none", Pane = "right" })).Ok);
            Assert.False((await Send("session.restore", id, new ControlArgs { Mode = "none", Pane = "scratch" })).Ok);

            // A pin set during the run waits for the next launch, which runs it, and the one after that too.
            Assert.Null(model.TakePendingPin(session.Id, PaneRole.Main));
            model.SetRestoreCommand(session.Id, PaneRole.Main, "vim");
            AppModel launched = AppModel.FromSnapshot(model.ToSnapshot(), launchRestore: true);
            Assert.Equal("claude --resume x", launched.TakePendingPin(session.Id, PaneRole.Main));
            Assert.Null(launched.TakePendingPin(session.Id, PaneRole.Main));
            Assert.Equal("claude --resume x", AppModel.FromSnapshot(launched.ToSnapshot(), launchRestore: true).TakePendingPin(session.Id, PaneRole.Main));
            Assert.Equal("claude --resume x", (await Send("tree")).Result!.Tree!.Workspaces.Single(w => w.Id == work.Id.Wire()).Sessions.Single().RestoreCommand);

            await Send("session.restore", id, new ControlArgs { Mode = "none" });
            Assert.Equal(string.Empty, AppModel.FromSnapshot(model.ToSnapshot(), launchRestore: true).TakePendingPin(session.Id, PaneRole.Main));

            host.Restore = "rerun";
            Assert.Null((await Send("session.restore", id, new ControlArgs { Mode = "clear" })).Result!.Text);
            launched = AppModel.FromSnapshot(model.ToSnapshot(), launchRestore: true);
            Assert.Null(launched.TakePendingPin(session.Id, PaneRole.Main));
            Assert.Equal("vim", launched.TakePendingCapture(session.Id, PaneRole.Main));
        }

        [Fact]
        public async Task RestoreCaptureNeedsRerunAndClearKeepsPins()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            Assert.False((await Send("restore.capture")).Ok);
            host.Restore = "rerun";
            Assert.Equal("captured 2 panes", (await Send("restore.capture")).Result!.Text);

            model.SetRestoreCommand(session.Id, PaneRole.Main, "vim");
            model.SetRestorePin(session.Id, PaneRole.Main, "htop");
            await Send("restore.clear");
            Assert.Null(session.RestoreCommand);
            Assert.Equal("htop", session.RestorePin);
        }

        [Fact]
        public async Task SidebarFoldingSkipsTheFlatFlaggedList()
        {
            Workspace other = model.AddWorkspace("other");
            model.AddSession(work.Id, "cmd", null);
            model.AddSession(other.Id, "cmd", null);
            model.SelectWorkspace(work.Id);
            model.SetSidebarMode(AppModel.FlaggedMode);

            await Send("sidebar.collapse");
            Assert.False(other.Collapsed);

            Assert.Equal("tree", (await Send("sidebar.flagged-layout")).Result!.Text);
            await Send("sidebar.collapse");
            Assert.True(other.Collapsed);
            Assert.False(work.Collapsed);

            await Send("sidebar.expand");
            Assert.False(other.Collapsed);
            Assert.False((await Send("sidebar.flagged-layout", args: new ControlArgs { Mode = "sideways" })).Ok);
        }

        [Fact]
        public async Task TheZoomKeepsItsTargetAndEndsWhenItIsGone()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(session.Id, true);
            Session other = model.AddSession(work.Id, "cmd", null);
            model.SelectSession(session.Id);

            // agterm's GUI toggle zooms the active surface and holds it: an overlay, a focus or another selection does not move it.
            model.SetZoom(null);
            Assert.Equal((session.Id, PaneRole.Split), model.ZoomTarget);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "o", Command = "htop" });
            model.FocusPane(session.Id, PaneRole.Main);
            model.SelectSession(other.Id);
            Assert.Equal((session.Id, PaneRole.Split), model.ZoomTarget);
            Assert.Equal(ControlDispatcher.SurfaceId(session.Id, PaneRole.Split), (await Send("tree")).Result!.Tree!.ZoomedSurface);

            // As agterm's zoom layer: the split closed takes the zoom down.
            model.CloseSplit(session.Id);
            Assert.True(dispatcher.DropStaleZoom());
            Assert.Null(model.ZoomTarget);
            Assert.True((await Send("surface.zoom", ControlDispatcher.SurfaceId(session.Id, PaneRole.Overlay), new ControlArgs { Mode = "show" })).Ok);
            model.CloseOverlay(session.Id, "o");
            Assert.True(dispatcher.DropStaleZoom());
            Assert.False(model.Zoomed);

            Assert.Equal("invalid surface: left", (await Send("surface.zoom", "left")).Error);
            Assert.Equal("invalid surface: left", (await Send("surface.zoom", "left", new ControlArgs { Mode = "hide" })).Error);
        }

        [Fact]
        public async Task TheZoomTogglesNothingUnderAPageAndTheDashboardClearsItsTarget()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(session.Id, true);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "page", Page = new PageSpec { Source = "https://example.com" } });
            model.SetZoom(null);
            Assert.False(model.Zoomed);
            Assert.Equal("no active surface", (await Send("surface.zoom")).Error);
            model.CloseOverlay(session.Id, "page");

            model.ZoomSurface(session.Id, PaneRole.Main, true);
            await Send("dashboard", args: new ControlArgs { Mru = true });
            Assert.Null(model.ZoomTarget);
            model.CloseDashboard();
            model.SetZoom(null);
            Assert.Equal((session.Id, PaneRole.Split), model.ZoomTarget);
        }

        [Fact]
        public async Task TheMainPaneIsNoSurfaceWhileOnlyTheFocusedSplitHasATerminal()
        {
            Session session = model.AddSession(work.Id, "cmd", null);
            model.SetSplit(session.Id, true);
            host.RunningPanes.Add((session.Id, PaneRole.Split));
            string left = ControlDispatcher.SurfaceId(session.Id, PaneRole.Main);
            Assert.Equal("surface not available: " + left, (await Send("surface.zoom", left)).Error);

            host.Running.Add(session.Id);
            Assert.True((await Send("surface.zoom", left)).Ok);
        }

        private Task<ControlResponse> Send(string cmd, string? target = null, ControlArgs? args = null)
        {
            return dispatcher.HandleAsync(new ControlRequest { Cmd = cmd, Target = target, Args = args });
        }

        private sealed class FakeHost : IControlHost
        {
            public HashSet<Guid> Running { get; } = new HashSet<Guid>();

            public HashSet<(Guid, PaneRole)> Held { get; } = new HashSet<(Guid, PaneRole)>();


            public List<Guid> Stopped { get; } = new List<Guid>();

            public List<string> Written { get; } = new List<string>();

            public string Version => "test";

            public IReadOnlyList<string> ShellIds => new[] { "pwsh", "cmd" };

            public string DefaultShellId => "pwsh";

            public bool SidebarVisible { get; private set; } = true;

            public double SidebarWidth { get; private set; } = 220;

            public HashSet<(Guid, PaneRole)> RunningPanes { get; } = new HashSet<(Guid, PaneRole)>();

            public List<string> Calls { get; } = new List<string>();

            public (List<string>? Argv, string? Shell) ForegroundOf(Guid sessionId, PaneRole pane) => (null, null);

            public string? InstalledVersion => null;

            public long? IdleMs => null;

            public long? AutoFollowMs => null;

            public bool IsRunning(Guid sessionId, PaneRole pane)
            {
                return pane == PaneRole.Main ? Running.Contains(sessionId) : RunningPanes.Contains((sessionId, pane));
            }

            public void StopPane(Guid sessionId, PaneRole pane)
            {
                Calls.Add("stop " + Panes.Name(pane));
            }

            public void SwapPaneShells(Guid sessionId)
            {
                Calls.Add("swap");
            }

            public void StopSession(Guid sessionId)
            {
                Stopped.Add(sessionId);
            }

            public void RestartSession(Guid sessionId, PaneRole pane)
            {
                Calls.Add("restart " + Panes.Name(pane));
            }

            public void Write(Guid sessionId, PaneRole pane, string data)
            {
                Written.Add(pane == PaneRole.Main ? data : Panes.Name(pane) + ":" + data);
            }

            public Task<string> ReadTextAsync(Guid sessionId, PaneRole pane, bool all, int? lines)
            {
                return Task.FromResult((pane == PaneRole.Main ? string.Empty : Panes.Name(pane) + " ") + "all=" + all + " lines=" + lines);
            }

            public bool Reveal(Guid sessionId)
            {
                return true;
            }

            public List<string> Notified { get; } = new List<string>();

            public List<string> Searches { get; } = new List<string>();

            /// <summary>The page's answer, "count index".</summary>
            public string SearchAnswer { get; set; } = "-1 -1";

            public Task<string> SearchAsync(Guid sessionId, PaneRole pane, string? needle, string? step)
            {
                Searches.Add(needle + "/" + step);
                return Task.FromResult(SearchAnswer);
            }

            public bool Banners { get; set; } = true;

            public bool Notify(Guid sessionId, string title, string body)
            {
                Notified.Add(title + ": " + body);
                return Banners;
            }

            /// <summary>The lead each live pane's daemon reported; none by default.</summary>
            public Dictionary<(Guid, PaneRole), string> Leads { get; } = new Dictionary<(Guid, PaneRole), string>();

            public string? PaneLead(Guid sessionId, PaneRole pane)
            {
                return Leads.TryGetValue((sessionId, pane), out string? lead) ? lead : null;
            }

            /// <summary>Panes joining again that have not reported their lead yet, agterm's reattaching.</summary>
            public HashSet<(Guid, PaneRole)> Reattaching { get; } = new HashSet<(Guid, PaneRole)>();

            public bool PaneCovered(Guid sessionId, PaneRole pane)
            {
                return PaneLead(sessionId, pane) is "follower" or "unowned" || Reattaching.Contains((sessionId, pane));
            }

            /// <summary>The half-typed text of a composition under way in a pane's terminal.</summary>
            public string Composition { get; set; } = string.Empty;

            /// <summary>What the daemon answers each type, in order; true once they run out.</summary>
            public Queue<bool> TypeAnswers { get; } = new Queue<bool>();

            /// <summary>Text typed through the pane's daemon, by pane.</summary>
            public List<string> DaemonTyped { get; } = new List<string>();

            public Task<bool> PaneTypeAsync(Guid sessionId, PaneRole pane, string text)
            {
                DaemonTyped.Add(Panes.Name(pane) + ":" + text);
                return Task.FromResult(TypeAnswers.Count == 0 || TypeAnswers.Dequeue());
            }

            /// <summary>What a pane's daemon answers to a screen read; null as a daemon that is not there.</summary>
            public DaemonScreen? DaemonScreen { get; set; }

            /// <summary>The daemon reads, by pane and whether they asked for the history too.</summary>
            public List<string> ScreenReads { get; } = new List<string>();

            public Task<DaemonScreen?> PaneScreenAsync(Guid sessionId, PaneRole pane, bool all)
            {
                ScreenReads.Add(Panes.Name(pane) + " all=" + all);
                return Task.FromResult(DaemonScreen);
            }

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

            public List<(string Id, double Seconds)> HudHides { get; } = new List<(string, double)>();

            public Task<string?> ClearBrowserDataAsync()
            {
                Calls.Add("browser clear");
                return Task.FromResult<string?>(null);
            }

            public bool BrowserClearing => false;

            public IReadOnlyList<string> ReloadConfig(bool keymapFile, bool hooksFile)
            {
                ConfigReloads++;
                return new[] { "line 1: oops" };
            }

            public int ConfigReloads { get; private set; }

            public int HookReads { get; private set; }

            public ControlKeymap KeymapList() => new ControlKeymap { Path = "keymap.conf", Actions = new List<ControlKeymapAction>(), Commands = new List<ControlKeymapCommand>(), Diagnostics = new List<ControlConfigDiagnostic>() };

            public ControlHooks HooksList()
            {
                HookReads++;
                return new ControlHooks { Path = "hooks.conf", Diagnostics = new List<ControlConfigDiagnostic>(), Hooks = new List<ControlHookEntry>() };
            }

            public string? RunCustomCommand(string name, Guid? sessionId)
            {
                return name == "Known" ? null : "no custom command named " + name;
            }

            public bool HasCustomCommand(string name) => name == "Known";

            public (IReadOnlyList<string> Names, string? Current, bool Sync, string? Light, string? Dark) ThemeState() => (new[] { "agshell", "Dracula" }, "agshell", false, null, null);

            public Task<(ControlLiveInventory? Inventory, string? Error)> LiveListAsync() => Task.FromResult<(ControlLiveInventory?, string?)>((new ControlLiveInventory { Restore = new ControlRestoreStatus { Configured = "none", RequestedAtLaunch = "none", Active = "none" }, InventoryComplete = true, Entries = new List<ControlLiveEntry>() }, null));

            public Task<(string? Error, string Report, int Affected)> LivePruneAsync() => Task.FromResult<(string?, string, int)>((null, "no orphan daemons", 0));

            public string Layout { get; set; } = "flat";

            public string Restore { get; set; } = "none";

            public string? Clipboard { get; set; }

            /// <summary>The terminals the page has, by session and pane, with their selection.</summary>
            public Dictionary<(Guid, PaneRole), string> Terminals { get; } = new Dictionary<(Guid, PaneRole), string>();

            public List<string> Pasted { get; } = new List<string>();

            public (string? Error, string Mode) LinkMode(string? mode) => mode is null or "browser" or "overlay" ? (null, mode ?? "browser") : ("invalid link mode: " + mode, "browser");

            public Task<string?> TerminalAsync(Guid sessionId, PaneRole pane, string action, string? text)
            {
                if (!Terminals.TryGetValue((sessionId, pane), out string? selection))
                {
                    return Task.FromResult<string?>(null);
                }

                if (action == "paste")
                {
                    Pasted.Add(Panes.Name(pane) + ":" + text);
                }

                if (action == "takeComposition")
                {
                    return Task.FromResult<string?>(Composition);
                }

                return Task.FromResult<string?>(action switch { "copy" => selection, "cursor" => "7", _ => string.Empty });
            }

            public string? LocalPath(Guid sessionId, string path) => path.StartsWith('/') ? null : path;

            public int Raised { get; private set; }

            public void Raise()
            {
                Raised++;
            }

            public bool ReleaseHeld(Guid sessionId, PaneRole pane) => Held.Remove((sessionId, pane));

            public bool IsHeld(Guid sessionId, PaneRole pane) => Held.Contains((sessionId, pane));

            public string? ReadClipboard() => Clipboard;

            public void WriteClipboard(string text)
            {
                Clipboard = text;
            }

            public (string? Error, int Count) CaptureRestoreNow() => Restore == "rerun" ? (null, 2) : ("restore.capture requires rerun mode; configured restore mode is " + Restore, 0);

            public (string? Error, string Layout) FlaggedLayout(string? layout)
            {
                if (layout is null)
                {
                    return (null, Layout);
                }

                if (layout is not ("flat" or "plain" or "tree" or "toggle"))
                {
                    return ("flagged layout is flat, plain, tree or toggle", Layout);
                }

                Layout = layout == "toggle" ? (Layout == "tree" ? "flat" : "tree") : layout;
                return (null, Layout);
            }

            public bool Saves { get; set; } = true;

            public bool SaveState() => Saves;

            public (string? Error, ControlRestoreStatus Status) RestoreMode(string? mode)
            {
                ControlRestoreStatus status = new ControlRestoreStatus { Configured = mode ?? Restore, RequestedAtLaunch = Restore, Active = Restore };
                return mode is null or "none" or "rerun" ? (null, status) : ("invalid restore mode: " + mode, status);
            }

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
                if (sound is not null || status == AgentStatus.Blocked)
                {
                    Calls.Add("sound " + AgentStatuses.Name(status) + " " + sound);
                }
            }

            public string? SetTheme(string? light, string? dark)
            {
                Calls.Add("theme " + light + "/" + dark);
                return light == "Nope" ? "unknown theme: Nope" : null;
            }

            public string? PageCommand(Guid sessionId, PaneRole slot, string action, string? url)
            {
                Calls.Add("page " + action + (url is null ? string.Empty : " " + url));
                return null;
            }

            public void ScheduleHudHide(Guid sessionId, string overlayId, double? seconds)
            {
                if (seconds is double hide)
                {
                    HudHides.Add((overlayId, hide));
                }
            }

            public void SetSidebar(bool? visible, double? width)
            {
                SidebarVisible = visible ?? SidebarVisible;
                SidebarWidth = width ?? SidebarWidth;
            }
        }
    }
}
