namespace AgShell.Core.Tests
{
    public class PaneFocusEventsTests
    {
        private readonly AppModel model = new AppModel();
        private readonly PaneFocusEvents events = new PaneFocusEvents();
        private readonly Session session;

        public PaneFocusEventsTests()
        {
            Workspace work = model.AddWorkspace("work");
            session = model.AddSession(work.Id, "cmd", null);
            model.PaneIdFor(session.Id, PaneRole.Main);
            model.SetSplit(session.Id, true);
            model.PaneIdFor(session.Id, PaneRole.Split);
            model.FocusPane(session.Id, PaneRole.Main);
            Assert.False(events.Observe(model.AllSessions));
        }

        [Fact]
        public void AMoveToTheOtherPaneIsReportedOnceSettled()
        {
            model.FocusPane(session.Id, PaneRole.Split);
            Assert.True(events.Observe(model.AllSessions));

            (Guid id, AgShell.Protocol.ControlEventPayload payload) = Assert.Single(events.Settle(model.FindSession));
            Assert.Equal(session.Id, id);
            Assert.Equal("right", payload.Pane);
            Assert.Equal(session.PaneIds[PaneRole.Split], payload.PaneId);
            Assert.Empty(events.Settle(model.FindSession));
        }

        [Fact]
        public void ANewSplitTakingFocusAndClosingTheFocusedSplitAreReported()
        {
            Workspace other = model.AddWorkspace("other");
            Session single = model.AddSession(other.Id, "cmd", null);
            model.PaneIdFor(single.Id, PaneRole.Main);
            events.Observe(model.AllSessions);

            model.SetSplit(single.Id, true);
            Assert.True(events.Observe(model.AllSessions));
            Assert.Equal("right", Assert.Single(events.Settle(model.FindSession)).Payload.Pane);

            model.CloseSplit(single.Id);
            Assert.True(events.Observe(model.AllSessions));
            Assert.Equal("left", Assert.Single(events.Settle(model.FindSession)).Payload.Pane);
        }

        [Fact]
        public void APaneGettingItsIdWhenItsShellStartsStaysSilent()
        {
            Workspace other = model.AddWorkspace("other");
            Session fresh = model.AddSession(other.Id, "cmd", null);
            events.Observe(model.AllSessions);

            model.PaneIdFor(fresh.Id, PaneRole.Main);
            events.Observe(model.AllSessions);
            Assert.Empty(events.Settle(model.FindSession));

            model.SetSplit(fresh.Id, true);
            Assert.True(events.Observe(model.AllSessions));
            Assert.Equal("right", Assert.Single(events.Settle(model.FindSession)).Payload.Pane);

            model.PaneIdFor(fresh.Id, PaneRole.Split);
            events.Observe(model.AllSessions);
            Assert.Empty(events.Settle(model.FindSession));
        }

        [Fact]
        public void AMoveAndBackOrASwapStaysSilent()
        {
            model.FocusPane(session.Id, PaneRole.Split);
            events.Observe(model.AllSessions);
            model.FocusPane(session.Id, PaneRole.Main);
            events.Observe(model.AllSessions);
            Assert.Empty(events.Settle(model.FindSession));

            model.SwapPanes(session.Id);
            events.Observe(model.AllSessions);
            Assert.Empty(events.Settle(model.FindSession));
        }
    }
}
