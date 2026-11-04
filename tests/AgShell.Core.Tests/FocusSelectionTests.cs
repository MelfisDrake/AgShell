using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    /// <summary>agterm's focus filter and selection rules: AppStore, AppStore+Focus, +CloseReselection and +PendingClose.</summary>
    public class FocusSelectionTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Workspace work;
        private readonly Workspace home;
        private readonly Workspace spare;
        private readonly Session a;
        private readonly Session b;
        private readonly Session c;

        public FocusSelectionTests()
        {
            work = model.AddWorkspace("work");
            home = model.AddWorkspace("home");
            spare = model.AddWorkspace("spare");
            a = model.AddSession(work.Id, "cmd", null, name: "a");
            b = model.AddSession(work.Id, "cmd", null, name: "b");
            c = model.AddSession(home.Id, "cmd", null, name: "c");
            model.SelectSession(a.Id);
        }

        [Fact]
        public void ASessionMadeInAHiddenWorkspaceTurnsTheFilterOffAndKeepsTheSet()
        {
            model.ToggleFocusWorkspace(work.Id);
            model.AddSession(home.Id, "cmd", null);

            Assert.False(model.FocusEnabled);
            Assert.Equal(new[] { work.Id }, model.FocusMembers);
        }

        [Fact]
        public void MovingTheSelectedSessionOutOfTheSetTurnsTheFilterOff()
        {
            model.ToggleFocusWorkspace(work.Id);
            model.MoveSession(b.Id, home.Id, 0);
            Assert.True(model.FocusEnabled);

            model.MoveSession(a.Id, home.Id, 0);
            Assert.False(model.FocusEnabled);
        }

        [Fact]
        public void SelectingAHiddenEmptyWorkspaceMarksIt()
        {
            model.ToggleFocusWorkspace(work.Id);
            model.SelectWorkspace(spare.Id);

            Assert.True(model.FocusEnabled);
            Assert.Contains(spare.Id, model.FocusMembers);
            Assert.Same(spare, model.CurrentWorkspace);
        }

        [Fact]
        public void WorkspaceStepsFollowAgtermsEdgeCases()
        {
            model.ToggleFocusWorkspace(home.Id);
            model.SetFocusEnabled(false);
            model.SelectSession(a.Id);
            model.SetFocusEnabled(true);
            // The hidden selection was moved into view: home is current, and alone it is a dead end.
            Assert.Same(home, model.CurrentWorkspace);
            Assert.False(model.SelectRelativeWorkspace(1));

            model.SetFocusMember(spare.Id, true);
            model.SetFocusMember(home.Id, false);
            // home is outside the set now: the step enters the set at the end it comes from.
            Assert.True(model.CanStepWorkspaces);
            Assert.True(model.SelectRelativeWorkspace(-1));
            Assert.Same(spare, model.CurrentWorkspace);

            model.SetSidebarMode(AppModel.FlaggedMode);
            Assert.False(model.SelectRelativeWorkspace(1));
        }

        [Fact]
        public void AddingToFocusOnlyMarks()
        {
            model.SetFocusMember(home.Id, true);
            Assert.False(model.FocusEnabled);
            Assert.Equal(new[] { home.Id }, model.FocusMembers);

            model.SetFocusMember(home.Id, false);
            model.SetFocusEnabled(true);
            Assert.False(model.FocusEnabled);
        }

        [Fact]
        public void ASelectionRevealsAFoldedWorkspaceOnScreenOnly()
        {
            model.SetWorkspaceCollapsed(home.Id, true);
            model.SelectSession(c.Id);

            Assert.True(home.Collapsed);
            Assert.False(model.ShownCollapsed(home));

            model.SetWorkspaceCollapsed(home.Id, true);
            Assert.True(model.ShownCollapsed(home));
        }

        [Fact]
        public void ClosingTheSelectedSessionPicksTheMostRecentShownOneOfItsWorkspace()
        {
            Session d = model.AddSession(work.Id, "cmd", null, name: "d");
            model.SelectSession(c.Id);
            model.SelectSession(b.Id);
            model.SelectSession(d.Id);
            model.ToggleFocusWorkspace(work.Id);

            model.RemoveSession(d.Id);
            Assert.Equal(b.Id, model.SelectedSessionId);
        }

        [Fact]
        public void ABatchCloseMovesTheSelectionOnceAndUndoSelectsTheActiveOne()
        {
            Session d = model.AddSession(work.Id, "cmd", null, name: "d");
            model.SelectSession(b.Id);
            int before = model.Events.Read(model.Events.Run, 0, new HashSet<string> { ControlEventKinds.SessionSelected }, 100).Batch.Items.Count;

            PendingClose close = model.SoftCloseSessions(new[] { a.Id, b.Id })!;
            List<ControlEvent> events = model.Events.Read(model.Events.Run, 0, new HashSet<string> { ControlEventKinds.SessionSelected, ControlEventKinds.SessionClosed }, 100).Batch.Items.ToList();
            Assert.Equal(before + 1, events.Count(item => item.Kind == ControlEventKinds.SessionSelected));
            // Closed before the new selection, as agterm's.
            Assert.Equal(ControlEventKinds.SessionSelected, events[^1].Kind);
            Assert.Equal(d.Id, model.SelectedSessionId);

            Assert.True(model.UndoClose(close.Id));
            Assert.Equal(b.Id, model.SelectedSessionId);
        }

        [Fact]
        public void UndoingAWorkspaceCloseMergesIntoARebuiltShellAndMarksItAgain()
        {
            model.ToggleFocusWorkspace(work.Id);
            model.SetFocusMember(home.Id, true);
            PendingClose closeSession = model.SoftCloseSessions(new[] { a.Id })!;
            PendingClose closeWorkspace = model.SoftCloseWorkspace(work.Id)!;
            Assert.DoesNotContain(work.Id, model.FocusMembers);

            // Undoing the session close rebuilds work as a shell; undoing the workspace close merges into it.
            model.UndoClose(closeSession.Id);
            model.UndoClose(closeWorkspace.Id);

            Assert.Single(model.Workspaces, workspace => workspace.Id == work.Id);
            Assert.Equal(new[] { a.Id, b.Id }, model.FindWorkspace(work.Id)!.Sessions.Select(session => session.Id).OrderBy(id => id == b.Id));
            Assert.Contains(work.Id, model.FocusMembers);
        }

        [Fact]
        public void OpenRecentMarksAWorkspaceThatWasInTheSet()
        {
            model.SetFocusMember(home.Id, true);
            model.SetFocusMember(work.Id, true);
            model.RemoveWorkspace(home.Id);
            Assert.DoesNotContain(home.Id, model.FocusMembers);

            Assert.True(model.ReopenRecent());
            Assert.Contains(home.Id, model.FocusMembers);
        }

        [Fact]
        public void CreatingASessionKeepsThePreviousOnesCompleted()
        {
            model.SetStatus(a.Id, AgentStatus.Completed, autoReset: true);
            model.AddSession(work.Id, "cmd", null);
            Assert.Equal(AgentStatus.Completed, a.Status);
        }
    
        [Fact]
        public void IdleChangesSendNoTreeChanged()
        {
            int trees = 0;
            int saves = 0;
            model.TreeChanged += () => trees++;
            model.StructureChanged += () => saves++;

            model.RenameSession(a.Id, "a");
            model.RenameWorkspace(work.Id, "work");
            model.MoveSession(a.Id, work.Id, 0);
            model.MoveWorkspace(work.Id, 0);
            model.SetSidebarMode(AppModel.TreeMode);
            model.SetFlag(new[] { a.Id }, false);
            Assert.Equal(0, trees);

            model.SetWorkspaceCollapsed(work.Id, true);
            model.SetSidebarMode(AppModel.FlaggedMode);
            Assert.Equal(0, trees);
            Assert.True(saves >= 2);
        }

        [Fact]
        public void ASnapshotFoldsRepeatedWorkspacesAndMovesAHiddenSelectionIntoView()
        {
            model.ToggleFocusWorkspace(home.Id);
            StateSnapshot saved = model.ToSnapshot();
            StateSnapshot doubled = saved with
            {
                SelectedSessionId = a.Id,
                Workspaces = saved.Workspaces.Concat(new[] { saved.Workspaces[0] with { Sessions = new List<SessionSnapshot> { saved.Workspaces[0].Sessions[0] with { Id = Guid.NewGuid() } } } }).ToList(),
            };

            AppModel restored = AppModel.FromSnapshot(doubled);
            Assert.Equal(3, restored.Workspaces.Count);
            Assert.Equal(3, restored.FindWorkspace(work.Id)!.Sessions.Count);
            Assert.Equal(c.Id, restored.SelectedSessionId);
        }

        [Fact]
        public void AContextWithALineBreakIsDroppedOnLoad()
        {
            StateSnapshot saved = model.ToSnapshot();
            WorkspaceSnapshot first = saved.Workspaces[0];
            StateSnapshot edited = saved with { Workspaces = new[] { first with { Sessions = new List<SessionSnapshot> { first.Sessions[0] with { Context = "two\nlines" } } } }.Concat(saved.Workspaces.Skip(1)).ToList() };
            Assert.Null(AppModel.FromSnapshot(edited).FindSession(a.Id)!.Context);
        }

        [Fact]
        public void AShellRunningAScriptIsAProgram()
        {
            Assert.True(CommandRestore.IsIdleShell(new[] { "bash", "-l" }));
            Assert.True(CommandRestore.IsIdleShell(new[] { "cmd.exe", "/d" }));
            Assert.False(CommandRestore.IsIdleShell(new[] { "bash", "./run.sh" }));
            Assert.False(CommandRestore.IsIdleShell(new[] { "pwsh", "-File", "x.ps1" }));
            Assert.True(CommandRestore.IsIdleShell(new[] { "wsl.exe", "-d", "Ubuntu" }));
            Assert.True(CommandRestore.ShouldRestore("bash", "bash ./run.sh", new HashSet<string>()));
            Assert.False(CommandRestore.ShouldRestore("claude", "claude \uFFFD", new HashSet<string>()));
        }
    
        [Fact]
        public void ANewPaneTakesTheSessionsFontSizeAndOthersKeepTheirs()
        {
            model.SetSplit(a.Id, true);
            model.ChangeFontSize(a.Id, 4, PaneRole.Split);
            Assert.Equal(AppModel.DefaultFontSize, a.FontSize);
            Assert.Equal(AppModel.DefaultFontSize + 4, a.SplitFontSize);
            Assert.Equal(AppModel.DefaultFontSize + 4, a.ScratchFontSize);
            Assert.Equal(AppModel.DefaultFontSize + 4, AppModel.FromSnapshot(model.ToSnapshot()).FindSession(a.Id)!.FontSize);
        }

        [Fact]
        public void ASessionAlwaysHasADirectoryExceptWslAndRemote()
        {
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), model.AddSession(work.Id, "cmd", null).Cwd);
            Assert.Null(model.AddSession(work.Id, "wsl:Ubuntu", null).Cwd);
        }
    }
}
