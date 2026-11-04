namespace AgShell.Core.Tests
{
    public class FocusTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Workspace work;
        private readonly Workspace home;
        private readonly Workspace misc;
        private readonly Session a;
        private readonly Session b;
        private readonly Session c;
        private readonly Session d;

        public FocusTests()
        {
            work = model.AddWorkspace("work");
            home = model.AddWorkspace("home");
            misc = model.AddWorkspace("misc");
            a = model.AddSession(work.Id, "cmd", null, name: "a");
            b = model.AddSession(work.Id, "cmd", null, name: "b");
            c = model.AddSession(home.Id, "cmd", null, name: "c");
            d = model.AddSession(misc.Id, "cmd", null, name: "d");
        }

        private string Navigable() => string.Join(",", model.NavigableSessions.Select(session => session.DisplayName));

        [Fact]
        public void FlagTogglesAllUnlessAllAreFlagged()
        {
            model.SetFlag(new[] { a.Id }, true);
            model.SetFlag(new[] { a.Id, c.Id }, null);
            Assert.True(a.Flagged && c.Flagged);

            model.SetFlag(new[] { a.Id, c.Id }, null);
            Assert.False(a.Flagged || c.Flagged);
        }

        [Fact]
        public void ClearFlagsUnflagsEverySession()
        {
            model.SetFlag(new[] { a.Id, c.Id }, true);

            Assert.Equal(2, model.ClearFlags());
            Assert.Empty(model.FlaggedSessions);
            Assert.Equal(0, model.ClearFlags());
        }

        [Fact]
        public void CollapseKeepsTheCurrentWorkspaceOpenAndExpandOpensAll()
        {
            model.SelectSession(c.Id);
            model.ToggleFocusWorkspace(home.Id);

            model.CollapseOtherWorkspaces();
            Assert.True(work.Collapsed && misc.Collapsed);
            Assert.False(home.Collapsed);

            model.ExpandAllWorkspaces();
            Assert.False(work.Collapsed || home.Collapsed || misc.Collapsed);
        }

        [Fact]
        public void TheFlaggedViewNarrowsNavigationAndMovesAHiddenSelection()
        {
            model.SetFlag(new[] { b.Id, d.Id }, true);
            model.SelectSession(c.Id);
            model.SelectSession(b.Id);
            model.SelectSession(a.Id);

            model.SetSidebarMode(AppModel.FlaggedMode);
            Assert.Equal("b,d", Navigable());
            Assert.Equal(b.Id, model.SelectedSessionId);

            model.SelectRelative(1);
            Assert.Equal(d.Id, model.SelectedSessionId);
            model.SelectRelative(1);
            Assert.Equal(b.Id, model.SelectedSessionId);

            model.SetSidebarMode(null);
            Assert.Equal(AppModel.TreeMode, model.SidebarMode);
        }

        [Fact]
        public void FocusReplacesTheSetAndTogglesOffOnTheSoleMember()
        {
            model.SelectSession(a.Id);
            model.ToggleFocusWorkspace(home.Id);
            Assert.Equal(new[] { home.Id }, model.FocusMembers);
            Assert.Equal("c", Navigable());
            Assert.Equal(c.Id, model.SelectedSessionId);
            Assert.Equal(home.Id, model.SoleFocusedWorkspaceId);

            model.SetFocusMember(misc.Id, true);
            Assert.Equal("c,d", Navigable());
            Assert.Null(model.SoleFocusedWorkspaceId);

            model.ToggleFocusWorkspace(misc.Id);
            Assert.Equal(new[] { misc.Id }, model.FocusMembers);
            model.ToggleFocusWorkspace(misc.Id);
            Assert.Empty(model.FocusMembers);
            Assert.False(model.FocusEnabled);
        }

        [Fact]
        public void SelectingOutsideTheSetSwitchesTheFilterOffButKeepsTheSet()
        {
            model.ToggleFocusWorkspace(home.Id);
            model.SelectSession(d.Id);

            Assert.False(model.FocusEnabled);
            Assert.Equal(new[] { home.Id }, model.FocusMembers);
            Assert.Equal("a,b,c,d", Navigable());

            model.SetFocusEnabled(true);
            Assert.Equal(c.Id, model.SelectedSessionId);
        }

        [Fact]
        public void WorkspaceStepsStayInsideTheFilter()
        {
            model.ToggleFocusWorkspace(work.Id);
            model.SetFocusMember(misc.Id, true);
            model.SelectSession(a.Id);

            Assert.True(model.SelectRelativeWorkspace(1));
            Assert.Equal(d.Id, model.SelectedSessionId);
            Assert.True(model.SelectRelativeWorkspace(1));
            Assert.Equal(a.Id, model.SelectedSessionId);
        }

        [Fact]
        public void FlagsAndFocusSurviveARestart()
        {
            model.SetFlag(new[] { c.Id }, true);
            model.ToggleFocusWorkspace(home.Id);
            model.SetSidebarMode(AppModel.FlaggedMode);

            AppModel restored = AppModel.FromSnapshot(model.ToSnapshot());

            Assert.True(restored.FindSession(c.Id)!.Flagged);
            Assert.False(restored.FindSession(a.Id)!.Flagged);
            Assert.Equal(new[] { home.Id }, restored.FocusMembers);
            Assert.True(restored.FocusEnabled);
            Assert.Equal(AppModel.FlaggedMode, restored.SidebarMode);
        }

        [Fact]
        public void ADeletedWorkspaceLeavesTheSet()
        {
            model.ToggleFocusWorkspace(home.Id);
            model.RemoveWorkspace(home.Id);

            Assert.Empty(model.FocusMembers);
            Assert.Equal("a,b,d", Navigable());
        }
    }
}
