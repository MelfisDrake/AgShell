// Portions adapted from umputun/agterm e0b4844.
// Copyright (c) 2026 Umputun. MIT; see licenses/agterm.txt.
using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class AgentStatusTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Workspace workspace;
        private readonly Session a;
        private readonly Session b;
        private readonly Session c;

        public AgentStatusTests()
        {
            workspace = model.AddWorkspace("w");
            a = model.AddSession(workspace.Id, "cmd", null);
            b = model.AddSession(workspace.Id, "cmd", null);
            c = model.AddSession(workspace.Id, "cmd", null);
        }

        [Fact]
        public void AutoResetClearsWhenTheSessionIsVisitedOrLeft()
        {
            model.SetStatus(a.Id, AgentStatus.Completed, autoReset: true);
            model.SetStatus(b.Id, AgentStatus.Completed);

            model.SelectSession(a.Id);
            Assert.Equal(AgentStatus.Idle, a.Status);

            model.SetStatus(a.Id, AgentStatus.Completed, autoReset: true);
            model.SelectSession(b.Id);
            Assert.Equal(AgentStatus.Idle, a.Status);
            Assert.Equal(AgentStatus.Completed, b.Status);
        }

        [Theory]
        [InlineData("enter", AgentStatus.Blocked, StatusKeystroke.Other, false)]
        [InlineData("enter", AgentStatus.Blocked, StatusKeystroke.Submit, true)]
        [InlineData("enter", AgentStatus.Blocked, StatusKeystroke.Interrupt, true)]
        [InlineData("enter", AgentStatus.Completed, StatusKeystroke.Interrupt, false)]
        [InlineData("never", AgentStatus.Completed, StatusKeystroke.Submit, false)]
        [InlineData("never", AgentStatus.Active, StatusKeystroke.Interrupt, true)]
        [InlineData("firstKey", AgentStatus.Completed, StatusKeystroke.Other, true)]
        public void TheResetSettingDecidesWhichKeyAnswers(string reset, AgentStatus status, StatusKeystroke keystroke, bool cleared)
        {
            model.StatusReset = reset;
            model.SetStatus(a.Id, status);
            model.ClearStatusByInput(a.Id, keystroke);
            Assert.Equal(cleared ? AgentStatus.Idle : status, a.Status);
        }

        [Fact]
        public void KeystrokesClearOnlyWhatTheyAnswer()
        {
            model.SetStatus(a.Id, AgentStatus.Active);
            model.ClearStatusByInput(a.Id, StatusKeystroke.Submit);
            Assert.Equal(AgentStatus.Active, a.Status);
            model.ClearStatusByInput(a.Id, StatusKeystroke.Interrupt);
            Assert.Equal(AgentStatus.Idle, a.Status);

            model.SetStatus(a.Id, AgentStatus.Blocked);
            model.ClearStatusByInput(a.Id, StatusKeystroke.Other);
            Assert.Equal(AgentStatus.Idle, a.Status);
        }

        [Fact]
        public void KeysAreClassifiedAsAgtermDoes()
        {
            Assert.Equal(StatusKeystroke.Interrupt, AgentStatuses.ClassifyKey("Escape", "Escape", false, false, false, false));
            Assert.Equal(StatusKeystroke.Interrupt, AgentStatuses.ClassifyKey("Escape", "Escape", false, true, false, false));
            Assert.Equal(StatusKeystroke.Interrupt, AgentStatuses.ClassifyKey("c", "KeyC", true, false, false, false));
            // A Cyrillic layout gives no "c" there, Dvorak gives "j": only the first is Ctrl+C.
            Assert.Equal(StatusKeystroke.Interrupt, AgentStatuses.ClassifyKey("\u0441", "KeyC", true, false, false, false));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyKey("j", "KeyC", true, false, false, false));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyKey("C", "KeyC", true, false, true, false));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyKey("[", "BracketLeft", true, false, false, false));
            Assert.Equal(StatusKeystroke.Submit, AgentStatuses.ClassifyKey("Enter", "Enter", false, false, false, false));
            Assert.Equal(StatusKeystroke.Submit, AgentStatuses.ClassifyKey("Enter", "NumpadEnter", false, false, false, false));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyKey("Enter", "Enter", false, false, true, false));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyKey("Enter", "Enter", false, true, false, false));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyKey("a", "KeyA", false, false, false, false));
        }

        [Fact]
        public void TypedTextSubmitsOnALineBreakAndNeverInterrupts()
        {
            Assert.Equal(StatusKeystroke.Submit, AgentStatuses.ClassifyTyped("ls\r"));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyTyped("\u001b"));
            Assert.Equal(StatusKeystroke.Other, AgentStatuses.ClassifyTyped("\u0003"));
        }

        [Fact]
        public void MarkForAttentionNeverReplacesALiveStatusAndSurvivesAVisit()
        {
            model.SetStatus(a.Id, AgentStatus.Blocked);
            model.MarkForAttention(a.Id);
            model.MarkForAttention(b.Id);

            model.SelectSession(b.Id);

            Assert.Equal(AgentStatus.Blocked, a.Status);
            Assert.Equal(AgentStatus.Completed, b.Status);
        }

        [Fact]
        public void AttentionNavigationSkipsTheCurrentSessionAndWraps()
        {
            model.SetStatus(a.Id, AgentStatus.Blocked);
            model.SetStatus(b.Id, AgentStatus.Active);
            model.SetStatus(c.Id, AgentStatus.Completed);
            model.SelectSession(c.Id);

            Assert.Equal(a.Id, model.FindAttention(true));
            Assert.Equal(a.Id, model.FindAttention(false));
            model.SetStatus(a.Id, AgentStatus.Idle);
            Assert.Null(model.FindAttention(true));
        }

        [Fact]
        public void ABlockIsOwnedByItsPane()
        {
            model.SetSplit(a.Id, true);
            Assert.True(model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Split));

            Assert.False(model.SetStatus(a.Id, AgentStatus.Active, pane: PaneRole.Main));
            Assert.False(model.SetStatus(a.Id, AgentStatus.Idle, pane: PaneRole.Main));
            Assert.True(model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Main));
            Assert.Equal(PaneRole.Main, a.StatusPane);

            Assert.True(model.SetStatus(a.Id, AgentStatus.Active, pane: PaneRole.Main));
            model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Split);
            model.ClearStatusByInput(a.Id, StatusKeystroke.Other, PaneRole.Main);
            Assert.Equal(AgentStatus.Blocked, a.Status);
            model.ClearStatusByInput(a.Id, StatusKeystroke.Other, PaneRole.Split);
            Assert.Equal(AgentStatus.Idle, a.Status);
        }

        [Fact]
        public void SelectingRevealsThePaneThatAsked()
        {
            model.SetSplit(a.Id, true);
            model.FocusPane(a.Id, PaneRole.Main);
            model.SetSplit(a.Id, false);
            model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Split);
            model.SelectSession(b.Id);

            model.SelectSession(a.Id, reveal: true);

            Assert.True(a.SplitShown);
            Assert.Equal(PaneRole.Split, a.FocusedPane);
        }

        [Fact]
        public void ASelectionNotTheUsersOwnLeavesThePanesAlone()
        {
            model.SetSplit(a.Id, true);
            model.FocusPane(a.Id, PaneRole.Main);
            model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Split);
            model.SelectSession(b.Id);

            model.SelectSession(a.Id);
            Assert.Equal(PaneRole.Main, a.FocusedPane);

            model.SelectSession(b.Id);
            Assert.Equal(a.Id, model.AutoFollow(stayOnActive: false));
            Assert.Equal(PaneRole.Main, a.FocusedPane);

            model.SelectSession(a.Id, reveal: true);
            Assert.Equal(PaneRole.Split, a.FocusedPane);
        }

        [Fact]
        public void AHiddenSplitShowsItsFocusedPaneMaximized()
        {
            model.SetSplit(a.Id, true);
            model.FocusPane(a.Id, PaneRole.Main);
            model.SetSplit(a.Id, false);
            Assert.True(a.RendersPane(PaneRole.Main));
            Assert.False(a.RendersPane(PaneRole.Split));

            model.FocusPane(a.Id, PaneRole.Split);

            Assert.False(a.SplitShown);
            Assert.Equal(PaneRole.Split, a.FocusedPane);
            Assert.True(a.RendersPane(PaneRole.Split));
            Assert.False(a.RendersPane(PaneRole.Main));
        }

        [Fact]
        public void AStatusClearedOnVisitStillRevealsItsPane()
        {
            model.SetSplit(a.Id, true);
            model.FocusPane(a.Id, PaneRole.Main);
            model.SetStatus(a.Id, AgentStatus.Blocked, autoReset: true, pane: PaneRole.Split);
            model.SelectSession(b.Id);

            model.SelectSession(a.Id, reveal: true);

            Assert.Equal(AgentStatus.Idle, a.Status);
            Assert.Equal(PaneRole.Split, a.FocusedPane);
        }

        [Fact]
        public void RevealingHidesTheScratchAndFocusesTheLeftPaneThatAsked()
        {
            model.SetSplit(a.Id, true);
            model.FocusPane(a.Id, PaneRole.Split);
            model.SetScratch(a.Id, true);
            model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Main);
            model.SelectSession(a.Id);

            model.RevealStatus(a.Id);

            Assert.False(a.ScratchShown);
            Assert.Equal(PaneRole.Main, a.FocusedPane);
        }

        [Fact]
        public void APaneThatGoesAwayTakesItsStatus()
        {
            model.SetSplit(a.Id, true);
            model.SetStatus(a.Id, AgentStatus.Blocked, pane: PaneRole.Split);
            model.SwapPanes(a.Id);
            Assert.Equal(PaneRole.Main, a.StatusPane);
            model.SwapPanes(a.Id);

            model.CloseSplit(a.Id);
            Assert.Equal(AgentStatus.Idle, a.Status);

            model.SetScratch(b.Id, true);
            model.SetStatus(b.Id, AgentStatus.Completed, pane: PaneRole.Scratch);
            model.ScratchClosed(b.Id);
            Assert.Equal(AgentStatus.Idle, b.Status);
            Assert.False(b.ScratchShown);
        }

        [Fact]
        public void NotificationsCountUntilVisited()
        {
            model.RecordNotification(a.Id, "title", "body", NotificationSources.Control);
            model.RecordNotification(a.Id, "title", "body", NotificationSources.Control);
            Assert.Equal(2, a.UnseenCount);

            model.SelectSession(a.Id);
            Assert.Equal(0, a.UnseenCount);
        }

        [Fact]
        public void EventsCarryTheTransitionAndPageByCursor()
        {
            (ControlEventBatch anchor, _) = model.Events.Read(null, null, null, 100);
            model.SetStatus(a.Id, AgentStatus.Active);
            model.SetStatus(a.Id, AgentStatus.Active);
            model.SetStatus(a.Id, AgentStatus.Blocked);
            model.RemoveSession(b.Id);

            (ControlEventBatch statuses, string? error) = model.Events.Read(anchor.Run, anchor.Next, new HashSet<string> { ControlEventKinds.Status }, 100);

            Assert.Null(error);
            Assert.Equal(new[] { "active", "blocked" }, statuses.Items.Select(item => item.Payload.Status));
            Assert.Equal(new[] { "idle", "active" }, statuses.Items.Select(item => item.Payload.Previous));

            (ControlEventBatch all, _) = model.Events.Read(anchor.Run, anchor.Next, null, 100);
            Assert.Equal(ControlEventKinds.SessionClosed, all.Items[^1].Kind);
            Assert.Equal("event run changed", model.Events.Read("other", 0, null, 100).Error);
            Assert.Empty(model.Events.Read(all.Run, all.Next, null, 100).Batch.Items);
        }

        [Fact]
        public void AnIdleWriteKeepsTheIndicatorAsWrittenAndARepeatGivesNoEvent()
        {
            (ControlEventBatch anchor, _) = model.Events.Read(null, null, null, 100);
            model.SetStatus(a.Id, AgentStatus.Idle, blink: true, color: "#FF0000", shape: "star");
            model.SetStatus(a.Id, AgentStatus.Idle, blink: true, color: "#FF0000", shape: "star");

            (ControlEventBatch statuses, _) = model.Events.Read(anchor.Run, anchor.Next, new HashSet<string> { ControlEventKinds.Status }, 100);
            ControlEvent item = Assert.Single(statuses.Items);
            Assert.True(item.Payload.Blink);
            Assert.Equal("#FF0000", item.Payload.Color);
            Assert.Equal("star", item.Payload.Shape);
            Assert.False(a.StatusBlink);
            Assert.Null(a.StatusColor);
            Assert.Null(a.StatusShape);
        }

        [Fact]
        public void SoundThrottleHoldsTheSameSoundFromItsLastPlay()
        {
            SoundThrottle throttle = new SoundThrottle(TimeSpan.FromMilliseconds(200));

            Assert.True(throttle.Allow("Ring01", TimeSpan.FromMilliseconds(1000)));
            Assert.False(throttle.Allow("Ring01", TimeSpan.FromMilliseconds(1100)));
            Assert.True(throttle.Allow("Alarm01", TimeSpan.FromMilliseconds(1100)));
            Assert.False(throttle.Allow("Ring01", TimeSpan.FromMilliseconds(1199)));
            Assert.True(throttle.Allow("Ring01", TimeSpan.FromMilliseconds(1200)));
        }

        [Fact]
        public void SessionSeenLeavesTheDeliveredBannersAndAVisitTakesThem()
        {
            List<Guid> seen = new List<Guid>();
            model.Seen += seen.Add;
            model.RecordNotification(a.Id, "t", "b", NotificationSources.Control);

            model.ClearUnseen(a.Id, clearDelivered: false);
            Assert.Equal(0, a.UnseenCount);
            Assert.Empty(seen);

            model.ClearUnseen(a.Id);
            Assert.Equal(new[] { a.Id }, seen);
        }
    }
}
