using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class RemotePresentationTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Session origin;
        private readonly Session row;

        /// <summary>The identities of the other machine's panes the row shows, as its daemons are named.</summary>
        private static readonly string Left = Guid.NewGuid().Wire();
        private static readonly string Right = Guid.NewGuid().Wire();

        public RemotePresentationTests()
        {
            Workspace work = model.AddWorkspace("work");
            origin = model.AddSession(work.Id, "cmd", "C:\\a", name: "a");
            row = model.AddRemoteSession(work.Id, "box", Guid.NewGuid(), "far", split: true, mainDaemon: Left, splitDaemon: Right);
        }

        /// <summary>agterm's layout: the primary first, the split's pane after it.</summary>
        private static ControlPresentationLayout Layout(string primary, string? split = null, bool shown = true, string axis = "vertical")
        {
            return new ControlPresentationLayout
            {
                Panes = split is null ? new List<string> { primary } : new List<string> { primary, split },
                Primary = primary,
                Axis = split is null ? null : axis,
                Shown = split is not null && shown,
            };
        }

        [Fact]
        public void ThePresentationCarriesStatusContextAndTheHudCountdown()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.SetStatus(origin.Id, AgentStatus.Blocked, pane: PaneRole.Main, color: "#ff0000");
            model.SetContext(origin.Id, "PR 7");
            model.OpenOverlay(origin.Id, new SessionOverlay { Id = "h", Hud = new HudSpec { Message = "build", HideAfter = 20 }, ShownAt = now.AddSeconds(-5) });

            ControlPresentation presentation = model.Presentation(origin.Id, now);

            Assert.Equal("blocked", presentation.Status!.Status);
            Assert.Equal("#ff0000", presentation.Status.Color);
            Assert.Equal(origin.PaneIds[PaneRole.Main], presentation.Status.Pane);
            Assert.Equal("PR 7", presentation.Context);
            Assert.Equal("build", presentation.Hud!.Message);
            Assert.Null(presentation.Hud.Pane);
            Assert.Equal(15, presentation.Hud.Remaining!.Value, 3);
        }

        [Fact]
        public void AnIdleSessionPresentsNoStatus()
        {
            Assert.Null(model.Presentation(origin.Id, DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public void TheRowTakesOnWhatTheOtherMachineSaysAndLosesItWithTheStream()
        {
            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "active", Blink = true, Pane = Right, ChangedAt = 1000 }, snapshot: false);
            model.ApplyRemoteContext(row.Id, "task");
            SessionOverlay? hud = model.ApplyRemoteHud(row.Id, new ControlPresentationHud { Id = "h", Message = "hi", Pane = Right, Remaining = 3 });

            Assert.Equal(AgentStatus.Active, row.Status);
            Assert.Equal(PaneRole.Split, row.StatusPane);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), row.StatusChangedAt);
            Assert.Equal("task", row.EffectiveContext);
            Assert.Equal(3, hud!.Hud!.HideAfter);
            Assert.Same(hud, row.Overlay);
            Assert.Equal(PaneRole.Split, hud.HudPane);

            model.ClearRemotePresentation(row.Id);

            Assert.Equal(AgentStatus.Idle, row.Status);
            Assert.Null(row.EffectiveContext);
            Assert.Empty(row.Overlays);
        }

        [Fact]
        public void ASnapshotDoesNotEndAStatusSetOnThisMachine()
        {
            model.SetStatus(row.Id, AgentStatus.Completed, force: true);

            model.ApplyRemoteStatus(row.Id, null, snapshot: true);
            Assert.Equal(AgentStatus.Completed, row.Status);

            model.ApplyRemoteStatus(row.Id, null, snapshot: false);
            Assert.Equal(AgentStatus.Idle, row.Status);
        }

        [Fact]
        public void AHudOfThisMachineIsLeftAlone()
        {
            model.OpenOverlay(row.Id, new SessionOverlay { Id = "mine", Hud = new HudSpec { Message = "local" } });

            Assert.Null(model.ApplyRemoteHud(row.Id, new ControlPresentationHud { Id = "theirs", Message = "remote" }));
            model.ClearRemotePresentation(row.Id);

            Assert.Equal("mine", row.Overlay!.Id);
        }

        private static AskRequest Ask(Guid sessionId, PaneRole? pane = null)
        {
            return new AskRequest
            {
                Id = Guid.NewGuid().ToString(),
                Title = "go?",
                Buttons = new[] { new ControlAskButton { Id = "yes", Label = "Yes" } },
                Style = "terminal",
                Align = "right",
                SessionId = sessionId,
                Pane = pane,
            };
        }

        [Fact]
        public async Task WithAPresenterAQuestionOverAMissingSplitIsRefusedForTheSplit()
        {
            ControlDispatcher dispatcher = new ControlDispatcher(model, new DashboardTests.NullHost());
            ControlRequest ask = new ControlRequest { Cmd = "ask.open", Target = origin.Id.Wire(), Args = new ControlArgs { Title = "Q", Buttons = new List<ControlAskButton> { new ControlAskButton { Id = "ok", Label = "OK" } }, Style = "terminal", Pane = "right" } };
            Assert.Equal("pane not visible", (await dispatcher.HandleAsync(ask)).Error);

            model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a");
            Assert.Equal("session has no split", (await dispatcher.HandleAsync(ask)).Error);
        }

        [Fact]
        public void APresenterGetsWhatItsHelloNamed()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.True(model.Presentation(origin.Id, now, "a").Presenter);
            Assert.False(model.PresenterShowsPages(origin.Id));
            Assert.False(model.PresenterSupports(origin.Id, ControlPresentationKinds.AskForm));

            model.SetStreamKinds("a", ControlPresentationKinds.SupportedKinds);
            Assert.True(model.PresenterShowsPages(origin.Id));
            Assert.True(model.PresenterSupports(origin.Id, ControlPresentationKinds.AskOther));

            model.ReleasePresenter(origin.Id, "a");
            Assert.False(model.PresenterSupports(origin.Id, ControlPresentationKinds.AskForm));
        }

        [Fact]
        public void AStreamOnAnOpenConnectionNeverGoesQuietAndLeavesWhenReleased()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.HoldPresentationStream("a");
            Assert.True(model.Presentation(origin.Id, now, "a").Presenter);
            int changes = 0;
            model.Changed += () => changes++;

            model.ExpirePresenters(now + TimeSpan.FromMinutes(5));
            Assert.True(model.HasPresenter(origin.Id));
            Assert.Equal(new ControlPresentersNode { Mirrors = 0, Presenter = true }, model.PresentersOf(origin.Id, now + TimeSpan.FromMinutes(5)));

            model.ReleasePresenter(origin.Id, "a");
            Assert.False(model.HasPresenter(origin.Id));
            Assert.True(changes > 0);
        }

        [Fact]
        public void TheFirstStreamToAskPresentsUntilItGoesQuiet()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            Assert.True(model.Presentation(origin.Id, now, "a").Presenter);
            Assert.Null(model.Presentation(origin.Id, now, "b").Presenter);
            Assert.Equal(1, model.PresenterGeneration(origin.Id));

            model.ExpirePresenters(now + AppModel.PresenterTimeout + TimeSpan.FromSeconds(1));
            Assert.False(model.HasPresenter(origin.Id));

            // A mirror asks again only when its stream reconnects, not on its ordinary reads.
            Assert.Null(model.Presentation(origin.Id, now, "b", acquire: false).Presenter);
            Assert.True(model.Presentation(origin.Id, now, "b", acquire: true).Presenter);
            Assert.True(model.Presentation(origin.Id, now, "b", acquire: false).Presenter);
            Assert.Equal(3, model.PresenterGeneration(origin.Id));
        }

        [Fact]
        public void AQuestionGoesToThePresenterOnlyWhereThisMachineFollows()
        {
            model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a");
            AskRequest leading = Ask(origin.Id);
            model.OpenAsk(leading);

            Assert.False(model.HandAskOver(leading, pane => "leader"));
            Assert.Null(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Ask);

            model.CancelAsk(leading.Id);
            AskRequest following = Ask(origin.Id);
            model.OpenAsk(following);

            Assert.True(model.HandAskOver(following, pane => "follower"));
            ControlPresentationAsk handed = model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Ask!;
            Assert.Equal(following.Id, handed.Id);
            Assert.Null(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "b").Ask);
        }

        [Fact]
        public void ThePresentersAnswerResolvesTheQuestionAndAnotherStreamsDoesNot()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            AskRequest ask = Ask(origin.Id);
            model.OpenAsk(ask);
            model.HandAskOver(ask, pane => "follower");
            int owner = model.PresenterGeneration(origin.Id);

            Assert.False(model.AnswerRemoteAsk(origin.Id, "b", new ControlPresentationFrame { Kind = ControlPresentationKinds.AskResolve, Id = ask.Id, Owner = owner, Button = "yes" }));
            Assert.False(model.AnswerRemoteAsk(origin.Id, "a", new ControlPresentationFrame { Kind = ControlPresentationKinds.AskResolve, Id = ask.Id, Owner = owner + 1, Button = "yes" }));
            Assert.True(model.AnswerRemoteAsk(origin.Id, "a", new ControlPresentationFrame { Kind = ControlPresentationKinds.AskResolve, Id = ask.Id, Owner = owner, Button = "yes" }));

            Assert.Equal("yes", model.AskResult(ask.Id)!.Id);
        }

        [Fact]
        public void ARefusedOrLostQuestionComesBackHere()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            AskRequest refused = Ask(origin.Id);
            model.OpenAsk(refused);
            model.HandAskOver(refused, pane => "follower");

            Assert.True(model.AnswerRemoteAsk(origin.Id, "a", new ControlPresentationFrame { Kind = ControlPresentationKinds.AskRejected, Id = refused.Id, Owner = model.PresenterGeneration(origin.Id) }));
            Assert.Null(refused.RemoteOwner);
            Assert.Equal("pending", model.AskResult(refused.Id)!.Result);

            model.CancelAsk(refused.Id);
            AskRequest lost = Ask(origin.Id);
            model.OpenAsk(lost);
            model.HandAskOver(lost, pane => "follower");
            model.ExpirePresenters(now + AppModel.PresenterTimeout + TimeSpan.FromSeconds(1));

            Assert.Null(lost.RemoteOwner);
        }

        [Fact]
        public void AHandedQuestionIsDrawnOnTheRowUnderItsOwnId()
        {
            ControlPresentationAsk ask = new ControlPresentationAsk { Id = "remote", Owner = 2, Title = "go?", Buttons = new List<ControlAskButton> { new ControlAskButton { Id = "yes", Label = "Yes" } }, Pane = Right };

            (AskRequest? replica, string? error) = model.OpenReplicaAsk(row.Id, ask);

            Assert.Null(error);
            Assert.NotEqual("remote", replica!.Id);
            Assert.Equal(PaneRole.Split, replica.Pane);
            Assert.Same(replica, model.TerminalAskOf(row.Id));
            Assert.Equal("ask already pending", model.OpenReplicaAsk(row.Id, ask).Error);
        }

        private SessionOverlay Program(string command = "build")
        {
            return new SessionOverlay { Id = Guid.NewGuid().ToString(), Command = command, Wait = true };
        }

        [Fact]
        public void AnOverlayGoesToThePresenterOnlyWhereThisMachineFollows()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");

            Assert.False(model.HandOverlayOver(origin.Id, Program(), PaneRole.Overlay, pane => "leader", now));
            SessionOverlay handed = Program();
            Assert.True(model.HandOverlayOver(origin.Id, handed, PaneRole.Overlay, pane => "follower", now));

            Assert.Empty(origin.Overlays);
            Assert.True(origin.LastOverlay(PaneRole.Overlay)!.Remote);
            ControlPresentationOverlay shown = model.Presentation(origin.Id, now, "a").Overlays!.Single();
            Assert.Equal(handed.Id, shown.Job);
            Assert.True(shown.Wait);
        }

        [Fact]
        public void AJobRunsOnceAndReportsItsExit()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, overlay.Id)!;

            Assert.True(model.ClaimOverlayJob(job, now));
            Assert.False(model.ClaimOverlayJob(job, now));
            model.OverlayJobStarted(job, now);
            Assert.Equal(OverlayJob.Running, job.State);
            Assert.False(AppModel.OverlayJobStopping(job));
            model.FinishOverlayJob(job, 7, null);
            model.FinishOverlayJob(job, null, "unknown");

            Assert.Equal(7, origin.LastOverlay(PaneRole.Overlay)!.ExitCode);
            Assert.Null(origin.LastOverlay(PaneRole.Overlay)!.Failure);
        }

        [Fact]
        public void AJobNobodyClaimsFailsAndOneWhoseHelperIsGoneIsUnknown()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay unclaimed = Program();
            model.HandOverlayOver(origin.Id, unclaimed, PaneRole.Overlay, pane => "follower", now);
            model.Presentation(origin.Id, now + AppModel.JobLaunchWindow, "a");
            model.ExpireOverlayJobs(now + AppModel.JobLaunchWindow);
            Assert.Equal("launch-failed", origin.LastOverlay(PaneRole.Overlay)!.Failure);

            SessionOverlay quiet = Program();
            model.HandOverlayOver(origin.Id, quiet, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, quiet.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            // agterm: once running, no timeout ends the job; its helper's connection closing does.
            model.ExpireOverlayJobs(now + TimeSpan.FromHours(1));
            Assert.Equal(OverlayJob.Running, job.State);
            model.OverlayJobHelperGone(job);
            Assert.Equal("unknown", origin.LastOverlay(PaneRole.Overlay)!.Failure);

            SessionOverlay late = Program();
            model.HandOverlayOver(origin.Id, late, PaneRole.Overlay, pane => "follower", now);
            OverlayJob slow = model.OverlayJobOf(origin.Id, late.Id)!;
            model.ClaimOverlayJob(slow, now);
            model.OverlayJobStarted(slow, now + AppModel.JobStartWindow);
            Assert.Equal("unknown", origin.LastOverlay(PaneRole.Overlay)!.Failure);
        }

        [Fact]
        public void ClosingAJobCancelsItThroughItsHelper()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay waiting = Program();
            model.HandOverlayOver(origin.Id, waiting, PaneRole.Overlay, pane => "follower", now);
            Assert.True(model.CancelOverlayJob(origin.Id, PaneRole.Overlay));
            Assert.Equal("canceled", origin.LastOverlay(PaneRole.Overlay)!.Failure);

            SessionOverlay running = Program();
            model.HandOverlayOver(origin.Id, running, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, running.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            Assert.True(model.CancelOverlayJob(origin.Id, PaneRole.Overlay));
            Assert.True(AppModel.OverlayJobStopping(job));
            Assert.Null(origin.LastOverlay(PaneRole.Overlay)!.Failure);
        }

        [Fact]
        public void APresenterThatCannotShowTheOverlayFailsIt()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            ControlPresentationFrame refusal = new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayRejected, Id = overlay.Id, Owner = model.PresenterGeneration(origin.Id) };

            Assert.False(model.RejectOverlayJob(origin.Id, "b", refusal));
            Assert.True(model.RejectOverlayJob(origin.Id, "a", refusal));
            Assert.Equal("launch-failed", origin.LastOverlay(PaneRole.Overlay)!.Failure);
        }

        [Fact]
        public void AStatusSoundGoesToThePresenterOnce()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.False(model.SendSoundToPresenter(origin.Id, "Alarm01"));

            model.Presentation(origin.Id, now, "a");
            // As agterm's sendToPresenter: a presenter whose hello did not list sound is not handed one; it plays here.
            Assert.False(model.SendSoundToPresenter(origin.Id, null));
            model.SetStreamKinds("a", new[] { ControlPresentationKinds.Sound });
            Assert.True(model.SendSoundToPresenter(origin.Id, null));

            Assert.Null(model.Presentation(origin.Id, now, "b").Sounds);
            Assert.Null(model.Presentation(origin.Id, now, "a").Sounds!.Single().Name);
            Assert.Null(model.Presentation(origin.Id, now, "a").Sounds);
        }

        [Fact]
        public void AMirroredHudWhoseTimeRanOutComesDownEvenUnderTheSameId()
        {
            Assert.NotNull(model.ApplyRemoteHud(row.Id, new ControlPresentationHud { Id = "h", Message = "hi", Remaining = 3 }));
            Assert.Null(model.ApplyRemoteHud(row.Id, new ControlPresentationHud { Id = "h", Message = "hi", Remaining = 0 }));
            Assert.Null(row.Overlay);
        }

        [Fact]
        public void AContextSetHereWinsOverTheMirroredOne()
        {
            model.ApplyRemoteContext(row.Id, "theirs");
            Assert.Equal("theirs", row.EffectiveContext);

            model.SetContext(row.Id, "mine");
            model.ApplyRemoteContext(row.Id, "theirs again");
            Assert.Equal("mine", row.EffectiveContext);

            model.SetContext(row.Id, null);
            Assert.Equal("theirs again", row.EffectiveContext);
        }

        [Fact]
        public void ARemoteRowOpensAndClosesWithItsHost()
        {
            Session other = model.AddRemoteSession(model.Workspaces[0].Id, "box", Guid.NewGuid(), "x", split: false);
            model.RemoveSession(other.Id);

            List<ControlEvent> events = model.Events.Read(model.Events.Run, 0, new HashSet<string> { ControlEventKinds.RemoteOpened, ControlEventKinds.RemoteClosed }, 100).Batch.Items;
            Assert.Equal(new[] { "remote.opened", "remote.opened", "remote.closed" }, events.Select(item => item.Kind));
            Assert.All(events, item => Assert.Equal("box", item.Payload.Host));
        }

        [Fact]
        public void TheRowFollowsTheSplitOfItsMachineButNeverOpensOne()
        {
            Assert.Equal(RemoteLayoutChange.None, model.ApplyRemoteLayout(row.Id, Layout(Left, Right, shown: false, axis: "horizontal")));
            Assert.False(row.SplitShown);
            Assert.Equal(SplitAxis.Horizontal, row.SplitAxis);

            // Swapped there: the primary is the pane here's split, which the window then puts in the main place.
            Assert.Equal(RemoteLayoutChange.None, model.ApplyRemoteLayout(row.Id, Layout(Right, Left, axis: "horizontal")));
            Assert.True(row.SplitShown);
            Assert.True(row.RemotePanesSwapped);
            Assert.True(model.RestoreRemoteOrder(row.Id));
            Assert.Equal(Right, AppModel.RemoteDaemonOf(row, PaneRole.Main));

            // The window takes the pane down: the model only says so.
            Assert.Equal(RemoteLayoutChange.SplitRemoved, model.ApplyRemoteLayout(row.Id, Layout(Right)));
            Assert.True(row.HasSplit);
            model.CloseSplit(row.Id);
            Assert.False(row.HasSplit);

            model.ApplyRemoteLayout(row.Id, Layout(Right, Left));
            Assert.False(row.HasSplit);
        }

        [Fact]
        public void TheOriginReportsItsPanesByIdentityThePrimaryFirst()
        {
            model.SetSplit(origin.Id, true, SplitAxis.Vertical);
            string main = origin.PaneIds[PaneRole.Main];
            string split = origin.PaneIds[PaneRole.Split];
            model.SwapPanes(origin.Id);

            ControlPresentationLayout layout = model.Presentation(origin.Id, DateTimeOffset.UtcNow).Layout!;
            Assert.True(layout.IsValid && layout.Shown);
            Assert.Equal(new[] { split, main }, layout.Panes);
            Assert.Equal(split, layout.Primary);
            Assert.Equal("vertical", layout.Axis);
        }

        [Fact]
        public void AGuiQuestionThatComesBackOffScreenEndsPresentationLost()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            AskRequest ask = new AskRequest
            {
                Id = "gui",
                Title = "go?",
                Buttons = new[] { new ControlAskButton { Id = "yes", Label = "Yes" } },
                Style = "gui",
                Align = "right",
                SessionId = origin.Id,
            };
            model.OpenAsk(ask);
            model.HandAskOver(ask, pane => "follower");
            model.SelectSession(row.Id);

            model.ExpirePresenters(now + AppModel.PresenterTimeout + TimeSpan.FromSeconds(1));

            ControlAskResult result = model.AskResult("gui")!;
            Assert.Equal("cancelled", result.Result);
            Assert.Equal("presentation-lost", result.Reason);
        }

        [Fact]
        public void ASlotWhoseOverlayRunsElsewhereIsReserved()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.HandOverlayOver(origin.Id, Program(), PaneRole.Overlay, pane => "follower", now);

            Assert.True(model.OverlaySlotReserved(origin.Id, PaneRole.Overlay));
            model.CancelOverlayJob(origin.Id, PaneRole.Overlay);
            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.Overlay));
        }

        [Fact]
        public void ASplitOpenedOnTheRowLaterIsLocal()
        {
            Assert.True(AppModel.IsRemotePane(row, PaneRole.Split));
            model.CloseSplit(row.Id);
            Assert.False(AppModel.IsRemotePane(row, PaneRole.Split));

            model.SetSplit(row.Id, true);
            Assert.True(row.HasSplit);
            Assert.False(AppModel.IsRemotePane(row, PaneRole.Split));
        }

        [Fact]
        public void ARemoteRowsPanesSwapHereAndAMixedRowFollowsItsPaneOfThatMachine()
        {
            // Both panes are the other machine's: a local layout change, as agterm's viewer swap.
            Assert.True(model.SwapPanes(row.Id));
            Assert.True(model.SwapPanes(row.Id));

            // As agterm's, a pane of that machine beside one of this machine's swaps too, and stays the one it stands for.
            model.CloseSplit(row.Id);
            model.SetSplit(row.Id, true);
            Assert.True(model.SwapPanes(row.Id));
            Assert.False(AppModel.IsRemotePane(row, PaneRole.Main));
            Assert.True(AppModel.IsRemotePane(row, PaneRole.Split));
            Assert.Equal(PaneRole.Split, AppModel.LocalPaneOf(row, PaneRole.Main));
            Assert.Equal(RemoteLayoutChange.None, model.ApplyRemoteLayout(row.Id, Layout(Left)));
            Assert.True(model.SwapPanes(row.Id));
            Assert.True(AppModel.IsRemotePane(row, PaneRole.Main));
            Assert.False(row.RemotePanesSwapped);
        }

        [Fact]
        public void AfterAMixedSwapTheOtherMachinesLeftStillOwnsItsStatusAndRightHasNoPane()
        {
            model.CloseSplit(row.Id);
            model.SetSplit(row.Id, true);
            model.SwapPanes(row.Id);

            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "blocked", Pane = Left }, snapshot: false);
            Assert.Equal(PaneRole.Split, row.StatusPane);
            Assert.False(row.RemoteStatusUnowned);
            Assert.Equal(PaneRole.OverlayRight, AppModel.LocalOverlaySlot(row, Left));
            Assert.Null(AppModel.LocalOverlaySlot(row, Right));

            model.PromoteSplit(row.Id);
            Assert.True(AppModel.IsRemotePane(row, PaneRole.Main));
        }

        [Fact]
        public void ASwapOfTwoRemotePanesMadeHereGivesWayToTheirMachinesOrder()
        {
            Assert.True(model.SwapPanes(row.Id));
            Assert.True(model.RestoreRemoteOrder(row.Id));
            Assert.False(row.RemotePanesSwapped);
            Assert.False(model.RestoreRemoteOrder(row.Id));
        }

        [Fact]
        public void ARestartedPaneLosesWhatItOwnedAndAClosedSplitItsResult()
        {
            model.SetSplit(origin.Id, true);
            model.OpenOverlay(origin.Id, new SessionOverlay { Id = "hud", Hud = new HudSpec { Message = "m" }, HudPane = PaneRole.Split });
            model.OpenOverlay(origin.Id, new SessionOverlay { Id = "right", Command = "htop" }, PaneRole.OverlayRight);
            model.OverlayExited(origin.Id, "right", 4, PaneRole.OverlayRight);

            model.ClearPaneOwnedState(origin.Id, PaneRole.Main);
            Assert.True(origin.Overlay!.IsHud);

            model.ClearPaneOwnedState(origin.Id, PaneRole.Split);
            Assert.Null(origin.Overlay);
            Assert.Equal(4, origin.LastOverlay(PaneRole.OverlayRight)!.ExitCode);

            model.CloseSplit(origin.Id);
            Assert.Null(origin.LastOverlay(PaneRole.OverlayRight));
        }

        [Fact]
        public void APaneStaysWithItsShellWhenThePanesSwapHereAndTheMainEndsThere()
        {
            model.SwapPanes(row.Id);
            // The other machine's main pane, shown here in the split, ended there: the split closes, the main pane here
            // still shows that machine's split.
            model.CloseSplit(row.Id);
            Assert.True(AppModel.IsRemotePane(row, PaneRole.Main));
            Assert.Equal(PaneRole.Split, AppModel.RemoteRoleOf(row, PaneRole.Main));
            Assert.True(AppModel.HasRemotePanes(row));
        }

        [Fact]
        public void ASwapThereRebindsThePaneHereByItsDaemonSoItsLeftNoLongerOwnsIt()
        {
            Session named = model.AddRemoteSession(model.Workspaces[0].Id, "box", Guid.NewGuid(), "named", split: true, mainDaemon: "a", splitDaemon: "b");
            model.CloseSplit(named.Id);
            model.SetSplit(named.Id, true);
            model.ApplyRemoteLayout(named.Id, Layout("b", "a"));

            Assert.Equal(PaneRole.Split, AppModel.RemoteRoleOf(named, PaneRole.Main));
            // b, now that machine's left, has no pane here: a split of this machine took its place.
            model.ApplyRemoteStatus(named.Id, new ControlPresentationStatus { Status = "blocked", Pane = "b" }, snapshot: false);
            Assert.True(named.RemoteStatusUnowned);
            model.ApplyRemoteStatus(named.Id, new ControlPresentationStatus { Status = "blocked", Pane = "a" }, snapshot: false);
            Assert.False(named.RemoteStatusUnowned);
            Assert.Equal(PaneRole.Main, named.StatusPane);
        }

        [Fact]
        public void BothPanesSwappedThereChangePlacesHereWithTheirDaemons()
        {
            Session named = model.AddRemoteSession(model.Workspaces[0].Id, "box", Guid.NewGuid(), "named", split: true, mainDaemon: "a", splitDaemon: "b");
            model.ApplyRemoteLayout(named.Id, Layout("a", "b"));
            Assert.False(named.RemotePanesSwapped);

            Assert.Equal(RemoteLayoutChange.None, model.ApplyRemoteLayout(named.Id, Layout("b", "a")));
            Assert.True(model.RestoreRemoteOrder(named.Id));
            Assert.Equal("b", AppModel.RemoteDaemonOf(named, PaneRole.Main));
            Assert.Equal("a", AppModel.RemoteDaemonOf(named, PaneRole.Split));
            Assert.Equal(PaneRole.Main, AppModel.RemoteRoleOf(named, PaneRole.Main));
            Assert.False(named.RemotePanesSwapped);
        }

        [Fact]
        public void ALayoutNamingItsPanesTellsWhichPaneWent()
        {
            model.ApplyRemoteLayout(row.Id, Layout(Left, Right));

            // The main pane went there and the split took its place: here the main pane is the one gone.
            Assert.Equal(new[] { PaneRole.Main }, AppModel.RemotePanesGone(row, Layout(Right)));
            Assert.Equal(new[] { PaneRole.Split }, AppModel.RemotePanesGone(row, Layout(Left)));

            // Swapped here, the pane showing that machine's split is the main one, and its id goes with it.
            model.SwapPanes(row.Id);
            Assert.Equal(new[] { PaneRole.Main }, AppModel.RemotePanesGone(row, Layout(Left)));
            Assert.Empty(AppModel.RemotePanesGone(row, Layout(Right, Left)));

            // From the session's own machine the layout names its panes.
            model.SetSplit(origin.Id, true);
            ControlPresentationLayout sent = model.Presentation(origin.Id, DateTimeOffset.UtcNow).Layout!;
            Assert.Equal(origin.PaneIds[PaneRole.Main], sent.MainId);
            Assert.Equal(origin.PaneIds[PaneRole.Split], sent.SplitId);
        }

        [Fact]
        public async Task ASessionWhosePanesAreNotLiveIsNotPresented()
        {
            ControlResponse response = await new ControlDispatcher(model, new DashboardTests.NullHost()).HandleAsync(new ControlRequest { Cmd = "live.presentation", Target = origin.Id.Wire() });
            Assert.Equal("session is not live-backed, so nothing can be attached to it", response.Error);
        }

        [Fact]
        public void AStatusFromAPaneWithNoCounterpartHereIsNotClearedByKeys()
        {
            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "blocked", Pane = "scratch" }, snapshot: false);
            Assert.True(row.RemoteStatusUnowned);
            model.ClearStatusByInput(row.Id, StatusKeystroke.Submit, PaneRole.Main);
            Assert.Equal(AgentStatus.Blocked, row.Status);

            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "blocked", Pane = Left }, snapshot: false);
            Assert.False(row.RemoteStatusUnowned);
            model.ClearStatusByInput(row.Id, StatusKeystroke.Submit, PaneRole.Main);
            Assert.Equal(AgentStatus.Idle, row.Status);
        }

        [Fact]
        public void ASwapThereIsReadFromThePrimaryAndTwoSwapsAreNone()
        {
            model.ApplyRemoteLayout(row.Id, Layout(Left, Right));
            Assert.False(model.RestoreRemoteOrder(row.Id));
            model.ApplyRemoteLayout(row.Id, Layout(Right, Left));
            model.ApplyRemoteLayout(row.Id, Layout(Left, Right));
            Assert.False(model.RestoreRemoteOrder(row.Id));
            model.ApplyRemoteLayout(row.Id, Layout(Right, Left));
            Assert.True(model.RestoreRemoteOrder(row.Id));
        }

        [Fact]
        public void ASplitClosedThereAfterASwapHereTakesTheMainPaneAndTheOtherStaysRemote()
        {
            Assert.True(model.SwapPanes(row.Id));
            Assert.True(row.RemotePanesSwapped);
            Assert.Equal(RemoteLayoutChange.SplitRemoved, model.ApplyRemoteLayout(row.Id, Layout(Left)));

            model.PromoteSplit(row.Id);

            Assert.False(row.HasSplit);
            Assert.False(row.RemotePanesSwapped);
            Assert.True(AppModel.IsRemotePane(row, PaneRole.Main));
            Assert.Equal(PaneRole.Main, AppModel.LocalPaneOf(row, PaneRole.Main));
        }

        [Fact]
        public void ARowWhoseMainPaneWentThereKeepsItsHostWithALocalPaneInItsPlace()
        {
            model.CloseSplit(row.Id);
            model.SetSplit(row.Id, true, SplitAxis.Vertical);

            model.PromoteSplit(row.Id);

            Assert.Equal("box", row.RemoteHost);
            Assert.False(AppModel.IsRemotePane(row, PaneRole.Main));
            Assert.False(AppModel.HasRemotePanes(row));
            model.SetSplit(row.Id, true, SplitAxis.Vertical);
            Assert.True(model.SwapPanes(row.Id));
            Assert.False(row.RemotePanesSwapped);
        }

        private OverlayJob RunningJob(PaneRole slot, DateTimeOffset now)
        {
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, slot, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, overlay.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            return job;
        }

        [Fact]
        public async Task ARestartedPaneCancelsItsJobButHoldsTheSlotUntilTheHelperReports()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SetSplit(origin.Id, true);
            OverlayJob job = RunningJob(PaneRole.OverlayRight, now);
            ControlDispatcher dispatcher = new ControlDispatcher(model, new DashboardTests.NullHost());
            ControlRequest read = new ControlRequest { Cmd = "session.overlay.result", Target = origin.Id.Wire(), Args = new ControlArgs { Pane = "right" } };
            ControlResponse running = await dispatcher.HandleAsync(read);
            Assert.Equal("overlay still running", running.Error);

            model.EndOverlayJobsOfPane(origin.Id, PaneRole.Split);

            Assert.True(model.OverlaySlotReserved(origin.Id, PaneRole.OverlayRight));
            Assert.True(AppModel.OverlayJobStopping(job));
            model.FinishOverlayJob(job, 9, null);
            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.OverlayRight));
            Assert.Equal(9, (await dispatcher.HandleAsync(read)).Result!.ExitCode);
        }

        [Fact]
        public void AClosedSplitFreesItsJobsSlotAtOnceAndDropsALateResult()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SetSplit(origin.Id, true);
            OverlayJob job = RunningJob(PaneRole.OverlayRight, now);

            model.CloseSplit(origin.Id);

            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.OverlayRight));
            Assert.Null(model.RemoteOverlaysOf(origin.Id));
            Assert.True(AppModel.OverlayJobStopping(job));
            Assert.Equal("canceled", model.Presentation(origin.Id, now, "a").Overlays!.Single().Failure);
            model.FinishOverlayJob(job, 0, null);
            Assert.NotEqual(0, origin.LastOverlay(PaneRole.OverlayRight)?.ExitCode);
        }

        [Fact]
        public void APromotedSplitDropsTheMainPanesJobBeforeItsOwnMovesLeft()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SetSplit(origin.Id, true);
            OverlayJob left = RunningJob(PaneRole.OverlayLeft, now);
            OverlayJob right = RunningJob(PaneRole.OverlayRight, now);

            model.PromoteSplit(origin.Id);

            Assert.True(AppModel.OverlayJobStopping(left));
            Assert.Equal(PaneRole.OverlayLeft, right.Slot);
            Assert.Single(model.RemoteOverlaysOf(origin.Id)!);
            Assert.True(model.CancelOverlayJob(origin.Id, PaneRole.OverlayLeft));
            Assert.True(right.CancelRequested);
        }

        [Fact]
        public async Task AJobOverAHiddenSplitIsStillReadAndClosed()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SetSplit(origin.Id, true);
            OverlayJob job = RunningJob(PaneRole.OverlayRight, now);
            model.SetSplit(origin.Id, false);
            ControlDispatcher dispatcher = new ControlDispatcher(model, new DashboardTests.NullHost());

            Assert.Equal("overlay still running", (await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.overlay.result", Target = origin.Id.Wire(), Args = new ControlArgs { Pane = "right" } })).Error);
            Assert.True((await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.overlay.close", Target = origin.Id.Wire(), Args = new ControlArgs { Pane = "right" } })).Ok);
            Assert.True(job.CancelRequested);
        }

        [Fact]
        public async Task AHudOverTheCapIsRefusedAsInAgterm()
        {
            ControlDispatcher dispatcher = new ControlDispatcher(model, new DashboardTests.NullHost());
            ControlResponse plain = await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.hud.open", Target = origin.Id.Wire(), Args = new ControlArgs { Message = new string('x', 257) } });
            Assert.Equal("hud message too long (max 256 characters)", plain.Error);
            ControlResponse markdown = await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.hud.open", Target = origin.Id.Wire(), Args = new ControlArgs { Message = new string('x', 257), Markdown = true } });
            Assert.True(markdown.Ok);
            ControlResponse detail = await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.hud.open", Target = origin.Id.Wire(), Args = new ControlArgs { Message = "m", Detail = new string('x', 257) } });
            Assert.Equal("hud detail too long (max 256 characters)", detail.Error);
        }

        [Fact]
        public async Task ARemoteJobsResultReadsUnderAHudOpenedWhileItRan()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, overlay.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            model.OpenOverlay(origin.Id, new SessionOverlay { Id = "hud", Hud = new HudSpec { Message = "m" } });
            model.FinishOverlayJob(job, 4, null);

            ControlResult result = (await new ControlDispatcher(model, new DashboardTests.NullHost()).HandleAsync(new ControlRequest { Cmd = "session.overlay.result", Target = origin.Id.Wire() })).Result!;
            Assert.Equal(4, result.ExitCode);
        }

        [Fact]
        public void ALocalSplitOnTheRowIgnoresTheOtherMachinesLayout()
        {
            model.CloseSplit(row.Id);
            model.SetSplit(row.Id, true, SplitAxis.Vertical);

            model.ApplyRemoteLayout(row.Id, Layout(Left));

            Assert.True(row.HasSplit);
            Assert.False(model.RestoreRemoteOrder(row.Id));
        }

        [Fact]
        public void AHeldResultKeepsTheSlotUntilThePresenterClosesIt()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, overlay.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            model.FinishOverlayJob(job, 0, null);

            Assert.True(model.OverlaySlotReserved(origin.Id, PaneRole.Overlay));
            ControlPresentationFrame closed = new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayClosed, Id = overlay.Id, Owner = job.Owner };
            Assert.True(model.OverlaySurfaceClosed(origin.Id, "a", closed));
            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.Overlay));
        }

        [Fact]
        public void ClosingAHeldResultHereAsksThePresenterToTakeItDown()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, overlay.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            model.FinishOverlayJob(job, 3, null);

            Assert.True(model.CancelOverlayJob(origin.Id, PaneRole.Overlay));
            Assert.True(model.Presentation(origin.Id, now, "a").Overlays!.Single().Dismissed);
            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.Overlay));
            Assert.Equal(3, origin.LastOverlay(PaneRole.Overlay)!.ExitCode);
        }

        [Fact]
        public void APresenterClosingAJobItNeverClaimedLeavesItToItsLaunchDeadline()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);

            model.OverlaySurfaceClosed(origin.Id, "a", new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayClosed, Id = overlay.Id, Owner = model.PresenterGeneration(origin.Id) });
            Assert.Null(origin.LastOverlay(PaneRole.Overlay)!.Failure);

            model.Presentation(origin.Id, now + TimeSpan.FromSeconds(31), "a");
            model.ExpireOverlayJobs(now + TimeSpan.FromSeconds(31));
            Assert.Equal("launch-failed", origin.LastOverlay(PaneRole.Overlay)!.Failure);
        }

        [Fact]
        public void AHandedOverlayIsResizedThereAndItsSlotFollowsASwap()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.HandOverlayOver(origin.Id, Program(), PaneRole.Overlay, pane => "follower", now);
            Assert.True(model.ResizeOverlayJob(origin.Id, 60));
            Assert.Equal(60, model.Presentation(origin.Id, now, "a").Overlays!.Single().SizePercent);

            model.SetSplit(origin.Id, true);
            model.HandOverlayOver(origin.Id, Program(), PaneRole.OverlayLeft, pane => "follower", now);
            model.SwapPanes(origin.Id);
            Assert.True(model.OverlaySlotReserved(origin.Id, PaneRole.OverlayRight));
            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.OverlayLeft));
        }

        [Fact]
        public void AHandedGuiQuestionWaitsInItsSessionsSlotNotTheWindows()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SelectSession(row.Id);
            AskRequest ask = new AskRequest { Id = "g", Title = "go?", Buttons = new[] { new ControlAskButton { Id = "yes", Label = "Yes" } }, Style = "gui", Align = "right", SessionId = origin.Id };

            Assert.True(model.CanHandAskOver(origin.Id, null, pane => "follower"));
            Assert.Null(model.OpenHandedAsk(ask));
            Assert.Null(model.GuiAsk);
            Assert.Equal("g", model.Presentation(origin.Id, now, "a").Ask!.Id);

            // Another question in this window's slot is not blocked by it.
            Assert.Null(model.OpenAsk(new AskRequest { Id = "local", Title = "x", Buttons = new[] { new ControlAskButton { Id = "ok", Label = "OK" } }, Style = "gui", Align = "right" }));
        }

        [Fact]
        public void AHandedGuiQuestionThatComesBackToItsSelectedSessionMovesToTheWindowsSlot()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SelectSession(origin.Id);
            AskRequest ask = new AskRequest { Id = "g", Title = "go?", Buttons = new[] { new ControlAskButton { Id = "yes", Label = "Yes" } }, Style = "gui", Align = "right", SessionId = origin.Id };
            model.OpenHandedAsk(ask);

            model.AnswerRemoteAsk(origin.Id, "a", new ControlPresentationFrame { Kind = ControlPresentationKinds.AskRejected, Id = "g", Owner = model.PresenterGeneration(origin.Id) });

            Assert.Same(ask, model.GuiAsk);
            Assert.Null(model.TerminalAskOf(origin.Id));
        }

        [Fact]
        public async Task ARemotePaneIsNotRestartedHere()
        {
            ControlResponse response = await new ControlDispatcher(model, new DashboardTests.NullHost()).HandleAsync(new ControlRequest { Cmd = "session.restart", Target = row.Id.Wire(), Args = new ControlArgs { Pane = "left" } });
            Assert.Equal("session.restart needs a local pane; this session runs on box", response.Error);
        }

        [Fact]
        public void AnUnownedStatusStaysWithTheRowAcrossASwapAndAPromotion()
        {
            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "blocked", Pane = "scratch" }, snapshot: false);
            PaneRole owner = row.StatusPane;
            model.ApplyRemoteLayout(row.Id, Layout(Right, Left));
            Assert.True(model.RestoreRemoteOrder(row.Id));
            Assert.Equal(owner, row.StatusPane);

            model.PromoteSplit(row.Id);
            Assert.Equal(AgentStatus.Blocked, row.Status);
        }

        [Fact]
        public void ASoftClosedRemoteRowIsHiddenAndAnUndoShowsItAgain()
        {
            List<bool> seen = new List<bool>();
            model.RemoteRowVisibility += (session, visible) => seen.Add(visible);

            model.SoftCloseSessions(new[] { row.Id });
            model.UndoClose();

            Assert.Equal(new[] { false, true }, seen);
        }

        [Fact]
        public void AHudOverAHiddenRemotePaneCoversTheSession()
        {
            model.FocusPane(row.Id, PaneRole.Main);
            model.SetSplit(row.Id, false);
            SessionOverlay? hud = model.ApplyRemoteHud(row.Id, new ControlPresentationHud { Id = "h", Message = "m", Pane = Right });
            Assert.Same(hud, row.Overlay);
            Assert.Equal("pane not shown here", model.OpenReplicaAsk(row.Id, new ControlPresentationAsk { Id = "q", Title = "t", Buttons = new List<ControlAskButton> { new ControlAskButton { Id = "y", Label = "Y" } }, Pane = Right }).Error);
        }

        [Fact]
        public void AMirroredContextThatBreaksTheRuleIsDropped()
        {
            model.ApplyRemoteContext(row.Id, "two\nlines");
            Assert.Null(row.EffectiveContext);
            model.ApplyRemoteContext(row.Id, new string('x', 300));
            Assert.Null(row.EffectiveContext);
        }

        [Fact]
        public void AHudWhoseTimeRanOutIsNotShown()
        {
            Assert.Null(model.ApplyRemoteHud(row.Id, new ControlPresentationHud { Id = "h", Message = "m", Remaining = 0 }));
            Assert.Empty(row.Overlays);
        }

        [Fact]
        public void TreeShowsHandedQuestionsAndReservedSlots()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.HandOverlayOver(origin.Id, Program(), PaneRole.Overlay, pane => "follower", now, follow: true);

            Assert.Equal(new int?[] { null }, model.RemoteOverlaysOf(origin.Id)!.Select(node => node.SizePercent));
            Assert.True(model.Presentation(origin.Id, now, "a").Overlays!.Single().Follow);
        }

        [Fact]
        public void ASelectedRemoteRowIsSavedAsTheLatestLocalSession()
        {
            model.SelectSession(origin.Id);
            model.SelectSession(row.Id);

            Assert.Equal(origin.Id, model.ToSnapshot().SelectedSessionId);
        }

        [Fact]
        public void ADuplicateOfARemoteRowIsALocalShell()
        {
            Session copy = model.DuplicateSession(row.Id, "pwsh");

            Assert.Equal("pwsh", copy.ProfileId);
            Assert.Null(copy.RemoteHost);
        }

        [Fact]
        public void AnOldKeymapThatUsesTheAttachChordKeepsIt()
        {
            KeymapResult mapped = Keymap.Parse("map ctrl+shift+r toggle_sidebar");
            Assert.Empty(mapped.Actions["attach_remote"]);
            Assert.Single(mapped.Actions["toggle_sidebar"]);

            KeymapResult plain = Keymap.Parse(string.Empty);
            Assert.Single(plain.Actions["attach_remote"]);
        }

        [Fact]
        public async Task APageOpensHereWhileThisMachineLeadsItsPanes()
        {
            model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a");
            ControlResponse response = await new ControlDispatcher(model, new DashboardTests.NullHost()).HandleAsync(new ControlRequest
            {
                Cmd = "session.overlay.open",
                Target = origin.Id.Wire(),
                Args = new ControlArgs { Url = "https://example.com" },
            });
            Assert.True(response.Ok);
            Assert.NotNull(origin.Overlay!.Page);
        }

        [Fact]
        public async Task AWebPageGoesToAPresenterThatShowsPages()
        {
            DashboardTests.NullHost host = new DashboardTests.NullHost { Lead = (sessionId, pane) => "follower" };
            ControlDispatcher dispatcher = new ControlDispatcher(model, host);
            Task<ControlResponse> Send(string cmd, ControlArgs args) => dispatcher.HandleAsync(new ControlRequest { Cmd = cmd, Target = origin.Id.Wire(), Args = args });
            model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a");

            Assert.Equal("a viewer presents this session: an html overlay would open where nobody sees it", (await Send("session.overlay.open", new ControlArgs { Url = "https://example.com" })).Error);
            model.SetStreamKinds("a", ControlPresentationKinds.SupportedKinds);
            Assert.Equal("a loopback url names this machine, so the machine presenting the session cannot load it", (await Send("session.overlay.open", new ControlArgs { Url = "http://localhost:5173" })).Error);

            Assert.Equal("page too large to show on another machine", (await Send("session.overlay.open", new ControlArgs { Url = "https://example.com/" + new string('a', PageServe.MaxFrameBytes) })).Error);
            Assert.Null(model.RemotePageAt(origin.Id, PaneRole.Overlay));
            string id = (await Send("session.overlay.open", new ControlArgs { Url = "https://example.com", Navigation = true })).Result!.PageId!;
            Assert.Null(origin.Overlay);
            Assert.Equal("overlay already open", (await Send("session.overlay.open", new ControlArgs { Command = "htop" })).Error);
            await Send("session.overlay.reload", new ControlArgs());
            Assert.Equal("show in Finder requires a file page", (await Send("session.overlay.navigate", new ControlArgs { To = "finder" })).Error);
            Assert.Equal("hidden", (await Send("session.overlay.hide", new ControlArgs())).Result!.Text);
            Assert.Equal("no overlay result: the slot holds an html page", (await Send("session.overlay.result", new ControlArgs())).Error);
            ControlPresentationPage handed = Assert.Single(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Pages!);
            Assert.Equal(("https://example.com", true, "reload-original"), (handed.Url, handed.Hidden, handed.Commands!.Single().Action));
            Assert.Equal("pending", PageOutcomes.Find(id)!.Value.Outcome);

            await Send("session.overlay.close", new ControlArgs());
            Assert.Equal("dismissed", PageOutcomes.Find(id)!.Value.Outcome);
            Assert.True(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Pages!.Single().Closed);

            string lost = (await Send("session.overlay.open", new ControlArgs { Url = "https://example.com" })).Result!.PageId!;
            model.ReleasePresenter(origin.Id, "a");
            Assert.Equal("dismissed", PageOutcomes.Find(lost)!.Value.Outcome);
            Assert.Null(model.RemotePageAt(origin.Id, PaneRole.Overlay));
        }

        [Fact]
        public async Task AFilePageIsServedToItsPresenterByTokenWhileItIsOpen()
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            string file = Path.Combine(folder, "page.html");
            File.WriteAllText(file, "<p>x</p>");
            try
            {
                DashboardTests.NullHost host = new DashboardTests.NullHost { Lead = (sessionId, pane) => "follower" };
                ControlDispatcher dispatcher = new ControlDispatcher(model, host);
                Task<ControlResponse> Send(string cmd, ControlArgs args) => dispatcher.HandleAsync(new ControlRequest { Cmd = cmd, Target = origin.Id.Wire(), Args = args });
                model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a");
                model.SetStreamKinds("a", ControlPresentationKinds.SupportedKinds);

                await Send("session.overlay.open", new ControlArgs { Html = file, Navigation = true });
                Assert.Null(origin.Overlay);
                ControlPresentationPage handed = Assert.Single(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Pages!);
                Assert.True(handed.IsFile);
                Assert.Equal(file, handed.Url);
                Assert.Equal("page is shown on another machine", (await Send("session.overlay.navigate", new ControlArgs { To = "browser" })).Error);
                ControlHtmlOverlayNode node = Assert.Single((await Send("tree", new ControlArgs())).Result!.Tree!.Workspaces.SelectMany(work => work.Sessions).Single(row => row.Id == origin.Id.Wire()).HtmlOverlays!);
                Assert.Equal((file, (string?)null), (node.File, node.Url));

                Assert.True(model.HasRemotePageToken(handed.Token));
                RemotePage? endedPage = null;
                model.RemotePageEnded += page => endedPage = page;
                Assert.Equal(file, model.ClaimRemotePage(handed.Token!, DateTimeOffset.UtcNow)!.Spec.Source);
                Assert.Null(model.ClaimRemotePage(handed.Token!, DateTimeOffset.UtcNow));
                Assert.Null(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Pages!.Single().Token);

                model.EndClaimedRemotePage(handed.Id);
                Assert.Equal(handed.Id, endedPage!.Id);
                Assert.Equal("dismissed", PageOutcomes.Find(handed.Id)!.Value.Outcome);
                Assert.True(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Pages!.Single().Closed);

                string unclaimed = (await Send("session.overlay.open", new ControlArgs { Html = file })).Result!.PageId!;
                model.ExpireOverlayJobs(DateTimeOffset.UtcNow + AppModel.JobLaunchWindow + TimeSpan.FromSeconds(1));
                Assert.Equal("dismissed", PageOutcomes.Find(unclaimed)!.Value.Outcome);
                Assert.True(model.Presentation(origin.Id, DateTimeOffset.UtcNow, "a").Pages!.Single(page => page.Id == unclaimed).Closed);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static AskRequest FormAsk(string? input = null, bool choices = false, bool multiple = false, string? other = null)
        {
            return new AskRequest
            {
                Id = "f",
                Title = "t",
                Buttons = new[] { new ControlAskButton { Id = "ok", Label = "OK" }, new ControlAskButton { Id = "no", Label = "No" } },
                CancelButton = "no",
                Style = "terminal",
                Align = "right",
                Input = input,
                Choices = choices ? new[] { new ControlAskChoice { Id = "a", Label = "A" }, new ControlAskChoice { Id = "b", Label = "B" } } : null,
                Multiple = multiple,
                Other = other,
            };
        }

        [Fact]
        public void AnAnswerTheFormCouldNotProduceIsRefused()
        {
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(input: "line"), "ok", null, null));
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(input: "line"), "ok", "x\ny", null));
            Assert.NotNull(AppModel.ValidatedAnswer(FormAsk(input: "multiline"), "ok", "x\ny", null));
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(choices: true), "ok", null, new[] { "a", "b" }));
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(choices: true, multiple: true), "ok", null, new[] { "b", "a" }));
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(choices: true), "ok", "other", new List<string>()));
            Assert.Equal("answered", AppModel.ValidatedAnswer(FormAsk(choices: true, other: "?"), "ok", "other", new List<string>())!.Result);
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(), "no", "text", null));
            Assert.Equal("escaped", AppModel.ValidatedAnswer(FormAsk(), null, null, null)!.Result);
            Assert.Null(AppModel.ValidatedAnswer(FormAsk(), "ok", "text", null));
        }

        [Fact]
        public void ClosingTheSessionOrItsSplitEndsTheOverlaysItHandedOut()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.SetSplit(origin.Id, true);
            SessionOverlay right = Program();
            model.HandOverlayOver(origin.Id, right, PaneRole.OverlayRight, pane => "follower", now);
            model.CloseSplit(origin.Id);
            Assert.Equal("canceled", origin.LastOverlay(PaneRole.OverlayRight)?.Failure ?? model.OverlayJobById(right.Id)!.Failure);

            SessionOverlay whole = Program();
            model.HandOverlayOver(origin.Id, whole, PaneRole.Overlay, pane => "follower", now);
            model.SoftCloseSessions(new[] { origin.Id });
            Assert.Equal("canceled", model.OverlayJobById(whole.Id)!.Failure);
            model.UndoClose();
            Assert.False(model.OverlaySlotReserved(origin.Id, PaneRole.Overlay));
        }

        [Fact]
        public void AResizeAfterThePresenterWentFails()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            OverlayJob job = model.OverlayJobOf(origin.Id, overlay.Id)!;
            model.ClaimOverlayJob(job, now);
            model.OverlayJobStarted(job, now);
            model.ReleasePresenter(origin.Id, "a");
            model.Presentation(origin.Id, now, "b");

            Assert.False(model.ResizeOverlayJob(origin.Id, 50));
        }

        [Fact]
        public void AJobNeverClaimedIsCanceledWhenItsPresenterGoes()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            SessionOverlay overlay = Program();
            model.HandOverlayOver(origin.Id, overlay, PaneRole.Overlay, pane => "follower", now);
            model.ReleasePresenter(origin.Id, "a");

            Assert.Equal("canceled", model.OverlayJobById(overlay.Id)!.Failure);
            Assert.False(model.ClaimOverlayJob(model.OverlayJobById(overlay.Id)!, now));
        }

        [Fact]
        public void AStatusFollowsItsPaneThroughASwapMadeHere()
        {
            model.SwapPanes(row.Id);
            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "blocked", Pane = Right }, snapshot: false);
            Assert.Equal(PaneRole.Main, row.StatusPane);

            model.SwapPanes(row.Id);
            model.ApplyRemoteStatus(row.Id, new ControlPresentationStatus { Status = "blocked", Pane = Right }, snapshot: false);
            Assert.Equal(PaneRole.Split, row.StatusPane);
        }

        [Fact]
        public void RemoteOpenedFollowsSessionCreatedDirectly()
        {
            Session added = model.AddRemoteSession(model.Workspaces[0].Id, "box", Guid.NewGuid(), "x", split: false);
            List<ControlEvent> events = model.Events.Read(model.Events.Run, 0, null, 1000).Batch.Items.Where(item => item.Session == added.Id.Wire()).ToList();

            Assert.Equal("session.created", events[0].Kind);
            Assert.Equal("remote.opened", events[1].Kind);
        }

        [Fact]
        public void ARemoteRowIsLeftOutOfTheSavedRecencyAndAClosedWorkspacesSelection()
        {
            model.SelectSession(origin.Id);
            model.SelectSession(row.Id);
            Assert.DoesNotContain(row.Id, model.ToSnapshot().SessionRecency);

            Workspace other = model.AddWorkspace("other");
            Session far = model.AddRemoteSession(other.Id, "box", Guid.NewGuid(), "far2", split: false);
            model.SelectSession(far.Id);
            model.SoftCloseWorkspace(other.Id);
            Assert.Null(model.RecentClosed.First().Workspace!.SelectedSessionId);
        }

        [Fact]
        public async Task OverlayCommandsSayWhereAHandedOverlayIsAndAHudLeavesNoResult()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            model.Presentation(origin.Id, now, "a");
            model.HandOverlayOver(origin.Id, Program(), PaneRole.Overlay, pane => "follower", now);
            ControlDispatcher dispatcher = new ControlDispatcher(model, new DashboardTests.NullHost());

            ControlResponse text = await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.overlay.text", Target = origin.Id.Wire() });
            Assert.Equal("overlay is shown on another machine", text.Error);
            ControlResponse open = await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.overlay.open", Target = origin.Id.Wire(), Args = new ControlArgs { Command = "x" } });
            Assert.Equal("overlay already open", open.Error);

            model.CancelOverlayJob(origin.Id, PaneRole.Overlay);
            model.OpenOverlay(origin.Id, new SessionOverlay { Id = "hud", Hud = new HudSpec { Message = "m" } });
            ControlResponse result = await dispatcher.HandleAsync(new ControlRequest { Cmd = "session.overlay.result", Target = origin.Id.Wire() });
            Assert.Equal("no overlay result: the slot holds a hud", result.Error);
        }

        [Fact]
        public async Task ARemoteRowIsNotPresentedOnward()
        {
            ControlResponse response = await new ControlDispatcher(model, new DashboardTests.NullHost()).HandleAsync(new ControlRequest { Cmd = "live.presentation", Target = row.Id.Wire() });

            Assert.False(response.Ok);
        }
    }
}
