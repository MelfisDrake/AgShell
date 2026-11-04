namespace AgShell.Core.Tests
{
    public class SidebarDropModelTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Workspace work;
        private readonly Workspace other;
        private readonly Session a;
        private readonly Session b;
        private readonly Session c;
        private readonly Session x;
        private int treeChanges;

        public SidebarDropModelTests()
        {
            work = model.AddWorkspace("work");
            other = model.AddWorkspace("other");
            a = model.AddSession(work.Id, "cmd", null);
            b = model.AddSession(work.Id, "cmd", null);
            c = model.AddSession(work.Id, "cmd", null);
            x = model.AddSession(other.Id, "cmd", null);
            model.TreeChanged += () => treeChanges++;
        }

        private static Guid[] Ids(Workspace workspace)
        {
            return workspace.Sessions.Select(session => session.Id).ToArray();
        }

        [Fact]
        public void ABatchDropMovesTheBlockInTreeOrderWithOneTreeChange()
        {
            Assert.Equal(2, model.DropSessions(new[] { c.Id, a.Id }, other.Id, x.Id, after: false));

            Assert.Equal(new[] { a.Id, c.Id, x.Id }, Ids(other));
            Assert.Equal(new[] { b.Id }, Ids(work));
            Assert.Equal(1, treeChanges);
        }

        [Fact]
        public void ADropWhereTheSessionsAlreadyStandIsANoOp()
        {
            Assert.Null(model.ResolveSessionDrop(new[] { b.Id }, work.Id, a.Id, after: true));
            Assert.Null(model.ResolveSessionDrop(new[] { c.Id }, work.Id, null, after: true));
            Assert.Null(model.ResolveSessionDrop(new[] { a.Id, b.Id }, work.Id, c.Id, after: false));
            Assert.Equal(0, model.DropSessions(new[] { b.Id }, work.Id, c.Id, after: false));
            Assert.Equal(0, treeChanges);
        }

        [Fact]
        public void ADropOnTheLowerHalfOfARowLandsAfterIt()
        {
            model.DropSessions(new[] { a.Id }, work.Id, b.Id, after: true);

            Assert.Equal(new[] { b.Id, a.Id, c.Id }, Ids(work));
        }

        [Fact]
        public void AMoveWithoutAnIndexLeavesThoseAlreadyThere()
        {
            Assert.Equal(1, model.MoveSessions(new[] { x.Id, a.Id }, other.Id));

            Assert.Equal(new[] { x.Id, a.Id }, Ids(other));
        }

        [Fact]
        public void AWorkspaceDropUnderTheFilterLandsNextToTheRowAimedAt()
        {
            Workspace third = model.AddWorkspace("third");
            model.SetFocusMember(other.Id, true);
            model.SetFocusMember(third.Id, true);
            model.SetFocusEnabled(true);

            Assert.True(model.DropWorkspace(third.Id, 0));

            Assert.Equal(new[] { work.Id, third.Id, other.Id }, model.Workspaces.Select(workspace => workspace.Id));
            Assert.False(model.DropWorkspace(third.Id, 1));
        }

        [Fact]
        public void ABlockDroppedAfterOneOfItsOwnRowsClosesUpThere()
        {
            Assert.Equal(2, model.DropSessions(new[] { a.Id, c.Id }, work.Id, c.Id, after: true));

            Assert.Equal(new[] { b.Id, a.Id, c.Id }, Ids(work));
        }

        [Fact]
        public void ARowADropOpenedStaysOpenOnScreenWithItsFoldSaved()
        {
            model.SetWorkspaceCollapsed(other.Id, true);

            model.KeepSpringOpen(other.Id);

            Assert.False(model.ShownCollapsed(other));
            Assert.True(other.Collapsed);
        }
    }
}
