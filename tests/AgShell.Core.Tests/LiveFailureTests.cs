namespace AgShell.Core.Tests
{
    public class LiveFailureTests
    {
        [Fact]
        public void FailureRoundTripsWithTheSameSessionPaneAndNeighbor()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-failed-live-" + Guid.NewGuid().ToString("N"));
            try
            {
                AppModel model = new AppModel();
                Workspace workspace = model.AddWorkspace("saved");
                Session failed = model.AddSession(workspace.Id, "cmd", @"C:\saved", name: "failed");
                Session neighbor = model.AddSession(workspace.Id, "pwsh", @"C:\neighbor", name: "neighbor");
                model.SetSplit(failed.Id, true);
                string main = failed.PaneIds[PaneRole.Main];
                string split = failed.PaneIds[PaneRole.Split];
                int saves = 0;
                model.LiveFailureChanged += () => saves++;

                model.SetLiveFailed(failed.Id, PaneRole.Main, true);
                Assert.Equal(1, saves);
                StateStore store = new StateStore(Path.Combine(directory, "state.json"));
                store.Save(model.ToSnapshot());
                AppModel restored = AppModel.FromSnapshot(Assert.IsType<StateSnapshot>(store.Load()), launchRestore: true);
                Session read = restored.FindSession(failed.Id)!;

                Assert.True(read.LiveFailed(PaneRole.Main));
                Assert.False(read.LiveFailed(PaneRole.Split));
                Assert.Equal((main, split, @"C:\saved", "cmd", "failed"),
                    (read.PaneIds[PaneRole.Main], read.PaneIds[PaneRole.Split], read.Cwd, read.ProfileId, read.CustomName));
                Assert.Equal(neighbor.PaneIds[PaneRole.Main], restored.FindSession(neighbor.Id)!.PaneIds[PaneRole.Main]);
                Assert.False(restored.FindSession(neighbor.Id)!.LiveFailed(PaneRole.Main));
                restored.SetLiveFailed(failed.Id, PaneRole.Main, false);
                Assert.Null(restored.ToSnapshot().Workspaces[0].Sessions[0].LiveFailedPanes);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Fact]
        public void FailureFollowsItsPaneThroughSwapPromotionAndClosingTheSplit()
        {
            AppModel model = new AppModel();
            Session session = model.AddSession(model.AddWorkspace().Id, "cmd", @"C:\saved");
            model.SetSplit(session.Id, true);
            string failed = session.PaneIds[PaneRole.Main];
            model.SetLiveFailed(session.Id, PaneRole.Main, true);

            model.SwapPanes(session.Id);
            Assert.False(session.LiveFailed(PaneRole.Main));
            Assert.True(session.LiveFailed(PaneRole.Split));
            Assert.Equal(failed, session.PaneIds[PaneRole.Split]);

            model.PromoteSplit(session.Id);
            Assert.True(session.LiveFailed(PaneRole.Main));
            Assert.Equal(failed, session.PaneIds[PaneRole.Main]);
            Assert.False(session.LiveFailed(PaneRole.Split));

            model.SetSplit(session.Id, true);
            model.SetLiveFailed(session.Id, PaneRole.Split, true);
            model.CloseSplit(session.Id);
            Assert.False(session.LiveFailed(PaneRole.Split));
            Assert.True(session.LiveFailed(PaneRole.Main));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SavingAFailureDuringUndoKeepsTheRowsAndTheFailureInTheCrashSnapshot(bool wholeWorkspace)
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("closing");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\saved");
            Session neighbor = model.AddSession(model.AddWorkspace("kept").Id, "pwsh", @"C:\neighbor");
            string pane = session.PaneIds[PaneRole.Main];
            PendingClose close = (wholeWorkspace ? model.SoftCloseWorkspace(workspace.Id) : model.SoftCloseSessions(new[] { session.Id }))!;
            StateSnapshot? saved = null;
            model.LiveFailureChanged += () => saved = model.LiveFailureSnapshot();

            model.SetLiveFailed(session.Id, PaneRole.Main, true);

            Assert.Null(model.FindSession(session.Id));
            AppModel afterCrash = AppModel.FromSnapshot(saved!);
            Assert.True(afterCrash.FindSession(session.Id)!.LiveFailed(PaneRole.Main));
            Assert.Equal(pane, afterCrash.FindSession(session.Id)!.PaneIds[PaneRole.Main]);
            Assert.NotNull(afterCrash.FindSession(neighbor.Id));
            Assert.DoesNotContain(model.ToSnapshot().Workspaces.SelectMany(item => item.Sessions), item => item.Id == session.Id);
            Assert.True(model.UndoClose(close.Id));
            Assert.True(model.FindSession(session.Id)!.LiveFailed(PaneRole.Main));
        }
    }
}
