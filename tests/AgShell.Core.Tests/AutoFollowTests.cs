namespace AgShell.Core.Tests
{
    public class AutoFollowTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Session a;
        private readonly Session b;
        private readonly Session c;

        public AutoFollowTests()
        {
            Workspace workspace = model.AddWorkspace("w");
            a = model.AddSession(workspace.Id, "cmd", null);
            b = model.AddSession(workspace.Id, "cmd", null);
            c = model.AddSession(workspace.Id, "cmd", null);
            model.SelectSession(a.Id);
        }

        [Fact]
        public void GoesToTheBlockedSessionThatWaitedLongest()
        {
            model.SetStatus(c.Id, AgentStatus.Blocked);
            Thread.Sleep(5);
            model.SetStatus(b.Id, AgentStatus.Blocked);

            Assert.Equal(c.Id, model.AutoFollow(stayOnActive: false));
            Assert.Equal(c.Id, model.SelectedSessionId);
        }

        [Fact]
        public void StaysOnABlockedSessionAndOptionallyOnARunningOne()
        {
            model.SetStatus(b.Id, AgentStatus.Blocked);
            model.SetStatus(a.Id, AgentStatus.Active);
            Assert.Null(model.AutoFollowTarget(stayOnActive: true));
            Assert.Equal(b.Id, model.AutoFollowTarget(stayOnActive: false));

            model.SetStatus(a.Id, AgentStatus.Blocked);
            Assert.Null(model.AutoFollowTarget(stayOnActive: false));
        }

        [Fact]
        public void DoesNotPullBackToALeftSessionUntilItBlocksAgain()
        {
            model.SetStatus(b.Id, AgentStatus.Blocked);
            model.AutoFollow(stayOnActive: false);
            model.SelectSession(a.Id);
            Assert.Null(model.AutoFollowTarget(stayOnActive: false));

            model.SetStatus(b.Id, AgentStatus.Blocked);
            Assert.Null(model.AutoFollowTarget(stayOnActive: false));

            model.SetStatus(b.Id, AgentStatus.Active);
            model.SetStatus(b.Id, AgentStatus.Blocked);
            Assert.Equal(b.Id, model.AutoFollowTarget(stayOnActive: false));
        }
    }
}
