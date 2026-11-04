using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class ClosingTests
    {
        private readonly AppModel model = new AppModel();
        private readonly Workspace work;
        private readonly Workspace home;
        private readonly Session a;
        private readonly Session b;
        private readonly Session c;

        public ClosingTests()
        {
            work = model.AddWorkspace("work");
            home = model.AddWorkspace("home");
            a = model.AddSession(work.Id, "cmd", "C:\\a", name: "a");
            b = model.AddSession(work.Id, "cmd", "C:\\b", name: "b");
            c = model.AddSession(home.Id, "cmd", "C:\\c", name: "c");
        }

        private string Tree()
        {
            return string.Join(" | ", model.Workspaces.Select(w => w.Name + ": " + string.Join(",", w.Sessions.Select(s => s.DisplayName))));
        }

        private int Closed()
        {
            return model.Events.Read(model.Events.Run, 0, new HashSet<string> { ControlEventKinds.SessionClosed }, 100).Batch.Items.Count;
        }

        [Fact]
        public void ARemoteRowIsNeitherSavedNorRemembered()
        {
            Session remote = model.AddRemoteSession(work.Id, "box", Guid.NewGuid(), "far", split: true);

            Assert.True(remote.HasSplit);
            Assert.Equal("box", remote.RemoteHost);
            Assert.DoesNotContain(model.ToSnapshot().Workspaces.SelectMany(w => w.Sessions), s => s.Id == remote.Id);

            PendingClose close = model.SoftCloseSessions(new[] { remote.Id })!;
            model.FinalizeClose(close.Id);
            Assert.Empty(model.RecentClosed);
        }

        [Fact]
        public void UndoPutsTheSameSessionsBackWhereTheyWere()
        {
            model.SelectSession(b.Id);
            PendingClose close = model.SoftCloseSessions(new[] { b.Id, a.Id })!;

            Assert.Equal("work:  | home: c", Tree());
            Assert.Equal(new[] { a.Id, b.Id }, model.PendingSessionIds);
            // As agterm's: the rows leave the tree now and are reported closed now; undo reports them created again.
            Assert.Equal(2, Closed());
            // As agterm's: they are in the recently closed list at once, and undo takes them out.
            Assert.Equal(2, model.RecentClosed.Count);

            Assert.True(model.UndoClose());
            Assert.Empty(model.RecentClosed);
            // Created once when made, and again by the undo.
            Assert.Equal(4, model.Events.Read(model.Events.Run, 0, new HashSet<string> { ControlEventKinds.SessionCreated }, 100).Batch.Items.Count(item => item.Session == a.Id.Wire() || item.Session == b.Id.Wire()));
            Assert.Equal("work: a,b | home: c", Tree());
            Assert.Same(a, model.FindSession(a.Id));
            // As agterm's: the session that was selected is selected again.
            Assert.Equal(b.Id, model.SelectedSessionId);
            Assert.Empty(model.PendingCloses);
            Assert.Empty(model.FinalizeClose(close.Id));
        }

        [Fact]
        public void ASoftCloseSavesNothingUntilItIsFinal()
        {
            int saves = 0;
            int cancels = 0;
            model.StructureChanged += () => saves++;
            model.PendingSaveCancelled += () => cancels++;

            PendingClose close = model.SoftCloseSessions(new[] { a.Id })!;
            Assert.Equal(0, saves);
            Assert.Equal(1, cancels);

            model.FinalizeClose(close.Id);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void FinalizingReportsTheCloseAndRemembersIt()
        {
            PendingClose close = model.SoftCloseSessions(new[] { a.Id })!;

            IReadOnlyList<Session> stopped = model.FinalizeClose(close.Id);

            Assert.Equal(new[] { a }, stopped);
            Assert.Equal(1, Closed());
            Assert.Equal("a", model.RecentClosed[0].Title);
            Assert.False(model.UndoClose());
        }

        [Fact]
        public void AClosedWorkspaceComesBackInPlaceWithItsSelection()
        {
            model.SelectSession(b.Id);
            model.SoftCloseWorkspace(work.Id);
            Assert.Equal("home: c", Tree());

            model.UndoClose();
            Assert.Equal("work: a,b | home: c", Tree());
            Assert.Equal(b.Id, model.SelectedSessionId);
        }

        [Fact]
        public void UndoRebuildsAWorkspaceThatWentAwayMeanwhile()
        {
            model.SoftCloseSessions(new[] { c.Id });
            model.RemoveWorkspace(home.Id);

            model.UndoClose();
            Assert.Equal("work: a,b | home: c", Tree());
            Assert.Equal(home.Id, model.Workspaces[1].Id);
        }

        [Fact]
        public void ReopenRecentRebuildsFromTheSnapshotAndDropsTheEntry()
        {
            model.SetSplit(a.Id, true, SplitAxis.Horizontal);
            model.RemoveSession(a.Id);
            model.RemoveWorkspace(home.Id);
            Assert.Equal(new[] { "home", "a" }, model.RecentClosed.Select(item => item.Title));

            Assert.True(model.ReopenRecent(model.RecentClosed[1].Id));
            Session reopened = model.FindSession(a.Id)!;
            Assert.NotSame(a, reopened);
            Assert.True(reopened.HasSplit);
            Assert.Equal(SplitAxis.Horizontal, reopened.SplitAxis);
            Assert.Equal("work: a,b", Tree());

            Assert.True(model.ReopenRecent());
            Assert.Equal("work: a,b | home: c", Tree());
            Assert.Empty(model.RecentClosed);
            Assert.False(model.ReopenRecent());
        }

        [Fact]
        public void TheLastWorkspaceCannotBeSoftClosed()
        {
            model.RemoveWorkspace(home.Id);
            Assert.Null(model.SoftCloseWorkspace(work.Id));
            Assert.Null(model.SoftCloseSessions(new[] { Guid.NewGuid() }));
        }

        [Fact]
        public void TheListKeepsTheNewestTwentyAndOneEntryPerThing()
        {
            for (int i = 0; i < 25; i++)
            {
                Session session = model.AddSession(work.Id, "cmd", null, name: "s" + i);
                model.RemoveSession(session.Id);
            }

            Assert.Equal(AppModel.RecentClosedLimit, model.RecentClosed.Count);
            Assert.Equal("s24", model.RecentClosed[0].Title);

            model.RemoveWorkspace(home.Id);
            model.ReopenRecent();
            model.RemoveWorkspace(home.Id);
            Assert.Single(model.RecentClosed, item => item.Workspace?.Snapshot.Id == home.Id);
        }

        [Fact]
        public void WindowsShareOneList()
        {
            RecentClosedList shared = new RecentClosedList();
            AppModel other = new AppModel();
            model.ShareRecent(shared);
            other.ShareRecent(shared);
            Workspace there = other.AddWorkspace("there");
            Session s = other.AddSession(there.Id, "cmd", null, name: "far");

            other.RemoveSession(s.Id);
            model.RemoveWorkspace(home.Id);

            Assert.Equal(new[] { "home", "far" }, model.RecentClosed.Select(item => item.Title));
            Assert.Equal((Guid?)there.Id, shared.Items[1].WorkspaceId);
            Assert.Equal(new[] { s.Id }, shared.Items[1].SessionIds);

            other.ReopenRecent(shared.Items[1].Id);
            Assert.NotNull(other.FindSession(s.Id));
            Assert.Single(model.RecentClosed);
            Assert.Same(shared.Items[0], shared.Resolve(null).Item);
            Assert.Equal("no such recent item: zz", shared.Resolve("zz").Error);
        }

        [Fact]
        public void TheRecentListSurvivesAReload()
        {
            string path = Path.Combine(Path.GetTempPath(), "agshell-recent-" + Guid.NewGuid() + ".json");
            try
            {
                model.RemoveWorkspace(home.Id);
                new RecentClosedStore(path).Save(model.RecentClosed);

                AppModel fresh = new AppModel();
                fresh.LoadRecent(new RecentClosedStore(path).Load());
                Assert.Equal("home", fresh.RecentClosed[0].Workspace!.Snapshot.Name);
                Assert.Equal(c.Id, fresh.RecentClosed[0].Workspace!.Snapshot.Sessions[0].Id);
                Assert.Empty(new RecentClosedStore(path + ".missing").Load());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TheFileIsVersionedAndAFailedWriteIsSwallowed()
        {
            string path = Path.Combine(Path.GetTempPath(), "agshell-recent-" + Guid.NewGuid() + ".json");
            try
            {
                model.RemoveWorkspace(home.Id);
                Assert.True(new RecentClosedStore(path).Save(model.RecentClosed));
                Assert.Contains("\"version\": 1", File.ReadAllText(path));

                File.WriteAllText(path, "{\"version\": 2, \"items\": []}");
                Assert.Empty(new RecentClosedStore(path).Load());
                File.WriteAllText(path, "[]");
                Assert.Empty(new RecentClosedStore(path).Load());

                Assert.False(new RecentClosedStore(Path.Combine(path, "recent-closed.json")).Save(model.RecentClosed));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AReopenedWorkspaceComesBackLast()
        {
            model.AddWorkspace("x");
            model.RemoveWorkspace(home.Id);

            Assert.True(model.ReopenRecent());
            Assert.Equal("work: a,b | x:  | home: c", Tree());
        }

        [Fact]
        public void SessionsAnotherWindowHoldsAreNotRebuilt()
        {
            model.RemoveSession(a.Id);
            RecentClosedItem session = model.RecentClosed[0];
            Assert.Equal(RecentClosedRestore.Failed, model.ReopenRecent(session, new HashSet<Guid> { a.Id }));
            Assert.Same(session, model.RecentClosed[0]);

            model.RemoveWorkspace(home.Id);
            RecentClosedItem workspace = model.RecentClosed[0];
            Assert.Equal(new RecentClosedRestore(true, true), model.ReopenRecent(workspace, new HashSet<Guid> { c.Id }));
            Assert.Equal("work: b | home: ", Tree());
            Assert.DoesNotContain(model.RecentClosed, item => item.Id == workspace.Id);
        }

        [Fact]
        public void AWorkspaceWhoseSessionsAreAllHereOnlySelectsOne()
        {
            RecentClosedItem item = new RecentClosedItem
            {
                Kind = "workspace",
                Title = "gone",
                Workspace = new RecentWorkspace { Snapshot = new WorkspaceSnapshot { Id = Guid.NewGuid(), Name = "gone", Sessions = model.ToSnapshot().Workspaces[1].Sessions } },
            };
            model.LoadRecent(new[] { item });

            Assert.True(model.ReopenRecent());
            Assert.Equal("work: a,b | home: c", Tree());
            Assert.Equal(c.Id, model.SelectedSessionId);
            Assert.Empty(model.RecentClosed);
        }

        [Fact]
        public void ReopeningAWorkspaceUndoesThePendingCloseOfItsSessions()
        {
            Session d = model.AddSession(home.Id, "cmd", "C:\\d", name: "d");
            RecentClosedItem item = new RecentClosedItem
            {
                Kind = "workspace",
                Title = "home",
                Workspace = new RecentWorkspace { Snapshot = model.ToSnapshot().Workspaces[1] },
            };
            model.SoftCloseSessions(new[] { d.Id });
            model.LoadRecent(new[] { item });

            Assert.True(model.ReopenRecent(item.Id));
            Assert.Equal("work: a,b | home: c,d", Tree());
            Assert.Same(d, model.FindSession(d.Id));
            Assert.Empty(model.PendingCloses);
        }

        [Fact]
        public void ReopeningAnOpenSessionOnlySelectsIt()
        {
            model.RemoveSession(a.Id);
            RecentClosedItem item = model.RecentClosed[0];
            model.ReopenRecent(item, new HashSet<Guid>());
            model.LoadRecent(new[] { item });
            int changes = 0;
            model.TreeChanged += () => changes++;

            Assert.True(model.ReopenRecent(item.Id));
            Assert.Equal(0, changes);
            Assert.Equal(a.Id, model.SelectedSessionId);
        }
    }
}
